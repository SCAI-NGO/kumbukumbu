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

    /// <summary>
    /// A member's slot on a shared vault, not stored here. Until members have keypairs (phase 2) it wraps the vault key
    /// as a user slot does, under the member's user key with the AAD of its own kind, so a rotation of her user key
    /// wraps it again unchanged. Under her own lookup, with the alias's hash under her own name key: the index on
    /// <c>(lookup, nameId)</c> keeps aliases and own vault names apart in both directions. The alias itself is sealed
    /// under a random alias key, bound to the vault and the membership, and the alias key under a key from her user
    /// key; the owner can read neither. Her keys and connections on the vault get the alias key, so a renamed alias
    /// reaches them too (<see cref="RenameAliasAsync"/>).
    /// </summary>
    internal SlotDocument MemberSlot(string vaultId, string membershipId, string memberId, byte[] memberKey, byte[] vaultKey, string alias)
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
            Wrapped = VaultCrypto.Seal(UserKek(memberKey, vaultId), vaultKey, SlotContext(vaultId, SlotDocument.Member)),
            CreatedAt = Now,
        };
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
    /// "Last use on this vault" of derived slots (planner's decision 9): <c>usedAt</c>, set at most once a minute per
    /// slot, by one update that doesn't hold up the request; a failure is only logged.
    /// </summary>
    private void TouchUsed(IEnumerable<SlotDocument> slots)
    {
        var now = Now;
        var stale = slots.Where(s => s.UsedAt is not { } at || now - at >= UsedGranularity).Select(s => s.Id).ToList();
        if (stale.Count == 0) return;
        var f = Builders<SlotDocument>.Filter;
        _ = Task.Run(async () =>
        {
            try
            {
                await Slots.UpdateManyAsync(
                    f.In(s => s.Id, stale) & (f.Eq(s => s.UsedAt, null) | f.Lt(s => s.UsedAt, now - UsedGranularity)), Builders<SlotDocument>.Update.Set(s => s.UsedAt, now));
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Recording the last use of {Count} slots failed", stale.Count);
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
