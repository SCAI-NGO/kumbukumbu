// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Sharing;

/// <summary>
/// The memberships of shared vaults (collab design §2): who may reach which vault of whom, with which role. Plain
/// fields, read on every request that touches a shared vault (<see cref="VaultCatalog.OpenForUserAsync"/>).
/// </summary>
public sealed class Memberships(IMongoDatabase db, VaultCatalog catalog, IOptions<KumbuOptions> options, TimeProvider time, ILogger<Memberships> log)
{
    internal const string CollectionName = "memberships";

    private volatile bool _ready;

    internal IMongoCollection<MembershipDocument> Docs { get; } = db.GetCollection<MembershipDocument>(CollectionName);

    internal const string LabelPurpose = "kumbu/v1/membership-label";
    internal const string WritersPurpose = "kumbu/v1/membership-writers";
    internal const string WriterIdsPurpose = "kumbu/v1/membership-writer-ids";

    /// <summary>What the member's sessions are in <c>writerIds</c>, in place of a credential id: all her sessions on the
    /// vault write under one writer id of the membership (collab design §8.1).</summary>
    internal const string SessionWriter = "session";

    /// <summary>The statuses that count towards the member limit: tombstones don't.</summary>
    private static readonly string[] Live = [MembershipStatus.Pending, MembershipStatus.Active];

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private int MemberLimit => options.Value.Vaults.MemberLimit;

