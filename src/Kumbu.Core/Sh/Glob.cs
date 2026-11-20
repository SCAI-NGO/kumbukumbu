// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh;

/// <summary>
/// One <c>rg -g</c> glob, with ripgrep's rules: <c>*</c> and <c>?</c> stay within a segment, <c>**</c> crosses
/// them (<c>**/</c> also matches nothing), <c>[…]</c> and <c>[!…]</c> are classes, a leading <c>!</c> excludes.
/// A pattern with a <c>/</c> is anchored to the root and tested against the path below it; one without is tested
/// against the file name, and, when it excludes, against every folder name on the way, as ripgrep prunes folders.
/// </summary>
public sealed class Glob
{
    private readonly Regex regex;
    private readonly bool anchored;
    private readonly bool foldersOnly;

    private Glob(Regex regex, bool anchored, bool excludes, bool foldersOnly)
    {
        this.foldersOnly = foldersOnly;
        this.regex = regex;
        this.anchored = anchored;
        Excludes = excludes;
    }

    /// <summary>The pattern started with <c>!</c>.</summary>
    public bool Excludes { get; }

    public static bool TryParse(string pattern, [NotNullWhen(true)] out Glob? glob, [NotNullWhen(false)] out string? error)
    {
        glob = null;
        error = null;
        var excludes = pattern.StartsWith('!');
        var body = excludes ? pattern[1..] : pattern;
        // A trailing slash says "folders only" and doesn't anchor, as in ripgrep.
        var foldersOnly = body.Length > 1 && body.EndsWith('/');
        if (foldersOnly) body = body[..^1];
        if (body.Length == 0)
        {
            error = $"rg: bad glob '{pattern}': empty";
            return false;
        }

        var anchored = body.Contains('/');
        if (body.StartsWith('/')) body = body[1..];

        if (!TryTranslate(body, pattern, true, out var translated, out error)) return false;
        try
        {
            glob = new Glob(new Regex("^" + translated + "$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant), anchored, excludes, foldersOnly);
        }
        catch (ArgumentException)
        {
            error = $"rg: bad glob '{pattern}': invalid class";
            return false;
        }
        catch (NotSupportedException)
        {
            error = $"rg: bad glob '{pattern}': too long";
            return false;
        }

        return true;
    }

    private static bool TryTranslate(string body, string pattern, bool braces, [NotNullWhen(true)] out string? regex, [NotNullWhen(false)] out string? error)
    {
        regex = null;
        error = null;
        var sb = new StringBuilder();
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            switch (c)
            {
                case '*' when i + 1 < body.Length && body[i + 1] == '*':
                    var atStart = i == 0 || body[i - 1] == '/';
                    i++;
                    if (atStart && i + 1 < body.Length && body[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }

                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                    var first = i + 1 < body.Length && body[i + 1] is '!' or '^' ? i + 2 : i + 1;
                    var end = first < body.Length ? body.IndexOf(']', first + 1) : -1;
                    if (end < 0)
                    {
                        error = $"rg: bad glob '{pattern}': unclosed [";
                        return false;
                    }

                    var negated = first > i + 1;
                    var content = new StringBuilder();
                    foreach (var k in body[first..end]) content.Append(k is '\\' or '[' or ']' or '^' ? "\\" + k : k.ToString());
                    // A trailing '-' is a literal, not the start of the range that the '/' below would make.
                    if (body[end - 1] == '-' && end - 1 >= first) content.Insert(content.Length - 1, '\\');
                    // A class never matches '/', as in ripgrep.
                    sb.Append(negated ? $"[^{content}/]" : $"[{content}-[/]]");
                    i = end;
                    break;
                case '{' when braces:
                    var close = body.IndexOf('}', i + 1);
                    if (close < 0)
                    {
                        error = $"rg: bad glob '{pattern}': unclosed {{";
                        return false;
                    }

                    var inner = body[(i + 1)..close];
                    if (inner.Contains('{'))
                    {
                        error = $"rg: bad glob '{pattern}': nested {{}}";
                        return false;
                    }

                    var alternatives = new List<string>();
                    foreach (var part in inner.Split(','))
                    {
                        if (!TryTranslate(part, pattern, false, out var one, out error)) return false;
                        alternatives.Add(one);
                    }

                    sb.Append("(?:").AppendJoin('|', alternatives).Append(')');
                    i = close;
                    break;
                case '\\' when i + 1 < body.Length:
                    sb.Append(Regex.Escape(body[++i].ToString()));
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        regex = sb.ToString();
        return true;
    }

    /// <summary>Whether the pattern applies to the path, judged from <paramref name="root"/>, the operand the search started at.
    /// An excluding pattern also applies when it matches a folder on the way, as ripgrep prunes the folder.</summary>
    public bool Matches(VaultPath path, VaultPath root)
    {
        var below = path.Segments.Count > root.Segments.Count && root.Segments.SequenceEqual(path.Segments.Take(root.Segments.Count))
            ? path.Segments.Skip(root.Segments.Count).ToList()
            : [path.Name];
        var folders = Enumerable.Range(1, below.Count - 1).Select(k => anchored ? string.Join('/', below.Take(k)) : below[k - 1]);
        var full = anchored ? string.Join('/', below) : below[^1];
        IEnumerable<string> candidates = foldersOnly ? folders : Excludes ? folders.Append(full) : [full];
        return candidates.Any(regex.IsMatch);
    }

    /// <summary>Kept when it matches at least one include (or there are none) and no exclude.</summary>
    public static bool Keeps(IReadOnlyList<Glob> globs, VaultPath path, VaultPath root)
    {
        var hasIncludes = false;
        var included = false;
        foreach (var glob in globs)
        {
            var hit = glob.Matches(path, root);
            if (glob.Excludes && hit) return false;
            if (glob.Excludes) continue;
            hasIncludes = true;
            included |= hit;
        }

        return included || !hasIncludes;
    }
}
