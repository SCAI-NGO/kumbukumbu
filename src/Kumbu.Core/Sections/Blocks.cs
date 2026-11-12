// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Kumbu.Core.Sections;

/// <summary>A block a <c>^id</c> names: 0-based lines, <see cref="End"/> excluded, the marker line included
/// when the id stands on a line of its own.</summary>
public sealed record Block(string Id, int Start, int End);

/// <summary>
/// Obsidian's block ids (spec §4.1): <c>^id</c> after a space at the end of a paragraph, list item, quote,
/// callout or heading, or on a line of its own after one blank line, naming the paragraph, list, table or
/// quote above it. Never inside front matter or code. The web's finder answers the same cases
/// (web/src/note/blocks.cases.json).
/// </summary>
public static partial class Blocks
{
    private enum Kind { Heading, Paragraph, List, Table, Quote }

    private sealed record Chunk(Kind Kind, int Start, int End);

    public static IReadOnlyList<Block> Parse(IReadOnlyList<string> lines)
    {
        var blocks = new List<Block>();
        var chunks = Chunks(lines);
        for (var c = 0; c < chunks.Count; c++)
        {
            var (kind, start, end) = chunks[c];
            var last = lines[end - 1];
            switch (kind)
            {
                case Kind.Heading:
                case Kind.Quote:
                    if (InlineRegex().Match(last) is { Success: true } q) blocks.Add(new Block(q.Groups[1].Value, start, end));
                    break;
                case Kind.List:
                    for (var i = start; i < end; i++)
                    {
                        if (ItemRegex().IsMatch(lines[i]) && InlineRegex().Match(lines[i]) is { Success: true } m)
                        {
                            blocks.Add(new Block(m.Groups[1].Value, i, ItemEnd(lines, i, end)));
                        }
                    }

                    break;
                case Kind.Paragraph:
                    if (end - start == 1 && OwnLineRegex().Match(last) is { Success: true } own)
                    {
                        // Exactly one blank line between it and the chunk it names.
                        if (c > 0 && chunks[c - 1] is { Kind: not Kind.Heading } above && above.End == start - 1 && Blank(lines[start - 1]))
                        {
                            blocks.Add(new Block(own.Groups[1].Value, above.Start, end));
                        }
                    }
                    else
                    {
                        // Right below a paragraph's text, an id on its own line is the paragraph's last line.
                        var p = InlineRegex().Match(last);
                        if (!p.Success && end - start > 1) p = OwnLineRegex().Match(last);
                        if (p.Success) blocks.Add(new Block(p.Groups[1].Value, start, end));
                    }

                    break;
            }
        }

        return blocks;
    }

    /// <summary>The first block with this id, ignoring case, or null.</summary>
    public static Block? Find(IReadOnlyList<string> lines, string id) =>
        Parse(lines).FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a section address names a block: "^id", also percent-encoded as "%5Eid".</summary>
    public static bool TryId(string fragment, [NotNullWhen(true)] out string? id)
    {
        id = FragmentRegex().Match(Uri.UnescapeDataString(fragment)) is { Success: true } m ? m.Groups[1].Value : null;
        return id is not null;
    }

    // The note's chunks outside front matter and code, split at blank lines, headings and fences.
    private static List<Chunk> Chunks(IReadOnlyList<string> lines)
    {
        var chunks = new List<Chunk>();
        var code = Outline.CodeLines(lines);
        var body = Outline.FrontmatterEnd(lines);
        var i = body;
        while (i < lines.Count)
        {
            if (code[i] || Blank(lines[i]))
            {
                i++;
                continue;
            }

            // Indented code follows a blank line (or no text at all), but in a list it continues an item.
            if (Indent(lines[i]) >= 4 && (i == body || Blank(lines[i - 1]) || code[i - 1]) && chunks is not [.., { Kind: Kind.List }])
            {
                while (i < lines.Count && !code[i] && (Blank(lines[i]) || Indent(lines[i]) >= 4)) i++;
                continue;
            }

            if (Outline.HeadingRegex().IsMatch(lines[i]))
            {
                chunks.Add(new Chunk(Kind.Heading, i, i + 1));
                i++;
                continue;
            }

            var kind = ItemRegex().IsMatch(lines[i]) ? Kind.List
                : QuoteRegex().IsMatch(lines[i]) ? Kind.Quote
                : i + 1 < lines.Count && lines[i].Contains('|') && DelimiterRowRegex().IsMatch(lines[i + 1]) ? Kind.Table
                : Kind.Paragraph;
            var start = i++;
            while (i < lines.Count && !code[i] && !Blank(lines[i]) && !Outline.HeadingRegex().IsMatch(lines[i])
                   && !(kind == Kind.Paragraph && (ItemRegex().IsMatch(lines[i]) || QuoteRegex().IsMatch(lines[i]))))
            {
                i++;
            }

            chunks.Add(new Chunk(kind, start, i));
        }

        return chunks;
    }

    // An item runs to the next item indented no deeper than its marker, or to text indented less.
    private static int ItemEnd(IReadOnlyList<string> lines, int item, int end)
    {
        var marker = Indent(lines[item]);
        for (var i = item + 1; i < end; i++)
        {
            var indent = Indent(lines[i]);
            if (ItemRegex().IsMatch(lines[i]) ? indent <= marker : indent < marker) return i;
        }

        return end;
    }

    private static bool Blank(string line) => string.IsNullOrWhiteSpace(line);

    // A tab takes the column to the next multiple of four.
    private static int Indent(string line)
    {
        var column = 0;
        foreach (var ch in line)
        {
            if (ch == ' ') column++;
            else if (ch == '\t') column += 4 - column % 4;
            else break;
        }

        return column;
    }

    [GeneratedRegex(@"[ \t]\^([A-Za-z0-9-]+)\s*$")]
    private static partial Regex InlineRegex();

    [GeneratedRegex(@"^\^([A-Za-z0-9-]+)\s*$")]
    private static partial Regex OwnLineRegex();

    [GeneratedRegex(@"^\^([A-Za-z0-9-]+)$")]
    private static partial Regex FragmentRegex();

    [GeneratedRegex(@"^[ \t]*(?:[-*+]|\d{1,9}[.)])(?:[ \t]|$)")]
    private static partial Regex ItemRegex();

    [GeneratedRegex(@"^ {0,3}>")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex DelimiterRowRegex();
}
