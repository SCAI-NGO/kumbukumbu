// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Board;
using Kumbu.Core.Links;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tags;

/// <summary>A tag of a note: its text without '#' as written (NFC), and the 0-based line it stands on (for a
/// frontmatter tag, the line of its list item or of the <c>tags</c> key).</summary>
public sealed record TagUse(string Tag, int Line);

/// <summary>
/// Reads a note's tags as Obsidian does: the frontmatter's <c>tags</c> property, and <c>#tags</c> in the text outside
/// code, comments and HTML. Pure; changes nothing.
/// </summary>
public static partial class TagReader
{
    public static IReadOnlyList<TagUse> Read(string content)
    {
        var lines = Lines.Split(content);
        var end = Outline.FrontmatterEnd(lines);
        var result = end > 0 ? FromFrontmatter(content, lines, end) : [];
        FromText(lines, end, result);
        return result;
    }

    /// <summary>Whether <paramref name="text"/> (without '#') is a whole tag: tag characters only, at least one of
    /// them not a digit.</summary>
    public static bool IsTag(string text)
    {
        if (text.Length == 0 || text[^1] == '/') return false;
        var letter = false;
        foreach (var c in text)
        {
            if (!IsTagChar(c)) return false;
            if (!char.IsAsciiDigit(c)) letter = true;
        }

        return letter;
    }

    /// <summary>
    /// Obsidian's tag characters: letters, digits, '_', '-', '/', and any other Unicode character (emoji among them)
    /// except whitespace, ASCII punctuation and the general and supplemental punctuation blocks.
    /// </summary>
    public static bool IsTagChar(char c) =>
        c is '_' or '-' or '/'
        || (!char.IsWhiteSpace(c) && !char.IsControl(c) && !(char.IsAscii(c) && char.IsPunctuation(c) || char.IsAscii(c) && char.IsSymbol(c))
            && c is not (>= ' ' and <= '⁯') and not (>= '⸀' and <= '⹿'));

    private static List<TagUse> FromFrontmatter(string content, IReadOnlyList<string> lines, int end)
    {
        // Most frontmatter has no tags: no YAML parse for it.
        var key = -1;
        for (var i = 1; i < end - 1; i++)
        {
            if (TagsKeyRegex().IsMatch(lines[i]))
            {
                key = i;
                break;
            }
        }

        if (key < 0) return [];
        var read = Frontmatter.Read(content);
        if (read.Problem is not null) return [];
        var values = read.Values.GetValueOrDefault("tags") switch
        {
            IReadOnlyList<object?> list => list.OfType<string>(),
            string s => [s],
            _ => [],
        };

        var result = new List<TagUse>();
        var next = key + 1;
        foreach (var value in values)
        {
            // A block list puts each value on its own line; find it there, else the key's line stands for it.
            var line = key;
            for (var i = next; i < end - 1 && (lines[i].StartsWith(' ') || lines[i].StartsWith('\t') || lines[i].StartsWith('-')); i++)
            {
                if (!lines[i].Contains(value, StringComparison.Ordinal)) continue;
                line = i;
                next = i + 1;
                break;
            }

            // Older notes write "tags: a, b" or "tags: a b"; Obsidian splits those.
            foreach (var part in value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                var tag = part.TrimStart('#').TrimEnd('/').Normalize(NormalizationForm.FormC);
                if (IsTag(tag)) result.Add(new TagUse(tag, line));
            }
        }

        return result;
    }

    private static void FromText(IReadOnlyList<string> lines, int start, List<TagUse> result)
    {
        var fenced = Outline.CodeLines(lines);
        var indented = IndentedCode(lines, start, fenced);
        string? comment = null;
        for (var i = start; i < lines.Count; i++)
        {
            if (fenced[i] || indented[i]) continue;
            var line = lines[i];
            var skip = Skipped(line);
            for (var p = 0; p < line.Length; p++)
            {
                if (comment is not null)
                {
                    var close = line.IndexOf(comment, p, StringComparison.Ordinal);
                    if (close < 0) break;
                    p = close + comment.Length - 1;
                    comment = null;
                    continue;
                }

                if (skip.FirstOrDefault(s => s.Start == p) is { End: > 0 } span)
                {
                    p = span.End - 1;
                    continue;
                }

                if (string.CompareOrdinal(line, p, "<!--", 0, 4) == 0)
                {
                    comment = "-->";
                    p += 3;
                    continue;
                }

                if (string.CompareOrdinal(line, p, "%%", 0, 2) == 0)
                {
                    comment = "%%";
                    p += 1;
                    continue;
                }

                if (line[p] != '#' || (p > 0 && !char.IsWhiteSpace(line[p - 1]))) continue;
                var stop = p + 1;
                while (stop < line.Length && IsTagChar(line[stop])) stop++;
                var tag = line[(p + 1)..stop].TrimEnd('/');
                if (IsTag(tag)) result.Add(new TagUse(tag.Normalize(NormalizationForm.FormC), i));
                p = stop - 1;
            }
        }
    }

    /// <summary>What a tag can't stand in on one line: inline code, wikilinks and HTML tags, as (start, end).</summary>
    private static List<(int Start, int End)> Skipped(string line)
    {
        var spans = NoteLink.CodeSpans(line);
        if (line.Contains("[[", StringComparison.Ordinal)) spans.AddRange(WikiLink.LinkRegex().Matches(line).Select(m => (m.Index, m.Index + m.Length)));
        if (line.Contains('<')) spans.AddRange(HtmlTagRegex().Matches(line).Select(m => (m.Index, m.Index + m.Length)));
        return spans;
    }

    /// <summary>
    /// Indented code blocks (four spaces or a tab) after a blank line, outside a list: inside a list the same indent
    /// is a nested item or its text, which is where Obsidian users write most of their nested tags.
    /// </summary>
    private static bool[] IndentedCode(IReadOnlyList<string> lines, int start, bool[] fenced)
    {
        var code = new bool[lines.Count];
        var blank = true;
        var inList = false;
        var inCode = false;
        for (var i = start; i < lines.Count; i++)
        {
            var line = lines[i];
            if (fenced[i])
            {
                blank = true;
                inCode = inList = false;
                continue;
            }

            if (line.Trim().Length == 0)
            {
                blank = true;
                continue;
            }

            var indent = Indent(line);
            if (indent >= 4 && (inCode || (blank && !inList)))
            {
                code[i] = inCode = true;
                continue;
            }

            inCode = false;
            if (ListItemRegex().IsMatch(line)) inList = true;
            else if (indent == 0 && blank) inList = false;
            blank = false;
        }

        return code;
    }

    private static int Indent(string line)
    {
        var columns = 0;
        foreach (var c in line)
        {
            if (c == ' ') columns++;
            else if (c == '\t') columns += 4 - columns % 4;
            else break;
        }

        return columns;
    }

    [GeneratedRegex(@"^(tags|""tags""|'tags')[ \t]*:")]
    private static partial Regex TagsKeyRegex();

    [GeneratedRegex(@"^[ \t]*([-*+]|\d{1,9}[.)])([ \t]|$)")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"</?[A-Za-z][A-Za-z0-9-]*(\s[^<>]*)?/?>")]
    private static partial Regex HtmlTagRegex();
}
