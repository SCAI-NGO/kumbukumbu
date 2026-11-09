// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Search;

internal enum BlockKind { Heading, Paragraph, List, Table, Code, Quote }

/// <summary>Lines [<paramref name="Start"/>, <paramref name="End"/>) of a note that the chunker keeps together.</summary>
internal sealed record Block(BlockKind Kind, int Start, int End);

/// <summary>The Markdown blocks of a section: its heading, then runs of non-blank lines, with fenced code one block
/// across blank lines and a GFM table, a list or a quote a block of its own where it starts inside a run.</summary>
internal static partial class Blocks
{
    public static IReadOnlyList<Block> Parse(IReadOnlyList<string> lines, int start, int end, bool[] code)
    {
        var blocks = new List<Block>();
        var i = start;
        while (i < end)
        {
            if (code[i])
            {
                var j = i;
                while (j < end && code[j]) j++;
                blocks.Add(new Block(BlockKind.Code, i, j));
                i = j;
                continue;
            }

            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                i++;
                continue;
            }

            if (Outline.HeadingRegex().IsMatch(lines[i]))
            {
                blocks.Add(new Block(BlockKind.Heading, i, i + 1));
                i++;
                continue;
            }

            var kind = KindAt(lines, i, end, code);
            var k = i + 1;
            while (k < end && !code[k] && !string.IsNullOrWhiteSpace(lines[k]) && Continues(kind, lines, k, end, code)) k++;
            blocks.Add(new Block(kind, i, k));
            i = k;
        }

        return blocks;
    }

    /// <summary>The lines where the items of the list [<paramref name="start"/>, <paramref name="end"/>) start: those at
    /// the list's first indentation with a marker.</summary>
    public static IEnumerable<int> Items(IReadOnlyList<string> lines, int start, int end)
    {
        var indent = Indent(lines[start]);
        for (var i = start; i < end; i++)
        {
            if (i == start || (Indent(lines[i]) == indent && ListRegex().IsMatch(lines[i]))) yield return i;
        }
    }

    private static BlockKind KindAt(IReadOnlyList<string> lines, int i, int end, bool[] code)
    {
        if (TableStarts(lines, i, end, code)) return BlockKind.Table;
        if (ListRegex().IsMatch(lines[i])) return BlockKind.List;
        if (QuoteRegex().IsMatch(lines[i])) return BlockKind.Quote;
        return BlockKind.Paragraph;
    }

    /// <summary>Whether line <paramref name="k"/> still belongs to the block of <paramref name="kind"/> before it.</summary>
    private static bool Continues(BlockKind kind, IReadOnlyList<string> lines, int k, int end, bool[] code)
    {
        if (kind == BlockKind.Table) return lines[k].Contains('|');
        if (TableStarts(lines, k, end, code)) return false;
        return kind switch
        {
            BlockKind.List => !QuoteRegex().IsMatch(lines[k]),
            BlockKind.Quote => !ListRegex().IsMatch(lines[k]),
            _ => !ListRegex().IsMatch(lines[k]) && !QuoteRegex().IsMatch(lines[k]),
        };
    }

    private static bool TableStarts(IReadOnlyList<string> lines, int i, int end, bool[] code) =>
        lines[i].Contains('|') && i + 1 < end && !code[i + 1] && SeparatorRegex().IsMatch(lines[i + 1]);

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"^\s*([-*+]|\d+[.)])\s")]
    private static partial Regex ListRegex();

    [GeneratedRegex(@"^\s*>")]
    private static partial Regex QuoteRegex();
}
