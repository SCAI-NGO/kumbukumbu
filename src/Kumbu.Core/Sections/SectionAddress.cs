// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sections;

/// <summary>One step of a section address: a heading's text and, for duplicates, its index.</summary>
public sealed record AddressSegment(string Text, int? Index);

/// <summary>
/// Section addresses (concept §4): <c>/Work/plan.md#Architecture#Gates</c>. The first '#' after ".md"
/// starts the address, each further '#' goes one level down, "\#" is a literal '#', and a trailing
/// "[n]" picks among headings with the same text under the same parent.
/// </summary>
public static partial class SectionAddress
{
    /// <summary>Splits "path.md#A#B" into the path and the address ("A#B"), which is null without one.</summary>
    public static (string Path, string? Fragment) Split(string arg)
    {
        var at = arg.IndexOf(".md#", StringComparison.Ordinal);
        return at < 0 ? (arg, null) : (arg[..(at + 3)], arg[(at + 4)..]);
    }

    public static bool TryParse(string fragment, [NotNullWhen(true)] out IReadOnlyList<AddressSegment>? segments)
    {
        var result = new List<AddressSegment>();
        var current = new StringBuilder();
        var escapedEnd = -1; // position in current after the last escaped character
        for (var i = 0; i <= fragment.Length; i++)
        {
            if (i == fragment.Length || fragment[i] == '#')
            {
                var text = current.ToString();
                int? index = null;
                // A trailing [n] is an index unless its '[' was escaped.
                if (IndexRegex().Match(text) is { Success: true } m && m.Index >= escapedEnd)
                {
                    index = int.Parse(m.Groups[1].Value);
                    text = text[..m.Index];
                }

                if (text.Trim().Length == 0)
                {
                    segments = null;
                    return false;
                }

                result.Add(new AddressSegment(text.Trim(), index));
                current.Clear();
                escapedEnd = -1;
                continue;
            }

            if (fragment[i] == '\\' && i + 1 < fragment.Length)
            {
                current.Append(fragment[++i]);
                escapedEnd = current.Length;
                continue;
            }

            current.Append(fragment[i]);
        }

        segments = result;
        return true;
    }

    /// <summary>Writes heading text as an address segment: escapes '\' and '#', and a '[' that would read as an index.</summary>
    public static string Escape(string text)
    {
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("#", "\\#", StringComparison.Ordinal);
        var m = IndexRegex().Match(escaped);
        return m.Success ? escaped[..m.Index] + "\\" + escaped[m.Index..] : escaped;
    }

    /// <summary>
    /// The headings an address matches: those whose chain ends with the address's segments. Exact text
    /// first; if nothing matches exactly, ignoring case; then as a link holds a heading (<see cref="Linked"/>),
    /// ignoring case: its text as written, as Obsidian links it, or as shown, as the web app does.
    /// </summary>
    public static IReadOnlyList<Heading> Resolve(IReadOnlyList<Heading> headings, IReadOnlyList<AddressSegment> segments)
    {
        // A heading's shown form is worked out once per lookup, not once per heading whose chain holds it.
        var shown = new Dictionary<string, string>(StringComparer.Ordinal);
        string LinkedShown(string h) => shown.TryGetValue(h, out var s) ? s : shown[h] = Linked(Shown(h));

        foreach (var same in new Func<string, string, bool>[]
        {
            (a, h) => a == h,
            (a, h) => string.Equals(a, h, StringComparison.OrdinalIgnoreCase),
            (a, h) => Linked(a) is { Length: > 0 } link
                && (string.Equals(link, Linked(h), StringComparison.OrdinalIgnoreCase) || string.Equals(link, LinkedShown(h), StringComparison.OrdinalIgnoreCase)),
        })
        {
            var matches = headings.Where(h => Matches(h, segments, same)).ToList();
            if (matches.Count > 0) return matches;
        }

        return [];
    }

    /// <summary>A heading's text as a link holds it: without '# | ^ [ ]' and "%%", runs of whitespace as one space
    /// ("C# notes | x" is "C notes x"), as Obsidian and the web app's link completion (headingLink) write it.</summary>
    private static string Linked(string text) => WhitespaceRegex().Replace(LinkUnsafeRegex().Replace(text, ""), " ").Trim();

    /// <summary>A heading's text as it reads, as the web app's headingText takes it: comments gone (outside code
    /// spans), wiki links by their label, Markdown links by their text, highlight, emphasis and code marks gone.</summary>
    private static string Shown(string text)
    {
        // "%%" inside a code span is text: the comments' marks are found with the spans blanked.
        var masked = MaskCodeSpans(text);
        var outside = new StringBuilder();
        var from = 0;
        var open = false;
        for (var p = masked.IndexOf("%%", StringComparison.Ordinal); p >= 0; p = masked.IndexOf("%%", p + 2, StringComparison.Ordinal))
        {
            if (!open) outside.Append(text, from, p - from);
            open = !open;
            from = p + 2;
        }

        if (!open) outside.Append(text, from, text.Length - from);
        var shown = WikiLinkRegex().Replace(outside.ToString(), m => WikiLabel(m.Groups[1].Value));
        shown = WithoutMarkdownLinks(shown);
        shown = WithoutHighlights(shown);
        return EmphasisRegex().Replace(shown, "$2").Trim();
    }

