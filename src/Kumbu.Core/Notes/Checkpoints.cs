// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Diffs;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;

namespace Kumbu.Core.Notes;

/// <summary>A previewed restore of a folder: the call that made it and every note in scope, at its version then.</summary>
public sealed record RestorePlan(string Scope, string Name, IReadOnlyList<(VaultPath Path, string Version)> Current, DateTime Created);

public enum RestoreChangeKind
{
    /// <summary>The note gets its content then.</summary>
    Changed,

    /// <summary>The note was removed since and comes back.</summary>
    Back,

    /// <summary>The note was made since and goes to the trash.</summary>
    Trash,

    /// <summary>The note was moved since (from <see cref="RestoreChange.From"/>) and moves back.</summary>
    Moved,
}

/// <summary>
/// One note a restore to a checkpoint changes, at its path in the checkpoint. <paramref name="Lines"/>: the lines
/// added and removed by the restore, for a note whose content changes (null for one that only comes back, goes
/// to the trash, or moves back unchanged).
/// </summary>
public sealed record RestoreChange(RestoreChangeKind Kind, VaultPath Path, VaultPath? From, (int Added, int Removed)? Lines);

/// <summary>
/// What a restore to a checkpoint would do, before it does it: the notes it changes (in text order), the links
/// outside that follow notes moved back, and the links outside that would lead nowhere. <paramref name="Plan"/>
/// applies it, null when there is nothing to restore.
/// </summary>
public sealed record RestorePreview(
    Checkpoint Checkpoint,
    string Where,
    IReadOnlyList<RestoreChange> Changes,
    int RewrittenLinks,
    IReadOnlyList<VaultPath> RewrittenIn,
    IReadOnlyList<string> Broken,
    string? Plan);

/// <summary>
/// Named checkpoints (concept §4): <c>mkcp</c>, <c>lscp</c>, <c>rmcp</c>, and what <c>diff</c> and
/// <c>restore</c> do with one. Reading from a checkpoint (<c>--checkpoint</c>) goes through a
/// <see cref="CheckpointView"/>.
/// </summary>
public sealed class Checkpoints(INoteStore store, NoteWriter writer, Plans plans, int limit = Checkpoints.DefaultLimit)
{
    public const int DefaultLimit = 10;

    public int Limit => limit;

    public Task<IReadOnlyList<string>> VaultsAsync(CancellationToken ct) => store.ListVaultsAsync(ct);

    /// <summary>Whether a note exists at the path now (to tell "created since" from "no such note").</summary>
    public async Task<bool> ExistsNowAsync(VaultPath path, CancellationToken ct) => await store.ReadAsync(path, ct) is not null;

    /// <summary>The vaults that have a checkpoint of that name.</summary>
    public async Task<IReadOnlyList<string>> VaultsWithAsync(string name, CancellationToken ct) =>
        [.. (await store.ListCheckpointsAsync(VaultPath.Root, ct)).Where(c => c.Name == name).Select(c => c.Vault).Distinct()];

