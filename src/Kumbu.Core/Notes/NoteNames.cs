// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>Which paths Kumbu² creates notes at (concept §4: it never creates names Windows can't store).</summary>
public static class NoteNames
{
    private static readonly HashSet<string> Reserved = new(
        ["CON", "PRN", "AUX", "NUL",
         .. Enumerable.Range(1, 9).Select(i => $"COM{i}"), .. Enumerable.Range(1, 9).Select(i => $"LPT{i}")],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns why a note can't be created at the path, or null if it can.</summary>
    public static string? Problem(VaultPath path)
    {
        if (path.Segments.Count < 2) return "notes live inside a vault, e.g. /Work/plan.md";
        if (!path.IsNote) return "note names end in .md";
        return NameProblem(path);
    }

    /// <summary>Why nothing, note or attachment, can be created at the path because of a name in it; null if
    /// the names are fine.</summary>
    public static string? NameProblem(VaultPath path)
    {
        foreach (var segment in path.Segments)
        {
            if (segment.StartsWith('.')) return $"'{segment}' starts with '.', which hides it in Obsidian";
            foreach (var c in segment)
            {
                if (c < ' ') return $"'{segment}' contains a control character";
                if ("<>:\"\\|?*".Contains(c)) return $"'{segment}' contains '{c}', which Windows can't store";
            }

            if (segment.EndsWith('.') || segment.EndsWith(' ')) return $"'{segment}' ends with a dot or space, which Windows can't store";
            if (Reserved.Contains(segment.Split('.')[0])) return $"'{segment}' is a reserved name on Windows";
        }

        return null;
    }
}
