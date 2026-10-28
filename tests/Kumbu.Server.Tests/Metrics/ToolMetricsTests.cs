// SPDX-License-Identifier: AGPL-3.0-or-later
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
        // Other tests list the sample vault: leave it as it was.
        await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "rm /Ideas/zz-tool-metrics.md" }, cancellationToken: Ct);

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
    public async Task A_tool_body_that_throws_is_counted_and_rethrown()
    {
        const string calls = "kumbu_mcp_tool_calls_total{tool=\"board\"}";
        const string durations = "kumbu_mcp_tool_duration_seconds_count{tool=\"board\"}";
        const string errors = "kumbu_mcp_tool_errors_total{tool=\"board\",kind=\"other\"}";
        var before = await Scrape.TextAsync(app);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Kumbu.Server.Mcp.ToolResult.RunAsync(KumbuMetrics.Tool.Board, () => throw new InvalidOperationException("unexpected")));

        var after = await Scrape.TextAsync(app);
        Assert.True(Scrape.Value(after, calls) >= Scrape.Value(before, calls) + 1);
        Assert.True(Scrape.Value(after, durations) >= Scrape.Value(before, durations) + 1);
        Assert.True(Scrape.Value(after, errors) >= Scrape.Value(before, errors) + 1);
    }

    [Fact]
    public async Task A_rest_board_conflict_is_counted()
    {
        const string series = "kumbu_write_conflicts_total{via=\"api\"}";
        var user = await Api.RestClient.NewAsync(app.PublicApi, "Work");
        await user.CreateAsync("Board/Board.md", "---\nkumbu-board: 1\ncolumns: [Ideas, Doing]\n---\n# Roadmap\n");
        await user.CreateAsync("Board/Task.md", "---\ncolumn: Ideas\nrank: a1\n---\nText.\n");
        var before = Scrape.Value(await Scrape.TextAsync(app), series);

        using var taken = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/board/cards", new { title = "Task", column = "Ideas" });

        Assert.Equal(System.Net.HttpStatusCode.Conflict, taken.StatusCode);
        Assert.True(Scrape.Value(await Scrape.TextAsync(app), series) >= before + 1);
    }

    [Fact]
    public async Task A_rest_conflict_is_counted()
    {
        const string series = "kumbu_write_conflicts_total{via=\"api\"}";
        // A vault of its own: the sample vaults are listed exactly by other tests.
        var user = await Api.RestClient.NewAsync(app.PublicApi, "Work");
        await user.CreateAsync("zz-rest-metrics.md", "one\n");
        var before = Scrape.Value(await Scrape.TextAsync(app), series);

        using var stale = await user.SendAsync(HttpMethod.Put, user.Url("zz-rest-metrics.md"), new { content = "two\n", version = "stale" });

        Assert.Equal(System.Net.HttpStatusCode.Conflict, stale.StatusCode);
        Assert.True(Scrape.Value(await Scrape.TextAsync(app), series) >= before + 1);
    }
}
