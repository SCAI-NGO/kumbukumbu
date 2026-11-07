// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Dav;

/// <summary>
/// What every <c>OPTIONS</c> on <c>/</c>, <c>/dav</c> and below says (WebDAV design §3.7): class 1 and 2 (locking is
/// advertised, and faked), and <c>MS-Author-Via</c>, which Windows and Office look for. Static, the same on every path,
/// so that it tells nothing of which vaults exist.
/// </summary>
public static class DavHeaders
{
    /// <summary>The methods <c>/dav</c> answers.</summary>
    public const string DavMethods = "OPTIONS, PROPFIND, PROPPATCH, GET, HEAD, PUT, DELETE, MKCOL, COPY, MOVE, LOCK, UNLOCK";

    /// <summary>The methods <c>/</c> answers: the web app, and a client's probe.</summary>
    public const string RootMethods = "OPTIONS, GET, HEAD";

    /// <summary>A body-less 200 with the DAV headers and <paramref name="allow"/>.</summary>
    public static void Options(HttpResponse response, string allow)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.Headers["DAV"] = "1, 2";
        response.Headers["MS-Author-Via"] = "DAV";
        response.Headers.Allow = allow;
        response.ContentLength = 0;
    }
}
