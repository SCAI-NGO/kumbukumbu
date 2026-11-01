// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Notes;

/// <summary>What a caller may do in a vault (collab design §3.3), ordered: each level includes the ones below it.
/// <see cref="Maintain"/> may change a vault's structure (the board's columns, checkpoints); <see cref="Owner"/> is the
/// owner's session and the dev token.</summary>
public enum AccessLevel
{
    Read,
    Write,
    Maintain,
    Owner,
}
