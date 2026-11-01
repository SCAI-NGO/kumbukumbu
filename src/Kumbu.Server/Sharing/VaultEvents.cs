// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kumbu.Server.Api;
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Kumbu.Server.Sharing;

/// <summary>
/// What happened to a shared vault and its memberships, for the owner's and the members' activity (collab design §8.3):
/// plain event documents (collection <c>vault_events</c>) holding ids, a kind, a time and at most a role or a date,
/// never a name or a text. The text is built when it is read, names resolved per viewer (<see cref="TextFor"/>).
/// The events of a membership are its owner's and its member's; those without one (<see cref="IsVaultWide"/>) are the
/// vault's, for every member. Deleted with the vault (<see cref="VaultCatalog.DeleteVaultsAsync"/>) and with a
/// member's account, as her memberships are.
/// </summary>
public static class VaultEvents
{
    internal const string CollectionName = "vault_events";

    // The owner's: what her invitations did.
    public const string InvitationCreated = "invitation-created";
    public const string InvitationWithdrawn = "invitation-withdrawn";
    public const string InvitationDeclined = "invitation-declined";
    public const string InvitationExpired = "invitation-expired";
    public const string InvitationAccepted = "invitation-accepted";
    public const string InvitationConfirmed = "invitation-confirmed";

    /// <summary>A member's export of the vault (owner's); recorded by the export routes (task 3.14).</summary>
    public const string MemberExport = "member-export";

    // The owner's and the member's.
    public const string MembershipRevoked = "membership-revoked";
    public const string MembershipLeft = "membership-left";

    /// <summary>The owner changed a member's role; <see cref="VaultEventDocument.Role"/> is the new one.</summary>
    public const string RoleChanged = "role-changed";

    // The vault's, for every member: no membership.
    public const string ProviderChanged = "provider-changed";

    /// <summary>A re-keying of the shared vault finished; members' credentials on it were revoked (phase 4).</summary>
    public const string RekeyFinished = "rekey-finished";

    /// <summary>The owner deleted her account and the vault is closing; <see cref="VaultEventDocument.Until"/> is when it
    /// goes (task 3.17).</summary>
    public const string ClosingStarted = "closing-started";

    /// <summary>The owner's account was deactivated: read-only for members (task 3.15).</summary>
    public const string OwnerInactive = "owner-inactive";

    /// <summary>The kinds a member sees of her own membership; the owner sees every kind.</summary>
    private static readonly HashSet<string> MemberKinds = [MembershipRevoked, MembershipLeft, RoleChanged];

    private static readonly HashSet<string> VaultWide = [ProviderChanged, RekeyFinished, ClosingStarted, OwnerInactive];

    /// <summary>Whether events of <paramref name="kind"/> belong to the vault rather than to one membership.</summary>
    public static bool IsVaultWide(string kind) => VaultWide.Contains(kind);

    internal static IMongoCollection<VaultEventDocument> Docs(IMongoDatabase db) => db.GetCollection<VaultEventDocument>(CollectionName);

    /// <summary>One event, inside the caller's transaction, so it commits with the change it records or not at all.
    /// <paramref name="membershipId"/> is null for a vault-wide one.</summary>
    internal static Task RecordAsync(
        IMongoDatabase db, IClientSessionHandle session, string vaultId, string kind, string? membershipId, DateTime at, string? role, CancellationToken ct,
        DateTime? until = null) =>
        Docs(db).InsertOneAsync(session, New(vaultId, kind, membershipId, at, role, until), cancellationToken: ct);

    /// <summary>One event on its own, for what isn't a transaction of the sharing's: an export, a provider change.</summary>
    internal static Task RecordAsync(IMongoDatabase db, string vaultId, string kind, string? membershipId, DateTime at, CancellationToken ct) =>
        Docs(db).InsertOneAsync(New(vaultId, kind, membershipId, at, null, null), cancellationToken: ct);

