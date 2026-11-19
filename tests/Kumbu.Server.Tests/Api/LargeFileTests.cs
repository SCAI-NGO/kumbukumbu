// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Core.Attachments;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Api;

/// <summary>What the tests of files larger than 15 MB share: their bytes, a body that streams them, a server with a
/// low bound.</summary>
internal static class LargeFiles
{
    /// <summary>The bound of <see cref="Bounded"/>: the least the validator allows is 15 MiB.</summary>
    public const long Bound = 20 * 1024 * 1024;

    public const int Size = 40_000_000;

    /// <summary><paramref name="size"/> bytes of <paramref name="seed"/>'s noise, starting with <paramref name="start"/>.</summary>
    public static byte[] Of(int size, int seed, ReadOnlySpan<byte> start = default)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        start.CopyTo(bytes);
        return bytes;
    }

    /// <summary>A server of the same database whose bound per file is <see cref="Bound"/>.</summary>
    public static WebApplicationFactory<Program> Bounded(KumbuAppFactory app) =>
        app.PublicApi.WithWebHostBuilder(b => b.UseSetting("Kumbu:Vaults:MaxFileBytes", Bound.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>The bytes as a body sent in 64 KiB pieces, with a Content-Length if <paramref name="declare"/>, else chunked.</summary>
    public sealed class Streamed(byte[] bytes, bool declare) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var at = 0; at < bytes.Length; at += 64 * 1024) await stream.WriteAsync(bytes.AsMemory(at, Math.Min(64 * 1024, bytes.Length - at)));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return declare;
        }
    }

    /// <summary>A body that declares <paramref name="declared"/> bytes and sends none until the test is over: a server that
    /// answers it answered before reading it.</summary>
    public sealed class Withheld(long declared) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.Infinite, TestContext.Current.CancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = declared;
            return true;
        }
    }
}

