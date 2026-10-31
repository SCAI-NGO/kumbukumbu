// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Crypto;

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
    internal async Task<byte[]> OwnerVaultKeyAsync(string ownerId, byte[] ownerKey, string vaultId, CancellationToken ct) =>
        (await UserVaultKeysAsync(ownerId, ownerKey, [vaultId], "A share", ct))[vaultId];
}
