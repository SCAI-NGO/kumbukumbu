// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Diffs;

/// <summary>Unified diffs of notes, like <c>git diff</c>, optionally with a word diff inside changed lines.</summary>
public static partial class TextDiff
{
    public const int Context = 3;

    /// <summary>Lines added and removed from <paramref name="before"/> to <paramref name="after"/>.</summary>
    public static (int Added, int Removed) Count(string before, string after)
    {
        var edits = Myers.Diff(Lines.Split(before), Lines.Split(after), StringComparer.Ordinal);
        return (edits.Count(e => e.Kind == EditKind.Added), edits.Count(e => e.Kind == EditKind.Removed));
    }

    /// <summary>The hunks of a unified diff with <see cref="Context"/> lines around each change; empty if equal.
    /// With <paramref name="words"/>, each hunk's changes are shown inline as [-old-]{+new+}.</summary>
    public static IReadOnlyList<string> Hunks(string before, string after, bool words)
    {
        var a = Lines.Split(before);
        var b = Lines.Split(after);
        var edits = Myers.Diff(a, b, StringComparer.Ordinal);
        if (edits.All(e => e.Kind == EditKind.Same)) return [];

        // Positions (line numbers before and after) of every edit.
        var positions = new List<(int A, int B)>(edits.Count);
        int ia = 0, ib = 0;
        foreach (var e in edits)
        {
            positions.Add((ia, ib));
            if (e.Kind != EditKind.Added) ia++;
            if (e.Kind != EditKind.Removed) ib++;
        }

        // Group changes whose context overlaps into one hunk.
        var output = new List<string>();
        var i = 0;
        while (i < edits.Count)
        {
            if (edits[i].Kind == EditKind.Same) { i++; continue; }
            var start = Math.Max(0, i - Context);
            var end = i;
            while (true)
            {
                while (end < edits.Count && edits[end].Kind != EditKind.Same) end++;
                var next = end;
                while (next < edits.Count && edits[next].Kind == EditKind.Same) next++;
                if (next < edits.Count && next - end <= 2 * Context) { end = next; continue; }
                end = Math.Min(edits.Count, end + Context);
                break;
            }

            var hunk = edits.Skip(start).Take(end - start).ToList();
            var (aFrom, bFrom) = positions[start];
            var aLen = hunk.Count(e => e.Kind != EditKind.Added);
            var bLen = hunk.Count(e => e.Kind != EditKind.Removed);
            output.Add($"@@ -{Range(aFrom, aLen)} +{Range(bFrom, bLen)} @@");
            output.AddRange(words ? WordHunk(hunk) : hunk.Select(e => Prefix(e.Kind) + e.Item));
            i = end;
        }

        return output;
    }

    private static string Range(int from, int length) =>
        length == 0 ? $"{from},0" : length == 1 ? $"{from + 1}" : $"{from + 1},{length}";

    private static string Prefix(EditKind kind) => kind switch { EditKind.Added => "+", EditKind.Removed => "-", _ => " " };

    /// <summary>Context lines as they are; each run of removed and added lines as one word diff.</summary>
    private static IEnumerable<string> WordHunk(List<Edit<string>> hunk)
    {
        var i = 0;
        while (i < hunk.Count)
        {
            if (hunk[i].Kind == EditKind.Same) { yield return " " + hunk[i].Item; i++; continue; }
            var removed = new List<string>();
            var added = new List<string>();
            while (i < hunk.Count && hunk[i].Kind != EditKind.Same)
            {
                (hunk[i].Kind == EditKind.Removed ? removed : added).Add(hunk[i].Item);
                i++;
            }

            foreach (var line in Words(string.Join('\n', removed), string.Join('\n', added)).Split('\n'))
            {
                yield return "~" + line;
            }
        }
    }

    /// <summary>A word diff in git's notation: [-removed-]{+added+}; whitespace counts as its own token.</summary>
    public static string Words(string before, string after)
    {
        var edits = Myers.Diff(Tokens(before), Tokens(after), StringComparer.Ordinal);
        var text = new StringBuilder();
        var i = 0;
        while (i < edits.Count)
        {
            if (edits[i].Kind == EditKind.Same) { text.Append(edits[i].Item); i++; continue; }
            var removed = new StringBuilder();
            var added = new StringBuilder();
            while (i < edits.Count && edits[i].Kind != EditKind.Same)
            {
                (edits[i].Kind == EditKind.Removed ? removed : added).Append(edits[i].Item);
                i++;
            }

            if (removed.Length > 0) text.Append("[-").Append(removed).Append("-]");
            if (added.Length > 0) text.Append("{+").Append(added).Append("+}");
        }

        return text.ToString();
    }

    private static List<string> Tokens(string text) => [.. TokenRegex().Matches(text).Select(m => m.Value)];

    // Words, runs of whitespace, and single other characters.
    [GeneratedRegex(@"[\p{L}\p{N}_]+|\s+|.", RegexOptions.Singleline)]
    private static partial Regex TokenRegex();
}