    public async Task<ShellResult> MakeAsync(VaultPath path, string name, CancellationToken ct)
    {
        if (writer.Caller.Refuse("mkcp", path) is { } refused) return refused;
        if (path.IsRoot) return ShellResult.Error("mkcp: a checkpoint covers a vault, a folder or a note, e.g. mkcp /Demo before-cleanup");
        if (!Checkpoint.IsValidName(name)) return ShellResult.Error($"mkcp: '{name}' is not a checkpoint name: {Checkpoint.NameRule}");

        var vault = path.Segments[0];
        if (!(await store.ListVaultsAsync(ct)).Contains(vault)) return ShellResult.Error($"mkcp: {path.Value}: No such file or directory");

        var existing = await store.ListCheckpointsAsync(VaultPath.Root.Child(vault), ct);
        if (existing.FirstOrDefault(c => c.Name == name) is { } taken)
        {
            return ShellResult.Error(
                $"mkcp: checkpoint '{name}' exists in {vault} (of {taken.Scope}, {History.Time(taken.CreatedAt)}); choose another name, or 'rmcp /{vault} {name}' first");
        }

        if (existing.Count >= limit)
        {
            return ShellResult.Error(
                $"mkcp: {vault} has {existing.Count} checkpoints, the limit; 'lscp /{vault}' lists them, 'rmcp /{vault} <name>' removes one");
        }

        IReadOnlyList<Note> notes;
        var folder = false;
        if (await store.ReadAsync(path, ct) is { } content) notes = [new Note(path, content)];
        else
        {
            if (await store.IsDamagedAsync(path, ct)) return ShellResult.Error(Damage.Message("mkcp", path));
            notes = await store.ReadNotesUnderAsync(path, ct);
            folder = true;
            if (notes.Count == 0 && path.Segments.Count > 1)
            {
                return ShellResult.Error($"mkcp: {path.Value}: No such file or directory{await Moves.HintAsync(store, path, ct)}");
            }
        }

        var checkpoint = new Checkpoint(
            name, path, folder, writer.Caller.Writer, writer.Now,
            [.. notes.OrderBy(n => n.Path.Value, TextOrder.Default).Select(n => new CheckpointNote(n.Path, VersionToken.Of(n.Content)))]);
        if (!await store.TryAddCheckpointAsync(checkpoint, notes, ct))
        {
            return ShellResult.Error($"mkcp: checkpoint '{name}' was made by someone else just now; choose another name");
        }

        var damaged = folder ? await store.CountDamagedAsync(VaultPath.Root.Child(vault), ct) : 0;
        var note = damaged > 0 ? $"; {Count(damaged, "damaged note")} in {vault} can't be part of it" : "";
        return ShellResult.Ok(
            $"checkpoint '{name}' of {checkpoint.Scope} ({Count(notes.Count, "note")}){note}; read from it with --checkpoint {name}, "
            + $"e.g. {(folder ? "ls" : "cat")} --checkpoint {name} {SectionLookup.Quote(path)}");
    }

    public async Task<ShellResult> ListAsync(VaultPath path, CancellationToken ct)
    {
        var all = await store.ListCheckpointsAsync(path.IsRoot ? VaultPath.Root : VaultPath.Root.Child(path.Segments[0]), ct);
        var shown = path.Segments.Count <= 1 ? all : [.. all.Where(c => c.Covers(path) || Checkpoint.IsBelow(c.Path, path))];
        var lines = new List<string>();
        if (shown.Count == 0) lines.Add(path.IsRoot ? "(no checkpoints)" : $"(no checkpoints for {path.Value})");
        else
        {
            var nameWidth = shown.Max(c => c.Name.Length);
            var scopeWidth = shown.Max(c => c.Scope.Length);
            var byWidth = shown.Max(c => c.CreatedBy.Length);
            lines.AddRange(shown.Select(c =>
                $"{c.Name.PadRight(nameWidth)}  {c.Scope.PadRight(scopeWidth)}  {History.Time(c.CreatedAt)}  {c.CreatedBy.PadRight(byWidth)}  {Count(c.Notes.Count, "note")}"));
        }

        if (!path.IsRoot) lines.Add($"{all.Count} of {limit} checkpoints in {path.Segments[0]}");
        return ShellResult.Ok(Lines.Join(lines));
    }

    public async Task<ShellResult> RemoveAsync(VaultPath path, string name, CancellationToken ct)
    {
        if (writer.Caller.Refuse("rmcp", path) is { } refused) return refused;
        if (path.IsRoot) return ShellResult.Error("rmcp: give a path in the checkpoint's vault: rmcp /Demo <name>");
        var vault = path.Segments[0];
        if (await FindAsync(vault, name, ct) is not { } checkpoint) return ShellResult.Error(Missing("rmcp", vault, name));
        if (path.Segments.Count > 1 && !checkpoint.Covers(path))
        {
            return ShellResult.Error($"rmcp: checkpoint '{name}' covers {checkpoint.Scope}, not {path.Value}; give its path or the vault (rmcp /{vault} {name})");
        }

        return await store.TryRemoveCheckpointAsync(vault, name, ct)
            ? ShellResult.Ok($"removed checkpoint '{name}' (of {checkpoint.Scope}); the notes themselves are unchanged")
            : ShellResult.Error(Missing("rmcp", vault, name));
    }

    public async Task<Checkpoint?> FindAsync(string vault, string name, CancellationToken ct) =>
        (await store.ListCheckpointsAsync(VaultPath.Root.Child(vault), ct)).FirstOrDefault(c => c.Name == name);

