// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Links;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Publishing;

/// The copy as stored: Files[n] is what "![name](f/n)" in Markdown shows; Plain names the embedded attachments that
/// became plain text, distinct, in order of first appearance.
public sealed record FairCopy(string Title, string Markdown, IReadOnlyList<VaultPath> Files, IReadOnlyList<string> Plain);

/// <summary>
/// Makes the fair copy of a note for publishing (spec §3.3): no frontmatter, comments, tags, raw HTML or path of the
/// vault, links to notes as plain text, the attachments it may copy numbered. Code stays byte for byte. Pure.
/// </summary>
public static partial class FairCopies
{
    public const string FilePrefix = "f/";

    /// attachment: the attachment an embed's target names (by LinkResolver's rules), or null.
    /// mayCopy: whether that attachment goes with the copy (an image, not unpublishable, its bytes usable).
    /// ownOrigin: the server's own origin ("https://kumbu.example.org"), whose links count as vault links; null for none.
    public static FairCopy Make(VaultPath note, string content, Func<string, VaultPath?> attachment, Func<VaultPath, bool> mayCopy, string? ownOrigin = null)
    {
        var all = Lines.Split(content);
        var lines = WithoutComments([.. all.Skip(Outline.FrontmatterEnd(all))]);
        var fenced = Outline.CodeLines(lines);
        var indented = TagReader.IndentedCode(lines, 0, fenced);
        var code = fenced.Zip(indented, (f, i) => f || i).ToArray();
        var origin = ownOrigin is not null && Uri.TryCreate(ownOrigin, UriKind.Absolute, out var o) ? o : null;
        var (references, definitions) = References(lines, code, origin);
        var copier = new Copier(attachment, mayCopy, origin, references);

        var title = StripMd(note.Name);
        var start = 0;
        while (start < lines.Count && lines[start].Trim().Length == 0) start++;
        if (start < lines.Count && !code[start] && TitleRegex().Match(lines[start]) is { Success: true } heading
            && new Copier(attachment, _ => false, origin, references).Inline(heading.Groups[1].Value).Trim() is { Length: > 0 } text)
        {
            title = text;
            start++;
            if (start < lines.Count && lines[start].Trim().Length == 0) start++;
        }
        else start = 0;

        var result = new List<string>();
        for (var i = start; i < lines.Count; i++)
        {
            if (code[i])
            {
                result.Add(lines[i]);
                continue;
            }

            if (definitions.Contains(i)) continue;
            var line = copier.Inline(lines[i]);
            if (line.Trim().Length == 0 && lines[i].Trim().Length > 0) continue;
            result.Add(line);
        }

        var markdown = Lines.Join(result);
        if (result.Count > 0 && content.EndsWith('\n')) markdown += "\n";
        return new FairCopy(title, markdown, copier.Files, copier.Plain);
    }

    /// <summary>The lines without <c>%%…%%</c> and <c>&lt;!-- … --&gt;</c> outside code, and without the lines they
    /// leave empty. Repeated until nothing changes, as a removed comment can hide a fence that made code of what
    /// followed it.</summary>
    private static List<string> WithoutComments(List<string> lines)
    {
        while (true)
        {
            var fenced = Outline.CodeLines(lines);
            var indented = TagReader.IndentedCode(lines, 0, fenced);
            var result = new List<string>(lines.Count);
            string? comment = null;
            for (var i = 0; i < lines.Count; i++)
            {
                var open = comment is not null;
                if (!open && (fenced[i] || indented[i]))
                {
                    result.Add(lines[i]);
                    continue;
                }

                var line = StripComments(lines[i], ref comment);
                if (line.Trim().Length == 0 && (open || lines[i].Trim().Length > 0)) continue;
                result.Add(line);
            }

            if (result.SequenceEqual(lines)) return result;
            lines = result;
        }
    }

