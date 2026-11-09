// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Links;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Skills;

namespace Kumbu.Core.Notes;

/// <summary>
/// <c>mv</c>: moves one note or attachment, or a folder of them, within a vault and rewrites every link whose target
/// the move would change. Everything (moves, link rewrites, move records) is one batch: all or nothing.
/// </summary>
public sealed class NoteMover(INoteStore store, TimeProvider time, ICaller caller)
{
    /// <summary>The clock moves are made by, for the commands that build on them.</summary>
    internal TimeProvider Time => time;

    public Task<ShellResult> MoveAsync(VaultPath source, VaultPath target, bool rewriteLinks, CancellationToken ct) =>
        MoveAsync(source, target, rewriteLinks, card: false, replacing: null, "mv", exact: false, ct);

    /// <summary>A move on behalf of <paramref name="command"/>, a command building on mv (archive), which its refusals
    /// and its history mark name. <paramref name="exact"/>: to exactly <paramref name="target"/>, never into a folder
    /// there; a folder found there is refused as a conflict.</summary>
    public Task<ShellResult> MoveAsync(VaultPath source, VaultPath target, bool rewriteLinks, string command, bool exact, CancellationToken ct) =>
        MoveAsync(source, target, rewriteLinks, card: false, replacing: null, command, exact, ct);

    /// <summary>A card's move by the board (to its archive): a card operation, so it needs <c>write</c> only, even where
    /// the move rewrites a link in <c>Board.md</c> (decided by the controller, after concept §7). A person's own
    /// <c>mv</c> that rewrites such a link is structural.</summary>
    internal Task<ShellResult> MoveCardAsync(VaultPath source, VaultPath target, CancellationToken ct) =>
        MoveAsync(source, target, rewriteLinks: true, card: true, replacing: null, "mv", exact: false, ct);

    /// <summary>
    /// WebDAV's <c>MOVE</c> over what is at <paramref name="target"/> (<c>Overwrite: T</c>, WebDAV design §3.6 case 5;
    /// <c>mv</c> never overwrites): the note, attachment or folder at <paramref name="source"/> to exactly
    /// <paramref name="target"/>, never into a folder there, links rewritten as <see cref="MoveAsync(VaultPath, VaultPath, bool, CancellationToken)"/>
    /// rewrites them, and what was at the target to the trash in the same batch. Every check of the move, and the
    /// caller's right to remove what is there, runs before anything is written: a refused move costs the target nothing.
    /// </summary>
    public async Task<ShellResult> MoveOverAsync(VaultPath source, VaultPath target, CancellationToken ct)
    {
        if (target.Segments.Count < 2) return ShellResult.Error($"mv: {target.Value}/ is a vault; a vault can't be replaced");
        if (caller.RefuseFolder("rm", target) is { } refused) return refused;
        var replaced = new NoteBatch();
        if (!await TreeWriter.TrashAsync(store, replaced, target, ct)) return Changed("mv", target);
        return await store.GetAsync(source, ct) is not null || await StoreAttachments.GetAsync(store, source, ct) is not null
            ? await MoveAsync(source, target, rewriteLinks: true, card: false, replaced, "mv", exact: false, ct)
            : await MoveFolderAsync(source, target, rewriteLinks: true, replaced, "mv", exact: false, ct);
    }