    /// <summary>The checkpoint as a read-only tree, or the error a command should give.</summary>
    public async Task<(CheckpointView? View, ShellResult? Error)> OpenAsync(string command, string vault, string name, CancellationToken ct)
    {
        if (await FindAsync(vault, name, ct) is not { } checkpoint) return (null, ShellResult.Error(Missing(command, vault, name)));
        return (new CheckpointView(checkpoint, await store.ReadCheckpointAsync(checkpoint, ct)), null);
    }

    public static string Missing(string command, string vault, string name) =>
        $"{command}: no checkpoint '{name}' in {vault} ('lscp /{vault}' lists them)";

    /// <summary>
    /// What a command with <paramref name="path"/> works on: the path itself if the checkpoint covers it, or
    /// the checkpoint's own path if it lies below <paramref name="path"/>. Null if they don't meet.
    /// </summary>
    public static VaultPath? Scope(Checkpoint checkpoint, VaultPath path) =>
        checkpoint.Covers(path) ? path : checkpoint.Relates(path) ? checkpoint.Path : null;

    /// <summary><c>diff &lt;folder&gt; --checkpoint name</c>: which notes changed, appeared, went or moved since.</summary>
    public async Task<ShellResult> ChangesAsync(CheckpointView view, VaultPath scope, CancellationToken ct)
    {
        var checkpoint = view.Checkpoint;
        var work = await PlanAsync(view, scope, ct);
        var rows = new List<(string Path, string Row)>();
        foreach (var (path, (now, then)) in work.Changed) rows.Add((path.Value, $"changed  {path.Value}  {Delta(then, now.Content)}"));
        foreach (var (path, then) in work.Back) rows.Add((path.Value, $"removed  {path.Value}  (-{TextDiff.Count(then, "").Removed})"));
        foreach (var note in work.Trash) rows.Add((note.Path.Value, $"new      {note.Path.Value}  +{TextDiff.Count("", note.Content).Added}"));
        foreach (var (to, (now, then)) in work.Moved)
        {
            rows.Add((to.Value, $"moved    {to.Value} → {now.Path.Value}{(then == now.Content ? "" : "  " + Delta(then, now.Content))}"));
        }

        var where = Where(checkpoint, scope);
        if (rows.Count == 0) return ShellResult.Ok($"(no changes in {where} since checkpoint '{checkpoint.Name}')");
        return ShellResult.Ok(Lines.Join([
            $"changes in {where} since checkpoint '{checkpoint.Name}' ({History.Time(checkpoint.CreatedAt)}, {checkpoint.CreatedBy}):",
            .. rows.OrderBy(r => r.Path, TextOrder.Default).Select(r => r.Row),
            $"diff <note> --checkpoint {checkpoint.Name} shows one note; restore --checkpoint {checkpoint.Name} <path> goes back",
        ]));
    }

    /// <summary>
    /// <c>restore --checkpoint name &lt;path&gt;</c>. A note goes back at once (an undoable write); a folder
    /// gets a preview and a plan token first, and its apply makes the folder match the checkpoint exactly:
    /// changed notes get their content then, removed ones come back, newer ones go to the trash.
    /// </summary>
    public async Task<ShellResult> RestoreAsync(VaultPath path, string name, string? token, CancellationToken ct)
    {
        if (writer.Caller.Refuse("restore", path) is { } refused) return refused;
        var by = writer.Caller.Writer;
        if (path.IsRoot) return ShellResult.Error("restore: give a path in the checkpoint's vault, e.g. restore --checkpoint before-cleanup /Demo");
        var (view, error) = await OpenAsync("restore", path.Segments[0], name, ct);
        if (view is null) return error!;
        var checkpoint = view.Checkpoint;
        if (Scope(checkpoint, path) is not { } scope)
        {
            return ShellResult.Error($"restore: {path.Value} is not in checkpoint '{name}' (it covers {checkpoint.Scope})");
        }

        var isNote = (!checkpoint.Folder && scope == checkpoint.Path) || await view.ReadAsync(scope, ct) is not null || await store.ReadAsync(scope, ct) is not null;
        if (isNote)
        {
            if (token is not null) return ShellResult.Error("restore: --plan is for folders; a note is restored at once");
            return await RestoreNoteAsync(view, scope, by, ct);
        }

        return token is null ? await PreviewAsync(view, scope, ct) : await ApplyAsync(view, scope, token, by, ct);
    }