    private static VaultEventDocument New(string vaultId, string kind, string? membershipId, DateTime at, string? role, DateTime? until) =>
        new() { Vault = vaultId, Kind = kind, Membership = membershipId, At = at, Role = role, Until = until };

    /// <summary>Inside a vault deletion or a member's account deletion: the events of these vaults, or of these memberships.</summary>
    internal static Task DeleteAsync(IMongoDatabase db, IClientSessionHandle session, IReadOnlyCollection<string>? vaultIds, IReadOnlyCollection<string>? membershipIds, CancellationToken ct)
    {
        var f = Builders<VaultEventDocument>.Filter;
        var which = vaultIds is not null ? f.In(e => e.Vault, vaultIds) : f.In(e => e.Membership, membershipIds ?? []);
        return Docs(db).DeleteManyAsync(session, which, cancellationToken: ct);
    }

    internal static Task EnsureIndexAsync(IMongoDatabase db, CancellationToken ct) =>
        Docs(db).Indexes.CreateOneAsync(
            new CreateIndexModel<VaultEventDocument>(Builders<VaultEventDocument>.IndexKeys.Ascending(e => e.Vault).Ascending(e => e.Membership).Descending(e => e.At)),
            cancellationToken: ct);

    /// <summary>
    /// The events <paramref name="memberId"/> sees of <paramref name="vaultId"/>: only those of her own memberships of
    /// it that are a member's to see (never another member's), and the vault's own since she joined, newest first.
    /// </summary>
    internal static async Task<IReadOnlyList<VaultEventDocument>> OfMemberAsync(IMongoDatabase db, string vaultId, string memberId, CancellationToken ct)
    {
        var mine = await db.GetCollection<MembershipDocument>(Memberships.CollectionName)
            .Find(m => m.Vault == vaultId && m.Member == memberId).Project(m => new { m.Id, m.Status, m.AcceptedAt, m.CreatedAt }).ToListAsync(ct);
        var f = Builders<VaultEventDocument>.Filter;
        var own = f.In(e => e.Membership, mine.Select(m => m.Id)) & f.In(e => e.Kind, MemberKinds);
        // The vault's own only since she joined: one from before would tell her it was shared before her.
        var joined = mine.Where(m => m.Status == MembershipStatus.Active).Select(m => m.AcceptedAt ?? m.CreatedAt).DefaultIfEmpty(DateTime.MaxValue).Min();
        var vaultWide = f.Eq(e => e.Membership, null) & f.In(e => e.Kind, VaultWide) & f.Gte(e => e.At, joined);
        return await Docs(db).Find(f.Eq(e => e.Vault, vaultId) & (own | vaultWide)).SortByDescending(e => e.At).ToListAsync(ct);
    }

    /// <summary>Every event of the vault, for its owner, newest first.</summary>
    internal static async Task<IReadOnlyList<VaultEventDocument>> OfOwnerAsync(IMongoDatabase db, string vaultId, CancellationToken ct) =>
        await Docs(db).Find(e => e.Vault == vaultId).SortByDescending(e => e.At).ToListAsync(ct);

    /// <summary>
    /// The events of <paramref name="access"/>'s vault its caller sees, with their texts, newest first: the owner all of
    /// them, a member hers and the vault's (<see cref="OfMemberAsync"/>).
    /// </summary>
    internal static async Task<IReadOnlyList<(VaultEventDocument Event, string Text)>> ForAsync(
        IMongoDatabase db, VaultAccess access, string? viewerId, Func<string, CancellationToken, Task<string?>> displayName, CancellationToken ct)
    {
        IReadOnlyList<VaultEventDocument> events;
        if (access.Membership is { } membership)
        {
            if (viewerId is null) return [];
            events = await OfMemberAsync(db, access.Id, viewerId, ct);
        }
        else if (access.IsOwner) events = await OfOwnerAsync(db, access.Id, ct);
        else return [];

        var texts = new EventTexts(db, access, displayName);
        var result = new List<(VaultEventDocument, string)>();
        foreach (var e in events) result.Add((e, await texts.TextAsync(e, ct)));
        return result;
    }

