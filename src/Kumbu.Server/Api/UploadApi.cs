// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Kumbu.Server.Api;

/// <summary><c>path</c> relative to the folder uploaded into, '/' between folders.</summary>
public sealed record UploadFile(string Path, string Content);

/// <summary><c>onConflict</c> for a file whose note exists: <c>ask</c> (default: list the conflicts and write
/// nothing), <c>replace</c> (a new version; the history keeps the old one) or <c>skip</c>.</summary>
public sealed record UploadRequest(IReadOnlyList<UploadFile> Files, string OnConflict = "ask");

public sealed record SkippedView(string Path, string Reason);

/// <summary>A file whose note exists, at <c>version</c>.</summary>
public sealed record UploadConflictView(string Path, string Version);

/// <summary>Paths as the request gave them. <c>written</c>: notes created; <c>replaced</c>: notes overwritten.</summary>
public sealed record UploadResultView(int Written, int Replaced, IReadOnlyList<SkippedView> Skipped, IReadOnlyList<UploadConflictView> Conflicts);

/// <summary>
/// The web app's upload (accounts design §4, "Upload"): Markdown files with their relative paths into a folder of
/// a vault, as one changeset of the caller and one transaction. What isn't a note is skipped and listed, per file,
/// so a dropped Obsidian vault comes in without its settings and trash.
/// </summary>
public static class UploadApi
{
    public const int MaxFiles = 200;
    public const int MaxBytes = 5_000_000;

    /// <summary>The body may be larger than the text it carries: JSON escapes (a client may write ü as ü).</summary>
    private const long MaxBody = 16 * 1024 * 1024;

    /// <summary>The refusal of a request over the limits; says them, so a client knows how to split.</summary>
    public const string Limits = "An upload takes at most 200 files and 5 MB of text per request; send larger uploads in batches.";

