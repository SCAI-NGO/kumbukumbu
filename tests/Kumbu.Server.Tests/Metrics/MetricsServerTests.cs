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
        // The runtime meter, as the adapter names it (dotnet.gc.collections).
        Assert.Contains("# TYPE dotnet_collection_count_total", text);
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
