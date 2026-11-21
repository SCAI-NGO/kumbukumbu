// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Kumbu.Core.Links;

/// <summary>A link to a note as written: a <see cref="WikiLink"/> or a <see cref="MarkdownLink"/>.</summary>
/// <param name="Line">0-based line of the note.</param>
/// <param name="Start">Character index of the link in that line.</param>
/// <param name="Raw">The link's exact source text.</param>
/// <param name="Target">The path <see cref="LinkResolver.Resolve"/> takes; empty for a heading of the note itself.</param>
/// <param name="Subpath">"#Heading", "#^block" or "", as written.</param>
public abstract partial record NoteLink(int Line, int Start, string Raw, string Target, string Subpath)
{
    /// <summary>All links of a note, both kinds, in source order.</summary>
    public static IReadOnlyList<NoteLink> Parse(string content) =>
        [.. WikiLink.Parse(content).Concat<NoteLink>(MarkdownLink.Parse(content)).OrderBy(l => l.Line).ThenBy(l => l.Start)];

    /// <summary>Inline code: a run of backticks up to the next run of the same length; in order, in one pass.</summary>
    internal static List<(int Start, int End)> CodeSpans(string line)
    {
        var spans = new List<(int, int)>();
        var runs = BacktickRegex().Matches(line).ToList();
        // Each run's next run of the same length, found from the right.
        var next = new int[runs.Count];
        var seen = new Dictionary<int, int>();
        for (var i = runs.Count - 1; i >= 0; i--)
        {
            next[i] = seen.TryGetValue(runs[i].Length, out var j) ? j : -1;
            seen[runs[i].Length] = i;
        }

        for (var i = 0; i < runs.Count; i++)
        {
            if (next[i] < 0) continue;
            spans.Add((runs[i].Index, runs[next[i]].Index + runs[next[i]].Length));
            i = next[i];
        }

        return spans;
    }

    [GeneratedRegex("`+")]
    private static partial Regex BacktickRegex();
}
