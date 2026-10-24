// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Search;

/// <summary>
/// A piece of a note that search ranks: one section's own text (its heading up to the next heading of any
/// level), the text before the first heading, or a part of a long section.
/// </summary>
/// <param name="Address">The section's address (#A#B), or "" for the text before the first heading.</param>
/// <param name="StartLine">0-based, inclusive; <paramref name="EndLine"/> exclusive.</param>
/// <param name="Context">What the chunk is about beyond its text: the note's name and the heading path.</param>
public sealed record Chunk(VaultPath Path, string Address, int StartLine, int EndLine, string Text, string Context)
{
    /// <summary>What gets embedded: the context, then the text (concept §6: the heading path as context).</summary>
    public string EmbeddingText => Context + "\n" + Text;

    public (VaultPath, int) Key => (Path, StartLine);
}

public static class Chunker
{
    /// <summary>About 512 tokens (concept §6); a token is roughly 4 characters of prose.</summary>
    public const int MaxChars = 2000;

    public static List<Chunk> Split(Note note)
    {
        var lines = Lines.Split(note.Content);
        var headings = Outline.Parse(lines);
        var title = note.Path.Name.EndsWith(".md", StringComparison.Ordinal) ? note.Path.Name[..^3] : note.Path.Name;
        var chunks = new List<Chunk>();

        var first = Outline.FrontmatterEnd(lines);
        var bodyStart = headings.Count > 0 ? headings[0].Line : lines.Count;
        Add(chunks, note.Path, "", title, lines, first, bodyStart);
        for (var i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            var end = i + 1 < headings.Count ? headings[i + 1].Line : lines.Count;
            var context = title + " > " + string.Join(" > ", heading.Chain().Reverse().Select(h => h.Text));
            Add(chunks, note.Path, heading.Address, context, lines, heading.Line, end);
        }

        return chunks;
    }

    /// <summary>Adds the lines [start, end) as one chunk, or as several of at most <see cref="MaxChars"/>,
    /// split between paragraphs where possible.</summary>
    private static void Add(List<Chunk> chunks, VaultPath path, string address, string context, IReadOnlyList<string> lines, int start, int end)
    {
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
        while (start < end && string.IsNullOrWhiteSpace(lines[start])) start++;
        if (start >= end) return;

        var partStart = start;
        var size = 0;
        var lastBreak = -1;
        for (var i = start; i < end; i++)
        {
            if (size > 0 && size + lines[i].Length + 1 > MaxChars)
            {
                var cut = lastBreak > partStart ? lastBreak : i;
                Emit(chunks, path, address, context, lines, partStart, cut);
                partStart = cut;
                while (partStart < end && string.IsNullOrWhiteSpace(lines[partStart])) partStart++;
                size = lines.Skip(partStart).Take(i - partStart).Sum(l => l.Length + 1);
                lastBreak = -1;
            }

            if (string.IsNullOrWhiteSpace(lines[i])) lastBreak = i;
            size += lines[i].Length + 1;
        }

        if (partStart < end) Emit(chunks, path, address, context, lines, partStart, end);
    }

    private static void Emit(List<Chunk> chunks, VaultPath path, string address, string context, IReadOnlyList<string> lines, int start, int end)
    {
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
        if (start >= end) return;
        var text = string.Join('\n', lines.Skip(start).Take(end - start));
        // A single line longer than the limit is cut; search still finds its start.
        if (text.Length > MaxChars * 2) text = text[..(MaxChars * 2)];
        chunks.Add(new Chunk(path, address, start, end, text, context));
    }
}
