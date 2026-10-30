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

        // Each metric with labels has a series for every combination of its allowed values, a histogram as its _count; the
        // series of kumbu_http_rate_limited_total (route templates, known to the running app) exist only after a 429.
        var expected = KumbuMetrics.AllowedLabels.Where(m => m.Key != "kumbu_http_rate_limited_total" && m.Value.Count > 0)
            .SelectMany(m => Combinations(m.Value).Select(labels => (m.Key, labels))).ToList();
        Assert.NotEmpty(expected);
        var missing = expected
            .Where(e => !text.Contains($"{e.Key}{{{e.labels}}} ", StringComparison.Ordinal) && !text.Contains($"{e.Key}_count{{{e.labels}}} ", StringComparison.Ordinal))
            .Select(e => $"{e.Key}{{{e.labels}}}").ToList();
        Assert.True(missing.Count == 0, string.Join('\n', missing));
    }

    private static IEnumerable<string> Combinations(IReadOnlyDictionary<string, IReadOnlySet<string>> labels) =>
        labels.Aggregate<KeyValuePair<string, IReadOnlySet<string>>, IEnumerable<string>>([""], (soFar, label) =>
            soFar.SelectMany(prefix => label.Value.Select(v => $"{(prefix.Length == 0 ? "" : prefix + ",")}{label.Key}=\"{v}\"")));

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
