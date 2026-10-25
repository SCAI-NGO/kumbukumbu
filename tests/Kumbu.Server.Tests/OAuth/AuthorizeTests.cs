// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.OAuth;

/// <summary>
/// The authorisation endpoint and the consent API (spec §4, §8; Rulings 3, 7, 8): an anonymous request stored
/// server-side, a decision bound to the signed-in owner's session and to that request, used once, and a one-time code
/// that wraps the vault keys as slots for a minute.
/// </summary>
[Collection("app")]
public sealed class AuthorizeTests(KumbuAppFactory app)
{
    private const string Callback = "https://claude.test/cb";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>A user with vaults <c>Arbeit</c> (and <c>Privat</c> if asked), a connection named <c>claude</c> on them, and a DCR client.</summary>
    private async Task<(RestClient Owner, string ConnectionId, string ClientId)> ConnectionAsync(string right = "write", bool twoVaults = false)
    {
        var owner = await RestClient.NewAsync(Api);
        var vaults = new Dictionary<string, string> { [owner.Vault] = right };
        if (twoVaults)
        {
            using var second = await owner.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Privat" });
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            vaults["Privat"] = "read";
        }

        using var created = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name = "claude", vaults });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
        return (owner, id, await OAuthFlow.RegisterAsync(Api, Callback));
    }

    private async Task<OAuthFlow> FlowAsync(string? scope = null, string right = "write", bool twoVaults = false)
    {
        var (owner, id, client) = await ConnectionAsync(right, twoVaults);
        return await OAuthFlow.StartAsync(Api, owner, id, client, Callback, scope);
    }

    private static string Location(HttpResponseMessage response) => response.Headers.Location!.OriginalString;

    private IMongoCollection<BsonDocument> Collection(string name) => app.Database.GetCollection<BsonDocument>(name);

    private static void AssertRedirectedError(HttpResponseMessage response, OAuthFlow flow, string error)
    {
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var location = Location(response);
        Assert.StartsWith(Callback + "?", location, StringComparison.Ordinal);
        var query = OAuthFlow.QueryOf(location);
        Assert.Equal(error, query["error"]);
        Assert.False(string.IsNullOrEmpty(query["error_description"]));
        Assert.Equal(flow.State, query["state"]);
        Assert.Equal(OAuthFlow.Issuer, query["iss"]);
        Assert.False(query.ContainsKey("code"));
    }

    [Fact]
    public async Task Allow_redirects_with_code_state_and_iss()
    {
        var flow = await FlowAsync();

        var redirect = await flow.RedirectAfterAsync(allow: true);

        Assert.StartsWith(Callback + "?", redirect, StringComparison.Ordinal);
        var query = OAuthFlow.QueryOf(redirect);
        Assert.StartsWith("kumbu_ac_", query["code"], StringComparison.Ordinal);
        Assert.Equal("kumbu_ac_".Length + 43, query["code"].Length);
        Assert.Equal(flow.State, query["state"]);
        Assert.Equal("https://kumbu.test", query["iss"]);

        // The client is sealed into the connection's meta, and the registration no longer expires (Ruling 14).
        using var list = await flow.Owner.GetAsync("/api/connections");
        var client = (await RestClient.JsonAsync(list)).AsArray().Single()!["client"]!;
        Assert.Equal("Test app", client["name"]!.GetValue<string>());
        Assert.Equal("claude.test", client["host"]!.GetValue<string>());
        Assert.False(client["verified"]!.GetValue<bool>());
        var registered = await Collection("oauth_clients").Find(new BsonDocument("_id", flow.ClientId)).SingleAsync(Ct);
        Assert.False(registered.Contains("expiresAt"));
    }

    [Fact]
    public async Task A_redirect_uri_with_a_query_keeps_it()
    {
        var (owner, id, _) = await ConnectionAsync();
        var client = await OAuthFlow.RegisterAsync(Api, Callback + "?app=1");
        var flow = await OAuthFlow.StartAsync(Api, owner, id, client, Callback + "?app=1");

        var redirect = await flow.RedirectAfterAsync(allow: true);

        Assert.StartsWith(Callback + "?app=1&", redirect, StringComparison.Ordinal);
        Assert.Equal("1", OAuthFlow.QueryOf(redirect)["app"]);
    }

    [Fact]
    public async Task Deny_redirects_with_access_denied()
    {
        var flow = await FlowAsync();

        var redirect = await flow.RedirectAfterAsync(allow: false);

        Assert.StartsWith(Callback + "?", redirect, StringComparison.Ordinal);
        var query = OAuthFlow.QueryOf(redirect);
        Assert.Equal("access_denied", query["error"]);
        Assert.Equal(flow.State, query["state"]);
        Assert.Equal(OAuthFlow.Issuer, query["iss"]);
        Assert.False(query.ContainsKey("code"));
        Assert.Equal(0, await Collection("oauth_tokens").CountDocumentsAsync(new BsonDocument("connection", flow.ConnectionId), cancellationToken: Ct));
        Assert.Equal(0, await Collection("slots").CountDocumentsAsync(new BsonDocument("connection", flow.ConnectionId), cancellationToken: Ct));
    }

    [Fact]
    public async Task Plain_pkce_is_refused()
    {
        var flow = await FlowAsync();

        foreach (var change in new (string, string?)[] { ("code_challenge_method", "plain"), ("code_challenge_method", null), ("code_challenge", null), ("code_challenge", "short") })
        {
            using var response = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(change));
            AssertRedirectedError(response, flow, "invalid_request");
        }
    }

    [Fact]
    public async Task Other_response_types_are_refused()
    {
        var flow = await FlowAsync();

        using var token = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(("response_type", "token")));
        using var missing = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(("response_type", null)));

        AssertRedirectedError(token, flow, "unsupported_response_type");
        AssertRedirectedError(missing, flow, "unsupported_response_type");
    }

    [Fact]
    public async Task A_missing_state_is_left_out_of_the_redirect()
    {
        var flow = await FlowAsync();

        using var response = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(("state", null), ("code_challenge_method", "plain")));

        var query = OAuthFlow.QueryOf(Location(response));
        Assert.Equal("invalid_request", query["error"]);
        Assert.False(query.ContainsKey("state"));
        Assert.Equal(OAuthFlow.Issuer, query["iss"]);
    }

    [Fact]
    public async Task Unknown_client_and_wrong_redirect_stay_on_our_page()
    {
        var flow = await FlowAsync();

        using var unknown = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(("client_id", "kumbu_client_" + new string('x', 22))));
        using var garbage = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(("client_id", "not a client")));
        using var noClient = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(("client_id", null)));
        using var evil = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(("redirect_uri", "https://evil.test/cb")));
        using var noRedirect = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(("redirect_uri", null)));
        // Even with every other parameter wrong too, nothing goes to an untrusted redirect URI.
        using var evilAndBroken = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(("redirect_uri", "https://evil.test/cb"), ("response_type", "token"), ("code_challenge", null)));

        foreach (var response in new[] { unknown, garbage, noClient })
        {
            Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
            Assert.Equal("/consent?error=unknown_client", Location(response));
        }

        foreach (var response in new[] { evil, noRedirect, evilAndBroken })
        {
            Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
            Assert.Equal("/consent?error=invalid_redirect_uri", Location(response));
        }
    }

    [Fact]
    public async Task Repeated_parameters_are_refused()
    {
        var flow = await FlowAsync();
        var parameters = flow.Parameters().Where(p => p.Value is not null).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}");

        async Task<HttpResponseMessage> WithTwice(string name, string value)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/oauth/authorize?" + string.Join('&', parameters) + $"&{name}={Uri.EscapeDataString(value)}");
            using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
            return await client.SendAsync(request, Ct);
        }

        using var twoClients = await WithTwice("client_id", flow.ClientId);
        using var twoRedirects = await WithTwice("redirect_uri", "https://evil.test/cb");
        using var twoChallenges = await WithTwice("code_challenge", OAuthFlow.ChallengeOf(OAuthFlow.NewVerifier()));

        Assert.Equal("/consent?error=unknown_client", Location(twoClients));
        Assert.Equal("/consent?error=invalid_redirect_uri", Location(twoRedirects));
        Assert.Equal("invalid_request", OAuthFlow.QueryOf(Location(twoChallenges))["error"]);
    }

    [Fact]
    public async Task Plain_mcp_as_resource_explains_connections()
    {
        var (owner, id, client) = await ConnectionAsync();
        var start = await OAuthFlow.StartAsync(Api, owner, id, client, Callback);

        await AssertNotAConnectionAsync(await start.AgainAsync(("resource", "https://kumbu.test/mcp")));
    }

    [Fact]
    public async Task Missing_resource_is_not_a_connection()
    {
        var (owner, id, client) = await ConnectionAsync();
        var start = await OAuthFlow.StartAsync(Api, owner, id, client, Callback);

        await AssertNotAConnectionAsync(await start.AgainAsync(("resource", null)));
        await AssertNotAConnectionAsync(await start.AgainAsync(("resource", "https://kumbu.test/mcp/c/aaaaaaaaaaaaaaaa")));
        await AssertNotAConnectionAsync(await start.AgainAsync(("resource", $"https://elsewhere.test/mcp/c/{id}")));
    }

    private static async Task AssertNotAConnectionAsync(OAuthFlow flow)
    {
        var view = await flow.ConsentAsync();
        Assert.Equal("not_a_connection", view.Problem);
        Assert.Null(view.Connection);
        Assert.False(view.Renewal);
        Assert.Equal("claude.test", view.RedirectHost);
        Assert.Equal("Test app", view.Client.Name);

        using var allow = await flow.DecideAsync(allow: true);
        Assert.Equal(HttpStatusCode.Conflict, allow.StatusCode);

        // Allow didn't consume it: "Back to claude.test" still redirects, with invalid_target.
        var redirect = OAuthFlow.QueryOf(await flow.RedirectAfterAsync(allow: false));
        Assert.Equal("invalid_target", redirect["error"]);
        Assert.Equal(flow.State, redirect["state"]);
        Assert.Equal(OAuthFlow.Issuer, redirect["iss"]);
    }

    [Fact]
    public async Task Another_users_connection_is_not_shown()
    {
        var flow = await FlowAsync(twoVaults: true);
        var other = await RestClient.NewAsync(Api, vault: "Fremd");

        using var response = await flow.GetConsentAsync(other);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var view = await flow.ConsentAsync(other);

        Assert.Equal("other_user", view.Problem);
        Assert.Null(view.Connection);
        Assert.False(view.Renewal);
        Assert.DoesNotContain("claude\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Arbeit", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Privat", body, StringComparison.Ordinal);
        Assert.DoesNotContain(flow.ConnectionId, body, StringComparison.Ordinal);

        using var allow = await flow.DecideAsync(allow: true, other);
        Assert.Equal(HttpStatusCode.Conflict, allow.StatusCode);
        Assert.Equal(0, await Collection("oauth_tokens").CountDocumentsAsync(new BsonDocument("connection", flow.ConnectionId), cancellationToken: Ct));

        // The owner can still decide: the other user's Allow didn't consume the request.
        Assert.StartsWith("kumbu_ac_", await flow.AllowAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_consent_view_shows_the_connection()
    {
        var flow = await FlowAsync(twoVaults: true);

        var view = await flow.ConsentAsync();

        Assert.Equal(flow.RequestId, view.RequestId);
        Assert.Null(view.Problem);
        Assert.False(view.Renewal);
        Assert.Equal(new Kumbu.Server.Api.ClientView("Test app", "claude.test", false), view.Client);
        Assert.Equal("claude.test", view.RedirectHost);
        Assert.NotNull(view.Connection);
        Assert.Equal("claude", view.Connection.Name);
        Assert.Equal(new Dictionary<string, string> { ["Arbeit"] = "write", ["Privat"] = "read" }, view.Connection.Vaults);
        Assert.Null(view.Connection.ExpiresAt);
        Assert.Null(view.Connection.CurrentClient);
    }

    [Fact]
    public async Task A_second_consent_is_a_renewal()
    {
        var first = await FlowAsync();
        await first.AllowAsync();

        var second = await OAuthFlow.StartAsync(Api, first.Owner, first.ConnectionId, first.ClientId, Callback);
        var view = await second.ConsentAsync();

        Assert.True(view.Renewal);
        Assert.Null(view.Problem);
        Assert.Equal(new Kumbu.Server.Api.ClientView("Test app", "claude.test", false), view.Connection!.CurrentClient);
    }

    [Fact]
    public async Task Read_scope_caps_the_rights_shown()
    {
        var (owner, id, client) = await ConnectionAsync(twoVaults: true);

        var read = await OAuthFlow.StartAsync(Api, owner, id, client, Callback, "kumbu:read");
        var both = await OAuthFlow.StartAsync(Api, owner, id, client, Callback, "kumbu:read kumbu:write offline_access");
        var none = await OAuthFlow.StartAsync(Api, owner, id, client, Callback, "openid");

        Assert.All((await read.ConsentAsync()).Connection!.Vaults.Values, r => Assert.Equal("read", r));
        Assert.Equal("write", (await both.ConsentAsync()).Connection!.Vaults["Arbeit"]);
        Assert.Equal("write", (await none.ConsentAsync()).Connection!.Vaults["Arbeit"]);

        // The code's slots carry the capped right too.
        var code = await read.AllowAsync();
        var codeId = (await Collection("oauth_tokens").Find(new BsonDocument("connection", id)).SingleAsync(Ct))["_id"].AsString;
        foreach (var slot in await Collection("slots").Find(new BsonDocument("token", codeId)).ToListAsync(Ct))
        {
            Assert.Equal("read", OpenSlot(slot, code, id, codeId).GetProperty("rights").GetString());
        }
    }

    [Fact]
    public async Task A_request_is_used_once()
    {
        var flow = await FlowAsync();
        await flow.AllowAsync();

        using var again = await flow.DecideAsync(allow: true);
        using var deny = await flow.DecideAsync(allow: false);
        using var view = await flow.GetConsentAsync();

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("This request has expired; start the connection again in the app.", await RestClient.ErrorAsync(again));
        Assert.Equal(HttpStatusCode.NotFound, deny.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, view.StatusCode);
        Assert.Equal(1, await Collection("oauth_tokens").CountDocumentsAsync(new BsonDocument("connection", flow.ConnectionId), cancellationToken: Ct));
    }

    [Fact]
    public async Task Unknown_and_expired_requests_are_404()
    {
        var flow = await FlowAsync();
        var owner = flow.Owner;

        using var unknown = await owner.GetAsync("/api/oauth/requests/" + new string('0', 32));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Ten minutes, checked in code: the TTL index only tidies up.
        await Collection("oauth_requests").UpdateOneAsync(
            new BsonDocument("_id", flow.RequestId), new BsonDocument("$set", new BsonDocument("expiresAt", DateTime.UtcNow.AddSeconds(-1))), cancellationToken: Ct);
        using var expired = await flow.GetConsentAsync();
        using var decision = await flow.DecideAsync(allow: true);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal("This request has expired; start the connection again in the app.", await RestClient.ErrorAsync(expired));
        Assert.Equal(HttpStatusCode.NotFound, decision.StatusCode);
    }

    [Fact]
    public async Task A_client_that_no_longer_resolves_is_said_so()
    {
        var flow = await FlowAsync();
        await Collection("oauth_clients").DeleteOneAsync(new BsonDocument("_id", flow.ClientId), Ct);

        using var view = await flow.GetConsentAsync();

        Assert.Equal(HttpStatusCode.NotFound, view.StatusCode);
        Assert.Equal("The app could not be identified any more; start the connection again in the app.", await RestClient.ErrorAsync(view));
    }

    [Fact]
    public async Task Requests_expire_after_ten_minutes()
    {
        var flow = await FlowAsync();

        var request = await Collection("oauth_requests").Find(new BsonDocument("_id", flow.RequestId)).SingleAsync(Ct);

        Assert.InRange(request["expiresAt"].ToUniversalTime(), DateTime.UtcNow.AddMinutes(10).AddSeconds(-30), DateTime.UtcNow.AddMinutes(10).AddSeconds(1));
        Assert.Equal(flow.ConnectionId, request["connection"].AsString);
        var indexes = await (await Collection("oauth_requests").Indexes.ListAsync(Ct)).ToListAsync(Ct);
        Assert.Contains(indexes, i => i["key"].AsBsonDocument.Contains("expiresAt") && i.Contains("expireAfterSeconds"));
    }

    [Fact]
    public async Task The_decision_needs_our_origin_and_a_session()
    {
        var flow = await FlowAsync();
        var key = await flow.Owner.KeyAsync("laptop", KeyRight.Write);
        var path = $"/api/oauth/requests/{flow.RequestId}/decision";

        async Task<HttpResponseMessage> PostAsync(string? cookie, string? origin, string? bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("""{"allow":true}""", Encoding.UTF8, "application/json") };
            if (cookie is not null) request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={cookie}");
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Add("X-Forwarded-For", $"198.24.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
            using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
            return await client.SendAsync(request, Ct);
        }

        using var noOrigin = await PostAsync(flow.Owner.Session, origin: null, bearer: null);
        using var foreignOrigin = await PostAsync(flow.Owner.Session, origin: "https://claude.test", bearer: null);
        using var apiKey = await PostAsync(cookie: null, origin: null, bearer: key);
        using var noCookie = await PostAsync(cookie: null, origin: RestClient.Origin, bearer: null);
        using var apiKeyRead = await flow.Owner.GetAsync($"/api/oauth/requests/{flow.RequestId}", bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, noOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, apiKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noCookie.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, apiKeyRead.StatusCode);
        Assert.Equal(0, await Collection("oauth_tokens").CountDocumentsAsync(new BsonDocument("connection", flow.ConnectionId), cancellationToken: Ct));

        // None of them consumed the request.
        Assert.StartsWith("kumbu_ac_", await flow.AllowAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoked_and_expired_connections_cannot_be_allowed()
    {
        var revoked = await FlowAsync();
        using (var revoke = await revoked.Owner.SendAsync(HttpMethod.Delete, $"/api/connections/{revoked.ConnectionId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        var expired = await FlowAsync();
        await Collection("connections").UpdateOneAsync(
            new BsonDocument("_id", expired.ConnectionId), new BsonDocument("$set", new BsonDocument("expiresAt", DateTime.UtcNow.AddMinutes(-1))), cancellationToken: Ct);

        foreach (var (flow, problem) in new[] { (revoked, "revoked"), (expired, "expired") })
        {
            var view = await flow.ConsentAsync();
            Assert.Equal(problem, view.Problem);
            using var allow = await flow.DecideAsync(allow: true);
            Assert.Equal(HttpStatusCode.Conflict, allow.StatusCode);
            Assert.Equal(0, await Collection("oauth_tokens").CountDocumentsAsync(new BsonDocument("connection", flow.ConnectionId), cancellationToken: Ct));
            Assert.Equal("access_denied", OAuthFlow.QueryOf(await flow.RedirectAfterAsync(allow: false))["error"]);
        }
    }

    [Fact]
    public async Task The_code_wraps_the_vault_keys_for_a_minute()
    {
        var flow = await FlowAsync(twoVaults: true);
        var before = DateTime.UtcNow;

        var code = await flow.AllowAsync();

        var record = await Collection("oauth_tokens").Find(new BsonDocument("connection", flow.ConnectionId)).SingleAsync(Ct);
        var tokenId = record["_id"].AsString;
        Assert.Matches("^[0-9a-f]{32}$", tokenId);
        Assert.Equal("code", record["kind"].AsString);
        Assert.Equal(flow.Owner.User.Id, record["user"].AsString);
        Assert.Equal(flow.ClientId, record["client"].AsString);
        Assert.Equal(Callback, record["redirectUri"].AsString);
        Assert.Equal(OAuthFlow.ChallengeOf(flow.Verifier), record["codeChallenge"].AsString);
        Assert.False(record["readOnly"].AsBoolean);
        Assert.False(record.Contains("usedAt"));
        var expiresAt = record["expiresAt"].ToUniversalTime();
        Assert.InRange(expiresAt, before.AddSeconds(59), DateTime.UtcNow.AddSeconds(61));
        Assert.Equal(expiresAt.AddMinutes(10), record["purgeAt"].ToUniversalTime());
        Assert.Equal(VaultCrypto.Derive(code, "kumbu/v1/oauth-token-lookup", []), record["lookup"].AsByteArray);

        // The grant opens with the code only, bound to its id and kind.
        var grant = JsonDocument.Parse(VaultCrypto.Open(VaultCrypto.Derive(code, "kumbu/v1/oauth-grant", []), record["grant"].AsByteArray, $"oauth:{tokenId}:code")).RootElement;
        Assert.Equal("claude", grant.GetProperty("name").GetString());
        Assert.Equal(flow.Resource, grant.GetProperty("resource").GetString());
        Assert.Equal(JsonValueKind.Null, grant.GetProperty("connectionExpiresAt").ValueKind);

        var slots = await Collection("slots").Find(new BsonDocument { ["kind"] = "connection", ["connection"] = flow.ConnectionId }).ToListAsync(Ct);
        Assert.Equal(2, slots.Count);
        var vaults = (await Api.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>().OpenForUserAsync(flow.Owner.User.Id, flow.Owner.UserKey, Ct)).ToDictionary(v => v.Id, v => v.Name);
        var rights = new Dictionary<string, string>();
        foreach (var slot in slots)
        {
            Assert.Equal(tokenId, slot["token"].AsString);
            Assert.Equal(expiresAt, slot["expiresAt"].ToUniversalTime());
            Assert.False(slot.Contains("lookup"));
            var payload = OpenSlot(slot, code, flow.ConnectionId, tokenId);
            Assert.Equal(32, payload.GetProperty("vaultKey").GetBytesFromBase64().Length);
            rights[vaults[slot["vault"].AsString]] = payload.GetProperty("rights").GetString()!;
        }

        Assert.Equal(new Dictionary<string, string> { ["Arbeit"] = "write", ["Privat"] = "read" }, rights);

        // Neither the code nor its random part is stored anywhere.
        var dump = string.Join('\n', (await Collection("oauth_tokens").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
            .Concat(await Collection("slots").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
            .Concat(await Collection("connections").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
            .Select(d => d.ToJson()));
        Assert.DoesNotContain(code["kumbu_ac_".Length..], dump, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(code)), dump, StringComparison.Ordinal);

        var slotIndexes = await (await Collection("slots").Indexes.ListAsync(Ct)).ToListAsync(Ct);
        Assert.Contains(slotIndexes, i => i["key"].AsBsonDocument.Contains("expiresAt") && i["expireAfterSeconds"].ToInt32() == 0);
        Assert.Contains(slotIndexes, i => i["key"].AsBsonDocument.Contains("token") && i.Contains("partialFilterExpression"));
        var tokenIndexes = await (await Collection("oauth_tokens").Indexes.ListAsync(Ct)).ToListAsync(Ct);
        Assert.Contains(tokenIndexes, i => i["key"].AsBsonDocument.Contains("lookup") && i.GetValue("unique", false).ToBoolean());
        Assert.Contains(tokenIndexes, i => i["key"].AsBsonDocument.Contains("purgeAt") && i["expireAfterSeconds"].ToInt32() == 0);
    }

    /// <summary>A connection slot's payload, opened with the token it was wrapped under: <c>{vaultKey, rights}</c>.</summary>
    private static JsonElement OpenSlot(BsonDocument slot, string token, string connectionId, string tokenId)
    {
        var vault = slot["vault"].AsString;
        var opened = VaultCrypto.Open(
            VaultCrypto.Derive(token, "kumbu/v1/oauth-kek", Encoding.UTF8.GetBytes(vault)), slot["wrapped"].AsByteArray, $"slot:{vault}:connection:{connectionId}:{tokenId}");
        return JsonDocument.Parse(opened).RootElement;
    }

    [Fact]
    public async Task Authorize_needs_no_cookie()
    {
        var (owner, id, client) = await ConnectionAsync();
        var flow = await OAuthFlow.StartAsync(Api, owner, id, client, Callback);

        // Without a cookie (as claude.ai's cross-site navigation arrives), and with a bogus one, alike.
        using var bogus = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(), cookie: $"{CallerMiddleware.SessionCookie}=nonsense");

        Assert.Equal(HttpStatusCode.SeeOther, bogus.StatusCode);
        Assert.StartsWith("/consent?request=", Location(bogus), StringComparison.Ordinal);
        Assert.Equal("no-store", bogus.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", bogus.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(bogus.Headers.Contains("Set-Cookie"));
        Assert.False(bogus.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Errors_carry_no_store_and_no_referrer_too()
    {
        var flow = await FlowAsync();

        using var page = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(("client_id", null)));
        using var redirected = await OAuthFlow.AuthorizeAsync(Api, flow.Parameters(("code_challenge", null)));

        foreach (var response in new[] { page, redirected })
        {
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        }
    }

    [Fact]
    public async Task The_flow_is_logged_without_its_secrets()
    {
        var log = new CapturedLog();
        var api = Api.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var owner = await RestClient.NewAsync(api);
        using var created = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name = "claude", vaults = new Dictionary<string, string> { [owner.Vault] = "write" } });
        var id = (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
        var flow = await OAuthFlow.StartAsync(api, owner, id, await OAuthFlow.RegisterAsync(api, Callback), Callback);

        var code = await flow.AllowAsync();

        var lines = log.Lines.Where(l => l.Contains("OAuth ", StringComparison.Ordinal)).ToList();
        Assert.Contains(lines, l => l.Contains("OAuth authorize pending", StringComparison.Ordinal) && l.Contains(id, StringComparison.Ordinal)
            && l.Contains("claude.test", StringComparison.Ordinal) && l.Contains(flow.RequestId, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("OAuth consent allowed", StringComparison.Ordinal) && l.Contains(id, StringComparison.Ordinal));
        foreach (var line in log.Lines)
        {
            Assert.DoesNotContain(code, line, StringComparison.Ordinal);
            Assert.DoesNotContain(flow.State, line, StringComparison.Ordinal);
            Assert.DoesNotContain(OAuthFlow.ChallengeOf(flow.Verifier), line, StringComparison.Ordinal);
        }
    }
}
