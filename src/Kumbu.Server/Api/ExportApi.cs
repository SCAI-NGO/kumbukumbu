// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kumbu.Server.Api;

public sealed record ExportTicketRequest(string Password);

/// <summary><c>ticket</c> goes into the export's URL as <c>?ticket=</c>, once, before <c>expiresAt</c>.</summary>
public sealed record ExportTicketView(string Ticket, DateTime ExpiresAt);

/// <summary>
/// The export (demo-round design §2): vaults of the session as a ZIP, one top-level folder per vault named as the
/// vault, its notes verbatim and its attachments byte for byte at their paths below it, each entry dated with its last
/// write. Only the current notes and files: no history, trash, checkpoints or settings. Unpacked, each folder opens in
/// Obsidian as a vault; it is also what the import reads. A checkpoint exports the same way, its notes and files as it
/// holds them (a folder checkpoint below the vault's folder), each entry dated with the time the checkpoint was made.
/// A download of several attachments (<c>GET /api/files/{vault}/zip</c>) is treated as an export too, unless the user
/// turned its password off. Sessions only (<see cref="SessionOnlyMetadata"/>): a key or a connection never exports. An
/// export is a heavyweight operation: it takes the notes out of the encryption at rest in one go, so it needs the
/// password again, as deleting a vault does, through a ticket (<see cref="ExportTickets"/>), and each one is logged with
/// ids and counts, never a name.
/// </summary>
public static class ExportApi
{
    public static void MapExportApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        api.MapPost("/export/ticket", TicketAsync).Errors(400, 401, 403, 429);
        api.MapGet("/vaults/{vault}/export", (string vault, string? tz, string? ticket, [AsParameters] Export export) => ExportAsync([vault], tz, ticket, export))
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Errors(400, 401, 403, 404, 503);
        api.MapGet("/export", ([FromQuery(Name = "vault")] string[]? vault, string? tz, string? ticket, [AsParameters] Export export) => ExportAsync(vault ?? [], tz, ticket, export))
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Errors(400, 401, 403, 404, 503);
        api.MapGet("/vaults/{vault}/checkpoints/{name}/export", CheckpointExportAsync)
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Errors(400, 401, 403, 404, 503);
        // A literal segment outranks FileApi's catch-all, and no file is named "zip" (a file has an extension);
        // a folder "zip" still reaches FileApi, as its paths have a segment more.
        api.MapGet("/files/{vault}/zip", FilesZipAsync)
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Errors(400, 401, 403, 404, 503);
        // The same as a form post (the web app's): a hundred paths would make the GET's request line too long. The
        // Origin check (Security.UseCallerRules) is its CSRF guard, as for every state-changing session request.
        // Posted into a hidden frame of the gallery, which reads an error answer there: the frame must be allowed.
        api.MapPost("/files/{vault}/zip", FilesZipFormAsync)
            .WithMetadata(SameOriginFramesMetadata.Instance)
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Errors(400, 401, 403, 404, 503);
    }

    /// <summary>The most files one download of several takes.</summary>
    public const int MaxFiles = 100;

    /// <summary>What every export needs, bound from the request.</summary>
    internal sealed record Export(
        INoteStore Store, TimeProvider Time, RequestCaller Caller, ExportTickets Tickets, ILoggerFactory Logs, HttpContext Http, CancellationToken Ct)
    {
        public ILogger Log => Logs.CreateLogger(typeof(ExportApi).FullName!);

        /// <summary>The ticket, used up; or 403, before anything is read.</summary>
        public void RequireTicket(string? ticket)
        {
            if (!Tickets.Take(Caller.User!.Id, ticket))
            {
                throw new ApiException(StatusCodes.Status403Forbidden, "An export needs your password: get a ticket from POST /api/export/ticket and add it as ?ticket=.");
            }
        }

        /// <summary>The ids of these vaults (by the name the caller sees), for the log.</summary>
        public async Task<string> VaultIdsAsync(IEnumerable<string> names)
        {
            var open = await Caller.OpenVaultsAsync(Ct);
            return string.Join(", ", names.Select(n => open.FirstOrDefault(v => v.Name == n)?.Id ?? "?"));
        }
    }

    /// <summary>The password, checked as a password change checks it (and limited alike), for a ticket.</summary>
    private static async Task<Ok<ExportTicketView>> TicketAsync(
        ExportTicketRequest body, RequestCaller caller, UserCatalog users, AccountLimits limits, ExportTickets tickets, CancellationToken ct)
    {
        var user = caller.User!;
        if (limits.PasswordCheck(user.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        if (!await users.CheckPasswordAsync(user.Id, caller.UserKey!, body.Password ?? "", ct)) throw ApiErrors.BadRequest("The password is wrong.");
        var (ticket, expiresAt) = tickets.Issue(user.Id);
        return TypedResults.Ok(new ExportTicketView(ticket, expiresAt));
    }

    /// <summary>As the vault export: the vault, the checkpoint and its notes are read before the first byte.</summary>
    private static async Task<IResult> CheckpointExportAsync(
        string vault, string name, string? tz, string? ticket, Checkpoints checkpoints, [AsParameters] Export export)
    {
        var (store, http, ct) = (export.Store, export.Http, export.Ct);
        export.RequireTicket(ticket);
        var root = ApiPaths.Parse(vault, null, note: false);
        var view = await HistoryApi.OpenCheckpointAsync(root, name, store, checkpoints, ct);
        var made = view.Checkpoint.CreatedAt;
        var entries = (await view.ReadNotesUnderAsync(root, ct))
            .Select(n => ExportEntry.OfNote(n.Path, n.Content, made))
            .Concat((await view.ListAttachmentsUnderAsync(root, ct)).Select(a => ExportEntry.OfFile(a.Path, view, a.Current.BlobId, made)))
            .OrderBy(e => e.Path.Value, StringComparer.Ordinal)
            .ToList();
        var zone = Zone(tz);
        export.Log.LogInformation(
            "User {UserId} exported a checkpoint of vault {VaultId}: {Notes} notes, {Files} files",
            export.Caller.User!.Id, await export.VaultIdsAsync([root.Segments[0]]), entries.Count(e => e.IsNote), entries.Count(e => !e.IsNote));
        return Zip(http, CheckpointZipName(root.Segments[0], name, made, zone), [(root.Segments[0] + "/", entries)], zone, export.Log, ct);
    }

    /// <summary>Every vault is checked and its notes taken before the first byte, so a refusal (unknown vault,
    /// one being re-keyed) is still a status and an error, never a broken ZIP. <paramref name="tz"/>: the reader's
    /// IANA time zone (the web app sends the browser's), for the entry times; UTC if it is missing or unknown.</summary>
    private static async Task<IResult> ExportAsync(string[] vaults, string? tz, string? ticket, Export export)
    {
        var (store, http, ct) = (export.Store, export.Http, export.Ct);
        export.RequireTicket(ticket);
        if (vaults.Length == 0) throw ApiErrors.BadRequest("Name at least one vault to export: ?vault=<name>, once per vault.");
        // Compared as parsed (NFC), so "Bücher" in two spellings is still one vault.
        var roots = vaults.Select(v => ApiPaths.Parse(v, null, note: false)).ToList();
        if (roots.Select(r => r.Segments[0]).Distinct(StringComparer.Ordinal).Count() != roots.Count) throw ApiErrors.BadRequest("Name each vault once.");

        var content = new List<(string Folder, IReadOnlyList<ExportEntry> Entries)>();
        foreach (var root in roots)
        {
            await NoteApi.RequireVaultAsync(store, root, ct);
            var entries = new List<ExportEntry>();
            foreach (var path in await store.ListNotesUnderAsync(root, ct))
            {
                // Only notes that open are listed (a damaged one isn't, and isn't exported); null only if the note
                // went between the listing and here.
                if (await store.GetAsync(path, ct) is { } note) entries.Add(ExportEntry.OfNote(note.Path, note.Content, note.LastWriteAt));
            }

            // Only the records here; each file's bytes are read as its entry is written, one at a time. FileApi.Files
            // fails loudly: a store that keeps no attachments must not export a vault without its files.
            var files = FileApi.Files(store);
            entries.AddRange((await files.ListAttachmentsUnderAsync(root, ct)).Select(a => ExportEntry.OfFile(a.Path, files, a.Current.BlobId, a.LastWriteAt)));

            content.Add((root.Segments[0] + "/", [.. entries.OrderBy(e => e.Path.Value, StringComparer.Ordinal)]));
        }

        export.Log.LogInformation(
            "User {UserId} exported {Count} vaults ({VaultIds}): {Notes} notes, {Files} files",
            export.Caller.User!.Id, content.Count, await export.VaultIdsAsync(roots.Select(r => r.Segments[0])),
            content.Sum(c => c.Entries.Count(e => e.IsNote)), content.Sum(c => c.Entries.Count(e => !e.IsNote)));
        return Zip(http, ZipName([.. roots.Select(r => r.Segments[0])], export.Time.GetUtcNow().UtcDateTime), content, Zone(tz), export.Log, ct);
    }

    /// <summary>
    /// Several attachments of one vault as a ZIP, at their paths in the vault (no vault folder on top), each entry dated
    /// with its last write. <c>path</c>: one per file, from the root of the vault, at most <see cref="MaxFiles"/>. Treated
    /// like an export (attachments design, "Server API"): it needs a ticket, unless the user turned "Ask for my password
    /// when downloading several files" off, and it is logged either way. Every file is checked before the first byte.
    /// </summary>
    private static Task<IResult> FilesZipAsync(
        string vault, [FromQuery(Name = "path")] string[]? path, string? tz, string? ticket, UserCatalog users, [AsParameters] Export export) =>
        ZipFilesAsync(vault, path ?? [], tz, ticket, users, export);

    /// <summary><c>POST /api/files/{vault}/zip</c>: the same, its fields in an <c>application/x-www-form-urlencoded</c>
    /// body (<c>path</c> once per file, <c>tz</c>, <c>ticket</c>). Read by hand: the form binder would bring antiforgery
    /// tokens, which the Origin check makes unneeded.</summary>
    private static async Task<IResult> FilesZipFormAsync(string vault, UserCatalog users, [AsParameters] Export export)
    {
        if (!export.Http.Request.HasFormContentType) throw ApiErrors.BadRequest("Send the files as a form: path=<path in the vault>, once per file.");
        var form = await export.Http.Request.ReadFormAsync(export.Ct);
        return await ZipFilesAsync(vault, [.. form["path"].OfType<string>()], form["tz"].FirstOrDefault(), form["ticket"].FirstOrDefault(), users, export);
    }

    private static async Task<IResult> ZipFilesAsync(string vault, string[] asked, string? tz, string? ticket, UserCatalog users, Export export)
    {
        var (store, http, ct) = (export.Store, export.Http, export.Ct);
        if (asked.Length == 0) throw ApiErrors.BadRequest("Name at least one file: ?path=<path in the vault>, once per file.");
        if (asked.Length > MaxFiles) throw ApiErrors.BadRequest($"A download takes at most {MaxFiles} files at once; this asked for {asked.Length}.");
        // Parsed (NFC, no '.' or '..') before anything is read; one file named twice is one entry.
        var paths = asked.Select(p => ApiPaths.Parse(vault, p.TrimStart('/'), PathKind.Attachment)).Distinct().ToList();

        var withPassword = await users.AsksPasswordForDownloadsAsync(export.Caller.User!.Id, ct);
        if (withPassword) export.RequireTicket(ticket);

        var root = ApiPaths.Parse(vault, null, note: false);
        await NoteApi.RequireVaultAsync(store, root, ct);
        var files = FileApi.Files(store);
        var entries = new List<ExportEntry>();
        long bytes = 0;
        foreach (var filePath in paths.OrderBy(p => p.Value, StringComparer.Ordinal))
        {
            var file = await files.GetAttachmentAsync(filePath, ct) ?? throw await NoteApi.MissingAsync(store, filePath, ct);
            entries.Add(ExportEntry.OfFile(file.Path, files, file.Current.BlobId, file.LastWriteAt));
            bytes += file.Current.Size;
        }

        // Logged once the last byte is out: a download cut off by a file that doesn't open is logged as such instead.
        var (userId, vaultId) = (export.Caller.User!.Id, await export.VaultIdsAsync([root.Segments[0]]));
        var name = $"{root.Segments[0]} files {export.Time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.zip";
        return Zip(http, name, [("", entries)], Zone(tz), export.Log, ct, completed: () => export.Log.LogInformation(
            "User {UserId} downloaded {Files} files ({Bytes} bytes) of vault {VaultId} as a ZIP, {Password}",
            userId, entries.Count, bytes, vaultId, withPassword ? "with the password" : "without the password (turned off)"));
    }

    /// <summary>
    /// A note or a file as it goes into the ZIP: what a vault's current note or attachment and a checkpoint's have in
    /// common. A note brings its text; a file only where its bytes are (<paramref name="Files"/>, <paramref name="BlobId"/>),
    /// opened when its entry is written.
    /// </summary>
    private sealed record ExportEntry(VaultPath Path, DateTime WrittenAt, string? Content, IAttachmentStore? Files, string? BlobId)
    {
        public bool IsNote => Content is not null;

        public static ExportEntry OfNote(VaultPath path, string content, DateTime writtenAt) => new(path, writtenAt, content, null, null);

        public static ExportEntry OfFile(VaultPath path, IAttachmentStore files, string blobId, DateTime writtenAt) => new(path, writtenAt, null, files, blobId);
    }

    /// <summary><paramref name="content"/>: the entries below each folder (the vault's name and a '/', or "" for none).</summary>
    /// <paramref name="completed"/>: run once the whole ZIP is out.
    private static IResult Zip(
        HttpContext http, string name, IReadOnlyList<(string Folder, IReadOnlyList<ExportEntry> Entries)> content, TimeZoneInfo zone, ILogger log, CancellationToken ct,
        Action? completed = null)
    {
        var disposition = new ContentDispositionHeaderValue("attachment") { FileName = $"\"{AsciiName(name)}\"", FileNameStar = name };
        http.Response.Headers.ContentDisposition = disposition.ToString();
        return TypedResults.Stream(
            async body =>
            {
                await WriteAsync(body, content, zone, log, ct);
                completed?.Invoke();
            },
            "application/zip");
    }

    /// <summary>The time zone of that IANA (or Windows) id, or UTC.</summary>
    internal static TimeZoneInfo Zone(string? tz) =>
        !string.IsNullOrWhiteSpace(tz) && tz.Length <= 64 && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var zone) ? zone : TimeZoneInfo.Utc;

    /// <summary>Small notes are sent together, not one tiny write each; memory stays at one note or file plus this.</summary>
    private const int DrainAt = 64 * 1024;

    /// <summary>Straight to the response: entries are written as they come (data descriptors, no seeking), so an
    /// export never sits in memory as a whole. <see cref="ZipArchive"/> still writes some headers synchronously,
    /// which Kestrel refuses; they go into <see cref="Chunks"/> and reach the response asynchronously in pieces of
    /// about <see cref="DrainAt"/>.</summary>
    private static async Task WriteAsync(
        Stream body, IReadOnlyList<(string Folder, IReadOnlyList<ExportEntry> Entries)> content, TimeZoneInfo zone, ILogger log, CancellationToken ct)
    {
        var chunks = new Chunks();
        var piece = new byte[DrainAt];
        // A null entry-name encoding: names are UTF-8 with the language flag set, so Bücher/Ü².md unpacks as such.
        var zip = await ZipArchive.CreateAsync(chunks, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);
        try
        {
            foreach (var (folder, entries) in content)
            {
                if (folder.Length > 0) zip.CreateEntry(folder);
                foreach (var item in entries)
                {
                    // Opened before its entry exists; a file whose bytes don't open ends the download (OpenAsync).
                    using var bytes = item.IsNote ? null : await OpenAsync(item, log, ct);

                    // Vault paths: checked segments, never '.', '..' or empty, so no entry unpacks outside its folder.
                    // Formats that are compressed already (images, most PDFs) are only stored faster.
                    var entry = zip.CreateEntry(folder + string.Join('/', item.Path.Segments.Skip(1)), item.IsNote ? CompressionLevel.Optimal : CompressionLevel.Fastest);
                    // A ZIP entry's time has no zone: unpackers read it as their local time. So the reader's clock at
                    // the moment of the write, each entry with its own offset (summer or winter time).
                    entry.LastWriteTime = TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(item.WrittenAt, DateTimeKind.Utc)), zone);
                    await using (var stream = await entry.OpenAsync(ct))
                    {
                        if (bytes is null)
                        {
                            await stream.WriteAsync(Encoding.UTF8.GetBytes(item.Content!), ct);
                        }
                        else
                        {
                            // In pieces, drained as they fill: a 15 MB file never sits in the buffer as a whole.
                            int read;
                            while ((read = await bytes.ReadAsync(piece, ct)) > 0)
                            {
                                await stream.WriteAsync(piece.AsMemory(0, read), ct);
                                if (chunks.Buffered >= DrainAt) await chunks.DrainAsync(body, ct);
                            }
                        }
                    }

                    if (chunks.Buffered >= DrainAt) await chunks.DrainAsync(body, ct);
                }
            }
        }
        finally
        {
            await zip.DisposeAsync();
        }

        await chunks.DrainAsync(body, ct);
    }

    /// <summary>
    /// A file's bytes. If they don't open (changed or damaged outside Kumbu², or gone), the download fails instead of
    /// arriving without the file: an error with the blob id (never a name) and an exception, which, once the response has
    /// begun, makes Kestrel reset it, so the browser shows a failed download, never a ZIP that looks complete. (A damaged
    /// note is never listed, so never exported; a file the export did list is one the user expects in it.)
    /// </summary>
    private static async Task<Stream> OpenAsync(ExportEntry file, ILogger log, CancellationToken ct)
    {
        try
        {
            if (await file.Files!.OpenBlobAsync(file.Path.Segments[0], file.BlobId!, ct) is { } bytes) return bytes;
        }
        catch (VaultUnavailableException e) when (e is not VaultBusyException)
        {
        }

        log.LogError("A ZIP download was cut off: the content of a file doesn't open (blob {BlobId})", file.BlobId);
        throw new InvalidOperationException("A file's content doesn't open; the ZIP download is cut off.");
    }

    /// <summary>A write-only, non-seekable buffer the ZIP writes into, emptied into the response by
    /// <see cref="DrainAsync"/>; it holds at most one note's entry, or one piece of a file's, beyond <see cref="DrainAt"/>.</summary>
    private sealed class Chunks : Stream
    {
        private readonly MemoryStream _buffer = new();

        public long Buffered => _buffer.Length;

        public async Task DrainAsync(Stream body, CancellationToken ct)
        {
            if (_buffer.Length == 0) return;
            await body.WriteAsync(_buffer.GetBuffer().AsMemory(0, (int)_buffer.Length), ct);
            await body.FlushAsync(ct);
            _buffer.SetLength(0);
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _buffer.Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _buffer.Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _buffer.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary><c>Work 2026-10-26.zip</c> for one vault, <c>Kumbu² export 2026-10-26.zip</c> for several; the
    /// server's UTC date.</summary>
    internal static string ZipName(IReadOnlyList<string> vaults, DateTime utcNow)
    {
        var date = utcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return vaults.Count == 1 ? $"{vaults[0]} {date}.zip" : $"Kumbu² export {date}.zip";
    }

    /// <summary><c>Work checkpoint before-cleanup 2026-10-19.zip</c>: the day the checkpoint was made, on the reader's
    /// clock, so it matches the date the web app shows.</summary>
    internal static string CheckpointZipName(string vault, string name, DateTime createdAtUtc, TimeZoneInfo zone)
    {
        var made = TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc)), zone);
        return $"{vault} checkpoint {name} {made.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.zip";
    }

    /// <summary>The plain <c>filename</c> beside <c>filename*</c>, for clients that read only it: accents dropped
    /// (ü → u), other non-ASCII characters and quotes left out; <paramref name="fallback"/> in front of a name left with
    /// nothing before its extension.</summary>
    internal static string AsciiName(string name, string fallback = "export")
    {
        var ascii = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (c is >= ' ' and <= '~' and not '"' and not '\\') ascii.Append(c);
        }

        var text = string.Join(' ', ascii.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.StartsWith('.') || text.Length == 0 ? fallback + text : text;
    }
}
