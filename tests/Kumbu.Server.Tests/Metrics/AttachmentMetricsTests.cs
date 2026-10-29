// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Server.Accounts;
using Kumbu.Server.Attachments;
using Kumbu.Server.Metrics;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Attachments;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using static Kumbu.Server.Tests.Attachments.PdfJobTests;

namespace Kumbu.Server.Tests.Metrics;

/// <summary>What <c>img</c> and <c>pdf</c> and the analyses leave in the metrics, over MCP with the sidecar faked.</summary>
[Collection("app")]
public sealed class AttachmentMetricsTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task ShAsync(WebApplicationFactory<Program> host, string key, string command)
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
        await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct);
    }

    /// <summary>Three pages, the second a mixed one with a drawing.</summary>
    private static SidecarAnalysis Paper()
    {
        var canned = FakePdfSidecar.Canned(3);
        var pages = canned.Analysis.Pages.ToList();
        pages[1] = new PdfPage(2, 595, 842, 0, PageType.Mixed, 11, 0.1, 0, 0.4, 0, [new PdfRegion("drawing", 72, 300, 451, 260)]);
        return canned with { Analysis = canned.Analysis with { Pages = pages } };
    }

    [Fact]
    public async Task The_analysis_duration_has_buckets_up_to_the_twenty_minutes_an_analysis_may_take()
    {
        var scrape = await Scrape.TextAsync(app);

        foreach (var le in new[] { "30", "60", "120", "300", "600", "1200" })
        {
            Assert.Contains($"kumbu_pdf_analysis_duration_seconds_bucket{{le=\"{le}\"}}", scrape);
        }
    }

    [Fact]
    public async Task An_analysis_leaves_its_pages_and_duration()
    {
        string[] series =
        [
            "kumbu_pdf_pages_analysed_total{type=\"text\"}", "kumbu_pdf_pages_analysed_total{type=\"mixed\"}", "kumbu_pdf_pages_analysed_total{type=\"image\"}",
            "kumbu_pdf_page_text_coverage_ratio_count", "kumbu_pdf_page_image_coverage_ratio_count", "kumbu_pdf_page_vector_coverage_ratio_count",
            "kumbu_pdf_page_vector_coverage_ratio_bucket{le=\"0.4\"}", "kumbu_pdf_page_chars_count", "kumbu_pdf_pages_per_document_count",
            "kumbu_pdf_pages_per_document_bucket{le=\"5\"}", "kumbu_pdf_analysis_duration_seconds_count",
        ];
        var before = await Scrape.TextAsync(app);
        var sidecar = new FakePdfSidecar { Analysis = _ => Paper() };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("Samples/paper.pdf", Pdf("a paper for the metrics"));
        var vault = await VaultAsync(host, user);
        var blobId = Convert.FromBase64String((await StateAsync(host, user, "Samples/paper.pdf")).BlobId);
        await EventuallyAsync(async () => await host.Services.GetRequiredService<PdfAnalyses>().HeaderAsync(vault, blobId, Ct) is not null, "the analysis is stored");

        var after = await Scrape.TextAsync(app);

        double Rise(string s) => Scrape.Value(after, s) - Scrape.Value(before, s);
        Assert.Equal(2, Rise(series[0]));
        Assert.Equal(1, Rise(series[1]));
        Assert.Equal(0, Rise(series[2]));
        Assert.Equal(3, Rise(series[3]));
        Assert.Equal(3, Rise(series[4]));
        Assert.Equal(3, Rise(series[5]));
        Assert.Equal(3, Rise(series[6]));
        Assert.Equal(3, Rise(series[7]));
        Assert.Equal(1, Rise(series[8]));
        Assert.Equal(1, Rise(series[9]));
        Assert.Equal(1, Rise(series[10]));
    }

    [Fact]
    public async Task A_failed_analysis_is_counted_by_reason()
    {
        const string series = "kumbu_pdf_analysis_failures_total{reason=\"timeout\"}";
        var before = Scrape.Value(await Scrape.TextAsync(app), series);
        await using var host = Host(app, new FakePdfSidecar { FailWith = SidecarException.Timeout });
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("Samples/slow.pdf", Pdf("a document that takes too long"));

        await EventuallyAsync(async () => Scrape.Value(await Scrape.TextAsync(app), series) >= before + 1, "the failure is counted");
    }

    [Fact]
    public async Task Img_and_pdf_calls_are_counted_with_the_labels_of_their_sets()
    {
        var sidecar = new FakePdfSidecar { Analysis = _ => Paper() };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("Samples/paper.pdf", Pdf("a paper to read"));
        await user.CreateFileAsync("Samples/shot.png", FakePdfSidecar.SolidPng(1440, 3120));
        var key = await user.KeyAsync("metrics", KeyRight.Read);
        var vault = await VaultAsync(host, user);
        var blobId = Convert.FromBase64String((await StateAsync(host, user, "Samples/paper.pdf")).BlobId);
        await EventuallyAsync(async () => await host.Services.GetRequiredService<PdfAnalyses>().HeaderAsync(vault, blobId, Ct) is not null, "the analysis is stored");
        var pdf = $"/{user.Vault}/Samples/paper.pdf";
        var png = $"/{user.Vault}/Samples/shot.png";
        var before = await Scrape.TextAsync(app);

        foreach (var command in new[]
        {
            $"img {png}", $"img {png} --size small --crop 0,0,100,100", $"img {pdf}", $"img {png} --size huge", $"img /{user.Vault}/Samples/none.png",
            $"img {png} --crop 0,0,9999,9999", $"pdf info {pdf}", $"pdf {pdf}", $"pdf {pdf} --images", $"pdf {pdf} 1 --images=all", $"pdf {pdf} 3 --crop 0,0,100,100",
        })
        {
            await ShAsync(host, key, command);
        }

        var after = await Scrape.TextAsync(app);
        double Rise(string s) => Scrape.Value(after, s) - Scrape.Value(before, s);

        Assert.Equal(1, Rise("kumbu_img_calls_total{step=\"medium\"}"));
        Assert.Equal(1, Rise("kumbu_img_calls_total{step=\"small\"}"));
        Assert.Equal(1, Rise("kumbu_img_crops_total"));
        Assert.Equal(1, Rise("kumbu_img_refusals_total{reason=\"pdf\"}"));
        Assert.Equal(1, Rise("kumbu_img_refusals_total{reason=\"bad_size\"}"));
        Assert.Equal(1, Rise("kumbu_img_refusals_total{reason=\"not_found\"}"));
        Assert.Equal(1, Rise("kumbu_img_refusals_total{reason=\"bad_crop\"}"));
        Assert.Equal(1, Rise("kumbu_pdf_calls_total{mode=\"info\"}"));
        Assert.Equal(1, Rise("kumbu_pdf_calls_total{mode=\"text\"}"));
        Assert.Equal(1, Rise("kumbu_pdf_calls_total{mode=\"images\"}"));
        Assert.Equal(1, Rise("kumbu_pdf_calls_total{mode=\"images_all\"}"));
        Assert.Equal(1, Rise("kumbu_pdf_calls_total{mode=\"crop\"}"));
        // Text: three pages by "pdf", three by "--images", one by "--images=all" (page 1).
        Assert.Equal(7, Rise("kumbu_pdf_pages_delivered_total{as=\"text\"}"));
        Assert.Equal(3, Rise("kumbu_pdf_pages_delivered_total{as=\"image\"}"));
        Assert.Equal(1, Rise("kumbu_pdf_pages_rendered_total{type=\"mixed\"}"));
        Assert.Equal(2, Rise("kumbu_pdf_pages_rendered_total{type=\"text\"}"));
        Assert.Equal(0, Rise("kumbu_pdf_pages_rendered_total{type=\"unknown\"}"));
        Assert.Equal(1, Rise("kumbu_pdf_text_pages_rendered_total{via=\"images_all\"}"));
        Assert.Equal(1, Rise("kumbu_pdf_text_pages_rendered_total{via=\"crop\"}"));
        Assert.Equal(3, Rise("kumbu_render_duration_seconds_count{kind=\"page\"}"));
        Assert.Equal(2, Rise("kumbu_render_duration_seconds_count{kind=\"image\"}"));
        Assert.Equal(3, Rise("kumbu_image_delivered_bytes_count{kind=\"page\"}"));
        Assert.Equal(2, Rise("kumbu_image_delivered_bytes_count{kind=\"image\"}"));
        Assert.Equal(3, Rise("kumbu_image_delivered_long_edge_pixels_count{kind=\"page\"}"));
        Assert.Equal(2, Rise("kumbu_image_delivered_long_edge_pixels_count{kind=\"image\"}"));
        Assert.Contains("kumbu_image_delivered_long_edge_pixels_bucket{kind=\"image\",le=\"2576\"}", after);
        Assert.Contains("kumbu_render_failures_total{kind=\"page\",reason=\"unavailable\"}", after);
    }

    [Fact]
    public async Task A_render_that_fails_is_counted_by_kind_and_reason()
    {
        const string series = "kumbu_render_failures_total{kind=\"image\",reason=\"unavailable\"}";
        const string refusal = "kumbu_img_refusals_total{reason=\"unavailable\"}";
        var sidecar = new FakePdfSidecar { Down = true };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("Samples/shot.png", FakePdfSidecar.SolidPng(640, 480));
        var key = await user.KeyAsync("metrics", KeyRight.Read);
        var before = await Scrape.TextAsync(app);

        await ShAsync(host, key, $"img /{user.Vault}/Samples/shot.png");

        var after = await Scrape.TextAsync(app);
        Assert.Equal(1, Scrape.Value(after, series) - Scrape.Value(before, series));
        Assert.Equal(1, Scrape.Value(after, refusal) - Scrape.Value(before, refusal));
    }

    [Fact]
    public void A_request_the_client_got_wrong_is_no_render_failure()
    {
        Assert.True(KumbuMetrics.IsRequestMistake(new SidecarException("no_such_page", "x")));
        Assert.True(KumbuMetrics.IsRequestMistake(new SidecarException("bad_crop", "x")));
        Assert.False(KumbuMetrics.IsRequestMistake(new SidecarException(SidecarException.Timeout, "x")));
        Assert.Equal(KumbuMetrics.FailReason.Invalid, KumbuMetrics.FailReasonOf(new SidecarException(SidecarException.Encrypted, "x")));
        Assert.Equal(KumbuMetrics.FailReason.Failed, KumbuMetrics.FailReasonOf(new SidecarException("other", "x")));
    }
}
