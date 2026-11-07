// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Xml.Linq;

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

    /// <summary>The share's credentials got the request through to the WebDAV endpoints: whatever they answered, it isn't
    /// a refusal of the credentials (401), of the connection (403 over plain http) or of the limiter (429), nor a fault (5xx).</summary>
    public static void Through(HttpResponseMessage response)
    {
        Assert.DoesNotContain(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests });
        Assert.True((int)response.StatusCode < 500, $"A fault: {(int)response.StatusCode}");
    }

    private static readonly XNamespace D = "DAV:";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A 207 with an XML body, its <c>DAV:multistatus</c>; disposes of the response.</summary>
    public static async Task<XElement> MultistatusAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
            Assert.StartsWith("application/xml", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
            var root = XDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).Root!;
            Assert.Equal(D + "multistatus", root.Name);
            return root;
        }
    }

    /// <summary>The hrefs of a multistatus, in its order.</summary>
    public static List<string> Hrefs(XElement multistatus) =>
        [.. multistatus.Elements(D + "response").Select(r => r.Element(D + "href")!.Value)];

    /// <summary>The one response of a multistatus for <paramref name="href"/>.</summary>
    public static XElement Response(XElement multistatus, string href) =>
        Assert.Single(multistatus.Elements(D + "response"), r => r.Element(D + "href")!.Value == href);

    /// <summary>The <c>prop</c> of the response's 200 propstat.</summary>
    public static XElement? Ok(XElement response) =>
        response.Elements(D + "propstat").FirstOrDefault(p => p.Element(D + "status")?.Value == "HTTP/1.1 200 OK")?.Element(D + "prop");

    /// <summary>A DAV: property of the response's 200 propstat.</summary>
    public static XElement? Prop(XElement response, string name) => Ok(response)?.Element(D + name);

    public static bool IsCollection(XElement response) => Prop(response, "resourcetype")?.Element(D + "collection") is not null;
}
