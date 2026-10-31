// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Crypto;
using Kumbu.Server.Sharing;
using MongoDB.Driver;

namespace Kumbu.Server.Vaults;

/// <summary>The slots of shared vaults' members (collab design §2.2, §2.3).</summary>
public sealed partial class VaultCatalog
{
    private const string AliasLabel = "kumbu/v1/alias";
    private const string InviteSlotLabel = "kumbu/v1/invite-slot";

    /// <summary>
    /// A member's slot on a shared vault, not stored here. It wraps the vault key to <paramref name="memberPublic"/>
    /// (collab design §5.2), bound to the vault and the membership; the caller derives that key from the member's
    /// private key, never from her user document (§5.4). Under her own lookup, with the alias's hash under her own
    /// name key: the index on <c>(lookup, nameId)</c> keeps aliases and own vault names apart in both directions. The
    /// alias itself is sealed under a random alias key, bound to the vault and the membership, and the alias key under
    /// a key from her user key; the owner can read neither. Her keys and connections on the vault get the alias key,
    /// so a renamed alias reaches them too (<see cref="RenameAliasAsync"/>).
    /// </summary>
    internal SlotDocument MemberSlot(string vaultId, string membershipId, string memberId, byte[] memberKey, byte[] memberPublic, byte[] vaultKey, string alias)
    {
        var aliasKey = VaultCrypto.NewKey();
        return new()
        {
            Vault = vaultId,
            Kind = SlotDocument.Member,
            Lookup = UserLookup(memberKey, memberId),
            NameId = AliasNameId(memberKey, alias),
            AliasKey = SealAliasKey(memberKey, vaultId, membershipId, aliasKey),
            Alias = SealAlias(aliasKey, vaultId, membershipId, alias),
            Membership = membershipId,
            Wrapped = MemberSlotCrypto.Wrap(memberPublic, vaultKey, MemberSlotContext(vaultId, membershipId)),
            CreatedAt = Now,
        };
    }

    /// <summary>
    /// A pending invitation's slot, not stored here (collab design §2.2): the vault key wrapped under
    /// <c>HKDF(invite secret, "kumbu/v1/invite-slot", salt = vault id)</c>, bound to the vault and the membership. It
    /// carries <paramref name="expiresAt"/> for the TTL index; the secret itself is in the link alone.
    /// </summary>
    internal SlotDocument InviteSlot(string vaultId, string membershipId, byte[] secret, byte[] vaultKey, DateTime expiresAt) => new()
    {
        Vault = vaultId,
        Kind = SlotDocument.Invite,
        Membership = membershipId,
        ExpiresAt = expiresAt,
        Wrapped = VaultCrypto.Seal(InviteKek(secret, vaultId), vaultKey, InviteSlotContext(vaultId, membershipId)),
        CreatedAt = Now,
    };

    /// <summary>The vault key an invite slot wraps, opened with the invite secret; <see cref="CryptographicException"/>
    /// if it doesn't open (another secret, vault or membership).</summary>
    internal static byte[] OpenInviteSlot(byte[] secret, SlotDocument slot) =>
        VaultCrypto.Open(InviteKek(secret, slot.Vault), slot.Wrapped, InviteSlotContext(slot.Vault, slot.Membership ?? ""));

    private static byte[] InviteKek(byte[] secret, string vaultId) => VaultCrypto.Derive(secret, InviteSlotLabel, Encoding.UTF8.GetBytes(vaultId));

    private static string InviteSlotContext(string vaultId, string membershipId) => $"slot:{vaultId}:{SlotDocument.Invite}:{membershipId}";

