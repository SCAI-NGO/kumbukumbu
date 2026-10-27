// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Server.OAuth;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.OAuth;

/// <summary>
/// The whole flow as an app runs it, with the official SDK's OAuth client (spec §4, §5): from the 401 on a connection's
/// URL through the metadata, the client's identification (a metadata document or a registration), the authorisation
/// with the owner's consent and the token exchange, to a tool call. And the log a "connected but never used" report
/// is followed with.
/// </summary>
[Collection("app")]
public sealed class SdkFlowTests(KumbuAppFactory app)
{
    private const string Callback = "http://127.0.0.1:1/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The public app, accepting metadata documents on a loopback port (<see cref="MetadataServer"/>), logging into
    /// <paramref name="log"/>. The SDK insists on an https client id, so the fetcher's requests to
    /// <c>https://127.0.0.1:{port}</c> go out as plain http, as behind a TLS terminator; its own checks and its
    /// connection pinning still run.
    /// </summary>
    private WebApplicationFactory<Program> Api(CapturedLog? log = null) => app.PublicApi.WithWebHostBuilder(b =>
    {
        b.UseSetting("Kumbu:OAuth:AllowLoopbackClients", "true");
        b.ConfigureServices(s => s.AddHttpClient(ClientMetadataServices.HttpClientName).AddHttpMessageHandler(() => new WithoutTls()));
        if (log is not null) b.ConfigureLogging(l => l.AddProvider(log));
    });

    /// <summary>The https client id the metadata server is reached at (<see cref="WithoutTls"/>).</summary>
    private static string HttpsId(MetadataServer server) => "https" + server.ClientId["http".Length..];

