// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kumbu.Core.Paths;

namespace Kumbu.Server.Dav;

/// <summary>
/// The methods that write under <c>/dav</c> (WebDAV design §3.5–§3.8): <c>PUT</c>, <c>MKCOL</c>, <c>DELETE</c>,
/// <c>COPY</c>, <c>MOVE</c>, and <c>LOCK</c> and <c>UNLOCK</c>. Each first does what the protocol asks of it wherever it
/// writes (the share's right, the root and the vaults' own folders refused, a missing parent, <c>Destination</c>,
/// <c>Depth</c>, <c>Overwrite</c> and the statuses they lead to), then writes either to the parking area, for a path
/// that is parked (<see cref="DavRules.IsParked"/>, or below a parked folder) and that the store has nothing at, or to
/// the store. So litmus, which works in a dot-folder, tests the same protocol layer the store's files go through
/// (spec §7). Writing to the store comes in later steps; until then it answers 501. Locks are never enforced:
/// <c>If</c> headers are read only to refresh a lock, and ignored everywhere else (§3.7).
/// </summary>
public sealed partial class DavWrites(HttpContext http, DavCaller caller, DavTree tree, DavParking parking, DavLocks locks)
{
    private HttpRequest Request => http.Request;

    private HttpResponse Response => http.Response;

    /// <summary><c>PUT</c> (RFC 4918 §9.7): 201 for a new file, 204 for one replaced, with the <c>ETag</c> of what was
    /// stored.</summary>
    public async Task PutAsync(DavTarget target, CancellationToken ct)
    {
        var place = Writable(target.Path);
        if (await tree.FindAsync(target.Path, ct) is { IsCollection: true }) throw new DavException(StatusCodes.Status405MethodNotAllowed, "A folder has no content to write.");
        await ParentAsync(target.Path, ct);
        if (!await ParkedAsync(target.Path, place, ct)) throw NotYet();

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
        if (!await ParkedAsync(target.Path, place, ct)) throw NotYet();

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
    /// written into it from elsewhere), what goes there is the store's, unless its own name is parked.
    /// </summary>
    private async Task<bool> ParkedAsync(VaultPath path, DavPlace place, CancellationToken ct)
    {
        if (await tree.FindStoredAsync(path, ct) is not null) return false;
        if (DavRules.IsParked([.. path.Segments.Skip(1)])) return true;
        if (parking.Holder(caller.ShareId, place.VaultId, place.Path) is not { } holder) return false;
        if (holder == place.Path) return true;
        var folder = path.Segments.Take(2 + holder.Count(c => c == '/')).Aggregate(VaultPath.Root, (p, s) => p.Child(s));
        return await tree.FindStoredAsync(folder, ct) is null;
    }

    /// <summary>A 409 if the folder <paramref name="path"/> would be in doesn't exist (RFC 4918 §9.3.1, §9.7.1).</summary>
    private async Task ParentAsync(VaultPath path, CancellationToken ct)
    {
        var parent = path.Segments.SkipLast(1).Aggregate(VaultPath.Root, (p, s) => p.Child(s));
        if (await tree.FindAsync(parent, ct) is not { IsCollection: true }) throw new DavException(StatusCodes.Status409Conflict, "The folder it would be in doesn't exist.");
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

    /// <summary>Writing to the store over WebDAV: the steps after parking.</summary>
    private static DavException NotYet() => new(StatusCodes.Status501NotImplemented, "Writing notes and attachments over WebDAV isn't there yet.");
}