    /// <summary>
    /// A member's own events across every vault shared with her, now or before, newest first: also those of a
    /// membership she no longer has (removed, left), which no vault of hers shows any more. A vault she still reaches is
    /// named by her alias (<paramref name="aliases"/>, by vault id), any other as "a vault shared with you".
    /// </summary>
    internal static async Task<IReadOnlyList<(VaultEventDocument Event, string? Vault, string Text)>> OfMemberEverywhereAsync(
        IMongoDatabase db, string memberId, IReadOnlyDictionary<string, string> aliases, Func<string, CancellationToken, Task<string?>> displayName, CancellationToken ct)
    {
        var mine = await db.GetCollection<MembershipDocument>(Memberships.CollectionName)
            .Find(m => m.Member == memberId).Project(m => new { m.Id, m.Vault, m.Owner, m.Status, m.AcceptedAt, m.CreatedAt }).ToListAsync(ct);
        if (mine.Count == 0) return [];
        var f = Builders<VaultEventDocument>.Filter;
        // A vault's own events only while she is a member, and only since she joined it (as OfMemberAsync).
        var vaultWide = mine.Where(m => m.Status == MembershipStatus.Active)
            .Select(m => f.Eq(e => e.Vault, m.Vault) & f.Eq(e => e.Membership, null) & f.In(e => e.Kind, VaultWide) & f.Gte(e => e.At, m.AcceptedAt ?? m.CreatedAt));
        var events = await Docs(db).Find(f.Or([f.In(e => e.Membership, mine.Select(m => m.Id)) & f.In(e => e.Kind, MemberKinds), .. vaultWide]))
            .SortByDescending(e => e.At).Limit(100).ToListAsync(ct);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<(VaultEventDocument, string?, string)>();
        foreach (var e in events)
        {
            var owner = mine.First(m => m.Vault == e.Vault).Owner;
            if (!owners.TryGetValue(owner, out var shown)) owners[owner] = shown = WriterLabels.Quoted(await displayName(owner, ct) ?? "");
            var alias = aliases.GetValueOrDefault(e.Vault);
            result.Add((e, alias, TextFor(e.Kind, toOwner: false, shown, alias ?? "a vault shared with you", e.Role, e.Until)));
        }

        return result;
    }

    /// <summary>
    /// An event's sentence for its viewer (collab design §8.2, §8.3): to the owner, the member by her label for him
    /// ("Bob", or "a former member" once the membership is gone); to a member, the owner by her display name, sanitised
    /// and quoted, and himself as "you". Never anything of another member.
    /// </summary>
    internal static string TextFor(string kind, bool toOwner, string person, string vault, string? role, DateTime? until) => (toOwner, kind) switch
    {
        (true, InvitationCreated) => $"You invited {person} to {role ?? "read"}.",
        (true, InvitationWithdrawn) => $"You withdrew the invitation for {person}.",
        (true, InvitationDeclined) => $"You declined {person}.",
        (true, InvitationExpired) => $"The invitation for {person} expired.",
        (true, InvitationAccepted) => $"{person} accepted the invitation.",
        (true, InvitationConfirmed) => $"You confirmed {person}.",
        (true, MemberExport) => $"{person} exported {vault}.",
        (true, MembershipRevoked) => $"You removed {person}.",
        (true, MembershipLeft) => $"{person} left {vault}.",
        (true, RoleChanged) => $"You changed {person}'s role to {role}.",
        (true, ProviderChanged) => "You changed the embedding provider.",
        (true, RekeyFinished) => $"{vault} was re-encrypted; members' keys and apps for it were revoked.",
        (true, ClosingStarted) => $"{vault} is closing{Until(until)}.",
        (true, OwnerInactive) => "Your account was deactivated; members can only read.",
        (false, MembershipRevoked) => $"{person} removed you from {vault}.",
        (false, MembershipLeft) => $"You left {vault}.",
        (false, RoleChanged) => $"{person} changed your role to {role}.",
        (false, ProviderChanged) => $"{person} changed the embedding provider of {vault}.",
        (false, RekeyFinished) => $"{person} re-encrypted {vault}; your keys and apps for it were revoked.",
        // The owner is gone: she isn't named.
        (false, ClosingStarted) => $"The owner deleted their account; {vault} will be deleted{Until(until)}. Export a copy to keep its notes.",
        (false, OwnerInactive) => $"{person}'s account is deactivated; {vault} is read-only until it is active again.",
        _ => "Something changed in the sharing of this vault.",
    };