    /// <summary>A wiki link's label as the web app shows it (linkLabel): its alias, else "target › heading", else
    /// its target or heading.</summary>
    private static string WikiLabel(string inner)
    {
        var bar = inner.IndexOf('|');
        if (bar >= 0) return inner[(bar + 1)..];
        var hash = inner.IndexOf('#');
        if (hash < 0) return inner;
        var (target, heading) = (inner[..hash], inner[(hash + 1)..]);
        return heading.Length == 0 ? target : target.Length == 0 ? heading : $"{target} › {heading}";
    }

    /// <summary>A note path with a section address, as it would be typed in sh.</summary>
    public static string Display(VaultPath path, Heading heading) => path.Value + heading.Address;

    private static bool Matches(Heading heading, IReadOnlyList<AddressSegment> segments, Func<string, string, bool> same)
    {
        var chain = heading.Chain().ToList();
        if (chain.Count < segments.Count) return false;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[segments.Count - 1 - i];
            var h = chain[i];
            if (!same(segment.Text, h.Text)) return false;
            if (segment.Index is { } index && index != Math.Max(h.Index, 1)) return false;
        }

        return true;
    }

    [GeneratedRegex(@"\[(\d+)\]$")]
    private static partial Regex IndexRegex();

    [GeneratedRegex(@"%%|[#|^\[\]]")]
    private static partial Regex LinkUnsafeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // The web app's patterns (outline.ts): a code span, its backticks paired by count, (`+)[\s\S]*?(?<!`)\1(?!`);
    // a wiki link; a Markdown link or image, !?\[([^\]]*)\]\([^)]*\); a highlight, ==(?=[^\s=])(.+?)(?<=[^\s=])==;
    // emphasis, strike-through and code marks. The scans below give exactly what those patterns give
    // (SectionAddressTests).

    /// <summary>The text with each code span blanked to spaces: an opening run of backticks closes at the next run
    /// of exactly its length, else, as the web app's pattern does, of the longest shorter length that has one.</summary>
    internal static string MaskCodeSpans(string text)
    {
        var runs = new List<(int Start, int Length)>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '`') continue;
            var start = i;
            while (i + 1 < text.Length && text[i + 1] == '`') i++;
            runs.Add((start, i - start + 1));
        }

        if (runs.Count < 2) return text;
        var byLength = new Dictionary<int, List<int>>();
        for (var r = 0; r < runs.Count; r++)
        {
            if (!byLength.TryGetValue(runs[r].Length, out var list)) byLength[runs[r].Length] = list = [];
            list.Add(r);
        }

        var masked = text.ToCharArray();
        for (var r = 0; r < runs.Count; r++)
        {
            for (var length = runs[r].Length; length >= 1; length--)
            {
                if (!byLength.TryGetValue(length, out var list)) continue;
                var at = list.BinarySearch(r + 1);
                if (at < 0) at = ~at;
                if (at == list.Count) continue;
                var closer = list[at];
                Array.Fill(masked, ' ', runs[r].Start, runs[closer].Start + length - runs[r].Start);
                r = closer;
                break;
            }
        }

        return new string(masked);
    }

    /// <summary>The text with each Markdown link or image replaced by its text: '[' up to the first ']', which a '('
    /// follows, up to the first ')' after it.</summary>
    internal static string WithoutMarkdownLinks(string text)
    {
        var nextBracket = NextOf(text, ']');
        var nextParen = NextOf(text, ')');
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var open = text[i] == '!' && i + 1 < text.Length && text[i + 1] == '[' ? i + 1 : i;
            if (text[open] == '[')
            {
                var close = nextBracket[open + 1];
                if (close + 1 < text.Length && text[close + 1] == '(' && nextParen[close + 2] is var end && end < text.Length)
                {
                    result.Append(text, open + 1, close - open - 1);
                    i = end + 1;
                    continue;
                }
            }

            result.Append(text[i++]);
        }

        return result.ToString();
    }

    /// <summary>The text with each highlight's "==" marks gone: an opening "==" before a character that is neither
    /// whitespace nor '=' closes at the first "==" on its line after one that is neither, at least one character on.</summary>
    internal static string WithoutHighlights(string text)
    {
        static bool Plain(char c) => c != '=' && !char.IsWhiteSpace(c);

        // nextCloser[k]: the first position at or after k where a closing "==" starts; nextNewline likewise.
        var nextCloser = new int[text.Length + 1];
        nextCloser[text.Length] = text.Length;
        for (var k = text.Length - 1; k >= 0; k--)
            nextCloser[k] = k >= 1 && k + 1 < text.Length && text[k] == '=' && text[k + 1] == '=' && Plain(text[k - 1]) ? k : nextCloser[k + 1];
        var nextNewline = NextOf(text, '\n');

        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (i + 3 < text.Length && text[i] == '=' && text[i + 1] == '=' && Plain(text[i + 2])
                && nextCloser[i + 3] is var close && close < text.Length && nextNewline[i + 2] > close)
            {
                result.Append(text, i + 2, close - i - 2);
                i = close + 2;
                continue;
            }

            result.Append(text[i++]);
        }

        return result.ToString();
    }

    /// <summary>For each position, the first position at or after it that holds the character, else the text's length.</summary>
    private static int[] NextOf(string text, char c)
    {
        var next = new int[text.Length + 1];
        next[text.Length] = text.Length;
        for (var k = text.Length - 1; k >= 0; k--) next[k] = text[k] == c ? k : next[k + 1];
        return next;
    }

    [GeneratedRegex(@"!?\[\[([^\[\]\n]+?)\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"(\*\*|__|\*|_|~~|`)(.+?)\1")]
    private static partial Regex EmphasisRegex();
}