    public static void MapUploadApi(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter)
            .MapPost("/upload/{vault}/{**folder}", UploadAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBody))
            .Errors(400, 401, 403, 404, 409, 413, 503);
    }

    private static async Task<Ok<UploadResultView>> UploadAsync(
        string vault, string? folder, UploadRequest body, RequestCaller caller, INoteStore store, NoteWriter writer, CancellationToken ct)
    {
        if (body.OnConflict is not ("ask" or "replace" or "skip")) throw ApiErrors.BadRequest("onConflict is 'ask', 'replace' or 'skip'.");
        if (body.Files.Count == 0) throw ApiErrors.BadRequest("There are no files to upload.");
        // The JSON reader checks the nullability of properties, not of list elements.
        if (body.Files.Any(f => f is null)) throw ApiErrors.BadRequest("Every entry of 'files' is a file: {\"path\": ..., \"content\": ...}.");
        if (body.Files.Count > MaxFiles || body.Files.Sum(f => (long)Encoding.UTF8.GetByteCount(f.Content)) > MaxBytes)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, Limits);
        }

        var dir = ApiPaths.Parse(vault, folder, note: false);
        await NoteApi.RequireVaultAsync(store, dir, ct);
        NoteApi.Refuse(caller, "upload", dir);
        if (await store.ReadAsync(dir, ct) is not null) throw ApiErrors.BadRequest($"{dir.Value} is a note, not a folder.");

        var skipped = new List<SkippedView>();
        var creates = new Dictionary<VaultPath, (UploadFile File, string Content)>();
        var existing = new Dictionary<VaultPath, (UploadFile File, string Content, StoredNote Note)>();
        foreach (var file in body.Files)
        {
            if (Target(dir, file.Path, out var reason) is not { } target)
            {
                skipped.Add(new SkippedView(file.Path, reason!));
                continue;
            }

            // Each note on its own as well: a folder may be open to the caller while a file in it (Board.md) is not.
            NoteApi.Refuse(caller, "upload", target);
            var content = file.Content.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (await ProblemAsync(store, target, content, creates.Keys.Concat(existing.Keys), ct) is { } problem)
            {
                skipped.Add(new SkippedView(file.Path, problem));
                continue;
            }

            if (await store.GetAsync(target, ct) is { } note) existing[target] = (file, content, note);
            else creates[target] = (file, content);
        }

        if (body.OnConflict == "ask" && existing.Count > 0)
        {
            return TypedResults.Ok(new UploadResultView(0, 0, skipped, [.. existing.Values.Select(e => new UploadConflictView(e.File.Path, e.Note.Version))]));
        }

        var now = writer.Now;
        var changeset = await writer.ChangesetAsync(dir, ct);
        var batch = new NoteBatch();
        foreach (var (path, (_, content)) in creates) batch.Creates.Add(new StoredNote(path, content, 1, caller.Writer, now, [], Changeset: changeset, Ref: caller.Reference));
        foreach (var (_, (file, content, note)) in existing)
        {
            if (body.OnConflict == "skip") skipped.Add(new SkippedView(file.Path, "a note exists at this path"));
            else if (note.Content == content) skipped.Add(new SkippedView(file.Path, "the note has this content already"));
            // The replaced state goes into the history even if it is of the same changeset (written a moment ago
            // in the web app): replacing on upload must stay undoable.
            else batch.Replaces.Add((note, NoteWriter.Next(note, content, caller.Writer, caller.Reference, now, changeset, keepCurrent: true)));
        }

        if (await store.TryCommitAsync(batch, caller.Writer, caller.Reference, now, ct) is { Count: > 0 } changed)
        {
            throw new ApiException(StatusCodes.Status409Conflict,
                $"Nothing was uploaded: {string.Join(", ", changed.Select(c => c.Value))} changed meanwhile; try again.");
        }

        return TypedResults.Ok(new UploadResultView(batch.Creates.Count, batch.Replaces.Count, skipped, []));
    }

    /// <summary>The note a file becomes, or null and why not.</summary>
    private static VaultPath? Target(VaultPath dir, string relative, out string? reason)
    {
        reason = null;
        var segments = relative.Split('/');
        if (relative.Contains('\\')) reason = "folders are separated by '/', not '\\'";
        else if (relative.StartsWith('/')) reason = "an absolute path; paths are relative to the folder uploaded into";
        else if (segments.Any(s => s is "" or "." or "..")) reason = "empty, '.' and '..' segments aren't allowed";
        else if (segments[..^1].Any(s => s.Equals(".obsidian", StringComparison.OrdinalIgnoreCase) || s.Equals(".trash", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Obsidian's settings or trash, not a note";
        }
        else if (NoteName(segments[^1]) is not { } name) reason = "not a Markdown file (.md or .markdown)";
        else
        {
            var target = segments[..^1].Append(name).Aggregate(dir, (p, s) => p.Child(s));
            reason = NoteNames.Problem(target);
            return reason is null ? target : null;
        }

        return null;
    }

    /// <summary>Kumbu² notes end in ".md"; ".markdown" and any case of either become it.</summary>
    private static string? NoteName(string name)
    {
        foreach (var extension in new[] { ".md", ".markdown" })
        {
            if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return name[..^extension.Length] + ".md";
        }

        return null;
    }

    /// <summary>Why <paramref name="target"/> can't be written, as <c>write</c> would refuse it; null if it can.
    /// <paramref name="planned"/>: the notes the upload already writes.</summary>
    private static async Task<string?> ProblemAsync(INoteStore store, VaultPath target, string content, IEnumerable<VaultPath> planned, CancellationToken ct)
    {
        var others = planned.ToHashSet();
        if (others.Contains(target)) return "another file of this upload has the same path";
        if (Encoding.UTF8.GetByteCount(content) > NoteWriter.MaxBytes) return $"larger than {NoteWriter.MaxBytes} bytes (1 MiB), the limit for a note";
        if (await store.IsDamagedAsync(target, ct)) return "the stored note at this path doesn't open (damaged outside Kumbu²)";
        if ((await store.ListNotesUnderAsync(target, ct)).Count > 0 || others.Any(p => p.Value.StartsWith(target.Value + "/", StringComparison.Ordinal)))
        {
            return "a folder of that name exists";
        }

        for (var parent = VaultPath.Root.Child(target.Segments[0]); parent.Segments.Count < target.Segments.Count - 1;)
        {
            parent = parent.Child(target.Segments[parent.Segments.Count]);
            if (others.Contains(parent) || await store.ReadAsync(parent, ct) is not null) return $"{parent.Value} is a note, not a folder";
        }

        return null;
    }
}
