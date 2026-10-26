// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Server.Api;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace Kumbu.Server.Tests.OAuth;

/// <summary>
/// One authorisation as an app and its user go through it on <see cref="KumbuAppFactory.PublicApi"/>: the app sends
/// the browser to <c>/oauth/authorize</c> (here without following the redirect), the user's consent page reads the
/// pending request and posts the decision with the user's session and the app's <c>Origin</c>, and the app exchanges
/// the code at <c>/oauth/token</c>. Each step is a method, so a test can stop at any of them or vary one.
/// <para>
/// <see cref="StartAsync"/> sends a fresh PKCE verifier (43 characters, its S256 challenge), a fresh <c>state</c> and
/// <c>resource</c> = the connection's URL; it asserts the 303 to <c>/consent?request={id}</c>. For a request that
/// should fail at the authorisation endpoint use <see cref="AuthorizeAsync"/> with <see cref="Parameters"/>.
/// </para>
/// </summary>
public sealed class OAuthFlow
{
    /// <summary>The issuer, as <c>iss</c> in every redirect: the public origin.</summary>
    public const string Issuer = KumbuAppFactory.PublicUrl;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _api;

    private OAuthFlow(WebApplicationFactory<Program> api, RestClient owner, string connectionId, string clientId, string redirectUri, string? scope)
    {
        _api = api;
        Owner = owner;
        ConnectionId = connectionId;
        ClientId = clientId;
        RedirectUri = redirectUri;
        Scope = scope;
        Resource = ResourceOf(connectionId);
        Verifier = NewVerifier();
        State = "st-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    }

    private OAuthFlow(OAuthFlow other)
    {
        _api = other._api;
        Owner = other.Owner;
        ConnectionId = other.ConnectionId;
        ClientId = other.ClientId;
        RedirectUri = other.RedirectUri;
        Scope = other.Scope;
        Resource = other.Resource;
        Verifier = other.Verifier;
        State = other.State;
    }

    public RestClient Owner { get; }

    public string ConnectionId { get; }

    public string ClientId { get; }

    public string RedirectUri { get; }

    public string? Scope { get; }

    /// <summary>The connection's URL, sent as <c>resource</c>.</summary>
    public string Resource { get; }

    /// <summary>The PKCE code verifier (43 characters of base64url); the challenge sent is <see cref="ChallengeOf"/> of it.</summary>
    public string Verifier { get; }

    public string State { get; }

    /// <summary>The pending request's id from the 303 to <c>/consent?request={id}</c>.</summary>
    public string RequestId { get; private set; } = "";

    /// <summary>The code of the last <see cref="AllowAsync"/>, or null before it.</summary>
    public string? Code { get; private set; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string ResourceOf(string connectionId) => $"{KumbuAppFactory.PublicUrl}/mcp/c/{connectionId}";

    /// <summary>Starts an authorisation for <paramref name="connectionId"/>'s URL and asserts it reaches the consent page.</summary>
    public static async Task<OAuthFlow> StartAsync(
        WebApplicationFactory<Program> api, RestClient owner, string connectionId, string clientId, string redirectUri, string? scope = null)
    {
        var flow = new OAuthFlow(api, owner, connectionId, clientId, redirectUri, scope);
        using var response = await AuthorizeAsync(api, flow.Parameters());
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/consent?request=", location, StringComparison.Ordinal);
        flow.RequestId = QueryOf(location)["request"];
        return flow;
    }

    /// <summary>Another authorisation request with this flow's parameters, changed as in <see cref="Parameters"/>
    /// (same verifier and state); asserts it reaches the consent page and returns the flow of that request.</summary>
    public async Task<OAuthFlow> AgainAsync(params (string Name, string? Value)[] changes)
    {
        var flow = new OAuthFlow(this);
        using var response = await AuthorizeAsync(_api, Parameters(changes));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/consent?request=", location, StringComparison.Ordinal);
        flow.RequestId = QueryOf(location)["request"];
        return flow;
    }

    /// <summary>The query this flow sends to <c>/oauth/authorize</c>; a test changes or removes entries (null removes) to vary it.</summary>
    public Dictionary<string, string?> Parameters(params (string Name, string? Value)[] changes)
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code_challenge"] = ChallengeOf(Verifier),
            ["code_challenge_method"] = "S256",
            ["state"] = State,
            ["resource"] = Resource,
        };
        if (Scope is not null) query["scope"] = Scope;
        foreach (var (name, value) in changes) query[name] = value;
        return query;
    }

