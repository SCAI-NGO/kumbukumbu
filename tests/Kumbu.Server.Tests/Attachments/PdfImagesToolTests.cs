// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Server.Accounts;
using Kumbu.Server.Attachments;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static Kumbu.Server.Tests.Attachments.PdfJobTests;

namespace Kumbu.Server.Tests.Attachments;

/// <summary><c>pdfimages</c> over MCP, end to end: an uploaded PDF, the shell, the fake sidecar, the stored attachments.</summary>
[Collection("app")]
public sealed class PdfImagesToolTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CallToolResult> ShAsync(WebApplicationFactory<Program> host, string key, string command)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(host.Server.BaseAddress, "mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
            },
            host.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct);
        return await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct);
    }

    /// <summary>A size as the shell gives it, for the small files here.</summary>
    private static string Size(int bytes) => bytes < 1024 ? $"{bytes} B" : $"{Math.Round(bytes / 1024.0)} KB";

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [Fact]
    public async Task The_images_of_a_page_are_stored_as_attachments_ls_lists()
    {
        var png = FakePdfSidecar.SolidPng(40, 30);
        var sidecar = new FakePdfSidecar
        {
            PageImages = (_, page) => FakePdfSidecar.CannedImages(page, 1),
            ExtractedImage = (_, _, _) => new Picture(png, "image/png", 40, 30, 40, 30),
        };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("x.pdf", Pdf("one page with a photo"));
        var key = await user.KeyAsync("pdfimages", KeyRight.Write);
        var vault = $"/{user.Vault}";

        var stored = await ShAsync(host, key, $"pdfimages {vault}/x.pdf {vault}/images/");
        var listed = await ShAsync(host, key, $"ls {vault}/images/");

        Assert.False(stored.IsError, Text(stored));
        Assert.Equal($"stored 1 image in {vault}/images/\n{vault}/images/x-p1-1.png  p1  40×30  {Size(png.Length)}  at 72,72,300,225", Text(stored));
        Assert.Equal((1, 1), Assert.Single(sidecar.ExtractRequests));
        Assert.False(listed.IsError, Text(listed));
        Assert.Equal($"x-p1-1.png  image 40×30 {Size(png.Length)}", Text(listed));
        using var file = await user.GetAsync(user.Url("images/x-p1-1.png", "files"));
        Assert.Equal(png, await file.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task A_region_is_stored_as_the_file_named()
    {
        var sidecar = new FakePdfSidecar { PageImages = (_, page) => FakePdfSidecar.CannedImages(page, 1) };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("x.pdf", Pdf("one page with a diagram"));
        var key = await user.KeyAsync("pdfimages", KeyRight.Write);
        var vault = $"/{user.Vault}";
        var png = FakePdfSidecar.SolidPng(600, 300);

        var stored = await ShAsync(host, key, $"pdfimages {vault}/x.pdf 1 --crop 72,72,144,72 \"{vault}/images/wiring diagram.png\"");
        var listed = await ShAsync(host, key, $"ls {vault}/images/");

        Assert.False(stored.IsError, Text(stored));
        Assert.Equal($"stored {vault}/images/wiring diagram.png  p1  600×300  {Size(png.Length)}  at 72,72,144,72", Text(stored));
        Assert.Equal((1, new Crop(72, 72, 144, 72), 0, false), Assert.Single(sidecar.RegionRequests));
        Assert.Equal($"wiring diagram.png  image 600×300 {Size(png.Length)}", Text(listed));
    }
}
