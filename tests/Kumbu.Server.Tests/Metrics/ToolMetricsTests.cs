// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kumbu.Server.Metrics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Metrics;

[Collection("app")]
public sealed class ToolMetricsTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Command_labels_are_known_names_or_other() =>
        Assert.Equal(["cat", "head", "other"], KumbuMetrics.CommandLabels(["cat", "head", "head", "grep", "rm -rf"]));

    [Fact]
    public async Task Known_commands_are_the_shells()
    {
        // A Shell needs a request's caller, so the app's own is asked: its help lists every command it has.
        await using var client = await app.ConnectAsync();
        var help = await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "help" }, cancellationToken: Ct);

        var text = Assert.IsType<TextContentBlock>(help.Content[0]).Text;
        var names = text.Split('\n').SkipWhile(l => l != "Commands:").Skip(1).TakeWhile(l => l.StartsWith("  ", StringComparison.Ordinal))
            .Select(l => l.Trim().Split(' ')[0]).ToList();
        Assert.True(new HashSet<string>(names).SetEquals(KumbuMetrics.KnownCommands), string.Join(' ', names));
    }

    [Fact]
    public async Task A_tool_error_is_counted_by_kind()
    {
        const string conflicts = "kumbu_mcp_tool_errors_total{tool=\"write\",kind=\"conflict\"}";
        const string writeConflicts = "kumbu_write_conflicts_total{via=\"mcp\"}";
        var before = await Scrape.TextAsync(app);
        await using var client = await app.ConnectAsync();
        await client.CallToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Ideas/zz-tool-metrics.md", ["content"] = "one\n" }, cancellationToken: Ct);

        var stale = await client.CallToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Ideas/zz-tool-metrics.md", ["content"] = "two\n", ["version"] = "stale" }, cancellationToken: Ct);
        var missing = await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "cat /Nope/x.md" }, cancellationToken: Ct);

        Assert.True(stale.IsError);
        Assert.True(missing.IsError);
        var after = await Scrape.TextAsync(app);
        Assert.True(Scrape.Value(after, conflicts) >= Scrape.Value(before, conflicts) + 1);
        Assert.True(Scrape.Value(after, writeConflicts) >= Scrape.Value(before, writeConflicts) + 1);
        Assert.Contains("kumbu_mcp_tool_errors_total{tool=\"sh\",kind=", after);
        Assert.True(Scrape.Value(after, "kumbu_mcp_tool_calls_total{tool=\"sh\"}") >= 1);
        Assert.True(Scrape.Value(after, "kumbu_sh_commands_total{command=\"cat\"}") >= 1);
        Assert.Contains("kumbu_sh_command_duration_seconds_bucket{command=\"cat\",le=\"0.005\"}", after);
    }

    [Fact]
    public async Task A_rest_conflict_is_counted()
    {
        const string series = "kumbu_write_conflicts_total{via=\"api\"}";
        var before = Scrape.Value(await Scrape.TextAsync(app), series);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", KumbuAppFactory.Token);
        var first = await client.PutAsJsonAsync("/api/notes/Ideas/zz-rest-metrics.md", new { content = "one\n" }, Ct);
        first.EnsureSuccessStatusCode();

        var stale = await client.PutAsJsonAsync("/api/notes/Ideas/zz-rest-metrics.md", new { content = "two\n", version = "stale" }, Ct);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, stale.StatusCode);
        Assert.True(Scrape.Value(await Scrape.TextAsync(app), series) >= before + 1);
    }
}
