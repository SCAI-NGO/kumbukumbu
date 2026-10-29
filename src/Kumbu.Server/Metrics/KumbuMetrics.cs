// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Server.Attachments;
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

        foreach (var type in Enum.GetValues<PageKind>())
        {
            if (type != PageKind.Unknown) PdfPagesAnalysed.WithLabels(Label(type));
            PdfPagesRendered.WithLabels(Label(type));
        }

        foreach (var reason in Enum.GetValues<FailReason>()) PdfAnalysisFailures.WithLabels(Label(reason));
        foreach (var mode in Enum.GetValues<PdfMode>()) PdfCalls.WithLabels(Label(mode));
        foreach (var delivery in Enum.GetValues<PageDelivery>()) PdfPagesDelivered.WithLabels(Label(delivery));
        foreach (var mode in TextPageModes) PdfTextPagesRendered.WithLabels(Label(mode));
        foreach (var step in Enum.GetValues<SizeStep>()) ImgCalls.WithLabels(Label(step));
        foreach (var reason in Enum.GetValues<ImgRefusal>()) ImgRefusals.WithLabels(Label(reason));
        foreach (var kind in Enum.GetValues<RenderKind>())
        {
            RenderDuration.WithLabels(Label(kind));
            DeliveredBytes.WithLabels(Label(kind));
            DeliveredLongEdge.WithLabels(Label(kind));
            foreach (var reason in Enum.GetValues<FailReason>()) RenderFailures.WithLabels(Label(kind), Label(reason));
        }
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

    public enum RenderKind { Page, Image }

    /// <summary>Why the sidecar failed: the same four for an analysis and a render.</summary>
    public enum FailReason { Timeout, Unavailable, Invalid, Failed }

    // A page the analysis calls text, rendered anyway: the 25 % threshold's miss signal.
    private static readonly PdfMode[] TextPageModes = [PdfMode.ImagesAll, PdfMode.Crop];

    private static readonly double[] Coverage = [0, 0.01, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5, 0.6, 0.7, 0.85, 0.95, 1];

    private static HistogramConfiguration Hist(double[] buckets, params string[] labels) => new() { LabelNames = labels, Buckets = buckets };

    private static readonly Counter PdfPagesAnalysed = Prometheus.Metrics.CreateCounter(
        "kumbu_pdf_pages_analysed_total",
        "PDF pages analysed, by type (text, mixed, image). Counted once per stored analysis, so a PDF analysed again after an upgrade counts again; a page read later is not counted.",
        new CounterConfiguration { LabelNames = ["type"] });

    private static readonly Histogram PdfTextCoverage = Prometheus.Metrics.CreateHistogram(
        "kumbu_pdf_page_text_coverage_ratio", "Share of the page covered by text, per analysed page (once per stored analysis).", Hist(Coverage));

    private static readonly Histogram PdfImageCoverage = Prometheus.Metrics.CreateHistogram(
        "kumbu_pdf_page_image_coverage_ratio", "Share of the page covered by images, per analysed page (once per stored analysis).", Hist(Coverage));

    private static readonly Histogram PdfVectorCoverage = Prometheus.Metrics.CreateHistogram(
        "kumbu_pdf_page_vector_coverage_ratio", "Share of the page covered by vector drawings, per analysed page (once per stored analysis).", Hist(Coverage));

    private static readonly Histogram PdfPageChars = Prometheus.Metrics.CreateHistogram(
        "kumbu_pdf_page_chars", "Characters of text per analysed page (once per stored analysis).", Hist([0, 50, 200, 500, 1000, 2000, 4000, 8000]));

    private static readonly Histogram PdfPagesPerDocument = Prometheus.Metrics.CreateHistogram(
        "kumbu_pdf_pages_per_document", "Pages analysed per PDF (once per stored analysis).", Hist([1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000]));

    private static readonly Histogram PdfAnalysisDuration = Prometheus.Metrics.CreateHistogram(
        "kumbu_pdf_analysis_duration_seconds", "How long the sidecar took to analyse a PDF that was then stored.", Hist([0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30]));

    private static readonly Counter PdfAnalysisFailures = Prometheus.Metrics.CreateCounter(
        "kumbu_pdf_analysis_failures_total", "PDF analyses that failed, by reason; a try that is repeated later counts each time.", new CounterConfiguration { LabelNames = ["reason"] });

    private static readonly Counter PdfCalls = Prometheus.Metrics.CreateCounter(
        "kumbu_pdf_calls_total", "pdf calls that reached a PDF, by mode (info, text, images, images_all, crop).", new CounterConfiguration { LabelNames = ["mode"] });

    private static readonly Counter PdfPagesDelivered = Prometheus.Metrics.CreateCounter(
        "kumbu_pdf_pages_delivered_total", "PDF pages delivered to a client, as text or as an image.", new CounterConfiguration { LabelNames = ["as"] });

    private static readonly Counter PdfPagesRendered = Prometheus.Metrics.CreateCounter(
        "kumbu_pdf_pages_rendered_total", "PDF pages delivered as an image, by the type the analysis gave them (unknown without an analysis).", new CounterConfiguration { LabelNames = ["type"] });

    private static readonly Counter PdfTextPagesRendered = Prometheus.Metrics.CreateCounter(
        "kumbu_pdf_text_pages_rendered_total",
        "Pages the analysis calls text that a client still asked to see as an image (--images=all or --crop). Each may mean the 25 % threshold missed a figure; an upper bound, since agents also look at text pages for other reasons.",
        new CounterConfiguration { LabelNames = ["via"] });

    private static readonly Counter ImgCalls = Prometheus.Metrics.CreateCounter(
        "kumbu_img_calls_total", "img calls that delivered an image, by size step.", new CounterConfiguration { LabelNames = ["step"] });

    private static readonly Counter ImgCrops = Prometheus.Metrics.CreateCounter(
        "kumbu_img_crops_total", "img calls that delivered a crop.");

    private static readonly Counter ImgRefusals = Prometheus.Metrics.CreateCounter(
        "kumbu_img_refusals_total", "img calls refused, by reason.", new CounterConfiguration { LabelNames = ["reason"] });

    private static readonly Histogram RenderDuration = Prometheus.Metrics.CreateHistogram(
        "kumbu_render_duration_seconds", "How long the sidecar took to render a page or an image, failures included.", Hist(Seconds, "kind"));

    private static readonly Counter RenderFailures = Prometheus.Metrics.CreateCounter(
        "kumbu_render_failures_total", "Renders that failed, by kind and reason; a page or crop that the request got wrong (no such page, outside the page) is not a failure.", new CounterConfiguration { LabelNames = ["kind", "reason"] });

    private static readonly Histogram DeliveredBytes = Prometheus.Metrics.CreateHistogram(
        "kumbu_image_delivered_bytes", "Bytes of each rendered image handed to the shell, by kind.", Hist([51200, 102400, 153600, 204800, 409600, 614400, 1048576, 2097152], "kind"));

    private static readonly Histogram DeliveredLongEdge = Prometheus.Metrics.CreateHistogram(
        "kumbu_image_delivered_long_edge_pixels", "Long edge in pixels of each rendered image handed to the shell, by kind.", Hist([256, 512, 768, 1024, 1568, 2048, 2576], "kind"));

    /// <summary>One analysed page of a PDF whose analysis was just stored.</summary>
    public static void PdfPageAnalysed(PdfPage page)
    {
        PdfPagesAnalysed.WithLabels(Label(KindOf(page.Type))).Inc();
        PdfTextCoverage.Observe(page.TextCoverage);
        PdfImageCoverage.Observe(page.ImageCoverage);
        PdfVectorCoverage.Observe(page.VectorCoverage);
        PdfPageChars.Observe(page.Chars);
    }

    public static void PdfAnalysed(int pages, TimeSpan elapsed)
    {
        PdfPagesPerDocument.Observe(pages);
        PdfAnalysisDuration.Observe(elapsed.TotalSeconds);
    }

    /// <summary>The sidecar's code as one of the four reasons: what the PDF is (encrypted, not a PDF, too large) is "invalid".</summary>
    public static FailReason FailReasonOf(SidecarException e) => e.Code switch
    {
        SidecarException.Timeout => FailReason.Timeout,
        SidecarException.Unavailable or SidecarException.Busy => FailReason.Unavailable,
        SidecarException.Encrypted or "not_pdf" or "too_large" or "damaged" => FailReason.Invalid,
        _ => FailReason.Failed,
    };

    public static void PdfAnalysisFailed(FailReason reason) => PdfAnalysisFailures.WithLabels(Label(reason)).Inc();

    public static void Rendered(RenderKind kind, TimeSpan elapsed) => RenderDuration.WithLabels(Label(kind)).Observe(elapsed.TotalSeconds);

    /// <summary>A page or crop the request got wrong: a refusal of the request, not a failure of the render.</summary>
    public static bool IsRequestMistake(SidecarException e) => e.Code is "no_such_page" or "bad_crop";

    public static void RenderFailed(RenderKind kind, FailReason reason) => RenderFailures.WithLabels(Label(kind), Label(reason)).Inc();

    public static void ImageDelivered(RenderKind kind, int bytes, int width, int height)
    {
        DeliveredBytes.WithLabels(Label(kind)).Observe(bytes);
        DeliveredLongEdge.WithLabels(Label(kind)).Observe(Math.Max(width, height));
    }

    /// <summary>What <c>img</c> and <c>pdf</c> report through <see cref="IAttachmentPixels.Count"/>; a record this does not know counts nothing.</summary>
    public static void AttachmentUsed(AttachmentUse use)
    {
        switch (use)
        {
            case PdfCalled c:
                PdfCalls.WithLabels(Label(c.Mode)).Inc();
                break;
            case PdfPageDelivered d:
                PdfPagesDelivered.WithLabels(Label(d.As)).Inc();
                break;
            case PdfPageRendered r:
                PdfPagesRendered.WithLabels(Label(r.Type)).Inc();
                if (r.Type == PageKind.Text && TextPageModes.Contains(r.Mode)) PdfTextPagesRendered.WithLabels(Label(r.Mode)).Inc();
                break;
            case ImgCalled i:
                ImgCalls.WithLabels(Label(i.Step)).Inc();
                if (i.Cropped) ImgCrops.Inc();
                break;
            case ImgRefused f:
                ImgRefusals.WithLabels(Label(f.Reason)).Inc();
                break;
        }
    }

    private static PageKind KindOf(PageType type) => type switch { PageType.Mixed => PageKind.Mixed, PageType.Image => PageKind.Image, _ => PageKind.Text };

    private static string Label(PageKind kind) => kind switch { PageKind.Text => "text", PageKind.Mixed => "mixed", PageKind.Image => "image", _ => "unknown" };

    private static string Label(PdfMode mode) => mode switch
    {
        PdfMode.Info => "info",
        PdfMode.Text => "text",
        PdfMode.Images => "images",
        PdfMode.ImagesAll => "images_all",
        _ => "crop",
    };

    private static string Label(PageDelivery delivery) => delivery == PageDelivery.Text ? "text" : "image";

    private static string Label(SizeStep step) => step switch { SizeStep.Small => "small", SizeStep.Medium => "medium", _ => "large" };

    private static string Label(ImgRefusal reason) => reason switch
    {
        ImgRefusal.Note => "note",
        ImgRefusal.Pdf => "pdf",
        ImgRefusal.NotFound => "not_found",
        ImgRefusal.TooLarge => "too_large",
        ImgRefusal.BadCrop => "bad_crop",
        ImgRefusal.BadSize => "bad_size",
        _ => "unavailable",
    };

    private static string Label(RenderKind kind) => kind == RenderKind.Page ? "page" : "image";

    private static string Label(FailReason reason) => reason switch
    {
        FailReason.Timeout => "timeout",
        FailReason.Unavailable => "unavailable",
        FailReason.Invalid => "invalid",
        _ => "failed",
    };

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
        "semsearch", "img", "pdf", "history", "diff", "restore", "mv", "rm", "trash", "mkcp", "lscp", "rmcp", "help",
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
    /// Every value each label of each kumbu_ metric can take, from the enums and the shell's command names, keyed by metric
    /// name (a histogram under its base name) and label name, in the order the metric declares its labels. A value legal for
    /// one metric is not for another. Not <c>endpoint</c> of <c>kumbu_http_rate_limited_total</c>: its values are the app's
    /// route templates and <c>none</c>, which only the running app knows. The label guard test checks a scrape against it,
    /// and the pre-creation test builds its series from it.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>> AllowedLabels { get; } = Allowed();

    private static Dictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>> Allowed()
    {
        var tool = Values<Tool>(Label);
        var provider = Values<ProviderKind>(Label);
        var command = new HashSet<string>(KnownCommands, StringComparer.Ordinal);
        var failures = Values<FailReason>(Label);
        var kinds = Values<RenderKind>(Label);
        var types = Values<PageKind>(Label);
        var analysed = new HashSet<string>(types.Where(t => t != "unknown"), StringComparer.Ordinal);
        var textVia = new HashSet<string>(TextPageModes.Select(Label), StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>>(StringComparer.Ordinal);
        void Metric(string name, params (string Label, HashSet<string> Values)[] labels) =>
            result[name] = labels.ToDictionary(l => l.Label, l => (IReadOnlySet<string>)l.Values, StringComparer.Ordinal);

        Metric("kumbu_http_rate_limited_total");
        Metric("kumbu_mcp_tool_calls_total", ("tool", tool));
        Metric("kumbu_mcp_tool_duration_seconds", ("tool", tool));
        Metric("kumbu_mcp_tool_errors_total", ("tool", tool), ("kind", Values<ToolError>(Label)));
        Metric("kumbu_sh_commands_total", ("command", new HashSet<string>([.. command, "other"], StringComparer.Ordinal)));
        Metric("kumbu_sh_command_duration_seconds", ("command", command));
        Metric("kumbu_write_conflicts_total", ("via", Values<WriteVia>(Label)));
        Metric("kumbu_embedding_batches_total", ("provider_kind", provider));
        Metric("kumbu_embedding_batch_duration_seconds", ("provider_kind", provider));
        Metric("kumbu_embedding_errors_total", ("provider_kind", provider));
        Metric("kumbu_embedding_backlog");
        Metric("kumbu_vaults_unlocked");
        Metric("kumbu_vault_unlocks_total");
        Metric("kumbu_vault_locks_total", ("reason", Values<LockReason>(Label)));
        Metric("kumbu_sign_ins_total", ("outcome", Values<Outcome>(Label)));
        Metric("kumbu_oauth_tokens_issued_total", ("grant", Values<Grant>(Label)));
        Metric("kumbu_pdf_pages_analysed_total", ("type", analysed));
        Metric("kumbu_pdf_page_text_coverage_ratio");
        Metric("kumbu_pdf_page_image_coverage_ratio");
        Metric("kumbu_pdf_page_vector_coverage_ratio");
        Metric("kumbu_pdf_page_chars");
        Metric("kumbu_pdf_pages_per_document");
        Metric("kumbu_pdf_analysis_duration_seconds");
        Metric("kumbu_pdf_analysis_failures_total", ("reason", failures));
        Metric("kumbu_pdf_calls_total", ("mode", Values<PdfMode>(Label)));
        Metric("kumbu_pdf_pages_delivered_total", ("as", Values<PageDelivery>(Label)));
        Metric("kumbu_pdf_pages_rendered_total", ("type", types));
        Metric("kumbu_pdf_text_pages_rendered_total", ("via", textVia));
        Metric("kumbu_img_calls_total", ("step", Values<SizeStep>(Label)));
        Metric("kumbu_img_crops_total");
        Metric("kumbu_img_refusals_total", ("reason", Values<ImgRefusal>(Label)));
        Metric("kumbu_render_duration_seconds", ("kind", kinds));
        Metric("kumbu_render_failures_total", ("kind", kinds), ("reason", failures));
        Metric("kumbu_image_delivered_bytes", ("kind", kinds));
        Metric("kumbu_image_delivered_long_edge_pixels", ("kind", kinds));
        return result;
    }

    private static HashSet<string> Values<T>(Func<T, string> label) where T : struct, Enum =>
        new(Enum.GetValues<T>().Select(label), StringComparer.Ordinal);

    /// <summary>Sets up the default collectors; call before anything uses the default registry. Idempotent.</summary>
    public static void Configure() => _ = Configured.Value;
}
