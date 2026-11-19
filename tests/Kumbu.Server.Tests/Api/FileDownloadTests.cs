// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>
/// A file of kind <c>file</c> is only ever a download (files spec, decision 4): <c>attachment</c> whatever the request
/// asks, its stored media type, <c>nosniff</c> and the sandbox policy, from <c>/api/files</c> and a checkpoint alike; and
/// every listing names its kind.
/// </summary>
[Collection("app")]
public sealed class FileDownloadTests(KumbuAppFactory app)
{
    /// <summary>A page that would run its script if a browser rendered it: kept as text, by its content.</summary>
    internal static readonly byte[] Page = "<!doctype html>\n<html><body><script>alert(document.cookie)</script></body></html>\n"u8.ToArray();

    /// <summary>Bytes that aren't UTF-8 text.</summary>
    internal static readonly byte[] Bin = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x3C, 0x73, 0x63, 0x72, 0x69, 0x70, 0x74, 0x3E];

    internal const string Text = "text/plain; charset=utf-8";

    internal const string Bytes = "application/octet-stream";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>The four headers decision 4 asks for, and the bytes.</summary>
    internal static async Task AssertDownloadAsync(HttpResponseMessage response, string name, string mediaType, byte[]? bytes)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal(FileApi.Disposition(name, download: true), Assert.Single(response.Content.Headers.GetValues("Content-Disposition")));
        Assert.Equal(mediaType, Assert.Single(response.Content.Headers.GetValues("Content-Type")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(Security.FileContentPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        if (bytes is not null) Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task A_file_from_the_file_api_is_a_download_whatever_the_request_asks()
    {
        var user = await RestClient.NewAsync(Api);
        var pageVersion = await user.CreateFileAsync("site/page.html", Page);
        await user.CreateFileAsync("blob.bin", Bin);

        foreach (var query in new[] { "", "?download=1", "?download=0", "?download=inline", $"?version={pageVersion}" })
        {
            using var page = await user.GetAsync(user.Url("site/page.html", "files") + query);
            await AssertDownloadAsync(page, "page.html", Text, Page);
        }

        foreach (var query in new[] { "", "?download=1", "?download=0" })
        {
            using var bin = await user.GetAsync(user.Url("blob.bin", "files") + query);
            await AssertDownloadAsync(bin, "blob.bin", Bytes, Bin);
        }

        // A known format keeps its own: an image opens in the browser.
        await user.CreateFileAsync("shot.png", FileApiTests.Png(4, 4));
        using var png = await user.GetAsync(user.Url("shot.png", "files"));
        Assert.Equal("inline", png.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_file_from_a_checkpoint_is_a_download()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateFileAsync("page.html", Page);
        await user.CreateFileAsync("blob.bin", Bin);
        using var made = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "before" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        foreach (var query in new[] { "", "?download=1", "?download=0" })
        {
            using var page = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints/before/files/page.html{query}");
            await AssertDownloadAsync(page, "page.html", Text, Page);
            using var bin = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints/before/files/blob.bin{query}");
            await AssertDownloadAsync(bin, "blob.bin", Bytes, Bin);
        }

        using var tree = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints/before/tree");
        var entries = (await RestClient.JsonAsync(tree))["root"]!["notes"]!.AsArray();
        Assert.Equal("file", entries.Single(e => e!["path"]!.GetValue<string>() == "/Arbeit/page.html")!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Listings_name_the_kind_file()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateFileAsync("page.html", Page);
        await user.CreateFileAsync("blob.bin", Bin);

        using var tree = await user.GetAsync($"/api/vaults/{user.Vault}/tree");
        var files = (await RestClient.JsonAsync(tree))["root"]!["notes"]!.AsArray();
        AssertFile(files, "/Arbeit/page.html", Text);
        AssertFile(files, "/Arbeit/blob.bin", Bytes);
        Assert.Equal(Page.Length, files.Single(f => f!["path"]!.GetValue<string>() == "/Arbeit/page.html")!["size"]!.GetValue<long>());

        // Sync: the full list and an answer to since, text or not (text=1 is a skill folder's text files, not these).
        using var full = await user.GetAsync($"/api/vaults/{user.Vault}/versions?attachments=1");
        var fullBody = await RestClient.JsonAsync(full);
        Assert.Equal(["file", "file"], Kinds(fullBody, "/Arbeit/blob.bin", "/Arbeit/page.html"));
        using var since = await user.GetAsync($"/api/vaults/{user.Vault}/versions?attachments=1&since={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-1).ToString("O"))}");
        Assert.Equal(["file", "file"], Kinds(await RestClient.JsonAsync(since), "/Arbeit/blob.bin", "/Arbeit/page.html"));

        using var recent = await user.GetAsync("/api/recent");
        var items = (await RestClient.JsonAsync(recent))["items"]!.AsArray();
        Assert.Equal("file", items.Single(i => i!["path"]!.GetValue<string>() == "/Arbeit/page.html")!["kind"]!.GetValue<string>());

        using var history = await user.GetAsync(user.Url("page.html", "history"));
        var version = Assert.Single((await RestClient.JsonAsync(history)).AsArray())!;
        Assert.Equal("file", version["kind"]!.GetValue<string>());
        Assert.Equal(Text, version["mediaType"]!.GetValue<string>());
    }

    private static void AssertFile(JsonArray files, string path, string mediaType)
    {
        var file = files.Single(f => f!["path"]!.GetValue<string>() == path)!;
        Assert.Equal("file", file["kind"]!.GetValue<string>());
        Assert.Equal(mediaType, file["mediaType"]!.GetValue<string>());
    }

    private static string[] Kinds(JsonNode body, params string[] paths) =>
        [.. paths.Select(p => body["notes"]!.AsArray().SingleOrDefault(e => e!["path"]!.GetValue<string>() == p)?["kind"]?.GetValue<string>() ?? "missing")];
}