    /// <summary>A GET of <c>/oauth/authorize</c> with <paramref name="query"/> (null values left out), as a browser
    /// navigating there: no cookie unless given, no redirect followed.</summary>
    public static async Task<HttpResponseMessage> AuthorizeAsync(WebApplicationFactory<Program> api, IEnumerable<KeyValuePair<string, string?>> query, string? cookie = null)
    {
        var url = QueryHelpers.AddQueryString("/oauth/authorize", query.Where(p => p.Value is not null));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-Forwarded-For", Address());
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    /// <summary>The consent page's read of the request, as <paramref name="user"/> (the owner by default).</summary>
    public Task<HttpResponseMessage> GetConsentAsync(RestClient? user = null) => (user ?? Owner).GetAsync($"/api/oauth/requests/{RequestId}");

    public async Task<ConsentView> ConsentAsync(RestClient? user = null)
    {
        using var response = await GetConsentAsync(user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConsentView>(Json, Ct))!;
    }

    /// <summary>The consent page's Allow or Deny, as <paramref name="user"/> (the owner by default), with the session and our <c>Origin</c>.</summary>
    public Task<HttpResponseMessage> DecideAsync(bool allow, RestClient? user = null) =>
        (user ?? Owner).SendAsync(HttpMethod.Post, $"/api/oauth/requests/{RequestId}/decision", new { allow });

    /// <summary>The URL the consent page navigates to after a decision that succeeded.</summary>
    public async Task<string> RedirectAfterAsync(bool allow, RestClient? user = null)
    {
        using var response = await DecideAsync(allow, user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConsentDecisionView>(Json, Ct))!.Redirect;
    }

    /// <summary>Allows as the owner and returns the code from the redirect, asserting the redirect URI, <c>state</c> and <c>iss</c>.</summary>
    public async Task<string> AllowAsync()
    {
        var redirect = await RedirectAfterAsync(allow: true);
        Assert.StartsWith(RedirectUri + (RedirectUri.Contains('?', StringComparison.Ordinal) ? "&" : "?"), redirect, StringComparison.Ordinal);
        var query = QueryOf(redirect);
        Assert.Equal(State, query["state"]);
        Assert.Equal(Issuer, query["iss"]);
        Assert.False(query.ContainsKey("error"));
        Code = query["code"];
        return Code;
    }

    /// <summary>
    /// A POST to <c>/oauth/token</c>, form-encoded. Unless the form names another <c>grant_type</c>, it starts from the
    /// exchange of <see cref="Code"/> (<c>grant_type</c>, <c>code</c>, <c>redirect_uri</c>, <c>client_id</c>,
    /// <c>code_verifier</c>, <c>resource</c>); entries in <paramref name="form"/> replace those of the same name, and a
    /// null value removes one.
    /// </summary>
    public async Task<HttpResponseMessage> TokenAsync(params (string Name, string? Value)[] form)
    {
        var fields = new Dictionary<string, string?>();
        var grant = form.LastOrDefault(f => f.Name == "grant_type").Value;
        if (grant is null or "authorization_code")
        {
            fields["grant_type"] = "authorization_code";
            fields["code"] = Code;
            fields["redirect_uri"] = RedirectUri;
            fields["client_id"] = ClientId;
            fields["code_verifier"] = Verifier;
            fields["resource"] = Resource;
        }

        foreach (var (name, value) in form) fields[name] = value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
        {
            Content = new FormUrlEncodedContent(fields.Where(f => f.Value is not null).Select(f => new KeyValuePair<string, string>(f.Key, f.Value!))),
        };
        request.Headers.Add("X-Forwarded-For", Address());
        using var client = _api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    /// <summary><c>grant_type=refresh_token</c> with <paramref name="refreshToken"/> and this flow's client id;
    /// entries in <paramref name="form"/> are added or replace those, a null value removes one.</summary>
    public Task<HttpResponseMessage> RefreshAsync(string refreshToken, params (string Name, string? Value)[] form) =>
        TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refreshToken), ("client_id", ClientId), .. form]);

    /// <summary>A POST to <c>/oauth/revoke</c> (RFC 7009) with <paramref name="token"/> and this flow's client id.</summary>
    public async Task<HttpResponseMessage> RevokeAsync(string token, string? hint = null)
    {
        var fields = new Dictionary<string, string> { ["token"] = token, ["client_id"] = ClientId };
        if (hint is not null) fields["token_type_hint"] = hint;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/revoke") { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("X-Forwarded-For", Address());
        using var client = _api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    /// <summary>Registers a public client with one redirect URI (RFC 7591) and returns its client id: for tests that don't need a metadata document.</summary>
    public static async Task<string> RegisterAsync(WebApplicationFactory<Program> api, string redirectUri, string name = "Test app")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/register")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { client_name = name, redirect_uris = new[] { redirectUri } }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Forwarded-For", Address());
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("client_id").GetString()!;
    }

    /// <summary>A PKCE verifier: 32 random bytes in base64url, 43 characters (RFC 7636 §4.1).</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 challenge of <paramref name="verifier"/>: <c>BASE64URL(SHA256(ASCII(verifier)))</c>.</summary>
    public static string ChallengeOf(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>The query of a URL (absolute or relative), one value per name.</summary>
    public static Dictionary<string, string> QueryOf(string url)
    {
        var question = url.IndexOf('?', StringComparison.Ordinal);
        return question < 0
            ? []
            : QueryHelpers.ParseQuery(url[question..]).ToDictionary(p => p.Key, p => p.Value.ToString());
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>An address of its own per request, so the per-address limits of other tests don't reach it.</summary>
    private static string Address() => $"198.23.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";
}
