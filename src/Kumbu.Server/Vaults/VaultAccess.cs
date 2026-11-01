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

    /// <summary>A member's writer id on the vault for this caller (her sessions', or this credential's), from the
    /// membership's sealed <c>writerIds</c>: all her writes there carry, in place of a name (collab design §8.1). Null for
    /// the owner, and for a member whose writer id didn't open, whose writes are then refused.</summary>
    public string? WriterId { get; init; }

    /// <summary>The credential's slot on the vault when it was derived through a membership, with its <c>usedAt</c> as
    /// read: the request that touches the vault records "last use on this vault" on it (planner's decision 9).</summary>
    public SlotDocument? DerivedSlot { get; init; }

    /// <summary>Why the vault is read-only for this caller whatever her role or credential (a <see cref="Refusals"/> code:
    /// <c>owner-inactive</c>, <c>closing</c>), or null. Her <see cref="Level"/> is <c>Read</c> then, and every write is refused with this
    /// code (<see cref="VaultCatalog.CheckWritable(UnlockedVault, VaultAccess)"/>).</summary>
    public string? ReadOnlyCode { get; init; }

    /// <summary>The refusal's sentence for <see cref="ReadOnlyCode"/>, the vault named as the caller sees it.</summary>
    public static string ReadOnlySentence(string code, string vault) => code switch
    {
        Refusals.OwnerInactive => $"vault {vault} is read-only while its owner's account is deactivated",
        Refusals.Closing => $"vault {vault} is read-only: its owner deleted their account, and it is closing",
        _ => $"vault {vault} is read-only",
    };

    /// <summary>The vault is seen under another name than its own: its paths are translated in both directions.</summary>
    public bool Renamed => !string.Equals(Name, Vault.Name, StringComparison.Ordinal);

    /// <summary>A path in the caller's view (under <see cref="Name"/>) as the vault stores it (under its own name).</summary>
    public VaultPath ToVault(VaultPath path) => Renamed ? path.WithVault(Vault.Name) : path;

    /// <summary>A path as the vault stores it, in the caller's view.</summary>
    public VaultPath ToCaller(VaultPath path) => Renamed ? path.WithVault(Name) : path;

    /// <summary>The owner's access, or the dev token's, to a vault under its own name.</summary>
    public static VaultAccess Owned(UnlockedVault vault) => new(vault, vault.Name, AccessLevel.Owner, null, IsOwner: true);
}
