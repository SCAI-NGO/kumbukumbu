// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Dav;

/// <summary>HTTP Basic with a WebDAV share's username and password on <c>/dav</c> (WebDAV design §3.2, §3.3, §3.7).</summary>
[Collection("app")]
public sealed class DavAuthTests(KumbuAppFactory app)
{
    private const string Challenge = "Basic realm=\"Kumbu²\", charset=\"UTF-8\"";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HttpMethod Propfind = DavClient.Propfind;

    private static readonly HttpMethod Options = HttpMethod.Options;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values) ? string.Join(", ", values) : "";

    private static void AssertChallenge(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Challenge, Header(response, "WWW-Authenticate"));
    }

    private static void AssertPrivateWithoutCookie(HttpResponseMessage response)
    {
        Assert.Equal("private", Header(response, "Cache-Control"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private IMongoCollection<BsonDocument> SlotDocs => app.Database.GetCollection<BsonDocument>("slots");

    private static FilterDefinition<BsonDocument> SlotsOf(string shareId) =>
        Builders<BsonDocument>.Filter.Eq("kind", "dav") & Builders<BsonDocument>.Filter.Eq("share", shareId);

    [Fact]
    public async Task Options_needs_no_credentials_and_says_DAV_1_2_and_MS_Author_Via()
    {
        var dav = await DavClient.NewAsync(Api);

        foreach (var path in new[] { "/dav", "/dav/", "/dav/Work/", "/dav/Work/plan.md" })
        {
            using var response = await DavClient.SendAsync(Api, Options, path, authorization: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("1, 2", Header(response, "DAV"));
            Assert.Equal("DAV", Header(response, "MS-Author-Via"));
            Assert.Contains("PROPFIND", Header(response, "Allow"), StringComparison.Ordinal);
            Assert.Empty(response.Headers.WwwAuthenticate);
            AssertPrivateWithoutCookie(response);
        }

        using var withCredentials = await dav.SendAsync(Options, "/dav/Work/");
        Assert.Equal(HttpStatusCode.OK, withCredentials.StatusCode);
    }

    [Fact]
    public async Task Options_on_a_nonexistent_vault_answers_the_same()
    {
        var dav = await DavClient.NewAsync(Api);

        using var existing = await dav.SendAsync(Options, "/dav/Work/");
        using var missing = await dav.SendAsync(Options, "/dav/Nowhere/");
        using var anonymous = await DavClient.SendAsync(Api, Options, "/dav/Nowhere/", authorization: null);

        foreach (var other in new[] { missing, anonymous })
        {
            Assert.Equal(existing.StatusCode, other.StatusCode);
            foreach (var name in new[] { "DAV", "MS-Author-Via", "Allow", "Cache-Control", "Content-Length" })
            {
                Assert.Equal(Header(existing, name), Header(other, name));
            }

            Assert.Equal(await existing.Content.ReadAsStringAsync(Ct), await other.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task Oauth_cors_never_answers_dav()
    {
        using var preflight = await DavClient.SendAsync(
            Api, Options, "/dav/", authorization: null,
            headers: new Dictionary<string, string> { ["Origin"] = "https://elsewhere.example.org", ["Access-Control-Request-Method"] = "PROPFIND" });

        Assert.Equal(HttpStatusCode.OK, preflight.StatusCode);
        Assert.Equal("1, 2", Header(preflight, "DAV"));
        Assert.DoesNotContain(preflight.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A member's share's slot on a re-keyed vault as a restored backup would bring it back: it wraps the old vault
    /// key, so opening it is "just re-encrypted", a known credential whose vault can't be opened just now. (The owner's
    /// re-keying revokes her own shares on the vault, and a member's that has no other vault.)</summary>
    [Fact]
    public async Task A_busy_vault_gives_503_and_releases_the_permit()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "garten");
        var dav = await DavClient.NewAsync(Api, bob, vaults: new Dictionary<string, string> { ["garten"] = "write", ["Work"] = "write" });
        var garten = await alice.VaultIdAsync();
        var onGarten = SlotsOf(dav.Id) & Builders<BsonDocument>.Filter.Eq("vault", garten);
        var old = await SlotDocs.Find(onGarten).SingleAsync(Ct);
        await Api.Services.GetRequiredService<VaultCatalog>().RekeyAsync(garten, alice.User.Id, alice.UserKey, Ct);
        await SlotDocs.DeleteManyAsync(onGarten, Ct);
        await SlotDocs.InsertOneAsync(old, cancellationToken: Ct);
        var ip = DavClient.NewIp();

        for (var i = 0; i <= WrongKeyLimiter.PermitLimit + 4; i++)
        {
            using var busy = await dav.SendAsync(Propfind, "/dav/", ip: ip);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.NotNull(busy.Headers.RetryAfter);
            AssertPrivateWithoutCookie(busy);
        }

        using var wrong = await dav.SendWithPasswordAsync(Propfind, "/dav/", DavTokens.Format(DavTokens.New()), ip);
        AssertChallenge(wrong);
    }

    [Fact]
    public async Task No_credentials_get_401_with_the_Basic_challenge()
    {
        foreach (var path in new[] { "/dav", "/dav/", "/dav/Work/plan.md" })
        {
            using var response = await DavClient.SendAsync(Api, Propfind, path, authorization: null);

            AssertChallenge(response);
            AssertPrivateWithoutCookie(response);
        }

        foreach (var malformed in new[] { "Basic", "Basic !!!", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("no-colon")), "Digest username=\"x\"" })
        {
            using var response = await DavClient.SendAsync(Api, Propfind, "/dav/", malformed);
            AssertChallenge(response);
        }
    }

    /// <summary>A real server checks response headers: the challenge's realm, "Kumbu²", isn't ASCII, and goes out in
    /// ISO-8859-1, the encoding clients read a realm in.</summary>
    [Fact]
    public async Task The_challenge_goes_out_over_kestrel()
    {
        // A port of its own: DotSegmentTests keeps a Kestrel factory of its own running.
        await using var factory = app.WithWebHostBuilder(b => b.UseUrls("http://127.0.0.1:0"));
        factory.UseKestrel();
        factory.StartServer();
        var address = new Uri(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port, Ct);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"PROPFIND /dav/ HTTP/1.1\r\nHost: {address.Authority}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), Ct);
        using var reader = new StreamReader(stream, Encoding.Latin1);
        var response = await reader.ReadToEndAsync(Ct);

        Assert.StartsWith("HTTP/1.1 401", response, StringComparison.Ordinal);
        Assert.Contains($"WWW-Authenticate: {Challenge}\r\n", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_right_share_gets_through()
    {
        var dav = await DavClient.NewAsync(Api);

        foreach (var path in new[] { "/dav", "/dav/", "/dav/Work/plan.md" })
        {
            using var response = await dav.SendAsync(Propfind, path);

            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            AssertPrivateWithoutCookie(response);
        }

        // Typed in by hand: lower case, without hyphens, O for 0.
        using var typed = await dav.SendWithPasswordAsync(Propfind, "/dav/", dav.Password.Replace("-", "", StringComparison.Ordinal).Replace('0', 'O').ToLowerInvariant());
        Assert.Equal(HttpStatusCode.NotImplemented, typed.StatusCode);
    }

    [Fact]
    public async Task A_wrong_token_gets_401_and_counts_against_the_limiter()
    {
        var dav = await DavClient.NewAsync(Api);
        var ip = DavClient.NewIp();

        for (var i = 0; i < WrongKeyLimiter.PermitLimit; i++)
        {
            using var wrong = await dav.SendWithPasswordAsync(Propfind, "/dav/", DavTokens.Format(DavTokens.New()), ip);
            AssertChallenge(wrong);
        }

        using var limited = await dav.SendAsync(Propfind, "/dav/", ip: ip);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        AssertPrivateWithoutCookie(limited);

        using var elsewhere = await dav.SendAsync(Propfind, "/dav/");
        Assert.Equal(HttpStatusCode.NotImplemented, elsewhere.StatusCode);
    }

    [Fact]
    public async Task A_revoked_share_gets_401_and_does_not_use_up_the_limiter()
    {
        var revoked = await DavClient.NewAsync(Api);
        var dav = await DavClient.NewAsync(Api, revoked.Owner, label: "Laptop");
        using (var gone = await revoked.Owner.SendAsync(HttpMethod.Delete, $"/api/dav-shares/{revoked.Id}")) Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
        var ip = DavClient.NewIp();

        for (var i = 0; i < 30; i++)
        {
            using var refused = await revoked.SendAsync(Propfind, "/dav/", ip: ip);
            AssertChallenge(refused);
        }

        using var right = await dav.SendAsync(Propfind, "/dav/", ip: ip);
        Assert.Equal(HttpStatusCode.NotImplemented, right.StatusCode);
    }

    [Fact]
    public async Task Another_shares_username_gets_401()
    {
        var first = await DavClient.NewAsync(Api);
        var second = await DavClient.NewAsync(Api, first.Owner, label: "Laptop");
        var ip = DavClient.NewIp();

        for (var i = 0; i < WrongKeyLimiter.PermitLimit; i++)
        {
            using var crossed = await first.SendWithPasswordAsync(Propfind, "/dav/", second.Password, ip);
            AssertChallenge(crossed);
        }

        // Refused like a wrong token: counted.
        using var limited = await second.SendAsync(Propfind, "/dav/", ip: ip);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task An_expired_share_gets_401()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var timed = Api.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(time);
        }));
        var dav = await DavClient.NewAsync(timed, expiresInDays: 30);

        time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1));
        using (var before = await dav.SendAsync(Propfind, "/dav/")) Assert.Equal(HttpStatusCode.NotImplemented, before.StatusCode);
        time.Advance(TimeSpan.FromMinutes(1));

        using var expired = await dav.SendAsync(Propfind, "/dav/");
        AssertChallenge(expired);
    }

    [Fact]
    public async Task A_deactivated_user_gets_401()
    {
        var dav = await DavClient.NewAsync(Api);
        var admin = await RestClient.NewAsync(Api, vault: "Admin", role: Roles.Admin);

        Assert.True(await Api.Services.GetRequiredService<Admin>().SetActiveAsync(admin.User, dav.Owner.User.Id, false, Ct));

        using var refused = await dav.SendAsync(Propfind, "/dav/");
        AssertChallenge(refused);
    }

    /// <summary>A re-keying of the owner's vault revokes a share that had only that vault (it is made again from the web
    /// app): known, so refused without counting as a guess.</summary>
    [Fact]
    public async Task A_rekeyed_vault_is_gone_from_the_share()
    {
        var dav = await DavClient.NewAsync(Api);
        await Api.Services.GetRequiredService<VaultCatalog>().RekeyAsync(await dav.Owner.VaultIdAsync(), dav.Owner.User.Id, dav.Owner.UserKey, Ct);
        Assert.Equal(0, await SlotDocs.CountDocumentsAsync(SlotsOf(dav.Id), cancellationToken: Ct));
        var ip = DavClient.NewIp();

        for (var i = 0; i <= WrongKeyLimiter.PermitLimit; i++)
        {
            using var refused = await dav.SendAsync(Propfind, "/dav/", ip: ip);
            AssertChallenge(refused);
        }
    }

    /// <summary>A share whose username and password are right but whose slots are gone (and that nothing revoked) opens
    /// no vault: refused rather than let in with nothing, and not counted as a guess either.</summary>
    [Fact]
    public async Task A_share_that_opens_no_vault_gets_401_and_is_not_counted()
    {
        var dav = await DavClient.NewAsync(Api);
        await SlotDocs.DeleteManyAsync(SlotsOf(dav.Id), Ct);
        var ip = DavClient.NewIp();

        for (var i = 0; i <= WrongKeyLimiter.PermitLimit; i++)
        {
            using var refused = await dav.SendAsync(Propfind, "/dav/", ip: ip);
            AssertChallenge(refused);
        }
    }

    [Fact]
    public async Task Plain_http_gets_403_outside_Development()
    {
        await using var production = Api.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var dav = (await DavClient.NewAsync(Api)).On(production);

        foreach (var method in new[] { Propfind, Options })
        {
            using var plain = await DavClient.SendAsync(production, method, "/dav/", dav.Authorization, https: false);

            Assert.Equal(HttpStatusCode.Forbidden, plain.StatusCode);
            Assert.Equal("text/plain", plain.Content.Headers.ContentType?.MediaType);
            Assert.Equal("WebDAV needs HTTPS.", await plain.Content.ReadAsStringAsync(Ct));
            AssertPrivateWithoutCookie(plain);
        }

        using var https = await dav.SendAsync(Propfind, "/dav/");
        Assert.Equal(HttpStatusCode.NotImplemented, https.StatusCode);
    }

    [Fact]
    public async Task Bearer_is_not_accepted_on_dav()
    {
        var dav = await DavClient.NewAsync(Api);
        var key = await dav.Owner.KeyAsync("agent", KeyRight.Write);

        foreach (var bearer in new[] { key, KumbuAppFactory.Token })
        {
            using var refused = await DavClient.SendAsync(Api, Propfind, "/dav/", $"Bearer {bearer}");
            AssertChallenge(refused);
        }

        // Nor the session cookie.
        using var cookie = await DavClient.SendAsync(
            Api, Propfind, "/dav/", authorization: null, headers: new Dictionary<string, string> { ["Cookie"] = $"{CallerMiddleware.SessionCookie}={dav.Owner.Session}" });
        AssertChallenge(cookie);
    }

    [Fact]
    public async Task Basic_is_not_accepted_on_api_or_mcp()
    {
        var dav = await DavClient.NewAsync(Api);

        using var api = await DavClient.SendAsync(Api, HttpMethod.Get, "/api/dav-shares", dav.Authorization);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);

        using var mcp = await DavClient.SendAsync(
            Api, HttpMethod.Post, "/mcp", dav.Authorization,
            DavClient.Body("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""", "application/json"),
            new Dictionary<string, string> { ["Accept"] = "application/json, text/event-stream" });
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
        Assert.Equal("Bearer", Assert.Single(mcp.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task Dav_switched_off_answers_404()
    {
        await using var off = Api.WithWebHostBuilder(b => b.UseSetting("Kumbu:Dav:Enabled", "false"));
        var dav = (await DavClient.NewAsync(Api)).On(off);

        foreach (var method in new[] { Propfind, Options })
        {
            using var response = await dav.SendAsync(method, "/dav/");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.False(response.Headers.Contains("DAV"));
        }

        // The shares stay, and work again once it is on.
        using var on = await dav.On(Api).SendAsync(Propfind, "/dav/");
        Assert.Equal(HttpStatusCode.NotImplemented, on.StatusCode);
    }

    [Fact]
    public async Task The_prefix_is_matched_case_insensitively()
    {
        var dav = await DavClient.NewAsync(Api);

        using var anonymous = await DavClient.SendAsync(Api, Propfind, "/DAV/", authorization: null);
        AssertChallenge(anonymous);

        using var right = await dav.SendAsync(Propfind, "/Dav/Work/");
        Assert.Equal(HttpStatusCode.NotImplemented, right.StatusCode);

        using var options = await DavClient.SendAsync(Api, Options, "/DAV/", authorization: null);
        Assert.Equal("1, 2", Header(options, "DAV"));
    }

    [Fact]
    public async Task The_token_and_the_header_appear_in_no_log_line()
    {
        var log = new CapturedLog();
        await using var logged = Api.WithWebHostBuilder(b => b.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(log).AddFilter<CapturedLog>(null, LogLevel.Trace)));
        var dav = await DavClient.NewAsync(logged);
        var wrong = DavTokens.Format(DavTokens.New());

        using (var right = await dav.SendAsync(Propfind, "/dav/Work/")) Assert.Equal(HttpStatusCode.NotImplemented, right.StatusCode);
        using (var refused = await dav.SendWithPasswordAsync(Propfind, "/dav/Work/", wrong)) AssertChallenge(refused);

        Assert.Contains(log.Lines, l => l.StartsWith("Trace", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("/dav/Work/", StringComparison.Ordinal));
        var secrets = new[]
        {
            dav.Password, DavTokens.Normalise(dav.Password)!, dav.Authorization["Basic ".Length..], wrong, DavTokens.Normalise(wrong)!,
            DavClient.Basic(dav.Username, wrong)["Basic ".Length..],
        };
        // Counted, not asserted on the lines: a failure would print the secret.
        Assert.Equal(0, secrets.Sum(secret => log.Lines.Count(l => l.Contains(secret, StringComparison.OrdinalIgnoreCase))));
    }

    [Fact]
    public async Task Options_on_root_answers_the_DAV_headers()
    {
        using var response = await DavClient.SendAsync(Api, Options, "/", authorization: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1, 2", Header(response, "DAV"));
        Assert.Equal("DAV", Header(response, "MS-Author-Via"));
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Propfind_on_root_is_not_the_web_app()
    {
        using var response = await DavClient.SendAsync(Api, Propfind, "/", authorization: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>The caller a share resolves to: its label marked "(dav)", an agent's reference with the share's id (its
    /// writes are not the user's own hand), but a person's changeset timing, and refusals that speak of the share.</summary>
    [Fact]
    public async Task A_share_writes_as_its_label_marked_dav_at_a_persons_pace()
    {
        var dav = await DavClient.NewAsync(Api, right: "read");
        var resolved = await Api.Services.GetRequiredService<DavShares>().ResolveAsync(dav.Username, dav.Password, Ct);
        var catalog = Api.Services.GetRequiredService<VaultCatalog>();

        var caller = Assert.IsType<RequestCaller>(await RequestCaller.ForDavShareAsync(resolved.Share!, DavTokens.Normalise(dav.Password)!, catalog, Ct));

        Assert.Equal(CallerKind.DavShare, caller.Kind);
        Assert.Equal("Work PC (dav)", caller.Writer);
        Assert.Equal(WriterRef.ForOwner(WriterRef.Agent, dav.Id), caller.Reference);
        Assert.Equal(Changeset.HumanIdle, caller.ChangesetIdle);
        Assert.Equal(dav.Owner.User.Id, caller.User!.Id);
        Assert.Equal(AccessLevel.Read, caller.Level("Work"));
        Assert.Equal("this share can only read vault Work", caller.ReadOnlySentence("Work"));
        Assert.Equal("this share has no vault named Nowhere", caller.ReadOnlySentence("Nowhere"));
        Assert.Equal("This share is set to read; it takes a share with maintain.", caller.StructuralLimit("Work"));
        Assert.All(await caller.OpenVaultsAsync(Ct), v => Assert.False(v.MayPublish));
    }
}
