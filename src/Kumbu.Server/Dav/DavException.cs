// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Xml.Linq;

namespace Kumbu.Server.Dav;

/// <summary>A WebDAV request answered with <see cref="Status"/> and a plain-text <see cref="Exception.Message"/>, or the
/// XML body <see cref="Body"/> when there is one (an RFC 4918 <c>DAV:error</c>).</summary>
public sealed class DavException(int status, string message, XElement? body = null) : Exception(message)
{
    public int Status { get; } = status;

    public XElement? Body { get; } = body;

    /// <summary>404: nothing the share sees is there.</summary>
    public static DavException NotFound() => new(StatusCodes.Status404NotFound, "Not found.");

    /// <summary>405: a folder or a file is where one would be made.</summary>
    public static DavException Exists() => new(StatusCodes.Status405MethodNotAllowed, "Something exists there already.");

    /// <summary>405: content written at a folder.</summary>
    public static DavException FolderHasNoContent() => new(StatusCodes.Status405MethodNotAllowed, "A folder has no content to write.");
}