    private static string StripComments(string line, ref string? comment)
    {
        if (comment is null && !line.Contains("%%", StringComparison.Ordinal) && !line.Contains("<!--", StringComparison.Ordinal)) return line;
        var spans = NoteLink.CodeSpans(line);
        var result = new StringBuilder(line.Length);
        var p = 0;
        while (p < line.Length)
        {
            if (comment is not null)
            {
                var close = line.IndexOf(comment, p, StringComparison.Ordinal);
                if (close < 0) break;
                p = close + comment.Length;
                comment = null;
                continue;
            }

            if (spans.FirstOrDefault(s => s.Start == p) is { End: > 0 } span)
            {
                result.Append(line, p, span.End - p);
                p = span.End;
            }
            else if (string.CompareOrdinal(line, p, "<!--", 0, 4) == 0)
            {
                comment = "-->";
                p += 4;
            }
            else if (string.CompareOrdinal(line, p, "%%", 0, 2) == 0)
            {
                comment = "%%";
                p += 2;
            }
            else result.Append(line[p++]);
        }

        return result.ToString();
    }

    /// <summary>The reference definitions outside code, by normalised label (the first one counts), and the lines of
    /// those that go: every one whose destination isn't a web link.</summary>
    private static (Dictionary<string, string> References, HashSet<int> Lines) References(IReadOnlyList<string> lines, bool[] code, Uri? origin)
    {
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        var gone = new HashSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (code[i] || DefinitionRegex().Match(lines[i]) is not { Success: true } m) continue;
            var used = new List<int> { i };
            var destination = m.Groups[2].Value;
            // The destination may stand on the next line.
            if (destination.Length == 0)
            {
                if (i + 1 >= lines.Count || code[i + 1] || lines[i + 1].Trim().Length == 0) continue;
                destination = lines[i + 1].Trim().Split([' ', '\t'])[0];
                used.Add(i + 1);
            }

            destination = Unbracket(destination);
            references.TryAdd(Label(m.Groups[1].Value), destination);
            if (Classify(destination, origin) != Kind.Web) gone.UnionWith(used);
        }

