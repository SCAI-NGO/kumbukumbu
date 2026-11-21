// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using Kumbu.Core.Graph;
using Kumbu.Core.Links;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Links;

/// <summary>
/// The link parsers take time in proportion to a line, whatever it holds, and find the links the parsers before them
/// found: those are kept below as <see cref="Reference"/>, and every short line over a few alphabets is compared.
/// </summary>
public class LinkParserCostTests
{
    private static readonly VaultPath Note = VaultPath.TryParse("/Work/Odd.md", out var p) ? p : throw new InvalidOperationException();

    private static string Fill(string unit, int size) => new StringBuilder(size + unit.Length).Insert(0, unit, size / unit.Length + 1).ToString(0, size);

    private static TimeSpan Time(Action work)
    {
        var watch = Stopwatch.StartNew();
        work();
        return watch.Elapsed;
    }

    [Theory]
    [InlineData("", "[")]
    [InlineData("", "[a](")]
    [InlineData("", "[[a]]")]
    [InlineData("`a` ", "[[a]]")]
    [InlineData("", "[a](b.md)[[c]]")]
    [InlineData("", "[](a")]
    [InlineData("[](a[](a", " ")]
    [InlineData("", "` `` ")]
    [InlineData("", "[a](<b")]
    [InlineData("", "[a](b \"")]
    public void A_long_line_is_read_in_the_time_of_a_line_of_words(string first, string second)
    {
        const int chars = 256 * 1024;
        LinkExtract.Of(Note, "warm [[up]] [a](b.md) `a` #b\n");
        var control = Time(() => LinkExtract.Of(Note, Fill("The quick brown fox reads a plan of tomatoes ", chars)));
        var odd = first.Length == 0 ? Fill(second, chars) : Fill(first, chars / 2) + Fill(second, chars / 2);

        var took = Time(() => LinkExtract.Of(Note, odd));

        var bound = TimeSpan.FromMilliseconds(Math.Max(500, control.TotalMilliseconds * 50));
        Assert.True(took <= bound, $"{took.TotalMilliseconds:0} ms beside {control.TotalMilliseconds:0.0} ms for words (bound {bound.TotalMilliseconds:0} ms)");
    }

    /// <summary>Every string of 1 to <paramref name="longest"/> characters of <paramref name="alphabet"/>.</summary>
    public static IEnumerable<string> AllStrings(string alphabet, int longest)
    {
        for (var n = 1; n <= longest; n++)
        {
            var digits = new int[n];
            var chars = new char[n];
            while (true)
            {
                for (var k = 0; k < n; k++) chars[k] = alphabet[digits[k]];
                yield return new string(chars);
                var at = n - 1;
                while (at >= 0 && ++digits[at] == alphabet.Length) digits[at--] = 0;
                if (at < 0) break;
            }
        }
    }

    [Theory]
    [InlineData("[]()\\ a", 7)]
    [InlineData("[]()<>\"`!#a", 5)]
    [InlineData("[]`!a|", 7)]
    [InlineData("[]()'\"\\ a", 6)]
    public void Every_short_line_has_the_links_it_had(string alphabet, int length)
    {
        foreach (var line in AllStrings(alphabet, length))
        {
            Assert.Equal(Reference.Markdown(line), MarkdownLink.Parse(line).Select(Shape));
            Assert.Equal(Reference.Wiki(line), WikiLink.Parse(line).Select(l => (l.Start, l.Raw)));
        }
    }

    private static string Shape(MarkdownLink l) => $"{l.Start}|{l.Raw}|{l.Target}|{l.Subpath}|{l.Text}|{l.Destination}|{l.Rest}|{l.Embed}";

    /// <summary>The parsers as they were, quadratic on some lines.</summary>
    private static class Reference
    {
        public static IEnumerable<(int, string)> Wiki(string line)
        {
            if (Outline.CodeLines(Lines.Split(line))[0]) return [];
            var spans = CodeSpans(line);
            return WikiLink.LinkRegex().Matches(line)
                .Where(m => !spans.Any(s => m.Index >= s.Start && m.Index < s.End))
                .Select(m => WikiLink.Parse(m.Value).SingleOrDefault() is { } l ? (m.Index, l.Raw) : (-1, ""))
                .Where(l => l.Item1 >= 0)
                .ToList();
        }

