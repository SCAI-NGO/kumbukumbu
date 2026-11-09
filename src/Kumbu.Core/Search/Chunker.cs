// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Search;

/// <summary>
/// A piece of a note that search ranks: a section's own text (its heading up to the next heading of any level), the
/// text before the first heading, short sections merged under the heading they share, or a part of a long section.
/// </summary>
/// <param name="Address">The section's address (#A#B), or "" for the text before the first heading; for merged
/// sections, the address of the deepest heading they share ("" for none).</param>
/// <param name="StartLine">0-based, inclusive; <paramref name="EndLine"/> exclusive: the lines the core covers. A
/// table part's repeated header rows lie before it.</param>
/// <param name="Context">What the chunk is about beyond its text: the note's name and the heading path.</param>
public sealed record Chunk(VaultPath Path, string Address, int StartLine, int EndLine, string Text, string Context)
{
    /// <summary>0, unless one line is split into pieces: then 0, 1, … in order, all on the same lines.</summary>
    public int Part { get; init; }

    /// <summary>What gets embedded: the context, then the text (concept §6: the heading path as context).</summary>
    public string EmbeddingText => Context + "\n" + Text;

    public (VaultPath, int, int) Key => (Path, StartLine, Part);
}

/// <summary>
/// Chunks a note by its Markdown structure (spec 2026-11-09-structural-chunking): sections, merged under their parent
/// while short, and split into whole blocks while long; tables between rows, with their header in every part.
/// Length is counted in <see cref="Words"/>, and the <see cref="ChunkBound"/> is a ceiling on every embedding text.
/// </summary>
public static class Chunker
{
    /// <summary>The version of the chunking, part of <see cref="ChunkBound.Scheme"/>.</summary>
    public const int SchemeVersion = 2;

    /// <summary>Short sections merge while they stay within this many words together.</summary>
    public const int TargetWords = 250;

    /// <summary>A core holds at most this many words.</summary>
    public const int MaxWords = 350;

    /// <summary>The words of a pre-roll or post-roll on each side, where the enclosing section is too long.</summary>
    public const int RollWords = 120;

    /// <summary>A chunk under this many words merges beyond <see cref="TargetWords"/> where the structure allows.</summary>
    public const int MinWords = 40;

    /// <summary>About 512 tokens (concept §6); a token is roughly 4 characters of prose. <see cref="Sections"/>' limit.</summary>
    public const int MaxChars = 2000;

    /// <summary>
    /// The chunks of each note's content as last split, by the content string itself: the server's notes keep their
    /// content strings while unchanged, so a search splits only what changed since the last. An entry goes with its string.
    /// </summary>
    private static readonly ConditionalWeakTable<string, Splitting> Splits = [];

    private sealed record Splitting(VaultPath Path, string Bound, Chunk[] Chunks);

    /// <summary>The note's chunks within <paramref name="bound"/>, in line order.</summary>
    public static List<Chunk> Split(Note note, ChunkBound bound)
    {
        if (Splits.TryGetValue(note.Content, out var known) && known.Path == note.Path && known.Bound == bound.Id) return [.. known.Chunks];
        var chunks = new ChunkSplitter(note, bound).Run();
        Splits.AddOrUpdate(note.Content, new Splitting(note.Path, bound.Id, [.. chunks]));
        return chunks;
    }

    /// <summary>Runs of Unicode letters and digits (with their combining marks): markup and punctuation weigh nothing.</summary>
    public static int Words(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var part = Rune.IsLetterOrDigit(rune)
                || (inWord && Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark);
            if (part && !inWord) count++;
            inWord = part;
        }

        return count;
    }

    /// <summary>
    /// The plain sections, for grouping matches by section (<c>rg</c>, the search in earlier versions): each section's
    /// own text and the text before the first heading, a section over <see cref="MaxChars"/> in parts.
    /// </summary>
    public static List<Chunk> Sections(Note note)
    {
        var lines = Lines.Split(note.Content);
        var headings = Outline.Parse(lines);
        var title = Title(note.Path);
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

    internal static string Title(VaultPath path) => path.Name.EndsWith(".md", StringComparison.Ordinal) ? path.Name[..^3] : path.Name;

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
