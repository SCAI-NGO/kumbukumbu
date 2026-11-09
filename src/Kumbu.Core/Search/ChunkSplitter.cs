// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;
using static Kumbu.Core.Search.Chunker;

namespace Kumbu.Core.Search;

/// <summary>One note's chunking (<see cref="Chunker.Split"/>). A candidate's cost is the context's plus every line's:
/// with arctic-m the costs of lines add up to the text's tokens exactly, and the fallback's to its characters.</summary>
internal sealed class ChunkSplitter
{
    /// <summary>A section: the text before the first heading (<see cref="Heading"/> null, the note itself) or a
    /// heading's own lines, blank lines trimmed at both ends.</summary>
    private sealed record Section(Heading? Heading, int Start, int End, bool HasChildren);

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

    public ChunkSplitter(Note note, ChunkBound bound)
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

        // Too long for a part of its own: item by item or line by line, the first into the open part if it fits.
        if (block.Kind == BlockKind.List)
        {
            var items = Blocks.Items(_lines, block.Start, block.End).Append(block.End).ToList();
            for (var i = 0; i + 1 < items.Count; i++) FeedUnit(items[i], items[i + 1]);
        }
        else
        {
            for (var i = block.Start; i < block.End; i++) FeedUnit(i, i + 1);
        }
    }

    /// <summary>
    /// A table too long for a part of its own: its first part holds the header rows and at least the first row; every
    /// later part repeats the header rows before its own, except one whose row doesn't fit with them. Header rows that
    /// can't share a part with the first row end the open part where they fit there.
    /// </summary>
    private void FeedTable(Block table)
    {
        var header = table.Start;
        var rows = header + 2;
        var first = Math.Min(rows + 1, table.End);
        if (TryAppend(first)) rows = first;
        else if (Fits(-1, header, first))
        {
            TryOpen(-1, header, first);
            rows = first;
        }
        else if (!TryAppend(rows))
        {
            FeedUnit(header, header + 1);
            FeedUnit(header + 1, rows);
        }

        // Whether the open part went without the header rows for a row of its own that doesn't fit with them.
        var bare = false;
        for (var r = rows; r < table.End; r++)
        {
            // The rows after such a row go to a part with the header again, if they fit with it.
            if (bare && _start >= 0 && Fits(header, r, r + 1))
            {
                TryOpen(header, r, r + 1);
                bare = false;
                continue;
            }

            if (TryAppend(r + 1)) continue;
            if (TryOpen(header, r, r + 1)) bare = false;
            else if (TryOpen(-1, r, r + 1)) bare = true;
            else
            {
                SplitLine(r);
                bare = false;
            }
        }
    }

    /// <summary>Lines [start, end) of a block that didn't fit whole: into the part, a new one, or line by line.</summary>
    private void FeedUnit(int start, int end)
    {
        if (_start < 0 && string.IsNullOrWhiteSpace(_lines[start]) && end == start + 1) return;
        if (TryAppend(end) || TryOpen(-1, start, end)) return;
        if (end - start > 1)
        {
            for (var i = start; i < end; i++) FeedUnit(i, i + 1);
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
    /// The length of the longest prefix of <paramref name="text"/> that costs at most <paramref name="room"/> and holds
    /// at most <see cref="Chunker.MaxWords"/> (a run without spaces can hold many, "a,b,c,…", and without a budget
    /// only the words limit it), by binary search; the word count grows with the prefix, but the token count isn't
    /// strictly monotonic, so the prefix is shortened while <paramref name="lead"/> and it don't fit. At least one
    /// character (or surrogate pair).
    /// </summary>
    private int Prefix(string text, int room, string lead)
    {
        var (low, high) = (1, text.Length);
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (_bound.Cost(text[..middle]) <= room && Words(text[..middle]) <= MaxWords) low = middle;
            else high = middle - 1;
        }

        var length = low;
        while (length > 1 && (char.IsHighSurrogate(text[length - 1]) || !_bound.Fits(lead + text[..length]))) length--;
        if (char.IsHighSurrogate(text[length - 1]) && length < text.Length) length++;
        return length;
    }
}