    /// <summary>Moves a note or an attachment; <paramref name="replacing"/> holds what goes to the trash in its batch
    /// (<see cref="MoveOverAsync"/>), and then <paramref name="target"/> is the path itself, whatever is there.</summary>
    private async Task<ShellResult> MoveAsync(
        VaultPath source, VaultPath target, bool rewriteLinks, bool card, NoteBatch? replacing, string command, bool exact, CancellationToken ct)
    {
        // As a folder: a folder named without its '/' is refused below as one, but a structural refusal comes first.
        if (caller.RefuseFolder(command, source) is { } refused) return refused;
        var existing = await store.GetAsync(source, ct);
        if (existing is null && await StoreAttachments.GetAsync(store, source, ct) is null)
        {
            if (await store.IsDamagedAsync(source, ct)) return ShellResult.Error(Damage.Message(command, source));
            return await IsFolderAsync(source, ct) || source.IsRoot
                ? ShellResult.Error($"{command}: {source.Value}: Is a directory (to move the folder, write {source.Value}/)")
                : ShellResult.Error($"{command}: {source.Value}: No such file or directory{await Moves.HintAsync(store, source, ct)}");
        }

        // A directory as target keeps the name.
        if (replacing is null && (await IsFolderAsync(target, ct) || target.Segments.Count == 1))
        {
            if (exact) return Appeared(command, target);
            target = target.Child(source.Name);
        }

        if (target == source) return ShellResult.Error($"{command}: {source.Value} and {target.Value} are the same {(existing is null ? "attachment" : "note")}");
        if (caller.Refuse(command, target) is { } refusedTarget) return refusedTarget;
        if (await CheckTargetAsync(source, target, Replaced(replacing), command, ct) is { } problem) return problem;

        return await CommitAsync(new Dictionary<VaultPath, VaultPath> { [source] = target }, rewriteLinks,
            $"moved {source.Value} to {target.Value}", card, replacing, command, ct);
    }

    /// <summary>Moves every note and attachment below <paramref name="source"/>, keeping the structure below it. Like
    /// Unix mv, into <paramref name="target"/> if that folder exists, otherwise as <paramref name="target"/>.</summary>
    public Task<ShellResult> MoveFolderAsync(VaultPath source, VaultPath target, bool rewriteLinks, CancellationToken ct) =>
        MoveFolderAsync(source, target, rewriteLinks, replacing: null, "mv", exact: false, ct);

    /// <summary>A folder's move on behalf of <paramref name="command"/>, exactly to <paramref name="target"/> if
    /// <paramref name="exact"/>, as <see cref="MoveAsync(VaultPath, VaultPath, bool, string, bool, CancellationToken)"/>.</summary>
    public Task<ShellResult> MoveFolderAsync(VaultPath source, VaultPath target, bool rewriteLinks, string command, bool exact, CancellationToken ct) =>
        MoveFolderAsync(source, target, rewriteLinks, replacing: null, command, exact, ct);

    private async Task<ShellResult> MoveFolderAsync(
        VaultPath source, VaultPath target, bool rewriteLinks, NoteBatch? replacing, string command, bool exact, CancellationToken ct)
    {
        if (caller.RefuseFolder(command, source) is { } refused) return refused;
        if (source.Segments.Count < 2) return ShellResult.Error($"{command}: {source.Value}/ is a vault; a vault can't be moved");
        var notes = await store.ListNotesUnderAsync(source, ct);
        var attachments = (await StoreAttachments.UnderAsync(store, source, ct)).Select(a => a.Path).ToList();
        if (notes.Count == 0 && attachments.Count == 0)
        {
            return await ExistsAsync(source, ct)
                ? ShellResult.Error($"{command}: {source.Value}/: Not a directory (to move the {(source.IsAttachment ? "attachment" : "note")}, leave out the '/')")
                : ShellResult.Error($"{command}: {source.Value}/: No such file or directory");
        }

        if (target.Vault != source.Vault)
        {
            return ShellResult.Error($"{command}: notes can't move between vaults ({source.Vault} → {target.Vault}); a vault is its own space");
        }

        if (replacing is null && (await IsFolderAsync(target, ct) || target.Segments.Count == 1))
        {
            if (exact) return Appeared(command, target);
            target = target.Child(source.Name);
        }

        if (target == source) return ShellResult.Error($"{command}: {source.Value}/ and {target.Value}/ are the same folder");
        if (target.Value.StartsWith(source.Value + "/", StringComparison.Ordinal))
        {
            return ShellResult.Error($"{command}: {target.Value}/ is inside {source.Value}/; a folder can't move into itself");
        }

        var replaced = Replaced(replacing);
        if (!replaced.Contains(target) && await ExistsAsync(target, ct)) return ShellResult.Error($"{command}: {target.Value}: Not a directory");

        var map = notes.Concat(attachments).ToDictionary(p => p, p => Rebase(p, source, target));
        // Each path on its own: a folder can carry a Board.md into the place of the board's.
        foreach (var path in map.Keys.Concat(map.Values))
        {
            if (caller.Refuse(command, path) is { } refusedPath) return refusedPath;
        }

        if (TextFilesOutsideSkills(map, source, await store.SkillsVaultAsync(ct)) is { } outside)
        {
            return ShellResult.Error($"{command}: {source.Value}/ can't move to {target.Value}/: text files are kept only inside a skill's folder: {outside} (move them into a skill's folder first, or remove them)");
        }

        foreach (var (from, to) in map)
        {
            if (await CheckTargetAsync(from, to, [.. map.Keys, .. replaced], command, ct) is { } problem) return problem;
        }

        // Kept folders' markers move along unmentioned, and a folder holding nothing else reports no file's version.
        var count = string.Join(", ", new[] { Count(notes.Count, "note"), Count(attachments.Count(a => !FolderMarker.Is(a)), "attachment") }.Where(c => c is not null));
        var result = await CommitAsync(map, rewriteLinks, $"moved {source.Value}/ to {target.Value}/{(count.Length > 0 ? $" ({count})" : "")}", card: false, replacing, command, ct);
        return map.Count == 1 && FolderMarker.Is(map.Keys.First()) ? result with { Version = null } : result;
    }

