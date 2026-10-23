// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>What to tell an agent about a note whose stored document doesn't open.</summary>
public static class Damage
{
    public static string Message(string tool, VaultPath path) =>
        $"{tool}: {path.Value}: damaged: the stored note doesn't open (changed or damaged outside Kumbu²; the server log has details). "
        + "Its path stays taken until the document is repaired or removed in the database";

    public static string Summary(int count) =>
        $"({count} {(count == 1 ? "note here doesn't" : "notes here don't")} open: changed or damaged outside Kumbu²; the server log has details)";
}
