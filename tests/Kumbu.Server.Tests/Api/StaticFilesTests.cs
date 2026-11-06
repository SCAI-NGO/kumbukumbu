// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Kumbu.Server.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>The web app: files from wwwroot, <c>index.html</c> for every other path outside <c>/api</c> and <c>/mcp</c>.</summary>
[Collection("app")]
public sealed class StaticFilesTests : IDisposable
{
    private const string Index = "<!doctype html><title>Kumbu test shell</title>";
    private const string ReaderPage = "<!doctype html><title>Kumbu test reader</title>";

    private readonly string _webRoot = Directory.CreateTempSubdirectory("kumbu-wwwroot-").FullName;
    private readonly WebApplicationFactory<Program> _web;

    public StaticFilesTests(KumbuAppFactory app)
    {
        // A fixture of its own, so the tests don't depend on a pnpm build of web/.
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), Index);
        File.WriteAllText(Path.Combine(_webRoot, "s.html"), ReaderPage);
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "assets", "app-abc123.js"), "console.log(1);");
        File.WriteAllText(Path.Combine(_webRoot, "manifest.webmanifest"), """{"name":"Kumbu²"}""");
        _web = app.WithWebHostBuilder(b => b.UseWebRoot(_webRoot));
    }

    public void Dispose()
    {
        _web.Dispose();
        Directory.Delete(_webRoot, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/notes/Arbeit/plan.md")]
    [InlineData("/signin")]
    public async Task The_shell_is_served_without_signing_in_and_is_never_cached(string path)
    {
        using var response = await _web.CreateClient().GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Index, await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains("no-cache", response.Headers.CacheControl!.ToString());
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task The_manifest_is_served_as_a_manifest_and_revalidated()
    {
        using var response = await _web.CreateClient().GetAsync("/manifest.webmanifest", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-cache", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Hashed_assets_are_cached_for_good()
    {
        using var response = await _web.CreateClient().GetAsync("/assets/app-abc123.js", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cache = response.Headers.CacheControl!;
        Assert.True(cache.Public);
        Assert.Equal(TimeSpan.FromDays(365), cache.MaxAge);
        Assert.Contains("immutable", cache.ToString());
    }

    [Theory]
    [InlineData("/assets/nope.js")]
    [InlineData("/assets/old/app-def456.css")]
    public async Task A_missing_asset_is_a_404_not_the_shell(string path)
    {
        // A tab from before a redeploy asks for its old chunks: the shell instead would fail as a script, cached.
        using var response = await _web.CreateClient().GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Kumbu test shell", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("/api/nope")]
    [InlineData("/api/vaults/x/y/z")]
    [InlineData("/mcp/nope")]
    public async Task The_fallback_never_answers_api_or_mcp_paths(string path)
    {
        var client = _web.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", KumbuAppFactory.Token);

        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("Kumbu test shell", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("/api/nope")]
    [InlineData("/mcp")]
    public async Task Api_and_mcp_paths_still_need_a_caller(string path)
    {
        using var response = await _web.CreateClient().GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_file_that_does_not_exist_with_an_extension_gets_the_shell_too()
    {
        // The router decides in the browser; the server only knows API paths.
        using var response = await _web.CreateClient().GetAsync("/notes/Arbeit/plan.md", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/s")]
    [InlineData("/s?x=1")]
    [InlineData("/s.html")]
    public async Task The_reader_page_has_its_headers(string path)
    {
        using var response = await _web.CreateClient().GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ReaderPage, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Security.ReaderPagePolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("noindex, nofollow", string.Join(", ", response.Headers.GetValues("X-Robots-Tag")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task A_head_of_the_reader_page_answers_as_its_get()
    {
        static string Headers(HttpResponseMessage r) => string.Join('\n', r.Headers.Concat(r.Content.Headers)
            .Where(h => h.Key != "Date").OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase).Select(h => $"{h.Key}: {string.Join(" | ", h.Value)}"));
        using var get = await _web.CreateClient().GetAsync("/s", Ct);
        using var head = await _web.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Head, "/s"), Ct);

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(Headers(get), Headers(head));
        Assert.Equal(Security.ReaderPagePolicy, Assert.Single(head.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task The_app_keeps_its_own_policy_and_the_reader_page_allows_nothing_inline()
    {
        using var response = await _web.CreateClient().GetAsync("/", Ct);

        Assert.Equal(Security.ContentSecurityPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.False(response.Headers.Contains("X-Robots-Tag"));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("no-cache", response.Headers.CacheControl!.ToString());
        Assert.DoesNotContain("'unsafe-inline'", Security.ReaderPagePolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-eval'", Security.ReaderPagePolicy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/session", "no-store")]
    [InlineData("/settings", "no-cache")]
    [InlineData("/sx", "no-cache")]
    public async Task Other_paths_carry_none_of_the_reader_headers(string path, string cache)
    {
        using var response = await _web.CreateClient().GetAsync(path, Ct);

        Assert.False(response.Headers.Contains("X-Robots-Tag"));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal(cache, response.Headers.CacheControl!.ToString());
        Assert.NotEqual(Security.ReaderPagePolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }
}
