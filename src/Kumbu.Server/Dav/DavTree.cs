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
/// (<see cref="FolderMarker"/>). What the share parked (WebDAV design §3.8) joins in, for this share only; where the
/// store has something at the same path, the store's is what the share sees, and writes there go to the store. A note
/// the share holds (<see cref="DavHolds"/>, §3.6 case 4) is, for this share only, under its held name and not at its
/// path; the store and everyone else still have it where it is.
/// </summary>
public sealed class DavTree(INoteStore notes, IAttachmentStore attachments, DavCaller caller, DavParking parking, DavHolds holds)
{
    /// <summary>
    /// The path a client's <paramref name="path"/> names (brief open point 6): a name ending in <c>.md</c> in another case
    /// (<c>Plan.MD</c>) is the note ending in <c>.md</c>, the one spelling the store keeps notes under, unless the
    /// share sees something under the exact name (a folder, a parked file). The one place this is decided: every method
    /// resolves its target here, and <c>COPY</c> and <c>MOVE</c> their destination too.
    /// </summary>
    public async Task<VaultPath> ResolveAsync(VaultPath path, CancellationToken ct)
    {
        if (path.Segments.Count < 2 || path.IsNote || !path.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return path;
        if (await FindAsync(path, ct) is not null) return path;
        return path.Segments.SkipLast(1).Aggregate(VaultPath.Root, (p, s) => p.Child(s)).Child(path.Name[..^3] + ".md");
    }

    /// <summary>The resource at <paramref name="path"/> as the share sees it, or null if there is none.</summary>
    public async Task<DavResource?> FindAsync(VaultPath path, CancellationToken ct)
    {
        if (Hidden(path)) return null;
        return await FindStoredAsync(path, ct) ?? (Parked(path) is { } item ? Parked(path, item) : null) ?? await HeldAsync(path, ct);
    }

    /// <summary>The resource the store has at <paramref name="path"/> (the root and the share's vaults included), or null
    /// if it has none.</summary>
    public async Task<DavResource?> FindStoredAsync(VaultPath path, CancellationToken ct)
    {
        if (path.IsRoot) return new DavResource(path, IsCollection: true);
        if (path.Segments.Count == 1) return (await notes.ListVaultsAsync(ct)).Contains(path.Segments[0], StringComparer.Ordinal) ? new DavResource(path, true) : null;
        if (FolderMarker.Is(path)) return null;
        if (path.IsNote && await notes.GetAsync(path, ct) is { } note) return Note(note);
        if (path.IsAttachment && await attachments.GetAttachmentAsync(path, ct) is { } attachment) return Attachment(attachment);
        return await IsFolderAsync(path, ct) ? new DavResource(path, true) : null;
    }

    /// <summary>The content of the note, attachment or parked file at <paramref name="path"/>, or null if there is none
    /// there (a collection, a folder marker, nothing).</summary>
    public async Task<DavContent?> ContentAsync(VaultPath path, CancellationToken ct)
    {
        if (path.Segments.Count < 2 || Hidden(path)) return null;
        if (await StoredContentAsync(path, ct) is { } stored) return stored;

        // A file the share parked, unless the store has a folder there, which is then what the share sees.
        if (Parked(path) is { } item) return !item.IsFolder && !await IsFolderAsync(path, ct) ? DavContent.Parked(item) : null;
        return Held(path) is { } held && await StoredContentAsync(held.Original, ct) is { } content ? content with { Name = path.Name } : null;
    }

    private async Task<DavContent?> StoredContentAsync(VaultPath path, CancellationToken ct)
    {
        if (FolderMarker.Is(path)) return null;
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
        // What the share holds in this vault under other names, asked once for the listing.
        var hidden = caller.Place(folder) is { } at ? holds.Hidden(caller.ShareId, at.VaultId) : new HashSet<string>();
        bool Hides(VaultPath p) => hidden.Contains(string.Join('/', p.Segments.Skip(1)));
        var notePaths = await notes.ListNotesUnderAsync(folder, ct);
        var files = await attachments.ListAttachmentsUnderAsync(folder, ct);

        var folders = notePaths.Concat(files.Select(a => a.Path))
            .Where(p => p.Segments.Count > depth + 1)
            .Select(p => p.Segments[depth])
            .Distinct(StringComparer.Ordinal)
            .Select(name => new DavResource(folder.Child(name), true))
            .ToList();

        var children = new List<DavResource>();
        foreach (var path in notePaths.Where(p => p.Segments.Count == depth + 1 && !Hides(p)))
        {
            if (await notes.GetAsync(path, ct) is { } note) children.Add(Note(note));
        }

        // A marker keeps its folder in the listing above (it is below it), but is never one of a folder's files.
        children.AddRange(files.Where(a => a.Path.Segments.Count == depth + 1 && !FolderMarker.Is(a.Path) && !Hides(a.Path)).Select(Attachment));

        // What this share parked in the folder, under a name the store doesn't have there.
        if (caller.Place(folder) is { } place)
        {
            var taken = folders.Select(f => f.Path.Name).Concat(children.Select(c => c.Path.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var item in parking.List(caller.ShareId, place.VaultId, place.Path).Where(i => !taken.Contains(i.Name)))
            {
                (item.IsFolder ? folders : children).Add(Parked(folder.Child(item.Name), item));
                taken.Add(item.Name);
            }

            // And what it holds there, under the held name.
            foreach (var held in holds.List(caller.ShareId, place.VaultId, place.Path))
            {
                var path = folder.Child(held.HeldName[(held.HeldName.LastIndexOf('/') + 1)..]);
                if (!taken.Contains(path.Name) && await HeldAsync(path, ct) is { } resource) children.Add(resource);
            }
        }

        return [.. folders.OrderBy(f => f.Path.Name, TextOrder.Default), .. children.OrderBy(c => c.Path.Name, TextOrder.Default)];
    }

    /// <summary>What the share parked at <paramref name="path"/>, or null; nothing at the root or a vault's own folder.</summary>
    public ParkedItem? Parked(VaultPath path) =>
        path.Segments.Count >= 2 && caller.Place(path) is { } place ? parking.Get(caller.ShareId, place.VaultId, place.Path) : null;

    /// <summary>What the share holds under the name <paramref name="path"/>, or null.</summary>
    internal DavHeld? Held(VaultPath path) =>
        path.Segments.Count >= 2 && caller.Place(path) is { } place ? holds.Held(caller.ShareId, place.VaultId, place.Path) : null;

    /// <summary>Whether <paramref name="path"/> is a file the share holds under another name, which it doesn't see here.</summary>
    private bool Hidden(VaultPath path) =>
        path.Segments.Count >= 2 && caller.Place(path) is { } place && holds.Hidden(caller.ShareId, place.VaultId).Contains(place.Path);

    /// <summary>The file the share holds under the name <paramref name="path"/>, as a resource of that name.</summary>
    private async Task<DavResource?> HeldAsync(VaultPath path, CancellationToken ct) =>
        Held(path) is { } held && await FindStoredAsync(held.Original, ct) is { IsCollection: false } file ? file with { Path = path } : null;

    private static DavResource Parked(VaultPath path, ParkedItem item) =>
        item.IsFolder ? new DavResource(path, true) : new DavResource(path, false, item.Bytes!.LongLength, DavContent.ParkedContentType, item.Version, item.Modified);

    private async Task<bool> IsFolderAsync(VaultPath path, CancellationToken ct) =>
        (await notes.ListNotesUnderAsync(path, ct)).Count > 0 || (await attachments.ListAttachmentsUnderAsync(path, ct)).Count > 0;

    private static DavResource Note(StoredNote note) =>
        new(note.Path, false, Encoding.UTF8.GetByteCount(note.Content), DavResource.NoteContentType, note.Version, note.LastWriteAt);

    private static DavResource Attachment(StoredAttachment file) =>
        new(file.Path, false, file.Current.Size, file.Current.MediaType, file.Version, file.LastWriteAt);
}