    private async Task<ShellResult> RestoreNoteAsync(CheckpointView view, VaultPath path, string by, CancellationToken ct)
    {
        var name = view.Checkpoint.Name;
        var then = await view.ReadAsync(path, ct);
        var existing = await store.GetAsync(path, ct);
        if (then is null)
        {
            return existing is null
                ? ShellResult.Error($"restore: {path.Value}: No such file or directory, now or in checkpoint '{name}'")
                : ShellResult.Error($"restore: {path.Value} was not in checkpoint '{name}' (created since); rm it to match the checkpoint");
        }

        var label = $"{by} (restore)";
        if (existing is null)
        {
            if (await store.IsDamagedAsync(path, ct)) return ShellResult.Error(Damage.Message("restore", path));
            var created = new StoredNote(path, then, 1, label, writer.Now, []);
            return await store.TryCreateAsync(created, ct)
                ? Written($"restored {path.Value} from checkpoint '{name}' (it had been removed)", created)
                : ShellResult.Error($"restore: {path.Value} was created by someone else just now; try again");
        }

        if (existing.Content == then) return Written($"{path.Value} is already as in checkpoint '{name}'", existing);

        // In no changeset, like every restore: a checkpoint of its own, so it can be undone on its own.
        var next = NoteWriter.Next(existing, then, label, writer.Now, changeset: null, keepCurrent: true);
        return await store.TryReplaceAsync(existing, next, ct)
            ? Written($"restored {path.Value} to checkpoint '{name}'", next)
            : ShellResult.Error($"restore: {path.Value} changed while restoring it; try again");
    }

    /// <summary>
    /// What a folder restore does. <see cref="Moved"/>: notes moved since the checkpoint (by the move records),
    /// by their checkpoint path; they move back, keeping their history, and the links the move rewrote follow
    /// them back (<see cref="Rewrites"/>, in notes outside the scope; notes inside get their content then).
    /// <see cref="Broken"/>: links outside the scope that would lead nowhere afterwards.
    /// </summary>
    private sealed record RestoreWork(
        Dictionary<VaultPath, (StoredNote Now, string Then)> Changed,
        Dictionary<VaultPath, string> Back,
        Dictionary<VaultPath, (StoredNote Now, string Then)> Moved,
        List<StoredNote> Trash,
        Dictionary<VaultPath, List<(Links.WikiLink Link, string Replacement)>> Rewrites,
        List<string> Broken)
    {
        public bool IsEmpty => Changed.Count == 0 && Back.Count == 0 && Moved.Count == 0 && Trash.Count == 0;

        /// <summary>Every note the apply depends on, at the version the preview saw.</summary>
        public IEnumerable<(VaultPath Path, string Version)> Current(Dictionary<VaultPath, StoredNote> inScope) =>
            inScope.Values.Concat(Moved.Values.Select(m => m.Now)).DistinctBy(n => n.Path)
                .OrderBy(n => n.Path.Value, StringComparer.Ordinal).Select(n => (n.Path, n.Version));
    }

    private const int MaxMoveHops = 10;