    /// <summary>What a member's agent is told of an event, once (collab design §8.4): <paramref name="vault"/> is the
    /// path of the vault as the agent sees it, <paramref name="owner"/> the owner's display name as shown to members.</summary>
    internal static string NoticeFor(string kind, string owner, string vault, string? role, DateTime? until) => kind switch
    {
        RoleChanged => $"Your role in {vault} is now {role}.",
        ProviderChanged => $"The owner ({owner}) changed the embedding provider of {vault}.",
        RekeyFinished => $"The owner ({owner}) re-encrypted {vault}.",
        ClosingStarted => $"{vault} will be deleted{Until(until)}; export a copy to keep its notes.",
        OwnerInactive => $"The owner of {vault} ({owner}) is deactivated; {vault} is read-only until the account is active again.",
        _ => $"The sharing of {vault} changed.",
    };

    private static string Until(DateTime? until) => until is { } at ? " on " + at.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) : "";

    /// <summary>The names an event's text takes, read once per request.</summary>
    private sealed class EventTexts(IMongoDatabase db, VaultAccess access, Func<string, CancellationToken, Task<string?>> displayName)
    {
        private Dictionary<string, string>? _labels;
        private string? _owner;

        public async Task<string> TextAsync(VaultEventDocument e, CancellationToken ct)
        {
            if (access.IsOwner)
            {
                var person = e.Membership is { } id ? (await LabelsAsync(ct)).GetValueOrDefault(id) ?? WriterLabels.FormerMember : "";
                return TextFor(e.Kind, toOwner: true, person, access.Name, e.Role, e.Until);
            }

            _owner ??= WriterLabels.Quoted(await displayName(access.Membership!.Owner, ct) ?? "");
            return TextFor(e.Kind, toOwner: false, _owner, access.Name, e.Role, e.Until);
        }

        /// <summary>The owner's labels for every membership of the vault, opened with the vault, sanitised.</summary>
        private async Task<Dictionary<string, string>> LabelsAsync(CancellationToken ct)
        {
            if (_labels is not null) return _labels;
            _labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var doc in await db.GetCollection<MembershipDocument>(Memberships.CollectionName).Find(m => m.Vault == access.Id).ToListAsync(ct))
            {
                try
                {
                    _labels[doc.Id] = WriterLabels.Sanitise(Encoding.UTF8.GetString(
                        access.Vault.OpenForMembership(Memberships.LabelPurpose, doc.Label, $"membership-label:{doc.Vault}:{doc.Id}")));
                }
                catch (CryptographicException)
                {
                    // Shown as a former member's.
                }
            }

            return _labels;
        }
    }
}

/// <summary>One event of a shared vault (collection <c>vault_events</c>).</summary>
[BsonIgnoreExtraElements]
public sealed class VaultEventDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    [BsonElement("kind")]
    public string Kind { get; set; } = "";

    /// <summary>The membership the event is of; null for the vault's own (<see cref="VaultEvents.IsVaultWide"/>).</summary>
    [BsonElement("membership")]
    public string? Membership { get; set; }

    [BsonElement("at")]
    public DateTime At { get; set; }

    /// <summary>A role change's new role, or an invitation's role, as <see cref="MemberRoles.ToText"/> gives it.</summary>
    [BsonElement("role"), BsonIgnoreIfNull]
    public string? Role { get; set; }

    /// <summary>A closing period's end.</summary>
    [BsonElement("until"), BsonIgnoreIfNull]
    public DateTime? Until { get; set; }
}
