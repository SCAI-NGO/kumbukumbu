// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Links;

/// <summary>
/// An inline Markdown link to a note or attachment as written: <c>[text](destination "title")</c>, or an embed
/// <c>![alt](destination)</c>. Keeps the exact source text of each part, so a rewrite can change the path and leave
/// everything else byte for byte.
/// </summary>
/// <param name="Line">0-based line of the note.</param>
/// <param name="Start">Character index of the link's '[' (its '!' for an embed) in that line.</param>
/// <param name="Target">The decoded path ("Meeting Notes.md" for <c>Meeting%20Notes.md</c>); empty for <c>(#Heading)</c>.</param>
/// <param name="Subpath">"#Heading", "#^block" or "", as written (still percent-encoded).</param>
/// <param name="Text">The link text with its brackets, e.g. "[see *this*]".</param>
/// <param name="Destination">The destination as written, angle brackets and subpath included.</param>
/// <param name="Rest">Everything after the destination up to and including ')': whitespace and title.</param>
/// <param name="Embed">An image or embed, <c>![alt](destination)</c>.</param>
public sealed partial record MarkdownLink(
    int Line, int Start, string Raw, string Target, string Subpath, string Text, string Destination, string Rest, bool Embed = false)
    : NoteLink(Line, Start, Raw, Target, Subpath)
{
    /// <summary>The same link pointing to <paramref name="note"/> by its absolute, encoded path; text,
    /// subpath, angle brackets and title unchanged.</summary>
    public string WithPath(VaultPath note)
    {
        var destination = Encode(note) + Subpath;
        if (Destination.StartsWith('<')) destination = $"<{destination}>";
        // Whitespace between '(' and the destination is kept too.
        return Raw[..(Raw.Length - Rest.Length - Destination.Length)] + destination + Rest;
    }

    /// <summary>A note's path as <c>mv</c> writes it: from the vault's root, each segment percent-encoded
    /// where Markdown or URLs would misread it (<c>/Folder/Note%20name.md</c>). Non-ASCII letters stay
    /// readable, as Obsidian and every CommonMark reader take them raw.</summary>
    public static string Encode(VaultPath note)
    {
        var result = new StringBuilder();
        Span<byte> bytes = stackalloc byte[4];
        foreach (var segment in note.Segments.Skip(1))
        {
            result.Append('/');
            foreach (var rune in segment.EnumerateRunes())
            {
                if (!NeedsEncoding(rune))
                {
                    result.Append(rune.ToString());
                    continue;
                }

                var length = rune.EncodeToUtf8(bytes);
                foreach (var b in bytes[..length]) result.Append('%').Append(b.ToString("X2"));
            }
        }

        return result.ToString();
    }

    private static bool NeedsEncoding(Rune rune) =>
        Rune.IsControl(rune) || (rune.IsAscii && " %#?()<>[]^|\\`\"{}".Contains((char)rune.Value));

    /// <summary>All inline Markdown links and embeds to notes and attachments, in order. External links and images
    /// (with a scheme or "//", never fetched nor read as a vault path) and reference-style links don't count, nor
    /// anything in fenced code blocks, inline code or a wikilink; frontmatter does, as for wikilinks.</summary>
    public static new IReadOnlyList<MarkdownLink> Parse(string content)
    {
        var lines = Lines.Split(content);
        var code = Outline.CodeLines(lines);
        var result = new List<MarkdownLink>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!code[i]) ParseLine(i, lines[i], result);
        }

        return result;
    }

    private static void ParseLine(int index, string line, List<MarkdownLink> result)
    {
        if (!line.Contains('[')) return;
        var scan = new Scan(line);
        for (var start = 0; start < line.Length; start++)
        {
            if (line[start] != '[' || scan.Hidden[start] || scan.Escaped[start]) continue;
            var embed = start > 0 && line[start - 1] == '!' && !scan.Escaped[start - 1] && !scan.Code[start - 1];
            if (TryParse(index, scan, start, embed) is not { } link) continue;
            result.Add(link);
            start = link.Start + link.Raw.Length - 1;
        }
    }

    /// <summary>The link at <paramref name="start"/>, every part found by a lookup in <paramref name="scan"/>: no scan of the
    /// line from here, so a line of many '[' costs its length, not its length squared. Nothing is copied until it is a link,
    /// and links don't overlap.</summary>
    private static MarkdownLink? TryParse(int index, Scan scan, int start, bool embed)
    {
        var line = scan.Line;
        // Link text: brackets balance, backslash escapes don't count. No '(' right after ']': plain brackets or a
        // reference-style link, neither is read.
        var i = scan.Close[start];
        if (i < 0 || i + 1 >= line.Length || line[i + 1] != '(') return null;
        i = scan.NextNonSpace[i + 2];
        var destinationStart = i;
        int innerStart, innerEnd;
        if (i < line.Length && line[i] == '<')
        {
            i = scan.NextAngle[i + 1];
            if (i < 0 || line[i] == '<') return null;
            (innerStart, innerEnd) = (destinationStart + 1, i);
            i++;
        }
        else
        {
            // Parentheses inside a bare destination must balance: "Plan%20(v2).md". It ends at the first space, tab or
            // control character, or at the ')' that takes the depth below where it started.
            var stop = scan.NextStop[i];
            var close = scan.DepthDrop[i] - 1;
            if (close >= 0 && close < stop) stop = close;
            if (stop >= line.Length) return null;
            (innerStart, innerEnd) = (destinationStart, stop);
            i = stop;
        }

        var destinationEnd = i;
        i = scan.NextNonSpace[i];
        if (i > destinationEnd && i < line.Length && line[i] is '"' or '\'' or '(')
        {
            i = (line[i] switch { '"' => scan.NextDouble, '\'' => scan.NextSingle, _ => scan.NextParen })[i + 1];
            if (i < 0) return null;
            i = scan.NextNonSpace[i + 1];
        }

        if (i >= line.Length || line[i] != ')') return null;
        // With a wikilink anywhere in it, "[see [[x]]](y.md)" or "[a](x[[y]].md)", Obsidian reads the wikilink, not a
        // Markdown link.
        if (scan.WikiBefore[i + 1] > scan.WikiBefore[start]) return null;
        var inner = line.AsSpan(innerStart, innerEnd - innerStart);
        if (inner.Length == 0 || SchemeRegex().IsMatch(inner) || inner.StartsWith("//", StringComparison.Ordinal)) return null;

        // The fragment starts at the first raw '#', so an encoded "%23" stays part of the name.
        var hash = inner.IndexOf('#');
        var path = hash >= 0 ? inner[..hash] : inner;
        var subpath = hash >= 0 ? inner[hash..] : [];
        if (path.Length == 0 && subpath.Length <= 1) return null;
        var from = embed ? start - 1 : start;
        return new MarkdownLink(index, from, line[from..(i + 1)], Decode(path.ToString()), subpath.ToString(), line[start..(scan.Close[start] + 1)],
            line[destinationStart..destinationEnd], line[destinationEnd..(i + 1)], embed);
    }

    /// <summary>
    /// What <see cref="TryParse"/> asks of a line, each worked out for every position in a pass or two. A backslash
    /// escapes the character after it, so a character is escaped when an odd number of backslashes stand right before
    /// it; every scan the parser used to make starts right after an unescaped character, so this one reading of the
    /// escapes is the one each of those scans made. The "next" lookups are -1 where there is none.
    /// </summary>
    private sealed class Scan
    {
        public readonly string Line;
        public readonly bool[] Escaped;
        /// <summary>Inside inline code.</summary>
        public readonly bool[] Code;
        /// <summary>Inside inline code or a wikilink: no link starts there.</summary>
        public readonly bool[] Hidden;
        /// <summary>How many characters before each position lie inside a wikilink.</summary>
        public readonly int[] WikiBefore;
        /// <summary>The ']' that closes the '[' at each position, brackets balanced.</summary>
        public readonly int[] Close;
        /// <summary>The first unescaped space, tab or control character at or after each position; the line's length if none.</summary>
        public readonly int[] NextStop;
        /// <summary>With the depth before each position counted over unescaped parentheses, the first later position
        /// where the depth is lower than here: one past the ')' that closes what starts here.</summary>
        public readonly int[] DepthDrop;
        public readonly int[] NextAngle, NextDouble, NextSingle, NextParen;
        /// <summary>The first position at or after each that isn't a space or tab; the line's length if none.</summary>
        public readonly int[] NextNonSpace;

        public Scan(string line)
        {
            Line = line;
            var n = line.Length;
            Escaped = new bool[n];
            for (var k = 1; k < n; k++) Escaped[k] = line[k - 1] == '\\' && !Escaped[k - 1];

            Code = new bool[n];
            Hidden = new bool[n];
            foreach (var (s, e) in CodeSpans(line))
            {
                for (var k = s; k < e; k++) Code[k] = Hidden[k] = true;
            }

            var wiki = new bool[n];
            foreach (System.Text.RegularExpressions.Match m in WikiLink.LinkRegex().Matches(line))
            {
                for (var k = m.Index; k < m.Index + m.Length; k++) wiki[k] = Hidden[k] = true;
            }

            WikiBefore = new int[n + 1];
            for (var k = 0; k < n; k++) WikiBefore[k + 1] = WikiBefore[k] + (wiki[k] ? 1 : 0);

            Close = new int[n];
            var open = new Stack<int>();
            for (var k = 0; k < n; k++)
            {
                Close[k] = -1;
                if (Escaped[k]) continue;
                if (line[k] == '[') open.Push(k);
                else if (line[k] == ']' && open.Count > 0) Close[open.Pop()] = k;
            }

            var depth = new int[n + 1];
            for (var k = 0; k < n; k++) depth[k + 1] = depth[k] + (Escaped[k] ? 0 : line[k] == '(' ? 1 : line[k] == ')' ? -1 : 0);
            // The depth moves by one at a time, so the first lower depth is the first of one less: found from the
            // right, by the last position seen at each depth (offset by n, as depths run from -n to n).
            DepthDrop = new int[n + 1];
            var seenAt = new int[2 * n + 2];
            Array.Fill(seenAt, -1);
            for (var k = n; k >= 0; k--)
            {
                DepthDrop[k] = depth[k] + n - 1 >= 0 ? seenAt[depth[k] + n - 1] : -1;
                seenAt[depth[k] + n] = k;
            }

            NextStop = Next(n, k => !Escaped[k] && (line[k] is ' ' or '\t' || char.IsControl(line[k])), n);
            NextAngle = Next(n, k => !Escaped[k] && line[k] is '<' or '>', -1);
            NextDouble = Next(n, k => !Escaped[k] && line[k] == '"', -1);
            NextSingle = Next(n, k => !Escaped[k] && line[k] == '\'', -1);
            NextParen = Next(n, k => !Escaped[k] && line[k] == ')', -1);
            NextNonSpace = Next(n, k => line[k] is not (' ' or '\t'), n);
        }

        private static int[] Next(int n, Func<int, bool> at, int none)
        {
            var next = new int[n + 1];
            next[n] = none;
            for (var k = n - 1; k >= 0; k--) next[k] = at(k) ? k : next[k + 1];
            return next;
        }
    }

    private static int SkipSpaces(string line, int i)
    {
        while (i < line.Length && line[i] is ' ' or '\t') i++;
        return i;
    }

    internal static string Decode(string path) => Uri.UnescapeDataString(EscapeRegex().Replace(path, "$1"));

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex SchemeRegex();

    // CommonMark: a backslash before ASCII punctuation stands for that character.
    [GeneratedRegex(@"\\([!-/:-@\[-`{-~])")]
    private static partial Regex EscapeRegex();
}
