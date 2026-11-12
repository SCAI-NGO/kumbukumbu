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
            var marks = new SortedSet<(int Start, int Length)>();
            foreach (var form in forms)
            {
                for (var at = line.IndexOf(form, StringComparison.OrdinalIgnoreCase); at >= 0;
                     at = line.IndexOf(form, at + 1, StringComparison.OrdinalIgnoreCase))
                {
                    var end = at + form.Length;
                    if (at > 0 && char.IsLetterOrDigit(line[at - 1])) continue;
                    if (end < line.Length && char.IsLetterOrDigit(line[end])) continue;
                    if (masked.Any(m => at < m.End && m.Start < end)) continue;
                    marks.Add((at, form.Length));
                }
            }

            if (marks.Count > 0) result.Add((i, [.. marks]));
        }

        return result;
    }

    /// <summary>What a mention can't stand in on one line, as (start, end): inline code, wikilinks, Markdown links and
    /// images of any destination, autolinks and bare URLs.</summary>
    private static List<(int Start, int End)> Masked(string line)
    {
        var spans = NoteLink.CodeSpans(line);
        spans.AddRange(WikiLink.LinkRegex().Matches(line).Select(m => (m.Index, m.Index + m.Length)));
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '[' && MarkdownLinkEnd(line, i) is { } end)
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
    private static int? MarkdownLinkEnd(string line, int start)
    {
        var i = start + 1;
        for (var depth = 0; i < line.Length; i++)
        {
            if (line[i] == '\\') i++;
            else if (line[i] == '[') depth++;
            else if (line[i] == ']' && depth-- == 0) break;
        }

        if (i + 1 >= line.Length || line[i + 1] != '(') return null;
        for (int j = i + 2, depth = 0; j < line.Length; j++)
        {
            if (line[j] == '\\') j++;
            else if (line[j] == '(') depth++;
            else if (line[j] == ')' && depth-- == 0) return j + 1;
        }

        return null;
    }

    [GeneratedRegex(@"<(?:[A-Za-z][A-Za-z0-9+.-]{1,31}:[^<>\s]*|[^<>\s@]+@[^<>\s]+)>")]
    private static partial Regex AutolinkRegex();

    [GeneratedRegex(@"\bhttps?://\S+")]
    private static partial Regex UrlRegex();
}
