// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Kumbu.Server.Api;
using Kumbu.Server.Dav;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Dav;

/// <summary>The parking area (WebDAV design §3.8): client junk kept in memory for the share that wrote it, served through
/// the same handlers as the store's, and never in MongoDB.</summary>
[Collection("app")]
public sealed class DavParkingTests(KumbuAppFactory app) : IAsyncDisposable
{
    // Whole seconds, as the other timed tests.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program>? _timed;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>A server of its own whose clock the test moves.</summary>
    private WebApplicationFactory<Program> Timed => _timed ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    public async ValueTask DisposeAsync()
    {
        if (_timed is not null) await _timed.DisposeAsync();
    }

    private static string Href(string pathBelowVault) => "/dav/Work/" + string.Join('/', pathBelowVault.Split('/').Select(Uri.EscapeDataString));

    private static HttpContent Bytes(byte[] bytes) => new ByteArrayContent(bytes);

    private static HttpContent Text(string text) => Bytes(Encoding.UTF8.GetBytes(text));

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) => headers.ToDictionary(h => h.Name, h => h.Value);

    private async Task<Dictionary<string, long>> CountsAsync()
    {
        var counts = new Dictionary<string, long>();
        foreach (var name in await (await app.Database.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct))
        {
            // The profiler's own collection counts reads.
            if (name.StartsWith("system.", StringComparison.Ordinal)) continue;
            counts[name] = await app.Database.GetCollection<BsonDocument>(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);
        }

        return counts;
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("._x.md")]
    [InlineData(".gitignore")]
    [InlineData(".~lock.a.md#")]
    [InlineData(".goutputstream-1")]
    [InlineData(".a.md.swp")]
    [InlineData("Thumbs.db")]
    [InlineData("desktop.ini")]
    [InlineData("~$doc.md")]
    [InlineData("~a.tmp")]
    [InlineData("a.tmp")]
    [InlineData("a.md~")]
    [InlineData("a.swp")]
    [InlineData("a.swx")]
    [InlineData("a.kate-swp")]
    [InlineData("a.part")]
    [InlineData("a.crdownload")]
    [InlineData(".ocTransferId1.part")]
    [InlineData("4913")]
    [InlineData("#a.md#")]
    [InlineData(".git/config")]
    public async Task Every_junk_pattern_is_parked_and_never_reaches_mongodb(string name)
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# Plan");
        // The share's first request, and whatever it records, before the counting starts.
        using (var first = await dav.PropfindAsync("/dav/Work/", depth: "1")) Assert.Equal(HttpStatusCode.MultiStatus, first.StatusCode);
        var before = await CountsAsync();

        if (name.Contains('/'))
        {
            using var folder = await dav.SendAsync(DavClient.Mkcol, Href(name[..name.LastIndexOf('/')]));
            Assert.Equal(HttpStatusCode.Created, folder.StatusCode);
        }

        using (var put = await dav.SendAsync(HttpMethod.Put, Href(name), Text("junk of " + name))) Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        using (var get = await dav.SendAsync(HttpMethod.Get, Href(name)))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("junk of " + name, await get.Content.ReadAsStringAsync(Ct));
        }

        var parent = name.Contains('/') ? Href(name[..name.LastIndexOf('/')]) + "/" : "/dav/Work/";
        var listing = await DavAssert.MultistatusAsync(await dav.PropfindAsync(parent, depth: "1"));
        Assert.Contains(Href(name), DavAssert.Hrefs(listing));

        Assert.Equal(before, await CountsAsync());
    }

    /// <summary>The store never has a dot-folder (<c>NoteNames</c> refuses one), so over HTTP what is below one is
    /// parked by its parked folder too; the rule itself is checked here.</summary>
    [Theory]
    [InlineData(".git/config", true)]
    [InlineData("Ideas/.obsidian/plugins/x/main.js", true)]
    [InlineData("Ideas/.DS_Store", true)]
    [InlineData("A.TMP", true)]
    [InlineData("THUMBS.DB", true)]
    [InlineData("#", false)]
    [InlineData("plan.md", false)]
    [InlineData("Ideas/plan.md", false)]
    [InlineData("Ideas.git/plan.md", false)]
    [InlineData("4913.md", false)]
    [InlineData("a.tmp.md", false)]
    [InlineData("diagram.png", false)]
    public void The_patterns_are_matched_on_the_name_and_on_dot_folders_above_it(string path, bool parked) =>
        Assert.Equal(parked, DavRules.IsParked(path.Split('/')));

    [Fact]
    public async Task Parked_files_are_listed_for_their_share_only()
    {
        var alice = await DavClient.NewAsync(Api, label: "Laptop");
        var other = await DavClient.NewAsync(Api, owner: alice.Owner, label: "Desktop");

        using (var put = await alice.SendAsync(HttpMethod.Put, "/dav/Work/.DS_Store", Text("finder"))) Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        Assert.Contains("/dav/Work/.DS_Store", DavAssert.Hrefs(await DavAssert.MultistatusAsync(await alice.PropfindAsync("/dav/Work/"))));
        Assert.DoesNotContain("/dav/Work/.DS_Store", DavAssert.Hrefs(await DavAssert.MultistatusAsync(await other.PropfindAsync("/dav/Work/"))));
        using (var get = await other.SendAsync(HttpMethod.Get, "/dav/Work/.DS_Store")) Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using (var find = await other.PropfindAsync("/dav/Work/.DS_Store", depth: "0")) Assert.Equal(HttpStatusCode.NotFound, find.StatusCode);
        using (var delete = await other.SendAsync(HttpMethod.Delete, "/dav/Work/.DS_Store")) Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        // The other share parks its own under the same name; each reads back its own.
        using (var put = await other.SendAsync(HttpMethod.Put, "/dav/Work/.DS_Store", Text("other"))) Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        using var mine = await alice.SendAsync(HttpMethod.Get, "/dav/Work/.DS_Store");
        Assert.Equal("finder", await mine.Content.ReadAsStringAsync(Ct));

        // Nothing of it in the app.
        using var tree = await alice.Owner.GetAsync("/api/vaults/Work/tree");
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        Assert.DoesNotContain("DS_Store", await tree.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parked_file_reads_back_as_an_octet_stream_attachment()
    {
        var dav = await DavClient.NewAsync(Api);
        var bytes = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.page.html", Bytes(bytes), Headers(("Content-Type", "text/html")));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var etag = put.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        using var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.page.html");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(bytes, await get.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("application/octet-stream", DavAssert.Header(get, "Content-Type"));
        Assert.Equal(FileApi.Disposition(".page.html", download: true), DavAssert.Header(get, "Content-Disposition"));
        Assert.StartsWith("attachment", DavAssert.Header(get, "Content-Disposition"), StringComparison.Ordinal);
        Assert.Equal(Security.FileContentPolicy, DavAssert.Header(get, "Content-Security-Policy"));
        Assert.Equal("nosniff", DavAssert.Header(get, "X-Content-Type-Options"));
        Assert.Equal(etag, get.Headers.ETag?.Tag);
        DavAssert.PrivateWithoutCookie(get);

        using var head = await dav.SendAsync(HttpMethod.Head, "/dav/Work/.page.html");
        Assert.Equal(bytes.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(Ct));
        using var same = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.page.html", headers: Headers(("If-None-Match", etag)));
        Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);

        var found = await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/.page.html", depth: "0"));
        var file = DavAssert.Response(found, "/dav/Work/.page.html");
        Assert.Equal("application/octet-stream", DavAssert.Prop(file, "getcontenttype")?.Value);
        Assert.Equal(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), DavAssert.Prop(file, "getcontentlength")?.Value);
        Assert.Equal(etag, DavAssert.Prop(file, "getetag")?.Value);
        Assert.False(DavAssert.IsCollection(file));
    }

    [Fact]
    public async Task Parked_files_expire_ten_minutes_after_their_last_write()
    {
        var dav = await DavClient.NewAsync(Timed);
        using (var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.DS_Store", Text("one"))) Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        _time.Advance(TimeSpan.FromMinutes(5));
        using (var again = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.DS_Store", Text("two"))) Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        _time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        using (var present = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.DS_Store"))
        {
            Assert.Equal(HttpStatusCode.OK, present.StatusCode);
            Assert.Equal("two", await present.Content.ReadAsStringAsync(Ct));
        }

        _time.Advance(TimeSpan.FromSeconds(2));
        using (var gone = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.DS_Store")) Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.DoesNotContain("/dav/Work/.DS_Store", DavAssert.Hrefs(await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/"))));
    }

    [Fact]
    public async Task The_oldest_parked_files_are_evicted_beyond_64_MB()
    {
        var dav = await DavClient.NewAsync(Timed);
        var chunk = new byte[15 * 1024 * 1024];
        foreach (var name in new[] { "a", "b", "c", "d", "e" })
        {
            using var put = await dav.SendAsync(HttpMethod.Put, $"/dav/Work/.{name}.part", Bytes(chunk));
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        // 75 MiB were written: the first went to make room, the last four (60 MiB) stay.
        using (var oldest = await dav.SendAsync(HttpMethod.Head, "/dav/Work/.a.part")) Assert.Equal(HttpStatusCode.NotFound, oldest.StatusCode);
        foreach (var name in new[] { "b", "c", "d", "e" })
        {
            using var kept = await dav.SendAsync(HttpMethod.Head, $"/dav/Work/.{name}.part");
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        }
    }

    [Fact]
    public async Task A_dot_folder_can_be_made_filled_and_listed()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/Routing.md", "Routing");

        using (var made = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/.git")) Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        using (var again = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/.git/")) Assert.Equal(HttpStatusCode.MethodNotAllowed, again.StatusCode);
        using (var objects = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/.git/objects/")) Assert.Equal(HttpStatusCode.Created, objects.StatusCode);
        using (var config = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.git/config", Text("[core]"))) Assert.Equal(HttpStatusCode.Created, config.StatusCode);
        using (var blob = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.git/objects/ab", Text("blob"))) Assert.Equal(HttpStatusCode.Created, blob.StatusCode);
        // In a real folder too.
        using (var inIdeas = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/Ideas/.obsidian")) Assert.Equal(HttpStatusCode.Created, inIdeas.StatusCode);

        var git = await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/.git/", depth: "1"));
        Assert.Equal(["/dav/Work/.git/", "/dav/Work/.git/objects/", "/dav/Work/.git/config"], DavAssert.Hrefs(git));
        Assert.True(DavAssert.IsCollection(DavAssert.Response(git, "/dav/Work/.git/")));
        Assert.True(DavAssert.IsCollection(DavAssert.Response(git, "/dav/Work/.git/objects/")));
        Assert.Equal("6", DavAssert.Prop(DavAssert.Response(git, "/dav/Work/.git/config"), "getcontentlength")?.Value);

        var vault = await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/", depth: "1"));
        Assert.True(DavAssert.IsCollection(DavAssert.Response(vault, "/dav/Work/.git/")));
        var ideas = await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/Ideas/", depth: "1"));
        Assert.Equal(["/dav/Work/Ideas/", "/dav/Work/Ideas/.obsidian/", "/dav/Work/Ideas/Routing.md"], DavAssert.Hrefs(ideas));

        // A parent that doesn't exist is a conflict (RFC 4918 §9.3.1, §9.7.1); a MKCOL with a body is refused.
        using (var orphan = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/.nothing/x")) Assert.Equal(HttpStatusCode.Conflict, orphan.StatusCode);
        using (var orphanFile = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.nothing/x", Text("x"))) Assert.Equal(HttpStatusCode.Conflict, orphanFile.StatusCode);
        using (var withBody = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/.vscode", Text("body"))) Assert.Equal(HttpStatusCode.UnsupportedMediaType, withBody.StatusCode);
        using (var overFile = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/.git/config")) Assert.Equal(HttpStatusCode.MethodNotAllowed, overFile.StatusCode);
        using (var putOnFolder = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.git", Text("x"))) Assert.Equal(HttpStatusCode.MethodNotAllowed, putOnFolder.StatusCode);
        using (var getFolder = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.git/")) Assert.Equal(HttpStatusCode.MethodNotAllowed, getFolder.StatusCode);

        // Nothing reached the store: the folder the store knows is Ideas only.
        using var tree = await dav.Owner.GetAsync("/api/vaults/Work/tree");
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        var known = await tree.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Ideas", known, StringComparison.Ordinal);
        Assert.DoesNotContain(".git", known, StringComparison.Ordinal);
        Assert.DoesNotContain(".obsidian", known, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parked_file_can_be_moved_copied_and_deleted_within_its_share()
    {
        var dav = await DavClient.NewAsync(Api);
        var other = await DavClient.NewAsync(Api, owner: dav.Owner, label: "Desktop");
        const string root = "https://localhost/dav/Work/";
        Dictionary<string, string> To(string path, string? overwrite = null, string? depth = null)
        {
            var headers = Headers(("Destination", root + path));
            if (overwrite is not null) headers["Overwrite"] = overwrite;
            if (depth is not null) headers["Depth"] = depth;
            return headers;
        }

        using (var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.a.swp", Text("swap"))) Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        using (var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/.a.swp", headers: To(".b.swp"))) Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        using (var refused = await dav.SendAsync(DavClient.Copy, "/dav/Work/.a.swp", headers: To(".b.swp", "F"))) Assert.Equal(HttpStatusCode.PreconditionFailed, refused.StatusCode);
        using (var over = await dav.SendAsync(DavClient.Copy, "/dav/Work/.a.swp", headers: To(".b.swp", "T"))) Assert.Equal(HttpStatusCode.NoContent, over.StatusCode);
        using (var self = await dav.SendAsync(DavClient.Copy, "/dav/Work/.a.swp", headers: To(".a.swp"))) Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        using (var missing = await dav.SendAsync(DavClient.Copy, "/dav/Work/.none.swp", headers: To(".x.swp"))) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var orphan = await dav.SendAsync(DavClient.Copy, "/dav/Work/.a.swp", headers: To(".nothing/x"))) Assert.Equal(HttpStatusCode.Conflict, orphan.StatusCode);
        using (var noDestination = await dav.SendAsync(DavClient.Copy, "/dav/Work/.a.swp")) Assert.Equal(HttpStatusCode.BadRequest, noDestination.StatusCode);
        using (var elsewhere = await dav.SendAsync(DavClient.Copy, "/dav/Work/.a.swp", headers: Headers(("Destination", "https://kumbu.example.org/dav/Work/.c.swp")))) Assert.Equal(HttpStatusCode.BadGateway, elsewhere.StatusCode);
        using (var outside = await dav.SendAsync(DavClient.Copy, "/dav/Work/.a.swp", headers: Headers(("Destination", "https://localhost/api/.c.swp")))) Assert.Equal(HttpStatusCode.BadGateway, outside.StatusCode);

        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/.b.swp", headers: To(".c.swp"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        using (var moved = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.b.swp")) Assert.Equal(HttpStatusCode.NotFound, moved.StatusCode);
        using (var arrived = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.c.swp")) Assert.Equal("swap", await arrived.Content.ReadAsStringAsync(Ct));

        // A folder, with what is in it.
        using (var made = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/.git")) Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        using (var config = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.git/config", Text("[core]"))) Assert.Equal(HttpStatusCode.Created, config.StatusCode);
        using (var shallow = await dav.SendAsync(DavClient.Copy, "/dav/Work/.git/", headers: To(".empty/", depth: "0"))) Assert.Equal(HttpStatusCode.Created, shallow.StatusCode);
        Assert.Equal(["/dav/Work/.empty/"], DavAssert.Hrefs(await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/.empty/"))));
        using (var deep = await dav.SendAsync(DavClient.Copy, "/dav/Work/.git/", headers: To(".git-copy/"))) Assert.Equal(HttpStatusCode.Created, deep.StatusCode);
        using (var into = await dav.SendAsync(DavClient.Move, "/dav/Work/.git/", headers: To(".git/inner/"))) Assert.Equal(HttpStatusCode.Forbidden, into.StatusCode);
        using (var folder = await dav.SendAsync(DavClient.Move, "/dav/Work/.git/", headers: To(".git2/"))) Assert.Equal(HttpStatusCode.Created, folder.StatusCode);
        using (var inside = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.git2/config")) Assert.Equal("[core]", await inside.Content.ReadAsStringAsync(Ct));
        using (var copied = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.git-copy/config")) Assert.Equal("[core]", await copied.Content.ReadAsStringAsync(Ct));
        using (var left = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.git/config")) Assert.Equal(HttpStatusCode.NotFound, left.StatusCode);

        // The other share sees none of it, and can't move it.
        using (var theirs = await other.SendAsync(DavClient.Move, "/dav/Work/.c.swp", headers: To(".d.swp"))) Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);

        using (var delete = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/.c.swp")) Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using (var again = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/.c.swp")) Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        using (var tree = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/.git2/")) Assert.Equal(HttpStatusCode.NoContent, tree.StatusCode);
        using (var below = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.git2/config")) Assert.Equal(HttpStatusCode.NotFound, below.StatusCode);
    }

    [Fact]
    public async Task Parked_files_stay_in_their_vault()
    {
        var owner = await Kumbu.Server.Tests.Api.RestClient.NewAsync(Api, vault: "Work");
        using (var personal = await owner.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Personal" })) Assert.Equal(HttpStatusCode.Created, personal.StatusCode);
        var dav = await DavClient.NewAsync(Api, owner, vaults: new Dictionary<string, string> { ["Work"] = "write", ["Personal"] = "write" });

        using (var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.a", Text("a"))) Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        using (var get = await dav.SendAsync(HttpMethod.Get, "/dav/Personal/.a")) Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        // Across vaults the server won't (WebDAV design §3.5): a client falls back to copy and delete.
        foreach (var method in new[] { DavClient.Copy, DavClient.Move })
        {
            using var across = await dav.SendAsync(method, "/dav/Work/.a", headers: Headers(("Destination", "https://localhost/dav/Personal/.a")));
            Assert.Equal(HttpStatusCode.BadGateway, across.StatusCode);
        }

        using (var nowhere = await dav.SendAsync(HttpMethod.Put, "/dav/Nowhere/.a", Text("a"))) Assert.Equal(HttpStatusCode.NotFound, nowhere.StatusCode);
        using (var vault = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/")) Assert.Equal(HttpStatusCode.Forbidden, vault.StatusCode);
        using (var root = await dav.SendAsync(HttpMethod.Put, "/dav/", Text("a"))) Assert.Equal(HttpStatusCode.Forbidden, root.StatusCode);
    }

    [Fact]
    public async Task A_read_share_cannot_park()
    {
        var dav = await DavClient.NewAsync(Api, right: "read");

        using (var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.DS_Store", Text("finder"))) Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        using (var mkcol = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/.git")) Assert.Equal(HttpStatusCode.Forbidden, mkcol.StatusCode);
        using (var delete = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/.DS_Store")) Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        using (var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/.a", headers: Headers(("Destination", "https://localhost/dav/Work/.b")))) Assert.Equal(HttpStatusCode.Forbidden, copy.StatusCode);
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/.a", headers: Headers(("Destination", "https://localhost/dav/Work/.b")))) Assert.Equal(HttpStatusCode.Forbidden, move.StatusCode);

        // It still reads and lists.
        using var listing = await dav.PropfindAsync("/dav/Work/");
        Assert.Equal(HttpStatusCode.MultiStatus, listing.StatusCode);
        using var nothing = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.DS_Store");
        Assert.Equal(HttpStatusCode.NotFound, nothing.StatusCode);
    }

    [Fact]
    public async Task Nothing_goes_onto_a_folder_above_its_source()
    {
        var dav = await DavClient.NewAsync(Api);
        using (var made = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/.x")) Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        using (var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.x/y", Text("y"))) Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        // Over its own folder: the target's removal would take the source with it (RFC 4918 §9.8.5 lets a server refuse).
        foreach (var method in new[] { DavClient.Move, DavClient.Copy })
        {
            using var up = await dav.SendAsync(method, "/dav/Work/.x/y", headers: Headers(("Destination", "https://localhost/dav/Work/.x"), ("Overwrite", "T")));
            Assert.Equal(HttpStatusCode.Forbidden, up.StatusCode);
        }

        using var still = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.x/y");
        Assert.Equal("y", await still.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_parked_folder_never_takes_what_goes_into_a_store_folder_of_its_name()
    {
        var dav = await DavClient.NewAsync(Api);
        using (var made = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/x.tmp")) Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        // Later the store gets a folder of that name, from elsewhere.
        await dav.Owner.CreateAsync("x.tmp/n.md", "# N");

        using (var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/x.tmp/m.md", Text("# M"))) Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        using var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/x.tmp/m.md");
        Assert.Equal("text/markdown; charset=utf-8", DavAssert.Header(get, "Content-Type"));
        Assert.Equal("# M", (await dav.Owner.ReadAsync("x.tmp/m.md"))["content"]!.GetValue<string>());

        // A parked name below it is parked all the same.
        using var junk = await dav.SendAsync(HttpMethod.Put, "/dav/Work/x.tmp/.DS_Store", Text("finder"));
        Assert.Equal(HttpStatusCode.Created, junk.StatusCode);
    }

    private static DavParking Parking() => new(new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds())));

    /// <summary>Each entry costs memory whatever its size: past 10 000 entries in a share the oldest file goes, and if
    /// only folders are left, a 507.</summary>
    [Fact]
    public void A_share_holds_at_most_ten_thousand_entries()
    {
        var parking = Parking();
        for (var i = 0; i < 10_000; i++) parking.Put("s", "v", $"f{i}", []);
        parking.Put("s", "v", "last", []);
        Assert.Null(parking.Get("s", "v", "f0"));
        Assert.NotNull(parking.Get("s", "v", "f1"));
        Assert.NotNull(parking.Get("s", "v", "last"));

        var folders = Parking();
        for (var i = 0; i < 10_000; i++) folders.MakeFolder("s", "v", $".d{i}");
        var full = Assert.Throws<DavException>(() => folders.MakeFolder("s", "v", ".one-more"));
        Assert.Equal(StatusCodes.Status507InsufficientStorage, full.Status);
    }

    [Fact]
    public void Folders_and_empty_files_cost_a_share_more_than_their_names()
    {
        var parking = Parking();
        parking.Put("s", "v", "big", new byte[DavRules.ParkingCap - 2048]);
        foreach (var name in new[] { "a", "b", "c", "d" }) parking.MakeFolder("s", "v", name);
        Assert.Null(parking.Get("s", "v", "big"));
    }

    [Fact]
    public void A_copy_never_evicts_its_own_source()
    {
        var parking = Parking();
        parking.Put("s", "v", "a", new byte[40 * 1024 * 1024]);

        var full = Assert.Throws<DavException>(() => parking.Copy("s", "v", "a", "b", deep: true));
        Assert.Equal(StatusCodes.Status507InsufficientStorage, full.Status);
        Assert.NotNull(parking.Get("s", "v", "a"));
        Assert.Null(parking.Get("s", "v", "b"));
        Assert.False(parking.Move("s", "v", "nothing", "c"));
    }

    /// <summary>What every share parks together is capped too (security review finding 4): past
    /// <see cref="DavRules.ParkingTotalCap"/> files of other shares go (of equally large ones, the oldest), and the new
    /// one is parked. One array stands for every file: the cap counts bytes, the test holds them once.</summary>
    [Fact]
    public void Parked_bytes_are_capped_across_shares_the_oldest_going_first()
    {
        var time = new FakeTimeProvider();
        var parking = new DavParking(time);
        var big = new byte[DavRules.ParkingCap - 64 * 1024];
        var full = (int)(DavRules.ParkingTotalCap / big.Length);
        for (var i = 0; i < full; i++)
        {
            parking.Put($"s{i}", "v", ".big", big);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.All(Enumerable.Range(0, full), i => Assert.NotNull(parking.Get($"s{i}", "v", ".big")));

        parking.Put("new", "v", ".big", big);

        Assert.Null(parking.Get("s0", "v", ".big"));
        Assert.All(Enumerable.Range(1, full - 1), i => Assert.NotNull(parking.Get($"s{i}", "v", ".big")));
        Assert.NotNull(parking.Get("new", "v", ".big"));
    }

    /// <summary>Past the server-wide cap, the share holding the most goes first, not whoever parked earliest: shares
    /// flooding the parking area evict their own files, never another share's temp file between its PUT and its MOVE.</summary>
    [Fact]
    public void Past_the_total_cap_the_largest_share_loses_its_oldest_file_first()
    {
        var time = new FakeTimeProvider();
        var parking = new DavParking(time);
        parking.Put("victim", "v", ".~tmp", [1, 2, 3]);
        time.Advance(TimeSpan.FromSeconds(1));
        var big = new byte[DavRules.ParkingCap - 64 * 1024];
        var flood = (int)(DavRules.ParkingTotalCap / big.Length) + 1;
        for (var i = 0; i < flood; i++)
        {
            parking.Put($"flood{i}", "v", ".big", big);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.NotNull(parking.Get("victim", "v", ".~tmp"));
        Assert.Null(parking.Get("flood0", "v", ".big"));
        Assert.NotNull(parking.Get($"flood{flood - 1}", "v", ".big"));
    }

    /// <summary>While the writing share is the largest, room is made in its own area only, even when another share's
    /// files are older.</summary>
    [Fact]
    public void A_writer_that_is_the_largest_share_evicts_its_own()
    {
        var time = new FakeTimeProvider();
        var parking = new DavParking(time);
        var other = new byte[46 * 1024 * 1024];
        for (var i = 0; i < 10; i++)
        {
            parking.Put($"s{i}", "v", ".other", other);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var half = new byte[30 * 1024 * 1024];
        parking.Put("writer", "v", ".first", half);
        time.Advance(TimeSpan.FromSeconds(1));
        parking.Put("writer", "v", ".second", half);

        Assert.All(Enumerable.Range(0, 10), i => Assert.NotNull(parking.Get($"s{i}", "v", ".other")));
        Assert.Null(parking.Get("writer", "v", ".first"));
        Assert.NotNull(parking.Get("writer", "v", ".second"));
    }

    /// <summary>The <c>/dav</c> endpoint lets a body through up to the parking cap and a little more, so that what
    /// DavBody counts, and refuses with its own sentence, is what Kestrel lets in: its default of 30,000,000 bytes would
    /// refuse a parked file of 29 to 64 MiB, a <c>.git</c> pack in a Finder copy, say.</summary>
    [Fact]
    public void The_dav_endpoint_takes_a_body_up_to_the_parking_cap()
    {
        // The methods WebDAV has; the 405 for any other reads no body.
        var endpoint = Api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == DavPaths.Prefix + "/{**path}" && e.Order == 0);

        Assert.True(endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize > DavRules.ParkingCap);
    }

    /// <summary>A parked PUT of 30 MB, above Kestrel's default body limit, through a real Kestrel (TestServer enforces
    /// no limit): parked, 201.</summary>
    [Fact]
    public async Task A_30_MB_parked_put_goes_through_kestrel()
    {
        var dav = await DavClient.NewAsync(Api);
        await using var real = app.PublicApi.WithWebHostBuilder(_ => { });
        real.UseKestrel(0);
        real.StartServer();
        var address = real.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var request = new HttpRequestMessage(HttpMethod.Put, "/dav/Work/.pack") { Content = new ByteArrayContent(new byte[30 * 1024 * 1024]) };
        request.Headers.TryAddWithoutValidation("Authorization", dav.Authorization);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", DavClient.NewIp());

        using var put = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
    }
}
