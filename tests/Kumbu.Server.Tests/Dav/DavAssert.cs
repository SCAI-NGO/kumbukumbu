// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;

namespace Kumbu.Server.Tests.Dav;

/// <summary>What the WebDAV tests check of every <c>/dav</c> answer, in one place.</summary>
public static class DavAssert
{
    /// <summary>The challenge <c>/dav</c> answers without a share's credentials (WebDAV design §3.3), as the spec fixes it.</summary>
    public const string Challenge = "Basic realm=\"Kumbu²\", charset=\"UTF-8\"";

    /// <summary>A header of the response or its content, its values joined; empty if it has none.</summary>
    public static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values) ? string.Join(", ", values) : "";

    /// <summary>Refused for its credentials: 401 with the Basic challenge.</summary>
    public static void Challenged(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Challenge, Header(response, "WWW-Authenticate"));
    }

    /// <summary>Kept out of shared caches, and without a cookie (WebDAV design §5).</summary>
    public static void PrivateWithoutCookie(HttpResponseMessage response)
    {
        Assert.Equal("private", Header(response, "Cache-Control"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    /// <summary>The share's credentials got the request through to the WebDAV endpoints: for now their placeholder's 501.</summary>
    public static void Through(HttpResponseMessage response) => Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
}
