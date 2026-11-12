// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Links;

/// <summary>The other names a note goes by: the top-level <c>aliases</c> of its front matter, a list or a single text
/// (spec §2). Each is trimmed and NFC; empty values and those that can't be a link's name are skipped, duplicates
/// (ignoring case) count once. Front matter that doesn't parse means no aliases.</summary>
public static class NoteAliases
{
    private static readonly char[] Forbidden = ['/', '#', '|', '[', ']', '^', '\n', '\r'];

    /// <summary>Aliases a note can have; the rest are ignored. Every link view builds an index of the vault's aliases
    /// and every unlinked-mentions search looks for each, so a note listing thousands could stall them.</summary>
    internal const int MaxPerNote = 100;

    /// <summary>Front matter longer than this (in characters) is not parsed for aliases: names don't need it, and the
    /// YAML reader builds the whole document before its budget can stop it.</summary>
    internal const int MaxFrontmatterLength = 64 * 1024;

    public static IReadOnlyList<string> Of(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal)) return [];
        var lines = Lines.Split(content);
        var end = Outline.FrontmatterEnd(lines);
        // Most notes have no aliases; skip parsing their front matter.
        if (end == 0 || !lines.Take(end).Any(l => l.Contains("aliases", StringComparison.Ordinal))) return [];
        if (lines.Take(end).Sum(l => l.Length + 1) > MaxFrontmatterLength) return [];

        object? value;
        try
        {
            value = Frontmatter.Read(content).Values.GetValueOrDefault("aliases");
        }
        catch (Exception e) when (e is not (OutOfMemoryException or OperationCanceledException))
        {
            // Frontmatter.Read turns the failures it knows into a Problem; any other one still means no aliases, never an
            // exception reaching a request (spec §1).
            return [];
        }

        IEnumerable<string> texts = value switch
        {
            string one => [one],
            List<object?> list => list.OfType<string>(),
            _ => [],
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new List<string>();
        foreach (var text in texts)
        {
            var alias = text.Trim().Normalize(NormalizationForm.FormC);
            if (alias.Length == 0 || alias.IndexOfAny(Forbidden) >= 0) continue;
            if (seen.Add(alias)) aliases.Add(alias);
            if (aliases.Count == MaxPerNote) break;
        }

        return aliases;
    }

    /// <summary>The aliases of each of <paramref name="notes"/> that has any.</summary>
    public static IReadOnlyDictionary<VaultPath, IReadOnlyList<string>> Of(IEnumerable<Note> notes, CancellationToken ct = default)
    {
        var result = new Dictionary<VaultPath, IReadOnlyList<string>>();
        foreach (var note in notes)
        {
            ct.ThrowIfCancellationRequested();
            if (Of(note.Content) is { Count: > 0 } aliases) result[note.Path] = aliases;
        }

        return result;
    }
}
