// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary><c>version</c>: one from the note's history; left out for a note in the trash, the most recently
/// removed one at the path comes back. <c>expected</c>: the version the caller believes is current, so a restore
/// never goes on top of a newer write it hasn't seen (409 with the note as it is, as a write's).</summary>
public sealed record RestoreRequest(string? Version = null, string? Expected = null);

/// <summary>The open note's version and last write, without its content: what the web app polls. <c>lastWriterKind</c> as on a note.</summary>
public sealed record NoteVersionView(string Path, string Version, string LastWriter, DateTime LastWriteAt, string? LastWriterKind = null);

/// <summary>A removed note; <c>expiresAt</c>: when it leaves the trash for good, <c>size</c> in bytes (UTF-8).</summary>
public sealed record TrashEntryView(string Path, string Version, DateTime DeletedAt, string DeletedBy, DateTime ExpiresAt, int Lines, int Size);

/// <summary>The vault's removed notes, most recently removed first; each stays <c>retentionDays</c>.</summary>
public sealed record TrashView(int RetentionDays, IReadOnlyList<TrashEntryView> Notes);

/// <summary>A named checkpoint; <c>scope</c> what it covers (a folder or vault ends in '/'), <c>notes</c> how many it holds.</summary>
public sealed record CheckpointEntryView(string Name, string Scope, string CreatedBy, DateTime CreatedAt, int Notes);

/// <summary>The vault's checkpoints, oldest first; a vault keeps at most <c>limit</c>.</summary>
public sealed record CheckpointsView(int Limit, IReadOnlyList<CheckpointEntryView> Checkpoints);

/// <summary>A checkpoint opened: its entry and its notes as a tree, at their paths then. A folder or note checkpoint
/// shows the folders above it and nothing beside it.</summary>
public sealed record CheckpointTreeView(CheckpointEntryView Checkpoint, TreeFolder Root);

/// <summary>A note as the checkpoint held it; <c>links</c> resolved among the checkpoint's notes, never today's.</summary>
public sealed record CheckpointNoteView(string Path, string Content, IReadOnlyList<LinkView> Links);

public sealed record CreateCheckpointRequest(string Name);

/// <summary>
/// A note a restore changes, at its path in the checkpoint. <c>kind</c>: <c>changed</c> (it gets its content
/// then), <c>back</c> (removed since, it comes back), <c>trash</c> (made since, it goes to the trash) or
/// <c>moved</c> (moved since from <c>from</c>, it moves back). <c>added</c> and <c>removed</c>: the lines the
/// restore adds and removes, for a note whose content changes.
/// </summary>
public sealed record RestoreChangeView(string Kind, string Path, string? From, int? Added, int? Removed);

/// <summary>
/// What restoring the checkpoint would do. <c>rewrittenLinks</c>: links in <c>rewrittenIn</c> (outside what it
/// covers) that follow notes moved back; <c>broken</c>: links outside that would lead nowhere afterwards.
/// <c>plan</c> applies exactly this, for an hour; null when there is nothing to restore.
/// </summary>
public sealed record RestorePreviewView(
    string Scope, IReadOnlyList<RestoreChangeView> Changes, int RewrittenLinks, IReadOnlyList<string> RewrittenIn, IReadOnlyList<string> Broken, string? Plan);

public sealed record RestoreCheckpointRequest(string Plan);

public sealed record RestoredView(string Message);

/// <summary>
/// A note in a changeset. <c>kind</c>: <c>added</c>, <c>changed</c>, <c>renamed</c> (from <c>from</c>) or <c>deleted</c>.
/// <c>version</c>: the state the changeset left (null for deleted); <c>before</c>: the state before it, null when there
/// was none or it is no longer kept (<c>beforeLost</c>). <c>trashed</c>: the note is in the trash now. Both versions
/// open with <c>?version=</c> on the note, or in the trash. For <c>renamed</c>: <c>from</c> → <c>to</c> is that rename;
/// <c>path</c> is where the note is now (or was deleted), which differs when it moved on since.
/// </summary>
public sealed record ActivityFileView(string Path, string Kind, string? From, string? Version, string? Before, bool BeforeLost, bool Trashed, string? To);

