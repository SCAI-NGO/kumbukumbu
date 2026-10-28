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

        // The labelled kumbu_ series appear only once something has counted (a vault lock, an OAuth token), so they
        // are checked against the metrics KumbuMetrics defines; the others against a scrape after an HTTP request and a sh call.
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
