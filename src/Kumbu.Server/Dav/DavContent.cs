// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Server.Api;

namespace Kumbu.Server.Dav;

/// <summary>
/// What a <c>GET</c> under <c>/dav</c> serves, for <see cref="DavEndpoints.ServeAsync"/>: the name the
/// <c>Content-Disposition</c> carries and whether it is <c>attachment</c> (<paramref name="Download"/>) rather than
/// <c>inline</c>, the type the server decided (never one a client sent), the length, the version the <c>ETag</c> is made
/// of, the last write, and how to open the bytes, which only a <c>GET</c> that is answered with them does.
/// </summary>
public sealed record DavContent(
    string Name, string ContentType, bool Download, long Length, string Version, DateTime LastWrite, Func<CancellationToken, Task<Stream>> Open)
{
    /// <summary>What a parked file is served as, whatever its name: bytes, never a type a browser would render.</summary>
    public const string ParkedContentType = "application/octet-stream";

    /// <summary>A note's text, UTF-8, as the REST API gives it.</summary>
    public static DavContent Note(StoredNote note)
    {
        var bytes = Encoding.UTF8.GetBytes(note.Content);
        return new(note.Path.Name, DavResource.NoteContentType, false, bytes.Length, note.Version, note.LastWriteAt,
            _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)));
    }

    /// <summary>An attachment's current bytes, typed by its checked format as <c>GET /api/files</c> types them.</summary>
    public static DavContent Attachment(IAttachmentStore files, StoredAttachment attachment) =>
        new(attachment.Path.Name, attachment.Current.MediaType, false, attachment.Current.Size, attachment.Version, attachment.LastWriteAt,
            ct => FileApi.OpenAsync(files, attachment.Path, attachment.Current, ct));

    /// <summary>
    /// A parked file's bytes (WebDAV design §3.8, brief open point 1): they can be anything under any name, so they go
    /// out as <see cref="ParkedContentType"/> and as an attachment, which a browser saves rather than opens, and with
    /// <see cref="Security.FileContentPolicy"/> like every answer under <c>/dav</c>.
    /// </summary>
    public static DavContent Parked(ParkedItem file) =>
        new(file.Name, ParkedContentType, true, file.Bytes!.LongLength, file.Version, file.Modified,
            _ => Task.FromResult<Stream>(new MemoryStream(file.Bytes, writable: false)));
}
