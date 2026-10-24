// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>
/// <c>mv</c>: moves one note, or a folder of notes, within a vault and rewrites every link whose target the
/// move would change. Everything (moves, link rewrites, move records) is one batch: all or nothing.
/// </summary>
public sealed class NoteMover(INoteStore store, TimeProvider time)
{
    public async Task<ShellResult> MoveAsync(VaultPath source, VaultPath target, bool rewriteLinks, string writer, CancellationToken ct)
    {
        var existing = await store.GetAsync(source, ct);
        if (existing is null)
        {
            if (await store.IsDamagedAsync(source, ct)) return ShellResult.Error(Damage.Message("mv", source));
            return (await store.ListNotesUnderAsync(source, ct)).Count > 0 || source.IsRoot
                ? ShellResult.Error($"mv: {source.Value}: Is a directory (to move the folder, write {source.Value}/)")
                : ShellResult.Error($"mv: {source.Value}: No such file or directory{await Moves.HintAsync(store, source, ct)}");
        }

        // A directory as target keeps the note's name.
        if ((await store.ListNotesUnderAsync(target, ct)).Count > 0 || target.Segments.Count == 1)
        {
            target = target.Child(source.Name);
        }

        if (target == source) return ShellResult.Error($"mv: {source.Value} and {target.Value} are the same note");
        if (await CheckTargetAsync(source, target, [], ct) is { } problem) return problem;

        return await CommitAsync(new Dictionary<VaultPath, VaultPath> { [source] = target }, rewriteLinks, writer,
            $"moved {source.Value} to {target.Value}", ct);
    }

    /// <summary>Moves every note below <paramref name="source"/>, keeping the structure below it. Like Unix mv,
    /// into <paramref name="target"/> if that folder exists, otherwise as <paramref name="target"/>.</summary>
    public async Task<ShellResult> MoveFolderAsync(VaultPath source, VaultPath target, bool rewriteLinks, string writer, CancellationToken ct)
    {
        if (source.Segments.Count < 2) return ShellResult.Error($"mv: {source.Value}/ is a vault; a vault can't be moved");
        var below = await store.ListNotesUnderAsync(source, ct);
        if (below.Count == 0)
        {
            return await store.GetAsync(source, ct) is not null
                ? ShellResult.Error($"mv: {source.Value}/: Not a directory (to move the note, leave out the '/')")
                : ShellResult.Error($"mv: {source.Value}/: No such file or directory");
        }

        if (target.Vault != source.Vault)
        {
            return ShellResult.Error($"mv: notes can't move between vaults ({source.Vault} → {target.Vault}); a vault is its own space");
        }

        if ((await store.ListNotesUnderAsync(target, ct)).Count > 0 || target.Segments.Count == 1) target = target.Child(source.Name);
        if (target == source) return ShellResult.Error($"mv: {source.Value}/ and {target.Value}/ are the same folder");
        if (target.Value.StartsWith(source.Value + "/", StringComparison.Ordinal))
        {
            return ShellResult.Error($"mv: {target.Value}/ is inside {source.Value}/; a folder can't move into itself");
        }

        if (await store.GetAsync(target, ct) is not null) return ShellResult.Error($"mv: {target.Value}: Not a directory");

        var map = below.ToDictionary(p => p, p => Rebase(p, source, target));
        foreach (var (from, to) in map)
        {
            if (await CheckTargetAsync(from, to, map.Keys.ToHashSet(), ct) is { } problem) return problem;
        }

        var count = map.Count == 1 ? "1 note" : $"{map.Count} notes";
        return await CommitAsync(map, rewriteLinks, writer, $"moved {source.Value}/ to {target.Value}/ ({count})", ct);
    }

    private static VaultPath Rebase(VaultPath path, VaultPath from, VaultPath to)
    {
        var result = to;
        foreach (var segment in path.Segments.Skip(from.Segments.Count)) result = result.Child(segment);
        return result;
    }

    /// <summary>Checks a target before anything is written. <paramref name="leaving"/> are paths moving away in
    /// the same batch, which don't count as existing.</summary>
    private async Task<ShellResult?> CheckTargetAsync(VaultPath source, VaultPath target, HashSet<VaultPath> leaving, CancellationToken ct)
    {
        if (target.Vault != source.Vault)
        {
            return ShellResult.Error($"mv: notes can't move between vaults ({source.Vault} → {target.Vault}); a vault is its own space");
        }

        if (NoteNames.Problem(target) is { } problem) return ShellResult.Error($"mv: {target.Value}: {problem}");
        if (await store.IsDamagedAsync(target, ct)) return ShellResult.Error(Damage.Message("mv", target));
        if (!leaving.Contains(target) && await store.GetAsync(target, ct) is not null)
        {
            return ShellResult.Error($"mv: {target.Value} already exists; rm it first, or choose another name");
        }

        for (var dir = VaultPath.Root.Child(target.Segments[0]); dir.Segments.Count < target.Segments.Count - 1;)
        {
            dir = dir.Child(target.Segments[dir.Segments.Count]);
            if (!leaving.Contains(dir) && await store.ReadAsync(dir, ct) is not null) return ShellResult.Error($"mv: {dir.Value}: Not a directory");
        }

        return null;
    }

