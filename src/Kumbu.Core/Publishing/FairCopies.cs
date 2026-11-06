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
/// vault, links to notes as plain text, the attachments it may copy numbered. Code stays byte for byte. Pure, and
/// linear in the note's length, whatever its brackets. When in doubt it takes out more rather than less.
/// </summary>
public static partial class FairCopies
{
    public const string FilePrefix = "f/";

    // Past this many passes, comments go wherever they stand, code or not.
    private const int CommentPasses = 8;

    // A longer destination is no link: the safety net then takes it out.
    private const int MaxDestination = 4096;

    // CommonMark's limit for a link label.
    private const int MaxLabel = 999;

    /// attachment: the attachment an embed's target names (by LinkResolver's rules), or null.
    /// mayCopy: whether that attachment goes with the copy (an image, not unpublishable, its bytes usable).
    /// ownOrigin: the server's own origin ("https://kumbu.example.org"), whose links count as vault links; null for none.
    public static FairCopy Make(VaultPath note, string content, Func<string, VaultPath?> attachment, Func<VaultPath, bool> mayCopy, string? ownOrigin = null)
    {
        var all = Lines.Split(content);
        var lines = WithoutComments([.. all.Skip(Outline.FrontmatterEnd(all))]);
        var code = CodeLines(lines);
        var host = OwnHost(ownOrigin);
        var (references, definitions) = References(lines, code, host);
        var copier = new Copier(attachment, mayCopy, host, references);

        var title = StripMd(note.Name);
        var start = 0;
        while (start < lines.Count && lines[start].Trim().Length == 0) start++;
        if (start < lines.Count && !code[start] && !definitions.ContainsKey(start) && TitleRegex().Match(lines[start]) is { Success: true } heading
            && copier.Inline(heading.Groups[1].Value, flat: true).Trim() is { Length: > 0 } text)
        {
            title = text;
            start++;
            if (start < lines.Count && lines[start].Trim().Length == 0) start++;
        }
        else start = 0;

        var result = new List<string>();
        for (var i = start; i < lines.Count;)
        {
            if (code[i] || lines[i].Trim().Length == 0)
            {
                result.Add(lines[i++]);
                continue;
            }

            if (definitions.TryGetValue(i, out var kept))
            {
                if (kept) result.Add(lines[i]);
                i++;
                continue;
            }

            // Links and HTML tags may run over the lines of a paragraph.
            var end = i + 1;
            while (end < lines.Count && !code[end] && lines[end].Trim().Length > 0 && !definitions.ContainsKey(end) && !Breaks(lines[end - 1], lines[end])) end++;
            var paragraph = lines.GetRange(i, end - i);
            var filtered = copier.Inline(string.Join('\n', paragraph), flat: false).Split('\n');
            var aligned = filtered.Length == paragraph.Count;
            for (var k = 0; k < filtered.Length; k++)
            {
                if (Empty(filtered[k]) && !(aligned && filtered[k] == paragraph[k])) continue;
                result.Add(filtered[k]);
            }

            i = end;
        }

        while (result.Count > 0 && result[0].Trim().Length == 0) result.RemoveAt(0);
        var markdown = Lines.Join(result);
        if (result.Count > 0 && content.EndsWith('\n')) markdown += "\n";
        return new FairCopy(title, markdown, copier.Files, copier.Plain);
    }

    private static bool[] CodeLines(IReadOnlyList<string> lines)
    {
        var fenced = Outline.CodeLines(lines);
        var indented = TagReader.IndentedCode(lines, 0, fenced);
        return [.. fenced.Zip(indented, (f, i) => f || i)];
    }

