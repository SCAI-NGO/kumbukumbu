// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Core.Board;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Tags;

/// <summary>
/// Sets or removes a note's flag in its frontmatter's <c>tags</c>, line by line: only the flag items change, every other
/// byte stays (quoting, comments, the list's form, each line's ending). <see cref="Frontmatter.Set"/> would rewrite the
/// key's whole extent.
/// </summary>
public static partial class FlagTags
{
    /// <summary>
    /// The content with every flag tag removed from the frontmatter's <c>tags</c> and <paramref name="flag"/>'s tag in the
    /// place of the first one (appended if there was none); null when <c>tags</c> has a form this can't edit (a map, a
    /// nested list, a value over several lines), the frontmatter doesn't parse before or after, a key other than <c>tags</c>
    /// would read differently after (a line that only looks like <c>tags:</c>, inside another key's quoted value), or the note
    /// starts with a byte order mark. A <c>tags</c> left empty is removed, and a frontmatter left empty with it, so setting a flag and
    /// removing it gives the note back.
    /// </summary>
    public static string? Set(string content, string? flag)
    {
        var tag = flag is null ? null : Flags.Tag(flag);
        // Neither TagReader nor Frontmatter.Read sees frontmatter behind a byte order mark, so neither the tags this would
        // edit nor the result could be read back.
        if (content.StartsWith('\uFEFF')) return null;
        var lines = Split(content);
        var end = Outline.FrontmatterEnd(lines.Select(l => l.Text).ToList());
        var eol = lines[0].End + "\n";
        if (end == 0) return tag is null ? content : $"---{eol}tags: [{tag}]{eol}---{eol}" + content;
        if (Frontmatter.Read(content).Problem is not null) return null;

        var close = end - 1;
        var key = -1;
        Match keyMatch = Match.Empty;
        for (var i = 1; i < close && key < 0; i++)
        {
            keyMatch = TagsKeyRegex().Match(lines[i].Text);
            if (keyMatch.Success) key = i;
        }

        if (key < 0)
        {
            if (tag is null) return content;
            lines.Insert(close, ($"tags: [{tag}]", lines[0].End));
            return Parsed(content, Join(lines));
        }

        // The key's extent is its line, the lines indented under it or starting with '-', and the blank lines between: as
        // Frontmatter.Set's, except that a line such as "-x" counts too, so the block is refused rather than cut short.
        var stop = key + 1;
        for (var next = stop; next < close; next++)
        {
            if (IsContinuation(lines[next].Text)) stop = next + 1;
            else if (lines[next].Text.Trim().Length > 0) break;
        }

        var text = lines[key].Text;
        var colon = keyMatch.Length;
        var value = colon;
        while (value < text.Length && text[value] is ' ' or '\t') value++;
        var bare = value == text.Length || text[value] == '#';

        bool emptied;
        if (stop > key + 1)
        {
            if (!bare) return null;
            if (Block(lines, key + 1, stop, tag) is not { } left) return null;
            emptied = left;
        }
        else
        {
            var changed = bare ? Empty(text, colon, text[value..], tag) : text[value] == '[' ? Flow(text, value, tag) : Scalar(text, colon, value, tag);
            if (changed is null) return null;
            emptied = changed.Length == 0;
            if (!emptied) lines[key] = (changed, lines[key].End);
        }

        if (emptied)
        {
            lines.RemoveRange(key, stop - key);
            close -= stop - key;
            if (close == 1) lines.RemoveRange(0, 2);
        }

        return Parsed(content, Join(lines));
    }

    /// <summary>The result, or null when its frontmatter no longer parses (a line added beside an indented map) or a key other
    /// than <c>tags</c> no longer reads as it did (the line edited was inside another key's value).</summary>
    private static string? Parsed(string content, string result)
    {
        var after = Frontmatter.Read(result);
        if (after.Problem is not null) return null;
        var before = Frontmatter.Read(content).Values;
        var others = before.Keys.Union(after.Values.Keys).Where(k => k != "tags");
        return others.All(k => before.TryGetValue(k, out var a) && after.Values.TryGetValue(k, out var b) && FrontmatterWrites.Same(a, b)) ? result : null;
    }

    /// <summary><c>tags:</c> with no value (or null, or ""): the flag as a flow list before any comment, or nothing to remove.</summary>
    private static string Empty(string text, int colon, string comment, string? tag) =>
        tag is null ? text : $"{text[..colon]} [{tag}]" + (comment.Length > 0 ? " " + comment : "");

