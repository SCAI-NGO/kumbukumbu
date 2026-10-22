// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sections;

/// <summary>One step of a section address: a heading's text and, for duplicates, its index.</summary>
public sealed record AddressSegment(string Text, int? Index);

/// <summary>
/// Section addresses (concept §4): <c>/Work/plan.md#Architecture#Gates</c>. The first '#' after ".md"
/// starts the address, each further '#' goes one level down, "\#" is a literal '#', and a trailing
/// "[n]" picks among headings with the same text under the same parent.
/// </summary>
public static partial class SectionAddress
{
    /// <summary>Splits "path.md#A#B" into the path and the address ("A#B"), which is null without one.</summary>
    public static (string Path, string? Fragment) Split(string arg)
    {
        var at = arg.IndexOf(".md#", StringComparison.Ordinal);
        return at < 0 ? (arg, null) : (arg[..(at + 3)], arg[(at + 4)..]);
    }

    public static bool TryParse(string fragment, [NotNullWhen(true)] out IReadOnlyList<AddressSegment>? segments)
    {
        var result = new List<AddressSegment>();
        var current = new StringBuilder();
        var escapedEnd = -1; // position in current after the last escaped character
        for (var i = 0; i <= fragment.Length; i++)
        {
            if (i == fragment.Length || fragment[i] == '#')
            {
                var text = current.ToString();
                int? index = null;
                // A trailing [n] is an index unless its '[' was escaped.
                if (IndexRegex().Match(text) is { Success: true } m && m.Index >= escapedEnd)
                {
                    index = int.Parse(m.Groups[1].Value);
                    text = text[..m.Index];
                }

                if (text.Trim().Length == 0)
                {
                    segments = null;
                    return false;
                }

                result.Add(new AddressSegment(text.Trim(), index));
                current.Clear();
                escapedEnd = -1;
                continue;
            }

            if (fragment[i] == '\\' && i + 1 < fragment.Length)
            {
                current.Append(fragment[++i]);
                escapedEnd = current.Length;
                continue;
            }

            current.Append(fragment[i]);
        }

        segments = result;
        return true;
    }

    /// <summary>Writes heading text as an address segment: escapes '\' and '#', and a '[' that would read as an index.</summary>
    public static string Escape(string text)
    {
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("#", "\\#", StringComparison.Ordinal);
        var m = IndexRegex().Match(escaped);
        return m.Success ? escaped[..m.Index] + "\\" + escaped[m.Index..] : escaped;
    }

    /// <summary>
    /// The headings an address matches: those whose chain ends with the address's segments. Exact text
    /// first; if nothing matches exactly, ignoring case.
    /// </summary>
    public static IReadOnlyList<Heading> Resolve(IReadOnlyList<Heading> headings, IReadOnlyList<AddressSegment> segments)
    {
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            var matches = headings.Where(h => Matches(h, segments, comparison)).ToList();
            if (matches.Count > 0) return matches;
        }

        return [];
    }

    /// <summary>A note path with a section address, as it would be typed in sh.</summary>
    public static string Display(VaultPath path, Heading heading) => path.Value + heading.Address;

    private static bool Matches(Heading heading, IReadOnlyList<AddressSegment> segments, StringComparison comparison)
    {
        var chain = heading.Chain().ToList();
        if (chain.Count < segments.Count) return false;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[segments.Count - 1 - i];
            var h = chain[i];
            if (!string.Equals(segment.Text, h.Text, comparison)) return false;
            if (segment.Index is { } index && index != Math.Max(h.Index, 1)) return false;
        }

        return true;
    }

    [GeneratedRegex(@"\[(\d+)\]$")]
    private static partial Regex IndexRegex();
}
