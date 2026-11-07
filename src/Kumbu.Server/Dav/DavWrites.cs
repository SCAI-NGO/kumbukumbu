// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Server.Attachments;
using Microsoft.Net.Http.Headers;

namespace Kumbu.Server.Dav;

/// <summary>
/// The methods that write under <c>/dav</c> (WebDAV design §3.5–§3.8): <c>PUT</c>, <c>MKCOL</c>, <c>DELETE</c>,
/// <c>COPY</c>, <c>MOVE</c>, and <c>LOCK</c> and <c>UNLOCK</c>. Each first does what the protocol asks of it wherever it
/// writes (the share's right, the root and the vaults' own folders refused, a missing parent, <c>Destination</c>,
/// <c>Depth</c>, <c>Overwrite</c> and the statuses they lead to), then writes either to the parking area, for a path
/// that is parked (<see cref="DavRules.IsParked"/>, or below a parked folder) and that the store has nothing at, or to
/// the store. So litmus, which works in a dot-folder, tests the same protocol layer the store's files go through
/// (spec §7). <c>PUT</c> and <c>MKCOL</c> write to the store through the writers every other path uses
/// (<see cref="NoteWriter"/>, <see cref="AttachmentWriter"/>), with their checks and the share's caller, which gives
/// the writer label and a person's changeset (§3.2); the store's side of the other methods comes in later steps and
/// answers 501 until then. Locks are never enforced: <c>If</c> headers are read only to refresh a lock, and ignored
/// everywhere else (§3.7).
/// </summary>
public sealed partial class DavWrites(HttpContext http, DavCaller caller, DavTree tree, DavParking parking, DavLocks locks, NoteWriter writer)
{
    /// <summary>A note's bytes are UTF-8 or refused (WebDAV design §3.5): an invalid byte throws rather than becoming
    /// U+FFFD. Decoding with <see cref="Encoding.GetString(byte[])"/> keeps a BOM as the text's first character.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private HttpRequest Request => http.Request;

    private HttpResponse Response => http.Response;

    /// <summary>
    /// <c>PUT</c> (RFC 4918 §9.7): 201 for a new file, 204 for one replaced, with the <c>ETag</c> of what was stored.
    /// What it is goes by its name's extension, in any case (brief open point 6): a note, an attachment of a format
    /// Kumbu² keeps, a parked file, or a 415. A parked placeholder (an empty attachment) is not parked for its own sake:
    /// the write that follows it goes to the store.
    /// </summary>
    public async Task PutAsync(DavTarget target, CancellationToken ct)
    {
        var place = Writable(target.Path);
        if (await tree.FindAsync(target.Path, ct) is { IsCollection: true }) throw new DavException(StatusCodes.Status405MethodNotAllowed, "A folder has no content to write.");
        await ParentAsync(target.Path, ct);
        if (!await ParkedAsync(target.Path, place, ct, itself: false))
        {
            var name = target.Path.Name;
            if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) await PutNoteAsync(target.Path, ct);
            else if (AttachmentFormat.ForName(name) is { } format && format != AttachmentFormat.Marker) await PutAttachmentAsync(target.Path, place, format, ct);
            else throw new DavException(StatusCodes.Status415UnsupportedMediaType, AttachmentFormat.UnsupportedMessage);
            return;
        }