    private async Task<RestoreWork> PlanAsync(CheckpointView view, VaultPath scope, CancellationToken ct)
    {
        var checkpoint = view.Checkpoint;
        var (then, now) = await BothAsync(view, scope, ct);
        var changed = new Dictionary<VaultPath, (StoredNote, string)>();
        var back = new Dictionary<VaultPath, string>();
        var moved = new Dictionary<VaultPath, (StoredNote Now, string Then)>();
        foreach (var (path, content) in then)
        {
            if (now.TryGetValue(path, out var existing))
            {
                if (existing.Content != content) changed[path] = (existing, content);
            }
            else if (await MovedToAsync(path, checkpoint, then, ct) is { } note && moved.Values.All(m => m.Now.Path != note.Path)) moved[path] = (note, content);
            else back[path] = content;
        }

        var leaving = moved.Values.Select(m => m.Now.Path).ToHashSet();
        var trash = now.Values.Where(n => !then.ContainsKey(n.Path) && !leaving.Contains(n.Path)).ToList();

        var rewrites = new Dictionary<VaultPath, List<(Links.WikiLink, string)>>();
        var broken = new List<string>();
        var vault = VaultPath.Root.Child(scope.Segments[0]);
        var notes = await store.ReadNotesUnderAsync(vault, ct);
        bool Outside(VaultPath p) => !(p == scope || Checkpoint.IsBelow(p, scope)) && !leaving.Contains(p);
        if (moved.Count > 0)
        {
            var map = moved.ToDictionary(m => m.Value.Now.Path, m => m.Key);
            foreach (var (path, list) in NoteMover.Rewrites(notes, map))
            {
                if (Outside(path)) rewrites[path] = list;
            }
        }

        // Links outside the scope that lead to a note now and nowhere after the restore.
        var trashed = trash.Select(t => t.Path).ToHashSet();
        var afterPaths = notes.Select(n => n.Path).Where(p => !trashed.Contains(p) && !leaving.Contains(p)).Concat(back.Keys).Concat(moved.Keys).Distinct().ToList();
        var before = new Links.LinkResolver(vault.Segments[0], notes.Select(n => n.Path));
        var after = new Links.LinkResolver(vault.Segments[0], afterPaths);
        foreach (var note in notes.Where(n => Outside(n.Path)).OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            var content = rewrites.TryGetValue(note.Path, out var list) ? NoteMover.Apply(note.Content, list) ?? note.Content : note.Content;
            foreach (var link in Links.NoteLink.Parse(content))
            {
                if (link.Target.Trim().Length == 0) continue;
                var was = before.Resolve(note.Path, link.Target);
                if (was.Kind != Links.LinkKind.Note || after.Resolve(note.Path, link.Target).Kind == Links.LinkKind.Note) continue;
                var why = trashed.Contains(was.Target!) ? "going to the trash" : "gone after the restore";
                broken.Add($"  {note.Path.Value}:{link.Line + 1}: {link.Raw} (now {was.Target!.Value}, {why})");
            }
        }

        return new RestoreWork(changed, back, moved, trash, rewrites, broken);
    }

    /// <summary>The note a checkpoint path was moved to since the checkpoint, following later moves; null if it
    /// wasn't moved, or the note there now is another one (in the checkpoint at its own path).</summary>
    private async Task<StoredNote?> MovedToAsync(VaultPath path, Checkpoint checkpoint, Dictionary<VaultPath, string> then, CancellationToken ct)
    {
        var at = path;
        var since = checkpoint.CreatedAt;
        for (var hop = 0; hop < MaxMoveHops; hop++)
        {
            if (await store.FindMoveAsync(at, ct) is not { } move || move.MovedAt < since) return null;
            at = move.To;
            since = move.MovedAt;
            if (await store.GetAsync(at, ct) is { } note) return then.ContainsKey(at) && checkpoint.Covers(at) ? null : note;
        }

        return null;
    }

    /// <summary>
    /// The restore of a whole checkpoint, as the web app offers it: a preview with a plan first, for a note too
    /// (where <c>restore --checkpoint</c> restores at once), applied by <see cref="ApplyWholeAsync"/>.
    /// </summary>
    public async Task<(RestorePreview? Preview, ShellResult? Error)> PreviewWholeAsync(string vault, string name, CancellationToken ct)
    {
        var vaultPath = VaultPath.Root.Child(vault);
        if (writer.Caller.Refuse("restore", vaultPath) is { } refused) return (null, refused);
        var (view, error) = await OpenAsync("restore", vault, name, ct);
        if (view is null) return (null, error);
        var (preview, current) = await PreviewOfAsync(view, view.Checkpoint.Path, ct);
        if (preview.Plan is { } token) plans.Add(token, new RestorePlan(view.Checkpoint.Path.Value, name, current, writer.Now), writer.Now);
        return (preview, null);
    }

    /// <summary>Applies the plan of <see cref="PreviewWholeAsync"/>: all of it, or nothing if a note changed since.</summary>
    public async Task<ShellResult> ApplyWholeAsync(string vault, string name, string token, CancellationToken ct)
    {
        var vaultPath = VaultPath.Root.Child(vault);
        if (writer.Caller.Refuse("restore", vaultPath) is { } refused) return refused;
        var (view, error) = await OpenAsync("restore", vault, name, ct);
        if (view is null) return error!;
        return await ApplyAsync(view, view.Checkpoint.Path, token, writer.Caller.Writer, ct);
    }

