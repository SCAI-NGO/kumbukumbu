// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.OAuth;

/// <summary>
/// The token endpoint and a connection's callers (spec §4, §5; Rulings 2, 11, 12, 18): a code exchanged once for an
/// access and a refresh token, each with slots of its own, and the access token accepted only on its connection's
/// URL, with exactly the consented rights, until it expires.
/// </summary>
[Collection("app")]
public sealed class TokenTests(KumbuAppFactory app)
{
    private const string Callback = "https://claude.test/cb";
    private const string Name = "Work";
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private IMongoCollection<BsonDocument> Collection(string name) => app.Database.GetCollection<BsonDocument>(name);

    /// <summary>The public app with a clock of the test's own, starting now.</summary>
    private WebApplicationFactory<Program> WithClock(FakeTimeProvider time) =>
        Api.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(time);
        }));

    /// <summary>A user with vaults <c>Arbeit</c> (write) and <c>Privat</c> (read) on one connection named <c>Work</c>.</summary>
    private static async Task<(RestClient Owner, string Id)> ConnectionAsync(WebApplicationFactory<Program> api, int? expiresInDays = null)
    {
        var owner = await RestClient.NewAsync(api);
        using var second = await owner.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Privat" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        using var created = await owner.SendAsync(
            HttpMethod.Post, "/api/connections", new { name = Name, vaults = new Dictionary<string, string> { ["Arbeit"] = "write", ["Privat"] = "read" }, expiresInDays });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (owner, (await RestClient.JsonAsync(created))["id"]!.GetValue<string>());
    }

    /// <summary>An authorisation of the connection by a fresh DCR client, allowed: the flow holds the code.</summary>
    private static async Task<OAuthFlow> AllowedAsync(WebApplicationFactory<Program> api, RestClient owner, string id)
    {
        var flow = await OAuthFlow.StartAsync(api, owner, id, await OAuthFlow.RegisterAsync(api, Callback), Callback);
        await flow.AllowAsync();
        return flow;
    }

    private static async Task<(string Access, string Refresh, JsonNode Answer)> ExchangeAsync(OAuthFlow flow)
    {
        using var response = await flow.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await RestClient.JsonAsync(response);
        return (json["access_token"]!.GetValue<string>(), json["refresh_token"]!.GetValue<string>(), json);
    }

    /// <summary>A connection of a new user, authorised: its flow and its tokens.</summary>
    private static async Task<(OAuthFlow Flow, string Access, string Refresh)> ConnectedAsync(WebApplicationFactory<Program> api)
    {
        var (owner, id) = await ConnectionAsync(api);
        var flow = await AllowedAsync(api, owner, id);
        var (access, refresh, _) = await ExchangeAsync(flow);
        return (flow, access, refresh);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response) => (await RestClient.JsonAsync(response))["error"]!.GetValue<string>();

    private static string Address() => $"198.24.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    /// <summary>An MCP <c>initialize</c> on <paramref name="path"/>, as a raw POST.</summary>
    private static async Task<HttpResponseMessage> InitializeAsync(
        WebApplicationFactory<Program> api, string path, string? bearer, string? address = null, string? cookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (cookie is not null) request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={cookie}");
        request.Headers.Add("X-Forwarded-For", address ?? Address());
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        return await client.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> GetAsync(WebApplicationFactory<Program> api, string path, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Forwarded-For", Address());
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        return await client.SendAsync(request, Ct);
    }

    /// <summary>A form POST to <c>/oauth/token</c> with exactly <paramref name="fields"/>.</summary>
    private static Task<HttpResponseMessage> TokenAsync(WebApplicationFactory<Program> api, params (string Name, string Value)[] fields) =>
        TokenFromAsync(api, Address(), fields);

    private static async Task<HttpResponseMessage> TokenFromAsync(WebApplicationFactory<Program> api, string address, params (string Name, string Value)[] fields)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
        {
            Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))),
        };
        request.Headers.Add("X-Forwarded-For", address);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        return await client.SendAsync(request, Ct);
    }

    private static string OwnAddress() => $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    private static string Challenge(HttpResponseMessage response) => response.Headers.GetValues("WWW-Authenticate").Single();

    /// <summary>The official SDK client on the connection's URL with the access token.</summary>
    private static async Task<McpClient> McpAsync(WebApplicationFactory<Program> api, string connectionId, string access)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(OAuthFlow.ResourceOf(connectionId)),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {access}", ["X-Forwarded-For"] = Address() },
            },
            api.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static Task<CallToolResult> ShAsync(McpClient client, string command) =>
        client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct).AsTask();

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    private static async Task<JsonNode> ListedAsync(RestClient owner, string id)
    {
        using var response = await owner.GetAsync("/api/connections");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response)).AsArray().Single(c => c!["id"]!.GetValue<string>() == id)!;
    }

    private async Task<BsonDocument> TokenRecordAsync(string connectionId, string kind) =>
        await Collection("oauth_tokens").Find(new BsonDocument { ["connection"] = connectionId, ["kind"] = kind }).SingleAsync(Ct);

    [Fact]
    public async Task Exchanges_a_code_for_tokens()
    {
        var (owner, id) = await ConnectionAsync(Api);
        var flow = await AllowedAsync(Api, owner, id);

        using var response = await flow.TokenAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("no-cache", response.Headers.Pragma.ToString());
        var json = await RestClient.JsonAsync(response);
        Assert.Equal("Bearer", json["token_type"]!.GetValue<string>());
        Assert.Equal(3600, json["expires_in"]!.GetValue<int>());
        Assert.StartsWith("kumbu_at_", json["access_token"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("kumbu_rt_", json["refresh_token"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("kumbu:read kumbu:write", json["scope"]!.GetValue<string>());
        var listed = await ListedAsync(owner, id);
        Assert.Equal("connected", listed["state"]!.GetValue<string>());
        Assert.Null(listed["reason"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_read_only_grant_answers_the_read_scope()
    {
        var (owner, id) = await ConnectionAsync(Api);
        var flow = await OAuthFlow.StartAsync(Api, owner, id, await OAuthFlow.RegisterAsync(Api, Callback), Callback, scope: "kumbu:read");
        await flow.AllowAsync();

        var (_, _, answer) = await ExchangeAsync(flow);

        Assert.Equal("kumbu:read", answer["scope"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_access_token_works_on_its_connection_url()
    {
        var (flow, access, _) = await ConnectedAsync(Api);

        await using var client = await McpAsync(Api, flow.ConnectionId, access);
        var ls = Text(await ShAsync(client, "ls /"));

        Assert.Equal(["Arbeit/", "Privat/"], ls.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Rights_are_exactly_as_consented()
    {
        var (flow, access, _) = await ConnectedAsync(Api);
        await using var client = await McpAsync(Api, flow.ConnectionId, access);

        var refused = await client.CallToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Privat/x.md", ["content"] = "x\n" }, cancellationToken: Ct);
        var written = await client.CallToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Arbeit/x.md", ["content"] = "x\n" }, cancellationToken: Ct);

        Assert.True(refused.IsError);
        Assert.Contains("can only read vault Privat", Text(refused), StringComparison.Ordinal);
        Assert.NotEqual(true, written.IsError);
        Assert.Contains(Name, Text(await ShAsync(client, "history /Arbeit/x.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_access_token_is_refused_elsewhere()
    {
        var (flow, access, refresh) = await ConnectedAsync(Api);
        using var other = await flow.Owner.SendAsync(
            HttpMethod.Post, "/api/connections", new { name = "other", vaults = new Dictionary<string, string> { ["Arbeit"] = "write" } });
        var otherId = (await RestClient.JsonAsync(other))["id"]!.GetValue<string>();

        using var mcp = await InitializeAsync(Api, "/mcp", access);
        using var api = await GetAsync(Api, "/api/vaults", access);
        using var elsewhere = await InitializeAsync(Api, $"/mcp/c/{otherId}", access);
        using var refreshOnOwn = await InitializeAsync(Api, $"/mcp/c/{flow.ConnectionId}", refresh);

        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
        Assert.Equal("Bearer", Challenge(mcp));
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, elsewhere.StatusCode);
        Assert.EndsWith("error=\"invalid_token\"", Challenge(elsewhere), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshOnOwn.StatusCode);
    }

    [Fact]
    public async Task Wrong_verifier_resource_client_or_redirect_is_invalid_grant()
    {
        var (owner, id) = await ConnectionAsync(Api);
        var flow = await AllowedAsync(Api, owner, id);
        using var other = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name = "other", vaults = new Dictionary<string, string> { ["Arbeit"] = "write" } });
        var otherResource = OAuthFlow.ResourceOf((await RestClient.JsonAsync(other))["id"]!.GetValue<string>());
        var otherClient = await OAuthFlow.RegisterAsync(Api, Callback);

        async Task<(HttpStatusCode, string)> Variation(params (string, string?)[] change)
        {
            var again = await flow.AgainAsync();
            await again.AllowAsync();
            using var response = await again.TokenAsync(change);
            return (response.StatusCode, response.IsSuccessStatusCode ? "" : await ErrorOf(response));
        }

        Assert.Equal((HttpStatusCode.BadRequest, "invalid_grant"), await Variation(("code_verifier", OAuthFlow.NewVerifier())));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_grant"), await Variation(("client_id", otherClient)));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_grant"), await Variation(("redirect_uri", Callback + "/other")));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_target"), await Variation(("resource", otherResource)));
        // Without a resource the code's own is meant (RFC 8707 makes it optional here).
        var (status, _) = await Variation(("resource", null));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task A_code_is_spent_by_the_first_request_that_presents_it()
    {
        var (owner, id) = await ConnectionAsync(Api);
        var flow = await AllowedAsync(Api, owner, id);

        using var wrong = await flow.TokenAsync(("code_verifier", OAuthFlow.NewVerifier()));
        using var right = await flow.TokenAsync();

        Assert.Equal("invalid_grant", await ErrorOf(wrong));
        Assert.Equal(HttpStatusCode.BadRequest, right.StatusCode);
        Assert.Equal("invalid_grant", await ErrorOf(right));
        Assert.Equal("waiting", (await ListedAsync(owner, id))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_reused_code_revokes_what_it_issued()
    {
        var (flow, access, _) = await ConnectedAsync(Api);

        using var again = await flow.TokenAsync();
        using var after = await InitializeAsync(Api, $"/mcp/c/{flow.ConnectionId}", access);

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_grant", await ErrorOf(again));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        var listed = await ListedAsync(flow.Owner, flow.ConnectionId);
        Assert.Equal("needs_reconnect", listed["state"]!.GetValue<string>());
        Assert.Equal(ConnectionReasons.CodeReused, listed["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = WithClock(time);
        var (owner, id) = await ConnectionAsync(api);
        var flow = await AllowedAsync(api, owner, id);

        time.Advance(TimeSpan.FromSeconds(61));
        using var response = await flow.TokenAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_grant", await ErrorOf(response));
    }

    [Fact]
    public async Task Json_bodies_and_unknown_grants_are_refused()
    {
        var (owner, id) = await ConnectionAsync(Api);
        var flow = await AllowedAsync(Api, owner, id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
        {
            Content = new StringContent(
                $$"""{"grant_type":"authorization_code","code":"{{flow.Code}}","redirect_uri":"{{Callback}}","client_id":"{{flow.ClientId}}","code_verifier":"{{flow.Verifier}}"}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Forwarded-For", Address());
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });

        using var json = await client.SendAsync(request, Ct);
        using var unknown = await flow.TokenAsync(("grant_type", "password"));
        using var twice = await TokenAsync(Api, ("grant_type", "authorization_code"), ("grant_type", "authorization_code"), ("client_id", flow.ClientId));
        // The code survived all of that: none of those requests reached it.
        using var exchanged = await flow.TokenAsync();

        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);
        Assert.Equal("invalid_request", await ErrorOf(json));
        Assert.True(json.Headers.CacheControl!.NoStore);
        Assert.Equal("unsupported_grant_type", await ErrorOf(unknown));
        Assert.Equal("invalid_request", await ErrorOf(twice));
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        Assert.Equal("*", exchanged.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Access_tokens_expire_and_their_slots_carry_the_ttl()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = WithClock(time);
        var (flow, access, _) = await ConnectedAsync(api);

        var record = await TokenRecordAsync(flow.ConnectionId, "access");
        var refreshRecord = await TokenRecordAsync(flow.ConnectionId, "refresh");
        var expiresAt = record["expiresAt"].ToUniversalTime();
        var slots = await Collection("slots").Find(new BsonDocument("token", record["_id"])).ToListAsync(Ct);
        var refreshSlots = await Collection("slots").Find(new BsonDocument("token", refreshRecord["_id"])).ToListAsync(Ct);
        Assert.InRange(expiresAt, time.GetUtcNow().UtcDateTime.AddMinutes(60).AddSeconds(-1), time.GetUtcNow().UtcDateTime.AddMinutes(60));
        Assert.Equal(expiresAt.AddHours(24), record["purgeAt"].ToUniversalTime());
        Assert.Equal(2, slots.Count);
        Assert.All(slots, s => Assert.Equal(expiresAt, s["expiresAt"].ToUniversalTime()));
        // The connection doesn't expire, nor do the refresh token's slots.
        Assert.Equal(2, refreshSlots.Count);
        Assert.All(refreshSlots, s => Assert.False(s.Contains("expiresAt")));
        // The code's slots are gone; its record stays, spent, to recognise a reuse.
        var code = await TokenRecordAsync(flow.ConnectionId, "code");
        Assert.True(code.Contains("usedAt"));
        Assert.Equal(0, await Collection("slots").CountDocumentsAsync(new BsonDocument("token", code["_id"]), cancellationToken: Ct));

        using (var before = await InitializeAsync(api, $"/mcp/c/{flow.ConnectionId}", access)) Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        time.Advance(TimeSpan.FromMinutes(61));
        using var after = await InitializeAsync(api, $"/mcp/c/{flow.ConnectionId}", access);

        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.EndsWith("error=\"invalid_token\"", Challenge(after), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_access_tokens_do_not_count_as_wrong_keys()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = WithClock(time);
        var (flow, access, _) = await ConnectedAsync(api);
        var path = $"/mcp/c/{flow.ConnectionId}";
        var address = $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";
        time.Advance(TimeSpan.FromMinutes(61));

        for (var i = 0; i < 25; i++)
        {
            using var expired = await InitializeAsync(api, path, access, address);
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        }

        var unknown = "kumbu_at_" + new string('A', 43);
        for (var i = 0; i < 20; i++)
        {
            using var wrong = await InitializeAsync(api, path, unknown, address);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var limited = await InitializeAsync(api, path, unknown, address);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task A_connection_expiry_caps_the_tokens()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = WithClock(time);
        var (owner, id) = await ConnectionAsync(api, expiresInDays: 30);

        time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromMinutes(10));
        var flow = await AllowedAsync(api, await owner.SignInAgainAsync(), id);
        var (_, _, answer) = await ExchangeAsync(flow);

        Assert.Equal(600, answer["expires_in"]!.GetValue<int>());
        var connection = await Collection("connections").Find(new BsonDocument("_id", id)).SingleAsync(Ct);
        Assert.Equal(connection["expiresAt"].ToUniversalTime(), (await TokenRecordAsync(id, "access"))["expiresAt"].ToUniversalTime());
        Assert.Equal(connection["expiresAt"].ToUniversalTime(), (await TokenRecordAsync(id, "refresh"))["expiresAt"].ToUniversalTime());
    }

    [Fact]
    public async Task Every_request_opens_the_slots_afresh()
    {
        var (flow, access, _) = await ConnectedAsync(Api);
        await using (var client = await McpAsync(Api, flow.ConnectionId, access))
        {
            Assert.Contains("Arbeit/", Text(await ShAsync(client, "ls /")), StringComparison.Ordinal);
        }

        var record = await TokenRecordAsync(flow.ConnectionId, "access");
        await Collection("slots").DeleteManyAsync(new BsonDocument("token", record["_id"]), Ct);
        using var after = await InitializeAsync(Api, $"/mcp/c/{flow.ConnectionId}", access);

        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.EndsWith("error=\"invalid_token\"", Challenge(after), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_failures_are_limited_per_client_and_network()
    {
        var client = "https://limited.test/" + Guid.NewGuid().ToString("N");
        var address = OwnAddress();
        (string, string)[] Bad(string id) =>
        [
            ("grant_type", "authorization_code"), ("code", "kumbu_ac_" + new string('B', 43)), ("redirect_uri", Callback), ("client_id", id),
            ("code_verifier", OAuthFlow.NewVerifier()),
        ];

        for (var i = 0; i < 20; i++)
        {
            using var bad = await TokenFromAsync(Api, address, Bad(client));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        using var tooMany = await TokenFromAsync(Api, address, Bad(client));
        // Someone else naming the same client id (say, claude.ai's) from another network isn't stopped by these.
        using var elsewhere = await TokenFromAsync(Api, OwnAddress(), Bad(client));

        Assert.Equal(HttpStatusCode.TooManyRequests, tooMany.StatusCode);
        Assert.True(tooMany.Headers.RetryAfter is not null);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
    }

    [Fact]
    public async Task Revoking_a_connection_tombstones_its_tokens_and_deletes_their_slots_but_not_an_api_keys()
    {
        var (flow, access, _) = await ConnectedAsync(Api);
        await flow.Owner.KeyAsync("laptop", KeyRight.Write);
        var keySlots = Builders<BsonDocument>.Filter.Eq("kind", "key") & Builders<BsonDocument>.Filter.Exists("apiKey")
            & Builders<BsonDocument>.Filter.In("vault", (await Collection("connections").Find(new BsonDocument("_id", flow.ConnectionId)).SingleAsync(Ct))["vaults"].AsBsonArray);
        var ofConnection = new BsonDocument("connection", flow.ConnectionId);
        Assert.Equal(3, await Collection("oauth_tokens").CountDocumentsAsync(ofConnection, cancellationToken: Ct)); // code, access, refresh
        Assert.Equal(4, await Collection("slots").CountDocumentsAsync(ofConnection, cancellationToken: Ct)); // two vaults, two tokens
        Assert.Equal(1, await Collection("slots").CountDocumentsAsync(keySlots, cancellationToken: Ct));

        using var revoked = await flow.Owner.SendAsync(HttpMethod.Delete, $"/api/connections/{flow.ConnectionId}");
        using var after = await InitializeAsync(Api, $"/mcp/c/{flow.ConnectionId}", access);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        await AssertTombstonesAsync(flow.ConnectionId, 3);
        Assert.Equal(0, await Collection("slots").CountDocumentsAsync(ofConnection, cancellationToken: Ct));
        Assert.Equal(1, await Collection("slots").CountDocumentsAsync(keySlots, cancellationToken: Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Revoking_all_tokens_leaves_the_connection_needing_a_reconnect()
    {
        var (flow, access, _) = await ConnectedAsync(Api);
        await flow.Owner.KeyAsync("laptop", KeyRight.Read);

        await Api.Services.GetRequiredService<Connections>().RevokeAllTokensAsync(flow.Owner.User, ConnectionReasons.PasswordChanged, Ct);
        using var after = await InitializeAsync(Api, $"/mcp/c/{flow.ConnectionId}", access);

        var ofConnection = new BsonDocument("connection", flow.ConnectionId);
        await AssertTombstonesAsync(flow.ConnectionId, 3);
        Assert.Equal(0, await Collection("slots").CountDocumentsAsync(ofConnection, cancellationToken: Ct));
        Assert.Equal(1, await Collection("slots").CountDocumentsAsync(new BsonDocument { ["kind"] = "key", ["apiKey"] = new BsonDocument("$exists", true), ["vault"] = (await Collection("connections").Find(new BsonDocument("_id", flow.ConnectionId)).SingleAsync(Ct))["vaults"][0] }, cancellationToken: Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        var listed = await ListedAsync(flow.Owner, flow.ConnectionId);
        Assert.Equal("needs_reconnect", listed["state"]!.GetValue<string>());
        Assert.Equal(ConnectionReasons.PasswordChanged, listed["reason"]!.GetValue<string>());

        // Reconnecting on the same URL makes it connected again, the reason gone.
        var renewed = await AllowedAsync(Api, flow.Owner, flow.ConnectionId);
        await ExchangeAsync(renewed);
        var connection = await Collection("connections").Find(new BsonDocument("_id", flow.ConnectionId)).SingleAsync(Ct);
        Assert.False(connection.Contains("tokensRevokedAt"));
        Assert.False(connection.Contains("tokensRevokedReason"));
        Assert.Equal("connected", (await ListedAsync(flow.Owner, flow.ConnectionId))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_second_exchange_replaces_the_connections_tokens()
    {
        var (flow, first, firstRefresh) = await ConnectedAsync(Api);

        var again = await flow.AgainAsync();
        await again.AllowAsync();
        var (second, _, _) = await ExchangeAsync(again);

        using var old = await InitializeAsync(Api, $"/mcp/c/{flow.ConnectionId}", first);
        using var current = await InitializeAsync(Api, $"/mcp/c/{flow.ConnectionId}", second);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        // One family per connection: one access and one refresh token, besides the two spent codes; the first
        // family's tokens are tombstones without slots.
        var records = await Collection("oauth_tokens").Find(new BsonDocument("connection", flow.ConnectionId)).ToListAsync(Ct);
        Assert.Equal(["access", "code", "code", "refresh"], records.Where(d => !d.Contains("revokedAt")).Select(d => d["kind"].AsString).Order(StringComparer.Ordinal));
        var tombstones = records.Where(d => d.Contains("revokedAt")).ToList();
        Assert.Equal(["access", "refresh"], tombstones.Select(d => d["kind"].AsString).Order(StringComparer.Ordinal));
        Assert.All(tombstones, d => Assert.False(d.Contains("grant")));
        Assert.Equal(0, await Collection("slots").CountDocumentsAsync(Builders<BsonDocument>.Filter.In("token", tombstones.Select(d => d["_id"])), cancellationToken: Ct));
        Assert.NotEqual(firstRefresh, second);
    }

    /// <summary>Every record of the connection is a tombstone: revoked, its lookup kept, no grant, gone within a day, no slots.</summary>
    private async Task AssertTombstonesAsync(string connectionId, int count)
    {
        var records = await Collection("oauth_tokens").Find(new BsonDocument("connection", connectionId)).ToListAsync(Ct);
        Assert.Equal(count, records.Count);
        Assert.All(records, d =>
        {
            Assert.True(d.Contains("revokedAt"));
            Assert.True(d.Contains("lookup"));
            Assert.False(d.Contains("grant"));
            Assert.InRange(d["purgeAt"].ToUniversalTime(), d["revokedAt"].ToUniversalTime().AddHours(24).AddSeconds(-1), d["revokedAt"].ToUniversalTime().AddHours(24).AddSeconds(1));
        });
        Assert.Equal(0, await Collection("slots").CountDocumentsAsync(new BsonDocument("connection", connectionId), cancellationToken: Ct));
    }

    [Fact]
    public async Task Revoked_access_tokens_do_not_count_as_wrong_keys()
    {
        var (flow, access, _) = await ConnectedAsync(Api);
        var address = OwnAddress();
        await Api.Services.GetRequiredService<Connections>().RevokeAllTokensAsync(flow.Owner.User, ConnectionReasons.PasswordChanged, Ct);
        await AssertTombstonesAsync(flow.ConnectionId, 3);

        for (var i = 0; i < 25; i++)
        {
            using var revoked = await InitializeAsync(Api, $"/mcp/c/{flow.ConnectionId}", access, address);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            Assert.EndsWith("error=\"invalid_token\"", Challenge(revoked), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_revoked_code_is_refused_and_revokes_nothing_more()
    {
        var (owner, id) = await ConnectionAsync(Api);
        var flow = await AllowedAsync(Api, owner, id);
        await Api.Services.GetRequiredService<Connections>().RevokeAllTokensAsync(owner.User, ConnectionReasons.PasswordChanged, Ct);

        using var response = await flow.TokenAsync();

        Assert.Equal("invalid_grant", await ErrorOf(response));
        Assert.Equal(ConnectionReasons.PasswordChanged, (await Collection("connections").Find(new BsonDocument("_id", id)).SingleAsync(Ct))["tokensRevokedReason"].AsString);
    }

    [Fact]
    public async Task A_changed_expiry_in_the_database_extends_nothing()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = WithClock(time);
        var (flow, access, _) = await ConnectedAsync(api);
        var record = await TokenRecordAsync(flow.ConnectionId, "access");
        var later = time.GetUtcNow().UtcDateTime.AddDays(1);
        await Collection("oauth_tokens").UpdateOneAsync(new BsonDocument("_id", record["_id"]), new BsonDocument("$set", new BsonDocument("expiresAt", later)), cancellationToken: Ct);
        await Collection("slots").UpdateManyAsync(new BsonDocument("token", record["_id"]), new BsonDocument("$set", new BsonDocument("expiresAt", later)), cancellationToken: Ct);

        using (var before = await InitializeAsync(api, $"/mcp/c/{flow.ConnectionId}", access)) Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        time.Advance(TimeSpan.FromMinutes(61));
        using var after = await InitializeAsync(api, $"/mcp/c/{flow.ConnectionId}", access);

        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.EndsWith("error=\"invalid_token\"", Challenge(after), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_exchanges_of_one_code_at_once_give_one_pair()
    {
        var (owner, id) = await ConnectionAsync(Api);
        var flow = await AllowedAsync(Api, owner, id);

        var responses = await Task.WhenAll(flow.TokenAsync(), flow.TokenAsync());

        try
        {
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            var refused = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("invalid_grant", await ErrorOf(refused));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public void Exchanges_and_pairs_print_no_secrets()
    {
        var exchange = new Kumbu.Server.OAuth.CodeExchange("kumbu_ac_secretcode", "client", Callback, "verifier-secret", null);
        var pair = new Kumbu.Server.OAuth.TokenPair("kumbu_at_secret", "kumbu_rt_secret", 3600, "kumbu:read");

        Assert.DoesNotContain("secret", exchange.ToString(), StringComparison.Ordinal);
        Assert.Contains("client", exchange.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", pair.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", new Kumbu.Server.OAuth.TokenResult(pair, null, null).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Path_edges_of_connection_urls()
    {
        var (flow, access, _) = await ConnectedAsync(Api);
        var key = await flow.Owner.KeyAsync("laptop", KeyRight.Write);

        // Routing matches the prefix case-insensitively, so the middleware does too; the id is lower-case only.
        using var upper = await InitializeAsync(Api, $"/MCP/C/{flow.ConnectionId}", access);
        using var upperId = await InitializeAsync(Api, $"/mcp/c/{flow.ConnectionId.ToUpperInvariant()}", access);
        using var bare = await InitializeAsync(Api, "/mcp/c", key);
        using var cx = await InitializeAsync(Api, "/mcp/cx", key);
        using var cookie = await InitializeAsync(Api, $"/mcp/c/{flow.ConnectionId}", bearer: null, cookie: flow.Owner.Session);

        Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, upperId.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        Assert.False(bare.Headers.Contains("WWW-Authenticate"));
        Assert.Equal(HttpStatusCode.NotFound, cx.StatusCode);
        Assert.False(cx.Headers.Contains("WWW-Authenticate"));
        Assert.Equal(HttpStatusCode.Unauthorized, cookie.StatusCode);
        Assert.DoesNotContain("invalid_token", Challenge(cookie), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_of_a_token_reaches_the_log_or_the_database()
    {
        var log = new CapturedLog();
        await using var api = Api.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var (flow, access, refresh) = await ConnectedAsync(api);
        await using (var client = await McpAsync(api, flow.ConnectionId, access)) await ShAsync(client, "ls /");

        foreach (var secret in new[] { flow.Code!, flow.Verifier, access, refresh })
        {
            Assert.DoesNotContain(log.Lines, l => l.Contains(secret, StringComparison.Ordinal));
            foreach (var name in new[] { "oauth_tokens", "slots", "connections" })
            {
                var docs = await Collection(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);
                Assert.DoesNotContain(docs, d => d.ToJson().Contains(secret, StringComparison.Ordinal));
            }
        }

        Assert.Contains(log.Lines, l => l.Contains("token", StringComparison.Ordinal) && l.Contains(flow.ConnectionId, StringComparison.Ordinal));
    }
}
