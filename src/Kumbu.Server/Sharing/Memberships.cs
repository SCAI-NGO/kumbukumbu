// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kumbu.Core.Crypto;
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
public sealed partial class Memberships(
    IMongoDatabase db, VaultCatalog catalog, ServerSecret secret, IOptions<KumbuOptions> options, TimeProvider time, ILogger<Memberships> log)
{
    internal const string CollectionName = "memberships";

    private volatile bool _ready;

    /// <summary>The users' keypairs; internal, for the tests' hooks.</summary>
    internal MemberKeys Keys { get; } = new(db);

    /// <summary>For tests: called with <c>"inside-grant"</c> once the grant's writes are done, before the commit, and in
    /// an acceptance with <c>"accept-before-user"</c> before the user document is written and <c>"inside-accept"</c>
    /// before the commit; an exception thrown there is a crash at that point.</summary>
    internal Func<string, Task>? FaultAt { get; set; }

    internal IMongoCollection<MembershipDocument> Docs { get; } = db.GetCollection<MembershipDocument>(CollectionName);

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    internal const string LabelPurpose = "kumbu/v1/membership-label";
    internal const string WritersPurpose = "kumbu/v1/membership-writers";
    internal const string WriterIdsPurpose = "kumbu/v1/membership-writer-ids";
    internal const string PinPurpose = "kumbu/v1/membership-pin";

    /// <summary>What the member's sessions are in <c>writerIds</c>, in place of a credential id: all her sessions on the
    /// vault write under one writer id of the membership (collab design §8.1).</summary>
    internal const string SessionWriter = "session";

    /// <summary>At most this many pending memberships per vault (concept §10.6), redeemed but unconfirmed ones included.</summary>
    public const int PendingLimit = 10;

    /// <summary>The statuses that count towards the member limit: tombstones don't.</summary>
    private static readonly string[] Live = [MembershipStatus.Pending, MembershipStatus.Active];

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private int MemberLimit => options.Value.Vaults.MemberLimit;

    /// <summary>
    /// Makes <paramref name="memberId"/> an active member of <paramref name="vaultId"/>, which <paramref name="ownerId"/>
    /// owns, under <paramref name="alias"/> (the member's name for it) and <paramref name="label"/> (the owner's for
    /// the member). One transaction: the member's keypair if she has none yet, the membership, with its label and its
    /// empty writers sealed under the vault key, the member slot (<see cref="VaultCatalog.MemberSlot"/>) and a write to
    /// the vault document, which makes two grants at once conflict, so the member limit, counted inside, holds, and to the
    /// member's user document, conditioned on her user key (<see cref="CredentialsChangedException"/> if it isn't hers
    /// any more). The core of an acceptance; returns the
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
        using var session = await catalog.StartSessionAsync(ct);
        // Retried on a write conflict: another grant on the vault, or a re-keying's start or finish.
        await session.WithTransactionAsync(
            async (s, token) =>
            {
                // Her user document first, conditioned on her user key (§13 M8): a rotation of it or her account's deletion
                // at the same time conflicts, so no slot commits wrapped to her old public key under her old lookup.
                await CountAcceptedAsync(s, memberId, memberKey, token);
                // Her keypair, if she has none, in this transaction (collab design §5.1): an invitation never waits for one,
                // and a crash leaves neither a keypair without the grant nor a slot without a keypair.
                var memberPublic = await MemberPublicKeyAsync(s, memberId, memberKey, token);
                var doc = NewMembership(vaultId, ownerId, memberId, memberPublic, vaultKey, id, label, role);
                var slot = catalog.MemberSlot(vaultId, id, memberId, memberKey, memberPublic, vaultKey, alias);
                await GrantInAsync(s, ownerId, ownerKey, vaultKey, doc, slot, alias, token);
                if (FaultAt is { } fault) await fault("inside-grant");
                return true;
            },
            cancellationToken: ct);
        log.LogInformation("Vault {VaultId} is shared with user {UserId} as membership {MembershipId}", vaultId, memberId, id);
        return id;
    }

    /// <summary>A grant's membership, not stored here.</summary>
    private MembershipDocument NewMembership(string vaultId, string ownerId, string memberId, byte[] memberPublic, byte[] vaultKey, string id, string label, MemberRole role)
    {
        var now = Now;
        return new MembershipDocument
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
            // The key the slot is wrapped to, pinned under the vault key: the only key a re-key or a rotation wraps to (§5.4).
            Pin = SealPin(vaultKey, vaultId, id, memberId, memberPublic),
            CreatedAt = now,
            AcceptedAt = now,
            Revision = 0,
        };
    }

    /// <summary>
    /// The public key a member slot for <paramref name="memberId"/> is wrapped to (collab design §5.4): derived from
    /// her private key, opened with her user key, never read from her user document, where a database-level attacker
    /// could have put his own. She gets a keypair here if she has none yet. <see cref="CryptographicException"/> if
    /// her private key doesn't open: then no slot is made. Inside <paramref name="session"/>'s transaction.
    /// </summary>
    private async Task<byte[]> MemberPublicKeyAsync(IClientSessionHandle session, string memberId, byte[] memberKey, CancellationToken ct)
    {
        var user = await Keys.EnsureAsync(session, memberId, memberKey, ct) ?? throw new ArgumentException("There is no such user.", nameof(memberId));
        try
        {
            using var privateKey = MemberKeys.OpenPrivate(user, memberKey);
            return MemberKeys.PublicFromPrivate(privateKey);
        }
        catch (CryptographicException)
        {
            log.LogWarning("The private key of user {UserId} does not open; nothing is shared with them", memberId);
            throw;
        }
    }

    /// <summary>
    /// A membership's <c>pin</c> (collab design §2.1, §5.4): the member's public key with what it is the key of, sealed
    /// under <paramref name="vaultKey"/>, bound to the vault and the membership. Written at acceptance from the keypair
    /// the member's session just unwrapped, never from her user document.
    /// </summary>
    internal static byte[] SealPin(byte[] vaultKey, string vaultId, string membershipId, string memberId, byte[] publicKey) =>
        VaultCatalog.SealForVault(
            vaultKey, vaultId, PinPurpose, JsonSerializer.SerializeToUtf8Bytes(new MembershipPin(membershipId, vaultId, memberId, MemberSlotCrypto.P256, publicKey)),
            PinContext(vaultId, membershipId));

    private static string PinContext(string vaultId, string membershipId) => $"membership-pin:{vaultId}:{membershipId}";

    /// <summary>
    /// The public key pinned in <paramref name="membership"/>: the only source of a key to wrap a member slot to after
    /// the grant (HARD RULE 3, collab design §5.4). Null unless the pin opens under <paramref name="vaultKey"/> and names
    /// this membership, its vault and its member, with a known algorithm and a key on the curve; never a key the
    /// database names in plain (the user document's <c>publicKey</c>).
    /// </summary>
    internal static byte[]? PinnedKey(MembershipDocument membership, byte[] vaultKey)
    {
        if (membership.Pin is not { } sealedPin || membership.Member is not { } member) return null;
        try
        {
            var pin = JsonSerializer.Deserialize<MembershipPin>(VaultCatalog.OpenForVault(vaultKey, membership.Vault, PinPurpose, sealedPin, PinContext(membership.Vault, membership.Id)));
            if (pin is null || pin.MembershipId != membership.Id || pin.Vault != membership.Vault || pin.Member != member || pin.Alg != MemberSlotCrypto.P256) return null;
            MemberSlotCrypto.RequireOnCurve(pin.PublicKey);
            return pin.PublicKey;
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Wraps the vault key again to the key pinned in <paramref name="membership"/> (<see cref="PinnedKey"/>, opened
    /// under <paramref name="vaultKey"/>), inside <paramref name="session"/>'s transaction: <paramref name="newVaultKey"/>
    /// for a re-key's finish, else <paramref name="vaultKey"/>. The shared seam of the member's rotation and the re-key
    /// finish; nothing else wraps a member slot after the grant. Only the membership's existing member slot is
    /// rewritten, never one created. Without a valid pin, the membership's member slots are deleted and logged as
    /// damaged. Returns the pinned key, or null if there was none (or no member slot to rewrap).
    /// </summary>
    internal Task<byte[]?> WrapToPinAsync(IClientSessionHandle session, MembershipDocument membership, byte[] vaultKey, byte[]? newVaultKey, CancellationToken ct) =>
        WrapToPinAsync(db, log, session, membership, vaultKey, newVaultKey, null, ct);

    /// <summary>
    /// As the instance method, on the collections, for <see cref="UserKeyRotation"/>, which can't depend on this class.
    /// With <paramref name="rotation"/>, the member's own rotation (collab design §5.1): the pin must name her old public
    /// key, or it counts as damaged; the slot is wrapped to her new public key instead, and only where it still holds
    /// what her old private key opened. The caller then seals the pin again for the new key.
    /// </summary>
    internal static async Task<byte[]?> WrapToPinAsync(
        IMongoDatabase db, ILogger log, IClientSessionHandle session, MembershipDocument membership, byte[] vaultKey, byte[]? newVaultKey, PinRotation? rotation,
        CancellationToken ct)
    {
        var slotDocs = db.GetCollection<SlotDocument>("slots");
        var f = Builders<SlotDocument>.Filter;
        var slots = f.Eq(s => s.Kind, SlotDocument.Member) & f.Eq(s => s.Membership, membership.Id);
        if (PinnedKey(membership, vaultKey) is not { } pinned || (rotation is not null && !CryptographicOperations.FixedTimeEquals(pinned, rotation.OldPublicKey)))
        {
            var deleted = await slotDocs.DeleteManyAsync(session, slots, cancellationToken: ct);
            log.LogWarning(
                "Membership {MembershipId} of vault {VaultId} has no valid pin: damaged; its {Count} member slots were deleted", membership.Id, membership.Vault, deleted.DeletedCount);
            return null;
        }

        // Every member slot of the membership on its vault: a second one written to the database gets the pinned key too.
        var target = slots & f.Eq(s => s.Vault, membership.Vault);
        if (rotation is not null) target &= f.Eq(s => s.Wrapped, rotation.OldWrapped);
        var updated = await slotDocs.UpdateManyAsync(
            session,
            target,
            Builders<SlotDocument>.Update.Set(s => s.Wrapped, MemberSlotCrypto.Wrap(rotation?.NewPublicKey ?? pinned, newVaultKey ?? vaultKey, VaultCatalog.MemberSlotContext(membership.Vault, membership.Id))),
            cancellationToken: ct);
        return updated.MatchedCount > 0 ? pinned : null;
    }

    /// <summary>The grant's writes, inside the caller's transaction.</summary>
    private async Task GrantInAsync(
        IClientSessionHandle s, string ownerId, byte[] ownerKey, byte[] vaultKey, MembershipDocument doc, SlotDocument slot, string alias, CancellationToken ct)
    {
        await MembershipsChangedAsync(s, doc.Vault, ct);
        // The key the slot wraps is still the vault's: no re-keying started or finished since it was opened.
        await catalog.RequireSameVaultKeysAsync(s, ownerId, ownerKey, new Dictionary<string, byte[]> { [doc.Vault] = vaultKey }, ct);
        await RequireUnderLimitsAsync(s, doc.Vault, pending: false, ct);

        try
        {
            await Docs.InsertOneAsync(s, doc, cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new AlreadyMemberException();
        }

        await InsertMemberSlotAsync(s, slot, alias, ct);
    }

    /// <summary>The write to the vault document of every transaction that creates or activates a membership (collab
    /// design §13 M8): two at once conflict, so the limits counted after it hold. <see cref="VaultDeletedException"/>
    /// if the vault is gone.</summary>
    private async Task MembershipsChangedAsync(IClientSessionHandle s, string vaultId, CancellationToken ct)
    {
        var vault = await catalog.Vaults.UpdateOneAsync(
            s, v => v.Id == vaultId, Builders<VaultDocument>.Update.Inc(v => v.MembershipsChanged, 1), cancellationToken: ct);
        if (vault.MatchedCount == 0) throw new VaultDeletedException();
    }

    /// <summary>
    /// Inside the transaction, after <see cref="MembershipsChangedAsync"/>: room for one more membership of the vault
    /// (<see cref="MemberLimit"/> pending and active ones), and with <paramref name="pending"/> for one more pending one
    /// (<see cref="PendingLimit"/>, redeemed but unconfirmed ones included).
    /// </summary>
    private async Task RequireUnderLimitsAsync(IClientSessionHandle s, string vaultId, bool pending, CancellationToken ct)
    {
        var f = Builders<MembershipDocument>.Filter;
        var live = await Docs.CountDocumentsAsync(s, f.Eq(m => m.Vault, vaultId) & f.In(m => m.Status, Live), cancellationToken: ct);
        if (live >= MemberLimit) throw new MemberLimitException(MemberLimit);
        if (!pending) return;
        var open = await Docs.CountDocumentsAsync(s, f.Eq(m => m.Vault, vaultId) & f.Eq(m => m.Status, MembershipStatus.Pending), cancellationToken: ct);
        if (open >= PendingLimit) throw new PendingLimitException(PendingLimit);
    }

    /// <summary>The member slot, inside the transaction: the name index refuses an alias the member already has as a
    /// vault name or another alias (<see cref="VaultNameTakenException"/>), which aborts the whole transaction.</summary>
    private async Task InsertMemberSlotAsync(IClientSessionHandle s, SlotDocument slot, string alias, CancellationToken ct)
    {
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

    /// <summary>
    /// Inside the transaction of <paramref name="userId"/>'s account deletion (collab design §7.2): every membership
    /// with that user as member goes, tombstones included, with its member slot and the slots derived through it
    /// (<c>grantedVia</c>). Nothing that names the member stays with the vault; their writes resolve to "a former member".
    /// Only <c>member == userId</c>: the vaults the user owns, and their memberships, go with those vaults. The number deleted.
    /// </summary>
    internal static async Task<int> DeleteOfMemberAsync(IMongoDatabase db, IClientSessionHandle s, string userId, CancellationToken ct)
    {
        var docs = db.GetCollection<MembershipDocument>(CollectionName);
        var ids = await docs.Find(s, m => m.Member == userId).Project(m => m.Id).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        var f = Builders<SlotDocument>.Filter;
        await db.GetCollection<SlotDocument>("slots").DeleteManyAsync(
            s, (f.Eq(sl => sl.Kind, SlotDocument.Member) & f.In(sl => sl.Membership, ids)) | f.In(sl => sl.GrantedVia, ids), cancellationToken: ct);
        await docs.DeleteManyAsync(s, Builders<MembershipDocument>.Filter.In(m => m.Id, ids), cancellationToken: ct);
        return ids.Count;
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

/// <summary>
/// A member's rotation of her keypair, for the rotation's <c>Memberships.WrapToPinAsync</c>:
/// <c>OldPublicKey</c> and <c>NewPublicKey</c> both derived from private keys in hand (the old one opened with her old
/// user key, the new one just made), never read from her user document; <c>OldWrapped</c> the member slot her old
/// private key opened.
/// </summary>
internal sealed record PinRotation(byte[] OldPublicKey, byte[] NewPublicKey, byte[] OldWrapped);

/// <summary>What a membership's <c>pin</c> holds, sealed under the vault key (collab design §2.1).</summary>
internal sealed record MembershipPin(
    [property: JsonPropertyName("membershipId")] string MembershipId,
    [property: JsonPropertyName("vault")] string Vault,
    [property: JsonPropertyName("member")] string Member,
    [property: JsonPropertyName("alg")] byte Alg,
    [property: JsonPropertyName("publicKey")] byte[] PublicKey);

/// <summary>The vault is shared with as many people as it may be.</summary>
public sealed class MemberLimitException(int limit) : Exception($"A vault can be shared with at most {limit} people.");

/// <summary>The vault has as many pending invitations as it may.</summary>
public sealed class PendingLimitException(int limit) : Exception($"A vault can have at most {limit} open invitations; withdraw one first.");

/// <summary>The person already has a membership of the vault.</summary>
public sealed class AlreadyMemberException() : Exception("This person is already a member of the vault.");

/// <summary>A membership's names, opened with its vault: the owner's <c>Label</c> for the member, the member's
/// <c>Writers</c> (credential id → display name) and <c>WriterIds</c> (writer id → credential id, or
/// <see cref="Memberships.SessionWriter"/>).</summary>
internal sealed record MembershipNames(
    string Id, string Owner, string Status, string Label, IReadOnlyDictionary<string, string> Writers, IReadOnlyDictionary<string, string> WriterIds);
