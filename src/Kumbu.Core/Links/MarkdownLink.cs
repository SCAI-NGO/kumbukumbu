// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Links;

/// <summary>
/// An inline Markdown link to a note as written: <c>[text](destination "title")</c>. Keeps the exact source
/// text of each part, so a rewrite can change the path and leave everything else byte for byte.
/// </summary>
/// <param name="Line">0-based line of the note.</param>
/// <param name="Start">Character index of the link's '[' in that line.</param>
/// <param name="Target">The decoded path ("Meeting Notes.md" for <c>Meeting%20Notes.md</c>); empty for <c>(#Heading)</c>.</param>
/// <param name="Subpath">"#Heading", "#^block" or "", as written (still percent-encoded).</param>
/// <param name="Text">The link text with its brackets, e.g. "[see *this*]".</param>
/// <param name="Destination">The destination as written, angle brackets and subpath included.</param>
/// <param name="Rest">Everything after the destination up to and including ')': whitespace and title.</param>
public sealed partial record MarkdownLink(
    int Line, int Start, string Raw, string Target, string Subpath, string Text, string Destination, string Rest)
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

    /// <summary>All inline Markdown links to notes, in order. External links (with a scheme or "//"), images
    /// and reference-style links don't count, nor anything in fenced code blocks, inline code or a wikilink;
    /// frontmatter does, as for wikilinks.</summary>
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
        var code = CodeSpans(line);
        var wikilinks = WikiLink.LinkRegex().Matches(line).Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
        for (var start = 0; start < line.Length; start++)
        {
            if (line[start] != '[' || code.Concat(wikilinks).Any(s => start >= s.Start && start < s.End)) continue;
            if (start > 0 && line[start - 1] == '!' && !Escaped(line, start - 1)) continue;
            if (Escaped(line, start)) continue;
            if (TryParse(index, line, start) is not { } link) continue;
            // With a wikilink anywhere in it, "[see [[x]]](y.md)" or "[a](x[[y]].md)", Obsidian reads the
            // wikilink, not a Markdown link.
            var end = start + link.Raw.Length;
            if (wikilinks.Any(w => w.Start < end && start < w.End)) continue;
            result.Add(link);
            start += link.Raw.Length - 1;
        }
    }

    private static MarkdownLink? TryParse(int index, string line, int start)
    {
        // Link text: brackets balance, backslash escapes don't count.
        var i = start + 1;
        for (var depth = 0; i < line.Length; i++)
        {
            if (line[i] == '\\') i++;
            else if (line[i] == '[') depth++;
            else if (line[i] == ']' && depth-- == 0) break;
        }

        // No '(' right after ']': plain brackets or a reference-style link, neither is read.
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
            // Parentheses inside a bare destination must balance: "Plan%20(v2).md".
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
        if (inner.Length == 0 || SchemeRegex().IsMatch(inner) || inner.StartsWith("//", StringComparison.Ordinal)) return null;

        // The fragment starts at the first raw '#', so an encoded "%23" stays part of the name.
        var hash = inner.IndexOf('#');
        var path = hash >= 0 ? inner[..hash] : inner;
        var subpath = hash >= 0 ? inner[hash..] : "";
        if (path.Length == 0 && subpath.Length <= 1) return null;
        var raw = line[start..(i + 1)];
        return new MarkdownLink(index, start, raw, Decode(path), subpath, text,
            line[destinationStart..destinationEnd], line[destinationEnd..(i + 1)]);
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

    private static string Decode(string path) => Uri.UnescapeDataString(EscapeRegex().Replace(path, "$1"));

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex SchemeRegex();

    // CommonMark: a backslash before ASCII punctuation stands for that character.
    [GeneratedRegex(@"\\([!-/:-@\[-`{-~])")]
    private static partial Regex EscapeRegex();
}