    /// <summary>
    /// What an invitation shows of its vault (collab design §4.2): its name and embedding provider, opened from the
    /// vault document's meta with <paramref name="vaultKey"/>, inside <paramref name="session"/>'s transaction if given.
    /// Nothing is loaded or unlocked, and the key is kept nowhere. Null if the vault is gone, in its two-key period, or
    /// its meta doesn't open with the key (it is no longer the vault's).
    /// </summary>
    internal async Task<InviteVaultMeta?> InviteMetaAsync(IClientSessionHandle? session, string vaultId, byte[] vaultKey, CancellationToken ct)
    {
        var doc = await (session is null ? Vaults.Find(v => v.Id == vaultId) : Vaults.Find(session, v => v.Id == vaultId)).FirstOrDefaultAsync(ct);
        if (doc is null || doc.RekeyStartedAt is not null) return null;
        try
        {
            var meta = System.Text.Json.JsonSerializer.Deserialize<VaultMeta>(VaultCrypto.Open(VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), doc.Meta, $"vault:{doc.Id}"))!;
            return new InviteVaultMeta(meta.Name, meta.Embeddings ?? DefaultEmbeddingProvider, doc.ClosingAt is not null);
        }
        catch (Exception e) when (e is CryptographicException or System.Text.Json.JsonException)
        {
            log.LogWarning("An invite slot of vault {VaultId} opened to a key its meta doesn't open with", vaultId);
            return null;
        }
    }

    /// <summary>Whether <paramref name="name"/> is free among the user's vault names and aliases: what the name index
    /// would refuse for a new member slot of hers.</summary>
    internal async Task<bool> NameFreeAsync(string userId, byte[] userKey, string name, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var f = Builders<SlotDocument>.Filter;
        return !await Slots.Find(f.Eq(s => s.Lookup, UserLookup(userKey, userId)) & f.Eq(s => s.NameId, AliasNameId(userKey, name))).AnyAsync(ct);
    }

    /// <summary>The AAD of a member slot's wrapped vault key.</summary>
    internal static string MemberSlotContext(string vaultId, string membershipId) => $"slot:{vaultId}:{SlotDocument.Member}:{membershipId}";

    /// <summary>The vault key a member slot wraps, opened with the member's private key; <see cref="CryptographicException"/>
    /// if it doesn't open (another key, another vault or membership, or not of this format).</summary>
    internal static byte[] OpenMemberSlot(ECDiffieHellman memberPrivate, SlotDocument slot) =>
        MemberSlotCrypto.Unwrap(memberPrivate, slot.Wrapped, MemberSlotContext(slot.Vault, slot.Membership ?? ""));

    /// <summary>
    /// The private key of <paramref name="userId"/>, opened with <paramref name="userKey"/>, for her member slots: read
    /// once per request that meets one. Null, logged, if she has none or it doesn't open; her member slots then open
    /// nothing.
    /// </summary>
    internal async Task<ECDiffieHellman?> MemberPrivateKeyAsync(IClientSessionHandle? session, string userId, byte[] userKey, CancellationToken ct)
    {
        var user = await (session is null ? Users.Find(u => u.Id == userId) : Users.Find(session, u => u.Id == userId)).FirstOrDefaultAsync(ct);
        if (user is null) return null;
        try
        {
            return MemberKeys.OpenPrivate(user, userKey);
        }
        catch (CryptographicException)
        {
            log.LogWarning("The private key of user {UserId} did not open; their member slots open nothing", userId);
            return null;
        }
    }

    /// <summary>The alias a member slot holds, opened with the member's user key.</summary>
    internal static string OpenAlias(byte[] memberKey, SlotDocument slot) => OpenAliasWith(OpenAliasKey(memberKey, slot), slot);

    /// <summary>The alias key of a member slot, opened with the member's user key.</summary>
    internal static byte[] OpenAliasKey(byte[] memberKey, SlotDocument slot) =>
        VaultCrypto.Open(AliasKeyKek(memberKey, slot.Vault), slot.AliasKey ?? [], AliasKeyContext(slot.Vault, slot.Membership ?? ""));

    /// <summary>The alias a member slot holds, opened with its alias key: how a member's key or connection reads it.</summary>
    internal static string OpenAliasWith(byte[] aliasKey, SlotDocument slot) =>
        Encoding.UTF8.GetString(VaultCrypto.Open(aliasKey, slot.Alias ?? [], AliasContext(slot.Vault, slot.Membership ?? "")));

    /// <summary>The alias key sealed again under another user key, for a rotation of the member's user key.</summary>
    internal static byte[] SealAliasKey(byte[] memberKey, string vaultId, string membershipId, byte[] aliasKey) =>
        VaultCrypto.Seal(AliasKeyKek(memberKey, vaultId), aliasKey, AliasKeyContext(vaultId, membershipId));

    private static byte[] SealAlias(byte[] aliasKey, string vaultId, string membershipId, string alias) =>
        VaultCrypto.Seal(aliasKey, Encoding.UTF8.GetBytes(alias), AliasContext(vaultId, membershipId));

    /// <summary>The alias's hash for the name index, under the member's own name key: as her own vault names are.</summary>
    internal static byte[] AliasNameId(byte[] memberKey, string alias) => VaultCrypto.Mac(VaultCrypto.Derive(memberKey, VaultNameLabel, []), NormaliseVaultName(alias));

    private static byte[] AliasKeyKek(byte[] memberKey, string vaultId) => VaultCrypto.Derive(memberKey, AliasLabel, Encoding.UTF8.GetBytes(vaultId));

    private static string AliasContext(string vaultId, string membershipId) => $"alias:{vaultId}:{membershipId}";

    private static string AliasKeyContext(string vaultId, string membershipId) => $"alias-key:{vaultId}:{membershipId}";

    /// <summary>
    /// Renames the vault <paramref name="memberId"/> has through <paramref name="membershipId"/> to
    /// <paramref name="newAlias"/> (collab design §2.3): one update of the member slot's <c>nameId</c> and <c>alias</c>,
    /// in a transaction that also reads the membership, which must be hers and active. The name index refuses a name
    /// she has for an own vault or another alias (<see cref="VaultNameTakenException"/>), and the old alias stays.
    /// Her keys and connections read the alias through the alias key, which doesn't change: from the next request on
    /// they see the new name too, and none of them keeps the old one, which is free for an own vault now.
    /// <see cref="ArgumentException"/> if no such membership is hers.
    /// </summary>
    internal async Task RenameAliasAsync(string memberId, byte[] memberKey, string membershipId, string newAlias, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var alias = newAlias.Normalize(NormalizationForm.FormC).Trim();
        if (alias.Length == 0 || alias.Contains('/')) throw new ArgumentException("That can't be a vault name.", nameof(newAlias));
        var f = Builders<SlotDocument>.Filter;
        var notYours = new ArgumentException("No vault is shared with you under that membership.", nameof(membershipId));
        using var session = await StartSessionAsync(ct);
        await session.WithTransactionAsync(
            async (s, token) =>
            {
                var membership = await db.GetCollection<MembershipDocument>(Memberships.CollectionName)
                    .Find(s, m => m.Id == membershipId && m.Member == memberId && m.Status == MembershipStatus.Active).FirstOrDefaultAsync(token) ?? throw notYours;
                var slot = await Slots.Find(s, f.Eq(x => x.Kind, SlotDocument.Member) & f.Eq(x => x.Membership, membershipId)
                    & f.Eq(x => x.Vault, membership.Vault) & f.Eq(x => x.Lookup, UserLookup(memberKey, memberId))).FirstOrDefaultAsync(token) ?? throw notYours;
                byte[] aliasKey;
                try
                {
                    aliasKey = OpenAliasKey(memberKey, slot);
                }
                catch (CryptographicException)
                {
                    throw notYours;
                }

                try
                {
                    var updated = await Slots.UpdateOneAsync(
                        s,
                        f.Eq(x => x.Id, slot.Id) & f.Eq(x => x.Alias, slot.Alias),
                        Builders<SlotDocument>.Update.Set(x => x.NameId, AliasNameId(memberKey, alias)).Set(x => x.Alias, SealAlias(aliasKey, slot.Vault, membershipId, alias)),
                        cancellationToken: token);
                    if (updated.MatchedCount == 0) throw notYours;
                }
                catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
                {
                    throw new VaultNameTakenException(alias);
                }

                return true;
            },
            cancellationToken: ct);
    }

    /// <summary>
    /// Seals what a membership keeps under the vault key (its label, writers, writer ids, pin): under
    /// <c>HKDF(vault key, purpose, vault id)</c>, with <paramref name="aad"/> binding it to its membership.
    /// </summary>
    internal static byte[] SealForVault(byte[] vaultKey, string vaultId, string purpose, byte[] plain, string aad) =>
        VaultCrypto.Seal(VaultCrypto.Derive(vaultKey, purpose, Encoding.UTF8.GetBytes(vaultId)), plain, aad);

    internal static byte[] OpenForVault(byte[] vaultKey, string vaultId, string purpose, byte[] sealedBlob, string aad) =>
        VaultCrypto.Open(VaultCrypto.Derive(vaultKey, purpose, Encoding.UTF8.GetBytes(vaultId)), sealedBlob, aad);

    /// <summary>
    /// Whether the vault has a membership or an invitation that is neither revoked nor left: shared, for what phase 1
    /// still refuses on shared vaults (re-keying, pairing a device). Inside <paramref name="session"/>'s transaction if given.
    /// </summary>
    internal async Task<bool> IsSharedAsync(IClientSessionHandle? session, string vaultId, CancellationToken ct)
    {
        var f = Builders<MembershipDocument>.Filter;
        var filter = f.Eq(m => m.Vault, vaultId) & f.Nin(m => m.Status, [MembershipStatus.Revoked, MembershipStatus.Left]);
        var docs = db.GetCollection<MembershipDocument>(Memberships.CollectionName);
        return await (session is null ? docs.Find(filter) : docs.Find(session, filter)).AnyAsync(ct);
    }

    /// <summary>The key of a vault <paramref name="ownerId"/> owns, from her user slot: <see cref="ArgumentException"/> if
    /// it isn't hers, <see cref="VaultBusyException"/> while it is re-keyed.</summary>
    internal async Task<byte[]> OwnerVaultKeyAsync(string ownerId, byte[] ownerKey, string vaultId, CancellationToken ct)
    {
        var grant = (await UserVaultKeysAsync(ownerId, ownerKey, [vaultId], "A share", ct))[vaultId];
        // Opened through a member slot: shared with her, not hers to share.
        if (grant.Membership is not null) throw new ArgumentException("A share can only be given vaults of your own.", nameof(vaultId));
        return grant.Key;
    }

    /// <summary>
    /// Of the memberships <paramref name="ids"/>, those active now, by id: one indexed query, read afresh on every call
    /// and never cached, since it is what stands between a revoked member and a vault already in memory.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, MembershipDocument>> ActiveMembershipsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var f = Builders<MembershipDocument>.Filter;
        var found = await db.GetCollection<MembershipDocument>(Memberships.CollectionName)
            .Find(f.In(m => m.Id, ids.Distinct(StringComparer.Ordinal)) & f.Eq(m => m.Status, MembershipStatus.Active))
            .Project<MembershipDocument>(Builders<MembershipDocument>.Projection.Include(m => m.Vault).Include(m => m.Owner).Include(m => m.Member).Include(m => m.Role).Include(m => m.WriterIds))
            .ToListAsync(ct);
        return found.ToDictionary(m => m.Id, StringComparer.Ordinal);
    }

    /// <summary>The display name of user <paramref name="userId"/> as typed, or null: for a member's view of her vault's owner.</summary>
    internal Task<string?> DisplayNameAsync(string userId, CancellationToken ct) =>
        Users.Find(u => u.Id == userId).Project(u => (string?)u.Display).FirstOrDefaultAsync(ct);

    /// <summary>Whether <paramref name="memberId"/> holds an active membership of <paramref name="vaultId"/>.</summary>
    internal Task<bool> HasActiveMembershipAsync(string vaultId, string memberId, CancellationToken ct) =>
        db.GetCollection<MembershipDocument>(Memberships.CollectionName)
            .Find(m => m.Vault == vaultId && m.Member == memberId && m.Status == MembershipStatus.Active).AnyAsync(ct);

    /// <summary>
    /// The memberships the derived slots among <paramref name="slots"/> (<c>grantedVia</c>) name, those active now; null,
    /// without a query, when none is derived: owners' keys and connections pay nothing.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, MembershipDocument>?> ActiveGrantsAsync(IEnumerable<SlotDocument> slots, CancellationToken ct)
    {
        var ids = slots.Select(s => s.GrantedVia).OfType<string>().ToList();
        return ids.Count == 0 ? null : await ActiveMembershipsAsync(ids, ct);
    }

    /// <summary>
    /// The member slots of the memberships in <paramref name="active"/>, by membership: where a member's key or
    /// connection reads the alias she has now (<see cref="AliasOf"/>). One indexed query, only when there are derived
    /// slots; null otherwise.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, SlotDocument>?> MemberAliasesAsync(IReadOnlyDictionary<string, MembershipDocument>? active, CancellationToken ct)
    {
        if (active is null || active.Count == 0) return null;
        var f = Builders<SlotDocument>.Filter;
        var slots = await Slots.Find(f.Eq(s => s.Kind, SlotDocument.Member) & f.In(s => s.Membership, active.Keys))
            .Project<SlotDocument>(Builders<SlotDocument>.Projection.Include(s => s.Vault).Include(s => s.Membership).Include(s => s.Alias))
            .ToListAsync(ct);
        // A membership has one member slot; a second (written to the database) makes the alias not open at all.
        return slots.GroupBy(s => s.Membership!, StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
    }

    /// <summary>The alias a derived slot's credential sees its vault under: its membership's member slot's, opened with the
    /// alias key the credential sealed; null if there is no such slot on the vault, or it doesn't open (fails closed).</summary>
    private string? AliasOf(IReadOnlyDictionary<string, SlotDocument>? aliases, SlotDocument slot, byte[]? aliasKey)
    {
        if (aliasKey is null || slot.GrantedVia is not { } id || aliases is null || !aliases.TryGetValue(id, out var member) || member.Vault != slot.Vault) return null;
        try
        {
            return OpenAliasWith(aliasKey, member);
        }
        catch (CryptographicException)
        {
            log.LogWarning("The alias of membership {MembershipId} on vault {VaultId} did not open for a credential", id, slot.Vault);
            return null;
        }
    }

    /// <summary>The active membership a derived slot was granted through, if it is on the slot's vault; else null.</summary>
    private static MembershipDocument? ActiveGrant(IReadOnlyDictionary<string, MembershipDocument>? active, SlotDocument slot) =>
        slot.GrantedVia is { } id && active is not null && active.TryGetValue(id, out var m) && m.Vault == slot.Vault ? m : null;

    /// <summary>A member's credential acts at most at her role (collab design §3.3): <c>min(role, sealed)</c>. A role
    /// that doesn't parse reads as <c>read</c>.</summary>
    internal static Accounts.KeyRight Capped(Accounts.KeyRight sealedRight, MembershipDocument membership)
    {
        var role = MemberRoles.Parse(membership.Role) switch
        {
            MemberRole.Maintain => Accounts.KeyRight.Maintain,
            MemberRole.Write => Accounts.KeyRight.Write,
            _ => Accounts.KeyRight.Read,
        };
        return sealedRight < role ? sealedRight : role;
    }

    /// <summary>A member's session on a shared vault: under her alias, at her role (collab design §3.3), writing under
    /// her sessions' writer id.</summary>
    private static VaultAccess MemberAccess(UnlockedVault vault, byte[] vaultKey, string alias, MembershipDocument membership) =>
        new(vault, alias, LevelOf(membership), membership, IsOwner: false) { WriterId = Memberships.WriterIdOf(membership, vaultKey, Memberships.SessionWriter) };

    /// <summary>A key's or a connection's access: the owner's, under the vault's name at its sealed level; a member's,
    /// under her alias, at most at her role, writing under the writer id recorded for <paramref name="credentialId"/>.</summary>
    private static VaultAccess CredentialAccess(
        UnlockedVault vault, byte[] vaultKey, Accounts.KeyRight right, MembershipDocument? membership, string? alias, string credentialId) =>
        membership is null
            ? new(vault, vault.Name, Accounts.KeyRights.ToLevel(right), null, IsOwner: true)
            : new(vault, alias!, Accounts.KeyRights.ToLevel(Capped(right, membership)), membership, IsOwner: false)
            {
                WriterId = Memberships.WriterIdOf(membership, vaultKey, credentialId),
            };

    /// <summary>The level a membership's role gives; a role that doesn't parse reads as <c>read</c>.</summary>
    private static Kumbu.Core.Notes.AccessLevel LevelOf(MembershipDocument membership) => MemberRoles.Parse(membership.Role) switch
    {
        MemberRole.Maintain => Kumbu.Core.Notes.AccessLevel.Maintain,
        MemberRole.Write => Kumbu.Core.Notes.AccessLevel.Write,
        _ => Kumbu.Core.Notes.AccessLevel.Read,
    };

    /// <summary>
    /// "Last use on this vault" of a member's credential (planner's decision 9, collab design §8.6, §13 L5): the request
    /// that touches the vault through <paramref name="access"/> sets its derived slot's <c>usedAt</c>, at most once a
    /// minute, by one update that doesn't hold up the request; a failure is only logged. Opening a credential's vaults
    /// isn't a use of each: only the vault a request reads or writes (<see cref="EncryptedNoteStore"/>) is, so the owner
    /// learns nothing of the member's use of her own vaults.
    /// </summary>
    internal void Used(VaultAccess access)
    {
        if (access.DerivedSlot is not { } slot) return;
        var now = Now;
        if (slot.UsedAt is { } at && now - at < UsedGranularity) return;
        // Once per slot and request object: the store touches a vault on every call.
        slot.UsedAt = now;
        var f = Builders<SlotDocument>.Filter;
        _ = Task.Run(async () =>
        {
            try
            {
                await Slots.UpdateOneAsync(
                    f.Eq(s => s.Id, slot.Id) & (f.Eq(s => s.UsedAt, null) | f.Lt(s => s.UsedAt, now - UsedGranularity)), Builders<SlotDocument>.Update.Set(s => s.UsedAt, now));
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Recording the last use of slot {SlotId} failed", slot.Id);
            }
        });
    }

    private static readonly TimeSpan UsedGranularity = TimeSpan.FromMinutes(1);

    /// <summary>Whether <paramref name="slot"/>'s membership is among <paramref name="active"/>, and is the membership of
    /// <paramref name="userId"/> on the slot's vault: a slot pointed at someone else's membership opens nothing.</summary>
    private static bool IsActiveMembership(IReadOnlyDictionary<string, MembershipDocument>? active, SlotDocument slot, string userId) =>
        slot.Membership is { } id && active is not null && active.TryGetValue(id, out var m)
        && m.Vault == slot.Vault && m.Member == userId;
}

/// <summary>What an invitation shows of its vault: the owner's name for it, its embedding provider, and whether it is closing.</summary>
internal sealed record InviteVaultMeta(string Name, string EmbeddingProvider, bool Closing);

