// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Xml.Linq;
using Kumbu.Server.Api;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Dav;

/// <summary><c>GET</c>, <c>HEAD</c> and <c>PROPPATCH</c> under <c>/dav</c> (WebDAV design §3.4, brief open point 1): a
/// note's text and an attachment's bytes, served as the app serves an attachment, so nothing runs at its origin.</summary>
[Collection("app")]
public sealed class DavReadTests(KumbuAppFactory app)
{
    private static readonly XNamespace D = "DAV:";

    private static readonly XNamespace Ms = "urn:schemas-microsoft-com:";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static string Header(HttpResponseMessage response, string name) => DavAssert.Header(response, name);

    [Fact]
    public async Task Get_of_a_note_returns_its_text_and_etag()
    {
        var dav = await DavClient.NewAsync(Api);
        var version = await dav.Owner.CreateAsync("Ideas/Café.md", "# Café\n\nÜber [[Routing]].\n");
        var text = (await dav.Owner.ReadAsync("Ideas/Café.md"))["content"]!.GetValue<string>();

        using var response = await dav.SendAsync(HttpMethod.Get, "/dav/Work/Ideas/Caf%C3%A9.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(text, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal($"\"{version}\"", response.Headers.ETag?.Tag);
        Assert.NotNull(response.Content.Headers.LastModified);
        DavAssert.PrivateWithoutCookie(response);
    }