        public static IEnumerable<string> Markdown(string line)
        {
            var result = new List<string>();
            if (!Outline.CodeLines(Lines.Split(line))[0]) ParseLine(line, result);
            return result;
        }

        private static List<(int Start, int End)> CodeSpans(string line)
        {
            var spans = new List<(int, int)>();
            var runs = System.Text.RegularExpressions.Regex.Matches(line, "`+").ToList();
            for (var i = 0; i < runs.Count; i++)
            {
                for (var j = i + 1; j < runs.Count; j++)
                {
                    if (runs[j].Length != runs[i].Length) continue;
                    spans.Add((runs[i].Index, runs[j].Index + runs[j].Length));
                    i = j;
                    break;
                }
            }

            return spans;
        }

        private static void ParseLine(string line, List<string> result)
        {
            var code = CodeSpans(line);
            var wikilinks = WikiLink.LinkRegex().Matches(line).Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
            for (var start = 0; start < line.Length; start++)
            {
                if (line[start] != '[' || code.Concat(wikilinks).Any(s => start >= s.Start && start < s.End)) continue;
                if (Escaped(line, start)) continue;
                var embed = start > 0 && line[start - 1] == '!' && !Escaped(line, start - 1) && !code.Any(s => start - 1 >= s.Start && start - 1 < s.End);
                if (TryParse(line, start, embed) is not { } link) continue;
                var end = link.Start + link.Length;
                if (wikilinks.Any(w => w.Start < end && start < w.End)) continue;
                result.Add(link.Shape);
                start = link.Start + link.Length - 1;
            }
        }

        private static (int Start, int Length, string Shape)? TryParse(string line, int start, bool embed)
        {
            var i = start + 1;
            for (var depth = 0; i < line.Length; i++)
            {
                if (line[i] == '\\') i++;
                else if (line[i] == '[') depth++;
                else if (line[i] == ']' && depth-- == 0) break;
            }

            if (i + 1 >= line.Length || line[i + 1] != '(') return null;
            var text = line[start..(i + 1)];
            i = SkipSpaces(line, i + 2);
            var destinationStart = i;
            string inner;
            if (i < line.Length && line[i] == '<')
            {
                for (i++; i < line.Length && line[i] != '>'; i++)
                {
                    if (line[i] == '<') return null;
                    if (line[i] == '\\') i++;
                }

                if (i >= line.Length) return null;
                inner = line[(destinationStart + 1)..i];
                i++;
            }
            else
            {
                for (var depth = 0; i < line.Length && line[i] != ' ' && line[i] != '\t' && !char.IsControl(line[i]); i++)
                {
                    if (line[i] == '\\') i++;
                    else if (line[i] == '(') depth++;
                    else if (line[i] == ')' && depth-- == 0) break;
                }

                inner = line[destinationStart..Math.Min(i, line.Length)];
            }

            var destinationEnd = i;
            i = SkipSpaces(line, i);
            if (i > destinationEnd && i < line.Length && line[i] is '"' or '\'' or '(')
            {
                var close = line[i] == '(' ? ')' : line[i];
                for (i++; i < line.Length && line[i] != close; i++)
                {
                    if (line[i] == '\\') i++;
                }

                if (i >= line.Length) return null;
                i = SkipSpaces(line, i + 1);
            }

            if (i >= line.Length || line[i] != ')') return null;
            if (inner.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(inner, "^[A-Za-z][A-Za-z0-9+.-]*:") || inner.StartsWith("//", StringComparison.Ordinal)) return null;
            var hash = inner.IndexOf('#');
            var path = hash >= 0 ? inner[..hash] : inner;
            var subpath = hash >= 0 ? inner[hash..] : "";
            if (path.Length == 0 && subpath.Length <= 1) return null;
            var from = embed ? start - 1 : start;
            var raw = line[from..(i + 1)];
            return (from, raw.Length,
                $"{from}|{raw}|{MarkdownLink.Decode(path)}|{subpath}|{text}|{line[destinationStart..destinationEnd]}|{line[destinationEnd..(i + 1)]}|{embed}");
        }

        private static int SkipSpaces(string line, int i)
        {
            while (i < line.Length && line[i] is ' ' or '\t') i++;
            return i;
        }

        private static bool Escaped(string line, int index)
        {
            var backslashes = 0;
            while (index - backslashes - 1 >= 0 && line[index - backslashes - 1] == '\\') backslashes++;
            return backslashes % 2 == 1;
        }
    }
}
