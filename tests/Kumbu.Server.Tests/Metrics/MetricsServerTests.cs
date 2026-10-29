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
        List<string> expected =
        [
            .. labels["tool"].SelectMany(t => new[] { $"kumbu_mcp_tool_calls_total{{tool=\"{t}\"}}", $"kumbu_mcp_tool_duration_seconds_count{{tool=\"{t}\"}}" }),
            .. labels["tool"].SelectMany(t => labels["kind"].Select(k => $"kumbu_mcp_tool_errors_total{{tool=\"{t}\",kind=\"{k}\"}}")),
            .. labels["command"].Select(c => $"kumbu_sh_commands_total{{command=\"{c}\"}}"),
            .. KumbuMetrics.KnownCommands.Select(c => $"kumbu_sh_command_duration_seconds_count{{command=\"{c}\"}}"),
            .. labels["via"].Select(v => $"kumbu_write_conflicts_total{{via=\"{v}\"}}"),
            .. labels["provider_kind"].SelectMany(p => new[]
            {
                $"kumbu_embedding_batches_total{{provider_kind=\"{p}\"}}", $"kumbu_embedding_batch_duration_seconds_count{{provider_kind=\"{p}\"}}",
                $"kumbu_embedding_errors_total{{provider_kind=\"{p}\"}}",
            }),
            .. labels["reason"].Select(r => $"kumbu_vault_locks_total{{reason=\"{r}\"}}"),
            .. labels["outcome"].Select(o => $"kumbu_sign_ins_total{{outcome=\"{o}\"}}"),
            .. labels["grant"].Select(g => $"kumbu_oauth_tokens_issued_total{{grant=\"{g}\"}}"),
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