    /// <summary>The text files among <paramref name="moves"/> that would land outside a skill's folder, named relative to
    /// <paramref name="source"/>, at most five and then "and N more"; null if there are none. <see cref="TreeWriter"/> copies by the same rule.</summary>
    internal static string? TextFilesOutsideSkills(IEnumerable<KeyValuePair<VaultPath, VaultPath>> moves, VaultPath source, string? skillsVault)
    {
        var names = moves
            .Where(m => AttachmentFormat.ForPath(m.Key)?.Kind == AttachmentKind.Text && !SkillPaths.IsInSkillFolder(m.Value, skillsVault))
            .Select(m => string.Join('/', m.Key.Segments.Skip(source.Segments.Count)))
            .Order(StringComparer.Ordinal).ToList();
        if (names.Count == 0) return null;
        return names.Count <= 5 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(5))} and {names.Count - 5} more";
    }

    /// <summary>The paths <paramref name="replacing"/> trashes: in its batch they don't count as existing.</summary>
    private static HashSet<VaultPath> Replaced(NoteBatch? replacing) =>
        replacing is null ? [] : [.. replacing.Trashes.Select(t => t.Path), .. replacing.AttachmentTrashes.Select(a => a.Path)];

    private static string? Count(int n, string what) => n switch { 0 => null, 1 => $"1 {what}", _ => $"{n} {what}s" };

    private static VaultPath Rebase(VaultPath path, VaultPath from, VaultPath to)
    {
        var result = to;
        foreach (var segment in path.Segments.Skip(from.Segments.Count)) result = result.Child(segment);
        return result;
    }

    /// <summary>Whether a note or an attachment is at <paramref name="path"/>.</summary>
    private async Task<bool> ExistsAsync(VaultPath path, CancellationToken ct) =>
        await store.GetAsync(path, ct) is not null || await StoreAttachments.GetAsync(store, path, ct) is not null;

    /// <summary>Whether notes or attachments are below <paramref name="path"/>.</summary>
    private async Task<bool> IsFolderAsync(VaultPath path, CancellationToken ct) =>
        (await store.ListNotesUnderAsync(path, ct)).Count > 0 || (await StoreAttachments.UnderAsync(store, path, ct)).Count > 0;

    /// <summary>Checks a target before anything is written. <paramref name="leaving"/> are paths moving away in
    /// the same batch, which don't count as existing.</summary>
    private async Task<ShellResult?> CheckTargetAsync(VaultPath source, VaultPath target, HashSet<VaultPath> leaving, string command, CancellationToken ct)
    {
        if (target.Vault != source.Vault)
        {
            return ShellResult.Error($"{command}: notes can't move between vaults ({source.Vault} → {target.Vault}); a vault is its own space");
        }

        if (AttachmentFormat.ForPath(source) is not null)
        {
            if (AttachmentFormat.RenameProblem(source, target) is { } renameProblem) return ShellResult.Error($"{command}: {target.Value}: {renameProblem}");
            if (FolderMarker.Is(target) && target.Segments.Count == 2)
            {
                return ShellResult.Error($"{command}: {FolderMarker.Shown(target)}: {FolderMarker.InVaultRoot}");
            }

            if (NoteNames.NameProblem(target) is { } nameProblem) return ShellResult.Error($"{command}: {target.Value}: {nameProblem}");
        }
        else if (NoteNames.Problem(target) is { } problem) return ShellResult.Error($"{command}: {target.Value}: {problem}");

        if (await store.IsDamagedAsync(target, ct)) return ShellResult.Error(Damage.Message(command, target));
        if (!leaving.Contains(target) && await ExistsAsync(target, ct))
        {
            return ShellResult.Error($"{command}: {target.Value} already exists; rm it first, or choose another name");
        }

        // A folder on the way may not be a note or an attachment.
        for (var dir = VaultPath.Root.Child(target.Segments[0]); dir.Segments.Count < target.Segments.Count - 1;)
        {
            dir = dir.Child(target.Segments[dir.Segments.Count]);
            if (!leaving.Contains(dir) && await ExistsAsync(dir, ct)) return ShellResult.Error($"{command}: {dir.Value}: Not a directory");
        }

        return null;
    }

    /// <summary>Builds one batch: every move, with the moved notes' own link rewrites, and the link rewrites in
    /// all other notes. A move changes no text, so a note or attachment without rewrites keeps its writer; rewritten
    /// notes get their own history entry, marked with the command ("(mv)"). What <paramref name="replacing"/> trashes joins the batch, and
    /// neither links from it nor to it are rewritten: a link to it now leads to what took its place.</summary>
    private async Task<ShellResult> CommitAsync(
        Dictionary<VaultPath, VaultPath> map, bool rewriteLinks, string message, bool card, NoteBatch? replacing, string command, CancellationToken ct)
    {
        var source = map.Keys.First();
        var vault = VaultPath.Root.Child(source.Segments[0]);
        var replaced = Replaced(replacing);
        var notes = (await store.ReadNotesUnderAsync(vault, ct)).Where(n => !replaced.Contains(n.Path)).ToList();
        var rewrites = !rewriteLinks ? []
            : Rewrites(notes, map, await StoreAttachments.PathsUnderAsync(store, vault, ct) is { } all ? [.. all.Where(p => !replaced.Contains(p))] : null);
        var now = time.GetUtcNow().UtcDateTime;
        var writer = caller.WriterIn(vault.Segments[0]);
        var reference = caller.ReferenceIn(vault.Segments[0]);
        var marked = Writers.Marked(writer, command);
        // Link rewrites join the writer's changeset like any write.
        var changeset = await Changeset.ForAsync(store, vault.Segments[0], reference, writer, caller.ChangesetIdle, now, ct);
        var batch = new NoteBatch { Changeset = changeset, IsMove = true };
        if (replacing is not null)
        {
            batch.Trashes.AddRange(replacing.Trashes);
            batch.AttachmentTrashes.AddRange(replacing.AttachmentTrashes);
        }

        var links = 0;
        var changed = new List<VaultPath>();
        NoteVersion? version = null;

        foreach (var (from, to) in map)
        {
            if (await store.GetAsync(from, ct) is not { } current)
            {
                if (await StoreAttachments.GetAsync(store, from, ct) is not { } attachment) return Changed(command, from);
                var movedAttachment = attachment with { Path = to, Revision = attachment.Revision + 1 };
                batch.AttachmentMoves.Add((attachment, movedAttachment));
                version = new NoteVersion(to.Value, movedAttachment.Version);
                continue;
            }

            var moved = current with { Path = to, Revision = current.Revision + 1 };
            if (rewrites.TryGetValue(to, out var own))
            {
                if (Apply(current.Content, own) is not { } content) return Changed(command, from);
                moved = NoteWriter.Next(current, content, marked, reference, now, changeset) with { Path = to };
                links += own.Count;
                changed.Add(to);
            }

            batch.Moves.Add((current, moved));
            version = new NoteVersion(to.Value, moved.Version);
        }

        foreach (var (path, list) in rewrites.Where(r => !map.ContainsValue(r.Key)))
        {
            // Rewriting a link is a write too: in a structural file only for those who may maintain the vault, unless
            // the board moves one of its cards.
            if (!card && caller.Refuse(command, path) is { } refused) return refused;
            if (await store.GetAsync(path, ct) is not { } current) return Changed(command, path);
            if (Apply(current.Content, list) is not { } content) return Changed(command, path);
            batch.Replaces.Add((current, NoteWriter.Next(current, content, marked, reference, now, changeset)));
            links += list.Count;
            changed.Add(path);
        }

        var conflicts = await store.TryCommitAsync(batch, writer, reference, now, ct);
        if (conflicts.Count > 0)
        {
            return ShellResult.Error(
                $"{command}: nothing was moved: {string.Join(", ", conflicts.Select(c => c.Value))} changed or appeared meanwhile; try again");
        }

        if (!rewriteLinks) message += "; links left as they are (--keep-links)";
        else if (changed.Count == 0) message += "; no links needed rewriting";
        else message += $"; rewrote {links} {(links == 1 ? "link" : "links")} in {string.Join(", ", changed.Order(Comparer<VaultPath>.Create((a, b) => TextOrder.Default.Compare(a.Value, b.Value))).Select(p => p.Value))}";

        return ShellResult.Ok(message) with { Version = map.Count == 1 ? version : null };
    }

    private static ShellResult Changed(string command, VaultPath path) =>
        ShellResult.Error($"{command}: nothing was moved: {path.Value} changed meanwhile; try again");

    /// <summary>An exact move's refusal of a folder that took the target's place after the caller chose it.</summary>
    private static ShellResult Appeared(string command, VaultPath target) =>
        ShellResult.Error($"{command}: nothing was moved: {target.Value}/ appeared meanwhile; try again");

    /// <summary>
    /// For each note (by its path after the move), the links whose target the move changes, with the text to
    /// put in their place: links to a moved note or attachment, and links that would now hit one by a name collision.
    /// <paramref name="map"/> holds notes and attachments; <paramref name="attachments"/> are the vault's attachments
    /// before the move, null if unknown (links to them then stay as they are).
    /// </summary>
    public static Dictionary<VaultPath, List<(NoteLink Link, string Replacement)>> Rewrites(
        IReadOnlyList<Note> notes, IReadOnlyDictionary<VaultPath, VaultPath> map, IReadOnlyList<VaultPath>? attachments = null)
    {
        var vault = map.Keys.First().Vault!;
        var before = new LinkResolver(vault, notes.Select(n => n.Path), attachments);
        var after = new LinkResolver(
            vault, notes.Select(n => map.GetValueOrDefault(n.Path, n.Path)), attachments?.Select(a => map.GetValueOrDefault(a, a)));
        var result = new Dictionary<VaultPath, List<(NoteLink, string)>>();
        foreach (var note in notes)
        {
            var oldPath = note.Path;
            var newPath = map.GetValueOrDefault(oldPath, oldPath);
            foreach (var link in NoteLink.Parse(note.Content))
            {
                if (link.Target.Trim().Length == 0) continue;
                var was = before.Resolve(oldPath, link.Target);
                if (!Found(was)) continue;

                var meant = map.GetValueOrDefault(was.Target!, was.Target!);
                var now = after.Resolve(newPath, link.Target);
                if (Found(now) && now.Target == meant) continue;

                if (!result.TryGetValue(newPath, out var list)) result[newPath] = list = [];
                // A wikilink keeps its form where it can; a Markdown link is written absolute, which never
                // depends on where the linking note is or on other names in the vault.
                list.Add((link, link is WikiLink w
                    ? w.WithTarget(NewTarget(w.Target, newPath, meant, map.ContainsKey(was.Target!), after))
                    : ((MarkdownLink)link).WithPath(meant)));
            }
        }

        return result;
    }

    /// <summary>
    /// The link text after the move (decided by Chris, 2026-10-23): keep the author's form, a bare name
    /// stays a bare name and a path keeps its length, if that resolves uniquely to the meant note or attachment (whose
    /// name keeps its extension). Relative
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
        return Found(resolved) && resolved.Target == meant ? kept : vaultPath;
    }

    /// <summary>Whether a link leads to one note or attachment.</summary>
    private static bool Found(LinkResolution r) => r.Kind is LinkKind.Note or LinkKind.Attachment && r.Target is not null;

    /// <summary>Replaces each link at its exact position; null if the text there is no longer that link.</summary>
    public static string? Apply(string content, List<(NoteLink Link, string Replacement)> links)
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