    private sealed class WithoutTls : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Scheme = "http" }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task<string> ConnectionAsync(RestClient owner, string name = "claude")
    {
        using var created = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name, vaults = new Dictionary<string, string> { [owner.Vault] = "write" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
    }

    /// <summary>A metadata document naming <see cref="Callback"/>, with members no log line may show.</summary>
    private static async Task<MetadataServer> MetadataAsync()
    {
        var server = await MetadataServer.StartAsync(new MetadataResponse());
        server.Response = () => new MetadataResponse(Document(HttpsId(server)));
        return server;
    }

    private static string Document(string clientId) => $$"""
        {"client_id":"{{clientId}}","client_name":"Sdk Flow Probe","client_uri":"https://probe.example/about",
         "logo_uri":"https://probe.example/logo-5c1e.png","redirect_uris":["{{Callback}}"],
         "grant_types":["authorization_code","refresh_token"],"response_types":["code"],"token_endpoint_auth_method":"none"}
        """;

    /// <summary>
    /// The SDK client on the connection's URL with OAuth: <paramref name="metadataUri"/> as its client id, else a
    /// registration. Its authorisation handler plays the browser: it GETs the authorisation URL, follows the 303 to
    /// the consent page, has the owner allow (session and <c>Origin</c>) and hands back what the redirect carries.
    /// </summary>
    private static async Task<McpClient> SdkClientAsync(WebApplicationFactory<Program> api, RestClient owner, string connectionId, Uri? metadataUri, Recorder seen)
    {
        var http = api.CreateDefaultClient(new Uri(KumbuAppFactory.PublicUrl), seen);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(OAuthFlow.ResourceOf(connectionId)),
                OAuth = new ClientOAuthOptions
                {
                    RedirectUri = new Uri(Callback),
                    ClientMetadataDocumentUri = metadataUri,
                    DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Sdk Flow Probe" },
                    AuthorizationCallbackHandler = async (context, ct) =>
                    {
                        using var authorize = await http.GetAsync(context.AuthorizationUri, ct);
                        Assert.Equal(HttpStatusCode.SeeOther, authorize.StatusCode);
                        var consent = authorize.Headers.Location!.OriginalString;
                        Assert.StartsWith("/consent?request=", consent, StringComparison.Ordinal);
                        var request = OAuthFlow.QueryOf(consent)["request"];

                        using var decided = await owner.SendAsync(HttpMethod.Post, $"/api/oauth/requests/{request}/decision", new { allow = true });
                        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
                        var redirect = (await RestClient.JsonAsync(decided))["redirect"]!.GetValue<string>();
                        Assert.StartsWith(Callback + "?", redirect, StringComparison.Ordinal);
                        var query = OAuthFlow.QueryOf(redirect);
                        return new AuthorizationResult { Code = query["code"], State = query["state"], Iss = query["iss"] };
                    },
                },
            },
            http,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static async Task<string> ShAsync(McpClient client, string command)
    {
        var result = await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct);
        Assert.NotEqual(true, result.IsError);
        return Assert.IsType<TextContentBlock>(result.Content[0]).Text;
    }

    [Fact]
    public async Task The_sdk_client_connects_through_cimd()
    {
        await using var metadata = await MetadataAsync();
        var api = Api();
        var owner = await RestClient.NewAsync(api);
        var id = await ConnectionAsync(owner);
        var seen = new Recorder();

        await using var client = await SdkClientAsync(api, owner, id, new Uri(HttpsId(metadata)), seen);

        Assert.Equal($"{owner.Vault}/", await ShAsync(client, "ls /"));
        AssertSawTheFlow(seen, id);
        Assert.DoesNotContain(seen.Lines, l => l.StartsWith("POST /oauth/register", StringComparison.Ordinal));
        Assert.Contains("/client.json", metadata.Paths);
        var authorize = seen.Lines.Single(l => l.StartsWith("GET /oauth/authorize?", StringComparison.Ordinal));
        Assert.Equal(HttpsId(metadata), OAuthFlow.QueryOf(authorize[..authorize.LastIndexOf(' ')])["client_id"]);
    }

    [Fact]
    public async Task The_sdk_client_connects_through_registration()
    {
        var api = Api();
        var owner = await RestClient.NewAsync(api);
        var id = await ConnectionAsync(owner);
        var seen = new Recorder();

        await using var client = await SdkClientAsync(api, owner, id, metadataUri: null, seen);

        Assert.Equal($"{owner.Vault}/", await ShAsync(client, "ls /"));
        AssertSawTheFlow(seen, id);
        Assert.Contains(seen.Lines, l => l == "POST /oauth/register 201");
    }

    /// <summary>The steps of the flow, in order: the challenge, both metadata documents, authorize, token, then MCP with the token.</summary>
    private static void AssertSawTheFlow(Recorder seen, string id)
    {
        var lines = seen.Lines.ToList();
        int At(Func<string, bool> match)
        {
            var index = lines.FindIndex(l => match(l));
            Assert.True(index >= 0, "Not seen; the server saw:\n" + string.Join('\n', lines));
            return index;
        }

        var challenge = At(l => l == $"POST /mcp/c/{id} 401");
        var resource = At(l => l == $"GET /.well-known/oauth-protected-resource/mcp/c/{id} 200");
        var server = At(l => l == "GET /.well-known/oauth-authorization-server 200");
        var authorize = At(l => l.StartsWith("GET /oauth/authorize?", StringComparison.Ordinal) && l.EndsWith(" 303", StringComparison.Ordinal));
        var token = At(l => l == "POST /oauth/token 200");
        var used = lines.FindLastIndex(l => l == $"POST /mcp/c/{id} 200");
        Assert.True(challenge < resource && resource < server && server < authorize && authorize < token && token < used, string.Join('\n', lines));
    }

    [Fact]
    public async Task Both_handshakes_work_on_a_connection_url()
    {
        var api = Api();
        var owner = await RestClient.NewAsync(api);
        var id = await ConnectionAsync(owner);
        var flow = await OAuthFlow.StartAsync(api, owner, id, await OAuthFlow.RegisterAsync(api, Callback), Callback);
        await flow.AllowAsync();
        using var exchanged = await flow.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        var access = (await RestClient.JsonAsync(exchanged))["access_token"]!.GetValue<string>();

        var initialize = await RawAsync(
            api, id, access,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""",
            []);
        var discover = await RawAsync(
            api, id, access,
            """{"jsonrpc":"2.0","id":2,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"raw","version":"0"}}}}""",
            [("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "server/discover")]);

        Assert.Equal("kumbu", initialize["result"]?["serverInfo"]?["name"]?.GetValue<string>());
        // 2026-07-28 carries the server's identity in the result's _meta rather than its body.
        var result = discover["result"] ?? throw new Xunit.Sdk.XunitException("No result: " + discover.ToJsonString());
        var info = result["serverInfo"] ?? result["_meta"]?["io.modelcontextprotocol/serverInfo"];
        Assert.Equal("kumbu", info?["name"]?.GetValue<string>());
    }

    /// <summary>A raw JSON-RPC POST on the connection's URL with the access token; asserts 200 and returns the message.</summary>
    private static async Task<JsonNode> RawAsync(WebApplicationFactory<Program> api, string id, string access, string body, (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/c/{id}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("X-Forwarded-For", Recorder.Address());
        foreach (var (name, value) in headers) request.Headers.Add(name, value);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        using var response = await client.SendAsync(request, Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {raw}");
        // Either plain JSON or an SSE stream whose last data: line is the message.
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? raw.Split('\n').Select(l => l.TrimEnd('\r')).Last(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..]
            : raw;
        return JsonNode.Parse(json)!;
    }

    [Fact]
    public async Task The_flow_is_logged_without_secrets()
    {
        await using var metadata = await MetadataAsync();
        var log = new CapturedLog();
        var api = Api(log);
        var owner = await RestClient.NewAsync(api);
        var id = await ConnectionAsync(owner);
        var flow = await OAuthFlow.StartAsync(api, owner, id, HttpsId(metadata), Callback);
        var code = await flow.AllowAsync();
        using var exchanged = await flow.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        var first = await RestClient.JsonAsync(exchanged);
        using var refreshed = await flow.RefreshAsync(first["refresh_token"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = await RestClient.JsonAsync(refreshed);
        using var revoked = await flow.RevokeAsync(second["refresh_token"]!.GetValue<string>(), "refresh_token");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        var lines = log.Lines.ToList();
        foreach (var step in new[] { "OAuth authorize pending", "OAuth consent allowed", "OAuth token issued", "OAuth refresh issued", "OAuth revoke" })
        {
            Assert.True(
                lines.Any(l => l.Contains(step, StringComparison.Ordinal) && l.Contains(id, StringComparison.Ordinal) && l.Contains("client 127.0.0.1", StringComparison.Ordinal)),
                $"No line '{step}' with the connection and the client host in:\n" + string.Join('\n', lines.Where(l => l.Contains("OAuth", StringComparison.Ordinal))));
        }

        var secrets = new[]
        {
            code, flow.Verifier, OAuthFlow.ChallengeOf(flow.Verifier), flow.State,
            first["access_token"]!.GetValue<string>(), first["refresh_token"]!.GetValue<string>(),
            second["access_token"]!.GetValue<string>(), second["refresh_token"]!.GetValue<string>(),
            // The document's own members, and the client id beyond its host.
            "Sdk Flow Probe", "probe.example", "logo-5c1e", HttpsId(metadata), Document(HttpsId(metadata)),
        };
        foreach (var secret in secrets)
            Assert.DoesNotContain(lines, l => l.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_issued_but_never_used_can_be_followed()
    {
        var log = new CapturedLog();
        var api = Api(log);
        var owner = await RestClient.NewAsync(api);
        var id = await ConnectionAsync(owner);
        var flow = await OAuthFlow.StartAsync(api, owner, id, await OAuthFlow.RegisterAsync(api, Callback), Callback);
        await flow.AllowAsync();

        using var exchanged = await flow.TokenAsync();

        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        Assert.Contains(log.Lines, l => l.Contains("OAuth token issued", StringComparison.Ordinal) && l.Contains(id, StringComparison.Ordinal));
        using var listed = await owner.GetAsync("/api/connections");
        var connection = (await RestClient.JsonAsync(listed)).AsArray().Single(c => c!["id"]!.GetValue<string>() == id)!;
        Assert.Null(connection["lastUsedAt"]?.GetValue<DateTime?>());
    }

    /// <summary>Every request the app's server answers for the SDK client, as <c>METHOD path?query status</c>, each from an address of its own.</summary>
    private sealed class Recorder : DelegatingHandler
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines.ToArray();

        public static string Address() => $"198.25.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Forwarded-For", Address());
            var response = await base.SendAsync(request, cancellationToken);
            _lines.Enqueue($"{request.Method} {request.RequestUri!.PathAndQuery} {(int)response.StatusCode}");
            return response;
        }
    }
}
