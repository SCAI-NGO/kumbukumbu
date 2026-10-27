// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kumbu.Server.Api;

public sealed record ExportTicketRequest(string Password);

/// <summary><c>ticket</c> goes into the export's URL as <c>?ticket=</c>, once, before <c>expiresAt</c>.</summary>
public sealed record ExportTicketView(string Ticket, DateTime ExpiresAt);

/// <summary>
/// The export (demo-round design §2): vaults of the session as a ZIP, one top-level folder per vault named as the
/// vault, its notes verbatim at their paths below it, each entry dated with the note's last write. Only the current
/// notes: no history, trash, checkpoints or settings. Unpacked, each folder opens in Obsidian as a vault; it is also
/// what the import reads. A checkpoint exports the same way, its notes as it holds them (a folder checkpoint
/// below the vault's folder), each entry dated with the time the checkpoint was made. Sessions only
/// (<see cref="SessionOnlyMetadata"/>): a key or a connection never exports. An export is a heavyweight operation: it
/// takes the notes out of the encryption at rest in one go, so it needs the password again, as deleting a vault does,
/// through a ticket (<see cref="ExportTickets"/>), and each one is logged with ids and counts, never a name.
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
    }

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
        var notes = (await view.ReadNotesUnderAsync(root, ct))
            .OrderBy(n => n.Path.Value, StringComparer.Ordinal)
            .Select(n => new ExportNote(n.Path, n.Content, made))
            .ToList();
        var zone = Zone(tz);
        export.Log.LogInformation(
            "User {UserId} exported a checkpoint of vault {VaultId}: {Notes} notes",
            export.Caller.User!.Id, await export.VaultIdsAsync([root.Segments[0]]), notes.Count);
        return Zip(http, CheckpointZipName(root.Segments[0], name, made, zone), [(root.Segments[0], notes)], zone, ct);
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

        var content = new List<(string Vault, IReadOnlyList<ExportNote> Notes)>();
        foreach (var root in roots)
        {
            await NoteApi.RequireVaultAsync(store, root, ct);
            var notes = new List<ExportNote>();
            foreach (var path in (await store.ListNotesUnderAsync(root, ct)).OrderBy(p => p.Value, StringComparer.Ordinal))
            {
                // Only notes that open are listed (a damaged one isn't, and isn't exported); null only if the note
                // went between the listing and here.
                if (await store.GetAsync(path, ct) is { } note) notes.Add(new ExportNote(note.Path, note.Content, note.LastWriteAt));
            }

            content.Add((root.Segments[0], notes));
        }

        export.Log.LogInformation(
            "User {UserId} exported {Count} vaults ({VaultIds}): {Notes} notes",
            export.Caller.User!.Id, content.Count, await export.VaultIdsAsync(content.Select(c => c.Vault)), content.Sum(c => c.Notes.Count));
        return Zip(http, ZipName([.. content.Select(c => c.Vault)], export.Time.GetUtcNow().UtcDateTime), content, Zone(tz), ct);
    }

    /// <summary>A note as it goes into the ZIP: what a vault's current note and a checkpoint's note have in common.</summary>
    private sealed record ExportNote(VaultPath Path, string Content, DateTime WrittenAt);

    private static IResult Zip(HttpContext http, string name, IReadOnlyList<(string Vault, IReadOnlyList<ExportNote> Notes)> content, TimeZoneInfo zone, CancellationToken ct)
    {
        var disposition = new ContentDispositionHeaderValue("attachment") { FileName = $"\"{AsciiName(name)}\"", FileNameStar = name };
        http.Response.Headers.ContentDisposition = disposition.ToString();
        return TypedResults.Stream(body => WriteAsync(body, content, zone, ct), "application/zip");
    }

    /// <summary>The time zone of that IANA (or Windows) id, or UTC.</summary>
    internal static TimeZoneInfo Zone(string? tz) =>
        !string.IsNullOrWhiteSpace(tz) && tz.Length <= 64 && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var zone) ? zone : TimeZoneInfo.Utc;

    /// <summary>Small notes are sent together, not one tiny write each; memory stays at one note plus this.</summary>
    private const int DrainAt = 64 * 1024;

    /// <summary>Straight to the response: entries are written as they come (data descriptors, no seeking), so an
    /// export never sits in memory as a whole. <see cref="ZipArchive"/> still writes some headers synchronously,
    /// which Kestrel refuses; they go into <see cref="Chunks"/> and reach the response asynchronously in pieces of
    /// about <see cref="DrainAt"/>.</summary>
    private static async Task WriteAsync(Stream body, IReadOnlyList<(string Vault, IReadOnlyList<ExportNote> Notes)> content, TimeZoneInfo zone, CancellationToken ct)
    {
        var chunks = new Chunks();
        // A null entry-name encoding: names are UTF-8 with the language flag set, so Bücher/Ü².md unpacks as such.
        var zip = await ZipArchive.CreateAsync(chunks, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);
        try
        {
            foreach (var (vault, notes) in content)
            {
                zip.CreateEntry(vault + "/");
                foreach (var note in notes)
                {
                    var entry = zip.CreateEntry(vault + "/" + string.Join('/', note.Path.Segments.Skip(1)), CompressionLevel.Optimal);
                    // A ZIP entry's time has no zone: unpackers read it as their local time. So the reader's clock at
                    // the moment of the write, each note with its own offset (summer or winter time).
                    entry.LastWriteTime = TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(note.WrittenAt, DateTimeKind.Utc)), zone);
                    await using (var stream = await entry.OpenAsync(ct)) await stream.WriteAsync(Encoding.UTF8.GetBytes(note.Content), ct);
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

    /// <summary>A write-only, non-seekable buffer the ZIP writes into, emptied into the response by
    /// <see cref="DrainAsync"/>; it holds at most one note's entry beyond <see cref="DrainAt"/>.</summary>
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
    /// (ü → u), other non-ASCII characters and quotes left out.</summary>
    internal static string AsciiName(string name)
    {
        var ascii = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (c is >= ' ' and <= '~' and not '"' and not '\\') ascii.Append(c);
        }

        var text = string.Join(' ', ascii.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.StartsWith('.') || text.Length == 0 ? "export" + text : text;
    }
}
