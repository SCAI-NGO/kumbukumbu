// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Server.Accounts;
using Kumbu.Server.Attachments;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static Kumbu.Server.Tests.Attachments.PdfJobTests;

namespace Kumbu.Server.Tests.Attachments;

/// <summary><c>pdf</c> and <c>cat</c> on a PDF over MCP, end to end: an uploaded PDF, its analysis, the shell, the fake sidecar.</summary>
[Collection("app")]
public sealed class PdfToolTests(KumbuAppFactory app)
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

    /// <summary>Three pages, the second a mixed one with a drawing.</summary>
    private static SidecarAnalysis Paper()
    {
        var canned = FakePdfSidecar.Canned(3);
        var pages = canned.Analysis.Pages.ToList();
        pages[1] = new PdfPage(2, 595, 842, 0, PageType.Mixed, 11, 0.1, 0, 0.4, 0, [new PdfRegion("drawing", 72, 300, 451, 260)]);
        return canned with { Analysis = canned.Analysis with { Pages = pages } };
    }

    private static string Text(ContentBlock block) => Assert.IsType<TextContentBlock>(block).Text;

    [Fact]
    public async Task An_analysed_pdf_reads_as_text_and_images_in_page_order()
    {
        var sidecar = new FakePdfSidecar { Analysis = _ => Paper() };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        var version = await user.CreateFileAsync("Samples/paper.pdf", Pdf("a paper with a chart"));
        var key = await user.KeyAsync("pdf", KeyRight.Read);
        var vault = await VaultAsync(host, user);
        var blobId = Convert.FromBase64String((await StateAsync(host, user, "Samples/paper.pdf")).BlobId);
        await EventuallyAsync(async () => await host.Services.GetRequiredService<PdfAnalyses>().HeaderAsync(vault, blobId, Ct) is not null, "the analysis is stored");
        var path = $"/{user.Vault}/Samples/paper.pdf";

        var info = await ShAsync(host, key, $"pdf info {path}");
        var cat = await ShAsync(host, key, $"cat {path}");
        var pages = await ShAsync(host, key, $"pdf {path} --images");

        Assert.False(info.IsError, Text(info.Content[0]));
        Assert.StartsWith("paper.pdf: PDF, 3 pages, ", Text(info.Content[0]));
        Assert.Contains("\nTitle: A title\nPages: 1 text, 2 mixed, 3 text\np2 mixed: vector drawing ≈ 40 % — regions (pt, x,y,w,h): drawing 72,300,451,260", Text(info.Content[0]));
        Assert.Matches(@"^paper\.pdf: PDF, 3 pages \(1 text, 2 mixed, 3 text\), \d+ B — read it with pdf \(help pdf\)$", Text(cat.Content[0]));

        Assert.False(pages.IsError, Text(pages.Content[0]));
        Assert.Equal(3, pages.Content.Count);
        Assert.StartsWith(
            "--- Page 1 of 3: text ---\nPage 1 text\n\n--- Page 2 of 3: mixed (text; vector drawing ≈ 40 % of the page) ---\nPage 2 text\n"
            + $"{path} (version {version}), page 2 of 3 (mixed): page 595×842 pt; medium: 920×1303 px PNG, ",
            Text(pages.Content[0]));
        var image = Assert.IsType<ImageContentBlock>(pages.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(FakePdfSidecar.SolidPng(920, 1303), image.DecodedData.ToArray());
        Assert.Equal("--- Page 3 of 3: text ---\nPage 3 text", Text(pages.Content[2]));
        Assert.Equal((2, SizeSteps.Of(SizeStep.Medium), (Crop?)null), Assert.Single(sidecar.PageRequests));
    }

    [Fact]
    public async Task Pending_with_the_sidecar_down_says_not_analysed_and_rendering_unavailable_without_throwing()
    {
        var sidecar = new FakePdfSidecar { Down = true };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("Samples/waiting.pdf", Pdf("waiting for the sidecar"));
        var key = await user.KeyAsync("pdf", KeyRight.Read);
        var path = $"/{user.Vault}/Samples/waiting.pdf";

        var text = await ShAsync(host, key, $"pdf {path}");
        var all = await ShAsync(host, key, $"pdf {path} 1 --images=all");
        var cat = await ShAsync(host, key, $"cat {path}");

        Assert.True(text.IsError);
        Assert.Equal($"pdf: {path} hasn't been analysed yet because PDF analysis is unavailable right now; try again later.", Text(Assert.Single(text.Content)));
        Assert.True(all.IsError);
        Assert.Equal("pdf: PDF rendering is unavailable right now; try again later", Text(Assert.Single(all.Content)));
        Assert.False(cat.IsError);
        Assert.EndsWith(" — not analysed yet; pdf reads it (help pdf)", Text(cat.Content[0]));
    }

    [Fact]
    public async Task Rg_finds_the_text_of_an_analysed_pdf_by_page()
    {
        await using var host = Host(app, new FakePdfSidecar { Analysis = _ => Paper() });
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("Samples/paper.pdf", Pdf("a paper to search"));
        var key = await user.KeyAsync("pdf", KeyRight.Read);
        var vault = await VaultAsync(host, user);
        var blobId = Convert.FromBase64String((await StateAsync(host, user, "Samples/paper.pdf")).BlobId);
        await EventuallyAsync(async () => await host.Services.GetRequiredService<PdfAnalyses>().HeaderAsync(vault, blobId, Ct) is not null, "the analysis is stored");
        var path = $"/{user.Vault}/Samples/paper.pdf";

        var found = await ShAsync(host, key, $"rg 'Page [23] text' /{user.Vault}");
        var count = await ShAsync(host, key, $"rg -c -i page {path}");

        Assert.False(found.IsError, Text(found.Content[0]));
        Assert.Equal($"{path}:p2: Page 2 text\n{path}:p3: Page 3 text", Text(Assert.Single(found.Content)));
        Assert.Equal($"{path}:3", Text(Assert.Single(count.Content)));
    }

    [Fact]
    public async Task A_crop_reaches_the_sidecar_with_the_same_numbers()
    {
        var sidecar = new FakePdfSidecar { Analysis = _ => Paper() };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("Samples/paper.pdf", Pdf("a paper to crop"));
        var key = await user.KeyAsync("pdf", KeyRight.Read);
        var path = $"/{user.Vault}/Samples/paper.pdf";

        var result = await ShAsync(host, key, $"pdf {path} 2 --crop 72,300,451,260 --size large");

        Assert.False(result.IsError, Text(result.Content[0]));
        Assert.Equal(2, result.Content.Count);
        Assert.Contains("page 2 of 3 (mixed): page 595×842 pt; crop 72,300,451,260; large: ", Text(result.Content[0]));
        Assert.Equal((2, SizeSteps.Of(SizeStep.Large), (Crop?)new Crop(72, 300, 451, 260)), Assert.Single(sidecar.PageRequests));
    }
}
