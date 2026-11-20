// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers;
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Attachments;
using Kumbu.Server.Auth;
using Kumbu.Server.Metrics;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace Kumbu.Server.Api;

/// <summary>An attachment as it is: <c>path</c> from the root of the vault tree, <c>mediaType</c> from its checked
/// format, <c>size</c> in bytes, <c>width</c> and <c>height</c> as displayed (images only), <c>version</c> what a write
/// or delete sends back, and who wrote it when.</summary>
public sealed record FileStateView(string Path, string MediaType, long Size, int? Width, int? Height, string Version, DateTime WrittenAt, string WrittenBy)
{
    internal static async Task<FileStateView> OfAsync(StoredAttachment a, IWriterLabels labels, CancellationToken ct) => new(
        a.Path.Value, a.Current.MediaType, a.Current.Size, a.Current.Width, a.Current.Height, a.Version, a.LastWriteAt,
        (await labels.RenderAsync(a.Path.Segments[0], a.LastWriter, a.Ref, ct)).Display);
}

/// <summary>A write or delete that lost against another one: the attachment as it is now, without its bytes.</summary>
public sealed record FileConflictView(string Error, FileStateView Current);

/// <summary>
/// Attachments' bytes (attachments design, "Server API"): read, upload and delete one file by vault and path, with
/// the rights of the notes API. The bytes are untrusted: the format is decided by the name and checked against the
/// content, images by their header only, and what is served is typed from that check, never from what a client sent.
/// </summary>
public static class FileApi
{
    /// <summary>The request body, until the caller may upload: the 15 MB of a file read whole and room for a little more.
    /// <see cref="StoreAsync"/> then lifts it to the server's bound (<see cref="LiftBodyLimit"/>).</summary>
    public const long MaxBody = AttachmentFormat.MaxBytes + 64 * 1024;

    /// <summary>How much of a file larger than 15 MB is read before it is streamed to the store: enough for a known
    /// format's magic bytes and an image's header, which comes before its pixels.</summary>
    internal const int HeadBytes = 1024 * 1024;

    internal const string CutOff = "The upload ended before all of the file arrived; nothing was stored. Try again.";

    /// <summary>The media types a browser may be given inline: the known formats', and text.</summary>
    private static readonly string[] InlineTypes = [.. AttachmentFormat.All.Select(f => f.MediaType).Append(AttachmentFormat.TextMediaType).Distinct()];

    private static readonly string[] MediaTypes = [.. InlineTypes, AttachmentFormat.OctetStream];