/// <summary>Files larger than 15 MB through every way in and out (files spec, decisions 11–13): streamed to the store up
/// to the bound, streamed out, and kept but not read by what reads a file whole.</summary>
[Collection("app")]
public sealed class LargeFileTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static async Task<JsonNode> StoredAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode} {body}");
        return JsonNode.Parse(body)!;
    }

    private static async Task AssertDownloadsAsync(RestClient user, string path, byte[] bytes)
    {
        using var get = await user.GetAsync(user.Url(path, "files"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(bytes.Length, get.Content.Headers.ContentLength);
        var read = await get.Content.ReadAsByteArrayAsync(Ct);
        Assert.True(bytes.AsSpan().SequenceEqual(read), $"{path} came back other than it went in");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_40_MB_file_is_stored_and_downloaded_byte_for_byte(bool declare)
    {
        var user = await RestClient.NewAsync(Api);
        var bytes = LargeFiles.Of(LargeFiles.Size, 1);

        using var put = await user.SendContentAsync(HttpMethod.Put, user.Url("big.bin", "files"), new LargeFiles.Streamed(bytes, declare), followRedirects: false);

        var state = await StoredAsync(put);
        Assert.Equal(LargeFiles.Size, state["size"]!.GetValue<long>());
        Assert.Equal(AttachmentFormat.OctetStream, state["mediaType"]!.GetValue<string>());
        Assert.Equal(Kumbu.Core.Notes.VersionToken.Of(bytes), state["version"]!.GetValue<string>());
        await AssertDownloadsAsync(user, "big.bin", bytes);
    }

    [Fact]
    public async Task A_large_file_is_overwritten_with_its_version()
    {
        var user = await RestClient.NewAsync(Api);
        using var first = await user.SendContentAsync(HttpMethod.Put, user.Url("big.bin", "files"), new LargeFiles.Streamed(LargeFiles.Of(LargeFiles.Size, 2), true), followRedirects: false);
        var version = (await StoredAsync(first))["version"]!.GetValue<string>();
        var bytes = LargeFiles.Of(LargeFiles.Size, 3);

        using var stale = await user.SendContentAsync(HttpMethod.Put, user.Url("big.bin", "files"), new LargeFiles.Streamed(bytes, true), followRedirects: false);
        using var second = await user.SendContentAsync(
            HttpMethod.Put, user.Url("big.bin", "files") + $"?version={version}", new LargeFiles.Streamed(bytes, false), followRedirects: false);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await StoredAsync(second, HttpStatusCode.OK);
        await AssertDownloadsAsync(user, "big.bin", bytes);
    }

    [Fact]
    public async Task A_large_pdf_is_kept_as_a_pdf_and_one_without_its_magic_is_refused()
    {
        var user = await RestClient.NewAsync(Api);
        var pdf = LargeFiles.Of(LargeFiles.Size, 4, FileApiTests.Pdf);
        var fake = LargeFiles.Of(LargeFiles.Size, 5);

        using var put = await user.SendContentAsync(HttpMethod.Put, user.Url("big.pdf", "files"), new LargeFiles.Streamed(pdf, true), followRedirects: false);
        using var refused = await user.SendContentAsync(HttpMethod.Put, user.Url("fake.pdf", "files"), new LargeFiles.Streamed(fake, false), followRedirects: false);

        Assert.Equal("application/pdf", (await StoredAsync(put))["mediaType"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("fake.pdf isn't a PDF document", await RestClient.ErrorAsync(refused), StringComparison.Ordinal);
        using var get = await user.GetAsync(user.Url("fake.pdf", "files"));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task A_large_image_above_the_pixel_limit_is_kept_with_its_size()
    {
        // Nothing decodes an image larger than 15 MB (decision 12), so the 50 megapixels that guard the decoder don't apply.
        var user = await RestClient.NewAsync(Api);
        var png = LargeFiles.Of(16 * 1024 * 1024, 13, FileApiTests.Png(10_000, 10_000));

        using var put = await user.SendContentAsync(HttpMethod.Put, user.Url("poster.png", "files"), new LargeFiles.Streamed(png, true), followRedirects: false);

        var state = await StoredAsync(put);
        Assert.Equal("image/png", state["mediaType"]!.GetValue<string>());
        Assert.Equal(10_000, state["width"]!.GetValue<int>());
        Assert.Equal(10_000, state["height"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_large_svg_is_a_413_saying_why()
    {
        var user = await RestClient.NewAsync(Api);
        var svg = LargeFiles.Of(LargeFiles.Size, 6, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1\" height=\"1\">"u8);

        using var put = await user.SendContentAsync(HttpMethod.Put, user.Url("big.svg", "files"), new LargeFiles.Streamed(svg, true), followRedirects: false);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, put.StatusCode);
        Assert.Contains("an SVG image is read whole to be checked, so it can have at most 15 MB", await RestClient.ErrorAsync(put), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_img_rg_and_cat_say_a_large_file_is_kept_but_not_read()
    {
        var user = await RestClient.NewAsync(Api);
        var key = await user.KeyAsync("Claude", KeyRight.Write);
        using var pdf = await user.SendContentAsync(
            HttpMethod.Put, user.Url("big.pdf", "files"), new LargeFiles.Streamed(LargeFiles.Of(LargeFiles.Size, 7, FileApiTests.Pdf), true), followRedirects: false);
        using var png = await user.SendContentAsync(
            HttpMethod.Put, user.Url("big.png", "files"), new LargeFiles.Streamed(LargeFiles.Of(LargeFiles.Size, 8, FileApiTests.Png(4000, 3000)), true), followRedirects: false);
        await StoredAsync(pdf);
        Assert.Equal(4000, (await StoredAsync(png))["width"]!.GetValue<int>());

        var (pdfError, pdfText) = await user.ShAsync("pdf /Arbeit/big.pdf", key);
        var (crop, cropText) = await user.ShAsync("pdf /Arbeit/big.pdf 1 --crop 0,0,10,10", key);
        var (imgError, imgText) = await user.ShAsync("img /Arbeit/big.png", key);
        var (rgError, rgText) = await user.ShAsync("rg anything /Arbeit/big.pdf", key);
        var (_, catPdf) = await user.ShAsync("cat /Arbeit/big.pdf", key);
        var (_, catPng) = await user.ShAsync("cat /Arbeit/big.png", key);

        Assert.True(pdfError);
        Assert.Equal($"pdf: /Arbeit/big.pdf is {AttachmentFormat.NotRead}", pdfText);
        Assert.True(crop);
        Assert.Equal($"pdf: /Arbeit/big.pdf is {AttachmentFormat.NotRead}", cropText);
        Assert.True(imgError);
        Assert.Equal($"img: /Arbeit/big.png is {AttachmentFormat.NotRead}", imgText);
        Assert.True(rgError);
        Assert.Equal($"rg: /Arbeit/big.pdf is {AttachmentFormat.NotRead}", rgText);
        Assert.Equal($"big.pdf: PDF, 38.1 MB — {AttachmentFormat.NotRead}", catPdf);
        Assert.Equal($"big.png: image (PNG), 4000×3000, 38.1 MB — {AttachmentFormat.NotRead}", catPng);
    }

    [Fact]
    public async Task Over_the_bound_is_a_413_naming_it_before_the_body_is_read_when_declared()
    {
        await using var bounded = LargeFiles.Bounded(app);
        var user = await RestClient.NewAsync(bounded);

        using var declared = await user.SendContentAsync(HttpMethod.Put, user.Url("big.bin", "files"), new LargeFiles.Withheld(LargeFiles.Bound + 1), followRedirects: false)
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using var chunked = await user.SendContentAsync(
            HttpMethod.Put, user.Url("big.bin", "files"), new LargeFiles.Streamed(LargeFiles.Of(21 * 1024 * 1024, 9), false), followRedirects: false);
        using var limit = await user.SendContentAsync(
            HttpMethod.Put, user.Url("limit.bin", "files"), new LargeFiles.Streamed(LargeFiles.Of((int)LargeFiles.Bound, 10), false), followRedirects: false);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal("A file can have at most 20 MiB.", await RestClient.ErrorAsync(declared));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        Assert.Equal("A file can have at most 20 MiB.", await RestClient.ErrorAsync(chunked));
        await StoredAsync(limit);
        using var get = await user.GetAsync(user.Url("big.bin", "files"));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task The_session_names_the_bound_per_file()
    {
        await using var bounded = LargeFiles.Bounded(app);

        async Task<long> BoundAsync(WebApplicationFactory<Program> api)
        {
            var user = await RestClient.NewAsync(api);
            using var session = await user.GetAsync("/api/session");
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
            return (await RestClient.JsonAsync(session))["maxFileBytes"]!.GetValue<long>();
        }

        Assert.Equal(1L << 30, await BoundAsync(Api));
        Assert.Equal(LargeFiles.Bound, await BoundAsync(bounded));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_agent_uploads_a_40_MB_file(bool declare)
    {
        var user = await RestClient.NewAsync(Api);
        var key = await user.KeyAsync("Claude", KeyRight.Write);
        var tool = await user.ToolAsync("upload", new Dictionary<string, object?> { ["path"] = "/Arbeit/big.pdf" }, key);
        var url = Assert.IsType<TextContentBlock>(tool.Content[0]).Text.Split('\n')[0];
        var bytes = LargeFiles.Of(LargeFiles.Size, 11, FileApiTests.Pdf);

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(url).PathAndQuery) { Content = new LargeFiles.Streamed(bytes, declare) };
        request.Headers.Add("X-Forwarded-For", $"198.21.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("https://kumbu.test") });
        using var put = await client.SendAsync(request, Ct);

        Assert.Equal(LargeFiles.Size, (await StoredAsync(put))["file"]!["size"]!.GetValue<long>());
        await AssertDownloadsAsync(user, "big.pdf", bytes);
    }

    [Fact]
    public async Task The_zip_export_holds_a_large_file_whole()
    {
        var user = await RestClient.NewAsync(Api);
        var bytes = LargeFiles.Of(LargeFiles.Size, 12);
        using var put = await user.SendContentAsync(HttpMethod.Put, user.Url("big.bin", "files"), new LargeFiles.Streamed(bytes, true), followRedirects: false);
        await StoredAsync(put);
        using var ticket = await user.SendAsync(HttpMethod.Post, "/api/export/ticket", new { password = RestClient.Password });
        var code = (await RestClient.JsonAsync(ticket))["ticket"]!.GetValue<string>();

        using var export = await user.GetAsync($"/api/vaults/{user.Vault}/export?ticket={Uri.EscapeDataString(code)}");

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var zip = new ZipArchive(await export.Content.ReadAsStreamAsync(Ct), ZipArchiveMode.Read);
        var entry = Assert.Single(zip.Entries, e => e.Name == "big.bin");
        using var read = new MemoryStream();
        await using (var stream = await entry.OpenAsync(Ct)) await stream.CopyToAsync(read, Ct);
        Assert.True(bytes.AsSpan().SequenceEqual(read.ToArray()));
    }

    [Fact]
    public async Task A_chunk_that_doesnt_open_mid_download_ends_it_with_an_error_not_a_clean_end()
    {
        var user = await RestClient.NewAsync(Api);
        using var put = await user.SendContentAsync(HttpMethod.Put, user.Url("big.bin", "files"), new LargeFiles.Streamed(LargeFiles.Of(LargeFiles.Size, 13), true), followRedirects: false);
        await StoredAsync(put);
        var vault = (await Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault).Vault;
        var chunks = app.Database.GetCollection<BsonDocument>("blob_chunks");
        var f = Builders<BsonDocument>.Filter;
        var third = await chunks.Find(f.Eq("vault", vault.Id) & f.Eq("n", 2)).SingleAsync(Ct);
        var sealedBytes = third["blob"].AsByteArray;
        sealedBytes[^1] ^= 0xFF;
        await chunks.UpdateOneAsync(f.Eq("_id", third["_id"]), Builders<BsonDocument>.Update.Set("blob", sealedBytes), cancellationToken: Ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, user.Url("big.bin", "files"));
        request.Headers.Add("Cookie", $"{Kumbu.Server.Auth.CallerMiddleware.SessionCookie}={user.Session}");
        request.Headers.Add("Origin", RestClient.Origin);
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        using var get = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);

        // The headers went out with the first chunks; what follows must not look like the whole file.
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(LargeFiles.Size, get.Content.Headers.ContentLength);
        var read = 0L;
        var failed = await Record.ExceptionAsync(async () =>
        {
            await using var body = await get.Content.ReadAsStreamAsync(Ct);
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer, Ct)) > 0) read += n;
        });
        Assert.NotNull(failed);
        Assert.True(read < LargeFiles.Size, $"read {read} bytes");
    }
}

/// <summary>Large files through a real Kestrel, which holds a body to a limit and resets a response that fails half way
/// (TestServer does neither): the limits lifted for a file's upload, the abort a client sees.</summary>
[Collection("app")]
public sealed class LargeFileKestrelTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient Client(WebApplicationFactory<Program> real) =>
        new() { BaseAddress = new Uri(real.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };

    private WebApplicationFactory<Program> Kestrel()
    {
        var real = app.PublicApi.WithWebHostBuilder(b => b.UseUrls("http://127.0.0.1:0"));
        real.UseKestrel();
        real.StartServer();
        return real;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string authorization, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", Dav.DavClient.NewIp());
        return request;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_40_MB_upload_goes_through_kestrels_limit(bool declare)
    {
        var user = await RestClient.NewAsync(app.PublicApi);
        var key = await user.KeyAsync("Claude", KeyRight.Write);
        await using var real = Kestrel();
        using var client = Client(real);
        var bytes = LargeFiles.Of(LargeFiles.Size, 31);

        using var request = Request(HttpMethod.Put, user.Url("big.bin", "files"), $"Bearer {key}", new LargeFiles.Streamed(bytes, declare));
        using var put = await client.SendAsync(request, Ct);

        Assert.True(put.StatusCode == HttpStatusCode.Created, await put.Content.ReadAsStringAsync(Ct));
        using var get = await client.SendAsync(Request(HttpMethod.Get, user.Url("big.bin", "files"), $"Bearer {key}"), Ct);
        var read = await get.Content.ReadAsByteArrayAsync(Ct);
        Assert.True(bytes.AsSpan().SequenceEqual(read));
    }

    [Fact]
    public async Task A_dav_put_above_the_64_MiB_of_other_requests_goes_through_kestrel()
    {
        var dav = await Dav.DavClient.NewAsync(app.PublicApi);
        await using var real = Kestrel();
        using var client = Client(real);

        using var put = await client.SendAsync(Request(HttpMethod.Put, "/dav/Work/big.bin", dav.Authorization, new LargeFiles.Streamed(new byte[70 * 1024 * 1024], false)), Ct);

        Assert.True(put.StatusCode == HttpStatusCode.Created, await put.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_chunk_that_doesnt_open_mid_download_resets_the_response()
    {
        var user = await RestClient.NewAsync(app.PublicApi);
        var key = await user.KeyAsync("Claude", KeyRight.Write);
        using var put = await user.SendContentAsync(HttpMethod.Put, user.Url("big.bin", "files"), new LargeFiles.Streamed(LargeFiles.Of(LargeFiles.Size, 32), true), followRedirects: false);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var vault = (await app.PublicApi.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault).Vault;
        var chunks = app.Database.GetCollection<BsonDocument>("blob_chunks");
        var f = Builders<BsonDocument>.Filter;
        var third = await chunks.Find(f.Eq("vault", vault.Id) & f.Eq("n", 2)).SingleAsync(Ct);
        var sealedBytes = third["blob"].AsByteArray;
        sealedBytes[^1] ^= 0xFF;
        await chunks.UpdateOneAsync(f.Eq("_id", third["_id"]), Builders<BsonDocument>.Update.Set("blob", sealedBytes), cancellationToken: Ct);
        await using var real = Kestrel();
        using var client = Client(real);

        using var get = await client.SendAsync(Request(HttpMethod.Get, user.Url("big.bin", "files"), $"Bearer {key}"), HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(LargeFiles.Size, get.Content.Headers.ContentLength);
        var read = 0L;
        var failed = await Record.ExceptionAsync(async () =>
        {
            await using var body = await get.Content.ReadAsStreamAsync(Ct);
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer, Ct)) > 0) read += n;
        });
        Assert.IsAssignableFrom<IOException>(failed is HttpRequestException { InnerException: { } inner } ? inner : failed);
        Assert.True(read < LargeFiles.Size, $"read {read} bytes");
    }
}
