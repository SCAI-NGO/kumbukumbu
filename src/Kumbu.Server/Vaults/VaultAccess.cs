// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Sharing;

namespace Kumbu.Server.Vaults;

/// <summary>
/// A vault as one caller has it on one request (collab design §3.2): the unlocked vault, which every caller shares, and
/// what is the caller's own: the name it sees the vault under (for the owner its <see cref="UnlockedVault.Shown"/> name, for a
/// member her alias), its level, and
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

    /// <summary>The caller may publish this vault's notes (publish design §3.8): the owner's session, or her key or
    /// connection given "May publish" here. Never a member, nor the dev token.</summary>
    public bool MayPublish { get; init; }

    /// <summary>The vault is opened through a WebDAV share, which never publishes: what publishing refuses it says so,
    /// rather than send it to a right no share can be given.</summary>
    public bool ViaShare { get; init; }

    /// <summary>The refusal's sentence for <see cref="ReadOnlyCode"/>, the vault named as the caller sees it.</summary>
    public static string ReadOnlySentence(string code, string vault) => code switch
    {
        Refusals.OwnerInactive => $"vault {vault} is read-only while its owner's account is deactivated",
        Refusals.Closing => $"vault {vault} is read-only: its owner deleted their account, and it is closing",
        _ => $"vault {vault} is read-only",
    };

    /// <summary>The caller's own vault marked as the skills vault (<see cref="VaultKinds.Skills"/>).</summary>
    public bool IsSkillsVault => IsOwner && Membership is null && Vault.Kind == VaultKinds.Skills;

    /// <summary>The vault is seen under another name than its stored one (a member's alias, or the name its owner renamed it
    /// to): its paths are translated in both directions.</summary>
    public bool Renamed => !string.Equals(Name, Vault.Name, StringComparison.Ordinal);

    /// <summary>A path in the caller's view (under <see cref="Name"/>) as the vault stores it (under its own name).</summary>
    public VaultPath ToVault(VaultPath path) => Renamed ? path.WithVault(Vault.Name) : path;

    /// <summary>A path as the vault stores it, in the caller's view.</summary>
    public VaultPath ToCaller(VaultPath path) => Renamed ? path.WithVault(Name) : path;

    /// <summary>The owner's access, or the dev token's, to a vault under the name it is shown as: once renamed, its paths
    /// are translated as a member's are.</summary>
    public static VaultAccess Owned(UnlockedVault vault) => new(vault, vault.Shown, AccessLevel.Owner, null, IsOwner: true);
}
