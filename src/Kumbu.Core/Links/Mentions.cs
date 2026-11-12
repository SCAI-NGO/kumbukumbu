// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Links;

/// <summary>A line of another note that names a note without linking to it; <paramref name="Line"/> 0-based,
/// <paramref name="Marks"/> where in <paramref name="Text"/> the title stands (UTF-16 offsets).</summary>
public sealed record Mention(VaultPath Source, int Line, string Text, IReadOnlyList<(int Start, int Length)> Marks);

/// <summary>Unlinked mentions: a note's title as a whole word, ignoring case, outside front matter, code and links.</summary>
public static partial class Mentions
{
    internal const int MinTitleLength = 3;

    /// <summary>The lines of <paramref name="content"/> that mention <paramref name="title"/>, each with its marks.</summary>
    public static IReadOnlyList<(int Line, IReadOnlyList<(int Start, int Length)> Marks)> In(string content, string title)
    {
        title = title.Normalize(NormalizationForm.FormC);
        if (title.Length < MinTitleLength) return [];
        // The raw line may hold either form; the marks stay offsets in it.
        string[] forms = [.. new[] { title, title.Normalize(NormalizationForm.FormD) }.Distinct(StringComparer.Ordinal)];

        var lines = Lines.Split(content);
        var start = Outline.FrontmatterEnd(lines);
        var fenced = Outline.CodeLines(lines);
        var indented = TagReader.IndentedCode(lines, start, fenced);
        var result = new List<(int, IReadOnlyList<(int, int)>)>();
        for (var i = start; i < lines.Count; i++)
        {
            if (fenced[i] || indented[i]) continue;
            var line = lines[i];
            if (!forms.Any(f => line.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;

            var masked = Masked(line);
            masked.Sort();
            var starts = masked.ConvertAll(m => m.Start).ToArray();
            var reach = new int[masked.Count];
            for (var k = 0; k < reach.Length; k++) reach[k] = k == 0 ? masked[0].End : Math.Max(reach[k - 1], masked[k].End);
            var marks = new SortedSet<(int Start, int Length)>();
            foreach (var form in forms)
            {
                for (var at = line.IndexOf(form, StringComparison.OrdinalIgnoreCase); at >= 0;
                     at = line.IndexOf(form, at + 1, StringComparison.OrdinalIgnoreCase))
                {
                    var end = at + form.Length;
                    if (at > 0 && char.IsLetterOrDigit(line[at - 1])) continue;
                    if (end < line.Length && char.IsLetterOrDigit(line[end])) continue;
                    if (Overlaps(starts, reach, at, end)) continue;
                    marks.Add((at, form.Length));
                }
            }

            if (marks.Count > 0) result.Add((i, [.. marks]));
        }

        return result;
    }

    // Whether [at, end) overlaps a span, the spans sorted by start and reach[k] the furthest end of the first k + 1.
    private static bool Overlaps(int[] starts, int[] reach, int at, int end)
    {
        int low = 0, high = starts.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (starts[middle] < end) low = middle + 1;
            else high = middle;
        }

        return low > 0 && reach[low - 1] > at;
    }

    /// <summary>What a mention can't stand in on one line, as (start, end): inline code, wikilinks, Markdown links and
    /// images of any destination, autolinks and bare URLs.</summary>
    private static List<(int Start, int End)> Masked(string line)
    {
        var spans = NoteLink.CodeSpans(line);
        spans.AddRange(WikiLink.LinkRegex().Matches(line).Select(m => (m.Index, m.Index + m.Length)));
        var closers = Closers(line);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '[' && MarkdownLinkEnd(line, closers, i) is { } end)
            {
                spans.Add((i, end));
                i = end - 1;
            }
        }

        spans.AddRange(AutolinkRegex().Matches(line).Select(m => (m.Index, m.Index + m.Length)));
        spans.AddRange(UrlRegex().Matches(line).Select(m => (m.Index, m.Index + m.Length)));
        return spans;
    }

    // "[text](destination)" with balanced brackets and parentheses; the index after it, or null.
    private static int? MarkdownLinkEnd(string line, int[] closers, int start)
    {
        var i = closers[start];
        if (i < 0 || i + 1 >= line.Length || line[i + 1] != '(') return null;
        var j = closers[i + 1];
        return j < 0 ? null : j + 1;
    }

    // For each '[' and '(' of the line the ']' or ')' that closes it, else -1, in one pass: the first unescaped closer
    // that takes the depth below the one just after the opener. An escape read from any opener on is read the same from
    // the start of the line, so one pass serves them all, escaped openers included.
    private static int[] Closers(string line)
    {
        var closers = new int[line.Length];
        Array.Fill(closers, -1);
        Stack<(int At, int Depth)> brackets = new(), parens = new();
        var (bracketDepth, parenDepth) = (0, 0);
        for (int i = 0, escaped = -1; i < line.Length; i++)
        {
            var counts = i != escaped;
            switch (line[i])
            {
                case '\\' when counts:
                    escaped = i + 1;
                    break;
                case '[':
                    if (counts) bracketDepth++;
                    brackets.Push((i, bracketDepth));
                    break;
                case '(':
                    if (counts) parenDepth++;
                    parens.Push((i, parenDepth));
                    break;
                case ']' when counts:
                    Close(brackets, --bracketDepth, i);
                    break;
                case ')' when counts:
                    Close(parens, --parenDepth, i);
                    break;
            }
        }

        return closers;

        void Close(Stack<(int At, int Depth)> open, int depth, int at)
        {
            while (open.TryPeek(out var o) && o.Depth > depth) closers[open.Pop().At] = at;
        }
    }

    [GeneratedRegex(@"<(?:[A-Za-z][A-Za-z0-9+.-]{1,31}:[^<>\s]*|[^<>\s@]+@[^<>\s]+)>")]
    private static partial Regex AutolinkRegex();

    [GeneratedRegex(@"\bhttps?://\S+")]
    private static partial Regex UrlRegex();
}
