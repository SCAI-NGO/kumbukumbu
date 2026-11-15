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

    private Glob(Regex regex, bool anchored, bool excludes)
    {
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
        if (body.Length == 0)
        {
            error = $"rg: bad glob '{pattern}': empty";
            return false;
        }

        var anchored = body.Contains('/');
        if (body.StartsWith('/')) body = body[1..];

        var sb = new StringBuilder("^");
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
                    var end = body.IndexOf(']', first + 1);
                    if (end < 0)
                    {
                        error = $"rg: bad glob '{pattern}': unclosed [";
                        return false;
                    }

                    var inside = body[(i + 1)..end];
                    var negated = inside.StartsWith('!') || inside.StartsWith('^');
                    if (negated) inside = inside[1..];
                    sb.Append(negated ? "[^/" : "[");
                    foreach (var k in inside) sb.Append(k is '\\' or '[' or ']' or '^' ? "\\" + k : k.ToString());
                    sb.Append(']');
                    i = end;
                    break;
                case '\\' when i + 1 < body.Length:
                    sb.Append(Regex.Escape(body[++i].ToString()));
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        sb.Append('$');
        glob = new Glob(new Regex(sb.ToString(), RegexOptions.NonBacktracking | RegexOptions.CultureInvariant), anchored, excludes);
        return true;
    }

    /// <summary>Whether the pattern applies to the path, judged from <paramref name="root"/>, the operand the search started at.</summary>
    public bool Matches(VaultPath path, VaultPath root)
    {
        var below = path.Segments.Count > root.Segments.Count && root.Segments.SequenceEqual(path.Segments.Take(root.Segments.Count))
            ? path.Segments.Skip(root.Segments.Count).ToList()
            : [path.Name];
        if (anchored) return regex.IsMatch(string.Join('/', below));
        return Excludes ? below.Any(regex.IsMatch) : regex.IsMatch(below[^1]);
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
