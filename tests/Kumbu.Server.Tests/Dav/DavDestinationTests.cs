// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Dav;
using Microsoft.AspNetCore.Http;

namespace Kumbu.Server.Tests.Dav;

/// <summary>The <c>Destination</c> of a <c>COPY</c> or <c>MOVE</c> (WebDAV design §3.5, §5): on the request's own scheme,
/// host and port, below <c>/dav/</c>, and a path as <see cref="DavPaths"/> reads one; anything else is none (502).</summary>
public sealed class DavDestinationTests
{
    private static HttpRequest Request(string scheme = "https", string host = "kumbu.example.org")
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = scheme;
        http.Request.Host = new HostString(host);
        http.Request.Path = "/dav/Work/a.md";
        return http.Request;
    }

    [Theory]
    [InlineData("https://kumbu.example.org/dav/Work/b.md", "/Work/b.md")]
    [InlineData("https://KUMBU.example.org/dav/Work/b.md", "/Work/b.md")]
    [InlineData("https://kumbu.example.org:443/dav/Work/b.md", "/Work/b.md")]
    [InlineData("https://kumbu.example.org/dav/Work/Caf%C3%A9%20ideas.md", "/Work/Café ideas.md")]
    [InlineData("https://kumbu.example.org/dav/Work/.git/", "/Work/.git")]
    [InlineData("https://kumbu.example.org/DAV/Work/b.md", "/Work/b.md")]
    [InlineData("https://kumbu.example.org/dav/Work/b.md?x=1", "/Work/b.md")]
    [InlineData("/dav/Work/b.md", "/Work/b.md")]
    public void A_destination_on_the_same_origin_below_dav_is_read(string header, string path)
    {
        var target = DavDestination.Parse(Request(), header);

        Assert.NotNull(target);
        Assert.Equal(path, target.Path.Value);
    }

    [Theory]
    [InlineData("https://evil.example/dav/Work/b.md")]
    [InlineData("http://kumbu.example.org/dav/Work/b.md")]
    [InlineData("https://kumbu.example.org:8443/dav/Work/b.md")]
    [InlineData("https://kumbu.example.org/api/notes/Work/b.md")]
    [InlineData("https://kumbu.example.org/davx/Work/b.md")]
    [InlineData("https://kumbu.example.org/dav/Work/a/../b.md")]
    [InlineData("https://kumbu.example.org/dav/Work/a%2Fb.md")]
    [InlineData("https://kumbu.example.org/dav/Work//b.md")]
    [InlineData("//evil.example/dav/Work/b.md")]
    [InlineData("dav/Work/b.md")]
    [InlineData("")]
    [InlineData("not a url")]
    public void Any_other_destination_is_none(string header) => Assert.Null(DavDestination.Parse(Request(), header));

    [Fact]
    public void The_port_is_the_requests()
    {
        Assert.NotNull(DavDestination.Parse(Request("http", "localhost:5000"), "http://localhost:5000/dav/Work/b.md"));
        Assert.Null(DavDestination.Parse(Request("http", "localhost:5000"), "http://localhost/dav/Work/b.md"));
        Assert.NotNull(DavDestination.Parse(Request("http", "localhost"), "http://localhost:80/dav/Work/b.md"));
    }
}
