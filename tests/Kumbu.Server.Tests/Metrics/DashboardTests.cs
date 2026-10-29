// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kumbu.Server.Metrics;
using ModelContextProtocol.Client;
using Prometheus;

namespace Kumbu.Server.Tests.Metrics;

[Collection("app")]
public sealed partial class DashboardTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"\b(kumbu_[a-z_]+|http_request[a-z_]*|process_[a-z_]+|dotnet_[a-z_]+|system_runtime_[a-z_]+)\b")]
    private static partial Regex Metric();

    [GeneratedRegex("_(bucket|sum|count)$")]
    private static partial Regex Suffix();

    [Fact]
    public async Task Every_metric_the_dashboard_queries_exists()
    {
        var file = Path.Combine(Path.GetDirectoryName(KumbuAppFactory.SamplesPath)!, "monitoring", "grafana", "dashboards", "kumbu.json");
        var dashboard = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!;
        var queried = Expressions(dashboard).SelectMany(e => Metric().Matches(e)).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

        // kumbu_http_rate_limited_total appears only after a 429 (its endpoint values are known to the running app alone), so the
        // kumbu_ series are checked against the metrics KumbuMetrics defines; the others against a scrape after an HTTP request and a sh call.
        await using (var client = await app.ConnectAsync())
        {
            await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "ls /" }, cancellationToken: Ct);
        }

        var text = await Scrape.TextAsync(app);
        var defined = Defined();
        Assert.NotEmpty(queried);
        bool Exported(string name) =>
            name.StartsWith("kumbu_", StringComparison.Ordinal) ? defined.Contains(name) : text.Contains($"# TYPE {name} ", StringComparison.Ordinal);

        // A histogram's _bucket, _sum and _count belong to its base name; a gauge may end in _count itself.
        Assert.All(queried, name => Assert.True(Exported(name) || Exported(Suffix().Replace(name, "")), $"{name} is queried but not exported"));
    }

    // A metric no panel shows is measured for nothing: the README lists every one under the dashboard's rows.
    [Fact]
    public async Task Every_metric_the_server_defines_has_a_panel()
    {
        var file = Path.Combine(Path.GetDirectoryName(KumbuAppFactory.SamplesPath)!, "monitoring", "grafana", "dashboards", "kumbu.json");
        var dashboard = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!;
        var queried = Expressions(dashboard).SelectMany(e => Metric().Matches(e)).Select(m => Suffix().Replace(m.Value, "")).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(Defined().Where(name => !queried.Contains(name)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_readme_lists_the_dashboard_s_rows_in_its_order()
    {
        var root = Path.GetDirectoryName(KumbuAppFactory.SamplesPath)!;
        var dashboard = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "monitoring", "grafana", "dashboards", "kumbu.json"), Ct))!;
        var rows = dashboard["panels"]!.AsArray().Where(p => (string?)p!["type"] == "row").Select(p => (string)p!["title"]!).ToList();
        var readme = await File.ReadAllLinesAsync(Path.Combine(root, "README.md"), Ct);
        var listed = readme.SkipWhile(l => !l.StartsWith("What is measured, in rows of the dashboard", StringComparison.Ordinal)).Skip(2)
            .TakeWhile(l => l.StartsWith("- **", StringComparison.Ordinal)).Select(l => l[4..l.IndexOf("**", 4, StringComparison.Ordinal)]).ToList();

        Assert.Equal(rows, listed);
    }

    // A PDF is analysed once per upload, a few a day: a 5-minute rate of that is mostly empty, and its spikes say nothing.
    [Fact]
    public async Task Per_upload_metrics_are_shown_over_an_hour_or_longer()
    {
        var file = Path.Combine(Path.GetDirectoryName(KumbuAppFactory.SamplesPath)!, "monitoring", "grafana", "dashboards", "kumbu.json");
        var dashboard = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!;
        string[] perUpload = ["kumbu_pdf_pages_analysed_total", "kumbu_pdf_page_", "kumbu_pdf_pages_per_document", "kumbu_pdf_analysis_"];

        var shortWindows = Expressions(dashboard).Where(e => perUpload.Any(m => e.Contains(m, StringComparison.Ordinal)) && e.Contains("[5m]", StringComparison.Ordinal));

        Assert.Empty(shortWindows);
    }

    private static IEnumerable<string> Expressions(JsonNode node) => node switch
    {
        JsonObject o => (o["targets"] is JsonArray targets ? targets.Select(t => (string?)t?["expr"]).OfType<string>() : [])
            .Concat(o.Where(p => p.Key != "targets" && p.Value is not null).SelectMany(p => Expressions(p.Value!))),
        JsonArray a => a.OfType<JsonNode>().SelectMany(Expressions),
        _ => [],
    };

    private static HashSet<string> Defined() =>
        typeof(KumbuMetrics).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(f => f.GetValue(null)).OfType<Collector>().Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
}
