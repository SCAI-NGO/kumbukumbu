// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Sections;

/// <summary>A note split into lines, with the section an address points to.</summary>
public sealed record SectionMatch(IReadOnlyList<string> Lines, Heading Heading)
{
    /// <summary>The section as <c>cat</c> shows it: heading line to last non-blank line.</summary>
    public string Text => Sh.Lines.Join(Lines.Skip(Heading.Line).Take(Heading.ContentEnd - Heading.Line));

    /// <summary>Character offset of a line's start in the note. After the last line it is the end of that
    /// line, before the note's final line break, so an edit there keeps the note ending with one.</summary>
    public int Offset(int line, string content)
    {
        if (line >= Lines.Count) return content.EndsWith('\n') ? content.Length - 1 : content.Length;
        var offset = 0;
        for (var i = 0; i < line && i < Lines.Count; i++) offset += Lines[i].Length + 1;
        return Math.Min(offset, content.Length);
    }
}

public static class SectionLookup
{
    /// <summary>Finds the section; the error has no command prefix, e.g. "/a.md#X: no such section (…)".</summary>
    public static bool TryFind(
        VaultPath path, string content, string fragment,
        [NotNullWhen(true)] out SectionMatch? match, [NotNullWhen(false)] out string? error)
    {
        match = null;
        var shown = $"{path.Value}#{fragment}";
        if (!SectionAddress.TryParse(fragment, out var segments))
        {
            error = $"{shown}: not a section address; use note.md#Heading or note.md#Parent#Child";
            return false;
        }

        var lines = Lines.Split(content);
        var found = SectionAddress.Resolve(Outline.Parse(lines), segments);
        switch (found.Count)
        {
            case 0:
                error = $"{shown}: no such section ('outline {Quote(path)}' lists them)";
                return false;
            case 1:
                match = new SectionMatch(lines, found[0]);
                error = null;
                return true;
            default:
                error = $"{shown}: ambiguous, it matches {string.Join(", ", found.Select(h => h.Address))}";
                return false;
        }
    }

    /// <summary>A path as it would be typed in sh: quoted if it contains a space.</summary>
    public static string Quote(VaultPath path) => path.Value.Contains(' ') ? $"\"{path.Value}\"" : path.Value;
}
