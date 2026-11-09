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

    /// <summary>For a table part whose <see cref="Text"/> starts with the table's header and separator rows: the line of
    /// the header row (before <see cref="StartLine"/>); -1 otherwise.</summary>
    public int RepeatedHeader { get; init; } = -1;

    /// <summary>For a chunk that starts inside fenced code (a part of a long code block): the fence that opened the
    /// block, as in the note; null otherwise. Without it, a part's "# comment" lines would read as headings.</summary>
    public string? Fence { get; init; }

    /// <summary>What gets embedded: the context, then the text (concept §6: the heading path as context).</summary>
    public string EmbeddingText => Context + "\n" + Text;

    public (VaultPath, int, int) Key => (Path, StartLine, Part);
}

/// <summary>
/// A piece of a PDF's page that search ranks (spec 2026-11-09-structural-chunking §3.6): whole lines of the page, or
/// a piece of a line too long for a chunk.
/// </summary>
/// <param name="Part">0, 1, … in order on the page.</param>
/// <param name="Core">What is embedded, after the context; blank lines trimmed at both ends.</param>
/// <param name="PreRoll">The whole lines before the core on the page, up to <see cref="Chunker.RollWords"/> words and
/// <see cref="Chunker.RollChars"/> characters: context for showing a hit, never embedded. "" at the start of the page.</param>
/// <param name="PostRoll">Likewise the lines after the core; "" at the end of the page.</param>
public sealed record PageChunk(int Part, string Core, string PreRoll, string PostRoll)
{
    /// <summary>"{PDF's name} > p{page}", shortened as a note's context is if the name is very long.</summary>
    public string Context { get; init; } = "";

    /// <summary>What gets embedded: the context, then the core.</summary>
    public string EmbeddingText => Context + "\n" + Core;
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

    /// <summary>A roll holds at most this many characters on each side.</summary>
    public const int RollChars = 1_000;

    /// <summary>The limit of <see cref="Sections"/> only (<c>rg</c>, <see cref="EarlierSearch"/>): about 512 tokens at
    /// roughly 4 characters of prose. The chunks that are embedded are bounded by their <see cref="ChunkBound"/>.</summary>
    public const int MaxChars = 2000;

    /// <summary>
    /// The chunks of each note's content as last split, by the content string itself: the server's notes keep their
    /// content strings while unchanged, so a search splits only what changed since the last. An entry goes with its string.
    /// </summary>
    private static readonly ConditionalWeakTable<string, Splitting> Splits = [];

    private sealed record Splitting(VaultPath Path, string Bound, Chunk[] Chunks);

    /// <summary>Whether <see cref="Split"/> has the chunks of this very content string at hand for the path and bound.</summary>
    public static bool IsSplit(Note note, ChunkBound bound) =>
        Splits.TryGetValue(note.Content, out var known) && known.Path == note.Path && known.Bound == bound.Id;

    /// <summary>The note's chunks within <paramref name="bound"/>, in line order.</summary>
    public static List<Chunk> Split(Note note, ChunkBound bound)
    {
        if (Splits.TryGetValue(note.Content, out var known) && known.Path == note.Path && known.Bound == bound.Id) return [.. known.Chunks];
        var chunks = new ChunkSplitter(note, bound).Run();
        Splits.AddOrUpdate(note.Content, new Splitting(note.Path, bound.Id, [.. chunks]));
        return chunks;
    }

    /// <summary>A PDF's name without ".pdf": what its pages' context starts with.</summary>
    public static string PdfTitle(VaultPath pdf) => pdf.Name[..^".pdf".Length];

