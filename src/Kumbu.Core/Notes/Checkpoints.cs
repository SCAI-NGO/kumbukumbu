// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Diffs;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;

namespace Kumbu.Core.Notes;

/// <summary>A previewed restore of a folder: the call that made it and every note in scope, at its version then.</summary>
public sealed record RestorePlan(string Scope, string Name, IReadOnlyList<(VaultPath Path, string Version)> Current, DateTime Created);

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

    public async Task<ShellResult> MakeAsync(VaultPath path, string name, string by, CancellationToken ct)
    {
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
            name, path, folder, by, writer.Now,
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
        if (path.IsRoot) return ShellResult.Error("rmcp: give a path in the checkpoint's vault: rmcp /Demo <name>");
        var vault = path.Segments[0];
        if (await FindAsync(vault, name, ct) is not { } checkpoint) return ShellResult.Error(Missing("rmcp", vault, name));
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

    /// <summary><c>diff &lt;folder&gt; --checkpoint name</c>: which notes changed, appeared or went since.</summary>
    public async Task<ShellResult> ChangesAsync(CheckpointView view, VaultPath scope, CancellationToken ct)
    {
        var checkpoint = view.Checkpoint;
        var (then, now) = await BothAsync(view, scope, ct);
        var rows = new List<string>();
        foreach (var path in then.Keys.Union(now.Keys).OrderBy(p => p.Value, TextOrder.Default))
        {
            var before = then.GetValueOrDefault(path);
            var after = now.GetValueOrDefault(path)?.Content;
            if (before == after) continue;
            var (added, removed) = TextDiff.Count(before ?? "", after ?? "");
            rows.Add(before is null ? $"new      {path.Value}  +{added}"
                : after is null ? $"removed  {path.Value}  (-{removed})"
                : $"changed  {path.Value}  +{added} -{removed}");
        }

        var where = Where(checkpoint, scope);
        if (rows.Count == 0) return ShellResult.Ok($"(no changes in {where} since checkpoint '{checkpoint.Name}')");
        return ShellResult.Ok(Lines.Join([
            $"changes in {where} since checkpoint '{checkpoint.Name}' ({History.Time(checkpoint.CreatedAt)}, {checkpoint.CreatedBy}):",
            .. rows,
            $"diff <note> --checkpoint {checkpoint.Name} shows one note; restore --checkpoint {checkpoint.Name} <path> goes back",
        ]));
    }

    /// <summary>
    /// <c>restore --checkpoint name &lt;path&gt;</c>. A note goes back at once (an undoable write); a folder
    /// gets a preview and a plan token first, and its apply makes the folder match the checkpoint exactly:
    /// changed notes get their content then, removed ones come back, newer ones go to the trash.
    /// </summary>
    public async Task<ShellResult> RestoreAsync(VaultPath path, string name, string? token, string by, CancellationToken ct)
    {
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

    private async Task<ShellResult> PreviewAsync(CheckpointView view, VaultPath scope, CancellationToken ct)
    {
        var checkpoint = view.Checkpoint;
        var (then, now) = await BothAsync(view, scope, ct);
        var rows = new List<string>();
        foreach (var path in then.Keys.Union(now.Keys).OrderBy(p => p.Value, TextOrder.Default))
        {
            var before = now.GetValueOrDefault(path)?.Content;
            var after = then.GetValueOrDefault(path);
            if (before == after) continue;
            var (added, removed) = TextDiff.Count(before ?? "", after ?? "");
            rows.Add(before is null ? $"  back      {path.Value}"
                : after is null ? $"  to trash  {path.Value}  (created since)"
                : $"  changed   {path.Value}  +{added} -{removed}");
        }

        var where = Where(checkpoint, scope);
        if (rows.Count == 0) return ShellResult.Ok($"{where} already matches checkpoint '{checkpoint.Name}'; nothing to restore");

        var current = now.Values.OrderBy(n => n.Path.Value, StringComparer.Ordinal).Select(n => (n.Path, n.Version)).ToList();
        var token = VersionToken.Of(string.Join('\n', [scope.Value, checkpoint.Name, checkpoint.CreatedAt.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), .. current.Select(c => $"{c.Path.Value} {c.Version}")]));
        var text = Lines.Join([
            $"restore {where} to checkpoint '{checkpoint.Name}' ({History.Time(checkpoint.CreatedAt)}, {checkpoint.CreatedBy}):",
            .. rows,
            "",
            $"plan: {token}",
            $"To apply: restore --checkpoint {checkpoint.Name} --plan {token} {SectionLookup.Quote(scope)}",
            "It changes exactly these notes, or nothing if one of them changed since. Notes going to the trash stay there for 30 days.",
        ]);
        if (text.Length > OutputLimit.MaxChars)
        {
            return ShellResult.Error($"restore: {Count(rows.Count, "note")} differ from checkpoint '{checkpoint.Name}', too many for one preview; restore a smaller folder");
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

        var (then, now) = await BothAsync(view, scope, ct);
        var planned = plan.Current.ToDictionary(c => c.Path, c => c.Version);
        var changed = now.Values.Where(n => planned.GetValueOrDefault(n.Path) != n.Version).Select(n => n.Path)
            .Concat(planned.Keys.Where(p => !now.ContainsKey(p)))
            .Select(p => p.Value).Order(StringComparer.Ordinal).ToList();
        if (changed.Count > 0)
        {
            plans.Failed(token, $"{string.Join(", ", changed)} changed since the preview", at);
            return ShellResult.Error($"restore: nothing was restored: {string.Join(", ", changed)} changed or appeared since the preview; preview again");
        }

        var label = $"{by} (restore)";
        var batch = new NoteBatch();
        foreach (var (path, content) in then)
        {
            if (now.TryGetValue(path, out var existing))
            {
                if (existing.Content != content) batch.Replaces.Add((existing, NoteWriter.Next(existing, content, label, at, changeset: null, keepCurrent: true)));
            }
            else batch.Creates.Add(new StoredNote(path, content, 1, label, at, []));
        }

        batch.Trashes.AddRange(now.Values.Where(n => !then.ContainsKey(n.Path)));
        var conflicts = await store.TryCommitAsync(batch, label, at, ct);
        if (conflicts.Count > 0)
        {
            var paths = string.Join(", ", conflicts.Select(c => c.Value));
            plans.Failed(token, $"{paths} changed since the preview", at);
            return ShellResult.Error($"restore: nothing was restored: {paths} changed or appeared since the preview; preview again");
        }

        var message = $"restored {Where(checkpoint, scope)} to checkpoint '{checkpoint.Name}': {batch.Replaces.Count} changed, "
            + $"{batch.Creates.Count} back, {batch.Trashes.Count} to the trash";
        plans.Applied(token, message, at);
        return ShellResult.Ok(message);
    }

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
