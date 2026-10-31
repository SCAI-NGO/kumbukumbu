// SPDX-License-Identifier: AGPL-3.0-or-later
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
    /// under a key from her user key, bound to the vault and the membership; the owner can't read it.
    /// </summary>
    internal SlotDocument MemberSlot(string vaultId, string membershipId, string memberId, byte[] memberKey, byte[] vaultKey, string alias) => new()
    {
        Vault = vaultId,
        Kind = SlotDocument.Member,
        Lookup = UserLookup(memberKey, memberId),
        NameId = VaultCrypto.Mac(VaultCrypto.Derive(memberKey, VaultNameLabel, []), NormaliseVaultName(alias)),
        Alias = VaultCrypto.Seal(AliasKey(memberKey, vaultId), Encoding.UTF8.GetBytes(alias), AliasContext(vaultId, membershipId)),
        Membership = membershipId,
        Wrapped = VaultCrypto.Seal(UserKek(memberKey, vaultId), vaultKey, SlotContext(vaultId, SlotDocument.Member)),
        CreatedAt = Now,
    };

    /// <summary>The alias a member slot holds, opened with the member's user key.</summary>
    internal static string OpenAlias(byte[] memberKey, SlotDocument slot) =>
        Encoding.UTF8.GetString(VaultCrypto.Open(AliasKey(memberKey, slot.Vault), slot.Alias ?? [], AliasContext(slot.Vault, slot.Membership ?? "")));

    private static byte[] AliasKey(byte[] memberKey, string vaultId) => VaultCrypto.Derive(memberKey, AliasLabel, Encoding.UTF8.GetBytes(vaultId));

    private static string AliasContext(string vaultId, string membershipId) => $"alias:{vaultId}:{membershipId}";

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
            .Project<MembershipDocument>(Builders<MembershipDocument>.Projection.Include(m => m.Vault).Include(m => m.Member).Include(m => m.Role))
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

    /// <summary>A member's session on a shared vault: under her alias, at her role (collab design §3.3).</summary>
    private static VaultAccess MemberAccess(UnlockedVault vault, string alias, MembershipDocument membership) =>
        new(vault, alias, LevelOf(membership), membership, IsOwner: false);

    /// <summary>A key's or a connection's access: the owner's, under the vault's name at its sealed level; a member's,
    /// under her alias sealed with it, at most at her role.</summary>
    private static VaultAccess CredentialAccess(UnlockedVault vault, Accounts.KeyRight right, MembershipDocument? membership, string? alias) =>
        membership is null
            ? new(vault, vault.Name, Accounts.KeyRights.ToLevel(right), null, IsOwner: true)
            : new(vault, alias!, Accounts.KeyRights.ToLevel(Capped(right, membership)), membership, IsOwner: false);

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
