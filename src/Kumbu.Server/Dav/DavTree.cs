// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Server.Dav;

/// <summary>
/// One resource as a WebDAV client sees it: the root and the vaults and folders are collections, notes and attachments
/// files (WebDAV design §3.4); <see cref="DavProperties"/> makes its properties of it. <paramref name="Length"/>, <paramref name="ContentType"/>, <paramref name="Version"/> and
/// <paramref name="LastWrite"/> are a file's; a collection has none of them (Kumbu² keeps no time for a folder).
/// </summary>
public sealed record DavResource(
    VaultPath Path, bool IsCollection, long? Length = null, string? ContentType = null, string? Version = null, DateTime? LastWrite = null)
{
    public const string NoteContentType = "text/markdown; charset=utf-8";

    public string Href => DavPaths.Href(Path, IsCollection);
}

/// <summary>
/// The tree a share sees, read through the request's store, which opens only the share's vaults and names each by the
/// caller's name for it (a member's alias): the vaults at the root, below each its notes, attachments and folders. A
/// folder exists while anything is below it, a folder marker included; markers themselves are never listed
/// (<see cref="FolderMarker"/>).
/// </summary>
public sealed class DavTree(INoteStore notes, IAttachmentStore attachments)
{
    /// <summary>The resource at <paramref name="path"/>, or null if there is none.</summary>
    public async Task<DavResource?> FindAsync(VaultPath path, CancellationToken ct)
    {
        if (path.IsRoot) return new DavResource(path, IsCollection: true);
        if (path.Segments.Count == 1) return (await notes.ListVaultsAsync(ct)).Contains(path.Segments[0], StringComparer.Ordinal) ? new DavResource(path, true) : null;
        if (FolderMarker.Is(path)) return null;
        if (path.IsNote && await notes.GetAsync(path, ct) is { } note) return Note(note);
        if (path.IsAttachment && await attachments.GetAttachmentAsync(path, ct) is { } attachment) return Attachment(attachment);
        return await IsFolderAsync(path, ct) ? new DavResource(path, true) : null;
    }

    /// <summary>The content of the note or attachment at <paramref name="path"/>, or null if there is none there (a
    /// collection, a folder marker, nothing).</summary>
    public async Task<DavContent?> ContentAsync(VaultPath path, CancellationToken ct)
    {
        if (path.Segments.Count < 2 || FolderMarker.Is(path)) return null;
        if (path.IsNote && await notes.GetAsync(path, ct) is { } note) return DavContent.Note(note);
        if (path.IsAttachment && await attachments.GetAttachmentAsync(path, ct) is { } attachment) return DavContent.Attachment(attachments, attachment);
        return null;
    }

    /// <summary>What <paramref name="collection"/> holds, folders first, each group in the app's text order.</summary>
    public async Task<IReadOnlyList<DavResource>> ChildrenAsync(DavResource collection, CancellationToken ct)
    {
        if (collection.Path.IsRoot)
        {
            return [.. (await notes.ListVaultsAsync(ct)).Order(TextOrder.Default).Select(v => new DavResource(VaultPath.Root.Child(v), true))];
        }

        var folder = collection.Path;
        var depth = folder.Segments.Count;
        var notePaths = await notes.ListNotesUnderAsync(folder, ct);
        var files = await attachments.ListAttachmentsUnderAsync(folder, ct);

        var folders = notePaths.Concat(files.Select(a => a.Path))
            .Where(p => p.Segments.Count > depth + 1)
            .Select(p => p.Segments[depth])
            .Distinct(StringComparer.Ordinal)
            .Order(TextOrder.Default)
            .Select(name => new DavResource(folder.Child(name), true));

        var children = new List<DavResource>();
        foreach (var path in notePaths.Where(p => p.Segments.Count == depth + 1))
        {
            if (await notes.GetAsync(path, ct) is { } note) children.Add(Note(note));
        }

        // A marker keeps its folder in the listing above (it is below it), but is never one of a folder's files.
        children.AddRange(files.Where(a => a.Path.Segments.Count == depth + 1 && !FolderMarker.Is(a.Path)).Select(Attachment));
        // The files this share parked in the folder join here, for this share only (WebDAV design §3.8, Task 7).
        return [.. folders, .. children.OrderBy(c => c.Path.Name, TextOrder.Default)];
    }

    private async Task<bool> IsFolderAsync(VaultPath path, CancellationToken ct) =>
        (await notes.ListNotesUnderAsync(path, ct)).Count > 0 || (await attachments.ListAttachmentsUnderAsync(path, ct)).Count > 0;

    private static DavResource Note(StoredNote note) =>
        new(note.Path, false, Encoding.UTF8.GetByteCount(note.Content), DavResource.NoteContentType, note.Version, note.LastWriteAt);

    private static DavResource Attachment(StoredAttachment file) =>
        new(file.Path, false, file.Current.Size, file.Current.MediaType, file.Version, file.LastWriteAt);
}
