// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>
/// What makes up a vault's structure (collab design §3.3): the files and commands only those who may maintain the
/// vault can change. One list, so further structural files (the OKF manifests) are one line each.
/// </summary>
public static class StructuralFiles
{
    /// <summary>The structural files of <paramref name="vault"/>.</summary>
    public static IEnumerable<VaultPath> Of(string vault) => [BoardPaths.Definition(vault)];

    /// <summary>The commands that change structure whatever path they name.</summary>
    public static readonly IReadOnlySet<string> StructuralCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "mkcp", "rmcp", "board create", "board rename_column",
    };

    public static bool IsStructural(VaultPath path) => path.Vault is { } vault && Of(vault).Contains(path);

    /// <summary>Whether a structural file lies below <paramref name="folder"/> (a move or removal of the folder takes it along).</summary>
    public static bool ContainsStructural(VaultPath folder) => folder.Vault is { } vault && Of(vault).Any(p => Checkpoint.IsBelow(p, folder));
}
