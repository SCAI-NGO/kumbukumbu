// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Dav;

/// <summary>
/// A WebDAV share of a <see cref="RestClient"/> user, made through <c>POST /api/dav-shares</c>, and the requests a WebDAV
/// client sends to <c>/dav</c>: HTTP Basic with the share's username and password, over plain http to the test server
/// but with <c>X-Forwarded-Proto: https</c>, as Traefik forwards it (the factory must trust the proxy, as
/// <see cref="KumbuAppFactory.PublicApi"/> does).
/// </summary>
public sealed class DavClient
{
    private DavClient(WebApplicationFactory<Program> api, RestClient owner, string id, string username, string password)
    {
        Api = api;
        Owner = owner;
        Id = id;
        Username = username;
        Password = password;
    }

    /// <summary>The server requests go to.</summary>
    public WebApplicationFactory<Program> Api { get; }

    /// <summary>The share's owner, signed in, with the vault the share was made for.</summary>
    public RestClient Owner { get; }

    /// <summary>The share's id, the four characters after the dot of its username.</summary>
    public string Id { get; }

    public string Username { get; }

    /// <summary>The password as the creating response showed it: in groups of four.</summary>
    public string Password { get; }

    /// <summary>The <c>Authorization</c> value of this share's credentials.</summary>
    public string Authorization => Basic(Username, Password);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A share of <paramref name="owner"/>'s vault (a new user with a vault "Work" if none is given), with
    /// <paramref name="right"/> there; or of <paramref name="vaults"/> (the owner's names for them, and the rights).</summary>
    public static async Task<DavClient> NewAsync(
        WebApplicationFactory<Program> api, RestClient? owner = null, string label = "Work PC", string right = "write", int? expiresInDays = null,
        IReadOnlyDictionary<string, string>? vaults = null)
    {
        owner ??= await RestClient.NewAsync(api, vault: "Work");
        vaults ??= new Dictionary<string, string> { [owner.Vault] = right };
        using var created = await owner.SendAsync(
            HttpMethod.Post, "/api/dav-shares", expiresInDays is null ? new { label, vaults } : new { label, vaults, expiresInDays });
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        var body = await RestClient.JsonAsync(created);
        var share = body["share"]!;
        return new DavClient(api, owner, share["id"]!.GetValue<string>(), share["username"]!.GetValue<string>(), body["password"]!.GetValue<string>());
    }

    /// <summary>The same share, its requests sent to another factory of the same database (e.g. one with other settings).</summary>
    public DavClient On(WebApplicationFactory<Program> api) => new(api, Owner, Id, Username, Password);

    /// <summary><c>Basic</c> and the base64 of UTF-8 <c>username:password</c> (RFC 7617).</summary>
    public static string Basic(string username, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

    /// <summary>A request with this share's credentials.</summary>
    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, HttpContent? content = null, IReadOnlyDictionary<string, string>? headers = null, string? ip = null) =>
        SendAsync(Api, method, path, Authorization, content, headers, ip);

    /// <summary>A <c>PROPFIND</c> with this share's credentials and <c>Depth</c> (none if null); <paramref name="headers"/>
    /// go beside it.</summary>
    public Task<HttpResponseMessage> PropfindAsync(
        string path, string? depth = "1", HttpContent? body = null, IReadOnlyDictionary<string, string>? headers = null, string? ip = null) =>
        SendAsync(Propfind, path, body, WithDepth(headers, depth), ip);

    /// <summary>A request with this share's username and another password.</summary>
    public Task<HttpResponseMessage> SendWithPasswordAsync(
        HttpMethod method, string path, string password, HttpContent? content = null, IReadOnlyDictionary<string, string>? headers = null, string? ip = null) =>
        SendAsync(Api, method, path, Basic(Username, password), content, headers, ip);

    /// <summary><paramref name="headers"/> with <c>Depth</c> set to <paramref name="depth"/>, or left as they are if it is null.</summary>
    public static IReadOnlyDictionary<string, string>? WithDepth(IReadOnlyDictionary<string, string>? headers, string? depth) =>
        depth is null ? headers : new Dictionary<string, string>(headers ?? new Dictionary<string, string>()) { ["Depth"] = depth };

    /// <summary>
    /// A request to <paramref name="api"/> with <paramref name="authorization"/> as it is (none if null), from
    /// <paramref name="ip"/> (a fresh address if null, so no other test's wrong attempts count against it), and with
    /// <c>X-Forwarded-Proto: https</c> unless <paramref name="https"/> is false. <paramref name="headers"/> go on the request
    /// or, for a content header (<c>Content-Type</c>, say), on <paramref name="content"/>; one that fits neither throws.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> api, HttpMethod method, string path, string? authorization, HttpContent? content = null,
        IReadOnlyDictionary<string, string>? headers = null, string? ip = null, bool https = true)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (https) request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", ip ?? NewIp());
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            if (request.Headers.TryAddWithoutValidation(name, value) || request.Content?.Headers.TryAddWithoutValidation(name, value) == true) continue;
            throw new ArgumentException($"The header {name} needs content to go on.", nameof(headers));
        }

        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        return await client.SendAsync(request, Ct);
    }

    private static int _ips;

    /// <summary>An address of its own, for a test that counts attempts against one: each call the next of
    /// 198.18.0.1 to 198.18.255.254, so no two tests of a run share one.</summary>
    public static string NewIp()
    {
        var n = Interlocked.Increment(ref _ips) - 1;
        return $"198.18.{n / 254 % 256}.{n % 254 + 1}";
    }

    // The WebDAV methods HttpMethod has no constant for, in one place.
    public static readonly HttpMethod Propfind = new("PROPFIND");
    public static readonly HttpMethod Proppatch = new("PROPPATCH");
    public static readonly HttpMethod Mkcol = new("MKCOL");
    public static readonly HttpMethod Copy = new("COPY");
    public static readonly HttpMethod Move = new("MOVE");
    public static readonly HttpMethod Lock = new("LOCK");
    public static readonly HttpMethod Unlock = new("UNLOCK");

    /// <summary>A body with a content type, for <see cref="SendAsync(HttpMethod, string, HttpContent?, IReadOnlyDictionary{string, string}?, string?)"/>.</summary>
    public static HttpContent Body(string text, string contentType = "application/xml") =>
        new StringContent(text, Encoding.UTF8, MediaTypeHeaderValue.Parse(contentType));

    /// <summary>A body without a length, sent as a chunked one is.</summary>
    public sealed class Unsized(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
