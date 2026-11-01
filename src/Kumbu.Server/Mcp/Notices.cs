// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Auth;
using Kumbu.Server.Sharing;
using Kumbu.Server.Vaults;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Kumbu.Server.Mcp;

/// <summary>
/// One-time notices to a member's agents (collab design §8.4): what changed about a vault shared with her ("Your role in
/// /garten is now read") is appended to the next tool result of each of her keys and connections, once per credential
/// and event. MCP is stateless, so "once" is a persisted mark (<c>notice_marks</c>, kept 30 days; only events of the
/// last 30 days are told, so a mark outlives what it marks). The owner's agents get none: her vaults are no member's
/// view. The notice is appended before it is marked, so a call that fails on the way loses no notice; two calls at
/// the same time may both carry it.
/// </summary>
public sealed class Notices(IMongoDatabase db, Memberships memberships, TimeProvider time, ILogger<Notices> log)
{
    internal const string CollectionName = "notice_marks";

    /// <summary>How long a mark is kept, and how far back events are told.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private volatile bool _ready;

    private IMongoCollection<NoticeMarkDocument> Marks { get; } = db.GetCollection<NoticeMarkDocument>(CollectionName);

    /// <summary>For tests: runs once the tool answered and its notices are appended, before they are marked; an
    /// exception there is the answer lost on its way.</summary>
    internal Func<Task>? FaultBeforeMark { get; set; }

    /// <summary>The filter, beside <see cref="RequiredArguments.Filter"/>.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, ct) =>
        {
            var result = await next(request, ct);
            if (request.Services?.GetService<Notices>() is not { } notices || request.Services.GetService<RequestCaller>() is not { } caller) return result;
            return await notices.AppendAsync(caller, result, ct);
        };

    /// <summary>The caller's notices not yet given, appended to <paramref name="result"/>, then marked as given.</summary>
    internal async Task<CallToolResult> AppendAsync(RequestCaller caller, CallToolResult result, CancellationToken ct)
    {
        if (caller.Kind is not (CallerKind.ApiKey or CallerKind.Connection) || caller.Reference.Id is not { } credentialId) return result;
        var shared = (await caller.OpenVaultsAsync(ct)).Where(a => a.Membership is not null).ToList();
        var pending = shared.Count == 0 ? [] : await PendingAsync(credentialId, shared, ct);
        var (lost, ownerless) = await LostAsync(credentialId, ct);
        // Nobody to name: marked unshown, or each call would look them up again for the rest of the window.
        if (pending.Count == 0 && lost.Count == 0)
        {
            if (ownerless.Count > 0) await MarkAsync(credentialId, ownerless, ct);
            return result;
        }
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (e, access) in pending)
        {
            var shown = await OwnerShownAsync(access.Membership!.Owner);
            // Starts on its own line, as the version block does: some clients join the blocks without a separator.
            result.Content.Add(new TextContentBlock { Text = "\nNotice: " + VaultEvents.NoticeFor(e.Kind, shown, "/" + access.Name, e.Role, e.Until) });
        }

        foreach (var (e, owner) in lost)
        {
            result.Content.Add(new TextContentBlock { Text = "\nNotice: " + VaultEvents.NoticeFor(e.Kind, await OwnerShownAsync(owner), "", null, null) });
        }

        if (FaultBeforeMark is { } fault) await fault();
        await MarkAsync(credentialId, pending.Select(p => p.Event.Id).Concat(lost.Select(l => l.Event.Id)).Concat(ownerless), ct);
        return result;