    /// <summary>A flow list on the key's line, <c>tags: [a, "b"] # c</c>: the new line, "" when no item is left, null when refused.</summary>
    private static string? Flow(string text, int open, string? tag)
    {
        var pieces = new List<string>();
        var start = open + 1;
        var close = -1;
        char? quote = null;
        for (var i = open + 1; i < text.Length && close < 0; i++)
        {
            var c = text[i];
            if (quote == '"')
            {
                if (c == '\\') i++;
                else if (c == '"') quote = null;
                continue;
            }

            if (quote == '\'')
            {
                if (c == '\'' && i + 1 < text.Length && text[i + 1] == '\'') i++;
                else if (c == '\'') quote = null;
                continue;
            }

            switch (c)
            {
                case '"' or '\'' when text[start..i].Trim().Length == 0:
                    quote = c;
                    break;
                case '[' or '{':
                    return null;
                case '#' when text[i - 1] is ' ' or '\t':
                    return null;
                case ',':
                    pieces.Add(text[start..i]);
                    start = i + 1;
                    break;
                case ']':
                    pieces.Add(text[start..i]);
                    close = i;
                    break;
            }
        }

        // A list over several lines never closes on this one.
        if (close < 0) return null;
        var after = text[(close + 1)..].TrimStart(' ', '\t');
        if (after.Length > 0 && after[0] != '#') return null;
        if (pieces is [var only] && only.Trim().Length == 0) pieces.Clear();

        var values = new List<string?>();
        foreach (var piece in pieces)
        {
            if (Item(piece, 0) is not var (s, e, _)) return null;
            if (s == e) return null;
            values.Add(piece[s..e]);
        }

        if (Plan(values, tag) is not var (replace, remove)) return text;
        if (replace >= 0)
        {
            var (s, e, _) = Item(pieces[replace], 0)!.Value;
            pieces[replace] = pieces[replace][..s] + tag + pieces[replace][e..];
        }

        // An item goes with the comma before it, the first with the one after; the spacing at the list's ends stays.
        foreach (var j in remove.OrderDescending())
        {
            if (j == 0 && pieces.Count > 1) pieces[1] = Leading(pieces[0]) + pieces[1].TrimStart(' ', '\t');
            else if (j > 0 && j == pieces.Count - 1) pieces[j - 1] = pieces[j - 1].TrimEnd(' ', '\t') + Trailing(pieces[j]);
            pieces.RemoveAt(j);
        }

        if (tag is not null && replace < 0)
        {
            if (pieces.Count == 0) pieces.Add(tag);
            else pieces[^1] = pieces[^1].TrimEnd(' ', '\t') + ", " + tag + Trailing(pieces[^1]);
        }

        return pieces.Count == 0 ? "" : text[..(open + 1)] + string.Join(",", pieces) + text[close..];
    }

    /// <summary>The string form, <c>tags: a, b</c> or <c>tags: a b</c> (quoted or not): it stays a string.</summary>
    private static string? Scalar(string text, int colon, int value, string? tag)
    {
        if (Item(text, value) is not var (s, e, quote)) return null;
        var inner = text[s..e];
        var comment = text[(quote == '\0' ? e : e + 1)..].TrimStart(' ', '\t');
        if (quote == '\0' && inner is "~" or "null" or "Null" or "NULL") return Empty(text, colon, comment, tag);

        var tokens = new List<string>();
        var separators = new List<string>();
        var first = -1;
        var last = 0;
        foreach (Match m in TokenRegex().Matches(inner))
        {
            if (first < 0) first = m.Index;
            else separators.Add(inner[last..m.Index]);
            tokens.Add(m.Value);
            last = m.Index + m.Length;
        }

        if (tokens.Count == 0) return Empty(text, colon, comment, tag);
        if (Plan(tokens, tag) is not var (replace, remove)) return text;
        if (replace >= 0) tokens[replace] = tag!;
        foreach (var j in remove.OrderDescending())
        {
            if (separators.Count > 0) separators.RemoveAt(j > 0 ? j - 1 : 0);
            tokens.RemoveAt(j);
        }

        if (tag is not null && replace < 0)
        {
            separators.Add(inner.Contains(',') || tokens.Count == 1 ? ", " : " ");
            tokens.Add(tag);
        }

        if (tokens.Count == 0) return "";
        var joined = tokens[0] + string.Concat(separators.Select((sep, i) => sep + tokens[i + 1]));
        return text[..(s + first)] + joined + text[(s + last)..];
    }