/// <summary>A changeset: what one writer did close together in time, <c>from</c> to <c>to</c>. <c>id</c> is null for
/// writes that join no changeset (a restore, a seed, older moves and removals; one row per commit). <c>writerKind</c> as
/// on a note; <c>restore</c>: the row restored a version or a checkpoint. <c>key</c> tells rows apart for paging.</summary>
public sealed record ActivityRowView(string? Id, string Writer, string? WriterKind, DateTime From, DateTime To, IReadOnlyList<ActivityFileView> Files, string Key, bool Restore);

/// <summary>The vault's changesets, newest first; <c>more</c>: older ones follow (ask again with <c>before</c> = the last
/// row's <c>to</c> and <c>key</c> = its <c>key</c>).</summary>
public sealed record ActivityView(IReadOnlyList<ActivityRowView> Rows, bool More);

/// <summary>
/// A note's restore, the vault's trash and its checkpoints for the web app: the code paths of <c>restore</c>,
/// <c>trash</c>, <c>mkcp</c>, <c>lscp</c> and <c>rmcp</c>, with their rights. Every caller lists what its
/// credential opens; a restore, making and removing a checkpoint need write rights to the vault, and so does a
/// checkpoint restore's preview, as in the shell. Removing a checkpoint is for good. Opening and reading a checkpoint needs only the right to read
/// the vault.
/// </summary>
public static class HistoryApi
{
    public static void MapHistoryApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter);
        api.MapPost("/restore/{vault}/{**path}", RestoreAsync).Errors(400, 401, 403, 404, 409, 503);
        api.MapGet("/version/{vault}/{**path}", VersionAsync).WithMetadata(BackgroundMetadata.Instance).Errors(400, 401, 404, 503);
        api.MapGet("/vaults/{vault}/trash", TrashAsync).Errors(400, 401, 404, 503);
        api.MapGet("/vaults/{vault}/checkpoints", ListCheckpointsAsync).Errors(400, 401, 404, 503);
        api.MapPost("/vaults/{vault}/checkpoints", CreateCheckpointAsync).Errors(400, 401, 403, 404, 409, 503);
        api.MapGet("/vaults/{vault}/checkpoints/{name}/tree", CheckpointTreeAsync).Errors(400, 401, 404, 503);
        api.MapGet("/vaults/{vault}/checkpoints/{name}/notes/{**path}", CheckpointNoteAsync).Errors(400, 401, 404, 503);
        api.MapPost("/vaults/{vault}/checkpoints/{name}/preview", PreviewAsync).Errors(400, 401, 403, 404, 409, 503);
        api.MapPost("/vaults/{vault}/checkpoints/{name}/restore", RestoreCheckpointAsync).Errors(400, 401, 403, 404, 409, 503);
        api.MapDelete("/vaults/{vault}/checkpoints/{name}", RemoveCheckpointAsync).Errors(400, 401, 403, 404, 503);
        api.MapGet("/vaults/{vault}/activity", ActivityAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 404, 503);
    }

    /// <summary>
    /// <c>restore &lt;path&gt; [version]</c>: an existing note goes back to a version of its history, as a new
    /// version (the one it replaces stays in the history); a removed one comes back from the trash.
    /// </summary>
    private static async Task<Results<Ok<NoteView>, Conflict<ConflictView>>> RestoreAsync(
        string vault, string? path, RestoreRequest body, RequestCaller caller, INoteStore store, NoteWriter writer, WriterKinds kinds, CancellationToken ct)
    {
        var notePath = ApiPaths.Parse(vault, path, note: true);
        await NoteApi.RequireVaultAsync(store, notePath, ct);
        NoteApi.Refuse(caller, "restore", notePath);
        var existing = await store.GetAsync(notePath, ct);
        if (existing is not null && body.Expected is not null && existing.Version != body.Expected) return NoteApi.Conflict(existing, body.Expected);
        var result = await writer.RestoreAsync(notePath, body.Version, ct);
        if (!result.IsError && await store.GetAsync(notePath, ct) is { } now) return TypedResults.Ok(await NoteView.FromAsync(now, kinds, ct));

        // What was asked for exists (in the history, or in the trash with a note at its path now), so it is the
        // state of the vault that stood in the way.
        var inTrash = (await store.ListTrashAsync(notePath, ct)).Any(t => t.Note.Path == notePath && (body.Version is null || t.Note.Version == body.Version));
        var known = existing is not null ? NoteHistory.Versions(existing).Any(v => v.Version == body.Version) || inTrash : inTrash;
        throw new ApiException(
            existing is not null && body.Version is null ? StatusCodes.Status400BadRequest : known ? StatusCodes.Status409Conflict : StatusCodes.Status404NotFound,
            result.Output);
    }

    /// <summary>
    /// What the web app polls while a note is open and the page visible. It doesn't count as the session's use
    /// (<see cref="BackgroundMetadata"/>), but like any read it keeps the vault unlocked.
    /// </summary>
    private static async Task<Ok<NoteVersionView>> VersionAsync(string vault, string? path, INoteStore store, WriterKinds kinds, CancellationToken ct)
    {
        var notePath = ApiPaths.Parse(vault, path, note: true);
        var note = await store.GetAsync(notePath, ct) ?? throw await NoteApi.MissingAsync(store, notePath, ct);
        return TypedResults.Ok(new NoteVersionView(notePath.Value, note.Version, note.LastWriter, note.LastWriteAt, await kinds.OfAsync(note.LastWriter, ct)));
    }

    private static async Task<Ok<TrashView>> TrashAsync(string vault, INoteStore store, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        await NoteApi.RequireVaultAsync(store, root, ct);
        var trashed = await store.ListTrashAsync(root, ct);
        return TypedResults.Ok(new TrashView(
            NoteWriter.TrashRetention.Days,
            [.. trashed.Select(t => new TrashEntryView(
                t.Note.Path.Value, t.Note.Version, t.DeletedAt, t.DeletedBy, t.DeletedAt + NoteWriter.TrashRetention,
                Lines.Split(t.Note.Content).Count, Encoding.UTF8.GetByteCount(t.Note.Content)))]));
    }

    public const int DefaultActivityRows = 50;
    public const int MaxActivityRows = 100;

    /// <summary>
    /// The vault's changesets for the Activity sheet, newest first: <c>limit</c> rows (1 to 100, default 50), only
    /// those after the last row shown: <c>before</c> (its <c>to</c>, an ISO time) and <c>key</c> (its key; without it,
    /// rows that ended before <c>before</c>). Sessions only: it names every writer of the vault.
    /// </summary>
    private static async Task<Ok<ActivityView>> ActivityAsync(
        string vault, string? before, string? key, int? limit, INoteStore store, WriterKinds kinds, CancellationToken ct)
    {
        var max = limit ?? DefaultActivityRows;
        if (max is < 1 or > MaxActivityRows) throw ApiErrors.BadRequest($"limit is 1 to {MaxActivityRows}.");
        DateTime? until = null;
        if (before is not null)
        {
            if (!DateTime.TryParse(before, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            {
                throw ApiErrors.BadRequest("before is a time, e.g. 2026-10-26T09:00:00Z.");
            }

            until = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        var root = ApiPaths.Parse(vault, null, note: false);
        await NoteApi.RequireVaultAsync(store, root, ct);
        var notes = new List<StoredNote>();
        foreach (var path in await store.ListNotesUnderAsync(root, ct))
        {
            if (await store.GetAsync(path, ct) is { } note) notes.Add(note);
        }

        // One more than asked, to know whether there are more.
        var rows = Activity.Rows(notes, await store.ListTrashAsync(root, ct), await store.ListMovesAsync(root, ct), until, max + 1, key);
        var kindOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        var views = new List<ActivityRowView>();
        foreach (var row in rows.Take(max))
        {
            if (!kindOf.TryGetValue(row.Writer, out var kind)) kindOf[row.Writer] = kind = await kinds.OfAsync(row.Writer, ct);
            views.Add(new ActivityRowView(row.Id, row.Writer, kind, row.From, row.To, [.. row.Files.Select(f => new ActivityFileView(
                f.Path, f.Kind.ToString().ToLowerInvariant(), f.From, f.Version, f.Before, f.BeforeLost, f.Trashed, f.To))], row.Key, row.Restore));
        }

        return TypedResults.Ok(new ActivityView(views, rows.Count > max));
    }

    private static async Task<Ok<CheckpointsView>> ListCheckpointsAsync(string vault, INoteStore store, Checkpoints checkpoints, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        await NoteApi.RequireVaultAsync(store, root, ct);
        return TypedResults.Ok(new CheckpointsView(checkpoints.Limit, [.. (await store.ListCheckpointsAsync(root, ct)).Select(View)]));
    }

    /// <summary><c>mkcp /vault name</c>: the whole vault as it is now, under a name until it is removed.</summary>
    private static async Task<Created<CheckpointEntryView>> CreateCheckpointAsync(
        string vault, CreateCheckpointRequest body, RequestCaller caller, INoteStore store, Checkpoints checkpoints, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        await NoteApi.RequireVaultAsync(store, root, ct);
        NoteApi.Refuse(caller, "mkcp", root);
        if (!Checkpoint.IsValidName(body.Name)) throw ApiErrors.BadRequest($"A checkpoint name has {Checkpoint.NameRule} characters.");

        // The name taken, the limit reached, or one made by someone else just now: the vault's state is in the way.
        var result = await checkpoints.MakeAsync(root, body.Name, ct);
        if (result.IsError) throw new ApiException(StatusCodes.Status409Conflict, result.Output);
        var made = await checkpoints.FindAsync(root.Segments[0], body.Name, ct)
            ?? throw new ApiException(StatusCodes.Status409Conflict, $"Checkpoint '{body.Name}' was removed again just now.");
        return TypedResults.Created($"/api/vaults/{Uri.EscapeDataString(root.Segments[0])}/checkpoints", View(made));
    }

    /// <summary>The checkpoint's notes as a tree, as <c>ls</c> shows them; reading needs no write right.</summary>
    private static async Task<Ok<CheckpointTreeView>> CheckpointTreeAsync(
        string vault, string name, INoteStore store, Checkpoints checkpoints, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        var view = await OpenCheckpointAsync(root, name, store, checkpoints, ct);
        return TypedResults.Ok(new CheckpointTreeView(View(view.Checkpoint), NoteApi.TreeOf(root, await view.ReadNotesUnderAsync(root, ct))));
    }

    /// <summary>One note as the checkpoint held it, its links resolved inside the checkpoint (a note moved since is still where it was).</summary>
    private static async Task<Ok<CheckpointNoteView>> CheckpointNoteAsync(
        string vault, string name, string? path, INoteStore store, Checkpoints checkpoints, CancellationToken ct)
    {
        var notePath = ApiPaths.Parse(vault, path, note: true);
        var view = await OpenCheckpointAsync(notePath, name, store, checkpoints, ct);
        var content = await view.ReadAsync(notePath, ct) ?? throw ApiErrors.NotFound($"Checkpoint '{name}' holds no note at {notePath.Value}.");
        return TypedResults.Ok(new CheckpointNoteView(notePath.Value, content, await NoteApi.LinkViewsAsync(view, notePath, content, ct)));
    }

    /// <summary>The checkpoint as a read-only tree: one open per request, nothing kept between them.</summary>
    internal static async Task<CheckpointView> OpenCheckpointAsync(
        VaultPath root, string name, INoteStore store, Checkpoints checkpoints, CancellationToken ct)
    {
        await NoteApi.RequireVaultAsync(store, root, ct);
        // "cat" only names the command in an error text that is discarded: the API words its own 404.
        var (view, _) = await checkpoints.OpenAsync("cat", root.Segments[0], name, ct);
        return view ?? throw ApiErrors.NotFound($"There is no checkpoint '{name}' in {root.Segments[0]}.");
    }

    /// <summary>
    /// The preview of a restore of the whole checkpoint, with the plan that applies it; nothing is written yet.
    /// A preview needs write rights, as <c>restore --checkpoint</c>'s does.
    /// </summary>
    private static async Task<Ok<RestorePreviewView>> PreviewAsync(
        string vault, string name, RequestCaller caller, INoteStore store, Checkpoints checkpoints, CancellationToken ct)
    {
        var root = await CheckpointVaultAsync(vault, name, "restore", caller, store, checkpoints, ct);
        RestorePreview? preview;
        ShellResult? error;
        try
        {
            (preview, error) = await checkpoints.PreviewWholeAsync(root.Segments[0], name, ct);
        }
        catch (VaultUnavailableException e) when (e is not VaultBusyException)
        {
            throw new ApiException(StatusCodes.Status409Conflict, e.Message);
        }

        if (preview is null) throw new ApiException(StatusCodes.Status409Conflict, error!.Output);
        return TypedResults.Ok(new RestorePreviewView(
            preview.Checkpoint.Scope,
            [.. preview.Changes.Select(c => new RestoreChangeView(
                c.Kind.ToString().ToLowerInvariant(), c.Path.Value, c.From?.Value, c.Lines?.Added, c.Lines?.Removed))],
            preview.RewrittenLinks,
            [.. preview.RewrittenIn.Select(p => p.Value)],
            preview.Broken,
            preview.Plan));
    }

    /// <summary>Applies a preview's plan: the notes it covers match the checkpoint, all at once, or nothing changes.</summary>
    private static async Task<Ok<RestoredView>> RestoreCheckpointAsync(
        string vault, string name, RestoreCheckpointRequest body, RequestCaller caller, INoteStore store, Checkpoints checkpoints, CancellationToken ct)
    {
        var root = await CheckpointVaultAsync(vault, name, "restore", caller, store, checkpoints, ct);
        ShellResult result;
        try
        {
            result = await checkpoints.ApplyWholeAsync(root.Segments[0], name, body.Plan, ct);
        }
        catch (VaultUnavailableException e) when (e is not VaultBusyException)
        {
            throw new ApiException(StatusCodes.Status409Conflict, e.Message);
        }

        // An unknown, expired or used plan, or a note that changed since the preview.
        return result.IsError ? throw new ApiException(StatusCodes.Status409Conflict, result.Output) : TypedResults.Ok(new RestoredView(result.Output));
    }

    /// <summary><c>rmcp</c>: the checkpoint and the content only it held go for good; the notes stay as they are.</summary>
    private static async Task<NoContent> RemoveCheckpointAsync(
        string vault, string name, RequestCaller caller, INoteStore store, Checkpoints checkpoints, CancellationToken ct)
    {
        var root = await CheckpointVaultAsync(vault, name, "rmcp", caller, store, checkpoints, ct);
        var result = await checkpoints.RemoveAsync(root, name, ct);
        return result.IsError ? throw ApiErrors.NotFound(result.Output) : TypedResults.NoContent();
    }

    /// <summary>The vault's root, once the vault, the caller's right to write it and the checkpoint are there.</summary>
    private static async Task<VaultPath> CheckpointVaultAsync(
        string vault, string name, string command, RequestCaller caller, INoteStore store, Checkpoints checkpoints, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        await NoteApi.RequireVaultAsync(store, root, ct);
        NoteApi.Refuse(caller, command, root);
        if (await checkpoints.FindAsync(root.Segments[0], name, ct) is null) throw ApiErrors.NotFound($"There is no checkpoint '{name}' in {root.Segments[0]}.");
        return root;
    }

    private static CheckpointEntryView View(Checkpoint c) => new(c.Name, c.Scope, c.CreatedBy, c.CreatedAt, c.Notes.Count);
}
