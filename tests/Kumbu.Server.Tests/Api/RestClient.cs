// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Api;

/// <summary>A signed-in user of <see cref="KumbuAppFactory.PublicApi"/> with a vault of their own, and the
/// requests the notes API tests send: as the session, or as an API key of the user.</summary>
public sealed class RestClient
{
    public const string Password = "correct horse battery staple";
    public const string Origin = KumbuAppFactory.PublicUrl;

    private readonly WebApplicationFactory<Program> _api;

    private RestClient(WebApplicationFactory<Program> api, string username, string session, User user, byte[] userKey, string vault)
    {
        _api = api;
        Username = username;
        Session = session;
        User = user;
        UserKey = userKey;
        Vault = vault;
    }

    /// <summary>The server this client talks to.</summary>
    public WebApplicationFactory<Program> Api => _api;

    public string Username { get; }

    public string Session { get; }

    public User User { get; }

    public byte[] UserKey { get; }

    /// <summary>The user's vault, created through <c>POST /api/vaults</c>.</summary>
    public string Vault { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A new user (of <paramref name="role"/>) signed in, with "Keep me signed in" if <paramref name="remember"/>.</summary>
    public static async Task<RestClient> NewAsync(WebApplicationFactory<Program> api, string vault = "Arbeit", bool remember = false, string role = Roles.User)
    {
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await api.Services.GetRequiredService<UserCatalog>().CreateAsync(username, Password, role, Ct);
        var (user, userKey, _) = (await api.Services.GetRequiredService<UserCatalog>().TrySignInAsync(username, Password, Ct))!.Value;

        using var signIn = await SendAsync(api, HttpMethod.Post, "/api/session", new { username, password = Password, remember }, session: null, bearer: null);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        var cookie = signIn.Headers.GetValues("Set-Cookie").First(v => v.StartsWith(CallerMiddleware.SessionCookie + "=", StringComparison.Ordinal));
        var session = cookie[(CallerMiddleware.SessionCookie.Length + 1)..].Split(';')[0];

        using var created = await SendAsync(api, HttpMethod.Post, "/api/vaults", new { name = vault }, session, bearer: null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return new RestClient(api, username, session, user, userKey, vault);
    }

    /// <summary>A new session of this user, e.g. after a test moved the clock past the old one's timeout.</summary>
    public async Task<RestClient> SignInAgainAsync()
    {
        using var signIn = await SendAsync(_api, HttpMethod.Post, "/api/session", new { username = Username, password = Password }, session: null, bearer: null);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        var cookie = signIn.Headers.GetValues("Set-Cookie").First(v => v.StartsWith(CallerMiddleware.SessionCookie + "=", StringComparison.Ordinal));
        return new RestClient(_api, Username, cookie[(CallerMiddleware.SessionCookie.Length + 1)..].Split(';')[0], User, UserKey, Vault);
    }

    /// <summary>An API key of this user for its vault, with <paramref name="right"/>.</summary>
    public async Task<string> KeyAsync(string name, KeyRight right, ApiKeyKind kind = ApiKeyKind.Agent)
    {
        var vault = (await _api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(User.Id, UserKey, Ct)).Single(v => v.Name == Vault);
        var (key, _) = await _api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            User, UserKey, DateTime.UtcNow, name, new Dictionary<string, KeyRight> { [vault.Id] = right }, Ct, kind: kind);
        return key;
    }

    /// <summary>One shell command over MCP as <paramref name="bearer"/>, on this client's own factory: for folder
    /// checkpoints (there is no REST route to make one) and for pinning what the web app tells people to run.</summary>
    public async Task<(bool IsError, string Text)> ShAsync(string command, string bearer)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(_api.Server.BaseAddress, "mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
            },
            _api.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct);
        var result = await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct);
        return (result.IsError ?? false, Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    /// <summary>A request as the session (with the web app's Origin), or as <paramref name="bearer"/> instead.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? bearer = null, IReadOnlyDictionary<string, string>? headers = null) =>
        SendAsync(_api, method, path, body, bearer is null ? Session : null, bearer, headers);

    public Task<HttpResponseMessage> GetAsync(string path, string? bearer = null) => SendAsync(HttpMethod.Get, path, bearer: bearer);

    /// <summary>A request with <paramref name="content"/> as its body as it is (an attachment's bytes), as the session or <paramref name="bearer"/>.</summary>
    public Task<HttpResponseMessage> SendContentAsync(HttpMethod method, string path, HttpContent content, string? bearer = null) =>
        SendAsync(_api, method, path, null, bearer is null ? Session : null, bearer, content: content);

    /// <summary>Uploads an attachment's bytes through <c>PUT /api/files</c>; <paramref name="version"/> to overwrite.</summary>
    public Task<HttpResponseMessage> PutFileAsync(string filePath, byte[] bytes, string? version = null, string? bearer = null) =>
        SendContentAsync(
            HttpMethod.Put,
            Url(filePath, "files") + (version is null ? "" : $"?version={Uri.EscapeDataString(version)}"),
            new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
            bearer);

    /// <summary>Creates an attachment and returns its version.</summary>
    public async Task<string> CreateFileAsync(string filePath, byte[] bytes)
    {
        using var response = await PutFileAsync(filePath, bytes);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response))["version"]!.GetValue<string>();
    }

    /// <summary>The URL of a note: <c>/api/{kind}/{vault}/{path}</c>, each segment escaped.</summary>
    public string Url(string notePath, string kind = "notes") =>
        $"/api/{kind}/{Uri.EscapeDataString(Vault)}/{string.Join('/', notePath.Split('/').Select(Uri.EscapeDataString))}";

    /// <summary>Creates a note through <c>PUT</c> and returns its version.</summary>
    public async Task<string> CreateAsync(string notePath, string content)
    {
        using var response = await SendAsync(HttpMethod.Put, Url(notePath), new { content });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response))["version"]!.GetValue<string>();
    }

    public async Task<JsonNode> ReadAsync(string notePath)
    {
        using var response = await GetAsync(Url(notePath));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    public static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    public static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await JsonAsync(response))["error"]!.GetValue<string>();

    private static async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> api, HttpMethod method, string path, object? body, string? session, string? bearer,
        IReadOnlyDictionary<string, string>? headers = null, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (content is not null) request.Content = content;
        if (session is not null) request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        else request.Headers.Add("Origin", Origin);
        // An address of its own per request, so the sign-in limits of other tests don't reach it.
        request.Headers.Add("X-Forwarded-For", $"198.20.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }
}