    [Fact]
    public async Task Get_of_a_note_goes_through_the_file_content_policy()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "<script>alert(1)</script>");

        using var response = await dav.SendAsync(HttpMethod.Get, "/dav/Work/plan.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown; charset=utf-8", Header(response, "Content-Type"));
        Assert.Equal(Security.FileContentPolicy, Header(response, "Content-Security-Policy"));
        Assert.Equal(FileApi.Disposition("plan.md", download: false), Header(response, "Content-Disposition"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task Get_of_an_attachment_has_the_same_headers_as_the_file_api()
    {
        var dav = await DavClient.NewAsync(Api);
        var bytes = FileApiTests.Png(4, 3);
        var version = await dav.Owner.CreateFileAsync("shots/Bild 1.png", bytes);

        using var api = await dav.Owner.GetAsync(dav.Owner.Url("shots/Bild 1.png", "files"));
        using var response = await dav.SendAsync(HttpMethod.Get, "/dav/Work/shots/Bild%201.png");

        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(Ct));
        foreach (var name in new[] { "Content-Type", "Content-Disposition", "Content-Security-Policy", "X-Content-Type-Options", "ETag" })
        {
            Assert.Equal(Header(api, name), Header(response, name));
        }

        Assert.Equal($"\"{version}\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Head_has_no_body_but_the_length()
    {
        var dav = await DavClient.NewAsync(Api);
        const string text = "# Plan — öffnen\n";
        await dav.Owner.CreateAsync("plan.md", text);
        var bytes = FileApiTests.Png(2, 2);
        await dav.Owner.CreateFileAsync("diagram.png", bytes);

        using var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/plan.md");
        using var head = await dav.SendAsync(HttpMethod.Head, "/dav/Work/plan.md");

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(Ct));
        foreach (var name in new[] { "Content-Type", "Content-Disposition", "Content-Security-Policy", "ETag", "Last-Modified" })
        {
            Assert.Equal(Header(get, name), Header(head, name));
        }

        using var file = await dav.SendAsync(HttpMethod.Head, "/dav/Work/diagram.png");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(bytes.Length, file.Content.Headers.ContentLength);
        Assert.Empty(await file.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("image/png", file.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task If_none_match_gives_304()
    {
        var dav = await DavClient.NewAsync(Api);
        var version = await dav.Owner.CreateAsync("plan.md", "# Plan");
        var fileVersion = await dav.Owner.CreateFileAsync("diagram.png", FileApiTests.Png(2, 2));

        foreach (var (path, tag) in new[] { ("/dav/Work/plan.md", version), ("/dav/Work/diagram.png", fileVersion) })
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
            {
                foreach (var match in new[] { $"\"{tag}\"", $"W/\"{tag}\"", $"\"other\", \"{tag}\"", "*" })
                {
                    using var same = await dav.SendAsync(method, path, headers: new Dictionary<string, string> { ["If-None-Match"] = match });
                    Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);
                    Assert.Empty(await same.Content.ReadAsByteArrayAsync(Ct));
                    Assert.Equal($"\"{tag}\"", same.Headers.ETag?.Tag);
                }
            }

            using var other = await dav.SendAsync(HttpMethod.Get, path, headers: new Dictionary<string, string> { ["If-None-Match"] = "\"other\"" });
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }
    }

    [Fact]
    public async Task Get_of_a_folder_is_405()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/Routing.md", "Routing");

        foreach (var path in new[] { "/dav/", "/dav/Work/", "/dav/Work", "/dav/Work/Ideas/", "/dav/Work/Ideas" })
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
            {
                using var response = await dav.SendAsync(method, path);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
                var allow = Header(response, "Allow");
                Assert.Contains("PROPFIND", allow, StringComparison.Ordinal);
                Assert.DoesNotContain("GET", allow, StringComparison.Ordinal);
            }
        }

        foreach (var path in new[] { "/dav/Work/nothing.md", "/dav/Work/nothing.png", "/dav/Nowhere/plan.md" })
        {
            using var missing = await dav.SendAsync(HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    private const string Update =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<D:propertyupdate xmlns:D=\"DAV:\" xmlns:Z=\"urn:schemas-microsoft-com:\">" +
        "<D:set><D:prop><Z:Win32LastModifiedTime>Sat, 07 Nov 2026 10:00:00 GMT</Z:Win32LastModifiedTime>" +
        "<Z:Win32FileAttributes>00000020</Z:Win32FileAttributes></D:prop></D:set>" +
        "<D:remove><D:prop><D:displayname/></D:prop></D:remove>" +
        "</D:propertyupdate>";

    [Fact]
    public async Task Proppatch_answers_207_and_stores_nothing()
    {
        var dav = await DavClient.NewAsync(Api);
        var version = await dav.Owner.CreateAsync("plan.md", "# Plan");

        foreach (var href in new[] { "/dav/Work/plan.md", "/dav/Work/" })
        {
            var patched = await DavAssert.MultistatusAsync(await dav.SendAsync(DavClient.Proppatch, href, DavClient.Body(Update)));
            var response = DavAssert.Response(patched, href);
            var ok = DavAssert.Ok(response);
            Assert.NotNull(ok);
            Assert.Equal([Ms + "Win32LastModifiedTime", Ms + "Win32FileAttributes", D + "displayname"], ok.Elements().Select(e => e.Name));
            Assert.All(ok.Elements(), e => Assert.Empty(e.Nodes()));
            Assert.Single(response.Elements(D + "propstat"));
        }

        var found = await DavAssert.MultistatusAsync(await dav.PropfindAsync(
            "/dav/Work/plan.md", depth: "0",
            body: DavClient.Body("<D:propfind xmlns:D=\"DAV:\" xmlns:Z=\"urn:schemas-microsoft-com:\"><D:prop><Z:Win32LastModifiedTime/><D:displayname/></D:prop></D:propfind>")));
        var plan = DavAssert.Response(found, "/dav/Work/plan.md");
        Assert.Null(DavAssert.Ok(plan)?.Element(Ms + "Win32LastModifiedTime"));
        Assert.Equal("plan.md", DavAssert.Prop(plan, "displayname")?.Value);
        Assert.Equal(version, (await dav.Owner.ReadAsync("plan.md"))["version"]!.GetValue<string>());

        using var missing = await dav.SendAsync(DavClient.Proppatch, "/dav/Work/nothing.md", DavClient.Body(Update));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        foreach (var body in new[] { "", "<D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>" })
        {
            using var bad = await dav.SendAsync(DavClient.Proppatch, "/dav/Work/plan.md", DavClient.Body(body));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
    }

    [Fact]
    public async Task A_read_share_can_read()
    {
        var dav = await DavClient.NewAsync(Api, right: "read");
        await dav.Owner.CreateAsync("plan.md", "# Plan");
        var bytes = FileApiTests.Png(2, 2);
        await dav.Owner.CreateFileAsync("diagram.png", bytes);

        using var note = await dav.SendAsync(HttpMethod.Get, "/dav/Work/plan.md");
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        Assert.Equal("# Plan", await note.Content.ReadAsStringAsync(Ct));
        using var file = await dav.SendAsync(HttpMethod.Get, "/dav/Work/diagram.png");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(bytes, await file.Content.ReadAsByteArrayAsync(Ct));

        // It stores nothing, so a read share may ask: Windows sets times after every copy it reads.
        var patched = await DavAssert.MultistatusAsync(await dav.SendAsync(DavClient.Proppatch, "/dav/Work/plan.md", DavClient.Body(Update)));
        Assert.NotNull(DavAssert.Ok(DavAssert.Response(patched, "/dav/Work/plan.md")));
    }
}
