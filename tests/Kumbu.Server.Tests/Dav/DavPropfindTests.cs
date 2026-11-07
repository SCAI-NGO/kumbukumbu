// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Dav;

/// <summary><c>PROPFIND</c> over a share's vaults (WebDAV design §3.4, §5): what a client sees, the depths it may ask for,
/// the XML bodies it may send and the paths it may name.</summary>
[Collection("app")]
public sealed class DavPropfindTests(KumbuAppFactory app)
{
    private static readonly XNamespace D = "DAV:";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    [Fact]
    public async Task Root_lists_the_shares_vaults_only()
    {
        var owner = await RestClient.NewAsync(Api, vault: "Work");
        using (var personal = await owner.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Personal" })) Assert.Equal(HttpStatusCode.Created, personal.StatusCode);
        var dav = await DavClient.NewAsync(Api, owner);

        var listing = await MultistatusAsync(await dav.PropfindAsync("/dav/", depth: "1"));

        Assert.Equal(["/dav/", "/dav/Work/"], Hrefs(listing));
        Assert.True(IsCollection(Response(listing, "/dav/Work/")));
        Assert.Equal("Work", Prop(Response(listing, "/dav/Work/"), "displayname")?.Value);

        var root = await MultistatusAsync(await dav.PropfindAsync("/dav/", depth: "0"));
        Assert.Equal(["/dav/"], Hrefs(root));
        Assert.True(IsCollection(Response(root, "/dav/")));

        using var other = await dav.PropfindAsync("/dav/Personal/", depth: "0");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task A_member_sees_the_vault_under_her_alias()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "garten");
        await alice.CreateAsync("Beete.md", "Tomaten");
        var dav = await DavClient.NewAsync(Api, bob, vaults: new Dictionary<string, string> { ["garten"] = "write" });

        var root = await MultistatusAsync(await dav.PropfindAsync("/dav/", depth: "1"));
        Assert.Equal(["/dav/", "/dav/garten/"], Hrefs(root));
        Assert.Equal("garten", Prop(Response(root, "/dav/garten/"), "displayname")?.Value);

        var vault = await MultistatusAsync(await dav.PropfindAsync("/dav/garten/", depth: "1"));
        Assert.Equal(["/dav/garten/", "/dav/garten/Beete.md"], Hrefs(vault));

        using var byOwnersName = await dav.PropfindAsync("/dav/Gartenarbeit/", depth: "0");
        Assert.Equal(HttpStatusCode.NotFound, byOwnersName.StatusCode);
    }

    [Fact]
    public async Task A_vault_lists_notes_attachments_and_folders_but_no_folder_markers()
    {
        var dav = await DavClient.NewAsync(Api);
        var owner = dav.Owner;
        await owner.CreateAsync("plan.md", "# Plan");
        await owner.CreateAsync("Ideas/Routing.md", "[[Questions]]");
        await owner.CreateFileAsync("diagram.png", FileApiTests.Png(2, 2));
        await owner.CreateFileAsync("Ideas/.vaultkeep", []);
        await owner.CreateFileAsync("Drafts/.vaultkeep", []);

        var vault = await MultistatusAsync(await dav.PropfindAsync("/dav/Work/", depth: "1"));

        // Folders first, then files.
        Assert.Equal(["/dav/Work/", "/dav/Work/Drafts/", "/dav/Work/Ideas/", "/dav/Work/diagram.png", "/dav/Work/plan.md"], Hrefs(vault));
        Assert.True(IsCollection(Response(vault, "/dav/Work/")));
        Assert.True(IsCollection(Response(vault, "/dav/Work/Ideas/")));
        Assert.True(IsCollection(Response(vault, "/dav/Work/Drafts/")));
        Assert.False(IsCollection(Response(vault, "/dav/Work/plan.md")));
        Assert.Equal("image/png", Prop(Response(vault, "/dav/Work/diagram.png"), "getcontenttype")?.Value);

        var ideas = await MultistatusAsync(await dav.PropfindAsync("/dav/Work/Ideas/", depth: "1"));
        Assert.Equal(["/dav/Work/Ideas/", "/dav/Work/Ideas/Routing.md"], Hrefs(ideas));

        var drafts = await MultistatusAsync(await dav.PropfindAsync("/dav/Work/Drafts/", depth: "1"));
        Assert.Equal(["/dav/Work/Drafts/"], Hrefs(drafts));

        using var marker = await dav.PropfindAsync("/dav/Work/Drafts/.vaultkeep", depth: "0");
        Assert.Equal(HttpStatusCode.NotFound, marker.StatusCode);
        using var missing = await dav.PropfindAsync("/dav/Work/Nowhere/", depth: "0");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Depth_infinity_and_a_missing_depth_get_403_with_propfind_finite_depth()
    {
        var dav = await DavClient.NewAsync(Api);

        foreach (var depth in new[] { "infinity", "Infinity", null })
        {
            using var response = await dav.PropfindAsync("/dav/Work/", depth: depth);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var error = XDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).Root!;
            Assert.Equal(D + "error", error.Name);
            Assert.NotNull(error.Element(D + "propfind-finite-depth"));
        }

        using var nonsense = await dav.PropfindAsync("/dav/Work/", depth: "2");
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
    }

