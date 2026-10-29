// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Search;
using Kumbu.Server.Vaults;
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
        CreateSeries();
        return true;
    });

    /// <summary>Every series a label can have exists from the start, at 0: a panel shows a 0, not "No data", until the first event.</summary>
    private static void CreateSeries()
    {
        foreach (var tool in Enum.GetValues<Tool>())
        {
            ToolCalls.WithLabels(Label(tool));
            ToolDuration.WithLabels(Label(tool));
            foreach (var kind in Enum.GetValues<ToolError>()) ToolErrors.WithLabels(Label(tool), Label(kind));
        }

        foreach (var command in KnownCommands)
        {
            ShCommands.WithLabels(command);
            ShCommandDuration.WithLabels(command);
        }

        ShCommands.WithLabels("other");
        foreach (var via in Enum.GetValues<WriteVia>()) WriteConflicts.WithLabels(Label(via));
        foreach (var kind in Enum.GetValues<ProviderKind>())
        {
            EmbeddingBatches.WithLabels(Label(kind));
            EmbeddingBatchDuration.WithLabels(Label(kind));
            EmbeddingErrors.WithLabels(Label(kind));
        }

        foreach (var reason in Enum.GetValues<LockReason>()) VaultLocks.WithLabels(Label(reason));
        foreach (var outcome in Enum.GetValues<Outcome>()) SignIns.WithLabels(Label(outcome));
        foreach (var grant in Enum.GetValues<Grant>()) OAuthTokens.WithLabels(Label(grant));
    }

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
        "kumbu_write_conflicts_total", "Writes refused as a conflict (a stale version, a name already taken), by way in.", new CounterConfiguration { LabelNames = ["via"] });

    public enum ProviderKind { Internal, External }

    public enum LockReason { Idle, Memory }

    public enum Outcome { Success, Failure }

    public enum Grant { AuthorizationCode, RefreshToken }

    private static readonly Counter EmbeddingBatches = Prometheus.Metrics.CreateCounter(
        "kumbu_embedding_batches_total", "Batches sent to an embedding provider, by provider kind.", new CounterConfiguration { LabelNames = ["provider_kind"] });

    private static readonly Histogram EmbeddingBatchDuration = Prometheus.Metrics.CreateHistogram(
        "kumbu_embedding_batch_duration_seconds", "How long an embedding batch took, by provider kind.", new HistogramConfiguration { LabelNames = ["provider_kind"], Buckets = Seconds });

    private static readonly Counter EmbeddingErrors = Prometheus.Metrics.CreateCounter(
        "kumbu_embedding_errors_total", "Embedding batches that failed, by provider kind.", new CounterConfiguration { LabelNames = ["provider_kind"] });

    private static readonly Gauge EmbeddingBacklog = Prometheus.Metrics.CreateGauge(
        "kumbu_embedding_backlog", "Notes waiting to be indexed or being indexed.");

    private static readonly Gauge VaultsUnlocked = Prometheus.Metrics.CreateGauge(
        "kumbu_vaults_unlocked", "Vaults unlocked in memory now.");

    private static readonly Counter VaultUnlocks = Prometheus.Metrics.CreateCounter(
        "kumbu_vault_unlocks_total", "Vaults unlocked.");

    private static readonly Counter VaultLocks = Prometheus.Metrics.CreateCounter(
        "kumbu_vault_locks_total", "Vaults locked by the sweeper, by reason.", new CounterConfiguration { LabelNames = ["reason"] });

    private static readonly Counter SignIns = Prometheus.Metrics.CreateCounter(
        "kumbu_sign_ins_total", "Password sign-ins, by outcome.", new CounterConfiguration { LabelNames = ["outcome"] });

    private static readonly Counter OAuthTokens = Prometheus.Metrics.CreateCounter(
        "kumbu_oauth_tokens_issued_total", "Token pairs issued by the OAuth token endpoint, by grant.", new CounterConfiguration { LabelNames = ["grant"] });

    // A test process builds many hosts: each is held weakly, so that a disposed host can be collected, and the gauges
    // add up the live ones.
    private static readonly List<(WeakReference<SearchIndex> Index, WeakReference<VaultCatalog> Catalog)> Hosts = [];
    private static bool _scrapeHooked;

    /// <summary>The gauges of this process's search index and vault catalog are read at each scrape. The callback is registered once;
    /// each call (another host in one process) adds the host to what it sums.</summary>
    public static void ReadOnScrape(SearchIndex index, VaultCatalog catalog)
    {
        lock (Hosts)
        {
            Hosts.Add((new WeakReference<SearchIndex>(index), new WeakReference<VaultCatalog>(catalog)));
            if (_scrapeHooked) return;
            _scrapeHooked = true;
        }

        Prometheus.Metrics.DefaultRegistry.AddBeforeCollectCallback(() =>
        {
            double backlog = 0, unlocked = 0;
            lock (Hosts)
            {
                Hosts.RemoveAll(h => !h.Index.TryGetTarget(out _) && !h.Catalog.TryGetTarget(out _));
                foreach (var (i, c) in Hosts)
                {
                    if (i.TryGetTarget(out var idx)) backlog += idx.Queued;
                    if (c.TryGetTarget(out var cat)) unlocked += cat.UnlockedCount;
                }
            }

            EmbeddingBacklog.Set(backlog);
            VaultsUnlocked.Set(unlocked);
        });
    }

    public static void EmbeddingBatchDone(ProviderKind kind, TimeSpan elapsed)
    {
        var label = Label(kind);
        EmbeddingBatches.WithLabels(label).Inc();
        EmbeddingBatchDuration.WithLabels(label).Observe(elapsed.TotalSeconds);
    }

    public static void EmbeddingFailed(ProviderKind kind) => EmbeddingErrors.WithLabels(Label(kind)).Inc();

    public static void VaultUnlocked() => VaultUnlocks.Inc();

    public static void VaultLocked(LockReason reason) => VaultLocks.WithLabels(Label(reason)).Inc();

    public static void SignedIn(Outcome outcome) => SignIns.WithLabels(Label(outcome)).Inc();

    public static void OAuthTokenIssued(Grant grant) => OAuthTokens.WithLabels(Label(grant)).Inc();

    private static string Label(ProviderKind kind) => kind == ProviderKind.External ? "external" : "internal";

    private static string Label(LockReason reason) => reason == LockReason.Idle ? "idle" : "memory";

    private static string Label(Outcome outcome) => outcome == Outcome.Success ? "success" : "failure";

    private static string Label(Grant grant) => grant == Grant.RefreshToken ? "refresh_token" : "authorization_code";

    private static string Label(WriteVia via) => via == WriteVia.Mcp ? "mcp" : "api";

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

    public static void WriteConflict(WriteVia via) => WriteConflicts.WithLabels(Label(via)).Inc();

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

    /// <summary>
    /// Every value each label of the kumbu_ metrics can take, from the enums and the shell's command names. Not
    /// <c>endpoint</c>: its values are the app's route templates and <c>none</c>, which only the running app knows.
    /// The label guard test checks a scrape against it.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedLabels { get; } = new Dictionary<string, IReadOnlySet<string>>
    {
        ["tool"] = Values<Tool>(Label),
        ["kind"] = Values<ToolError>(Label),
        ["via"] = Values<WriteVia>(Label),
        ["provider_kind"] = Values<ProviderKind>(Label),
        ["reason"] = Values<LockReason>(Label),
        ["outcome"] = Values<Outcome>(Label),
        ["grant"] = Values<Grant>(Label),
        ["command"] = new HashSet<string>([.. KnownCommands, "other"], StringComparer.Ordinal),
    };

    private static HashSet<string> Values<T>(Func<T, string> label) where T : struct, Enum =>
        new(Enum.GetValues<T>().Select(label), StringComparer.Ordinal);

    /// <summary>Sets up the default collectors; call before anything uses the default registry. Idempotent.</summary>
    public static void Configure() => _ = Configured.Value;
}
