// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Paths;

/// <summary>
/// A file and a folder never share a name (spec 2026-11-18, decision 2): what a path is, is decided by what is stored
/// there. A path is a folder while anything is stored below it (a note, a file or a kept folder's marker); nothing is
/// stored below a note or a file. Writers check this before writing, with these sentences; a store checks it again in
/// its transaction (<see cref="Clash"/>), so two writes racing can't break it.
/// </summary>
public static class FileOrFolder
{
    /// <summary>The refusal of a note or file at <paramref name="folder"/>, a folder.</summary>
    public static string IsFolder(string tool, VaultPath folder) =>
        $"{tool}: {folder.Value} is a folder; a file and a folder can't share a name";

    /// <summary>The refusal of anything below <paramref name="entry"/>, a note or a file.</summary>
    public static string NotFolder(string tool, VaultPath entry) => $"{tool}: {NotFolder(entry)}";

    /// <summary>The same, without a tool: as a reason one of several files was skipped.</summary>
    public static string NotFolder(VaultPath entry) =>
        $"{entry.Value} is {(entry.IsNote ? "a note" : "a file")}, not a folder; nothing can be kept below it";

    /// <summary>
    /// The first path of <paramref name="placed"/> that breaks the rule once every one of them is stored, or the note or
    /// file above one that is in its way; null if none does. <paramref name="stored"/> are the paths of the notes and
    /// files of the vault after the write, <paramref name="placed"/> among them.
    /// </summary>
    public static VaultPath? Clash(IReadOnlyCollection<VaultPath> placed, IReadOnlySet<VaultPath> stored)
    {
        if (placed.Count == 0) return null;
        // By the paths' text: a working set can hold many thousands of paths.
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in stored)
        {
            var value = path.Value;
            for (var slash = value.LastIndexOf('/'); slash > 0 && folders.Add(value[..slash]); slash = value.LastIndexOf('/', slash - 1))
            {
            }
        }

        foreach (var path in placed)
        {
            if (folders.Contains(path.Value)) return path;
            if (FoldersAbove(path).FirstOrDefault(stored.Contains) is { } entry) return entry;
        }

        return null;
    }

    /// <summary>
    /// <see cref="Clash(IReadOnlyCollection{VaultPath}, IReadOnlySet{VaultPath})"/> for a few placed paths, without a set
    /// of the vault: what is stored after the write is <paramref name="placed"/>, and what <paramref name="isStored"/> says
    /// is stored now unless it is <paramref name="leaving"/>; <paramref name="storedNow"/> are those paths, scanned once
    /// per placed path for anything below it.
    /// </summary>
    public static VaultPath? Clash(
        IReadOnlyCollection<VaultPath> placed, IReadOnlySet<VaultPath> leaving, Func<VaultPath, bool> isStored, IEnumerable<VaultPath> storedNow)
    {
        foreach (var path in placed)
        {
            var below = path.Value + "/";
            if (placed.Any(p => p.Value.StartsWith(below, StringComparison.Ordinal))
                || storedNow.Any(p => p.Value.StartsWith(below, StringComparison.Ordinal) && !leaving.Contains(p)))
            {
                return path;
            }

            if (FoldersAbove(path).FirstOrDefault(dir => placed.Contains(dir) || (isStored(dir) && !leaving.Contains(dir))) is { } entry) return entry;
        }

        return null;
    }

    /// <summary>The folders <paramref name="path"/> lies in, below its vault, from the top.</summary>
    public static IEnumerable<VaultPath> FoldersAbove(VaultPath path)
    {
        if (path.Segments.Count < 2) yield break;
        for (var dir = VaultPath.Root.Child(path.Segments[0]); dir.Segments.Count < path.Segments.Count - 1;)
        {
            dir = dir.Child(path.Segments[dir.Segments.Count]);
            yield return dir;
        }
    }
}