    [Fact]
    public async Task Properties_of_a_note()
    {
        var dav = await DavClient.NewAsync(Api);
        const string content = "Grüße aus Zürich ²";
        var version = await dav.Owner.CreateAsync("Ideas/plan.md", content);
        var rest = await dav.Owner.ReadAsync("Ideas/plan.md");

        var listing = await MultistatusAsync(await dav.PropfindAsync("/dav/Work/Ideas/plan.md", depth: "0"));

        var note = Response(listing, "/dav/Work/Ideas/plan.md");
        Assert.Equal($"\"{version}\"", Prop(note, "getetag")?.Value);
        Assert.Equal($"\"{rest["version"]!.GetValue<string>()}\"", Prop(note, "getetag")?.Value);
        Assert.Equal(Encoding.UTF8.GetByteCount(content).ToString(CultureInfo.InvariantCulture), Prop(note, "getcontentlength")?.Value);
        Assert.NotEqual(content.Length, Encoding.UTF8.GetByteCount(content));
        Assert.Equal("text/markdown; charset=utf-8", Prop(note, "getcontenttype")?.Value);
        Assert.Equal("plan.md", Prop(note, "displayname")?.Value);
        Assert.False(IsCollection(note));
        var written = rest["lastWriteAt"]!.GetValue<DateTime>().ToUniversalTime();
        Assert.Equal(written.ToString("R", CultureInfo.InvariantCulture), Prop(note, "getlastmodified")?.Value);
        Assert.Equal(written.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), Prop(note, "creationdate")?.Value);
        var entries = Prop(note, "supportedlock")!.Elements(D + "lockentry").ToList();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Element(D + "lockscope")?.Element(D + "exclusive") is not null && e.Element(D + "locktype")?.Element(D + "write") is not null);
        Assert.Contains(entries, e => e.Element(D + "lockscope")?.Element(D + "shared") is not null && e.Element(D + "locktype")?.Element(D + "write") is not null);
        Assert.NotNull(Prop(note, "lockdiscovery"));

        // The same as asked for by name, and as names only.
        var named = await MultistatusAsync(await dav.PropfindAsync(
            "/dav/Work/Ideas/plan.md", depth: "0",
            body: DavClient.Body("<D:propfind xmlns:D=\"DAV:\"><D:prop><D:getetag/><D:getcontentlength/></D:prop></D:propfind>")));
        var props = Ok(Response(named, "/dav/Work/Ideas/plan.md"))!;
        Assert.Equal([D + "getetag", D + "getcontentlength"], props.Elements().Select(e => e.Name));
        Assert.Equal($"\"{version}\"", props.Element(D + "getetag")!.Value);

        var names = await MultistatusAsync(await dav.PropfindAsync(
            "/dav/Work/Ideas/plan.md", depth: "0", body: DavClient.Body("<propfind xmlns=\"DAV:\"><propname/></propfind>")));
        var empty = Ok(Response(names, "/dav/Work/Ideas/plan.md"))!;
        Assert.Contains(empty.Elements(), e => e.Name == D + "getetag");
        Assert.All(empty.Elements(), e => Assert.True(e.IsEmpty));
    }

    [Fact]
    public async Task Properties_of_an_attachment()
    {
        var dav = await DavClient.NewAsync(Api);
        var png = FileApiTests.Png(3, 3);
        var version = await dav.Owner.CreateFileAsync("diagram.png", png);

        var listing = await MultistatusAsync(await dav.PropfindAsync("/dav/Work/diagram.png", depth: "0"));

        var file = Response(listing, "/dav/Work/diagram.png");
        Assert.Equal($"\"{version}\"", Prop(file, "getetag")?.Value);
        Assert.Equal(png.Length.ToString(CultureInfo.InvariantCulture), Prop(file, "getcontentlength")?.Value);
        Assert.Equal("image/png", Prop(file, "getcontenttype")?.Value);
    }

    [Fact]
    public async Task An_unknown_property_is_404_in_its_propstat()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# Plan");

        var listing = await MultistatusAsync(await dav.PropfindAsync(
            "/dav/Work/plan.md", depth: "0",
            body: DavClient.Body("<D:propfind xmlns:D=\"DAV:\" xmlns:Z=\"urn:example:z\"><D:prop><D:getetag/><Z:colour/></D:prop></D:propfind>")));

        var response = Response(listing, "/dav/Work/plan.md");
        var propstats = response.Elements(D + "propstat").ToList();
        var ok = Assert.Single(propstats, p => p.Element(D + "status")!.Value == "HTTP/1.1 200 OK");
        var missing = Assert.Single(propstats, p => p.Element(D + "status")!.Value == "HTTP/1.1 404 Not Found");
        Assert.NotNull(ok.Element(D + "prop")!.Element(D + "getetag"));
        Assert.Equal([XName.Get("colour", "urn:example:z")], missing.Element(D + "prop")!.Elements().Select(e => e.Name));
    }

    /// <summary>One reader for every XML body: Tasks 6 and 7 add <c>PROPPATCH</c> and <c>LOCK</c> to this test.</summary>
    [Fact]
    public async Task A_doctype_in_the_body_is_refused()
    {
        var dav = await DavClient.NewAsync(Api);
        string[] bodies =
        [
            "<?xml version=\"1.0\"?><!DOCTYPE propfind [<!ENTITY x \"y\">]><D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>",
            "<?xml version=\"1.0\"?><!DOCTYPE propfind [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><D:propfind xmlns:D=\"DAV:\"><D:prop><D:x>&x;</D:x></D:prop></D:propfind>",
            "<?xml version=\"1.0\"?><!DOCTYPE propfind SYSTEM \"http://127.0.0.1/evil.dtd\"><D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>",
        ];

        foreach (var body in bodies)
        {
            using var response = await dav.PropfindAsync("/dav/Work/", depth: "0", body: DavClient.Body(body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var malformed = await dav.PropfindAsync("/dav/Work/", depth: "0", body: DavClient.Body("<D:propfind xmlns:D=\"DAV:\"><D:allprop>"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var notPropfind = await dav.PropfindAsync("/dav/Work/", depth: "0", body: DavClient.Body("<D:lockinfo xmlns:D=\"DAV:\"/>"));
        Assert.Equal(HttpStatusCode.BadRequest, notPropfind.StatusCode);
    }

    [Fact]
    public async Task A_body_over_64_KB_is_refused()
    {
        var dav = await DavClient.NewAsync(Api);
        string Body(int bytes)
        {
            const string head = "<D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind><!--";
            const string tail = "-->";
            return head + new string('x', bytes - head.Length - tail.Length) + tail;
        }

        using (var atTheLimit = await dav.PropfindAsync("/dav/Work/", depth: "0", body: new Unsized(Encoding.UTF8.GetBytes(Body(64 * 1024)))))
        {
            Assert.Equal(HttpStatusCode.MultiStatus, atTheLimit.StatusCode);
        }

        using (var chunked = await dav.PropfindAsync("/dav/Work/", depth: "0", body: new Unsized(Encoding.UTF8.GetBytes(Body(64 * 1024 + 1)))))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        }

        using var declared = await dav.PropfindAsync("/dav/Work/", depth: "0", body: DavClient.Body(Body(200 * 1024)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
    }

    [Fact]
    public async Task Hrefs_are_percent_encoded_and_round_trip()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Café ideas.md", "Kaffee");
        await dav.Owner.CreateAsync("Öl & Wein/50% [neu].md", "Prost");

        var vault = await MultistatusAsync(await dav.PropfindAsync("/dav/Work/", depth: "1"));

        Assert.Contains("/dav/Work/Caf%C3%A9%20ideas.md", Hrefs(vault));
        Assert.Contains("/dav/Work/%C3%96l%20%26%20Wein/", Hrefs(vault));
        Assert.Equal("Café ideas.md", Prop(Response(vault, "/dav/Work/Caf%C3%A9%20ideas.md"), "displayname")?.Value);

        foreach (var href in new[] { "/dav/Work/Caf%C3%A9%20ideas.md", "/dav/Work/%C3%96l%20%26%20Wein/50%25%20%5Bneu%5D.md" })
        {
            var again = await MultistatusAsync(await dav.PropfindAsync(href, depth: "0"));
            Assert.Equal([href], Hrefs(again));
        }

        // Decomposed, as a Mac sends it: the same note.
        var decomposed = await MultistatusAsync(await dav.PropfindAsync("/dav/Work/Cafe%CC%81%20ideas.md", depth: "0"));
        Assert.Equal(["/dav/Work/Caf%C3%A9%20ideas.md"], Hrefs(decomposed));
    }

    [Theory]
    [InlineData("/dav/Work/a/../plan.md")]
    [InlineData("/dav/Work/%2E%2E/Work/plan.md")]
    [InlineData("/dav/Work/./plan.md")]
    [InlineData("/dav/Work/a%2Fplan.md")]
    [InlineData("/dav/Work/a%2fplan.md")]
    [InlineData("/dav/Work//plan.md")]
    [InlineData("/dav/Work/a%5Cplan.md")]
    public async Task Dot_dot_and_encoded_slashes_are_refused(string target)
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# Plan");
        await using var kestrel = Kestrel();

        var response = await RawAsync(kestrel, $"PROPFIND {target} HTTP/1.1", dav.Authorization);
        var fine = await RawAsync(kestrel, "PROPFIND /dav/Work/plan.md HTTP/1.1", dav.Authorization);

        Assert.StartsWith("HTTP/1.1 400", response, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 207", fine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dav_without_slash_redirects_or_answers_as_dav_slash()
    {
        var dav = await DavClient.NewAsync(Api);

        foreach (var method in new[] { DavClient.Propfind, HttpMethod.Get, HttpMethod.Head })
        {
            foreach (var path in new[] { "/dav", "/dav?x=1", "/DAV" })
            {
                using var response = await dav.SendAsync(method, path, headers: new Dictionary<string, string> { ["Depth"] = "0" });

                Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
                Assert.Equal("/dav/", response.Headers.Location?.OriginalString);
                DavAssert.PrivateWithoutCookie(response);
            }
        }

        foreach (var method in new[] { DavClient.Proppatch, DavClient.Lock, DavClient.Mkcol, HttpMethod.Put })
        {
            using var bare = await dav.SendAsync(method, "/dav");
            using var slash = await dav.SendAsync(method, "/dav/");

            Assert.NotEqual(HttpStatusCode.PermanentRedirect, bare.StatusCode);
            Assert.Equal(slash.StatusCode, bare.StatusCode);
        }
    }

    /// <summary>The metrics refuse a method HTTP and WebDAV don't have before anything else (HttpMetrics: bounded labels);
    /// without them, /dav does, and says which it has.</summary>
    [Fact]
    public async Task An_unknown_method_gets_405()
    {
        var dav = await DavClient.NewAsync(Api);
        await using var withoutMetrics = Api.WithWebHostBuilder(b => b.UseSetting("Kumbu:Metrics:Enabled", "false"));

        using var refused = await dav.SendAsync(new HttpMethod("BREW"), "/dav/Work/");
        using var answered = await dav.On(withoutMetrics).SendAsync(new HttpMethod("BREW"), "/dav/Work/");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, refused.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, answered.StatusCode);
        Assert.Equal("OPTIONS, PROPFIND, PROPPATCH, GET, HEAD, PUT, DELETE, MKCOL, COPY, MOVE, LOCK, UNLOCK", DavAssert.Header(answered, "Allow"));
        DavAssert.PrivateWithoutCookie(answered);
    }

    private WebApplicationFactory<Program> Kestrel()
    {
        var factory = Api.WithWebHostBuilder(b => b.UseUrls("http://127.0.0.1:0"));
        factory.UseKestrel();
        factory.StartServer();
        return factory;
    }

    /// <summary>A request over a socket, so that its target arrives as written: HttpClient resolves dot segments.</summary>
    private static async Task<string> RawAsync(WebApplicationFactory<Program> factory, string requestLine, string authorization)
    {
        var address = new Uri(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port, Ct);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"{requestLine}\r\nHost: {address.Authority}\r\nAuthorization: {authorization}\r\nX-Forwarded-Proto: https\r\nX-Forwarded-For: {DavClient.NewIp()}\r\n"
            + "Depth: 0\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), Ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(Ct);
    }

    private static async Task<XElement> MultistatusAsync(HttpResponseMessage response)
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

    private static List<string> Hrefs(XElement multistatus) =>
        [.. multistatus.Elements(D + "response").Select(r => r.Element(D + "href")!.Value)];

    private static XElement Response(XElement multistatus, string href) =>
        Assert.Single(multistatus.Elements(D + "response"), r => r.Element(D + "href")!.Value == href);

    /// <summary>The <c>prop</c> of the response's 200 propstat.</summary>
    private static XElement? Ok(XElement response) =>
        response.Elements(D + "propstat").FirstOrDefault(p => p.Element(D + "status")?.Value == "HTTP/1.1 200 OK")?.Element(D + "prop");

    private static XElement? Prop(XElement response, string name) => Ok(response)?.Element(D + name);

    private static bool IsCollection(XElement response) => Prop(response, "resourcetype")?.Element(D + "collection") is not null;

    /// <summary>A body without a length, sent as a chunked one is.</summary>
    private sealed class Unsized(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