    /// <summary>
    /// A page of a PDF in chunks, in order; none if it has no text. A page has no Markdown structure: its lines are
    /// packed greedily within <see cref="MaxWords"/> and the bound, and a chunk that would take the next line ends at
    /// the last blank line in its second half, else at that line. A line that doesn't fit alone is split into pieces.
    /// </summary>
    public static IReadOnlyList<PageChunk> SplitPage(string pdfTitle, int page, string text, ChunkBound bound)
    {
        var lines = Lines.Split(text);
        var (context, contextCost) = ChunkSplitter.Shortened($"{pdfTitle} > p{page}", bound);
        var words = lines.Select(Words).ToArray();
        var costs = lines.Select(bound.Cost).ToArray();
        var cores = new List<(int Start, int End, string Text)>();
        for (var start = 0; start < lines.Count;)
        {
            if (string.IsNullOrWhiteSpace(lines[start]))
            {
                start++;
                continue;
            }

            var (end, cost, count) = (start, contextCost, 0);
            while (end < lines.Count && count + words[end] <= MaxWords && cost + costs[end] <= bound.Budget)
            {
                cost += costs[end];
                count += words[end];
                end++;
            }

            if (end == start)
            {
                foreach (var piece in ChunkSplitter.Pieces(lines[start], (context, contextCost), bound)) cores.Add((start, start + 1, piece));
                start++;
                continue;
            }

            if (end < lines.Count)
            {
                var blank = end - 1;
                while (blank > start && !string.IsNullOrWhiteSpace(lines[blank])) blank--;
                if (blank > start && 2 * words[start..blank].Sum() >= count) end = blank;
            }

            end = ChunkSplitter.Trimmed(lines, start, end).End;
            cores.Add((start, end, string.Join('\n', lines.Skip(start).Take(end - start))));
            start = end;
        }

        return [.. cores.Select((c, i) => new PageChunk(i, c.Text, Roll(c.Start - 1, -1), Roll(c.End, 1)) { Context = context })];

        // Whole lines from the one at `from` on in `step`'s direction, while they stay within the roll's budget.
        string Roll(int from, int step)
        {
            var (count, chars, taken) = (0, -1, 0);
            for (var i = from; i >= 0 && i < lines.Count; i += step)
            {
                if (count + words[i] > RollWords || chars + 1 + lines[i].Length > RollChars) break;
                (count, chars, taken) = (count + words[i], chars + 1 + lines[i].Length, taken + 1);
            }

            var (first, end) = step > 0 ? (from, from + taken) : (from - taken + 1, from + 1);
            (first, end) = ChunkSplitter.Trimmed(lines, first, end);
            return string.Join('\n', lines.Skip(first).Take(end - first));
        }
    }

    /// <summary>
    /// The text around a chunk of <paramref name="note"/>, for showing a hit and never embedded (spec §3.4): the rest of
    /// the enclosing section (the parent of the chunk's address heading; the note for none) before and after the core,
    /// each side whole if within <see cref="RollWords"/> and <see cref="RollChars"/>, else the nearest whole blocks, then
    /// lines, within them. A table part's repeated header rows are left out; blank lines trimmed at both ends.
    /// </summary>
    public static (string Before, string After) Rolls(Note note, Chunk chunk)
    {
        var lines = Lines.Split(note.Content);
        var code = Outline.CodeLines(lines);
        var parent = chunk.Address.Length == 0 ? null : Outline.Parse(lines).FirstOrDefault(h => h.Address == chunk.Address)?.Parent;
        var (start, end) = parent is null ? (Outline.FrontmatterEnd(lines), lines.Count) : (parent.Line, parent.End);
        (start, end) = (Math.Min(start, chunk.StartLine), Math.Max(end, chunk.EndLine));

        var header = chunk.RepeatedHeader;
        var from = chunk.StartLine;
        foreach (var block in Blocks.Parse(lines, start, chunk.StartLine, code).Reverse())
        {
            if (Fits(block.Start, chunk.StartLine))
            {
                from = block.Start;
                continue;
            }

            for (var i = block.End - 1; i >= block.Start && Fits(i, chunk.StartLine); i--) from = i;
            break;
        }

        var to = chunk.EndLine;
        foreach (var block in Blocks.Parse(lines, chunk.EndLine, end, code))
        {
            if (Fits(chunk.EndLine, block.End))
            {
                to = block.End;
                continue;
            }

            for (var i = block.Start + 1; i <= block.End && Fits(chunk.EndLine, i); i++) to = i;
            break;
        }

        return (Text(from, chunk.StartLine), Text(chunk.EndLine, to));

        bool Fits(int first, int last) => Text(first, last) is var text && Words(text) <= RollWords && text.Length <= RollChars;

        string Text(int first, int last)
        {
            (first, last) = ChunkSplitter.Trimmed(lines, first, last);
            var kept = Enumerable.Range(first, last - first).Where(i => header < 0 || (i != header && i != header + 1));
            return string.Join('\n', kept.Select(i => lines[i]));
        }
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
