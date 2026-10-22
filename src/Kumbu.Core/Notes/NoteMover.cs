// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>
/// <c>mv</c>: moves one note within its vault and rewrites every link whose target the move would change,
/// to the most specific form (the vault path without ".md"). Content is otherwise never touched.
/// </summary>
public sealed class NoteMover(INoteStore store, TimeProvider time)
{
    private const int Retries = 3;

    public async Task<ShellResult> MoveAsync(VaultPath source, VaultPath target, bool rewriteLinks, string writer, CancellationToken ct)
    {
        var existing = await store.GetAsync(source, ct);
        if (existing is null)
        {
            return (await store.ListNotesUnderAsync(source, ct)).Count > 0 || source.IsRoot
                ? ShellResult.Error($"mv: {source.Value}: Is a directory (mv moves one note at a time)")
                : ShellResult.Error($"mv: {source.Value}: No such file or directory");
        }

        // A directory as target keeps the note's name.
        if ((await store.ListNotesUnderAsync(target, ct)).Count > 0 || target.Segments.Count == 1)
        {
            target = target.Child(source.Name);
        }

        if (target == source) return ShellResult.Error($"mv: {source.Value} and {target.Value} are the same note");
        if (target.Vault != source.Vault)
        {
            return ShellResult.Error($"mv: notes can't move between vaults ({source.Vault} → {target.Vault}); a vault is its own space");
        }

        if (NoteNames.Problem(target) is { } problem) return ShellResult.Error($"mv: {target.Value}: {problem}");
        if (await store.GetAsync(target, ct) is not null)
        {
            return ShellResult.Error($"mv: {target.Value} already exists; rm it first, or choose another name");
        }

        for (var dir = VaultPath.Root.Child(target.Segments[0]); dir.Segments.Count < target.Segments.Count - 1;)
        {
            dir = dir.Child(target.Segments[dir.Segments.Count]);
            if (await store.ReadAsync(dir, ct) is not null) return ShellResult.Error($"mv: {dir.Value}: Not a directory");
        }

        var vault = VaultPath.Root.Child(source.Segments[0]);
        var notes = await store.ReadNotesUnderAsync(vault, ct);
        // A move changes no content, so who wrote it and when stay as they were (the history is about text).
        var moved = existing with { Path = target, Revision = existing.Revision + 1 };
        if (!await store.TryMoveAsync(existing, moved, ct))
        {
            return ShellResult.Error($"mv: {source.Value} changed while moving it, or {target.Value} was just created; try again");
        }

        var message = $"moved {source.Value} to {target.Value}";
        if (!rewriteLinks) return Done(message + "; links left as they are (--keep-links)", moved);

        var rewrites = Rewrites(notes, source, target);
        var changed = new List<string>();
        var failed = new List<string>();
        var count = 0;
        foreach (var (path, links) in rewrites.OrderBy(r => r.Key.Value, TextOrder.Default))
        {
            if (await RewriteAsync(path, links, writer, ct)) { changed.Add(path.Value); count += links.Count; }
            else failed.Add(path.Value);
        }

        message += changed.Count == 0
            ? "; no links needed rewriting"
            : $"; rewrote {count} {(count == 1 ? "link" : "links")} in {string.Join(", ", changed)}";
        if (failed.Count > 0) message += $"; could not update {string.Join(", ", failed)} (changed meanwhile; 'links' shows what to fix)";

        return Done(message, await store.GetAsync(target, ct) ?? moved);
    }

    /// <summary>
    /// For each note (by its path after the move), the links whose target the move changes, with the text to
    /// put in their place: links to the moved note, and links that would now hit it by a name collision.
    /// </summary>
    public static Dictionary<VaultPath, List<(WikiLink Link, string Replacement)>> Rewrites(
        IReadOnlyList<Note> notes, VaultPath source, VaultPath target)
    {
        var vault = source.Vault!;
        var before = new LinkResolver(vault, notes.Select(n => n.Path));
        var after = new LinkResolver(vault, notes.Select(n => n.Path == source ? target : n.Path));
        var result = new Dictionary<VaultPath, List<(WikiLink, string)>>();
        foreach (var note in notes)
        {
            var oldPath = note.Path;
            var newPath = oldPath == source ? target : oldPath;
            foreach (var link in WikiLink.Parse(note.Content))
            {
                if (link.Target.Trim().Length == 0) continue;
                var was = before.Resolve(oldPath, link.Target);
                if (was.Kind != LinkKind.Note) continue;

                var meant = was.Target == source ? target : was.Target!;
                var now = after.Resolve(newPath, link.Target);
                if (now.Kind == LinkKind.Note && now.Target == meant) continue;

                if (!result.TryGetValue(newPath, out var list)) result[newPath] = list = [];
                list.Add((link, link.WithTarget(LinkResolver.LinkText(meant))));
            }
        }

        return result;
    }

    private async Task<bool> RewriteAsync(VaultPath path, List<(WikiLink Link, string Replacement)> links, string writer, CancellationToken ct)
    {
        for (var attempt = 0; attempt < Retries; attempt++)
        {
            var current = await store.GetAsync(path, ct);
            if (current is null) return false;

            var content = Apply(current.Content, links);
            if (content is null) return false;
            if (content == current.Content) return true;

            var next = NoteWriter.Next(current, content, writer, time.GetUtcNow().UtcDateTime);
            if (await store.TryReplaceAsync(current, next, ct)) return true;
        }

        return false;
    }

    /// <summary>Replaces each link at its exact position; null if the text there is no longer that link.</summary>
    private static string? Apply(string content, List<(WikiLink Link, string Replacement)> links)
    {
        var lines = Lines.Split(content).ToArray();
        foreach (var group in links.GroupBy(l => l.Link.Line))
        {
            if (group.Key >= lines.Length) return null;
            var line = lines[group.Key];
            foreach (var (link, replacement) in group.OrderByDescending(l => l.Link.Start))
            {
                if (link.Start + link.Raw.Length > line.Length || line.Substring(link.Start, link.Raw.Length) != link.Raw) return null;
                line = string.Concat(line.AsSpan(0, link.Start), replacement, line.AsSpan(link.Start + link.Raw.Length));
            }

            lines[group.Key] = line;
        }

        var result = Lines.Join(lines);
        return content.EndsWith('\n') ? result + "\n" : result;
    }

    private static ShellResult Done(string message, StoredNote note) =>
        ShellResult.Ok(message) with { Version = new NoteVersion(note.Path.Value, note.Version) };
}
