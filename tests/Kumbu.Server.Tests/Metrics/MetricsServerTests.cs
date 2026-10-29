// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Metrics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Metrics;

[Collection("app")]
public sealed class MetricsServerTests(KumbuAppFactory app)
{
    [Fact]
    public async Task The_metrics_port_serves_metrics()
    {
        var text = await Scrape.TextAsync(app);

        Assert.Contains("# TYPE process_cpu_seconds_total", text);
        // The System.Runtime meter's dotnet.gc.collections, as the meter adapter names it.
        Assert.Contains("# TYPE system_runtime_dotnet_gc_collections", text);
    }

    // The metrics are not on the app's port: a proxy forwarding a request with Host: localhost:9464 must not reach them.
    [Fact]
    public async Task The_app_port_serves_no_metrics()
    {
        using var client = app.CreateClient();
        var plain = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("# TYPE", await plain.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Host = "localhost:9464";
        var spoofed = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("# TYPE", await spoofed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // A panel of a counter that has not counted yet shows a 0, not "No data".
    [Fact]
    public async Task Every_labelled_series_is_there_before_its_first_event()
    {
        var text = await Scrape.TextAsync(app);

        var labels = KumbuMetrics.AllowedLabels;
        // kind, via and reason are also label names of the attachment metrics, whose values the allowed sets then include.
        string[] errorKinds = ["invalid", "refused", "not_found", "conflict", "unavailable", "damaged", "other"];
        string[] pdfModes = ["info", "text", "images", "images_all", "crop"];
        string[] failReasons = ["timeout", "unavailable", "invalid", "failed"];
        string[] renderKinds = ["page", "image"];
        List<string> expected =
        [
            .. labels["tool"].SelectMany(t => new[] { $"kumbu_mcp_tool_calls_total{{tool=\"{t}\"}}", $"kumbu_mcp_tool_duration_seconds_count{{tool=\"{t}\"}}" }),
            .. labels["tool"].SelectMany(t => errorKinds.Select(k => $"kumbu_mcp_tool_errors_total{{tool=\"{t}\",kind=\"{k}\"}}")),
            .. labels["command"].Select(c => $"kumbu_sh_commands_total{{command=\"{c}\"}}"),
            .. KumbuMetrics.KnownCommands.Select(c => $"kumbu_sh_command_duration_seconds_count{{command=\"{c}\"}}"),
            .. new[] { "mcp", "api" }.Select(v => $"kumbu_write_conflicts_total{{via=\"{v}\"}}"),
            .. labels["provider_kind"].SelectMany(p => new[]
            {
                $"kumbu_embedding_batches_total{{provider_kind=\"{p}\"}}", $"kumbu_embedding_batch_duration_seconds_count{{provider_kind=\"{p}\"}}",
                $"kumbu_embedding_errors_total{{provider_kind=\"{p}\"}}",
            }),
            .. new[] { "idle", "memory" }.Select(r => $"kumbu_vault_locks_total{{reason=\"{r}\"}}"),
            .. labels["outcome"].Select(o => $"kumbu_sign_ins_total{{outcome=\"{o}\"}}"),
            .. labels["grant"].Select(g => $"kumbu_oauth_tokens_issued_total{{grant=\"{g}\"}}"),
            .. new[] { "text", "mixed", "image" }.Select(t => $"kumbu_pdf_pages_analysed_total{{type=\"{t}\"}}"),
            .. new[] { "text", "mixed", "image", "unknown" }.Select(t => $"kumbu_pdf_pages_rendered_total{{type=\"{t}\"}}"),
            .. failReasons.Select(r => $"kumbu_pdf_analysis_failures_total{{reason=\"{r}\"}}"),
            .. pdfModes.Select(m => $"kumbu_pdf_calls_total{{mode=\"{m}\"}}"),
            .. new[] { "text", "image" }.Select(a => $"kumbu_pdf_pages_delivered_total{{as=\"{a}\"}}"),
            .. new[] { "images_all", "crop" }.Select(v => $"kumbu_pdf_text_pages_rendered_total{{via=\"{v}\"}}"),
            .. new[] { "small", "medium", "large" }.Select(st => $"kumbu_img_calls_total{{step=\"{st}\"}}"),
            .. new[] { "note", "pdf", "not_found", "too_large", "bad_crop", "bad_size", "unavailable" }.Select(r => $"kumbu_img_refusals_total{{reason=\"{r}\"}}"),
            .. renderKinds.SelectMany(k => new[]
            {
                $"kumbu_render_duration_seconds_count{{kind=\"{k}\"}}", $"kumbu_image_delivered_bytes_count{{kind=\"{k}\"}}",
                $"kumbu_image_delivered_long_edge_pixels_count{{kind=\"{k}\"}}",
            }),
            .. renderKinds.SelectMany(k => failReasons.Select(r => $"kumbu_render_failures_total{{kind=\"{k}\",reason=\"{r}\"}}")),
            "kumbu_img_crops_total",
        ];
        var missing = expected.Where(series => !text.Contains(series + " ", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, string.Join('\n', missing));
    }

    [Fact]
    public void Disabled_metrics_open_no_port()
    {
        using var disabled = app.WithWebHostBuilder(b => b.UseSetting("Kumbu:Metrics:Enabled", "false"));

        Assert.Null(disabled.Services.GetService<MetricsServer>());
    }

    [Fact]
    public void Configure_is_idempotent()
    {
        KumbuMetrics.Configure();
        KumbuMetrics.Configure();
    }
}