    /// <summary>A block list, <c>tags:</c> then <c>  - a</c> lines: edits the item lines in place; true when no item is left
    /// (the caller removes the key), null when refused.</summary>
    private static bool? Block(List<(string Text, string End)> lines, int from, int stop, string? tag)
    {
        var items = new List<(int Line, int Start, int End)>();
        string? indent = null;
        string? marker = null;
        for (var i = from; i < stop; i++)
        {
            var text = lines[i].Text;
            if (text.Trim().Length == 0) continue;
            var m = BlockItemRegex().Match(text);
            // Anything but items at one indent is a nested list, a map or a value over several lines.
            if (!m.Success || (indent ??= m.Groups[1].Value) != m.Groups[1].Value) return null;
            if (m.Length == text.Length)
            {
                items.Add((i, text.Length, text.Length));
                continue;
            }

            marker ??= m.Value;
            if (Item(text, m.Length) is not var (s, e, _)) return null;
            items.Add((i, s, e));
        }

        var values = items.Select(it => it.Start == it.End ? null : lines[it.Line].Text[it.Start..it.End]).ToList();
        if (Plan(values, tag) is not var (replace, remove)) return false;
        if (tag is null && remove.Count == items.Count) return true;

        if (replace >= 0)
        {
            var (line, s, e) = items[replace];
            lines[line] = (lines[line].Text[..s] + tag + lines[line].Text[e..], lines[line].End);
        }
        else if (tag is not null)
        {
            var at = items[^1].Line;
            lines.Insert(at + 1, ((marker ?? "  - ") + tag, lines[at].End));
        }

        foreach (var j in remove.OrderDescending()) lines.RemoveAt(items[j].Line);
        return false;
    }

    /// <summary>The first flag item takes the new flag in its place and the others go; without one, the flag is appended
    /// (Replace -1). Null when there is nothing to do.</summary>
    private static (int Replace, List<int> Remove)? Plan(IReadOnlyList<string?> values, string? tag)
    {
        var flags = Enumerable.Range(0, values.Count)
            .Where(i => values[i] is { } v && TagMatch.Normalise(v) is { } t && Flags.NameOf(t) is not null)
            .ToList();
        if (flags.Count == 0) return tag is null ? null : (-1, []);
        return tag is null ? (-1, flags) : (flags[0], flags[1..]);
    }

    /// <summary>
    /// A YAML scalar starting at or after <paramref name="from"/>, up to a comment: the span of its text inside any quotes
    /// and the quote ('\0' for none); null for what isn't a plain string (a map, a nested list, a block scalar, an alias,
    /// an escape).
    /// </summary>
    private static (int Start, int End, char Quote)? Item(string text, int from)
    {
        var s = from;
        while (s < text.Length && text[s] is ' ' or '\t') s++;
        if (s == text.Length || text[s] == '#') return (s, s, '\0');

        var c = text[s];
        if (c is '"' or '\'')
        {
            var e = text.IndexOf(c, s + 1);
            if (e < 0 || text[(s + 1)..e].Contains('\\') || (e + 1 < text.Length && text[e + 1] == c)) return null;
            var after = text[(e + 1)..].TrimStart(' ', '\t');
            return after.Length == 0 || after[0] == '#' ? (s + 1, e, c) : null;
        }

        if (c is '[' or ']' or '{' or '}' or '|' or '>' or '&' or '*' or '!' or '%' or '@' or '`' or '?') return null;
        if (c == '-' && (s + 1 == text.Length || text[s + 1] is ' ' or '\t')) return null;

        var end = text.Length;
        for (var i = s + 1; i < text.Length; i++)
        {
            if (text[i] == '#' && text[i - 1] is ' ' or '\t')
            {
                end = i;
                break;
            }
        }

        while (end > s && text[end - 1] is ' ' or '\t') end--;
        var plain = text[s..end];
        if (plain.EndsWith(':') || plain.Contains(": ", StringComparison.Ordinal) || plain.Contains(":\t", StringComparison.Ordinal)) return null;
        return (s, end, '\0');
    }

    private static bool IsContinuation(string line) =>
        line.Trim().Length > 0 && (line.StartsWith(' ') || line.StartsWith('\t') || line.StartsWith('-'));

    private static string Leading(string piece) => piece[..(piece.Length - piece.TrimStart(' ', '\t').Length)];

    private static string Trailing(string piece) => piece[piece.TrimEnd(' ', '\t').Length..];

    /// <summary>The lines without their '\n', each with its '\r' (or nothing) apart, so an edited line keeps its ending.</summary>
    private static List<(string Text, string End)> Split(string content) =>
        content.Split('\n').Select(l => l.EndsWith('\r') ? (l[..^1], "\r") : (l, "")).ToList();

    private static string Join(List<(string Text, string End)> lines) => string.Join('\n', lines.Select(l => l.Text + l.End));

    // Stricter than TagReader's, which only decides whether to parse the YAML at all: this one finds the line to edit, so the
    // colon must end the key (in YAML, "tags:x" is not the key tags).
    [GeneratedRegex(@"^(tags|""tags""|'tags')[ \t]*:(?=[ \t]|$)")]
    private static partial Regex TagsKeyRegex();

    [GeneratedRegex(@"^([ \t]*)-([ \t]+|$)")]
    private static partial Regex BlockItemRegex();

    // Obsidian splits the string form on commas and whitespace.
    [GeneratedRegex(@"[^,\s]+")]
    private static partial Regex TokenRegex();
}
