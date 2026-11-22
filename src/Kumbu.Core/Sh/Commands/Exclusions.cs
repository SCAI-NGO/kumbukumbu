// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Graph;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// What <c>--outside</c> and <c>--without-tag</c> leave out of <c>semsearch</c> and <c>similar</c> (spec
/// 2026-11-22-knowledge-synthesis §3.7): the notes at or below a path, and the notes carrying a tag, nested ones included.
/// </summary>
internal sealed record Exclusions(IReadOnlyList<VaultPath> Outside, IReadOnlyList<string> WithoutTags)
{
    public static Option[] Spec { get; } = [new(null, "outside", TakesValue: true), new(null, "without-tag", TakesValue: true)];

    public bool Any => Outside.Count > 0 || WithoutTags.Count > 0;

    /// <summary>The exclusions asked for, or the error that names the one that can't be: not a path, not a tag, too many.</summary>
    public static bool TryParse(string command, Options options, out Exclusions exclusions, out ShellResult? error)
    {
        exclusions = new([], []);
        error = null;
        var (paths, tags) = (options.Values("outside"), options.Values("without-tag"));
        if (paths.Count > GraphFilter.MaxFilters || tags.Count > GraphFilter.MaxFilters)
        {
            error = ShellResult.Error($"{command}: --outside and --without-tag can each be given at most {GraphFilter.MaxFilters} times");
            return false;
        }

        var outside = new List<VaultPath>();
        foreach (var text in paths)
        {
            if (!CommandArgs.TryParsePath(command, text, out var path, out error)) return false;
            if (path.IsRoot)
            {
                error = ShellResult.Error($"{command}: --outside / would leave out everything; give a vault or a folder");
                return false;
            }

            outside.Add(path);
        }

        var without = new List<string>();
        foreach (var text in tags)
        {
            if (TagFilter.Parse(text) is not { } tag)
            {
                error = ShellResult.Error($"{command}: {TagFilter.NotATag(text)}");
                return false;
            }

            without.Add(tag);
        }

        exclusions = new(outside, without);
        return true;
    }

    public bool LeavesOut(VaultPath path) => Outside.Any(o => AtOrBelow(path, o));

    public bool LeavesOut(Note note) => LeavesOut(note.Path) || WithoutTags.Any(t => TagFilter.Carries(note, t));

    private static bool AtOrBelow(VaultPath path, VaultPath folder) =>
        folder.Segments.Count <= path.Segments.Count
        && folder.Segments.Zip(path.Segments).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal));
}
