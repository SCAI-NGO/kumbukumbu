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

    /// <summary>A request with this share's username and another password.</summary>
    public Task<HttpResponseMessage> SendWithPasswordAsync(HttpMethod method, string path, string password, string? ip = null) =>
        SendAsync(Api, method, path, Basic(Username, password), ip: ip);

    /// <summary>
    /// A request to <paramref name="api"/> with <paramref name="authorization"/> as it is (none if null), from
    /// <paramref name="ip"/> (a fresh address if null, so no other test's wrong attempts count against it), and with
    /// <c>X-Forwarded-Proto: https</c> unless <paramref name="https"/> is false.
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
            if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        return await client.SendAsync(request, Ct);
    }

    /// <summary>An address of its own, for a test that counts attempts against one.</summary>
    public static string NewIp() => $"198.18.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    /// <summary>A WebDAV method HttpMethod has no constant for, such as <c>PROPFIND</c>.</summary>
    public static HttpMethod Method(string name) => new(name);

    public static readonly HttpMethod Propfind = new("PROPFIND");

    /// <summary>A body with a content type, for <see cref="SendAsync(HttpMethod, string, HttpContent?, IReadOnlyDictionary{string, string}?, string?)"/>.</summary>
    public static HttpContent Body(string text, string contentType = "application/xml") =>
        new StringContent(text, Encoding.UTF8, MediaTypeHeaderValue.Parse(contentType));
}