    /// <summary>The lines without <c>%%…%%</c> and <c>&lt;!-- … --&gt;</c> outside code, and without the lines they
    /// leave empty. Repeated until nothing changes, as a removed comment can hide a fence that made code of what
    /// followed it.</summary>
    private static List<string> WithoutComments(List<string> lines)
    {
        for (var pass = 0; ; pass++)
        {
            var anywhere = pass >= CommentPasses;
            var code = anywhere ? new bool[lines.Count] : CodeLines(lines);
            var result = new List<string>(lines.Count);
            string? comment = null;
            for (var i = 0; i < lines.Count; i++)
            {
                var open = comment is not null;
                if (!open && code[i])
                {
                    result.Add(lines[i]);
                    continue;
                }

                var line = StripComments(lines[i], ref comment, anywhere);
                if (Empty(line) && (open || line != lines[i])) continue;
                result.Add(line);
            }

            if (result.SequenceEqual(lines)) return result;
            lines = result;
        }
    }

    private static string StripComments(string line, ref string? comment, bool anywhere)
    {
        if (comment is null && !line.Contains("%%", StringComparison.Ordinal) && !line.Contains("<!--", StringComparison.Ordinal)) return line;
        var spans = anywhere ? [] : CodeSpans(line);
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

            if (spans.TryGetValue(p, out var end))
            {
                result.Append(line, p, end - p);
                p = end;
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

    /// <summary>
    /// Code spans, start to end: a run of backticks up to the next run of the same length (CommonMark). A backslash
    /// before a run takes its first backtick out of it. Linear.
    /// </summary>
    private static Dictionary<int, int> CodeSpans(string s)
    {
        var spans = new Dictionary<int, int>();
        if (!s.Contains('`')) return spans;
        var runs = new List<(int Start, int Length, bool Escaped)>();
        for (var i = 0; i < s.Length;)
        {
            if (s[i] != '`')
            {
                i++;
                continue;
            }

            var j = i;
            while (j < s.Length && s[j] == '`') j++;
            var backslashes = 0;
            while (i - backslashes - 1 >= 0 && s[i - backslashes - 1] == '\\') backslashes++;
            runs.Add((i, j - i, backslashes % 2 == 1));
            i = j;
        }

        var byLength = new Dictionary<int, List<int>>();
        for (var k = 0; k < runs.Count; k++)
        {
            if (!byLength.TryGetValue(runs[k].Length, out var list)) byLength[runs[k].Length] = list = [];
            list.Add(k);
        }

        for (var k = 0; k < runs.Count;)
        {
            var (start, length, escaped) = runs[k];
            if (escaped)
            {
                start++;
                length--;
            }

            var closer = -1;
            if (length > 0 && byLength.TryGetValue(length, out var candidates))
            {
                var at = candidates.BinarySearch(k + 1);
                at = at >= 0 ? at : ~at;
                if (at < candidates.Count) closer = candidates[at];
            }

            if (closer < 0)
            {
                k++;
                continue;
            }

            spans[start] = runs[closer].Start + runs[closer].Length;
            k = closer + 1;
        }

        return spans;
    }

    /// <summary>The reference definitions outside code, by normalised label (the first one counts), and every line a
    /// definition takes, with whether it stays: only one to a web link does. Footnotes (<c>[^1]:</c>) are none.</summary>
    private static (Dictionary<string, string> References, Dictionary<int, bool> Lines) References(List<string> lines, bool[] code, string? host)
    {
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new Dictionary<int, bool>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (code[i] || DefinitionRegex().Match(lines[i]) is not { Success: true } m || m.Groups["label"].Value.StartsWith('^')) continue;
            var used = new List<int> { i };
            var rest = m.Groups["rest"].Value.Trim();
            var j = i;
            // The destination may stand on the next line, the title too.
            if (rest.Length == 0)
            {
                if (i + 1 >= lines.Count || code[i + 1] || Continuation(lines[i + 1]).Length == 0) continue;
                rest = Continuation(lines[++j]);
                used.Add(j);
            }

            var d = DestinationRegex().Match(rest);
            var title = d.Groups[2].Value.Trim();
            if (title.Length == 0 && j + 1 < lines.Count && !code[j + 1] && Continuation(lines[j + 1]) is { Length: > 0 } next && next[0] is '"' or '\'' or '(')
            {
                title = next;
                used.Add(++j);
            }

            if (title.Length > 0 && title[0] is '"' or '\'' or '(')
            {
                var close = title[0] == '(' ? ')' : title[0];
                var closed = title.IndexOf(close, 1) >= 0;
                while (!closed && j + 1 < lines.Count && !code[j + 1] && lines[j + 1].Trim().Length > 0)
                {
                    closed = lines[++j].Contains(close);
                    used.Add(j);
                }
            }

            var destination = Unbracket(d.Groups[1].Value);
            references.TryAdd(Label(m.Groups["label"].Value), destination);
            var keep = Classify(destination, host) == Kind.Web;
            foreach (var line in used) taken[line] = keep;
            i = j;
        }

        return (references, taken);
    }

    /// <summary>A line without its container markers ('>' and indentation).</summary>
    private static string Continuation(string line) => line.TrimStart(' ', '\t', '>').Trim();

    private static string Label(string label) => WhitespaceRegex().Replace(label.Trim(), " ").ToUpperInvariant().ToLowerInvariant();

    private static string Unbracket(string destination) =>
        destination.Length >= 2 && destination[0] == '<' && destination[^1] == '>' ? destination[1..^1] : destination;

    private static string StripMd(string name) => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;

    /// <summary>Whether <paramref name="line"/> starts a block of its own after <paramref name="previous"/>: a list
    /// item, a heading, a thematic break, another depth of quote.</summary>
    private static bool Breaks(string previous, string line) =>
        ListItemRegex().IsMatch(line) || HeadingLineRegex().IsMatch(line) || HeadingLineRegex().IsMatch(previous)
        || ThematicRegex().IsMatch(line) || ThematicRegex().IsMatch(previous) || QuoteDepth(previous) != QuoteDepth(line);

    private static int QuoteDepth(string line)
    {
        var depth = 0;
        foreach (var c in line)
        {
            if (c == '>') depth++;
            else if (c is not (' ' or '\t')) break;
        }

        return depth;
    }

    /// <summary>Nothing but whitespace, quote and list markers, a task box.</summary>
    private static bool Empty(string line) => line.Trim().Length == 0 || MarkersRegex().IsMatch(line);

    private static string? OwnHost(string? origin) => origin is not null && Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? Host(uri) : null;

    // The same host is the same server: scheme and port don't count, nor a trailing dot or "www.".
    private static string Host(Uri uri)
    {
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private enum Kind { Vault, Web, Own, Other }

    private static Kind Classify(string destination, string? host)
    {
        var protocolRelative = destination.StartsWith("//", StringComparison.Ordinal);
        var scheme = SchemeRegex().Match(destination);
        if (!protocolRelative && !scheme.Success) return Kind.Vault;
        var name = scheme.Groups[1].Value;
        if (!protocolRelative && name.Equals("mailto", StringComparison.OrdinalIgnoreCase)) return Kind.Web;
        if (!protocolRelative && !name.Equals("http", StringComparison.OrdinalIgnoreCase) && !name.Equals("https", StringComparison.OrdinalIgnoreCase)) return Kind.Other;
        // What doesn't parse can't be shown to be elsewhere.
        if (!Uri.TryCreate(protocolRelative ? "https:" + destination : destination, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return Kind.Other;
        return host is not null && Host(uri) == host ? Kind.Own : Kind.Web;
    }

    /// <summary>Text with ranges that stay as they are: code, kept web links and autolinks, numbered embeds.</summary>
    private sealed class Output
    {
        public StringBuilder Text { get; } = new();

        public List<(int Start, int End)> Kept { get; } = [];

        public void Append(string text, bool kept = false)
        {
            if (kept && text.Length > 0) Kept.Add((Text.Length, Text.Length + text.Length));
            Text.Append(text);
        }

        public void Append(char c) => Text.Append(c);

        /// <summary>Takes off spaces and tabs at the end, never kept text nor a line break.</summary>
        public void TrimEnd()
        {
            var floor = Kept.Count > 0 ? Kept[^1].End : 0;
            while (Text.Length > floor && Text[^1] is ' ' or '\t') Text.Length--;
        }
    }

    private sealed class Copier(Func<string, VaultPath?> attachment, Func<VaultPath, bool> mayCopy, string? host, Dictionary<string, string> references)
    {
        private readonly Dictionary<VaultPath, int> _numbers = [];
        private readonly Regex? _own = host is null ? null : new Regex(
            $@"\G(?<![A-Za-z0-9.@/-])(?<scheme>(?:https?:)?//)?(?<www>www\.)?{Regex.Escape(host)}\.?(?![A-Za-z0-9-])(?!\.[A-Za-z0-9-])(?<port>:[0-9]{{1,5}})?(?<path>[/?#][^\s<>]*)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public List<VaultPath> Files { get; } = [];

        public List<string> Plain { get; } = [];

        public Dictionary<string, string> References => references;

        public string? HostName => host;

        /// <summary>A paragraph, or with <paramref name="flat"/> a title or an alt, filtered; flat, every link and
        /// image is its text.</summary>
        public string Inline(string text, bool flat)
        {
            var scanner = new Scanner(this, text);
            var output = new Output();
            scanner.Scan(0, text.Length, flat, output);
            return SafetyNet(output);
        }

        public string Alt(Scanner scanner, int from, int to)
        {
            var output = new Output();
            scanner.Scan(from, to, true, output);
            return SafetyNet(output);
        }

        /// <summary>The end of a URL to the server itself starting at <paramref name="p"/>, or 0. A bare host name in
        /// running text is none; one with a scheme, "www.", a port or a path is.</summary>
        public int OwnUrl(string s, int p, int limit)
        {
            if (_own is null || s[p] is not ('h' or 'H' or '/' or 'w' or 'W') && char.ToLowerInvariant(s[p]) != host![0]) return 0;
            var m = _own.Match(s, p);
            if (!m.Success || m.Index != p) return 0;
            if (!m.Groups["scheme"].Success && !m.Groups["www"].Success && !m.Groups["port"].Success && !m.Groups["path"].Success) return 0;
            var end = Math.Min(p + m.Length, limit);
            var hostEnd = m.Groups["port"].Success ? m.Groups["port"].Index : m.Groups["path"].Success ? m.Groups["path"].Index : p + m.Length;
            // Trailing punctuation is the sentence's, as for GFM's autolinks.
            while (end > hostEnd && "?!.,:;*_~)'\"".Contains(s[end - 1])) end--;
            return end;
        }

        /// <summary>The text a wikilink or wiki embed leaves, or null for one that names nothing.</summary>
        public (string Text, bool Kept)? WikiText(Match wiki, bool copy)
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
            var shown = alias.Length > 0 ? Inline(alias, flat: true)
                : target.Length == 0 ? subpath.TrimStart('^')
                : StripMd(target.TrimEnd('/').Split('/')[^1]);
            return embed && target.Length > 0 ? Embed(target, "", shown, copy) : (shown, false);
        }

        /// <summary>An embed of <paramref name="target"/>: with <paramref name="copy"/>, the attachment by number if it
        /// may go with the copy; else plain text: <paramref name="alt"/>, <paramref name="plain"/> or its name.</summary>
        public (string Text, bool Kept) Embed(string target, string alt, string? plain, bool copy)
        {
            var file = target.Length > 0 ? attachment(target) : null;
            if (copy && file is not null && mayCopy(file))
            {
                if (!_numbers.TryGetValue(file, out var n))
                {
                    _numbers[file] = n = Files.Count;
                    Files.Add(file);
                }

                return ($"![{Escape(alt.Length > 0 ? alt : file.Name)}]({FilePrefix}{n})", true);
            }

            var name = file?.Name ?? target.TrimEnd('/').Split('/')[^1];
            var attached = file is not null || (Path.GetExtension(name) is { Length: > 1 } ext && !ext.Equals(".md", StringComparison.OrdinalIgnoreCase));
            if (attached && name.Length > 0 && !Plain.Contains(name)) Plain.Add(name);
            return (alt.Length > 0 ? alt : plain ?? (attached ? name : StripMd(name)), false);
        }

        private static string Escape(string text) => text.Replace("[", "\\[").Replace("]", "\\]");
    }

    /// <summary>One text's constructs, found once: code spans, autolinks and HTML tags, matching brackets, wikilinks.</summary>
    private sealed class Scanner
    {
        private readonly Copier _copier;
        private readonly string _s;
        private readonly Dictionary<int, int> _code;
        private readonly Dictionary<int, (int End, string? Url)> _angles = [];
        private readonly int[] _close;
        private readonly Dictionary<int, Match> _wikilinks;
        private readonly Dictionary<string, (int From, int Found)> _next = [];

        public Scanner(Copier copier, string s)
        {
            _copier = copier;
            _s = s;
            _code = CodeSpans(s);
            _wikilinks = s.Contains("[[", StringComparison.Ordinal) ? WikiLink.LinkRegex().Matches(s).ToDictionary(m => m.Index) : [];
            _close = new int[s.Length];
            Array.Fill(_close, -1);
            // Code, autolinks and HTML tags bind more tightly than link brackets.
            var open = new Stack<int>();
            for (var i = 0; i < s.Length;)
            {
                if (_code.TryGetValue(i, out var end))
                {
                    i = end;
                    continue;
                }

                if (s[i] == '<' && Angle(i) is { } angle)
                {
                    _angles[i] = angle;
                    i = angle.End;
                    continue;
                }

                if (s[i] == '[') open.Push(i);
                else if (s[i] == ']' && open.Count > 0) _close[open.Pop()] = i;
                i++;
            }
        }

        public void Scan(int from, int to, bool flat, Output o)
        {
            var s = _s;
            // A link's text runs to its ']' (a limit); there its tail is written and skipped.
            var limits = new Stack<(int Close, int End, string Tail, bool Kept)>();
            var textStart = -1;
            var htmlEnd = -1;
            var aloneUntil = -1;
            var notAloneBefore = -1;
            var p = from;
            while (p < to)
            {
                if (limits.Count > 0 && p == limits.Peek().Close)
                {
                    var region = limits.Pop();
                    o.Append(region.Tail, region.Kept);
                    p = region.End;
                    continue;
                }

                var limit = limits.Count > 0 ? limits.Peek().Close : to;
                var plain = flat || limits.Count > 0;
                var c = s[p];
                if (_code.TryGetValue(p, out var codeEnd) && codeEnd <= limit)
                {
                    o.Append(s[p..codeEnd], kept: true);
                    p = codeEnd;
                }
                else if (_angles.TryGetValue(p, out var angle) && angle.End <= limit)
                {
                    if (angle.Url is null) htmlEnd = angle.End;
                    // An autolink's text is its URL: a web one stays whole, any other goes whole.
                    else if (Classify(angle.Url, _copier.HostName) == Kind.Web && !angle.Url.Any(char.IsWhiteSpace))
                    {
                        if (plain) o.Append(angle.Url);
                        else o.Append(s[p..angle.End], kept: true);
                    }

                    p = angle.End;
                }
                else if (_wikilinks.TryGetValue(p, out var wiki) && p + wiki.Length <= limit && _copier.WikiText(wiki, copy: !plain) is { } shown)
                {
                    o.Append(shown.Text, shown.Kept);
                    p += wiki.Length;
                }
                else if ((c == '[' || (c == '!' && p + 1 < limit && s[p + 1] == '[')) && Link(p, limit) is { } link)
                {
                    var kind = Classify(link.Destination, _copier.HostName);
                    if (plain || (!link.Embed && kind != Kind.Web))
                    {
                        limits.Push((link.Close, link.End, "", false));
                        p = textStart = link.Open + 1;
                    }
                    else if (!link.Embed)
                    {
                        o.Append("[", kept: true);
                        limits.Push((link.Close, link.End, s[link.Close..link.End], true));
                        p = textStart = link.Open + 1;
                    }
                    else
                    {
                        var alt = _copier.Alt(this, link.Open + 1, link.Close);
                        switch (kind)
                        {
                            case Kind.Web:
                                // A remote image becomes a link: the reader's browser fetches nothing from third parties.
                                o.Append("[", kept: true);
                                o.Append(alt.Length == 0 ? link.Destination : alt);
                                o.Append(s[link.Close..link.End], kept: true);
                                break;
                            case Kind.Vault:
                                var hash = link.Destination.IndexOf('#');
                                var embed = _copier.Embed(MarkdownLink.Decode(hash >= 0 ? link.Destination[..hash] : link.Destination), alt, null, copy: true);
                                o.Append(embed.Text, embed.Kept);
                                break;
                            default:
                                o.Append(alt);
                                break;
                        }

                        p = link.End;
                    }
                }
                else if (c == '\\' && p + 1 < limit && s[p + 1] == '#')
                {
                    o.Append(s.Substring(p, 2));
                    p += 2;
                }
                else if (c == '#' && (p == from || p == textStart || p == htmlEnd || char.IsWhiteSpace(s[p - 1])) && TagEnd(s, p, limit) is var end && end > 0)
                {
                    bool alone;
                    if (end <= aloneUntil) alone = true;
                    else if (end <= notAloneBefore) alone = false;
                    else
                    {
                        var q = AloneFrom(s, end, limit);
                        alone = q < 0;
                        if (alone) aloneUntil = LineEnd(s, end, limit);
                        else notAloneBefore = q;
                    }

                    if (alone) o.TrimEnd();
                    else o.Append(s[(p + 1)..end]);
                    p = end;
                }
                else if (_copier.OwnUrl(s, p, limit) is > 0 and var ownEnd) p = ownEnd;
                else
                {
                    o.Append(c);
                    p++;
                }
            }
        }

        private sealed record LinkAt(bool Embed, int Open, int Close, int End, string Destination);

        /// <summary>The inline or reference link or image starting at <paramref name="p"/>, within the limit.</summary>
        private LinkAt? Link(int p, int limit)
        {
            var s = _s;
            var embed = s[p] == '!';
            var open = embed ? p + 1 : p;
            var close = _close[open];
            if (close < 0 || close >= limit) return null;
            var after = close + 1;
            if (after < limit && s[after] == '(' && Tail(after, limit) is { } tail) return new LinkAt(embed, open, close, tail.End, tail.Destination);
            if (close - open - 1 > MaxLabel) return null;
            string label;
            int end;
            if (after < limit && s[after] == '[' && _close[after] is var second && second > after && second < limit && second - after - 1 <= MaxLabel)
            {
                label = second > after + 1 ? s[(after + 1)..second] : s[(open + 1)..close];
                end = second + 1;
            }
            else
            {
                label = s[(open + 1)..close];
                end = after;
            }

            return _copier.References.TryGetValue(Label(label), out var destination) ? new LinkAt(embed, open, close, end, destination) : null;
        }

        /// <summary>"(destination title)" from the '(' at <paramref name="paren"/>; line breaks may stand in it.</summary>
        private (int End, string Destination)? Tail(int paren, int limit)
        {
            var s = _s;
            var i = SkipSpace(paren + 1, limit);
            string destination;
            if (i < limit && s[i] == '<')
            {
                var j = i + 1;
                while (j < limit && s[j] is not ('>' or '<' or '\n')) j++;
                if (j >= limit || s[j] != '>') return null;
                destination = s[(i + 1)..j];
                i = j + 1;
            }
            else
            {
                var start = i;
                for (var depth = 0; i < limit && !char.IsWhiteSpace(s[i]) && !char.IsControl(s[i]); i++)
                {
                    if (i - start > MaxDestination) return null;
                    if (s[i] == '\\') i++;
                    else if (s[i] == '(') depth++;
                    else if (s[i] == ')' && depth-- == 0) break;
                }

                if (i > limit) return null;
                destination = s[start..i];
            }

            var afterDestination = i;
            i = SkipSpace(i, limit);
            if (i > afterDestination && i < limit && s[i] is '"' or '\'' or '(')
            {
                var close = Next(s[i] == '(' ? ")" : s[i].ToString(), i + 1);
                if (close < 0 || close >= limit) return null;
                i = SkipSpace(close + 1, limit);
            }

            return i < limit && s[i] == ')' ? (i + 1, Unbracket(destination)) : null;
        }

        private int SkipSpace(int i, int limit)
        {
            while (i < limit && _s[i] is ' ' or '\t' or '\n') i++;
            return i;
        }

        /// <summary>An autolink (its URL) or a raw HTML tag (no URL) at the '<' at <paramref name="i"/>.</summary>
        private (int End, string? Url)? Angle(int i)
        {
            if (AutolinkRegex().Match(_s, i) is { Success: true } autolink) return (i + autolink.Length, autolink.Groups[1].Value);
            return HtmlEnd(i) is > 0 and var end ? (end, null) : null;
        }

        /// <summary>The end of the HTML tag, declaration or processing instruction at <paramref name="i"/>, or 0.
        /// Attribute values may hold '>' in quotes, and the tag may run over lines.</summary>
        private int HtmlEnd(int i)
        {
            var s = _s;
            var j = i + 1;
            if (j >= s.Length) return 0;
            if (string.CompareOrdinal(s, j, "![CDATA[", 0, 8) == 0) return Next("]]>", j) is >= 0 and var c ? c + 3 : 0;
            if (s[j] == '?') return Next("?>", j) is >= 0 and var q ? q + 2 : 0;
            if (s[j] == '!' && j + 1 < s.Length && char.IsAsciiLetter(s[j + 1])) return Next(">", j) is >= 0 and var d ? d + 1 : 0;
            var closing = s[j] == '/';
            if (closing) j++;
            if (j >= s.Length || !char.IsAsciiLetter(s[j])) return 0;
            while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] == '-')) j++;
            if (closing)
            {
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                return j < s.Length && s[j] == '>' ? j + 1 : 0;
            }

            while (true)
            {
                var space = j;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                if (j >= s.Length) return 0;
                if (s[j] == '>') return j + 1;
                if (s[j] == '/' && j + 1 < s.Length && s[j + 1] == '>') return j + 2;
                if (j == space || !(char.IsAsciiLetter(s[j]) || s[j] is '_' or ':')) return 0;
                while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] is '_' or ':' or '.' or '-')) j++;
                var name = j;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                if (j >= s.Length || s[j] != '=')
                {
                    j = name;
                    continue;
                }

                j++;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                if (j >= s.Length) return 0;
                if (s[j] is '"' or '\'')
                {
                    var quote = Next(s[j].ToString(), j + 1);
                    if (quote < 0) return 0;
                    j = quote + 1;
                    continue;
                }

                var value = j;
                while (j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] is not ('"' or '\'' or '=' or '<' or '>' or '`')) j++;
                if (j == value) return 0;
            }
        }

        /// <summary>The first <paramref name="what"/> at or after <paramref name="from"/>, or -1. Remembers the last
        /// answer, so the many searches of one scan, from ever later places, stay linear together.</summary>
        private int Next(string what, int from)
        {
            if (_next.TryGetValue(what, out var last) && last.From <= from && (last.Found < 0 || last.Found >= from)) return last.Found;
            var found = _s.IndexOf(what, from, StringComparison.Ordinal);
            _next[what] = (from, found);
            return found;
        }
    }

    /// <summary>The end of the tag whose '#' is at <paramref name="p"/>, or 0 if none starts there.</summary>
    private static int TagEnd(string text, int p, int limit)
    {
        var stop = p + 1;
        while (stop < limit && TagReader.IsTagChar(text[stop])) stop++;
        return TagReader.IsTag(text[(p + 1)..stop].TrimEnd('/')) ? stop : 0;
    }

    /// <summary>-1 if nothing but whitespace and other tags follows <paramref name="from"/> to the end of its line,
    /// else where something else stands.</summary>
    private static int AloneFrom(string text, int from, int limit)
    {
        var p = from;
        while (true)
        {
            while (p < limit && text[p] is ' ' or '\t') p++;
            if (p == limit || text[p] == '\n') return -1;
            var end = text[p] == '#' ? TagEnd(text, p, limit) : 0;
            if (end == 0) return p;
            p = end;
        }
    }

    private static int LineEnd(string text, int from, int limit)
    {
        var end = text.IndexOf('\n', from, limit - from);
        return end < 0 ? limit : end;
    }

    /// <summary>
    /// The last line of defence, outside what stays as it is: what is left of an inline link loses its destination,
    /// what is left of a tag goes, and a line shaped like a reference definition goes.
    /// </summary>
    private static string SafetyNet(Output output)
    {
        var s = output.Text.ToString();
        var result = new StringBuilder(s.Length);
        var at = 0;
        foreach (var (start, end) in output.Kept)
        {
            Clean(s, at, start, result);
            result.Append(s, start, end - start);
            at = end;
        }

        Clean(s, at, s.Length, result);
        var text = result.ToString();
        if (!text.Contains("]:", StringComparison.Ordinal)) return text;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (DefinitionShapeRegex().IsMatch(lines[i])) lines[i] = "";
        }

        return string.Join('\n', lines);
    }

    private static void Clean(string s, int from, int to, StringBuilder result)
    {
        for (var i = from; i < to; i++)
        {
            var c = s[i];
            if (c == ']' && i + 1 < to && s[i + 1] == '(')
            {
                var j = i + 2;
                for (var depth = 0; j < to && s[j] != '\n'; j++)
                {
                    if (s[j] == '(') depth++;
                    else if (s[j] == ')' && depth-- == 0) break;
                }

                i = j < to && s[j] == ')' ? j : j - 1;
                continue;
            }

            // What is left of a tag the scanner couldn't close, or one cut by a block's end, goes to its '>' or the
            // end of its line.
            if (c == '<' && i + 1 < to && (char.IsAsciiLetter(s[i + 1]) || s[i + 1] is '/' or '!' or '?'))
            {
                var j = i + 1;
                while (j < to && s[j] is not ('>' or '\n')) j++;
                i = j < to && s[j] == '>' ? j : j - 1;
                continue;
            }

            result.Append(c);
        }
    }

    [GeneratedRegex(@"^ {0,3}#[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex TitleRegex();

    // Inside quotes and list items too, at any depth.
    [GeneratedRegex(@"^(?:[ \t]*(?:>|[-*+](?=[ \t])|[0-9]{1,9}[.)](?=[ \t])))*[ \t]*\[(?<label>(?:[^\[\]\\]|\\.){1,999})\]:(?<rest>.*)$")]
    private static partial Regex DefinitionRegex();

    [GeneratedRegex(@"^(?:[ \t>]|[-*+]|[0-9]{1,9}[.)])*\[(?!\^)[^\]]*\]:")]
    private static partial Regex DefinitionShapeRegex();

    [GeneratedRegex(@"^(<[^<>]*>|\S+)(.*)$")]
    private static partial Regex DestinationRegex();

    [GeneratedRegex(@"\G<([A-Za-z][A-Za-z0-9+.-]{1,31}:[^<>\n]*)>")]
    private static partial Regex AutolinkRegex();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.-]*):")]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"^[0-9]+(x[0-9]+)?$")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[ \t>]*(?:[-*+]|[0-9]{1,9}[.)])(?:[ \t]|$)")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^[ \t>]*#{1,6}(?:[ \t]|$)")]
    private static partial Regex HeadingLineRegex();

    [GeneratedRegex(@"^[ \t>]*([-*_])(?:[ \t]*\1){2,}[ \t]*$")]
    private static partial Regex ThematicRegex();

    [GeneratedRegex(@"^(?:[ \t>]|(?:[-*+]|[0-9]{1,9}[.)])(?=[ \t]|$)|\[[ xX]\](?=[ \t]|$))*$")]
    private static partial Regex MarkersRegex();
}
