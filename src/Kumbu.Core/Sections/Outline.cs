// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Kumbu.Core.Sections;

/// <summary>Finds the ATX headings of a Markdown note, skipping YAML frontmatter and fenced code.</summary>
public static partial class Outline
{
    public static IReadOnlyList<Heading> Parse(IReadOnlyList<string> lines)
    {
        var found = new List<(int Level, string Text, int Line)>();
        var code = CodeLines(lines);
        for (var i = FrontmatterEnd(lines); i < lines.Count; i++)
        {
            var line = lines[i];
            if (code[i]) continue;
            if (HeadingRegex().Match(line) is { Success: true } h)
            {
                found.Add((h.Groups[1].Length, Rendered(h.Groups[2].Value.Trim()), i));
            }
        }

        var headings = new List<Heading>(found.Count);
        var open = new Stack<Heading>();
        for (var k = 0; k < found.Count; k++)
        {
            var (level, text, line) = found[k];
            while (open.Count > 0 && open.Peek().Level >= level) open.Pop();
            var parent = open.Count > 0 ? open.Peek() : null;

            var end = lines.Count;
            for (var j = k + 1; j < found.Count; j++)
            {
                if (found[j].Level <= level)
                {
                    end = found[j].Line;
                    break;
                }
            }

            var contentEnd = end;
            while (contentEnd > line + 1 && string.IsNullOrWhiteSpace(lines[contentEnd - 1])) contentEnd--;

            var heading = new Heading(level, text, 0, line, end, contentEnd, parent);
            headings.Add(heading);
            open.Push(heading);
        }

        return NumberDuplicates(headings);
    }

    /// <summary>A heading's text as Obsidian shows it: Markdown backslash escapes removed ("Q\#A" is "Q#A"),
    /// so an address uses the same text a person sees.</summary>
    public static string Rendered(string source) => BackslashEscapeRegex().Replace(source, "$1");

    /// <summary>Which lines belong to fenced code blocks (``` or ~~~), the fence lines included.</summary>
    public static bool[] CodeLines(IReadOnlyList<string> lines)
    {
        var code = new bool[lines.Count];
        string? fence = null;
        for (var i = 0; i < lines.Count; i++)
        {
            if (FenceRegex().Match(lines[i]) is { Success: true } f)
            {
                var marker = f.Groups[1].Value;
                code[i] = true;
                if (fence is null) fence = marker;
                // A closing fence uses the same character, at least as many times, and nothing after it.
                else if (marker[0] == fence[0] && marker.Length >= fence.Length && f.Groups[2].Value.Trim().Length == 0) fence = null;
                continue;
            }

            code[i] = fence is not null;
        }

        return code;
    }

    /// <summary>The line after YAML frontmatter ("---" on the first line up to the next "---" or "..."), or 0.</summary>
    public static int FrontmatterEnd(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || lines[0].TrimEnd() != "---") return 0;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].TrimEnd() is "---" or "...") return i + 1;
        }

        return 0;
    }

    // Siblings with the same text get 1-based indexes; parents are rebuilt so addresses carry them.
    private static List<Heading> NumberDuplicates(List<Heading> headings)
    {
        var result = new List<Heading>(headings.Count);
        var map = new Dictionary<Heading, Heading>(ReferenceEqualityComparer.Instance);
        var seen = new Dictionary<(Heading? Parent, string Text), int>(ParentTextComparer.Instance);
        var totals = headings
            .GroupBy(h => (h.Parent, h.Text), ParentTextComparer.Instance)
            .ToDictionary(g => g.Key, g => g.Count(), ParentTextComparer.Instance);

        foreach (var h in headings)
        {
            var key = (h.Parent, h.Text);
            var index = 0;
            if (totals[key] > 1)
            {
                seen[(h.Parent, h.Text)] = index = seen.GetValueOrDefault((h.Parent, h.Text)) + 1;
            }

            var numbered = h with { Index = index, Parent = h.Parent is null ? null : map[h.Parent] };
            map[h] = numbered;
            result.Add(numbered);
        }

        return result;
    }

    private sealed class ParentTextComparer : IEqualityComparer<(Heading? Parent, string Text)>
    {
        public static readonly ParentTextComparer Instance = new();

        public bool Equals((Heading? Parent, string Text) x, (Heading? Parent, string Text) y) =>
            ReferenceEquals(x.Parent, y.Parent) && x.Text == y.Text;

        public int GetHashCode((Heading? Parent, string Text) obj) =>
            HashCode.Combine(obj.Parent is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Parent), obj.Text);
    }

    // Up to three spaces, 1-6 hashes, then a space and the text, or nothing; a closing hash run is dropped.
    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")]
    internal static partial Regex HeadingRegex();

    // CommonMark: a backslash before ASCII punctuation makes it literal.
    [GeneratedRegex(@"\\([!-/:-@\[-`{-~])")]
    private static partial Regex BackslashEscapeRegex();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$")]
    private static partial Regex FenceRegex();
}
