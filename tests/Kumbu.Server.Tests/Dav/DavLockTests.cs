// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Dav;

/// <summary>Fake locking (WebDAV design §3.7): <c>LOCK</c> hands out a token and reports it, nothing is ever refused for a
/// lock, and <c>If</c> headers are ignored.</summary>
[Collection("app")]
public sealed class DavLockTests(KumbuAppFactory app) : IAsyncDisposable
{
    private static readonly XNamespace D = "DAV:";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program>? _timed;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private WebApplicationFactory<Program> Timed => _timed ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    public async ValueTask DisposeAsync()
    {
        if (_timed is not null) await _timed.DisposeAsync();
    }

    private const string Owner = "<D:owner><D:href>mailto:alice@kumbu.example.org</D:href></D:owner>";

    private static HttpContent LockInfo(string scope = "exclusive") => DavClient.Body(
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><D:lockinfo xmlns:D=\"DAV:\"><D:lockscope><D:{scope}/></D:lockscope><D:locktype><D:write/></D:locktype>{Owner}</D:lockinfo>");

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) => headers.ToDictionary(h => h.Name, h => h.Value);

    /// <summary>A new lock on <paramref name="path"/>: its token and the activelock the answer reports.</summary>
    private static async Task<(string Token, XElement Active)> LockAsync(
        DavClient dav, string path, IReadOnlyDictionary<string, string>? headers = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await dav.SendAsync(DavClient.Lock, path, LockInfo(), headers);
        Assert.Equal(expected, response.StatusCode);
        Assert.StartsWith("application/xml", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        var header = DavAssert.Header(response, "Lock-Token");
        Assert.Matches("^<opaquelocktoken:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}>$", header);
        var body = XDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).Root!;
        Assert.Equal(D + "prop", body.Name);
        var active = Assert.Single(body.Element(D + "lockdiscovery")!.Elements(D + "activelock"));
        return (header[1..^1], active);
    }

    /// <summary>The activelocks <c>lockdiscovery</c> reports for <paramref name="href"/>.</summary>
    private static async Task<List<XElement>> DiscoverAsync(DavClient dav, string href)
    {
        var found = await DavAssert.MultistatusAsync(await dav.PropfindAsync(
            href, depth: "0", body: DavClient.Body("<D:propfind xmlns:D=\"DAV:\"><D:prop><D:lockdiscovery/></D:prop></D:propfind>")));
        var discovery = DavAssert.Prop(DavAssert.Response(found, href), "lockdiscovery");
        Assert.NotNull(discovery);
        return [.. discovery.Elements(D + "activelock")];
    }

    private static string? Timeout(XElement active) => active.Element(D + "timeout")?.Value;

    [Fact]
    public async Task Lock_returns_a_token_lockdiscovery_and_the_default_timeout()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# Plan");

        var (token, active) = await LockAsync(dav, "/dav/Work/plan.md");

        Assert.Equal(token, active.Element(D + "locktoken")?.Element(D + "href")?.Value);
        Assert.Equal("Second-600", Timeout(active));
        Assert.NotNull(active.Element(D + "lockscope")?.Element(D + "exclusive"));
        Assert.NotNull(active.Element(D + "locktype")?.Element(D + "write"));
        Assert.Equal("infinity", active.Element(D + "depth")?.Value);
        Assert.Equal("mailto:alice@kumbu.example.org", active.Element(D + "owner")?.Element(D + "href")?.Value);
        Assert.Equal("/dav/Work/plan.md", active.Element(D + "lockroot")?.Element(D + "href")?.Value);

        // On a parked file, and on a folder.
        using (var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.plan.md.swp", new ByteArrayContent([1]))) Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var (parked, _) = await LockAsync(dav, "/dav/Work/.plan.md.swp");
        Assert.NotEqual(token, parked);
        var (_, folder) = await LockAsync(dav, "/dav/Work/", Headers(("Depth", "0")));
        Assert.Equal("0", folder.Element(D + "depth")?.Value);
    }

    [Fact]
    public async Task A_lock_on_an_unmapped_path_parks_an_empty_file_or_creates_nothing()
    {
        var dav = await DavClient.NewAsync(Api);
        using (var made = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/.litmus/")) Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        // A parked name: an empty file is parked, as Go's x/net/webdav creates one (RFC 4918 §9.10.4).
        await LockAsync(dav, "/dav/Work/.litmus/lockme", expected: HttpStatusCode.Created);
        using (var parked = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.litmus/lockme"))
        {
            Assert.Equal(HttpStatusCode.OK, parked.StatusCode);
            Assert.Empty(await parked.Content.ReadAsByteArrayAsync(Ct));
        }

        // A name of the store: the lock is held, nothing is created.
        await LockAsync(dav, "/dav/Work/new.md");
        using (var nothing = await dav.PropfindAsync("/dav/Work/new.md", depth: "0")) Assert.Equal(HttpStatusCode.NotFound, nothing.StatusCode);

        // Without a parent: a conflict.
        using var orphan = await dav.SendAsync(DavClient.Lock, "/dav/Work/.nothing/lockme", LockInfo());
        Assert.Equal(HttpStatusCode.Conflict, orphan.StatusCode);
    }

    [Fact]
    public async Task Lock_timeout_is_at_most_what_the_client_asked()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# Plan");

        foreach (var (asked, granted) in new[]
        {
            ("Second-60", "Second-60"), ("Second-3600", "Second-600"), ("Infinite", "Second-600"),
            ("Infinite, Second-4100000000", "Second-600"), ("Second-1", "Second-1"), ("nonsense", "Second-600"),
        })
        {
            var (_, active) = await LockAsync(dav, "/dav/Work/plan.md", Headers(("Timeout", asked)));
            Assert.Equal(granted, Timeout(active));
        }
    }

    [Fact]
    public async Task A_lock_refresh_extends_it()
    {
        var dav = await DavClient.NewAsync(Timed);
        await dav.Owner.CreateAsync("plan.md", "# Plan");
        var (token, _) = await LockAsync(dav, "/dav/Work/plan.md");

        _time.Advance(TimeSpan.FromSeconds(590));
        Assert.Equal("Second-10", Timeout(Assert.Single(await DiscoverAsync(dav, "/dav/Work/plan.md"))));
        using (var refresh = await dav.SendAsync(DavClient.Lock, "/dav/Work/plan.md", headers: Headers(("If", $"(<{token}>)"), ("Timeout", "Second-600"))))
        {
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
            var body = XDocument.Parse(await refresh.Content.ReadAsStringAsync(Ct)).Root!;
            var active = Assert.Single(body.Element(D + "lockdiscovery")!.Elements(D + "activelock"));
            Assert.Equal("Second-600", Timeout(active));
            Assert.Equal(token, active.Element(D + "locktoken")?.Element(D + "href")?.Value);
        }

        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal("Second-580", Timeout(Assert.Single(await DiscoverAsync(dav, "/dav/Work/plan.md"))));
        _time.Advance(TimeSpan.FromSeconds(580));
        Assert.Empty(await DiscoverAsync(dav, "/dav/Work/plan.md"));

        // An expired or unknown token: the refresh's precondition fails.
        foreach (var unknown in new[] { token, $"opaquelocktoken:{Guid.NewGuid()}" })
        {
            using var stale = await dav.SendAsync(DavClient.Lock, "/dav/Work/plan.md", headers: Headers(("If", $"(<{unknown}>)")));
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        }
    }

    [Fact]
    public async Task Unlock_answers_204()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# Plan");
        var (token, _) = await LockAsync(dav, "/dav/Work/plan.md");
        var other = await DavClient.NewAsync(Api, owner: dav.Owner, label: "Desktop");

        // Another share's token is none of its own.
        using (var theirs = await other.SendAsync(DavClient.Unlock, "/dav/Work/plan.md", headers: Headers(("Lock-Token", $"<{token}>")))) Assert.Equal(HttpStatusCode.Conflict, theirs.StatusCode);
        using (var unlock = await dav.SendAsync(DavClient.Unlock, "/dav/Work/plan.md", headers: Headers(("Lock-Token", $"<{token}>")))) Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
        Assert.Empty(await DiscoverAsync(dav, "/dav/Work/plan.md"));
        using (var again = await dav.SendAsync(DavClient.Unlock, "/dav/Work/plan.md", headers: Headers(("Lock-Token", $"<{token}>")))) Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        using (var none = await dav.SendAsync(DavClient.Unlock, "/dav/Work/plan.md")) Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [Fact]
    public async Task If_headers_with_lock_tokens_are_ignored()
    {
        var dav = await DavClient.NewAsync(Api);
        using (var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.lockme", new ByteArrayContent([1]))) Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        await LockAsync(dav, "/dav/Work/.lockme");

        // Without the token, and with one the server never gave: both write.
        using (var without = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.lockme", new ByteArrayContent([2]))) Assert.Equal(HttpStatusCode.NoContent, without.StatusCode);
        var unknown = Headers(("If", $"(<opaquelocktoken:{Guid.NewGuid()}>)"));
        using (var with = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.lockme", new ByteArrayContent([3]), unknown)) Assert.Equal(HttpStatusCode.NoContent, with.StatusCode);
        using (var fresh = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.fresh", new ByteArrayContent([4]), unknown)) Assert.Equal(HttpStatusCode.Created, fresh.StatusCode);
        using (var delete = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/.lockme", headers: unknown)) Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Lockdiscovery_shows_a_held_lock()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/Routing.md", "Routing");
        await dav.Owner.CreateAsync("plan.md", "# Plan");
        var other = await DavClient.NewAsync(Api, owner: dav.Owner, label: "Desktop");

        var (token, _) = await LockAsync(dav, "/dav/Work/Ideas/");

        var listing = await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/Ideas/", depth: "1"));
        foreach (var href in new[] { "/dav/Work/Ideas/", "/dav/Work/Ideas/Routing.md" })
        {
            var active = Assert.Single(DavAssert.Prop(DavAssert.Response(listing, href), "lockdiscovery")!.Elements(D + "activelock"));
            Assert.Equal(token, active.Element(D + "locktoken")?.Element(D + "href")?.Value);
            Assert.Equal("/dav/Work/Ideas/", active.Element(D + "lockroot")?.Element(D + "href")?.Value);
        }

        // Not on what the lock doesn't cover, and not for another share.
        Assert.Empty(await DiscoverAsync(dav, "/dav/Work/plan.md"));
        Assert.Empty(await DiscoverAsync(dav, "/dav/Work/"));
        Assert.Empty(await DiscoverAsync(other, "/dav/Work/Ideas/"));
    }

    [Fact]
    public async Task A_read_share_cannot_lock()
    {
        var dav = await DavClient.NewAsync(Api, right: "read");
        await dav.Owner.CreateAsync("plan.md", "# Plan");

        using var refused = await dav.SendAsync(DavClient.Lock, "/dav/Work/plan.md", LockInfo());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var root = await dav.SendAsync(DavClient.Lock, "/dav/", LockInfo());
        Assert.Equal(HttpStatusCode.Forbidden, root.StatusCode);
        using var missing = await dav.SendAsync(DavClient.Lock, "/dav/Nowhere/plan.md", LockInfo());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_lock_body_that_is_no_lockinfo_is_400()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# Plan");

        foreach (var body in new[]
        {
            "<D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>",
            "<D:lockinfo xmlns:D=\"DAV:\"><D:locktype><D:write/></D:locktype></D:lockinfo>",
            "<D:lockinfo xmlns:D=\"DAV:\"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:read/></D:locktype></D:lockinfo>",
        })
        {
            using var response = await dav.SendAsync(DavClient.Lock, "/dav/Work/plan.md", DavClient.Body(body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var depth = await dav.SendAsync(DavClient.Lock, "/dav/Work/plan.md", LockInfo(), Headers(("Depth", "1")));
        Assert.Equal(HttpStatusCode.BadRequest, depth.StatusCode);
        using var noToken = await dav.SendAsync(DavClient.Lock, "/dav/Work/plan.md");
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
    }
}