        async Task<string> OwnerShownAsync(string owner)
        {
            if (!owners.TryGetValue(owner, out var shown)) owners[owner] = shown = Api.WriterLabels.Owner(await memberships.DisplayNameAsync(owner, ct));
            return shown;
        }
    }

    /// <summary>The finished re-keyings, within <see cref="Window"/>, of vaults whose start took the credential's slot
    /// (collab design §7.1): it no longer reaches the vault and is told so once, with the vault's owner (its
    /// membership's, which the event's vault names). With them the ids of those whose vault has no membership left to
    /// name its owner (every member deleted her account): never told, but marked like the rest.</summary>
    private async Task<(IReadOnlyList<(VaultEventDocument Event, string Owner)> Lost, IReadOnlyList<ObjectId> Ownerless)> LostAsync(string credentialId, CancellationToken ct)
    {
        var f = Builders<VaultEventDocument>.Filter;
        var since = time.GetUtcNow().UtcDateTime - Window;
        var mine = f.Eq(e => e.Kind, VaultEvents.RekeyFinished) & f.AnyEq(e => e.Credentials, credentialId) & f.Gt(e => e.At, since);
        if (!await VaultEvents.Docs(db).Find(mine).AnyAsync(ct)) return ([], []);
        await EnsureReadyAsync(ct);
        var m = Builders<NoticeMarkDocument>.Filter;
        var given = await Marks.Find(m.Eq(x => x.Credential, credentialId) & m.Gt(x => x.At, since)).Project(x => x.Event).ToListAsync(ct);
        var events = await VaultEvents.Docs(db).Find(mine & f.Nin(e => e.Id, given)).SortBy(e => e.At).Limit(20).ToListAsync(ct);
        var vaults = events.Select(e => e.Vault).Distinct().ToList();
        if (vaults.Count == 0) return ([], []);
        // Every membership of a vault names its one owner.
        var owners = (await db.GetCollection<MembershipDocument>(Memberships.CollectionName).Find(x => vaults.Contains(x.Vault)).Project(x => new { x.Vault, x.Owner }).ToListAsync(ct))
            .GroupBy(x => x.Vault).ToDictionary(g => g.Key, g => g.First().Owner);
        return ([.. events.Where(e => owners.ContainsKey(e.Vault)).Select(e => (e, owners[e.Vault]))], [.. events.Where(e => !owners.ContainsKey(e.Vault)).Select(e => e.Id)]);
    }

    /// <summary>The events of the shared vaults <paramref name="shared"/> the credential hasn't been told, oldest first:
    /// her own membership's role changes and the vaults' own events, since the credential's slot on the vault was made
    /// and within <see cref="Window"/>.</summary>
    private async Task<IReadOnlyList<(VaultEventDocument Event, VaultAccess Access)>> PendingAsync(string credentialId, IReadOnlyList<VaultAccess> shared, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var since = time.GetUtcNow().UtcDateTime - Window;
        // What the credential was told within the window, first: limiting before leaving those out would return the same
        // given events on every call once there are 20 of them, and nothing newer would ever be told. A mark is made
        // after its event, so the marks of every event in the window are in it too.
        var m = Builders<NoticeMarkDocument>.Filter;
        var given = await Marks.Find(m.Eq(x => x.Credential, credentialId) & m.Gt(x => x.At, since)).Project(x => x.Event).ToListAsync(ct);
        var f = Builders<VaultEventDocument>.Filter;
        var filters = shared.Select(a =>
            f.Eq(e => e.Vault, a.Id)
            & f.Gt(e => e.At, a.DerivedSlot?.CreatedAt is { } made && made > since ? made : since)
            & ((f.Eq(e => e.Membership, a.Membership!.Id) & f.In(e => e.Kind, OwnKinds)) | (f.Eq(e => e.Membership, null) & f.In(e => e.Kind, VaultKinds))));
        var events = await VaultEvents.Docs(db).Find(f.Or(filters) & f.Nin(e => e.Id, given)).SortBy(e => e.At).Limit(20).ToListAsync(ct);
        return [.. events.Select(e => (e, shared.First(a => a.Id == e.Vault)))];
    }

    /// <summary>What an agent is told: a change of its role, and the vault's own events. A removal it learns from the
    /// vault being gone.</summary>
    private static readonly string[] OwnKinds = [VaultEvents.RoleChanged];

    /// <summary>A finished re-keying isn't among them: no credential reaches the vault across one (<see cref="LostAsync"/>).</summary>
    private static readonly string[] VaultKinds = [VaultEvents.ProviderChanged, VaultEvents.ClosingStarted, VaultEvents.OwnerInactive];

    private async Task MarkAsync(string credentialId, IEnumerable<ObjectId> events, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            await Marks.InsertManyAsync(
                events.Select(e => new NoticeMarkDocument { Credential = credentialId, Event = e, At = now }), new InsertManyOptions { IsOrdered = false }, ct);
        }
        catch (MongoBulkWriteException e) when (e.WriteErrors.All(w => w.Category == ServerErrorCategory.DuplicateKey))
        {
            // A call at the same time marked it first.
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The notice comes once more with the next call; that is all a lost mark costs.
            log.LogWarning(e, "Marking the notices of credential {CredentialId} as given failed", credentialId);
        }
    }

    internal async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        var keys = Builders<NoticeMarkDocument>.IndexKeys;
        await Marks.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<NoticeMarkDocument>(keys.Ascending(x => x.Credential).Ascending(x => x.Event), new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<NoticeMarkDocument>(keys.Ascending(x => x.At), new CreateIndexOptions { ExpireAfter = Window }),
            ],
            ct);
        _ready = true;
    }
}

/// <summary>That a credential was told of an event (collection <c>notice_marks</c>): ids only, gone after 30 days.</summary>
[BsonIgnoreExtraElements]
public sealed class NoticeMarkDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    /// <summary>The API key's or connection's id.</summary>
    [BsonElement("credential")]
    public string Credential { get; set; } = "";

    [BsonElement("event")]
    public ObjectId Event { get; set; }

    [BsonElement("at")]
    public DateTime At { get; set; }
}
