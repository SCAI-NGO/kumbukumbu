// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Dav;

/// <summary>
/// The <c>Destination</c> header of a <c>COPY</c> or <c>MOVE</c> (RFC 4918 §10.3, WebDAV design §3.5 and §5): an absolute
/// URL on the request's own scheme, host and port (as the forwarded headers left them), or an absolute path, which
/// names the same origin; its path below <c>/dav/</c> as <see cref="DavPaths"/> reads one, from the header as sent, so
/// that no URL parser resolves a dot segment or decodes a slash on the way. Anything else is none, and the server won't
/// go there (502).
/// </summary>
public static class DavDestination
{
    public static DavTarget? Parse(HttpRequest request, string header)
    {
        string path;
        if (header.StartsWith('/'))
        {
            // "//host/…" is a URL without its scheme, not a path.
            if (header.StartsWith("//", StringComparison.Ordinal)) return null;
            path = header;
        }
        else
        {
            if (!Uri.TryCreate(header, UriKind.Absolute, out var uri) || !SameOrigin(request, uri)) return null;
            var authority = header.IndexOf("://", StringComparison.Ordinal) + 3;
            var slash = header.IndexOf('/', authority);
            if (slash < 0) return null;
            path = header[slash..];
        }

        try
        {
            return DavPaths.Parse(path.Split('?', '#')[0]);
        }
        catch (DavException)
        {
            return null;
        }
    }

    /// <summary>Whether the URL is on the request's origin. Only a client's own mistakes are caught here: what keeps a
    /// copy or move inside what the share may touch is the caller's check that the destination is in the source's vault
    /// (by id), as every path is resolved through the share's own vaults.</summary>
    private static bool SameOrigin(HttpRequest request, Uri uri) =>
        uri.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == (request.Host.Port ?? (request.IsHttps ? 443 : 80))
        && uri.UserInfo.Length == 0;
}
