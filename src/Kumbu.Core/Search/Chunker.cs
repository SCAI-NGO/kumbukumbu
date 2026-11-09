// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
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

    /// <summary>The note's chunks within <paramref name="bound"/>, in line order.</summary>
    public static List<Chunk> Split(Note note, ChunkBound bound) => new Splitter(note, bound).Run();

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

    private static string Title(VaultPath path) => path.Name.EndsWith(".md", StringComparison.Ordinal) ? path.Name[..^3] : path.Name;

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

    /// <summary>A section: the text before the first heading (<see cref="Heading"/> null, the note itself) or a
    /// heading's own lines, blank lines trimmed at both ends.</summary>
    private sealed record Section(Heading? Heading, int Start, int End, bool HasChildren);

    /// <summary>One note's chunking. A candidate's cost is the context's plus every line's: with arctic-m the costs of
    /// lines add up to the text's tokens exactly, and the fallback's to its characters.</summary>
    private sealed class Splitter
    {
        private readonly Note _note;
        private readonly ChunkBound _bound;
        private readonly IReadOnlyList<string> _lines;
        private readonly bool[] _code;
        private readonly string _title;

        // Prefix sums over the lines: [i] is the sum of the lines before line i.
        private readonly int[] _words;
        private readonly int[] _costs;
        private readonly Dictionary<Heading, (string Text, int Cost)> _contexts = new(ReferenceEqualityComparer.Instance);
        private (string Text, int Cost)? _noteContext;
        private readonly List<Chunk> _chunks = [];

        // The part being packed while a long section is split: its lines, and the header rows it repeats (or -1).
        private string _address = "";
        private (string Text, int Cost) _context;
        private int _start = -1, _end, _header = -1;

        public Splitter(Note note, ChunkBound bound)
        {
            (_note, _bound) = (note, bound);
            _lines = Lines.Split(note.Content);
            _code = Outline.CodeLines(_lines);
            _title = Title(note.Path);
            _words = new int[_lines.Count + 1];
            _costs = new int[_lines.Count + 1];
            for (var i = 0; i < _lines.Count; i++)
            {
                _words[i + 1] = _words[i] + Words(_lines[i]);
                _costs[i + 1] = _costs[i] + bound.Cost(_lines[i]);
            }
        }

        public List<Chunk> Run()
        {
            var sections = SectionsOf();
            var group = new List<Section>();
            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (!Whole(section))
                {
                    Flush(group);
                    SplitSection(section);
                    continue;
                }

                if (group.Count > 0 && !section.HasChildren && Joins(group, section, i + 1 < sections.Count ? sections[i + 1] : null))
                {
                    group.Add(section);
                    continue;
                }

                Flush(group);
                group.Add(section);
            }

            Flush(group);
            return _chunks;
        }

        private List<Section> SectionsOf()
        {
            var headings = Outline.Parse(_lines);
            var sections = new List<Section>(headings.Count + 1);
            var bodyStart = headings.Count > 0 ? headings[0].Line : _lines.Count;
            if (Trimmed(Outline.FrontmatterEnd(_lines), bodyStart) is var (start, end) && start < end) sections.Add(new Section(null, start, end, headings.Count > 0));
            for (var i = 0; i < headings.Count; i++)
            {
                var next = i + 1 < headings.Count ? headings[i + 1].Line : _lines.Count;
                var (_, last) = Trimmed(headings[i].Line, next);
                sections.Add(new Section(headings[i], headings[i].Line, last, i + 1 < headings.Count && ReferenceEquals(headings[i + 1].Parent, headings[i])));
            }

            return sections;
        }

        private (int Start, int End) Trimmed(int start, int end)
        {
            while (end > start && string.IsNullOrWhiteSpace(_lines[end - 1])) end--;
            while (start < end && string.IsNullOrWhiteSpace(_lines[start])) start++;
            return (start, end);
        }

        private int WordsOf(int start, int end) => _words[end] - _words[start];

        private int CostOf(int start, int end) => _costs[end] - _costs[start];

        private bool Within(int context, int cost, int words) => words <= MaxWords && context + cost <= _bound.Budget;

        private bool Whole(Section section) => Within(Context(section.Heading).Cost, CostOf(section.Start, section.End), WordsOf(section.Start, section.End));

        // ---- Merging whole sections ----

        /// <summary>Whether <paramref name="next"/> joins <paramref name="group"/>: within the target (or the maximum, to take
        /// in a chunk under <see cref="MinWords"/> that would otherwise stand alone), within the budget, and below one parent.</summary>
        private bool Joins(List<Section> group, Section next, Section? after)
        {
            if (Common([.. group, next]) is not (true, var shared)) return false;
            var first = group[0];
            var words = WordsOf(first.Start, next.End);
            if (!Within(Context(shared).Cost, CostOf(first.Start, next.End), words)) return false;
            if (words <= TargetWords) return true;
            var groupWords = WordsOf(first.Start, group[^1].End);
            var nextWords = WordsOf(next.Start, next.End);
            return groupWords < MinWords || (nextWords < MinWords && !TakesIn(next, after));
        }

        /// <summary>Whether a group that starts with the small <paramref name="section"/> would take in <paramref name="after"/>.</summary>
        private bool TakesIn(Section section, Section? after) =>
            after is not null && !after.HasChildren && Whole(after) && Common([section, after]) is (true, var shared)
            && Within(Context(shared).Cost, CostOf(section.Start, after.End), WordsOf(section.Start, after.End));

        /// <summary>
        /// The deepest heading that is an ancestor-or-self of every section (null for the note itself), and whether the
        /// sections may form a chunk under it: every one after the first is its child, and the first is it or its child.
        /// </summary>
        private static (bool Allowed, Heading? Shared) Common(List<Section> sections)
        {
            var shared = sections[0].Heading;
            foreach (var section in sections.Skip(1)) shared = Ancestor(shared, section.Heading);
            var first = sections[0];
            var allowed = (first.Heading is null ? shared is null : ReferenceEquals(first.Heading, shared) || ReferenceEquals(first.Heading.Parent, shared))
                && sections.Skip(1).All(s => s.Heading is not null && ReferenceEquals(s.Heading.Parent, shared));
            return (allowed, shared);
        }

        private static Heading? Ancestor(Heading? a, Heading? b)
        {
            if (a is null || b is null) return null;
            var chain = a.Chain().ToHashSet(ReferenceEqualityComparer.Instance);
            return b.Chain().FirstOrDefault(chain.Contains);
        }

        private void Flush(List<Section> group)
        {
            if (group.Count == 0) return;
            var shared = Common(group).Shared;
            var context = Context(shared);
            _chunks.Add(new Chunk(_note.Path, shared?.Address ?? "", group[0].Start, group[^1].End, Text(-1, group[0].Start, group[^1].End), context.Text));
            group.Clear();
        }

        /// <summary>The title and the heading path; its leading words only, if it would take more than a quarter of the budget.</summary>
        private (string Text, int Cost) Context(Heading? heading)
        {
            if (heading is null) return _noteContext ??= Shortened(_title);
            if (_contexts.TryGetValue(heading, out var known)) return known;
            return _contexts[heading] = Shortened(_title + " > " + string.Join(" > ", heading.Chain().Reverse().Select(h => h.Text)));
        }

        private (string Text, int Cost) Shortened(string context)
        {
            var limit = _bound.Budget / 4;
            var cost = _bound.Cost(context);
            if (cost <= limit) return (context, cost);

            // The costs of space-separated pieces add up to the whole's, for arctic-m and the fallback alike.
            var kept = new List<string>();
            var sum = 0;
            foreach (var word in context.Split(' '))
            {
                var next = _bound.Cost(word);
                if (sum + next > limit) break;
                kept.Add(word);
                sum += next;
            }

            var text = kept.Count > 0 ? string.Join(' ', kept) : context[..Prefix(context, limit, "")];
            return (text, _bound.Cost(text));
        }

        // ---- Splitting a long section into parts ----

        private void SplitSection(Section section)
        {
            _address = section.Heading?.Address ?? "";
            _context = Context(section.Heading);
            foreach (var block in Blocks.Parse(_lines, section.Start, section.End, _code)) Feed(block);
            Close();
        }

        private void Feed(Block block)
        {
            if (TryAppend(block.End)) return;
            // A block that fits a part of its own stays whole, a table too.
            if (Fits(-1, block.Start, block.End))
            {
                TryOpen(-1, block.Start, block.End);
                return;
            }

            if (block.Kind == BlockKind.Table)
            {
                FeedTable(block);
                return;
            }

            Close();
            if (block.Kind == BlockKind.List)
            {
                var items = Blocks.Items(_lines, block.Start, block.End).Append(block.End).ToList();
                for (var i = 0; i + 1 < items.Count; i++) FeedUnit(items[i], items[i + 1], -1);
            }
            else
            {
                for (var i = block.Start; i < block.End; i++) FeedUnit(i, i + 1, -1);
            }
        }

        /// <summary>A table too long for the part: its first part starts with the header rows and the first row, the
        /// others repeat the header rows before their own, unless the header leaves no room for a row.</summary>
        private void FeedTable(Block table)
        {
            var header = table.Start;
            var rows = header + 2;
            var repeat = -1;
            if (rows < table.End)
            {
                var widest = Enumerable.Range(rows, table.End - rows)
                    .Select(r => (Cost: CostOf(r, r + 1), Words: WordsOf(r, r + 1)))
                    .Where(r => Within(_context.Cost, r.Cost, r.Words))
                    .DefaultIfEmpty()
                    .Aggregate((a, b) => (Math.Max(a.Cost, b.Cost), Math.Max(a.Words, b.Words)));
                if (Within(_context.Cost, CostOf(header, rows) + widest.Cost, WordsOf(header, rows) + widest.Words)) repeat = header;
            }

            var first = Math.Min(rows + 1, table.End);
            if (TryAppend(first) || TryOpen(-1, header, first))
            {
                rows = first;
            }
            else
            {
                FeedUnit(header, header + 1, -1);
                FeedUnit(header + 1, rows, -1);
            }

            for (var r = rows; r < table.End; r++) FeedUnit(r, r + 1, repeat);
        }

        /// <summary>Lines [start, end) of a block that didn't fit whole: into the part, a new one, or line by line.</summary>
        private void FeedUnit(int start, int end, int repeat)
        {
            if (_start < 0 && string.IsNullOrWhiteSpace(_lines[start]) && end == start + 1) return;
            if (TryAppend(end)) return;
            if (TryOpen(repeat, start, end) || (repeat >= 0 && TryOpen(-1, start, end))) return;
            if (end - start > 1)
            {
                for (var i = start; i < end; i++) FeedUnit(i, i + 1, repeat);
                return;
            }

            SplitLine(start);
        }

        private bool TryAppend(int end)
        {
            if (_start < 0 || !Fits(_header, _start, end)) return false;
            _end = end;
            return true;
        }

        /// <summary>Closes the part, and opens one with [start, end) if that fits.</summary>
        private bool TryOpen(int header, int start, int end)
        {
            Close();
            if (!Fits(header, start, end)) return false;
            (_header, _start, _end) = (header, start, end);
            return true;
        }

        private bool Fits(int header, int start, int end)
        {
            var (cost, words) = (CostOf(start, end), WordsOf(start, end));
            if (header >= 0) (cost, words) = (cost + CostOf(header, header + 2), words + WordsOf(header, header + 2));
            return Within(_context.Cost, cost, words);
        }

        private void Close()
        {
            if (_start < 0) return;
            var (start, end) = Trimmed(_start, _end);
            if (start < end) _chunks.Add(new Chunk(_note.Path, _address, start, end, Text(_header, start, end), _context.Text));
            (_start, _header) = (-1, -1);
        }

        private string Text(int header, int start, int end)
        {
            var text = new StringBuilder();
            if (header >= 0) text.Append(_lines[header]).Append('\n').Append(_lines[header + 1]).Append('\n');
            for (var i = start; i < end; i++)
            {
                if (i > start) text.Append('\n');
                text.Append(_lines[i]);
            }

            return text.ToString();
        }

        /// <summary>A line too long for a chunk: pieces of whole words, as many as fit; a run without spaces that doesn't
        /// fit alone is cut at the longest prefix that does.</summary>
        private void SplitLine(int line)
        {
            Close();
            var part = 0;
            var piece = new List<string>();
            var (cost, words) = (0, 0);
            foreach (var word in _lines[line].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var (wordCost, wordWords) = (_bound.Cost(word), Words(word));
                if (piece.Count > 0 && !Within(_context.Cost, cost + wordCost, words + wordWords)) Piece();
                if (piece.Count == 0 && !Within(_context.Cost, wordCost, wordWords))
                {
                    var rest = word;
                    while (!Within(_context.Cost, _bound.Cost(rest), Words(rest)))
                    {
                        var length = Prefix(rest, _bound.Budget - _context.Cost, _context.Text + "\n");
                        Add(rest[..length]);
                        rest = rest[length..];
                    }

                    if (rest.Length > 0) (piece, cost, words) = ([rest], _bound.Cost(rest), Words(rest));
                    continue;
                }

                piece.Add(word);
                cost += wordCost;
                words += wordWords;
            }

            if (piece.Count > 0) Piece();

            void Piece()
            {
                Add(string.Join(' ', piece));
                (piece, cost, words) = ([], 0, 0);
            }

            void Add(string text) => _chunks.Add(new Chunk(_note.Path, _address, line, line + 1, text, _context.Text) { Part = part++ });
        }

        /// <summary>
        /// The length of the longest prefix of <paramref name="text"/> that costs at most <paramref name="room"/>, by binary
        /// search; the token count isn't strictly monotonic, so it is shortened while <paramref name="lead"/> and the
        /// prefix don't fit. At least one character (or surrogate pair).
        /// </summary>
        private int Prefix(string text, int room, string lead)
        {
            var (low, high) = (1, text.Length);
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                if (_bound.Cost(text[..middle]) <= room) low = middle;
                else high = middle - 1;
            }

            var length = low;
            while (length > 1 && (char.IsHighSurrogate(text[length - 1]) || !_bound.Fits(lead + text[..length]))) length--;
            if (char.IsHighSurrogate(text[length - 1]) && length < text.Length) length++;
            return length;
        }
    }
}