        return (references, gone);
    }

    private static string Label(string label) => WhitespaceRegex().Replace(label.Trim(), " ").ToUpperInvariant();

    private static string Unbracket(string destination) =>
        destination.Length >= 2 && destination[0] == '<' && destination[^1] == '>' ? destination[1..^1] : destination;

    private static string StripMd(string name) => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;

    private enum Kind { Vault, Web, Own, Other }

    private static Kind Classify(string destination, Uri? origin)
    {
        var protocolRelative = destination.StartsWith("//", StringComparison.Ordinal);
        var scheme = SchemeRegex().Match(destination);
        if (!protocolRelative && !scheme.Success) return Kind.Vault;
        if (!protocolRelative && scheme.Groups[1].Value.Equals("mailto", StringComparison.OrdinalIgnoreCase)) return Kind.Web;
        if (!protocolRelative && !scheme.Groups[1].Value.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !scheme.Groups[1].Value.Equals("https", StringComparison.OrdinalIgnoreCase)) return Kind.Other;
        var url = protocolRelative ? (origin?.Scheme ?? "https") + ":" + destination : destination;
        // What doesn't parse can't be shown to be elsewhere.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return Kind.Other;
        return origin is not null && uri.Scheme.Equals(origin.Scheme, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals(origin.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == origin.Port
            ? Kind.Own
            : Kind.Web;
    }

    private sealed class Copier(Func<string, VaultPath?> attachment, Func<VaultPath, bool> mayCopy, Uri? origin, Dictionary<string, string> references)
    {
        private readonly Dictionary<VaultPath, int> _numbers = [];

        public List<VaultPath> Files { get; } = [];

        public List<string> Plain { get; } = [];

        /// <summary>One line, or the text of a link, filtered: code spans stay as written.</summary>
        public string Inline(string text)
        {
            var spans = NoteLink.CodeSpans(text);
            var wikilinks = WikiLink.LinkRegex().Matches(text).ToDictionary(m => m.Index);
            var html = text.Contains('<') ? TagReader.HtmlTagRegex().Matches(text).ToDictionary(m => m.Index) : [];
            var autolinks = text.Contains('<') ? AutolinkRegex().Matches(text).ToDictionary(m => m.Index) : [];
            var result = new StringBuilder(text.Length);
            var p = 0;
            while (p < text.Length)
            {
                var c = text[p];
                if (c == '`' && spans.FirstOrDefault(s => s.Start == p) is { End: > 0 } span)
                {
                    result.Append(text, p, span.End - p);
                    p = span.End;
                }
                else if (c == '\\' && p + 1 < text.Length && text[p + 1] == '#')
                {
                    result.Append(text, p, 2);
                    p += 2;
                }
                else if (autolinks.TryGetValue(p, out var autolink))
                {
                    // Its text is the URL: it stays whole or goes whole.
                    if (Classify(autolink.Groups[1].Value, origin) == Kind.Web) result.Append(autolink.Value);
                    p += autolink.Length;
                }
                else if (html.TryGetValue(p, out var tag)) p += tag.Length;
                else if (wikilinks.TryGetValue(p, out var wiki) && WikiText(wiki) is { } wikiText)
                {
                    result.Append(wikiText);
                    p += wiki.Length;
                }
                else if (c == '[' || (c == '!' && p + 1 < text.Length && text[p + 1] == '['))
                {
                    var embed = c == '!';
                    var open = embed ? p + 1 : p;
                    if (MarkdownLink.TryParse(0, text, open, embed, external: true) is { } link)
                    {
                        var tail = link.Raw[((embed ? 1 : 0) + link.Text.Length)..];
                        result.Append(Link(link.Text[1..^1], Unbracket(link.Destination), embed, tail));
                        p += link.Raw.Length;
                    }
                    else if (Reference(text, open) is { } reference)
                    {
                        result.Append(Link(reference.Text, reference.Destination, embed, text[(open + reference.TextLength)..reference.End]));
                        p = reference.End;
                    }
                    else result.Append(text[p++]);
                }
                else if (c == '#' && (p == 0 || char.IsWhiteSpace(text[p - 1])) && TagEnd(text, p) is var end && end > 0)
                {
                    if (Alone(text, end))
                    {
                        while (result.Length > 0 && char.IsWhiteSpace(result[^1])) result.Length--;
                    }
                    else result.Append(text, p + 1, end - p - 1);

                    p = end;
                }
                else result.Append(text[p++]);
            }

            return result.ToString();
        }

        /// <summary>A link or image to <paramref name="destination"/> (unbracketed), as the copy shows it;
        /// <paramref name="tail"/> is what follows its text as written: "(destination title)" or "[label]".</summary>
        private string Link(string text, string destination, bool embed, string tail)
        {
            var shown = Inline(text);
            switch (Classify(destination, origin))
            {
                case Kind.Web:
                    // A remote image becomes a link: the reader's browser fetches nothing from third parties.
                    return $"[{(embed && shown.Length == 0 ? destination : shown)}]{tail}";
                case Kind.Vault when embed:
                    var hash = destination.IndexOf('#');
                    return Embed(MarkdownLink.Decode(hash >= 0 ? destination[..hash] : destination), shown);
                default:
                    return shown;
            }
        }

        /// <summary>The text a wikilink or wiki embed leaves, or null for one that names nothing.</summary>
        private string? WikiText(Match wiki)
        {
            var inner = wiki.Groups[2].Value;
            var alias = "";
            var bar = inner.IndexOf('|');
            if (bar >= 0)
            {
                alias = inner[(bar + 1)..];
                inner = inner[..(bar > 0 && inner[bar - 1] == '\\' ? bar - 1 : bar)];
            }

            var hash = inner.IndexOf('#');
            var target = hash >= 0 ? inner[..hash] : inner;
            var subpath = hash >= 0 ? inner[(hash + 1)..] : "";
            if (target.Trim().Length == 0 && subpath.Length == 0) return null;
            var embed = wiki.Groups[1].Length > 0;
            // "![[shot.png|300]]" gives a size, not a text.
            if (embed && SizeRegex().IsMatch(alias)) alias = "";
            var shown = alias.Length > 0 ? Inline(alias)
                : target.Length == 0 ? subpath.TrimStart('^')
                : StripMd(target.TrimEnd('/').Split('/')[^1]);
            return embed && target.Length > 0 ? Embed(target, "", shown) : shown;
        }

        /// <summary>An embed of <paramref name="target"/>: the attachment by number if it may go with the copy, else
        /// plain text: <paramref name="alt"/>, <paramref name="plain"/> or its name.</summary>
        private string Embed(string target, string alt, string? plain = null)
        {
            var file = attachment(target);
            if (file is not null && mayCopy(file))
            {
                if (!_numbers.TryGetValue(file, out var n))
                {
                    _numbers[file] = n = Files.Count;
                    Files.Add(file);
                }

                return $"![{Escape(alt.Length > 0 ? alt : file.Name)}]({FilePrefix}{n})";
            }

            var name = file?.Name ?? target.TrimEnd('/').Split('/')[^1];
            var attached = file is not null || (Path.GetExtension(name) is { Length: > 1 } ext && !ext.Equals(".md", StringComparison.OrdinalIgnoreCase));
            if (attached && !Plain.Contains(name)) Plain.Add(name);
            return alt.Length > 0 ? alt : plain ?? (attached ? name : StripMd(name));
        }

        /// <summary>A reference-style use at '[' <paramref name="open"/> whose label has a definition: full
        /// <c>[text][label]</c>, collapsed <c>[label][]</c> or shortcut <c>[label]</c>.</summary>
        private (string Text, int TextLength, string Destination, int End)? Reference(string text, int open)
        {
            if (Bracketed(text, open) is not { } close) return null;
            var inner = text[(open + 1)..close];
            var label = inner;
            var end = close + 1;
            if (end < text.Length && text[end] == '[' && Bracketed(text, end) is { } second)
            {
                if (second > end + 1) label = text[(end + 1)..second];
                end = second + 1;
            }

            return references.TryGetValue(Label(label), out var destination) ? (inner, close + 1 - open, destination, end) : null;
        }

        /// <summary>The ']' closing the '[' at <paramref name="open"/>, brackets balanced, escapes skipped.</summary>
        private static int? Bracketed(string text, int open)
        {
            for (int i = open + 1, depth = 0; i < text.Length; i++)
            {
                if (text[i] == '\\') i++;
                else if (text[i] == '[') depth++;
                else if (text[i] == ']' && depth-- == 0) return i;
            }

            return null;
        }

        private static string Escape(string text) => text.Replace("[", "\\[").Replace("]", "\\]");
    }

    /// <summary>The end of the tag whose '#' is at <paramref name="p"/>, or 0 if none starts there.</summary>
    private static int TagEnd(string text, int p)
    {
        var stop = p + 1;
        while (stop < text.Length && TagReader.IsTagChar(text[stop])) stop++;
        return TagReader.IsTag(text[(p + 1)..stop].TrimEnd('/')) ? stop : 0;
    }

    /// <summary>Whether nothing but whitespace and other tags follows <paramref name="from"/> to the end.</summary>
    private static bool Alone(string text, int from)
    {
        var p = from;
        while (true)
        {
            while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
            if (p == text.Length) return true;
            var end = text[p] == '#' ? TagEnd(text, p) : 0;
            if (end == 0) return false;
            p = end;
        }
    }

    [GeneratedRegex(@"^ {0,3}#[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"^ {0,3}\[((?:[^\[\]\\]|\\.)+)\]:[ \t]*(<[^<>]*>|\S*)")]
    private static partial Regex DefinitionRegex();

    [GeneratedRegex(@"<([A-Za-z][A-Za-z0-9+.-]{1,31}:[^\s<>]*)>")]
    private static partial Regex AutolinkRegex();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.-]*):")]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"^\d+(x\d+)?$")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
