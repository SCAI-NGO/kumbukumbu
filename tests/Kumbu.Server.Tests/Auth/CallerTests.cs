// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Auth;

/// <summary>Every request resolves to a caller (a session, an API key or the dev token), or is refused.</summary>
[Collection("app")]
public sealed class CallerTests(KumbuAppFactory app)
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent InitializeBody() => new(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""",
        Encoding.UTF8,
        "application/json");

    private static HttpRequestMessage Initialize(string? bearer = null, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = InitializeBody() };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    private static async Task<CallToolResult> ShAsync(McpClient client, string command) =>
        await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct);

    /// <summary>A new user with one vault of their own, signed in: the user and their session's secret.</summary>
    private async Task<(User User, string Session)> SignedInWithVaultAsync(string vault)
    {
        var users = new UserCatalog(
            app.Database, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), app.Services.GetRequiredService<ApiKeys>(), TimeProvider.System, NullLogger<UserCatalog>.Instance);
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, Password, Roles.User, Ct);
        var (user, key, readAt) = (await users.TrySignInAsync(username, Password, Ct))!.Value;
        await app.Services.GetRequiredService<VaultCatalog>().CreateForUserAsync(vault, user.Id, key, Ct);
        return (user, await app.Services.GetRequiredService<Sessions>().StartAsync(user, key, readAt, Ct));
    }

    [Fact]
    public async Task A_session_cookie_opens_exactly_the_users_vaults()
    {
        var (user, session) = await SignedInWithVaultAsync("Mine");
        await SignedInWithVaultAsync("Theirs");
        await using var app2 = await MinimalAppAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");

        using var response = await app2.GetTestClient().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"Session {user.Username}: Mine", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_session_cookie_gets_past_the_caller_on_api()
    {
        var (_, session) = await SignedInWithVaultAsync("Mine");
        using var http = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/nothing-here");
        request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // MCP clients send bearer keys only. A cookie there would let a page on a sibling subdomain (same site, so
    // SameSite=Strict doesn't help) post a blind no-cors tools/call, e.g. rm, with the user's session.
    [Theory]
    [InlineData("/mcp")]
    [InlineData("/MCP")]
    public async Task A_valid_session_cookie_is_refused_on_mcp(string path)
    {
        var (_, session) = await SignedInWithVaultAsync("Mine");
        using var http = app.CreateClient();
        using var request = Initialize();
        request.RequestUri = new Uri(path, UriKind.Relative);
        request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task A_session_writes_as_the_users_username()
    {
        var (user, _) = await SignedInWithVaultAsync("Journal");
        var users = new UserCatalog(
            app.Database, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), app.Services.GetRequiredService<ApiKeys>(), TimeProvider.System, NullLogger<UserCatalog>.Instance);
        var (_, key, _) = (await users.TrySignInAsync(user.Username, Password, Ct))!.Value;
        var caller = RequestCaller.ForSession(user, key, app.Services.GetRequiredService<VaultCatalog>());

        Assert.Equal(CallerKind.Session, caller.Kind);
        Assert.Equal(user.Username, caller.Writer);
        Assert.Equal(["Journal"], (await caller.OpenVaultsAsync(Ct)).Select(v => v.Name));
    }

    // The web app will add MapFallbackToFile("index.html").AllowAnonymous(): it matches unmapped /api and /mcp
    // paths too, and must not answer them without a caller.
    [Theory]
    [InlineData("/api/nope", HttpStatusCode.Unauthorized)]
    [InlineData("/Api/nope", HttpStatusCode.Unauthorized)]
    [InlineData("/mcp/nope", HttpStatusCode.Unauthorized)]
    [InlineData("/api/open", HttpStatusCode.OK)]
    [InlineData("/some/page", HttpStatusCode.OK)]
    public async Task An_anonymous_fallback_does_not_open_api_or_mcp(string path, HttpStatusCode expected)
    {
        await using var app2 = await MinimalAppAsync();

        using var response = await app2.GetTestClient().GetAsync(path, Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>The caller middleware in a pipeline of test endpoints, over the app's services: an anonymous
    /// fallback as the web app will have, an anonymous /api endpoint, and one that shows the caller.</summary>
    private async Task<WebApplication> MinimalAppAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(app.Services.GetRequiredService<IOptions<KumbuOptions>>());
        builder.Services.AddSingleton(app.Services.GetRequiredService<Sessions>());
        builder.Services.AddSingleton(app.Services.GetRequiredService<ApiKeys>());
        builder.Services.AddSingleton(app.Services.GetRequiredService<VaultCatalog>());
        builder.Services.AddSingleton(new WrongKeyLimiter());
        var minimal = builder.Build();
        minimal.UseRouting();
        minimal.UseMiddleware<CallerMiddleware>();
        minimal.MapGet("/api/open", () => "open").AllowAnonymous();
        minimal.MapGet("/api/whoami", async (HttpContext context) =>
        {
            var caller = context.Features.Get<RequestCaller>()!;
            var vaults = await caller.OpenVaultsAsync(context.RequestAborted);
            return $"{caller.Kind} {caller.Writer}: {string.Join(",", vaults.Select(v => v.Name).Order())}";
        });
        minimal.MapFallback(() => "page").AllowAnonymous();
        await minimal.StartAsync(Ct);
        return minimal;
    }

    [Fact]
    public async Task An_unknown_session_cookie_is_401()
    {
        using var http = app.CreateClient();
        using var request = Initialize();
        request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task The_dev_token_opens_the_dev_token_vaults_as_today()
    {
        await using var client = await app.ConnectAsync();

        Assert.Contains("Demo/", Text(await ShAsync(client, "ls /")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_configured_dev_token_the_dev_token_is_refused()
    {
        await using var off = app.WithWebHostBuilder(b => b.UseSetting("Kumbu:DevToken", ""));
        using var http = off.CreateClient();
        using var request = Initialize(bearer: KumbuAppFactory.Token);

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_without_a_caller_is_401_with_a_json_error()
    {
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/API/vaults", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Api_with_the_dev_token_gets_past_the_caller()
    {
        using var http = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/nothing-here");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", KumbuAppFactory.Token);

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Paths_outside_mcp_and_api_need_no_caller()
    {
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/index.html", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Twenty_one_wrong_keys_in_a_minute_from_one_ip_are_429()
    {
        await using var proxied = app.WithWebHostBuilder(b => b.UseSetting("Kumbu:Proxy:Trusted", "true").UseSetting("Kumbu:PublicUrl", KumbuAppFactory.PublicUrl));
        using var http = proxied.CreateClient();
        const string ip = "203.0.113.7";

        for (var i = 0; i < 20; i++)
        {
            using var wrong = Initialize(bearer: $"kumbu_wrong{i}", forwardedFor: ip);
            using var refused = await http.SendAsync(wrong, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using var last = Initialize(bearer: "kumbu_wrong20", forwardedFor: ip);
        using var limited = await http.SendAsync(last, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);

        // Even the right key: a guess that happens to be right must not tell itself apart.
        using var right = Initialize(bearer: KumbuAppFactory.Token, forwardedFor: ip);
        using var stillLimited = await http.SendAsync(right, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, stillLimited.StatusCode);

        using var other = Initialize(bearer: KumbuAppFactory.Token, forwardedFor: "203.0.113.8");
        using var otherIp = await http.SendAsync(other, Ct);
        Assert.Equal(HttpStatusCode.OK, otherIp.StatusCode);
    }

    [Fact]
    public async Task Without_a_trusted_proxy_x_forwarded_for_does_not_pick_the_rate_limited_ip()
    {
        await using var direct = app.WithWebHostBuilder(_ => { });
        using var http = direct.CreateClient();

        for (var i = 0; i < 20; i++)
        {
            using var wrong = Initialize(bearer: $"kumbu_wrong{i}", forwardedFor: $"198.51.100.{i}");
            using var refused = await http.SendAsync(wrong, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using var last = Initialize(bearer: "kumbu_wrong20", forwardedFor: "198.51.100.200");
        using var limited = await http.SendAsync(last, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}
