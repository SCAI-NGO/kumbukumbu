// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.OAuth;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.OAuth;

/// <summary>A connection's URL, its protected resource metadata and the authorisation server's metadata (RFC 9728, RFC 8414).</summary>
[Collection("app")]
public sealed class DiscoveryTests(KumbuAppFactory app)
{
    private const string Origin = KumbuAppFactory.PublicUrl;
    private const string Scope = "kumbu:read kumbu:write";
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private async Task<(RestClient User, string Id)> ConnectionAsync()
    {
        var user = await RestClient.NewAsync(Api);
        using var created = await user.SendAsync(HttpMethod.Post, "/api/connections", new { name = "claude", vaults = new Dictionary<string, string> { [user.Vault] = "write" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (user, (await RestClient.JsonAsync(created))["id"]!.GetValue<string>());
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer = null, string? body = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        }

        if (bearer is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (origin is not null) request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Forwarded-For", $"198.21.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    private static string Challenge(string id) =>
        $"Bearer resource_metadata=\"{Origin}/.well-known/oauth-protected-resource/mcp/c/{id}\", scope=\"{Scope}\"";

    private static string RawWwwAuthenticate(HttpResponseMessage response) => response.Headers.GetValues("WWW-Authenticate").Single();

    [Fact]
    public async Task Unauthenticated_connection_url_answers_401_with_the_challenge()
    {
        var (_, id) = await ConnectionAsync();

        using var response = await SendAsync(HttpMethod.Post, $"/mcp/c/{id}", body: Initialize);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Challenge(id), RawWwwAuthenticate(response));
    }

    [Fact]
    public async Task A_wrong_bearer_adds_invalid_token()
    {
        var (_, id) = await ConnectionAsync();

        using var response = await SendAsync(HttpMethod.Post, $"/mcp/c/{id}", "kumbu_at_x", Initialize);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Challenge(id) + ", error=\"invalid_token\"", RawWwwAuthenticate(response));
    }

    [Fact]
    public async Task An_api_key_is_refused_on_a_connection_url()
    {
        var (user, id) = await ConnectionAsync();
        var key = await user.KeyAsync("laptop", KeyRight.Write);

        using var onConnection = await SendAsync(HttpMethod.Post, $"/mcp/c/{id}", key, Initialize);
        using var onPlain = await SendAsync(HttpMethod.Post, "/mcp", key, Initialize);

        Assert.Equal(HttpStatusCode.Unauthorized, onConnection.StatusCode);
        Assert.StartsWith(Challenge(id), RawWwwAuthenticate(onConnection), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, onPlain.StatusCode);
    }

    [Fact]
    public async Task The_dev_token_is_refused_on_a_connection_url()
    {
        var (_, id) = await ConnectionAsync();
        using var response = await SendAsync(HttpMethod.Post, $"/mcp/c/{id}", KumbuAppFactory.Token, Initialize);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Plain_mcp_points_nowhere()
    {
        using var mcp = await SendAsync(HttpMethod.Post, "/mcp", body: Initialize);
        using var root = await SendAsync(HttpMethod.Get, "/.well-known/oauth-protected-resource");
        using var plain = await SendAsync(HttpMethod.Get, "/.well-known/oauth-protected-resource/mcp");
        using var openid = await SendAsync(HttpMethod.Get, "/.well-known/openid-configuration");

        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
        Assert.Equal("Bearer", RawWwwAuthenticate(mcp));
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, plain.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, openid.StatusCode);
    }

    [Fact]
    public async Task Protected_resource_metadata_names_the_connection()
    {
        var (_, id) = await ConnectionAsync();

        using var response = await SendAsync(HttpMethod.Get, $"/.well-known/oauth-protected-resource/mcp/c/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=300", response.Headers.CacheControl!.ToString());
        var json = await RestClient.JsonAsync(response);
        Assert.Equal($"{Origin}/mcp/c/{id}", json["resource"]!.GetValue<string>());
        Assert.Equal([Origin], json["authorization_servers"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["header"], json["bearer_methods_supported"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["kumbu:read", "kumbu:write"], json["scopes_supported"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("Kumbu²", json["resource_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_ids_have_no_metadata_and_no_endpoint()
    {
        const string random = "abcdefghijklmnop";

        using var metadata = await SendAsync(HttpMethod.Get, $"/.well-known/oauth-protected-resource/mcp/c/{random}");
        using var endpoint = await SendAsync(HttpMethod.Post, $"/mcp/c/{random}", body: Initialize);
        using var odd = await SendAsync(HttpMethod.Post, "/mcp/c/not-an-id", body: Initialize);

        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        Assert.Equal("Not found.", await RestClient.ErrorAsync(metadata));
        Assert.Equal(HttpStatusCode.NotFound, endpoint.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, odd.StatusCode);
    }

    [Fact]
    public async Task Trailing_slash_and_host_case_reach_the_same_connection()
    {
        var (_, id) = await ConnectionAsync();

        using var slash = await SendAsync(HttpMethod.Get, $"/.well-known/oauth-protected-resource/mcp/c/{id}/");
        using var endpoint = await SendAsync(HttpMethod.Post, $"/mcp/c/{id}/", body: Initialize);

        Assert.Equal(HttpStatusCode.OK, slash.StatusCode);
        Assert.Equal($"{Origin}/mcp/c/{id}/", (await RestClient.JsonAsync(slash))["resource"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, endpoint.StatusCode);
        Assert.Equal(id, OAuthUrls.ConnectionIdOf(Origin, $"HTTPS://KUMBU.TEST:443/mcp/c/{id}/"));
        Assert.Equal(id, OAuthUrls.ConnectionIdOf(Origin, $"https://kumbu.test/mcp/c/{id}"));
        Assert.Null(OAuthUrls.ConnectionIdOf(Origin, $"https://kumbu.test/mcp/c/{id.ToUpperInvariant()}"));
        Assert.Null(OAuthUrls.ConnectionIdOf(Origin, $"https://kumbu.test/mcp"));
        Assert.Null(OAuthUrls.ConnectionIdOf(Origin, $"https://other.test/mcp/c/{id}"));
        Assert.Null(OAuthUrls.ConnectionIdOf(Origin, $"http://kumbu.test/mcp/c/{id}"));
        Assert.Null(OAuthUrls.ConnectionIdOf(Origin, $"https://kumbu.test/mcp/c/{id}/more"));
    }

    [Fact]
    public async Task Authorization_server_metadata_is_complete()
    {
        using var response = await SendAsync(HttpMethod.Get, "/.well-known/oauth-authorization-server");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=300", response.Headers.CacheControl!.ToString());
        var json = (await RestClient.JsonAsync(response)).AsObject();
        var expected = new JsonObject
        {
            ["issuer"] = Origin,
            ["authorization_endpoint"] = Origin + "/oauth/authorize",
            ["token_endpoint"] = Origin + "/oauth/token",
            ["registration_endpoint"] = Origin + "/oauth/register",
            ["revocation_endpoint"] = Origin + "/oauth/revoke",
            ["response_types_supported"] = new JsonArray("code"),
            ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
            ["code_challenge_methods_supported"] = new JsonArray("S256"),
            ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
            ["revocation_endpoint_auth_methods_supported"] = new JsonArray("none"),
            ["scopes_supported"] = new JsonArray("kumbu:read", "kumbu:write"),
            ["client_id_metadata_document_supported"] = true,
            ["authorization_response_iss_parameter_supported"] = true,
        };
        Assert.True(JsonNode.DeepEquals(expected, json), json.ToJsonString());
        Assert.DoesNotContain("offline_access", json.ToJsonString());
    }

    [Fact]
    public async Task Oauth_documents_allow_cross_origin_reads()
    {
        var (_, id) = await ConnectionAsync();

        using var preflight = await SendAsync(HttpMethod.Options, "/oauth/token", origin: "http://localhost:6274");
        using var document = await SendAsync(HttpMethod.Get, "/.well-known/oauth-authorization-server", origin: "http://localhost:6274");
        using var connection = await SendAsync(HttpMethod.Options, $"/mcp/c/{id}", origin: "http://localhost:6274");

        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal("*", preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("GET, POST, OPTIONS", preflight.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Equal("Content-Type, Authorization, MCP-Protocol-Version", preflight.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.Equal("600", preflight.Headers.GetValues("Access-Control-Max-Age").Single());
        Assert.Equal("*", document.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(connection.Headers.Contains("Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// No access token is valid before the token endpoint exists, so the route is proven from inside: the endpoint
    /// <c>MapMcp("/mcp/c/{id}")</c> registered is run for a caller, with the route value set, and must serve the
    /// same tools as <c>/mcp</c>.
    /// </summary>
    [Fact]
    public async Task The_connection_route_serves_the_same_tools_as_plain_mcp()
    {
        var plain = await ToolNamesAsync("/mcp", new RouteValueDictionary());
        var connection = await ToolNamesAsync("/mcp/c/abcdefghijklmnop", new RouteValueDictionary { ["id"] = "abcdefghijklmnop" });

        Assert.NotEmpty(plain);
        Assert.Equal(plain, connection);
    }

    private async Task<string[]> ToolNamesAsync(string path, RouteValueDictionary values)
    {
        var endpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText!.TrimEnd('/') == path.Replace("abcdefghijklmnop", "{id}") && e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);
        await using var scope = app.Services.CreateAsyncScope();
        var catalog = app.Services.GetRequiredService<VaultCatalog>();
        var body = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.Headers.Accept = "application/json, text/event-stream";
        context.Request.Headers["MCP-Protocol-Version"] = "2025-06-18";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        foreach (var (key, value) in values) context.Request.RouteValues[key] = value;
        context.SetEndpoint(endpoint);
        context.Features.Set(RequestCaller.ForDevToken(KumbuAppFactory.Token, catalog));
        var output = new MemoryStream();
        context.Response.Body = output;

        await endpoint.RequestDelegate!(context);

        Assert.Equal(200, context.Response.StatusCode);
        var text = Encoding.UTF8.GetString(output.ToArray());
        var data = text.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))?[5..].Trim() ?? text;
        return [.. JsonNode.Parse(data)!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Order()];
    }
}
