// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;
using Kumbu.Core.Sh;
using Kumbu.Server.Attachments;
using Kumbu.Server.Publishing;
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
/// the writer label and a person's changeset (§3.2); <c>DELETE</c> and <c>COPY</c> through <see cref="TreeWriter"/>, a
/// folder with everything below it in one batch; <c>MOVE</c> through <see cref="NoteMover"/>, as <c>sh mv</c>, with what
/// an editor's atomic save does in between (§3.6 cases 1–5, <see cref="DavHolds"/>). A share never may withdraw a
/// publication (publish design §3.7), so a published note it removes is held and not carried out (cases 6 and 7,
/// <see cref="Publications"/>). Locks are never enforced: <c>If</c> headers are read only to refresh a lock, and
/// ignored everywhere else (§3.7).
/// </summary>
internal sealed partial class DavWrites(
    HttpContext http, DavCaller caller, DavTree tree, DavParking parking, DavLocks locks, NoteWriter writer, TreeWriter trees, NoteMover mover,
    DavHolds holds, Publications publications, ILogger<DavWrites> log)
{
    /// <summary>A note's bytes are UTF-8 or refused (WebDAV design §3.5): an invalid byte throws rather than becoming
    /// U+FFFD. Decoding with <see cref="Encoding.GetString(byte[])"/> keeps a BOM as the text's first character.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private HttpRequest Request => http.Request;

    private HttpResponse Response => http.Response;

    /// <summary>
    /// <c>PUT</c> (RFC 4918 §9.7): 201 for a new file, 204 for one replaced, with the <c>ETag</c> of what was stored.
    /// What it is goes by its name's extension, in any case (brief open point 6; a note's name the target's resolution
    /// already spelt <c>.md</c>): a note, an attachment of a format Kumbu² keeps, a parked file, or a 415. A parked
    /// placeholder (an empty attachment) is not parked for its own sake: the write that follows it goes to the store.
    /// </summary>
    public async Task PutAsync(DavTarget target, CancellationToken ct)
    {
        var place = Writable(target.Path);
        if (await tree.FindAsync(target.Path, ct) is { IsCollection: true }) throw new DavException(StatusCodes.Status405MethodNotAllowed, "A folder has no content to write.");
        await ParentAsync(target.Path, ct);
        if (!await ParkedAsync(target.Path, place, ct, itself: false))
        {
            var name = target.Path.Name;
            if (target.Path.IsNote) await PutNoteAsync(target, ct);
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

    /// <summary>
    /// The share's holds whose minute is up, ended (WebDAV design §3.6 case 4): at the start of each of its requests,
    /// before anything else, with this request's writers, so with the rights the share has now (<see cref="DavHolds"/>).
    /// </summary>
    public Task EndHoldsAsync() => holds.ExpireAsync(caller.ShareId, EndAsync);

    /// <summary>
    /// A held note to the trash as the share's <c>DELETE</c> would have sent it, through <see cref="NoteWriter.RemoveAsync"/>
    /// (or <see cref="AttachmentWriter.RemoveAsync"/>) with its rules, and only at the version it was held at: changed
    /// meanwhile (from the web app, say), moved, or in a vault the share now names otherwise, it stays as it is. Nothing new
    /// is recorded for case 3. What the writer refuses stays too, which loses nothing: a right the share has lost, a busy
    /// vault. A published note (§3.6 cases 6 and 7) is never trashed: the hold is over, so the share sees it where it is
    /// again, and the attempt is recorded for the owner (<see cref="KeptAsync"/>); so too for one published in the minute,
    /// which the store refuses to trash.
    /// </summary>
    private async Task EndAsync(DavHeld held)
    {
        // Not the request's token: an aborted request mustn't leave an ending half done or a hold lost.
        var ct = CancellationToken.None;
        try
        {
            var path = held.Original;
            if (caller.Place(path)?.VaultId != held.VaultId) return;
            if (held.Published)
            {
                await KeptAsync(path, ct);
                return;
            }

            var result = path.IsNote ? await writer.RemoveAsync(path, ct, held.Version)
                : writer.ForAttachments() is { } files ? await files.RemoveAsync(path.Value, ct, held.Version)
                : null;
            // The kind, not the sentence: that names the note, and a log names no note.
            if (result is { IsError: true }) log.LogInformation("A note WebDAV share {ShareId} held stays ({Kind})", held.ShareId, result.Kind);
        }
        catch (PublishedNoteException)
        {
            log.LogInformation("A published note WebDAV share {ShareId} held stays where it is", held.ShareId);
            await KeptAsync(held.Original, ct);
        }
        // A vault busy or deleted, MongoDB away: the note stays, and the request goes on.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("A note WebDAV share {ShareId} held stays ({Error})", held.ShareId, e.GetType().Name);
        }
    }

    /// <summary>
    /// The owner's activity records that the share tried to remove the published note at <paramref name="path"/>, which
    /// stays (WebDAV design §3.6 case 6), with the share's writer label, and only while the share may still write in the
    /// vault: recorded with the ending request's rights, as anything the share writes. A failure is logged, and the note
    /// stays all the same.
    /// </summary>
    private async Task KeptAsync(VaultPath path, CancellationToken ct)
    {
        var name = path.Segments[0];
        if (!caller.CanWrite(name) || caller.Access(name) is not { } access) return;
        try
        {
            await publications.RecordKeptAsync(access, path, writer.Caller.WriterIn(name), writer.Caller.ReferenceIn(name), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("WebDAV share {ShareId}'s attempt on a published note isn't recorded ({Error})", caller.ShareId, e.GetType().Name);
        }
    }

    /// <summary>The published notes at or below <paramref name="path"/> as the share sees them (WebDAV design §3.6 cases 6
    /// and 7); none in a vault without publications.</summary>
    private async Task<IReadOnlySet<VaultPath>> PublishedAsync(VaultPath path, CancellationToken ct) =>
        caller.Access(path.Segments[0]) is { } access ? (await publications.PublishedUnderAsync(access, path, ct)).ToHashSet() : new HashSet<VaultPath>();

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
        // Not parked, a name the share holds a note under: the note goes in its minute as it would have, and the
        // DELETE does nothing more (WebDAV design §3.6 case 4).
        if (await ParkedAsync(target.Path, place, ct))
        {
            if (!parking.Remove(caller.ShareId, place.VaultId, place.Path)) holds.Delete(caller.ShareId, place.VaultId, place.Path);
        }
        else await DeleteStoredAsync(target.Path, ct);
        Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>
    /// A <c>DELETE</c> in the store: the note, the attachment, or the folder with everything below it to the trash, in
    /// one batch, as <see cref="TreeWriter"/> does it, with the rights it asks for (a folder with a structural file in
    /// it needs <c>maintain</c>). Each note it trashed is recorded for the share (<see cref="DavHolds"/>), so that an
    /// editor's save that deletes the original first gets it back (WebDAV design §3.6 case 3). A published note, which a
    /// share never may withdraw, isn't trashed but held (case 6): hidden from the share for the minute, and in a folder the
    /// rest goes and the published notes stay, with the folders above them; the rights are the remover's, on all of it. One published meanwhile still makes the store
    /// refuse the whole batch (<see cref="PublishedNoteException"/>, a 403), and nothing goes.
    /// </summary>
    private async Task DeleteStoredAsync(VaultPath path, CancellationToken ct)
    {
        var published = await PublishedAsync(path, ct);
        var removed = await trees.RemoveAsync(path, ct, keep: published);
        if (removed.Result.IsError) throw Refusal(removed.Result);
        foreach (var note in removed.Trashed)
        {
            if (caller.Place(note.Path) is { } at) holds.RecordRemoval(caller.ShareId, at.VaultId, at.Path, note.Version);
        }

        foreach (var note in published) await HoldDeletedAsync(note, ct);
    }

    /// <summary>The published note at <paramref name="path"/>, which the share deleted, held under no name
    /// (<see cref="DavHolds.HoldDeleted"/>); nothing if it is gone meanwhile.</summary>
    private async Task HoldDeletedAsync(VaultPath path, CancellationToken ct)
    {
        if (caller.Place(path) is { } at && await tree.FindStoredAsync(path, ct) is { IsCollection: false, Version: { } version })
        {
            holds.HoldDeleted(caller.ShareId, at.VaultId, path, version);
        }
    }

    /// <summary>
    /// <c>COPY</c> and <c>MOVE</c> (RFC 4918 §9.8, §9.9) within one vault: 201 to a free destination, 204 over one that
    /// was there, which <c>Overwrite: F</c> refuses (412). A <c>Destination</c> that isn't on this server's
    /// <c>/dav/</c>, or in another vault, is a 502: the server won't go there, and a file manager falls back to copying
    /// and deleting (WebDAV design §3.5). <c>COPY</c> with <c>Depth: 0</c> copies a folder without what is in it;
    /// <c>MOVE</c> always moves everything. A <c>MOVE</c> of what the share holds under a backup name takes the held
    /// note's place in it: back onto its own path, it ends the hold and writes nothing (an editor that gave up its save).
    /// </summary>
    public async Task TransferAsync(DavTarget source, bool move, CancellationToken ct)
    {
        var from = Writable(source.Path);
        var header = Request.Headers["Destination"].ToString();
        if (header.Length == 0) throw new DavException(StatusCodes.Status400BadRequest, "COPY and MOVE name where to in a Destination header.");
        var parsed = DavDestination.Parse(Request, header)
            ?? throw new DavException(StatusCodes.Status502BadGateway, "The destination is not on this server's WebDAV.");
        var destination = parsed with { Path = await tree.ResolveAsync(parsed.Path, ct) };
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
        if (move && await ReleaseHeldAsync(source.Path, from, ct) is { } original)
        {
            if (original == destination.Path)
            {
                Response.StatusCode = StatusCodes.Status201Created;
                return;
            }

            (source, from) = (new DavTarget(original), caller.Place(original) ?? throw NotFound());
        }

        var stored = await tree.FindStoredAsync(source.Path, ct) is not null;
        var parked = await ParkedAsync(destination.Path, to, ct);
        var replaced = existing is not null;
        if (!move && (stored || !parked))
        {
            if (stored && !parked) await CopyStoredAsync(source.Path, destination.Path, deep, replaced, ct);
            else if (stored) await CopyIntoParkingAsync(source.Path, to, deep, replaced, ct);
            else await CopyOutOfParkingAsync(source.Path, destination, to, move: false, ct);
            return;
        }

        if (stored || !parked)
        {
            if (stored && !parked) await MoveStoredAsync(source.Path, destination.Path, replaced, ct);
            else if (stored) await HoldAsync(source.Path, to, replaced, ct);
            else await CopyOutOfParkingAsync(source.Path, destination, to, move: true, ct);
            return;
        }

        if (existing is not null) parking.Remove(caller.ShareId, to.VaultId, to.Path);
        var done = move ? parking.Move(caller.ShareId, from.VaultId, from.Path, to.Path) : parking.Copy(caller.ShareId, from.VaultId, from.Path, to.Path, deep);
        // Expired or removed by another request of the share since it was found.
        if (!done) throw NotFound();
        Response.StatusCode = existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }

    /// <summary>
    /// A <c>COPY</c> within the store: <see cref="TreeWriter.CopyAsync"/>, which writes the source's content at the
    /// destination and rewrites no link, a folder with everything below it unless <paramref name="deep"/> is false. What is
    /// at the destination goes to the trash in the same batch, after every check of the copy passed, so that a refused copy
    /// never costs the target.
    /// </summary>
    private async Task CopyStoredAsync(VaultPath from, VaultPath to, bool deep, bool replaced, CancellationToken ct)
    {
        var copied = await trees.CopyAsync(from, to, deep, overwrite: true, ct);
        if (copied.Result.IsError) throw Refusal(copied.Result);
        if (copied.Result.Version is { } version) Response.Headers.ETag = $"\"{version.Token}\"";
        Response.StatusCode = replaced ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
    }

    /// <summary>A <c>MOVE</c> of a name the share holds a note under (and nothing else is under): the hold ends, and the
    /// held note's path is what moves on (WebDAV design §3.6 case 4); null for any other name.</summary>
    private async Task<VaultPath?> ReleaseHeldAsync(VaultPath path, DavPlace place, CancellationToken ct)
    {
        if (tree.Held(path) is null || await tree.FindStoredAsync(path, ct) is not null || tree.Parked(path) is not null) return null;
        return holds.Release(caller.ShareId, place.VaultId, place.Path)?.Original;
    }

    /// <summary>
    /// A <c>MOVE</c> within the store (WebDAV design §3.6 cases 1 and 5): <see cref="NoteMover"/>, as <c>sh mv</c>, a
    /// note, an attachment or a folder with everything below it, and every link the move changes rewritten, in one
    /// batch. Over what is at the destination (<paramref name="replaced"/>, <c>Overwrite: T</c>), that goes to the trash
    /// in the same batch, after every check of the move passed.
    /// </summary>
    private async Task MoveStoredAsync(VaultPath from, VaultPath to, bool replaced, CancellationToken ct)
    {
        var moved = replaced ? await mover.MoveOverAsync(from, to, ct)
            : await tree.FindStoredAsync(from, ct) is { IsCollection: true } ? await mover.MoveFolderAsync(from, to, rewriteLinks: true, ct)
            : await mover.MoveAsync(from, to, rewriteLinks: true, ct);
        if (moved.IsError) throw Refusal(moved);
        if (moved.Version is { } version) Response.Headers.ETag = $"\"{version.Token}\"";
        Response.StatusCode = replaced ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
    }

    /// <summary>
    /// A <c>MOVE</c> of a note or an attachment onto a parked name (WebDAV design §3.6 case 4: an editor renaming the
    /// original to <c>plan.md~</c> before it writes the new one): held, not moved (<see cref="DavHolds"/>). The share
    /// sees it under that name and no longer at its path; the store and everyone else still have it where it was. Its
    /// ending is a <c>DELETE</c>'s, so a share that couldn't remove it (a structural file without <c>maintain</c>) is
    /// refused now rather than left wondering in a minute; the rules' source of truth stays the remover the ending calls
    /// (<see cref="EndAsync"/>), which checks them again with the rights the share has then. A folder is no editor's
    /// backup: 403. A published note is held too, and its ending trashes nothing (§3.6 case 7, with case 6's ending).
    /// </summary>
    private async Task HoldAsync(VaultPath from, DavPlace to, bool replaced, CancellationToken ct)
    {
        if (await tree.FindStoredAsync(from, ct) is not { IsCollection: false, Version: { } version })
        {
            throw new DavException(StatusCodes.Status403Forbidden, "A folder can't be moved onto a name Kumbu² doesn't keep.");
        }

        if (writer.Caller.RefuseFolder("rm", from) is { } refused) throw Refusal(refused);
        if (replaced) parking.Remove(caller.ShareId, to.VaultId, to.Path);
        holds.Hold(caller.ShareId, to.VaultId, to.Path, from, version, published: (await PublishedAsync(from, ct)).Contains(from));
        Response.StatusCode = replaced ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
    }

    /// <summary>
    /// A <c>COPY</c> of what the store has onto a parked name (a backup <c>plan.md~</c>, a folder into <c>.backup/</c>): a
    /// parked copy of what the share sees there, for this share only; the store is not touched. Everything is read
    /// before what was parked at the destination goes.
    /// </summary>
    private async Task CopyIntoParkingAsync(VaultPath from, DavPlace to, bool deep, bool replaced, CancellationToken ct)
    {
        var files = new List<(string Path, byte[]? Bytes)>();
        await ReadForParkingAsync(from, to.Path, deep, files, ct);
        if (replaced) parking.Remove(caller.ShareId, to.VaultId, to.Path);
        // Folders before what is in them, as they were read.
        foreach (var (path, bytes) in files)
        {
            if (bytes is null) parking.MakeFolder(caller.ShareId, to.VaultId, path);
            else parking.Put(caller.ShareId, to.VaultId, path, bytes);
        }

        Response.StatusCode = replaced ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
    }

    /// <summary>What <paramref name="from"/> holds as the share sees it, at <paramref name="at"/> below the vault: a
    /// file's bytes, or a folder (no bytes) and, if <paramref name="deep"/>, everything in it.</summary>
    private async Task ReadForParkingAsync(VaultPath from, string at, bool deep, List<(string, byte[]?)> files, CancellationToken ct)
    {
        if (await tree.ContentAsync(from, ct) is { } content)
        {
            await using var stream = await content.Open(ct);
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes, ct);
            files.Add((at, bytes.ToArray()));
            return;
        }

        if (await tree.FindAsync(from, ct) is not { IsCollection: true } folder) throw NotFound();
        files.Add((at, null));
        if (!deep) return;
        foreach (var child in await tree.ChildrenAsync(folder, ct)) await ReadForParkingAsync(child.Path, at + "/" + child.Path.Name, deep, files, ct);
    }

    /// <summary>
    /// A <c>COPY</c> or <c>MOVE</c> of a parked file onto a path of the store: a <c>PUT</c> of its bytes there (brief
    /// open point 6), with a <c>PUT</c>'s checks and statuses: a note's size and UTF-8, an attachment's format, the
    /// rights. Over a file the store has, that is a content write of it, as a <c>PUT</c> would be, which is one write
    /// after every check: a refusal leaves the target as it was. So an editor's temp file moved over its note is one
    /// version of the note and rewrites no link (WebDAV design §3.6 case 2); one moved onto a free note path that the
    /// share removed a note at within the minute brings that note back with it (case 3). A copied file stays parked, a
    /// moved one goes once it is written, and stays parked if the write is refused. A parked folder stays in the parking
    /// area: what is in it was parked because Kumbu² doesn't keep it (403).
    /// </summary>
    private async Task CopyOutOfParkingAsync(VaultPath from, DavTarget destination, DavPlace to, bool move, CancellationToken ct)
    {
        if (tree.Parked(from) is not { } item) throw NotFound();
        if (item.IsFolder) throw new DavException(StatusCodes.Status403Forbidden, "A parked folder can't be copied or moved out of where it is parked; take its files one at a time.");
        if (await tree.FindAsync(destination.Path, ct) is { IsCollection: true }) throw new DavException(StatusCodes.Status405MethodNotAllowed, "A folder has no content to write.");
        var path = destination.Path;
        if (path.IsNote) await PutNoteAsync(destination, ct, item.Bytes, moved: move);
        else if (AttachmentFormat.ForName(path.Name) is { } format && format != AttachmentFormat.Marker) await PutAttachmentAsync(path, to, format, ct, item.Bytes);
        else throw new DavException(StatusCodes.Status415UnsupportedMediaType, AttachmentFormat.UnsupportedMessage);
        if (move && caller.Place(from) is { } place) parking.Remove(caller.ShareId, place.VaultId, place.Path);
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
    /// written through <see cref="NoteWriter"/>, which makes <c>\r\n</c> <c>\n</c> as every write path does. Asked
    /// as <c>Plan.MD</c>, the answer says in <c>Location</c> where the note is. Every check runs before anything is
    /// written. A <c>COPY</c> or <c>MOVE</c> of a parked file passes its bytes as <paramref name="copied"/>: the request's
    /// preconditions are then the source's, not the note's; a <c>MOVE</c> says so in <paramref name="moved"/>.
    /// At the path of a note the share holds under a backup name, the write is a content write of that note, which the
    /// share saw as gone (WebDAV design §3.6 case 4).
    /// </summary>
    private async Task PutNoteAsync(DavTarget target, CancellationToken ct, byte[]? copied = null, bool moved = false)
    {
        var path = target.Path;
        if (writer.Caller.Refuse("write", path) is { } refused) throw Refusal(refused);

        var bytes = (copied is null ? await DavBody.ReadLimitedAsync(Request, NoteWriter.MaxBytes, ct) : copied.Length <= NoteWriter.MaxBytes ? copied : null)
            ?? throw new DavException(StatusCodes.Status413PayloadTooLarge, $"A note can have at most {NoteWriter.MaxBytes / 1024 / 1024} MiB.");
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new DavException(StatusCodes.Status415UnsupportedMediaType, "A note is UTF-8 text; this one isn't.");
        }

        var place = caller.Place(path) ?? throw NotFound();
        var arrived = holds.TakeArrival(caller.ShareId, place.VaultId, place.Path) is not null;
        var existing = await tree.FindStoredAsync(path, ct);
        if (copied is null) Preconditions(arrived ? null : existing?.Version);
        var restored = false;
        if (moved && existing is null && await RestoreRemovedAsync(path, place, ct) is { } back) (existing, restored) = (back, true);
        var (result, replaced) = await WriteOnceMoreIfLostAsync(path, existing?.Version, v => writer.WriteAsync(path.Value, text, v, ct), ct);
        if (result.IsError) throw Refusal(result);
        if (target.Asked is not null) Response.Headers.Location = DavPaths.Href(path, collection: false);
        Written(result.Version!.Token, created: !replaced || arrived || restored);
    }

    /// <summary>
    /// The note this share removed at <paramref name="path"/> within <see cref="DavRules.HoldWindow"/> (WebDAV design
    /// §3.6 case 3: an editor that deletes the original before it moves its temp file into place), back from the trash
    /// at the version it was removed at, history and all, for the write that follows; null if there is none such or it
    /// doesn't come back (the trash emptied meanwhile, say), and the write is then a new note. Another share's removal
    /// never counts: its editor isn't this one.
    /// </summary>
    private async Task<DavResource?> RestoreRemovedAsync(VaultPath path, DavPlace place, CancellationToken ct)
    {
        if (holds.TakeRemoval(caller.ShareId, place.VaultId, place.Path) is not { } version) return null;
        return (await writer.RestoreAsync(path, version, ct)).IsError ? null : await tree.FindStoredAsync(path, ct);
    }

    /// <summary>
    /// An attachment's <c>PUT</c>: at most <see cref="AttachmentFormat.MaxBytes"/> (413), its preconditions (412), and
    /// written through <see cref="AttachmentWriter"/>, whose check of the bytes against the format the name says is a
    /// 415 with its reason. An empty body is the placeholder a file manager writes before the content (WebDAV design
    /// §3.5): parked, for this share, unless the store has the file, which then stays as it is until the content
    /// comes. The content goes to the store and takes the placeholder's place. <paramref name="copied"/>, and a file the
    /// share holds at the path, as for <see cref="PutNoteAsync"/>.
    /// </summary>
    private async Task PutAttachmentAsync(VaultPath path, DavPlace place, AttachmentFormat format, CancellationToken ct, byte[]? copied = null)
    {
        var files = writer.ForAttachments() ?? throw NoAttachments();
        if (writer.Caller.Refuse("upload", path) is { } refused) throw Refusal(refused);
        var bytes = (copied is null ? await DavBody.ReadLimitedAsync(Request, AttachmentFormat.MaxBytes, ct) : copied.Length <= AttachmentFormat.MaxBytes ? copied : null)
            ?? throw new DavException(StatusCodes.Status413PayloadTooLarge, $"A file can have at most {AttachmentFormat.MaxBytes / 1024 / 1024} MB.");

        // An empty body leaves a held file held: the content that follows is its arrival.
        var arrived = bytes.Length > 0 && holds.TakeArrival(caller.ShareId, place.VaultId, place.Path) is not null;
        var existing = await tree.FindStoredAsync(path, ct);
        var placeholder = existing is null ? tree.Parked(path) : null;
        if (copied is null) Preconditions(arrived ? null : existing?.Version ?? placeholder?.Version);
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

        // The dimensions stored are read here from the bytes stored, as on /api/files; bytes that aren't of the
        // format have none, and the writer refuses them.
        (int, int)? dimensions = null;
        var matches = AttachmentFormat.Problem(path, bytes) is null;
        if (matches && format.Kind == AttachmentKind.Image)
        {
            dimensions = ImageHeader.Read(bytes, out var headerProblem)
                ?? throw new DavException(StatusCodes.Status415UnsupportedMediaType, headerProblem ?? "The image's header doesn't read.");
        }

        var (result, replaced) = await WriteOnceMoreIfLostAsync(
            path, existing?.Version, async v => (await files.WriteAsync(path.Value, bytes, dimensions, v, ct)).Result, ct);
        if (result.IsError) throw matches ? Refusal(result) : new DavException(StatusCodes.Status415UnsupportedMediaType, result.Output);
        if (placeholder is not null) parking.Remove(caller.ShareId, place.VaultId, place.Path);
        Written(result.Version!.Token, created: (!replaced || arrived) && placeholder is null);
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
    /// <paramref name="write"/> on the state of version <paramref name="version"/> (null: nothing there), and, if it lost
    /// the compare-and-swap to a write that came in between (WebDAV design §3.5), once more on the state that one left,
    /// which it then overwrites: WebDAV has no merge. A request with a precondition asked for the state it named, which
    /// is gone: 412. Whether the write replaced something, for 201 or 204.
    /// </summary>
    private async Task<(ShellResult Result, bool Replaced)> WriteOnceMoreIfLostAsync(
        VaultPath path, string? version, Func<string?, Task<ShellResult>> write, CancellationToken ct)
    {
        var result = await write(version);
        if (result.IsError && await OvertakenAsync(path, result, version, ct) is { } now)
        {
            if (Request.Headers.IfMatch.Count > 0 || Request.Headers.IfNoneMatch.Count > 0)
            {
                throw new DavException(StatusCodes.Status412PreconditionFailed, "The file changed while it was written.");
            }

            (result, version) = (await write(now), now);
        }

        return (result, version is not null);
    }

    /// <summary>The version another write left at <paramref name="path"/> when <paramref name="failed"/> is the
    /// writer's version conflict (<see cref="ShellErrorKind.Conflict"/>: changed, or created, meanwhile) and a file is
    /// there now at another version than <paramref name="version"/>; null for any other failure.</summary>
    private async Task<string?> OvertakenAsync(VaultPath path, ShellResult failed, string? version, CancellationToken ct)
    {
        if (failed.Kind != ShellErrorKind.Conflict) return null;
        return await tree.FindStoredAsync(path, ct) is { IsCollection: false, Version: { } now } && now != version ? now : null;
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
        // Without itself, the folder it is in; "" for one in the vault's own folder, where nothing is parked above.
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
}
