// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Sharing;

namespace Kumbu.Server.Vaults;

/// <summary>
/// A vault as one caller has it on one request (collab design §3.2): the unlocked vault, which every caller shares, and
/// what is the caller's own: the name it sees the vault under (the owner's name, or a member's alias), its level, and
/// the membership it came through (null for the owner and the dev token). Built by the opening methods of
/// <see cref="VaultCatalog"/>, never cached across requests.
/// </summary>
public sealed record VaultAccess(UnlockedVault Vault, string Name, AccessLevel Level, MembershipDocument? Membership, bool IsOwner)
{
    public string Id => Vault.Id;

    /// <summary>The vault is seen under another name than its own: its paths are translated in both directions.</summary>
    public bool Renamed => !string.Equals(Name, Vault.Name, StringComparison.Ordinal);

    /// <summary>A path in the caller's view (under <see cref="Name"/>) as the vault stores it (under its own name).</summary>
    public VaultPath ToVault(VaultPath path) => Renamed ? path.WithVault(Vault.Name) : path;

    /// <summary>A path as the vault stores it, in the caller's view.</summary>
    public VaultPath ToCaller(VaultPath path) => Renamed ? path.WithVault(Name) : path;

    /// <summary>The owner's access, or the dev token's, to a vault under its own name.</summary>
    public static VaultAccess Owned(UnlockedVault vault) => new(vault, vault.Name, AccessLevel.Owner, null, IsOwner: true);
}