    /// <summary>The preview of a restore of <paramref name="scope"/>, and the versions its plan depends on.</summary>
    private async Task<(RestorePreview Preview, IReadOnlyList<(VaultPath Path, string Version)> Current)> PreviewOfAsync(
        CheckpointView view, VaultPath scope, CancellationToken ct)
    {
        var checkpoint = view.Checkpoint;
        var work = await PlanAsync(view, scope, ct);
        var changes = new List<RestoreChange>();
        foreach (var (path, (now, then)) in work.Changed) changes.Add(new(RestoreChangeKind.Changed, path, null, TextDiff.Count(now.Content, then)));
        foreach (var path in work.Back.Keys) changes.Add(new(RestoreChangeKind.Back, path, null, null));
        foreach (var note in work.Trash) changes.Add(new(RestoreChangeKind.Trash, note.Path, null, null));
        foreach (var (to, (now, then)) in work.Moved)
        {
            changes.Add(new(RestoreChangeKind.Moved, to, now.Path, now.Content == then ? null : TextDiff.Count(now.Content, then)));
        }

        var (_, inScope) = await BothAsync(view, scope, ct);
        var current = work.Current(inScope).ToList();
        var token = work.IsEmpty
            ? null
            : VersionToken.Of(string.Join('\n', [scope.Value, checkpoint.Name, checkpoint.CreatedAt.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), .. current.Select(c => $"{c.Path.Value} {c.Version}")]));
        var preview = new RestorePreview(
            checkpoint,
            Where(checkpoint, scope),
            [.. changes.OrderBy(c => c.Path.Value, TextOrder.Default)],
            work.Rewrites.Values.Sum(l => l.Count),
            [.. work.Rewrites.Keys.OrderBy(p => p.Value, StringComparer.Ordinal)],
            [.. work.Broken.Select(b => b[2..])],
            token);
        return (preview, current);
    }

    private async Task<ShellResult> PreviewAsync(CheckpointView view, VaultPath scope, CancellationToken ct)
    {
        var checkpoint = view.Checkpoint;
        var (preview, current) = await PreviewOfAsync(view, scope, ct);
        var where = preview.Where;
        if (preview.Plan is not { } token) return ShellResult.Ok($"{where} already matches checkpoint '{checkpoint.Name}'; nothing to restore");

        var lines = new List<string>
        {
            $"restore {where} to checkpoint '{checkpoint.Name}' ({History.Time(checkpoint.CreatedAt)}, {checkpoint.CreatedBy}):",
        };
        lines.AddRange(preview.Changes.Select(c => c.Kind switch
        {
            RestoreChangeKind.Changed => $"  changed     {c.Path.Value}  {Delta(c.Lines!.Value)}",
            RestoreChangeKind.Back => $"  back        {c.Path.Value}",
            RestoreChangeKind.Trash => $"  to trash    {c.Path.Value}  (created since)",
            _ => $"  moved back  {c.From!.Value} → {c.Path.Value}{(c.Lines is { } delta ? "  " + Delta(delta) : "")}",
        }));
        if (preview.RewrittenLinks > 0)
        {
            lines.Add($"Links follow the notes moved back: {Count(preview.RewrittenLinks, "link")} rewritten in {string.Join(", ", preview.RewrittenIn.Select(p => p.Value))}.");
        }

        if (preview.Broken.Count > 0)
        {
            lines.Add($"After the restore, {Count(preview.Broken.Count, "link")} outside {where} would lead nowhere:");
            lines.AddRange(preview.Broken.Select(b => "  " + b));
        }

        lines.AddRange([
            "",
            $"plan: {token}",
            $"To apply: restore --checkpoint {checkpoint.Name} --plan {token} {SectionLookup.Quote(scope)}",
            "It changes exactly these notes, or nothing if one of them changed since. Notes going to the trash stay there for 30 days.",
        ]);
        var text = Lines.Join(lines);
        if (text.Length > OutputLimit.MaxChars)
        {
            return ShellResult.Error($"restore: {Count(preview.Changes.Count, "note")} differ from checkpoint '{checkpoint.Name}', too many for one preview; restore a smaller folder");
        }

        plans.Add(token, new RestorePlan(scope.Value, checkpoint.Name, current, writer.Now), writer.Now);
        return ShellResult.Ok(text);
    }

    private async Task<ShellResult> ApplyAsync(CheckpointView view, VaultPath scope, string token, string by, CancellationToken ct)
    {
        var checkpoint = view.Checkpoint;
        var at = writer.Now;
        if (plans.Take<RestorePlan>(token, at) is not { } plan) return ShellResult.Error(plans.Unavailable("restore", token, at));
        if (plan.Scope != scope.Value || plan.Name != checkpoint.Name)
        {
            plans.Return(token, plan, plan.Created);
            return ShellResult.Error("restore: this plan was made for another path or checkpoint; make the same call as the preview, with --plan");
        }

        var work = await PlanAsync(view, scope, ct);
        var (_, inScope) = await BothAsync(view, scope, ct);
        var current = work.Current(inScope).ToDictionary(c => c.Path, c => c.Version);
        var planned = plan.Current.ToDictionary(c => c.Path, c => c.Version);
        var changed = current.Where(c => planned.GetValueOrDefault(c.Key) != c.Value).Select(c => c.Key)
            .Concat(planned.Keys.Where(p => !current.ContainsKey(p)))
            .Select(p => p.Value).Distinct().Order(StringComparer.Ordinal).ToList();
        if (changed.Count > 0) return Failed(token, string.Join(", ", changed), at);

        var label = $"{by} (restore)";
        var batch = new NoteBatch();
        foreach (var (now1, then) in work.Changed.Values) batch.Replaces.Add((now1, NoteWriter.Next(now1, then, label, at, changeset: null, keepCurrent: true)));
        foreach (var (path, then) in work.Back) batch.Creates.Add(new StoredNote(path, then, 1, label, at, []));
        foreach (var (to, (moving, then)) in work.Moved)
        {
            var target = moving.Content == then
                ? moving with { Path = to, Revision = moving.Revision + 1 }
                : NoteWriter.Next(moving, then, label, at, changeset: null, keepCurrent: true) with { Path = to };
            batch.Moves.Add((moving, target));
        }

        foreach (var (path, list) in work.Rewrites)
        {
            if (await store.GetAsync(path, ct) is not { } linking || NoteMover.Apply(linking.Content, list) is not { } content) return Failed(token, path.Value, at);
            batch.Replaces.Add((linking, NoteWriter.Next(linking, content, label, at, changeset: null, keepCurrent: true)));
        }

        batch.Trashes.AddRange(work.Trash);
        var conflicts = await store.TryCommitAsync(batch, label, at, ct);
        if (conflicts.Count > 0) return Failed(token, string.Join(", ", conflicts.Select(c => c.Value)), at);

        var message = $"restored {Where(checkpoint, scope)} to checkpoint '{checkpoint.Name}': {work.Changed.Count} changed, "
            + $"{work.Back.Count} back, {work.Moved.Count} moved back, {work.Trash.Count} to the trash";
        if (work.Rewrites.Count > 0)
        {
            message += $"; rewrote {Count(work.Rewrites.Values.Sum(l => l.Count), "link")} in {string.Join(", ", work.Rewrites.Keys.Select(p => p.Value).Order(StringComparer.Ordinal))}";
        }

        plans.Applied(token, message, at);
        return ShellResult.Ok(message);
    }

    private ShellResult Failed(string token, string paths, DateTime at)
    {
        var reason = $"{paths} changed or appeared since the preview";
        plans.Failed(token, reason, at);
        return ShellResult.Error($"restore: nothing was restored: {reason}; preview again");
    }

    private static string Delta(string from, string to) => Delta(TextDiff.Count(from, to));

    private static string Delta((int Added, int Removed) lines) => $"+{lines.Added} -{lines.Removed}";

    /// <summary>The notes in scope as the checkpoint holds them, and as they are now.</summary>
    private async Task<(Dictionary<VaultPath, string> Then, Dictionary<VaultPath, StoredNote> Now)> BothAsync(
        CheckpointView view, VaultPath scope, CancellationToken ct)
    {
        var then = new Dictionary<VaultPath, string>();
        if (await view.ReadAsync(scope, ct) is { } single) then[scope] = single;
        foreach (var note in await view.ReadNotesUnderAsync(scope, ct)) then[note.Path] = note.Content;

        var now = new Dictionary<VaultPath, StoredNote>();
        IEnumerable<VaultPath> paths = await store.ReadAsync(scope, ct) is not null ? [scope] : await store.ListNotesUnderAsync(scope, ct);
        foreach (var path in paths)
        {
            if (await store.GetAsync(path, ct) is { } note) now[path] = note;
        }

        return (then, now);
    }

    private static string Where(Checkpoint checkpoint, VaultPath scope) =>
        scope == checkpoint.Path ? checkpoint.Scope : scope.Value + "/";

    private static ShellResult Written(string message, StoredNote note) =>
        ShellResult.Ok(message) with { Version = new NoteVersion(note.Path.Value, note.Version) };

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
