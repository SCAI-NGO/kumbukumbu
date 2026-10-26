// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.OAuth;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.OAuth;

/// <summary>
/// The <c>refresh_token</c> grant and revocation by the client (spec §5; Ruling 11): every refresh rotates both tokens
/// and the refresh token's slots, a retry within 30 seconds gets the same pair back, a reuse after that revokes the
/// connection's tokens, and <c>/oauth/revoke</c> lets the app end them.
/// </summary>
[Collection("app")]
public sealed class RefreshTests(KumbuAppFactory app)
{
    private const string Callback = "https://claude.test/cb";
    private const string Name = "cor.energy";
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private IMongoCollection<BsonDocument> Collection(string name) => app.Database.GetCollection<BsonDocument>(name);

    private WebApplicationFactory<Program> WithClock(FakeTimeProvider time) =>
        Api.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(time);
        }));

    private sealed record Pair(string Access, string Refresh, int ExpiresIn, string Scope);

    /// <summary>A user with vaults <c>Arbeit</c> (write) and <c>Privat</c> (read) on one connection, authorised by a
    /// fresh DCR client: its flow and first pair.</summary>
    private static async Task<(OAuthFlow Flow, Pair Pair)> ConnectedAsync(WebApplicationFactory<Program> api, int? expiresInDays = null)
    {
        var owner = await RestClient.NewAsync(api);
        using var second = await owner.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Privat" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        using var created = await owner.SendAsync(
            HttpMethod.Post, "/api/connections", new { name = Name, vaults = new Dictionary<string, string> { ["Arbeit"] = "write", ["Privat"] = "read" }, expiresInDays });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
        var flow = await OAuthFlow.StartAsync(api, owner, id, await OAuthFlow.RegisterAsync(api, Callback), Callback);
        await flow.AllowAsync();
        using var response = await flow.TokenAsync();
        return (flow, await PairOf(response));
    }

    private static async Task<Pair> PairOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var json = await RestClient.JsonAsync(response);
        return new Pair(
            json["access_token"]!.GetValue<string>(), json["refresh_token"]!.GetValue<string>(), json["expires_in"]!.GetValue<int>(), json["scope"]!.GetValue<string>());
    }

    private static async Task<Pair> RefreshedAsync(OAuthFlow flow, string refresh)
    {
        using var response = await flow.RefreshAsync(refresh);
        return await PairOf(response);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await RestClient.JsonAsync(response))["error"]!.GetValue<string>();
    }

    private static async Task<string> RefreshErrorAsync(OAuthFlow flow, string refresh, params (string, string?)[] form)
    {
        using var response = await flow.RefreshAsync(refresh, form);
        return await ErrorOf(response);
    }

    private static string Address() => $"198.25.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    /// <summary>The status of an MCP <c>initialize</c> on the connection's URL with <paramref name="access"/>.</summary>
    private static async Task<HttpStatusCode> InitializeAsync(WebApplicationFactory<Program> api, OAuthFlow flow, string access)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/c/{flow.ConnectionId}") { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add("X-Forwarded-For", Address());
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    private static async Task<JsonNode> ListedAsync(RestClient owner, string id)
    {
        using var response = await owner.GetAsync("/api/connections");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response)).AsArray().Single(c => c!["id"]!.GetValue<string>() == id)!;
    }

    private static async Task<(string State, string? Reason)> StateAsync(OAuthFlow flow)
    {
        var listed = await ListedAsync(flow.Owner, flow.ConnectionId);
        return (listed["state"]!.GetValue<string>(), listed["reason"]?.GetValue<string>());
    }

    /// <summary>The record of a token, found by its lookup value as the server finds it.</summary>
    private async Task<BsonDocument> RecordOfAsync(string token) =>
        await Collection("oauth_tokens").Find(new BsonDocument("lookup", new BsonBinaryData(OAuthTokens.Lookup(token)))).SingleAsync(Ct);

    private async Task<long> SlotsOfAsync(BsonDocument record) =>
        await Collection("slots").CountDocumentsAsync(new BsonDocument("token", record["_id"]), cancellationToken: Ct);

    [Fact]
    public async Task Refreshing_rotates_both_tokens()
    {
        var (flow, first) = await ConnectedAsync(Api);
        var oldRecord = await RecordOfAsync(first.Refresh);

        var second = await RefreshedAsync(flow, first.Refresh);

        Assert.NotEqual(first.Access, second.Access);
        Assert.NotEqual(first.Refresh, second.Refresh);
        Assert.StartsWith(OAuthTokens.AccessPrefix, second.Access, StringComparison.Ordinal);
        Assert.StartsWith(OAuthTokens.RefreshPrefix, second.Refresh, StringComparison.Ordinal);
        Assert.Equal(3600, second.ExpiresIn);
        Assert.Equal("kumbu:read kumbu:write", second.Scope);
        // The old access token lives out its hour; the new one works too.
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(Api, flow, first.Access));
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(Api, flow, second.Access));
        // The old refresh token's slots are gone; its record stays, spent, without its grant, for 30 days.
        var used = await RecordOfAsync(first.Refresh);
        Assert.Equal(0, await SlotsOfAsync(used));
        Assert.True(used.Contains("usedAt"));
        Assert.False(used.Contains("grant"));
        Assert.False(used.Contains("revokedAt"));
        Assert.Equal(used["usedAt"].ToUniversalTime().AddDays(30), used["purgeAt"].ToUniversalTime());
        Assert.Equal(oldRecord["_id"], used["_id"]);
        // The new tokens have slots of their own, two vaults each, and name the old record as their parent.
        var newRefresh = await RecordOfAsync(second.Refresh);
        var newAccess = await RecordOfAsync(second.Access);
        Assert.Equal(2, await SlotsOfAsync(newRefresh));
        Assert.Equal(2, await SlotsOfAsync(newAccess));
        Assert.Equal(oldRecord["_id"], newRefresh["parent"]);
        Assert.Equal(oldRecord["_id"], newAccess["parent"]);
        // The pair kept for a retry is sealed: neither token is anywhere in the clear.
        foreach (var name in new[] { "oauth_tokens", "slots", "connections" })
        {
            var docs = await Collection(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);
            Assert.DoesNotContain(docs, d => d.ToJson().Contains(second.Access, StringComparison.Ordinal) || d.ToJson().Contains(second.Refresh, StringComparison.Ordinal));
        }

        Assert.Equal(("connected", (string?)null), await StateAsync(flow));
    }

    [Fact]
    public async Task A_retry_within_the_grace_period_gets_the_same_pair()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = WithClock(time);
        var (flow, first) = await ConnectedAsync(api);

        var second = await RefreshedAsync(flow, first.Refresh);
        time.Advance(TimeSpan.FromSeconds(5));
        var retried = await RefreshedAsync(flow, first.Refresh);

        Assert.Equal(second.Access, retried.Access);
        Assert.Equal(second.Refresh, retried.Refresh);
        Assert.Equal(second.Scope, retried.Scope);
        Assert.Equal(3595, retried.ExpiresIn);
        Assert.Equal(("connected", (string?)null), await StateAsync(flow));
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(api, flow, second.Access));
        // And the pair it got back still rotates.
        await RefreshedAsync(flow, retried.Refresh);
    }

    [Fact]
    public async Task Parallel_refreshes_of_one_token_get_the_same_pair()
    {
        var (flow, first) = await ConnectedAsync(Api);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => flow.RefreshAsync(first.Refresh)));
        Pair[] pairs;
        try
        {
            pairs = await Task.WhenAll(responses.Select(PairOf));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        Assert.Single(pairs.Distinct());
        // One live refresh token, the one they all got, and the connection is untouched.
        var live = await Collection("oauth_tokens").Find(
            new BsonDocument { ["connection"] = flow.ConnectionId, ["kind"] = "refresh", ["usedAt"] = new BsonDocument("$exists", false), ["revokedAt"] = new BsonDocument("$exists", false) })
            .ToListAsync(Ct);
        Assert.Equal((await RecordOfAsync(pairs[0].Refresh))["_id"], Assert.Single(live)["_id"]);
        Assert.Equal(2, await Collection("oauth_tokens").CountDocumentsAsync(
            new BsonDocument { ["connection"] = flow.ConnectionId, ["kind"] = "access", ["revokedAt"] = new BsonDocument("$exists", false) }, cancellationToken: Ct));
        Assert.Equal(("connected", (string?)null), await StateAsync(flow));
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(Api, flow, pairs[0].Access));
    }

    [Fact]
    public async Task Reuse_after_the_grace_period_revokes_the_connection()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = WithClock(time);
        var (flow, first) = await ConnectedAsync(api);
        var before = await ListedAsync(flow.Owner, flow.ConnectionId);

        var second = await RefreshedAsync(flow, first.Refresh);
        time.Advance(TimeSpan.FromSeconds(31));
        var reused = await RefreshErrorAsync(flow, first.Refresh);

        Assert.Equal("invalid_grant", reused);
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(api, flow, second.Access));
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(api, flow, first.Access));
        Assert.Equal("invalid_grant", await RefreshErrorAsync(flow, second.Refresh));
        Assert.Equal(("needs_reconnect", ConnectionReasons.RefreshReused), await StateAsync(flow));
        Assert.Equal(0, await Collection("slots").CountDocumentsAsync(new BsonDocument("connection", flow.ConnectionId), cancellationToken: Ct));
        // The kept pair went with the revocation; the used record keeps its 30 days to recognise a later reuse.
        var used = await RecordOfAsync(first.Refresh);
        Assert.False(used.Contains("successor"));
        Assert.True(used.Contains("revokedAt"));
        Assert.Equal(used["usedAt"].ToUniversalTime().AddDays(30), used["purgeAt"].ToUniversalTime());

        // A new consent through the same URL connects it again, with the same name and rights.
        var again = await flow.AgainAsync();
        await again.AllowAsync();
        using var exchanged = await again.TokenAsync();
        var renewed = await PairOf(exchanged);
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(api, flow, renewed.Access));
        var after = await ListedAsync(flow.Owner, flow.ConnectionId);
        Assert.Equal("connected", after["state"]!.GetValue<string>());
        Assert.Equal(before["name"]!.GetValue<string>(), after["name"]!.GetValue<string>());
        Assert.Equal(before["vaults"]!.ToJsonString(), after["vaults"]!.ToJsonString());
    }

    [Fact]
    public async Task A_replaced_refresh_token_presented_again_revokes_the_new_family()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = WithClock(time);
        var (flow, first) = await ConnectedAsync(api);
        var again = await flow.AgainAsync();
        await again.AllowAsync();
        using var exchanged = await again.TokenAsync();
        var renewed = await PairOf(exchanged);

        // Within the grace period, a refresh still in flight when the family was replaced is only refused.
        Assert.Equal("invalid_grant", await RefreshErrorAsync(flow, first.Refresh));
        Assert.Equal(("connected", (string?)null), await StateAsync(flow));
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("invalid_grant", await RefreshErrorAsync(flow, first.Refresh));

        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(api, flow, renewed.Access));
        Assert.Equal(("needs_reconnect", ConnectionReasons.RefreshReused), await StateAsync(flow));
    }

    [Fact]
    public async Task A_revoked_refresh_token_is_refused_and_revokes_nothing_more()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = WithClock(time);
        var (flow, first) = await ConnectedAsync(api);
        await api.Services.GetRequiredService<Connections>().RevokeAllTokensAsync(flow.Owner.User, ConnectionReasons.PasswordChanged, Ct);
        time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal("invalid_grant", await RefreshErrorAsync(flow, first.Refresh));
        Assert.Equal(("needs_reconnect", ConnectionReasons.PasswordChanged), await StateAsync(flow));
    }

    [Fact]
    public async Task A_refresh_token_belongs_to_its_client()
    {
        var (flow, first) = await ConnectedAsync(Api);
        var other = await OAuthFlow.RegisterAsync(Api, Callback);

        Assert.Equal("invalid_grant", await RefreshErrorAsync(flow, first.Refresh, ("client_id", other)));

        // Nothing was revoked or spent: the right client still refreshes.
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(Api, flow, first.Access));
        Assert.Equal(("connected", (string?)null), await StateAsync(flow));
        await RefreshedAsync(flow, first.Refresh);
    }

    [Fact]
    public async Task A_sent_resource_must_be_the_connections()
    {
        var (flow, first) = await ConnectedAsync(Api);

        Assert.Equal("invalid_target", await RefreshErrorAsync(flow, first.Refresh, ("resource", OAuthFlow.ResourceOf("aaaaaaaaaaaaaaaa"))));
        using var malformed = await flow.RefreshAsync("kumbu_rt_short");
        Assert.Equal("invalid_grant", await ErrorOf(malformed));
        using var noToken = await flow.RefreshAsync(first.Refresh, ("refresh_token", null));
        Assert.Equal("invalid_request", await ErrorOf(noToken));

        // Still unspent; with the connection's own URL, and with a scope, it rotates.
        using var response = await flow.RefreshAsync(first.Refresh, ("resource", flow.Resource + "/"), ("scope", "kumbu:read kumbu:write"));
        await PairOf(response);
    }

    [Fact]
    public async Task Refresh_after_the_connection_expired_is_invalid_grant()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = WithClock(time);
        var (flow, first) = await ConnectedAsync(api, expiresInDays: 30);
        var connection = await Collection("connections").Find(new BsonDocument("_id", flow.ConnectionId)).SingleAsync(Ct);
        var expiresAt = connection["expiresAt"].ToUniversalTime();

        // The refresh token's slots carry the connection's expiry, so MongoDB's TTL index removes them then.
        var second = await RefreshedAsync(flow, first.Refresh);
        var record = await RecordOfAsync(second.Refresh);
        var slots = await Collection("slots").Find(new BsonDocument("token", record["_id"])).ToListAsync(Ct);
        Assert.Equal(expiresAt, record["expiresAt"].ToUniversalTime());
        Assert.Equal(2, slots.Count);
        Assert.All(slots, s => Assert.Equal(expiresAt, s["expiresAt"].ToUniversalTime()));

        time.Advance(TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1));

        Assert.Equal("invalid_grant", await RefreshErrorAsync(flow, second.Refresh));
    }

    [Fact]
    public async Task The_client_can_revoke()
    {
        var (flow, first) = await ConnectedAsync(Api);
        var (bystander, theirs) = await ConnectedAsync(Api);

        using (var random = await flow.RevokeAsync("not-a-token-" + Guid.NewGuid()))
        {
            Assert.Equal(HttpStatusCode.OK, random.StatusCode);
            Assert.True(random.Headers.CacheControl!.NoStore);
            Assert.Equal("*", random.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }

        Assert.Equal(("connected", (string?)null), await StateAsync(flow));
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(Api, flow, first.Access));

        using var revoked = await flow.RevokeAsync(first.Refresh, "refresh_token");

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.True(revoked.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(Api, flow, first.Access));
        Assert.Equal("invalid_grant", await RefreshErrorAsync(flow, first.Refresh));
        Assert.Equal(("needs_reconnect", ConnectionReasons.ClientRevoked), await StateAsync(flow));
        // Revoking again, or with the revoked access token, changes nothing and still answers 200.
        using (var twice = await flow.RevokeAsync(first.Refresh)) Assert.Equal(HttpStatusCode.OK, twice.StatusCode);
        using (var access = await flow.RevokeAsync(first.Access)) Assert.Equal(HttpStatusCode.OK, access.StatusCode);
        Assert.Equal(("needs_reconnect", ConnectionReasons.ClientRevoked), await StateAsync(flow));
        // Someone else's connection is untouched.
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(Api, bystander, theirs.Access));
    }

    [Fact]
    public async Task Revoking_an_access_token_revokes_only_it()
    {
        var (flow, first) = await ConnectedAsync(Api);

        using var revoked = await flow.RevokeAsync(first.Access, "access_token");

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(Api, flow, first.Access));
        var record = await RecordOfAsync(first.Access);
        Assert.True(record.Contains("revokedAt"));
        Assert.Equal(0, await SlotsOfAsync(record));
        Assert.Equal(("connected", (string?)null), await StateAsync(flow));
        var second = await RefreshedAsync(flow, first.Refresh);
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(Api, flow, second.Access));
    }

    [Fact]
    public async Task No_successor_outlives_its_use()
    {
        var (flow, first) = await ConnectedAsync(Api);

        var second = await RefreshedAsync(flow, first.Refresh);
        Assert.True((await RecordOfAsync(first.Refresh)).Contains("successor"));
        await RefreshedAsync(flow, second.Refresh);

        Assert.False((await RecordOfAsync(first.Refresh)).Contains("successor"));
        Assert.True((await RecordOfAsync(second.Refresh)).Contains("successor"));
        // Once its successor is used, a retry of the first token within the grace period has nothing to give back.
        Assert.Equal("invalid_grant", await RefreshErrorAsync(flow, first.Refresh));
        Assert.Equal(("connected", (string?)null), await StateAsync(flow));
    }
}