    /// <summary>Builds one batch: every move, with the moved notes' own link rewrites, and the link rewrites in
    /// all other notes. A move changes no text, so a note without rewrites keeps its writer; rewritten notes
    /// get their own history entry, marked "(mv)".</summary>
    private async Task<ShellResult> CommitAsync(
        Dictionary<VaultPath, VaultPath> map, bool rewriteLinks, string writer, string message, CancellationToken ct)
    {
        var source = map.Keys.First();
        var vault = VaultPath.Root.Child(source.Segments[0]);
        var notes = await store.ReadNotesUnderAsync(vault, ct);
        var rewrites = rewriteLinks ? Rewrites(notes, map) : [];
        var now = time.GetUtcNow().UtcDateTime;
        var marked = $"{writer} (mv)";
        // Link rewrites join the writer's changeset like any write.
        var changeset = await Changeset.ForAsync(store, vault.Segments[0], writer, now, ct);
        var batch = new NoteBatch();
        var links = 0;
        var changed = new List<VaultPath>();

        foreach (var (from, to) in map)
        {
            if (await store.GetAsync(from, ct) is not { } current) return Changed(from);
            var moved = current with { Path = to, Revision = current.Revision + 1 };
            if (rewrites.TryGetValue(to, out var own))
            {
                if (Apply(current.Content, own) is not { } content) return Changed(from);
                moved = NoteWriter.Next(current, content, marked, now, changeset) with { Path = to };
                links += own.Count;
                changed.Add(to);
            }

            batch.Moves.Add((current, moved));
        }

        foreach (var (path, list) in rewrites.Where(r => !map.ContainsValue(r.Key)))
        {
            if (await store.GetAsync(path, ct) is not { } current) return Changed(path);
            if (Apply(current.Content, list) is not { } content) return Changed(path);
            batch.Replaces.Add((current, NoteWriter.Next(current, content, marked, now, changeset)));
            links += list.Count;
            changed.Add(path);
        }

        var conflicts = await store.TryCommitAsync(batch, writer, now, ct);
        if (conflicts.Count > 0)
        {
            return ShellResult.Error(
                $"mv: nothing was moved: {string.Join(", ", conflicts.Select(c => c.Value))} changed or appeared meanwhile; try again");
        }

        if (!rewriteLinks) message += "; links left as they are (--keep-links)";
        else if (changed.Count == 0) message += "; no links needed rewriting";
        else message += $"; rewrote {links} {(links == 1 ? "link" : "links")} in {string.Join(", ", changed.Order(Comparer<VaultPath>.Create((a, b) => TextOrder.Default.Compare(a.Value, b.Value))).Select(p => p.Value))}";

        var first = batch.Moves[0].Target;
        return ShellResult.Ok(message) with { Version = map.Count == 1 ? new NoteVersion(first.Path.Value, first.Version) : null };
    }

    private static ShellResult Changed(VaultPath path) =>
        ShellResult.Error($"mv: nothing was moved: {path.Value} changed meanwhile; try again");

    /// <summary>
    /// For each note (by its path after the move), the links whose target the move changes, with the text to
    /// put in their place: links to a moved note, and links that would now hit one by a name collision.
    /// </summary>
    public static Dictionary<VaultPath, List<(WikiLink Link, string Replacement)>> Rewrites(
        IReadOnlyList<Note> notes, IReadOnlyDictionary<VaultPath, VaultPath> map)
    {
        var vault = map.Keys.First().Vault!;
        var before = new LinkResolver(vault, notes.Select(n => n.Path));
        var after = new LinkResolver(vault, notes.Select(n => map.GetValueOrDefault(n.Path, n.Path)));
        var result = new Dictionary<VaultPath, List<(WikiLink, string)>>();
        foreach (var note in notes)
        {
            var oldPath = note.Path;
            var newPath = map.GetValueOrDefault(oldPath, oldPath);
            foreach (var link in WikiLink.Parse(note.Content))
            {
                if (link.Target.Trim().Length == 0) continue;
                var was = before.Resolve(oldPath, link.Target);
                if (was.Kind != LinkKind.Note) continue;

                var meant = map.GetValueOrDefault(was.Target!, was.Target!);
                var now = after.Resolve(newPath, link.Target);
                if (now.Kind == LinkKind.Note && now.Target == meant) continue;

                if (!result.TryGetValue(newPath, out var list)) result[newPath] = list = [];
                list.Add((link, link.WithTarget(NewTarget(link.Target, newPath, meant, map.ContainsKey(was.Target!), after))));
            }
        }

        return result;
    }

    /// <summary>
    /// The link text after the move (decided by Chris, 2026-10-23): keep the author's form, a bare name
    /// stays a bare name and a path keeps its length, if that resolves uniquely to the meant note. Relative
    /// links, links to other notes caught by a name collision, and forms that would not resolve fall back to
    /// the vault path, which can't be ambiguous.
    /// </summary>
    private static string NewTarget(string oldTarget, VaultPath linking, VaultPath meant, bool namedMovedNote, LinkResolver after)
    {
        var text = oldTarget.Trim();
        var vaultPath = LinkResolver.LinkText(meant);
        if (!namedMovedNote || text.StartsWith("./", StringComparison.Ordinal) || text.StartsWith("../", StringComparison.Ordinal))
        {
            return vaultPath;
        }

        var segments = vaultPath.Split('/');
        var kept = text.StartsWith('/')
            ? "/" + vaultPath
            : string.Join('/', segments.TakeLast(Math.Min(text.Split('/').Length, segments.Length)));
        if (text.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) kept += ".md";

        var resolved = after.Resolve(linking, kept);
        return resolved.Kind == LinkKind.Note && resolved.Target == meant ? kept : vaultPath;
    }

    /// <summary>Replaces each link at its exact position; null if the text there is no longer that link.</summary>
    public static string? Apply(string content, List<(WikiLink Link, string Replacement)> links)
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
}
