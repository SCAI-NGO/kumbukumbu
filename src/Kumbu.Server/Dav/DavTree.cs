// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Server.Dav;

/// <summary>
/// One resource as a WebDAV client sees it: the root and the vaults and folders are collections, notes and attachments
/// files (WebDAV design §3.4). <paramref name="Length"/>, <paramref name="ContentType"/>, <paramref name="Version"/> and
/// <paramref name="LastWrite"/> are a file's; a collection has none of them (Kumbu² keeps no time for a folder).
/// </summary>
public sealed record DavResource(
    VaultPath Path, bool IsCollection, long? Length = null, string? ContentType = null, string? Version = null, DateTime? LastWrite = null)
{
    public const string NoteContentType = "text/markdown; charset=utf-8";

    public string Href => DavPaths.Href(Path, IsCollection);

    /// <summary>The live properties a <c>PROPFIND</c> can report, by name, each built when asked for. The quota properties
    /// of RFC 4331 (the root and the vaults the share's user owns) join here (Task 12); <c>lockdiscovery</c> lists the
    /// resource's locks once there are any (Task 7).</summary>
    public IReadOnlyDictionary<XName, Func<XElement>> Properties()
    {
        var d = DavXml.Dav;
        var properties = new Dictionary<XName, Func<XElement>>
        {
            [d + "resourcetype"] = () => new XElement(d + "resourcetype", IsCollection ? new XElement(d + "collection") : null),
        };
        if (!Path.IsRoot) properties[d + "displayname"] = () => new XElement(d + "displayname", Path.Name);
        if (Length is { } length) properties[d + "getcontentlength"] = () => new XElement(d + "getcontentlength", length.ToString(CultureInfo.InvariantCulture));
        if (ContentType is { } type) properties[d + "getcontenttype"] = () => new XElement(d + "getcontenttype", type);
        if (Version is { } version) properties[d + "getetag"] = () => new XElement(d + "getetag", $"\"{version}\"");
        if (LastWrite is { } written)
        {
            // The last write is both: Kumbu² keeps no creation time (WebDAV design §3.4).
            var utc = DateTime.SpecifyKind(written, DateTimeKind.Utc);
            properties[d + "getlastmodified"] = () => new XElement(d + "getlastmodified", utc.ToString("R", CultureInfo.InvariantCulture));
            properties[d + "creationdate"] = () => new XElement(d + "creationdate", utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        properties[d + "supportedlock"] = () => new XElement(d + "supportedlock", LockEntry(d + "exclusive"), LockEntry(d + "shared"));
        properties[d + "lockdiscovery"] = () => new XElement(d + "lockdiscovery");
        return properties;

        XElement LockEntry(XName scope) =>
            new(d + "lockentry", new XElement(d + "lockscope", new XElement(scope)), new XElement(d + "locktype", new XElement(d + "write")));
    }
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
        children.AddRange(ParkedChildren(folder));
        return [.. folders, .. children.OrderBy(c => c.Path.Name, TextOrder.Default)];
    }

    /// <summary>The files the share parked in <paramref name="folder"/> (WebDAV design §3.8), listed for that share only:
    /// none until parking comes (Task 7).</summary>
    private static IEnumerable<DavResource> ParkedChildren(VaultPath folder) => [];

    private async Task<bool> IsFolderAsync(VaultPath path, CancellationToken ct) =>
        (await notes.ListNotesUnderAsync(path, ct)).Count > 0 || (await attachments.ListAttachmentsUnderAsync(path, ct)).Count > 0;

    private static DavResource Note(StoredNote note) =>
        new(note.Path, false, Encoding.UTF8.GetByteCount(note.Content), DavResource.NoteContentType, note.Version, note.LastWriteAt);

    private static DavResource Attachment(StoredAttachment file) =>
        new(file.Path, false, file.Current.Size, file.Current.MediaType, file.Version, file.LastWriteAt);
}