    /// <summary>
    /// Makes <paramref name="memberId"/> an active member of <paramref name="vaultId"/>, which <paramref name="ownerId"/>
    /// owns, under <paramref name="alias"/> (the member's name for it) and <paramref name="label"/> (the owner's for
    /// the member). One transaction: the membership, with its label and its empty writers sealed under the vault key,
    /// the member slot (<see cref="VaultCatalog.MemberSlot"/>) and a write to the vault document, which makes two
    /// grants at once conflict, so the member limit, counted inside, holds. The core of an acceptance; returns the
    /// membership's id. <see cref="NotPermittedException"/> for the owner as member, <see cref="MemberLimitException"/>
    /// at the limit, <see cref="AlreadyMemberException"/>, <see cref="VaultNameTakenException"/> if the member has a
    /// vault or an alias of that name; nothing is written then.
    /// </summary>
    public async Task<string> GrantAsync(
        string ownerId, byte[] ownerKey, string vaultId, string memberId, byte[] memberKey, string alias, string label, MemberRole role, CancellationToken ct)
    {
        if (string.Equals(ownerId, memberId, StringComparison.Ordinal)) throw new NotPermittedException("You can't be a member of your own vault.");
        await EnsureReadyAsync(ct);
        var vaultKey = await catalog.OwnerVaultKeyAsync(ownerId, ownerKey, vaultId, ct);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var now = Now;
        var doc = new MembershipDocument
        {
            Id = id,
            Vault = vaultId,
            Owner = ownerId,
            Member = memberId,
            Role = MemberRoles.ToText(role),
            Status = MembershipStatus.Active,
            Label = VaultCatalog.SealForVault(vaultKey, vaultId, LabelPurpose, Encoding.UTF8.GetBytes(label), $"membership-label:{vaultId}:{id}"),
            Writers = VaultCatalog.SealForVault(vaultKey, vaultId, WritersPurpose, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>()), $"membership-writers:{vaultId}:{id}"),
            WriterIds = VaultCatalog.SealForVault(
                vaultKey, vaultId, WriterIdsPurpose, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { [NewWriterId()] = SessionWriter }), $"membership-writer-ids:{vaultId}:{id}"),
            CreatedAt = now,
            AcceptedAt = now,
            Revision = 0,
        };
        var slot = catalog.MemberSlot(vaultId, id, memberId, memberKey, vaultKey, alias);

        using var session = await catalog.StartSessionAsync(ct);
        // Retried on a write conflict: another grant on the vault, or a re-keying's start or finish.
        await session.WithTransactionAsync(
            async (s, token) =>
            {
                await GrantInAsync(s, ownerId, ownerKey, vaultKey, doc, slot, alias, token);
                return true;
            },
            cancellationToken: ct);
        log.LogInformation("Vault {VaultId} is shared with user {UserId} as membership {MembershipId}", vaultId, memberId, id);
        return id;
    }

    /// <summary>The grant's writes, inside the caller's transaction.</summary>
    private async Task GrantInAsync(
        IClientSessionHandle s, string ownerId, byte[] ownerKey, byte[] vaultKey, MembershipDocument doc, SlotDocument slot, string alias, CancellationToken ct)
    {
        var vault = await catalog.Vaults.UpdateOneAsync(
            s, v => v.Id == doc.Vault, Builders<VaultDocument>.Update.Inc(v => v.MembershipsChanged, 1), cancellationToken: ct);
        if (vault.MatchedCount == 0) throw new VaultDeletedException();
        // The key the slot wraps is still the vault's: no re-keying started or finished since it was opened.
        await catalog.RequireSameVaultKeysAsync(s, ownerId, ownerKey, new Dictionary<string, byte[]> { [doc.Vault] = vaultKey }, ct);

        var f = Builders<MembershipDocument>.Filter;
        var live = await Docs.CountDocumentsAsync(s, f.Eq(m => m.Vault, doc.Vault) & f.In(m => m.Status, Live), cancellationToken: ct);
        if (live >= MemberLimit) throw new MemberLimitException(MemberLimit);

        try
        {
            await Docs.InsertOneAsync(s, doc, cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new AlreadyMemberException();
        }

        try
        {
            await catalog.Slots.InsertOneAsync(s, slot, cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new VaultNameTakenException(alias);
        }
    }

    /// <summary>
    /// Inside the transaction that stores a member's new credential slot derived through <paramref name="membershipId"/>
    /// (collab design §3.4): the membership, if it is still active and on <paramref name="vaultId"/>, gets the
    /// credential's display name in its sealed <c>writers</c> and a fresh writer id for it in <c>writerIds</c>, with
    /// <c>revision</c> raised; the update's filter on <c>status == active</c> is the check, so a revocation that
    /// committed first refuses the credential (<see cref="ArgumentException"/>, starting with <paramref name="noun"/>),
    /// and one that commits later conflicts with this write. The vault document is written too, so a deletion of the
    /// vault under way conflicts as well. <paramref name="displayName"/> is only the agent's or device's part of the
    /// name ("Claude", "Laptop"), never a device key's full name, which holds the member's alias.
    /// </summary>
    internal static async Task RecordCredentialAsync(
        IMongoDatabase db, IClientSessionHandle s, string membershipId, string vaultId, string credentialId, string displayName, byte[] vaultKey, string noun,
        CancellationToken ct)
    {
        var docs = db.GetCollection<MembershipDocument>(CollectionName);
        var f = Builders<MembershipDocument>.Filter;
        var refused = new ArgumentException($"{noun} can only be given vaults of your own or shared with you.", "vaults");
        // Read whatever its status: the update below decides.
        var doc = await docs.Find(s, f.Eq(m => m.Id, membershipId) & f.Eq(m => m.Vault, vaultId)).FirstOrDefaultAsync(ct) ?? throw refused;
        var writers = JsonSerializer.Deserialize<Dictionary<string, string>>(
            VaultCatalog.OpenForVault(vaultKey, vaultId, WritersPurpose, doc.Writers, $"membership-writers:{vaultId}:{membershipId}"))!;
        var writerIds = JsonSerializer.Deserialize<Dictionary<string, string>>(
            VaultCatalog.OpenForVault(vaultKey, vaultId, WriterIdsPurpose, doc.WriterIds, $"membership-writer-ids:{vaultId}:{membershipId}"))!;
        writers[credentialId] = displayName;
        // One writer id per credential: a connection consented to again keeps its own.
        if (!writerIds.ContainsValue(credentialId)) writerIds[NewWriterId()] = credentialId;

        var updated = await docs.UpdateOneAsync(
            s,
            f.Eq(m => m.Id, membershipId) & f.Eq(m => m.Status, MembershipStatus.Active) & f.Eq(m => m.Revision, doc.Revision),
            Builders<MembershipDocument>.Update
                .Set(m => m.Writers, VaultCatalog.SealForVault(vaultKey, vaultId, WritersPurpose, JsonSerializer.SerializeToUtf8Bytes(writers), $"membership-writers:{vaultId}:{membershipId}"))
                .Set(m => m.WriterIds, VaultCatalog.SealForVault(vaultKey, vaultId, WriterIdsPurpose, JsonSerializer.SerializeToUtf8Bytes(writerIds), $"membership-writer-ids:{vaultId}:{membershipId}"))
                .Inc(m => m.Revision, 1),
            cancellationToken: ct);
        if (updated.MatchedCount == 0) throw refused;

        var vault = await db.GetCollection<VaultDocument>("vaults").UpdateOneAsync(
            s, v => v.Id == vaultId, Builders<VaultDocument>.Update.Inc(v => v.MembershipsChanged, 1), cancellationToken: ct);
        if (vault.MatchedCount == 0) throw refused;
    }

    /// <summary>The writers a membership's sealed <c>writers</c> holds (credential id → display name), opened with the vault key.</summary>
    internal static IReadOnlyDictionary<string, string> OpenWriters(MembershipDocument doc, byte[] vaultKey) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            VaultCatalog.OpenForVault(vaultKey, doc.Vault, WritersPurpose, doc.Writers, $"membership-writers:{doc.Vault}:{doc.Id}"))!;

    /// <summary>A writer id: random, of the membership alone, so it says nothing about the person or the credential.</summary>
    private static string NewWriterId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>The writer id of <paramref name="credentialId"/> (or <see cref="SessionWriter"/>) in the membership's
    /// sealed <c>writerIds</c>; null if it has none, or they don't open.</summary>
    internal static string? WriterIdOf(MembershipDocument doc, byte[] vaultKey, string credentialId)
    {
        try
        {
            return OpenWriterIds(doc, vaultKey).FirstOrDefault(w => w.Value == credentialId).Key;
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The writer ids a membership's sealed <c>writerIds</c> holds (writer id → credential id).</summary>
    internal static IReadOnlyDictionary<string, string> OpenWriterIds(MembershipDocument doc, byte[] vaultKey) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            VaultCatalog.OpenForVault(vaultKey, doc.Vault, WriterIdsPurpose, doc.WriterIds, $"membership-writer-ids:{doc.Vault}:{doc.Id}"))!;

    /// <summary>
    /// What names the members of <paramref name="vault"/> (collab design §8.2), opened with the vault: every membership
    /// with a member, tombstones included, or only <paramref name="membershipId"/>. Read afresh on every call; one that
    /// doesn't open is left out (its writers show as a former member's).
    /// </summary>
    internal async Task<IReadOnlyList<MembershipNames>> NamesAsync(UnlockedVault vault, string? membershipId, CancellationToken ct)
    {
        var f = Builders<MembershipDocument>.Filter;
        var filter = f.Eq(m => m.Vault, vault.Id) & f.Ne(m => m.Member, null);
        if (membershipId is not null) filter &= f.Eq(m => m.Id, membershipId);
        var result = new List<MembershipNames>();
        foreach (var doc in await Docs.Find(filter).ToListAsync(ct))
        {
            try
            {
                result.Add(new MembershipNames(
                    doc.Id, doc.Owner, doc.Status,
                    Encoding.UTF8.GetString(vault.OpenForMembership(LabelPurpose, doc.Label, $"membership-label:{doc.Vault}:{doc.Id}")),
                    JsonSerializer.Deserialize<Dictionary<string, string>>(vault.OpenForMembership(WritersPurpose, doc.Writers, $"membership-writers:{doc.Vault}:{doc.Id}"))!,
                    JsonSerializer.Deserialize<Dictionary<string, string>>(vault.OpenForMembership(WriterIdsPurpose, doc.WriterIds, $"membership-writer-ids:{doc.Vault}:{doc.Id}"))!));
            }
            catch (Exception e) when (e is CryptographicException or JsonException)
            {
                log.LogWarning("Membership {MembershipId} of vault {VaultId} does not open", doc.Id, vault.Id);
            }
        }

        return result;
    }

    /// <summary>The display name of the user <paramref name="userId"/> (the username as typed), or null.</summary>
    internal async Task<string?> DisplayNameAsync(string userId, CancellationToken ct) =>
        (await db.GetCollection<UserDocument>("users").Find(u => u.Id == userId).Project(u => u.Display).FirstOrDefaultAsync(ct));

    /// <summary>
    /// The filter that moves a membership from <paramref name="from"/> to <paramref name="to"/>, or null if statuses
    /// can't move that way: only <c>pending → active → revoked | left</c> and <c>pending → revoked</c>. A tombstone
    /// never comes back.
    /// </summary>
    internal static FilterDefinition<MembershipDocument>? TransitionFilter(string from, string to) =>
        (from, to) switch
        {
            (MembershipStatus.Pending, MembershipStatus.Active)
                or (MembershipStatus.Pending, MembershipStatus.Revoked)
                or (MembershipStatus.Active, MembershipStatus.Revoked)
                or (MembershipStatus.Active, MembershipStatus.Left) => Builders<MembershipDocument>.Filter.Eq(m => m.Status, from),
            _ => null,
        };

    /// <summary>Creates the indexes once per process; lazy, so the server starts before MongoDB is reachable.</summary>
    internal async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        var keys = Builders<MembershipDocument>.IndexKeys;
        await Docs.Indexes.CreateManyAsync(
            [
                // One membership per vault and member; a pending invitation nobody redeemed has no member (null), and
                // any number of those may wait side by side: $type, since $exists would count a null.
                new CreateIndexModel<MembershipDocument>(
                    keys.Ascending(m => m.Vault).Ascending(m => m.Member),
                    new CreateIndexOptions<MembershipDocument>
                    {
                        Unique = true,
                        PartialFilterExpression = new BsonDocument("member", new BsonDocument("$type", "string")),
                    }),
                // A member's vaults, and a vault's members, each by status.
                new CreateIndexModel<MembershipDocument>(keys.Ascending(m => m.Member).Ascending(m => m.Status)),
                new CreateIndexModel<MembershipDocument>(keys.Ascending(m => m.Vault).Ascending(m => m.Status)),
                // An invite secret finds its membership; redemption unsets it.
                new CreateIndexModel<MembershipDocument>(
                    keys.Ascending(m => m.InviteLookup),
                    new CreateIndexOptions<MembershipDocument>
                    {
                        Unique = true,
                        PartialFilterExpression = new BsonDocument("inviteLookup", new BsonDocument("$type", "binData")),
                    }),
            ],
            ct);
        _ready = true;
    }
}

/// <summary>The vault is shared with as many people as it may be.</summary>
public sealed class MemberLimitException(int limit) : Exception($"A vault can be shared with at most {limit} people.");

/// <summary>The person already has a membership of the vault.</summary>
public sealed class AlreadyMemberException() : Exception("This person is already a member of the vault.");

/// <summary>A membership's names, opened with its vault: the owner's <c>Label</c> for the member, the member's
/// <c>Writers</c> (credential id → display name) and <c>WriterIds</c> (writer id → credential id, or
/// <see cref="Memberships.SessionWriter"/>).</summary>
internal sealed record MembershipNames(
    string Id, string Owner, string Status, string Label, IReadOnlyDictionary<string, string> Writers, IReadOnlyDictionary<string, string> WriterIds);
