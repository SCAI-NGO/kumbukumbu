// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers;
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Attachments;
using Kumbu.Server.Auth;
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
    internal static FileStateView Of(StoredAttachment a) =>
        new(a.Path.Value, a.Current.MediaType, a.Current.Size, a.Current.Width, a.Current.Height, a.Version, a.LastWriteAt, a.LastWriter);
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
    /// <summary>The request body: the file's 15 MB and room for a little more, so the limit is the file's to say.</summary>
    public const long MaxBody = AttachmentFormat.MaxBytes + 64 * 1024;

    /// <summary>The refusal of a file over the limit.</summary>
    public const string TooLarge = "A file can have at most 15 MB.";

    private const string CutOff = "The upload ended before all of the file arrived; nothing was stored. Try again.";

    private static readonly string[] MediaTypes = [.. AttachmentFormat.All.Select(f => f.MediaType).Distinct()];

    public static void MapFileApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter);
        api.MapGet("/files/{vault}/{**path}", ReadAsync).ProducesFile().Errors(400, 401, 404, 503);
        api.MapPut("/files/{vault}/{**path}", WriteAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBody))
            // Described, not declared with Accepts: routing would then refuse a body whose Content-Type isn't listed,
            // and the Content-Type is the client's to get wrong.
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Description = "The file's bytes, at most 15 MB; the Content-Type is ignored.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/octet-stream"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } },
                    },
                };
                return Task.CompletedTask;
            })
            .Errors(400, 401, 403, 404, 413, 503);
        api.MapDelete("/files/{vault}/{**path}", DeleteAsync).Errors(400, 401, 403, 404, 409, 503);
    }

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
    /// else <c>inline</c>. The <c>ETag</c> is the version token of what is served.
    /// </summary>
    private static async Task<FileStreamHttpResult> ReadAsync(
        string vault, string? path, string? version, string? download, HttpResponse response, RequestCaller caller, INoteStore store, CancellationToken ct)
    {
        var filePath = ApiPaths.Parse(vault, path, PathKind.Attachment);
        var files = Files(store);
        var attachment = await files.GetAttachmentAsync(filePath, ct);
        if (version is null) return await ServeAsync(response, files, filePath, (attachment ?? throw await NoteApi.MissingAsync(store, filePath, ct)).Current, download, ct);

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

        return await ServeAsync(response, files, filePath, found, download, ct);
    }

    /// <summary>One state's bytes from <paramref name="files"/> (the vault, or a checkpoint's view of it).</summary>
    internal static async Task<FileStreamHttpResult> ServeAsync(
        HttpResponse response, IAttachmentStore files, VaultPath path, AttachmentState state, string? download, CancellationToken ct)
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
        if (bytes is null) throw new ContentMissingException($"The content of {path.Value} doesn't open: it was changed or removed outside Kumbu².");
        response.Headers.ContentDisposition = Disposition(path.Name, download is "1" or "true");
        return TypedResults.Stream(bytes, state.MediaType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{state.Version}\""));
    }

    /// <summary>
    /// Uploads the body as the file at the path: created without <c>version</c>, overwritten with the version the
    /// caller last read (409 with the file as it is now otherwise). The body is the file's bytes as they are; its
    /// <c>Content-Type</c> is ignored. Nothing is stored before the whole body has arrived and passed the checks.
    /// </summary>
    private static async Task<Results<Created<FileStateView>, Ok<FileStateView>, Conflict<FileConflictView>>> WriteAsync(
        string vault, string? path, string? version, HttpRequest request, RequestCaller caller, INoteStore store, NoteWriter notes, CancellationToken ct)
    {
        var filePath = ApiPaths.Parse(vault, path, PathKind.Attachment);
        await NoteApi.RequireVaultAsync(store, filePath, ct);
        // Before the body is read: a key that may only read buffers and stores nothing.
        NoteApi.Refuse(caller, "upload", filePath);
        var files = Files(store);
        var writer = notes.ForAttachments() ?? throw ApiErrors.NotFound("This server keeps no attachments.");
        var declared = request.ContentLength;
        if (declared > AttachmentFormat.MaxBytes) throw new ApiException(StatusCodes.Status413PayloadTooLarge, TooLarge);

        // One more byte than may come, to tell "exactly the limit" from "more".
        var capacity = (int)Math.Min(declared ?? AttachmentFormat.MaxBytes, AttachmentFormat.MaxBytes) + 1;
        var buffer = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            var length = await ReadBodyAsync(request, buffer, capacity, declared, ct);
            var bytes = buffer.AsMemory(0, length);
            if (AttachmentFormat.Problem(filePath, bytes.Span) is { } problem) throw ApiErrors.BadRequest(problem + ".");

            // The dimensions the writer stores are the ones read here from the bytes it stores, never a client's.
            (int, int)? dimensions = null;
            if (AttachmentFormat.ForPath(filePath)!.Kind == AttachmentKind.Image)
            {
                using var header = new MemoryStream(buffer, 0, length, writable: false);
                dimensions = ImageHeader.Read(header, out var headerProblem) ?? throw ApiErrors.BadRequest(headerProblem ?? "The image's header doesn't read.");
            }

            // Asked first, so that a conflict answers with the file as it is; the writer checks again when it writes.
            var existing = await files.GetAttachmentAsync(filePath, ct);
            if (existing is not null && existing.Version != version) return Conflict(existing, version);
            if (existing is null && version is not null) throw await NoteApi.MissingAsync(store, filePath, ct);

            var (result, written) = await writer.WriteAsync(filePath.Value, bytes, dimensions, version, ct);
            if (result.IsError)
            {
                return result.Kind switch
                {
                    Kumbu.Core.Sh.ShellErrorKind.Conflict when written is not null => Conflict(written, version),
                    Kumbu.Core.Sh.ShellErrorKind.NotFound => throw await NoteApi.MissingAsync(store, filePath, ct),
                    Kumbu.Core.Sh.ShellErrorKind.Refused => throw new ApiException(StatusCodes.Status403Forbidden, result.Output),
                    Kumbu.Core.Sh.ShellErrorKind.Conflict => throw new ApiException(StatusCodes.Status409Conflict, result.Output),
                    _ => throw ApiErrors.BadRequest(result.Output),
                };
            }

            var view = FileStateView.Of(written!);
            return existing is null ? TypedResults.Created(ApiPaths.Url("files", filePath), view) : TypedResults.Ok(view);
        }
        finally
        {
            // The pool hands the array to the next renter: the file's bytes don't go with it, also when reading
            // stopped part-way (too large, cut off), so all that may have been written is cleared.
            buffer.AsSpan(0, capacity).Clear();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The whole body into <paramref name="buffer"/>; 413 past the limit, 400 for a body that ends before
    /// its <c>Content-Length</c> or is cut off.</summary>
    private static async Task<int> ReadBodyAsync(HttpRequest request, byte[] buffer, int capacity, long? declared, CancellationToken ct)
    {
        var length = 0;
        try
        {
            while (length < capacity)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(length, capacity - length), ct);
                if (read == 0) break;
                length += read;
            }
        }
        catch (IOException)
        {
            // The client went away mid-body (Kestrel's own "unexpected end" is a BadHttpRequestException, answered alike).
            throw ApiErrors.BadRequest(CutOff);
        }

        if (length == capacity)
        {
            throw declared is null || declared > AttachmentFormat.MaxBytes - 1
                ? new ApiException(StatusCodes.Status413PayloadTooLarge, TooLarge)
                : ApiErrors.BadRequest("The body is longer than its Content-Length.");
        }

        if (declared is { } expected && length != expected) throw ApiErrors.BadRequest(CutOff);
        return length;
    }

    /// <summary><c>version</c>: trash the file only if it is still at that version, else 409 with the file as it is now.
    /// Without it, whatever version it is at.</summary>
    private static async Task<Results<NoContent, Conflict<FileConflictView>>> DeleteAsync(
        string vault, string? path, string? version, RequestCaller caller, INoteStore store, NoteWriter notes, CancellationToken ct)
    {
        var filePath = ApiPaths.Parse(vault, path, PathKind.Attachment);
        await NoteApi.RequireVaultAsync(store, filePath, ct);
        NoteApi.Refuse(caller, "rm", filePath);
        var files = Files(store);
        var writer = notes.ForAttachments() ?? throw ApiErrors.NotFound("This server keeps no attachments.");
        var existing = await files.GetAttachmentAsync(filePath, ct) ?? throw await NoteApi.MissingAsync(store, filePath, ct);
        if (version is not null && existing.Version != version) return Conflict(existing, version);

        var result = await writer.RemoveAsync(filePath.Value, ct, version);
        if (!result.IsError) return TypedResults.NoContent();
        var now = await files.GetAttachmentAsync(filePath, ct) ?? throw await NoteApi.MissingAsync(store, filePath, ct);
        if (version is not null && now.Version != version) return Conflict(now, version);
        throw new ApiException(StatusCodes.Status409Conflict, result.Output);
    }

    internal static Conflict<FileConflictView> Conflict(StoredAttachment current, string? version) => TypedResults.Conflict(new FileConflictView(
        version is null
            ? $"{current.Path.Value} exists already; to overwrite it, send its version."
            : $"{current.Path.Value} has changed since version {version} (it is now {current.Version}).",
        FileStateView.Of(current)));

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