    public static void MapFileApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter);
        api.MapGet("/files/{vault}/{**path}", ReadAsync).ProducesFile().Errors(400, 401, 404, 503);
        api.MapPut("/files/{vault}/{**path}", WriteAsync).TakesFileBody().Errors(400, 401, 403, 404, 413, 503, 507);
        api.MapDelete("/files/{vault}/{**path}", DeleteAsync).Errors(400, 401, 403, 404, 409, 503);
    }

    /// <summary>The body of a route that stores it with <see cref="StoreAsync"/>: the size limit, and the file's bytes in
    /// the OpenAPI document. Described, not declared with Accepts: routing would then refuse a body whose Content-Type
    /// isn't listed, and the Content-Type is the client's to get wrong.</summary>
    internal static RouteHandlerBuilder TakesFileBody(this RouteHandlerBuilder builder) =>
        builder
            .WithMetadata(new RequestSizeLimitAttribute(MaxBody))
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Description = "The file's bytes, up to the server's bound per file (Kumbu:Vaults:MaxFileBytes, 1 GiB by default); the Content-Type is ignored.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/octet-stream"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } },
                    },
                };
                return Task.CompletedTask;
            });

    /// <summary>Declares a 200 with an attachment's bytes, in any of the formats Kumbu² keeps, and a 500 for bytes that
    /// are gone or don't open (<see cref="ContentMissingException"/>; never a 404, which a client reads as deleted).
    /// The responses carry <see cref="Security.FileContentPolicy"/>.</summary>
    internal static RouteHandlerBuilder ProducesFile(this RouteHandlerBuilder builder) =>
        builder.Produces(StatusCodes.Status200OK, typeof(byte[]), MediaTypes[0], MediaTypes[1..])
            .Errors(StatusCodes.Status500InternalServerError)
            .WithMetadata(FileContentMetadata.Instance);

    /// <summary>
    /// The file's bytes, typed by its format. <c>version</c>: a token from its history (or, for a session, of a file at
    /// this path in the trash) instead of the current state. <c>download=1</c>: <c>Content-Disposition: attachment</c>,
    /// else <c>inline</c>; a file of kind <c>file</c> is always an attachment (<see cref="OnlyAsDownload"/>). The
    /// <c>ETag</c> is the version token of what is served.
    /// </summary>
    private static async Task<FileStreamHttpResult> ReadAsync(
        string vault, string? path, string? version, string? download, HttpResponse response, RequestCaller caller, INoteStore store, CancellationToken ct)
    {
        var filePath = ApiPaths.Parse(vault, path, PathKind.Attachment);
        var files = Files(store);
        var attachment = await files.GetAttachmentAsync(filePath, ct);
        var skillsVault = await store.SkillsVaultAsync(ct);
        if (version is null) return await ServeAsync(response, files, filePath, (attachment ?? throw await NoteApi.MissingAsync(store, filePath, ct)).Current, download, skillsVault, ct);

        // As a note's ?version=: its history, then (sessions only: the trash is the owner's) the trash.
        var found = attachment is null ? null : NoteHistory.Versions(attachment).FirstOrDefault(v => v.Version == version)?.State;
        if (found is null && caller.Kind == CallerKind.Session)
        {
            found = (await files.ListAttachmentTrashAsync(filePath, ct))
                .Where(t => t.Attachment.Path == filePath)
                .SelectMany(t => NoteHistory.Versions(t.Attachment))
                .FirstOrDefault(v => v.Version == version)?.State;
        }

        if (found is null)
        {
            throw attachment is null ? await NoteApi.MissingAsync(store, filePath, ct) : ApiErrors.NotFound($"{filePath.Value} has no version {version} (its history lists them).");
        }

        return await ServeAsync(response, files, filePath, found, download, skillsVault, ct);
    }

    /// <summary>One state's bytes from <paramref name="files"/> (the vault, or a checkpoint's view of it); <paramref name="skillsVault"/>
    /// as <see cref="OnlyAsDownload"/> takes it.</summary>
    internal static async Task<FileStreamHttpResult> ServeAsync(
        HttpResponse response, IAttachmentStore files, VaultPath path, AttachmentState state, string? download, string? skillsVault, CancellationToken ct)
    {
        var bytes = await OpenAsync(files, path, state, ct);
        response.Headers.ContentDisposition = Disposition(path.Name, download is "1" or "true" || OnlyAsDownload(path, state.MediaType, skillsVault));
        // A large file's stream doesn't know its length; the record does. No ranges, as for every file here.
        response.ContentLength = state.Size;
        return TypedResults.Stream(bytes, state.MediaType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{state.Version}\""));
    }

    /// <summary>
    /// Whether a state's bytes go out only as <c>attachment</c>, whatever the request asks (files spec, decision 4): a
    /// file of kind <c>file</c>, and anything stored with a media type no known format has. An uploaded <c>.html</c>
    /// opened inline would be stored XSS; <c>nosniff</c> and <see cref="Security.FileContentPolicy"/> come on top.
    /// <paramref name="skillsVault"/> is the caller's name for the vault marked as the skills vault, as the writers pass
    /// it (<see cref="AttachmentFormat.ForPath"/>): text in a skill's folder is a file anywhere else.
    /// </summary>
    internal static bool OnlyAsDownload(VaultPath path, string mediaType, string? skillsVault) =>
        AttachmentFormat.ForPath(path, skillsVault)?.Kind == AttachmentKind.File || !InlineTypes.Contains(mediaType);

    /// <summary>One state's bytes; <see cref="ContentMissingException"/> if they are gone or don't open.</summary>
    internal static async Task<Stream> OpenAsync(IAttachmentStore files, VaultPath path, AttachmentState state, CancellationToken ct)
    {
        Stream? bytes;
        try
        {
            bytes = await files.OpenBlobAsync(path.Segments[0], state.BlobId, ct);
        }
        catch (VaultUnavailableException e) when (e is not VaultBusyException)
        {
            bytes = null;
        }

        // The file exists, its bytes don't: a fault of the server, not a 404, which a client takes for a deletion.
        return bytes ?? throw new ContentMissingException($"The content of {path.Value} doesn't open: it was changed or removed outside Kumbu².");
    }

    /// <summary>Uploads the body as the file at the path (<see cref="StoreAsync"/>), with the version the caller last read to overwrite it.</summary>
    private static async Task<Results<Created<FileStateView>, Ok<FileStateView>, Conflict<FileConflictView>>> WriteAsync(
        string vault, string? path, string? version, HttpRequest request, RequestCaller caller, INoteStore store, NoteWriter notes, WriterLabels labels,
        CancellationToken ct)
    {
        var filePath = ApiPaths.Parse(vault, path, PathKind.Attachment);
        return await StoreAsync(filePath, version, request, caller, store, notes, labels, ct);
    }

    /// <summary>
    /// Stores the request's body as the file at the path: created without <c>version</c>, overwritten with the version
    /// the caller last read (409 with the file as it is now otherwise). The body is the file's bytes as they are; its
    /// <c>Content-Type</c> is ignored. Nothing is recorded before the whole body has arrived and passed the checks. The one
    /// write path of both routes that upload a file, <c>PUT /api/files</c> (<see cref="WriteAsync"/>) and an agent's
    /// <c>PUT /api/agent-upload</c> (<see cref="AgentUploadApi"/>), so that both check, limit and record it alike.
    /// </summary>
    internal static async Task<Results<Created<FileStateView>, Ok<FileStateView>, Conflict<FileConflictView>>> StoreAsync(
        VaultPath filePath, string? version, HttpRequest request, RequestCaller caller, INoteStore store, NoteWriter notes, WriterLabels labels,
        CancellationToken ct)
    {
        await NoteApi.RequireVaultAsync(store, filePath, ct);
        // Before the body is read: a key that may only read buffers and stores nothing.
        NoteApi.Refuse(caller, "upload", filePath);
        if (FolderMarker.Is(filePath) && filePath.Segments.Count == 2) throw ApiErrors.BadRequest($"{FolderMarker.InVaultRoot}.");
        var files = Files(store);
        var writer = notes.ForAttachments() ?? throw ApiErrors.NotFound("This server keeps no attachments.");
        var bound = request.HttpContext.RequestServices.GetRequiredService<VaultCatalog>().MaxFileBytes;
        var declared = request.ContentLength;
        if (declared > bound) throw new ApiException(StatusCodes.Status413PayloadTooLarge, new FileTooLargeException(bound).Message);
        LiftBodyLimit(request.HttpContext, bound);
        // Kestrel takes the rate at the body's first read.
        if (declared > AttachmentFormat.MaxBytes) RequireLargeBodyRate(request.HttpContext);

        using var body = new ReceivedBody();
        if (!await body.ReadAsync(request, declared, ct)) return await StoreLargeAsync(filePath, version, body.Bytes, request, declared, bound, files, writer, store, labels, ct);
        var bytes = body.Bytes;
        if (AttachmentFormat.Problem(filePath, bytes.Span) is { } problem) throw ApiErrors.BadRequest(problem + ".");

        // The dimensions the writer stores are the ones read here from the bytes it stores, never a client's; an SVG's
        // the writer reads itself, and no pixel count limits it, as it is rendered at the size asked for.
        var dimensions = Dimensions(filePath, bytes);
        return await RecordAsync(filePath, version, files, store, labels, v => writer.WriteAsync(filePath.Value, bytes, dimensions, v, ct), ct);
    }

    /// <summary>
    /// A file larger than 15 MB (files spec, decision 11): checked from its first bytes (<paramref name="head"/>: a known
    /// format's magic bytes; an image's size from its header where it reads, without the pixel limit, as nothing decodes
    /// it; an SVG must be read whole, so it can't be this large), asked for a conflict, then the rest of the body
    /// streamed to the store, and its record written. Nothing is held but a chunk or two at a time. A body that doesn't
    /// arrive whole, or runs past the bound, stores nothing; one that sends nothing is ended after 30 s without a byte
    /// (<see cref="VaultCatalog.LargeBodySilence"/>).
    /// </summary>
    private static async Task<Results<Created<FileStateView>, Ok<FileStateView>, Conflict<FileConflictView>>> StoreLargeAsync(
        VaultPath filePath, string? version, ReadOnlyMemory<byte> head, HttpRequest request, long? declared, long bound, IAttachmentStore files, AttachmentWriter writer,
        INoteStore store, WriterLabels labels, CancellationToken ct)
    {
        if (AttachmentFormat.TooLargeToCheck(filePath) is { } tooLarge) throw new ApiException(StatusCodes.Status413PayloadTooLarge, tooLarge + ".");
        if (AttachmentFormat.LargeProblem(filePath, head.Span) is { } problem) throw ApiErrors.BadRequest(problem + ".");
        var dimensions = AttachmentFormat.ForPath(filePath) is { Kind: AttachmentKind.Image } ? ImageHeader.Size(head.Span) : null;
        if (await ConflictBeforeAsync(filePath, version, files, store, labels, ct) is { } conflict) return conflict;
        var services = request.HttpContext.RequestServices;
        var body = new QuietBody(request.Body, services.GetRequiredService<VaultCatalog>().LargeBodySilence, services.GetRequiredService<TimeProvider>());

        LargeBlob blob;
        try
        {
            blob = await writer.PutLargeAsync(filePath.Segments[0], head, body, declared, ct);
        }
        catch (FileTooLargeException e)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, e.Message);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel's own limit, which LiftBodyLimit set just above the bound.
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, new FileTooLargeException(bound).Message);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            // The client went away or stopped sending mid-body, or it held another length than it declared.
            throw ApiErrors.BadRequest(e is InvalidDataException ? e.Message : CutOff);
        }

        return await RecordAsync(filePath, version, files, store, labels, v => writer.WriteLargeAsync(filePath.Value, blob, head, dimensions, v, ct), ct);
    }

    /// <summary>
    /// Lifts the request's body limit from <see cref="MaxBody"/>, which held it until the caller was known to upload, to
    /// the server's bound and a little more, so that the bound is the store's to say (<see cref="FileTooLargeException"/>).
    /// </summary>
    internal static void LiftBodyLimit(HttpContext http, long bound)
    {
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = bound + 64 * 1024;
        }
    }

    /// <summary>The least a large body must keep arriving at, after <see cref="LargeBodyGrace"/>: 8 KiB a second. Kestrel's
    /// default, 240 bytes, would let a client that all but stopped hold a write slot for days; and the sweep takes chunks
    /// no head names once the newest is older than its grace of 10 minutes, which 4 MiB at 8 KiB a second (about 8.5
    /// minutes a chunk) stays within.</summary>
    internal const int LargeBodyBytesPerSecond = 8 * 1024;

    internal static readonly TimeSpan LargeBodyGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Holds a body declared larger than 15 MB to <see cref="LargeBodyBytesPerSecond"/>, before the body's first byte is
    /// read: Kestrel takes a request's rate once, at that read. Only where the server lets a request set it, over
    /// HTTP/1.1: HTTP/2 keeps its rates per connection, and its feature refuses one per request; the test host has none.
    /// </summary>
    internal static void RequireLargeBodyRate(HttpContext http)
    {
        if (http.Features.Get<Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature>() is not { } rate) return;
        try
        {
            rate.MinDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(LargeBodyBytesPerSecond, LargeBodyGrace);
        }
        catch (NotSupportedException)
        {
        }
    }

    /// <summary>An image's displayed width and height from its header (400 if it doesn't read); null for anything else,
    /// and for an SVG, whose the writer reads itself.</summary>
    private static (int, int)? Dimensions(VaultPath filePath, ReadOnlyMemory<byte> bytes) =>
        AttachmentFormat.ForPath(filePath) is { Kind: AttachmentKind.Image } format && format != AttachmentFormat.Svg
            ? ImageHeader.Read(bytes.Span, out var headerProblem) ?? throw ApiErrors.BadRequest(headerProblem ?? "The image's header doesn't read.")
            : null;

    /// <summary>Asked first, so that a conflict answers with the file as it is; the writer checks again when it writes.</summary>
    private static async Task<Conflict<FileConflictView>?> ConflictBeforeAsync(
        VaultPath filePath, string? version, IAttachmentStore files, INoteStore store, WriterLabels labels, CancellationToken ct)
    {
        var existing = await files.GetAttachmentAsync(filePath, ct);
        if (existing is not null && existing.Version != version) return await ConflictAsync(existing, version, labels, ct);
        if (existing is null && version is not null) throw await NoteApi.MissingAsync(store, filePath, ct);
        return null;
    }

    /// <summary>The writer's <paramref name="write"/> of the checked file, and its answer: 201 for a new file, 200 for
    /// one overwritten, 409 with the file as it is now.</summary>
    private static async Task<Results<Created<FileStateView>, Ok<FileStateView>, Conflict<FileConflictView>>> RecordAsync(
        VaultPath filePath, string? version, IAttachmentStore files, INoteStore store, WriterLabels labels, Func<string?, Task<WriteResult>> write, CancellationToken ct)
    {
        if (await ConflictBeforeAsync(filePath, version, files, store, labels, ct) is { } conflict) return conflict;
        var created = version is null;

        var (result, written) = await write(version);
        if (result.IsError)
        {
            return result.Kind switch
            {
                Kumbu.Core.Sh.ShellErrorKind.Conflict when written is not null => await ConflictAsync(written, version, labels, ct),
                Kumbu.Core.Sh.ShellErrorKind.NotFound => throw await NoteApi.MissingAsync(store, filePath, ct),
                Kumbu.Core.Sh.ShellErrorKind.Refused => throw NoteApi.Refused(result),
                Kumbu.Core.Sh.ShellErrorKind.Conflict => throw ConflictWithoutState(result.Output),
                _ => throw ApiErrors.BadRequest(result.Output),
            };
        }

        var view = await FileStateView.OfAsync(written!, labels, ct);
        return created ? TypedResults.Created(ApiPaths.Url("files", filePath), view) : TypedResults.Ok(view);
    }

    /// <summary>
    /// A request's body in arrays from the pool that grow with the bytes received, never with the length declared, so
    /// a body that arrives slowly holds what it has sent. The pool hands an array to the next renter: the file's bytes
    /// don't go with it, also when reading stopped part-way (too large, cut off), so each array goes back cleared.
    /// </summary>
    private sealed class ReceivedBody : IDisposable
    {
        private const int InitialSize = 128 * 1024;

        private byte[] _buffer = [];
        private int _length;

        public ReadOnlyMemory<byte> Bytes => _buffer.AsMemory(0, _length);

        /// <summary>
        /// The whole body, true, if it is at most 15 MB; otherwise the start of a larger one, false: with a declared length
        /// above 15 MB, its first <see cref="HeadBytes"/>, and without one, the 15 MB and a byte that told it apart. 400
        /// for a body that ends before its <c>Content-Length</c> or is cut off.
        /// </summary>
        public async Task<bool> ReadAsync(HttpRequest request, long? declared, CancellationToken ct)
        {
            var large = declared > AttachmentFormat.MaxBytes;
            // One more byte than a whole body may have, to tell "exactly 15 MB" from "more".
            var capacity = large ? HeadBytes : (int)Math.Min(declared ?? AttachmentFormat.MaxBytes, AttachmentFormat.MaxBytes) + 1;
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(capacity, InitialSize));
            try
            {
                while (_length < capacity)
                {
                    if (_length == _buffer.Length) Grow(Math.Min(capacity, _buffer.Length * 2));
                    var read = await request.Body.ReadAsync(_buffer.AsMemory(_length, Math.Min(_buffer.Length, capacity) - _length), ct);
                    if (read == 0) break;
                    _length += read;
                }
            }
            catch (IOException)
            {
                // The client went away mid-body (Kestrel's own "unexpected end" is a BadHttpRequestException, answered alike).
                throw ApiErrors.BadRequest(CutOff);
            }

            if (large)
            {
                if (_length < capacity) throw ApiErrors.BadRequest(CutOff);
                return false;
            }

            if (_length == capacity)
            {
                if (declared is null) return false;
                throw ApiErrors.BadRequest("The body is longer than its Content-Length.");
            }

            if (declared is { } expected && _length != expected) throw ApiErrors.BadRequest(CutOff);
            return true;
        }

        private void Grow(int size)
        {
            var next = ArrayPool<byte>.Shared.Rent(size);
            _buffer.AsSpan(0, _length).CopyTo(next);
            Release();
            _buffer = next;
        }

        private void Release()
        {
            _buffer.AsSpan().Clear();
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }

        public void Dispose()
        {
            if (_buffer.Length > 0) Release();
        }
    }

    /// <summary><c>version</c>: trash the file only if it is still at that version, else 409 with the file as it is now.
    /// Without it, whatever version it is at.</summary>
    private static async Task<Results<NoContent, Conflict<FileConflictView>>> DeleteAsync(
        string vault, string? path, string? version, RequestCaller caller, INoteStore store, NoteWriter notes, WriterLabels labels, CancellationToken ct)
    {
        var filePath = ApiPaths.Parse(vault, path, PathKind.Attachment);
        await NoteApi.RequireVaultAsync(store, filePath, ct);
        NoteApi.Refuse(caller, "rm", filePath);
        var files = Files(store);
        var writer = notes.ForAttachments() ?? throw ApiErrors.NotFound("This server keeps no attachments.");
        var existing = await files.GetAttachmentAsync(filePath, ct) ?? throw await NoteApi.MissingAsync(store, filePath, ct);
        if (version is not null && existing.Version != version) return await ConflictAsync(existing, version, labels, ct);

        var result = await writer.RemoveAsync(filePath.Value, ct, version);
        if (!result.IsError) return TypedResults.NoContent();
        var now = await files.GetAttachmentAsync(filePath, ct) ?? throw await NoteApi.MissingAsync(store, filePath, ct);
        if (version is not null && now.Version != version) return await ConflictAsync(now, version, labels, ct);
        throw ConflictWithoutState(result.Output);
    }

    private static ApiException ConflictWithoutState(string message)
    {
        KumbuMetrics.WriteConflict(KumbuMetrics.WriteVia.Api);
        return new ApiException(StatusCodes.Status409Conflict, message);
    }

    internal static async Task<Conflict<FileConflictView>> ConflictAsync(StoredAttachment current, string? version, IWriterLabels labels, CancellationToken ct)
    {
        KumbuMetrics.WriteConflict(KumbuMetrics.WriteVia.Api);
        return TypedResults.Conflict(new FileConflictView(
            version is null
                ? $"{current.Path.Value} exists already; to overwrite it, send its version."
                : $"{current.Path.Value} has changed since version {version} (it is now {current.Version}).",
            await FileStateView.OfAsync(current, labels, ct)));
    }

    /// <summary>The attachments of the caller's store; every store of this server keeps them.</summary>
    internal static IAttachmentStore Files(INoteStore store) =>
        store as IAttachmentStore ?? throw ApiErrors.NotFound("This server keeps no attachments.");

    /// <summary>
    /// <c>inline</c> or <c>attachment</c>, with the name twice (RFC 6266): <c>filename*</c> the name exactly, UTF-8 and
    /// percent-encoded (RFC 5987), and a plain <c>filename</c> for older clients: ASCII only, accents dropped, and no
    /// quote, backslash or '%' (which some browsers decode there), so nothing in a name can end or bend the header.
    /// </summary>
    internal static string Disposition(string name, bool download)
    {
        var encoded = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            if (char.IsAsciiLetterOrDigit((char)b) || "!#$&+-.^_`|~".Contains((char)b)) encoded.Append((char)b);
            else encoded.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return $"{(download ? "attachment" : "inline")}; filename=\"{ExportApi.AsciiName(name.Replace("%", "", StringComparison.Ordinal), "file")}\"; filename*=UTF-8''{encoded}";
    }
}