        var bytes = await DavBody.ReadLimitedAsync(Request, DavRules.ParkingCap, ct)
            ?? throw new DavException(StatusCodes.Status413PayloadTooLarge, $"A parked file can have at most {DavRules.ParkingCap / 1024 / 1024} MiB.");
        var (item, created) = parking.Put(caller.ShareId, place.VaultId, place.Path, bytes);
        Response.Headers.ETag = $"\"{item.Version}\"";
        Response.StatusCode = created ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }

    /// <summary><c>MKCOL</c> (RFC 4918 §9.3): 201; 405 where something is, 409 without the folder above, 415 with a body.</summary>
    public async Task MkcolAsync(DavTarget target, CancellationToken ct)
    {
        var place = Writable(target.Path);
        if (DavBody.Has(Request)) throw new DavException(StatusCodes.Status415UnsupportedMediaType, "MKCOL takes no body.");
        if (await tree.FindAsync(target.Path, ct) is not null) throw new DavException(StatusCodes.Status405MethodNotAllowed, "Something exists there already.");
        await ParentAsync(target.Path, ct);
        if (!await ParkedAsync(target.Path, place, ct))
        {
            var files = writer.ForAttachments() ?? throw NoAttachments();
            var made = await files.MakeFolderAsync(target.Path, ct);
            if (made.IsError) throw Refusal(made);
            // Made by another request meanwhile: mkdir leaves it as it is, and writes nothing.
            if (made.Version is null) throw new DavException(StatusCodes.Status405MethodNotAllowed, "Something exists there already.");
            Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        // Another request of the share may have made it meanwhile.
        if (!parking.MakeFolder(caller.ShareId, place.VaultId, place.Path)) throw new DavException(StatusCodes.Status405MethodNotAllowed, "Something exists there already.");
        Response.StatusCode = StatusCodes.Status201Created;
    }

    /// <summary><c>DELETE</c> (RFC 4918 §9.6): 204, a folder with everything below it; 404 if there is nothing.</summary>
    public async Task DeleteAsync(DavTarget target, CancellationToken ct)
    {
        var place = Writable(target.Path);
        if (await tree.FindAsync(target.Path, ct) is null) throw NotFound();
        if (!await ParkedAsync(target.Path, place, ct)) throw NotYet();

        parking.Remove(caller.ShareId, place.VaultId, place.Path);
        Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>
    /// <c>COPY</c> and <c>MOVE</c> (RFC 4918 §9.8, §9.9) within one vault: 201 to a free destination, 204 over one that
    /// was there, which <c>Overwrite: F</c> refuses (412). A <c>Destination</c> that isn't on this server's
    /// <c>/dav/</c>, or in another vault, is a 502: the server won't go there, and a file manager falls back to copying
    /// and deleting (WebDAV design §3.5). <c>COPY</c> with <c>Depth: 0</c> copies a folder without what is in it;
    /// <c>MOVE</c> always moves everything.
    /// </summary>
    public async Task TransferAsync(DavTarget source, bool move, CancellationToken ct)
    {
        var from = Writable(source.Path);
        var header = Request.Headers["Destination"].ToString();
        if (header.Length == 0) throw new DavException(StatusCodes.Status400BadRequest, "COPY and MOVE name where to in a Destination header.");
        var destination = DavDestination.Parse(Request, header)
            ?? throw new DavException(StatusCodes.Status502BadGateway, "The destination is not on this server's WebDAV.");
        if (destination.Path.IsRoot || caller.VaultId(destination.Path.Segments[0]) != from.VaultId)
        {
            throw new DavException(StatusCodes.Status502BadGateway, "Kumbu² copies and moves within one vault only.");
        }

        var to = Writable(destination.Path);
        var deep = Depth(move ? ["infinity"] : ["0", "infinity"]) != "0";
        var overwrite = Overwrite();
        if (to.Path == from.Path) throw new DavException(StatusCodes.Status403Forbidden, "The source and the destination are the same.");
        if (to.Path.StartsWith(from.Path + "/", StringComparison.Ordinal)) throw new DavException(StatusCodes.Status403Forbidden, "A folder can't go into itself.");
        // Over a folder above it, the destination's removal would take the source with it.
        if (from.Path.StartsWith(to.Path + "/", StringComparison.Ordinal)) throw new DavException(StatusCodes.Status403Forbidden, "Nothing can go onto a folder it is in.");

        if (await tree.FindAsync(source.Path, ct) is null) throw NotFound();
        await ParentAsync(destination.Path, ct);
        var existing = await tree.FindAsync(destination.Path, ct);
        if (existing is not null && !overwrite) throw new DavException(StatusCodes.Status412PreconditionFailed, "Something exists at the destination, and Overwrite is F.");
        if (!await ParkedAsync(source.Path, from, ct) || !await ParkedAsync(destination.Path, to, ct)) throw NotYet();

        if (existing is not null) parking.Remove(caller.ShareId, to.VaultId, to.Path);
        var done = move ? parking.Move(caller.ShareId, from.VaultId, from.Path, to.Path) : parking.Copy(caller.ShareId, from.VaultId, from.Path, to.Path, deep);
        // Expired or removed by another request of the share since it was found.
        if (!done) throw NotFound();
        Response.StatusCode = existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }

    /// <summary>
    /// <c>LOCK</c> (RFC 4918 §9.10): with a <c>lockinfo</c> body a new lock, 200, and its token in <c>Lock-Token</c>;
    /// without a body the refresh of the lock its <c>If</c> header names, 200, or 412 if the share holds none such.
    /// A lock asks for what a write does: a share that may only read gets 403, and Office then opens the file read-only.
    /// On an unmapped path a parked name gets an empty parked file (201), as Go's x/net/webdav creates one; a name of the
    /// store gets its lock and nothing else (200), since the store keeps no empty file of an unknown kind. Neither
    /// kind is ever enforced.
    /// </summary>
    public async Task LockAsync(DavTarget target, CancellationToken ct)
    {
        if (target.Path.IsRoot) throw new DavException(StatusCodes.Status403Forbidden, "The share's root can't be locked; what is in it can.");
        var name = target.Path.Segments[0];
        var place = caller.Place(target.Path) ?? throw NotFound();
        if (!caller.CanWrite(name)) throw new DavException(StatusCodes.Status403Forbidden, caller.ReadOnly(name));

        var body = await DavXml.ReadBodyAsync(Request, ct);
        var timeout = Timeout();
        if (body is null)
        {
            var token = LockToken().Match(Request.Headers["If"].ToString()) is { Success: true } m ? m.Groups[1].Value
                : throw new DavException(StatusCodes.Status400BadRequest, "A LOCK without a body refreshes the lock its If header names.");
            var refreshed = locks.Refresh(caller.ShareId, token, timeout)
                ?? throw new DavException(StatusCodes.Status412PreconditionFailed, "This share holds no such lock.", DavXml.Error("lock-token-matches-request-uri"));
            await DavXml.WriteAsync(Response, StatusCodes.Status200OK, Discovery(refreshed), ct);
            return;
        }

        var (exclusive, owner) = DavXml.ParseLockInfo(body);
        var deep = Depth(["0", "infinity"]) != "0";
        var status = StatusCodes.Status200OK;
        var resource = await tree.FindAsync(target.Path, ct);
        if (resource is null)
        {
            await ParentAsync(target.Path, ct);
            if (await ParkedAsync(target.Path, place, ct))
            {
                parking.Put(caller.ShareId, place.VaultId, place.Path, []);
                status = StatusCodes.Status201Created;
            }
        }

        var href = resource?.Href ?? DavPaths.Href(target.Path, collection: false);
        var granted = locks.Lock(caller.ShareId, place.VaultId, place.Path, href, deep, exclusive, owner, timeout);
        Response.Headers["Lock-Token"] = $"<{granted.Token}>";
        await DavXml.WriteAsync(Response, status, Discovery(granted), ct);
    }

    /// <summary><c>UNLOCK</c> (RFC 4918 §9.11): 204 for a lock the share holds, 409 for any other token.</summary>
    public Task UnlockAsync(CancellationToken ct)
    {
        var header = Request.Headers["Lock-Token"].ToString().Trim();
        if (header.Length < 3 || header[0] != '<' || header[^1] != '>') throw new DavException(StatusCodes.Status400BadRequest, "UNLOCK names its lock in a Lock-Token header.");
        if (!locks.Unlock(caller.ShareId, header[1..^1]))
        {
            throw new DavException(StatusCodes.Status409Conflict, "This share holds no such lock.", DavXml.Error("lock-token-matches-request-uri"));
        }

        Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }

    /// <summary>
    /// A note's <c>PUT</c>: at most <see cref="NoteWriter.MaxBytes"/> (413), UTF-8 (415), its preconditions (412), and
    /// written through <see cref="NoteWriter"/>, which makes <c>\r\n</c> <c>\n</c> as every write path does. A name
    /// ending in <c>.MD</c> or any other case is the note ending in <c>.md</c>, where the store keeps notes; the answer
    /// then says where in <c>Location</c>. Every check runs before anything is written.
    /// </summary>
    private async Task PutNoteAsync(VaultPath path, CancellationToken ct)
    {
        var stored = path.IsNote ? path : Parent(path).Child(path.Name[..^3] + ".md");
        if (stored != path && await tree.FindAsync(stored, ct) is { IsCollection: true }) throw new DavException(StatusCodes.Status405MethodNotAllowed, "A folder has no content to write.");
        if (writer.Caller.Refuse("write", stored) is { } refused) throw Refusal(refused);

        var bytes = await DavBody.ReadLimitedAsync(Request, NoteWriter.MaxBytes, ct)
            ?? throw new DavException(StatusCodes.Status413PayloadTooLarge, $"A note can have at most {NoteWriter.MaxBytes} bytes (1 MiB).");
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new DavException(StatusCodes.Status415UnsupportedMediaType, "A note is UTF-8 text; this one isn't.");
        }

        var existing = await tree.FindStoredAsync(stored, ct);
        Preconditions(existing?.Version);
        var result = await writer.WriteAsync(stored.Value, text, existing?.Version, ct);
        if (result.IsError && await LostRaceAsync(stored, existing?.Version, ct) is var (lost, now) && lost)
        {
            result = await writer.WriteAsync(stored.Value, text, now, ct);
        }

        if (result.IsError) throw Refusal(result);
        if (stored != path) Response.Headers.Location = DavPaths.Href(stored, collection: false);
        Written(result.Version!.Token, created: existing is null);
    }

    /// <summary>
    /// An attachment's <c>PUT</c>: at most <see cref="AttachmentFormat.MaxBytes"/> (413), its preconditions (412), and
    /// written through <see cref="AttachmentWriter"/>, its bytes checked against the format the name says (415 with the
    /// reason). An empty body is the placeholder a file manager writes before the content (WebDAV design §3.5): parked,
    /// for this share, unless the store has the file, which then stays as it is until the content comes. The content
    /// goes to the store and takes the placeholder's place.
    /// </summary>
    private async Task PutAttachmentAsync(VaultPath path, DavPlace place, AttachmentFormat format, CancellationToken ct)
    {
        var files = writer.ForAttachments() ?? throw NoAttachments();
        if (writer.Caller.Refuse("upload", path) is { } refused) throw Refusal(refused);
        var bytes = await DavBody.ReadLimitedAsync(Request, AttachmentFormat.MaxBytes, ct)
            ?? throw new DavException(StatusCodes.Status413PayloadTooLarge, "A file can have at most 15 MB.");

        var existing = await tree.FindStoredAsync(path, ct);
        var placeholder = existing is null ? tree.Parked(path) : null;
        Preconditions(existing?.Version ?? placeholder?.Version);
        if (bytes.Length == 0)
        {
            if (existing is not null)
            {
                Written(existing.Version!, created: false);
                return;
            }

            var (item, created) = parking.Put(caller.ShareId, place.VaultId, place.Path, bytes);
            Written(item.Version, created);
            return;
        }

        if (AttachmentFormat.Problem(path, bytes) is { } problem) throw new DavException(StatusCodes.Status415UnsupportedMediaType, problem + ".");
        // The dimensions stored are read here from the bytes stored, as on /api/files.
        (int, int)? dimensions = null;
        if (format.Kind == AttachmentKind.Image)
        {
            dimensions = ImageHeader.Read(bytes, out var headerProblem)
                ?? throw new DavException(StatusCodes.Status415UnsupportedMediaType, headerProblem ?? "The image's header doesn't read.");
        }

        var (result, _) = await files.WriteAsync(path.Value, bytes, dimensions, existing?.Version, ct);
        if (result.IsError && await LostRaceAsync(path, existing?.Version, ct) is var (lost, now) && lost)
        {
            (result, _) = await files.WriteAsync(path.Value, bytes, dimensions, now, ct);
        }

        if (result.IsError) throw Refusal(result);
        if (placeholder is not null) parking.Remove(caller.ShareId, place.VaultId, place.Path);
        Written(result.Version!.Token, created: existing is null && placeholder is null);
    }

    /// <summary>
    /// <c>If-Match</c> and <c>If-None-Match</c> (RFC 9110 §13.1.1, §13.1.2) against <paramref name="version"/>, the
    /// version of what is at the path, null if nothing is: a 412 unless they hold. <c>If-Match</c> compares strongly,
    /// <c>If-None-Match</c> weakly; <c>*</c> stands for any version.
    /// </summary>
    private void Preconditions(string? version)
    {
        var etag = version is null ? null : new EntityTagHeaderValue($"\"{version}\"");
        var ifMatch = Request.Headers.IfMatch;
        if (ifMatch.Count > 0)
        {
            var holds = etag is not null && EntityTagHeaderValue.TryParseList(ifMatch, out var tags)
                && tags.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Compare(etag, useStrongComparison: true));
            if (!holds) throw new DavException(StatusCodes.Status412PreconditionFailed, "The file isn't at the version If-Match names.");
        }

        var ifNoneMatch = Request.Headers.IfNoneMatch;
        if (ifNoneMatch.Count > 0 && etag is not null && EntityTagHeaderValue.TryParseList(ifNoneMatch, out var none)
            && none.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Compare(etag, useStrongComparison: false)))
        {
            throw new DavException(StatusCodes.Status412PreconditionFailed, "Something is there already, and If-None-Match says it may not be.");
        }
    }

    /// <summary>
    /// After a write that failed: whether another write came in between this one's look and its write (WebDAV design
    /// §3.5), and the version of what that other one left there (null for nothing). Without a precondition, the write
    /// is then tried once more on that state, and overwrites it: WebDAV has no merge. A write with one was asked of
    /// the state it named, which is gone: 412.
    /// </summary>
    private async Task<(bool Lost, string? Now)> LostRaceAsync(VaultPath path, string? version, CancellationToken ct)
    {
        var now = await tree.FindStoredAsync(path, ct);
        if (now is { IsCollection: true } || now?.Version == version) return (false, null);
        if (Request.Headers.IfMatch.Count > 0 || Request.Headers.IfNoneMatch.Count > 0)
        {
            throw new DavException(StatusCodes.Status412PreconditionFailed, "The file changed while it was written.");
        }

        return (true, now?.Version);
    }

    /// <summary>The answer to a <c>PUT</c> that stored <paramref name="version"/>.</summary>
    private void Written(string version, bool created)
    {
        Response.Headers.ETag = $"\"{version}\"";
        Response.StatusCode = created ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }

    /// <summary>A writer's refusal as <c>/api/notes</c> and <c>/api/files</c> answer it (<see cref="Api.FileApi"/>):
    /// a refused right 403, nothing there 404, the vault's state in the way 409, anything else the request's fault,
    /// 400; with the writer's sentence.</summary>
    private static DavException Refusal(ShellResult result) => new(
        result.Code is not null || result.Kind == ShellErrorKind.Refused ? StatusCodes.Status403Forbidden
        : result.Kind switch
        {
            ShellErrorKind.NotFound => StatusCodes.Status404NotFound,
            ShellErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        },
        result.Output);

    private static DavException NoAttachments() => new(StatusCodes.Status404NotFound, "This server keeps no attachments.");

    private static VaultPath Parent(VaultPath path) => path.Segments.SkipLast(1).Aggregate(VaultPath.Root, (p, s) => p.Child(s));

    /// <summary>
    /// Where a method writes at <paramref name="path"/>: the vault and the path below it. The root and a vault's own folder
    /// are never written to (403; brief open point 6), a vault the share doesn't have is not found, and one it may only
    /// read is a 403, parking included (WebDAV design §3.5: a share with <c>read</c> writes nothing).
    /// </summary>
    private DavPlace Writable(VaultPath path)
    {
        if (path.Segments.Count < 2) throw new DavException(StatusCodes.Status403Forbidden, "The share's root and a vault's own folder can't be changed; what is in them can.");
        var place = caller.Place(path) ?? throw NotFound();
        if (!caller.CanWrite(path.Segments[0])) throw new DavException(StatusCodes.Status403Forbidden, caller.ReadOnly(path.Segments[0]));
        return place;
    }

    /// <summary>
    /// Whether a write at <paramref name="path"/> goes to the parking area: the store has nothing there (what the store
    /// has stays the store's), and its name is parked, or it is parked already, or a parked folder is above it. Such a
    /// folder holds what goes into it only while the store has no folder of that name: once the store has one (a note
    /// written into it from elsewhere), what goes there is the store's, unless its own name is parked. Without
    /// <paramref name="itself"/>, what is parked at the path itself doesn't count: an empty attachment parked as a
    /// placeholder holds its place until the content comes, and the content is the store's.
    /// </summary>
    private async Task<bool> ParkedAsync(VaultPath path, DavPlace place, CancellationToken ct, bool itself = true)
    {
        if (await tree.FindStoredAsync(path, ct) is not null) return false;
        if (DavRules.IsParked([.. path.Segments.Skip(1)])) return true;
        var below = itself ? place.Path : place.Path[..Math.Max(0, place.Path.LastIndexOf('/'))];
        if (parking.Holder(caller.ShareId, place.VaultId, below) is not { } holder) return false;
        if (holder == place.Path) return true;
        var folder = path.Segments.Take(2 + holder.Count(c => c == '/')).Aggregate(VaultPath.Root, (p, s) => p.Child(s));
        return await tree.FindStoredAsync(folder, ct) is null;
    }

    /// <summary>A 409 if the folder <paramref name="path"/> would be in doesn't exist (RFC 4918 §9.3.1, §9.7.1).</summary>
    private async Task ParentAsync(VaultPath path, CancellationToken ct)
    {
        if (await tree.FindAsync(Parent(path), ct) is not { IsCollection: true }) throw new DavException(StatusCodes.Status409Conflict, "The folder it would be in doesn't exist.");
    }

    /// <summary>The <c>Depth</c> header, one of <paramref name="allowed"/>; none means <c>infinity</c> (RFC 4918 §10.2).</summary>
    private string Depth(string[] allowed)
    {
        var depth = Request.Headers["Depth"].ToString().Trim().ToLowerInvariant();
        if (depth.Length == 0) depth = "infinity";
        return allowed.Contains(depth) ? depth : throw new DavException(StatusCodes.Status400BadRequest, $"Depth is {string.Join(" or ", allowed)} here.");
    }

    /// <summary>The <c>Overwrite</c> header (RFC 4918 §10.6): <c>T</c>, the default, or <c>F</c>.</summary>
    private bool Overwrite() => Request.Headers["Overwrite"].ToString().Trim() switch
    {
        "" or "T" or "t" => true,
        "F" or "f" => false,
        _ => throw new DavException(StatusCodes.Status400BadRequest, "Overwrite is T or F."),
    };

    /// <summary>The timeout the client's first <c>Timeout</c> choice asks for (RFC 4918 §10.7); null for
    /// <c>Infinite</c>, none, or one that doesn't read, which all get the default. <see cref="DavLocks"/> decides what is
    /// granted; the bound here only keeps a huge number a <see cref="TimeSpan"/>.</summary>
    private TimeSpan? Timeout()
    {
        var first = Request.Headers["Timeout"].ToString().Split(',')[0].Trim();
        return first.StartsWith("Second-", StringComparison.OrdinalIgnoreCase) && long.TryParse(first["Second-".Length..], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, int.MaxValue))
            : null;
    }

    /// <summary>The body of a <c>LOCK</c>'s answer: a <c>prop</c> with the lock's <c>lockdiscovery</c>.</summary>
    private XElement Discovery(DavLock held) =>
        new(DavXml.Dav + "prop", new XAttribute(XNamespace.Xmlns + "D", DavXml.Dav.NamespaceName), DavXml.LockDiscovery([(held, locks.SecondsLeft(held))]));

    [GeneratedRegex("<(opaquelocktoken:[^>]+)>", RegexOptions.IgnoreCase)]
    private static partial Regex LockToken();

    private static DavException NotFound() => new(StatusCodes.Status404NotFound, "Not found.");

    /// <summary>Deleting, copying and moving in the store over WebDAV: the steps after these.</summary>
    private static DavException NotYet() => new(StatusCodes.Status501NotImplemented, "Deleting, copying and moving notes over WebDAV isn't there yet.");
}
