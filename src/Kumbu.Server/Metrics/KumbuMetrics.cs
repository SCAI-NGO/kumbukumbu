// SPDX-License-Identifier: AGPL-3.0-or-later
using Prometheus;

namespace Kumbu.Server.Metrics;

public static class KumbuMetrics
{
    private static readonly Lazy<bool> Configured = new(() =>
    {
        // Only the process and runtime basics: no event counters, no prometheus-net self-metrics, and of the meters
        // only System.Runtime (the adapter must stay on for that, so SuppressMeters is false).
        // The default registry is process-wide and these may be set once, though a test process builds many hosts.
        Prometheus.Metrics.SuppressDefaultMetrics(new SuppressDefaultMetricOptions
        {
            SuppressEventCounters = true,
            SuppressMeters = false,
            SuppressDebugMetrics = true,
        });
        Prometheus.Metrics.ConfigureMeterAdapter(o => o.InstrumentFilterPredicate = i => i.Meter.Name == "System.Runtime");
        Prometheus.Metrics.DefaultFactory.ExemplarBehavior = ExemplarBehavior.NoExemplars();
        return true;
    });

    public static readonly Counter RateLimited = Prometheus.Metrics.CreateCounter(
        "kumbu_http_rate_limited_total", "Requests answered 429, by route template.", new CounterConfiguration { LabelNames = ["endpoint"] });

    public enum Tool { Sh, Write, Edit, Replace, Board }

    public enum ToolError { Invalid, Refused, NotFound, Conflict, Unavailable, Damaged, Other }

    public enum WriteVia { Mcp, Api }

    private static readonly double[] Seconds = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30];

    private static readonly Counter ToolCalls = Prometheus.Metrics.CreateCounter(
        "kumbu_mcp_tool_calls_total", "MCP tool calls, by tool.", new CounterConfiguration { LabelNames = ["tool"] });

    private static readonly Histogram ToolDuration = Prometheus.Metrics.CreateHistogram(
        "kumbu_mcp_tool_duration_seconds", "How long an MCP tool call took, by tool.", new HistogramConfiguration { LabelNames = ["tool"], Buckets = Seconds });

    private static readonly Counter ToolErrors = Prometheus.Metrics.CreateCounter(
        "kumbu_mcp_tool_errors_total", "MCP tool calls answered with an error, by tool and kind.", new CounterConfiguration { LabelNames = ["tool", "kind"] });

    private static readonly Counter ShCommands = Prometheus.Metrics.CreateCounter(
        "kumbu_sh_commands_total", "Commands in sh command lines, by command name; anything the shell does not have is 'other'.", new CounterConfiguration { LabelNames = ["command"] });

    private static readonly Histogram ShCommandDuration = Prometheus.Metrics.CreateHistogram(
        "kumbu_sh_command_duration_seconds", "How long a sh command took, by command name.", new HistogramConfiguration { LabelNames = ["command"], Buckets = Seconds });

    private static readonly Counter WriteConflicts = Prometheus.Metrics.CreateCounter(
        "kumbu_write_conflicts_total", "Note writes refused because the note had changed, by way in.", new CounterConfiguration { LabelNames = ["via"] });

    /// <summary>The shell's command names; the only values the command label can take besides "other". A test compares it with the shell.</summary>
    public static readonly IReadOnlyList<string> KnownCommands =
    [
        "ls", "cat", "outline", "rg", "links", "backlinks", "tags", "board", "head", "tail", "wc", "sort",
        "semsearch", "history", "diff", "restore", "mv", "rm", "trash", "mkcp", "lscp", "rmcp", "help",
    ];

    /// <summary>The label values for the commands of a line: distinct, a known name as itself, anything else "other", first seen first.</summary>
    public static IReadOnlyList<string> CommandLabels(IReadOnlyList<string> typed) =>
        [.. typed.Select(c => KnownCommands.Contains(c, StringComparer.Ordinal) ? c : "other").Distinct(StringComparer.Ordinal)];

    public static void ShellRan(IReadOnlyList<string> typed)
    {
        foreach (var label in CommandLabels(typed)) ShCommands.WithLabels(label).Inc();
    }

    public static void ShellTimed(string command, TimeSpan elapsed)
    {
        if (KnownCommands.Contains(command, StringComparer.Ordinal)) ShCommandDuration.WithLabels(command).Observe(elapsed.TotalSeconds);
    }

    public static void ToolCalled(Tool tool, TimeSpan elapsed)
    {
        var label = Label(tool);
        ToolCalls.WithLabels(label).Inc();
        ToolDuration.WithLabels(label).Observe(elapsed.TotalSeconds);
    }

    public static void ToolFailed(Tool tool, ToolError kind) => ToolErrors.WithLabels(Label(tool), Label(kind)).Inc();

    public static void WriteConflict(WriteVia via) => WriteConflicts.WithLabels(via == WriteVia.Mcp ? "mcp" : "api").Inc();

    private static string Label(Tool tool) => tool switch
    {
        Tool.Sh => "sh",
        Tool.Write => "write",
        Tool.Edit => "edit",
        Tool.Replace => "replace",
        _ => "board",
    };

    private static string Label(ToolError kind) => kind switch
    {
        ToolError.Invalid => "invalid",
        ToolError.Refused => "refused",
        ToolError.NotFound => "not_found",
        ToolError.Conflict => "conflict",
        ToolError.Unavailable => "unavailable",
        ToolError.Damaged => "damaged",
        _ => "other",
    };

    /// <summary>Sets up the default collectors; call before anything uses the default registry. Idempotent.</summary>
    public static void Configure() => _ = Configured.Value;
}
