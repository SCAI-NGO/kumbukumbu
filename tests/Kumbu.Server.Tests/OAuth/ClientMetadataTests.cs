// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json;
using Kumbu.Server.OAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.OAuth;

/// <summary>Client identification by metadata document (CIMD) and the guards around fetching it (Ruling 15).</summary>
public sealed class ClientMetadataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClientMetadataFetcher Fetcher(bool allowLoopback = true, TimeProvider? time = null, CapturedLog? log = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => { if (log is not null) b.AddProvider(log); });
        services.AddSingleton<IOptions<KumbuOptions>>(Options.Create(new KumbuOptions { OAuth = { AllowLoopbackClients = allowLoopback } }));
        services.AddSingleton(time ?? TimeProvider.System);
        services.AddClientMetadataFetcher();
        return services.BuildServiceProvider().GetRequiredService<ClientMetadataFetcher>();
    }

    private static string Document(string clientId, string? redirectUris = null, string? extra = null) =>
        $$"""{"client_id":{{JsonSerializer.Serialize(clientId)}},"client_name":"Claude","redirect_uris":{{redirectUris ?? """["https://claude.ai/api/mcp/auth_callback"]"""}}{{(extra is null ? "" : "," + extra)}}}""";

    /// <summary>A server whose document is built from its own address, which is not known before it starts.</summary>
    private static async Task<MetadataServer> ServerAsync(Func<string, MetadataResponse> response)
    {
        var server = await MetadataServer.StartAsync(new MetadataResponse());
        server.Response = () => response(server.ClientId);
        return server;
    }

    [Fact]
    public async Task Fetches_claudes_document_shape()
    {
        // Research §2: Claude's own document, with the client_id swapped for the local URL.
        await using var server = await ServerAsync(id => new MetadataResponse($$"""
            {"client_id":"{{id}}","client_name":"Claude","client_uri":"https://claude.ai",
             "redirect_uris":["https://claude.ai/api/mcp/auth_callback"],
             "grant_types":["authorization_code","refresh_token","urn:ietf:params:oauth:grant-type:jwt-bearer"],
             "response_types":["code"],"token_endpoint_auth_method":"none","logo_uri":"https://claude.ai/logo.png"}
            """));

        var result = await Fetcher().FetchAsync(server.ClientId, Ct);

        Assert.Null(result.Error);
        var client = Assert.IsType<OAuthClient>(result.Client);
        Assert.Equal(server.ClientId, client.ClientId);
        Assert.Equal("Claude", client.Name);
        Assert.Equal("127.0.0.1", client.Host);
        Assert.Equal(["https://claude.ai/api/mcp/auth_callback"], client.RedirectUris);
        Assert.True(client.Verified);
    }

    [Fact]
    public async Task Cleans_the_name()
    {
        await using var server = await ServerAsync(id => new MetadataResponse(
            $$"""{"client_id":"{{id}}","client_name":"  Cl\u0000au‮de\n{{new string('x', 200)}}","redirect_uris":["https://claude.ai/cb"]}"""));

        var client = (await Fetcher().FetchAsync(server.ClientId, Ct)).Client!;

        Assert.Equal(100, client.Name.Length);
        Assert.StartsWith("Claudexxx", client.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_a_document_whose_client_id_differs()
    {
        await using var server = await ServerAsync(_ => new MetadataResponse(Document("https://evil.example/client.json")));

        var result = await Fetcher().FetchAsync(server.ClientId, Ct);

        Assert.Null(result.Client);
        Assert.Equal("client_id_mismatch", result.Error);
    }

    [Fact]
    public async Task Refuses_redirects()
    {
        await using var server = await ServerAsync(_ => new MetadataResponse(RedirectTo: "/other"));

        var result = await Fetcher().FetchAsync(server.ClientId, Ct);

        Assert.Equal("redirected", result.Error);
        Assert.DoesNotContain("/other", server.Paths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refuses_oversized_documents(bool chunked)
    {
        await using var server = await ServerAsync(id => new MetadataResponse(
            Document(id, extra: $"\"pad\":\"{new string('a', 17 * 1024)}\""), Chunked: chunked));

        var result = await Fetcher().FetchAsync(server.ClientId, Ct);

        Assert.Equal("too_large", result.Error);
    }

    [Fact]
    public async Task Accepts_a_document_just_under_the_limit()
    {
        await using var server = await ServerAsync(id => new MetadataResponse(
            Document(id, extra: $"\"pad\":\"{new string('a', ClientMetadataFetcher.MaxBytes - 300)}\""), Chunked: true));

        var result = await Fetcher().FetchAsync(server.ClientId, Ct);

        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Gives_up_after_five_seconds()
    {
        await using var server = await ServerAsync(id => new MetadataResponse(Document(id), Delay: TimeSpan.FromSeconds(6)));
        var started = DateTime.UtcNow;

        var result = await Fetcher().FetchAsync(server.ClientId, Ct);

        Assert.Equal("unreachable", result.Error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task Refuses_bad_redirect_uris()
    {
        foreach (var (uris, error) in new[]
        {
            ("""["http://example.org/cb"]""", "bad_redirect_uri"),
            ("""["https://x/#f"]""", "bad_redirect_uri"),
            ("""["https://claude.ai/cb","not a uri"]""", "bad_redirect_uri"),
            ("""[42]""", "bad_redirect_uri"),
            ("""[]""", "no_redirect_uris"),
            ("""{"a":"b"}""", "no_redirect_uris"),
        })
        {
            await using var server = await ServerAsync(id => new MetadataResponse(Document(id, uris)));

            var result = await Fetcher().FetchAsync(server.ClientId, Ct);

            Assert.Equal(error, result.Error);
        }
    }

    [Fact]
    public async Task Accepts_loopback_http_redirects()
    {
        await using var server = await ServerAsync(id => new MetadataResponse(
            Document(id, """["http://localhost/callback","http://127.0.0.1/callback"]""")));

        var result = await Fetcher().FetchAsync(server.ClientId, Ct);

        Assert.Equal(["http://localhost/callback", "http://127.0.0.1/callback"], result.Client!.RedirectUris);
    }

    [Fact]
    public async Task Refuses_confidential_clients()
    {
        await using var server = await ServerAsync(id => new MetadataResponse(
            Document(id, extra: """ "token_endpoint_auth_method":"client_secret_basic" """)));

        var result = await Fetcher().FetchAsync(server.ClientId, Ct);

        Assert.Equal("not_public_client", result.Error);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2]")]
    [InlineData("""{"client_name":"x"}""")]
    public async Task Refuses_what_is_not_a_document(string body)
    {
        await using var server = await ServerAsync(_ => new MetadataResponse(body));

        var result = await Fetcher().FetchAsync(server.ClientId, Ct);

        Assert.NotNull(result.Error);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task A_server_error_is_unreachable()
    {
        await using var server = await ServerAsync(_ => new MetadataResponse(Status: 500));

        Assert.Equal("unreachable", (await Fetcher().FetchAsync(server.ClientId, Ct)).Error);
    }

    [Fact]
    public async Task Caches_by_max_age_within_bounds()
    {
        var time = new FakeTimeProvider();
        await using var server = await ServerAsync(id => new MetadataResponse(Document(id), Headers: new Dictionary<string, string> { ["Cache-Control"] = "max-age=10" }));
        var fetcher = Fetcher(time: time);

        Assert.Null((await fetcher.FetchAsync(server.ClientId, Ct)).Error);
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.Null((await fetcher.FetchAsync(server.ClientId, Ct)).Error);
        Assert.Equal(1, server.Requests);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null((await fetcher.FetchAsync(server.ClientId, Ct)).Error);
        Assert.Equal(2, server.Requests);
    }

    [Fact]
    public async Task Caches_for_an_hour_without_max_age_and_for_a_day_at_most()
    {
        var time = new FakeTimeProvider();
        await using var plain = await ServerAsync(id => new MetadataResponse(Document(id)));
        await using var huge = await ServerAsync(id => new MetadataResponse(Document(id), Headers: new Dictionary<string, string> { ["Cache-Control"] = "max-age=99999999" }));
        var fetcher = Fetcher(time: time);

        await fetcher.FetchAsync(plain.ClientId, Ct);
        await fetcher.FetchAsync(huge.ClientId, Ct);
        time.Advance(TimeSpan.FromMinutes(59));
        await fetcher.FetchAsync(plain.ClientId, Ct);
        Assert.Equal(1, plain.Requests);
        time.Advance(TimeSpan.FromMinutes(2));
        await fetcher.FetchAsync(plain.ClientId, Ct);
        Assert.Equal(2, plain.Requests);

        time.Advance(TimeSpan.FromHours(22));
        await fetcher.FetchAsync(huge.ClientId, Ct);
        Assert.Equal(1, huge.Requests);
        time.Advance(TimeSpan.FromHours(2));
        await fetcher.FetchAsync(huge.ClientId, Ct);
        Assert.Equal(2, huge.Requests);
    }

    [Fact]
    public async Task Failures_are_cached_for_a_minute()
    {
        var time = new FakeTimeProvider();
        await using var server = await ServerAsync(_ => new MetadataResponse(Status: 500));
        var fetcher = Fetcher(time: time);

        await fetcher.FetchAsync(server.ClientId, Ct);
        time.Advance(TimeSpan.FromSeconds(50));
        await fetcher.FetchAsync(server.ClientId, Ct);
        Assert.Equal(1, server.Requests);

        time.Advance(TimeSpan.FromSeconds(20));
        await fetcher.FetchAsync(server.ClientId, Ct);
        Assert.Equal(2, server.Requests);
    }

    [Fact]
    public async Task Without_the_option_loopback_is_refused()
    {
        await using var server = await ServerAsync(id => new MetadataResponse(Document(id)));

        var result = await Fetcher(allowLoopback: false).FetchAsync(server.ClientId, Ct);

        Assert.Equal("not_public", result.Error);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task Refuses_plain_http_to_other_hosts_and_private_literals_over_https()
    {
        var fetcher = Fetcher();

        Assert.Equal("not_public", (await fetcher.FetchAsync("http://example.org/client.json", Ct)).Error);
        Assert.Equal("not_public", (await fetcher.FetchAsync("https://169.254.169.254/client.json", Ct)).Error);
        Assert.Equal("not_public", (await fetcher.FetchAsync("https://[::ffff:10.0.0.1]/client.json", Ct)).Error);
    }

    [Fact]
    public async Task A_name_that_resolves_to_a_private_address_is_refused_at_connection()
    {
        // "localhost" is a name, not a literal: only the pinned connection can catch it.
        var result = await Fetcher(allowLoopback: false).FetchAsync("https://localhost:1/client.json", Ct);

        Assert.Equal("not_public", result.Error);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("0.1.2.3", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.51.100.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::127.0.0.1", false)]
    [InlineData("::10.0.0.1", false)]
    [InlineData("64:ff9b::7f00:1", false)]
    [InlineData("2002:7f00:1::", false)]
    [InlineData("2002:0a00:1::1", false)]
    [InlineData("2002:0101:0101::1", true)]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", false)]
    [InlineData("64:ff9b:1::1", false)]
    [InlineData("::ffff:0:127.0.0.1", false)]
    [InlineData("::ffff:0:10.0.0.1", false)]
    [InlineData("::ffff:0:1.1.1.1", true)]
    [InlineData("160.79.104.10", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("::ffff:1.1.1.1", true)]
    public void Private_and_special_addresses_are_not_public(string address, bool expected) =>
        Assert.Equal(expected, ClientMetadataFetcher.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public void Redirect_matching_is_exact_but_ignores_loopback_ports()
    {
        static OAuthClient Client(params string[] uris) => new("https://c.example/c.json", "C", "c.example", uris, true);

        var claude = Client("https://claude.ai/api/mcp/auth_callback");
        Assert.True(OAuthClients.RedirectMatches(claude, "https://claude.ai/api/mcp/auth_callback"));
        Assert.False(OAuthClients.RedirectMatches(claude, "https://claude.ai/api/mcp/auth_callback/"));
        Assert.False(OAuthClients.RedirectMatches(claude, "https://claude.ai/api/mcp/AUTH_callback"));
        Assert.False(OAuthClients.RedirectMatches(claude, "https://claude.ai:444/api/mcp/auth_callback"));
        Assert.False(OAuthClients.RedirectMatches(claude, "https://claude.ai/api/mcp/auth_callback?x=1"));

        var code = Client("http://localhost/callback", "http://127.0.0.1/callback");
        Assert.True(OAuthClients.RedirectMatches(code, "http://localhost:53122/callback"));
        Assert.False(OAuthClients.RedirectMatches(code, "http://localhost:53122/other"));
        Assert.False(OAuthClients.RedirectMatches(code, "http://[::1]:53122/callback"));
        Assert.False(OAuthClients.RedirectMatches(code, "https://localhost:53122/callback"));

        var numeric = Client("http://127.0.0.1:1/cb");
        Assert.True(OAuthClients.RedirectMatches(numeric, "http://127.0.0.1:9/cb"));

        // The port is ignored only for a loopback host, not for any http one.
        Assert.False(OAuthClients.RedirectMatches(Client("http://example.org/cb"), "http://example.org:81/cb"));
    }

    [Fact]
    public async Task A_non_url_client_id_resolves_to_nothing_for_now()
    {
        var clients = new OAuthClients(null!, Fetcher(), TimeProvider.System);

        Assert.Null(await clients.ResolveAsync("kumbu_client_abc", Ct));
        Assert.Null(await clients.ResolveAsync("", Ct));
    }

    [Fact]
    public async Task A_url_client_id_resolves_through_the_fetcher()
    {
        await using var server = await ServerAsync(id => new MetadataResponse(Document(id)));
        var clients = new OAuthClients(null!, Fetcher(), TimeProvider.System);

        var client = await clients.ResolveAsync(server.ClientId, Ct);

        Assert.Equal("Claude", client!.Name);
    }

    [Fact]
    public async Task Never_logs_the_document()
    {
        const string marker = "MARKER-7f3a9c-SECRET";
        var log = new CapturedLog();
        var fetcher = Fetcher(log: log);
        foreach (var body in new[]
        {
            $$"""{"client_id":"x","client_name":"{{marker}}","redirect_uris":["https://claude.ai/cb"]}""",
            $"not json {marker}",
            $$"""{"client_id":"ID","client_name":"{{marker}}","redirect_uris":["http://{{marker}}.example/cb"]}""",
        })
        {
            await using var server = await ServerAsync(id => new MetadataResponse(body.Replace("\"ID\"", $"\"{id}\"", StringComparison.Ordinal)));
            var result = await fetcher.FetchAsync(server.ClientId, Ct);
            Assert.NotNull(result.Error);
        }

        await using var good = await ServerAsync(id => new MetadataResponse(Document(id).Replace("Claude", marker, StringComparison.Ordinal)));
        Assert.Null((await fetcher.FetchAsync(good.ClientId, Ct)).Error);

        Assert.Contains(log.Lines, l => l.Contains(nameof(ClientMetadataFetcher), StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains(marker, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"client_id":"ID","client_name":"\ud800x","redirect_uris":["https://claude.ai/cb"]}""")]
    [InlineData("""{"client_id":"ID","client_name":"Claude","redirect_uris":["https://claude.ai/\ud800"]}""")]
    [InlineData("""{"client_id":"\ud800","client_name":"Claude","redirect_uris":["https://claude.ai/cb"]}""")]
    public async Task A_lone_surrogate_is_a_refusal_and_is_cached(string body)
    {
        var time = new FakeTimeProvider();
        await using var server = await ServerAsync(id => new MetadataResponse(body.Replace("\"ID\"", $"\"{id}\"", StringComparison.Ordinal)));
        var fetcher = Fetcher(time: time);

        Assert.Equal("not_json", (await fetcher.FetchAsync(server.ClientId, Ct)).Error);
        Assert.Equal("not_json", (await fetcher.FetchAsync(server.ClientId, Ct)).Error);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task Keeps_emoji_in_the_name()
    {
        await using var server = await ServerAsync(id => new MetadataResponse(
            $$"""{"client_id":"{{id}}","client_name":"Claude \ud83d\ude00","redirect_uris":["https://claude.ai/cb"]}"""));

        Assert.Equal("Claude \U0001F600", (await Fetcher().FetchAsync(server.ClientId, Ct)).Client!.Name);
    }

    [Fact]
    public void The_client_uses_no_proxy_cookies_or_redirects()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<KumbuOptions>>(Options.Create(new KumbuOptions()));
        services.AddSingleton(TimeProvider.System);
        services.AddClientMetadataFetcher();
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler? handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("cimd");
        while (handler is DelegatingHandler d) handler = d.InnerHandler;

        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.UseProxy);
        Assert.False(sockets.UseCookies);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.NotNull(sockets.ConnectCallback);
        Assert.Equal(ClientMetadataFetcher.Timeout, sockets.ConnectTimeout);
    }
}
