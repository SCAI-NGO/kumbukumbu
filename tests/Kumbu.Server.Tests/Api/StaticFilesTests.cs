// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>The web app: files from wwwroot, <c>index.html</c> for every other path outside <c>/api</c> and <c>/mcp</c>.</summary>
[Collection("app")]
public sealed class StaticFilesTests : IDisposable
{
    private const string Index = "<!doctype html><title>Kumbu test shell</title>";

    private readonly string _webRoot = Directory.CreateTempSubdirectory("kumbu-wwwroot-").FullName;
    private readonly WebApplicationFactory<Program> _web;

    public StaticFilesTests(KumbuAppFactory app)
    {
        // A fixture of its own, so the tests don't depend on a pnpm build of web/.
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), Index);
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "assets", "app-abc123.js"), "console.log(1);");
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
}
