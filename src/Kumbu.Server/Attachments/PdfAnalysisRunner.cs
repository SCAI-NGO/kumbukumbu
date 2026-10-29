// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Kumbu.Core.Attachments;
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Attachments;

/// <summary>
/// Runs the analyses of PDFs (<c>pdf_jobs</c>), which need the vault key and so run only while a vault is unlocked:
/// when a vault is unlocked (its PDFs without analysis or job are queued first, so a restart, an older upload or a
/// re-keying loses none), when a job is queued, and once a minute. One analysis at a time server-wide, on demand
/// (<see cref="RunNowAsync"/>) included, so the sidecar's two workers keep room for rendering. A sidecar that is away
/// or busy keeps the job for the next round, however long; any other failure is tried <see cref="MaxTries"/> times, and
/// then recorded in place of the analysis, as a password-protected or unreadable PDF is at once. Logs the vault id and
/// the outcome only, never content or names.
/// </summary>
public sealed class PdfAnalysisRunner : BackgroundService
{
    public const int MaxTries = 5;

    public const string PasswordProtected = "this PDF is password-protected; Kumbu² can't read it";

    /// <summary>The code a try counts with when the sidecar answered but the analysis couldn't be stored.</summary>
    private const string NotStored = "not_stored";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly VaultCatalog _catalog;
    private readonly PdfAnalyses _analyses;
    private readonly IPdfSidecar _sidecar;
    private readonly ILogger<PdfAnalysisRunner> _log;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly ConcurrentDictionary<(string Vault, string Blob), Task<Outcome>> _running = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _down;

    public PdfAnalysisRunner(VaultCatalog catalog, PdfAnalyses analyses, IPdfSidecar sidecar, ILogger<PdfAnalysisRunner> log)
    {
        (_catalog, _analyses, _sidecar, _log) = (catalog, analyses, sidecar, log);
        catalog.Unlocked += OnUnlocked;
    }

    /// <summary>How long <see cref="RunNowAsync"/> waits for its result (a test shortens it).</summary>
    internal TimeSpan OnDemandWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>What a run came to: an analysis or a failure recorded, or a sidecar's code to try again on, or nothing (skipped).</summary>
    private sealed record Outcome(PdfAnalysis? Analysis, PdfFailure? Failure, string? Code)
    {
        public static readonly Outcome Skipped = new(null, null, null);
    }

    /// <summary>Starts a round now rather than at the next minute.</summary>
    public void Wake() => _wake.Writer.TryWrite(0);

    /// <summary>Queues the PDFs among <paramref name="states"/> whose blobs have neither analysis nor job, after a commit
    /// named them. Never throws: a job that couldn't be queued is queued at the vault's next unlock.</summary>
    public async Task EnqueueAsync(UnlockedVault vault, IEnumerable<AttachmentState> states, CancellationToken ct)
    {
        var ids = BlobIds(states);
        if (ids.Count == 0) return;
        try
        {
            if (await _analyses.EnqueueAsync(vault, ids, evenIfAnalysed: false, ct) > 0) Wake();
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            _log.LogWarning("Queueing PDFs of vault {VaultId} for analysis failed; its next unlock queues them", vault.Id);
        }
    }

    private static List<byte[]> BlobIds(IEnumerable<AttachmentState> states) =>
        [.. states.Where(AttachmentFormat.IsPdf)
            .Select(s => EncryptedNoteStore.TryParseBlobId(s.BlobId, out var id) ? id : null).OfType<byte[]>()
            .DistinctBy(Convert.ToBase64String)];

    /// <summary>
    /// For <c>pdf</c> on a PDF whose analysis is missing or outdated: queues it and runs it, waiting at most
    /// <see cref="OnDemandWait"/> (a turn behind another analysis included). Null if it takes longer, when the job stays
    /// queued and the run goes on, or if it failed for now; <see cref="PixelsUnavailableException"/> if the PDF can't
    /// be analysed (password-protected, say).
    /// </summary>
    public async Task<PdfAnalysis?> RunNowAsync(UnlockedVault vault, AttachmentState state, CancellationToken ct)
    {
        if (BlobIds([state]) is not [var blobId] || !_catalog.MayIndex(vault)) return null;
        await _analyses.EnqueueAsync(vault, [blobId], evenIfAnalysed: true, ct);
        var run = RunAsync(vault, blobId);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var first = await Task.WhenAny(run, Task.Delay(OnDemandWait, wait.Token));
        await wait.CancelAsync();
        ct.ThrowIfCancellationRequested();
        if (first != run) return null;
        var outcome = await run;
        if (outcome.Failure is { } failure) throw new PixelsUnavailableException(failure.Message);
        return outcome.Analysis;
    }

    /// <summary>The jobs of every unlocked vault, one at a time; a sidecar that is away ends the round.</summary>
    internal async Task RoundAsync(CancellationToken ct)
    {
        foreach (var vault in _catalog.UnlockedVaults)
        {
            foreach (var job in await _analyses.JobsAsync(vault.Id, ct))
            {
                if (!_catalog.MayIndex(vault)) break;
                if ((await RunAsync(vault, job.BlobId).WaitAsync(ct)).Code == SidecarException.Unavailable) return;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stop = stoppingToken.Register(_stop.Cancel);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogError(e, "A round of PDF analyses failed");
            }

            using var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            tick.CancelAfter(Interval);
            try
            {
                await _wake.Reader.WaitToReadAsync(tick.Token);
                _wake.Reader.TryRead(out _);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The minute is up.
            }
        }
    }

    /// <summary>On the loading thread: the queries go to the background.</summary>
    private void OnUnlocked(UnlockedVault vault) => _ = Task.Run(async () =>
    {
        try
        {
            await EnqueueAsync(vault, vault.Attachments.Values.Select(a => a.Attachment.Current), _stop.Token);
            Wake();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception e)
        {
            _log.LogError("Queueing the PDFs of vault {VaultId} after its unlock failed: {Error}", vault.Id, e.GetType().Name);
        }
    });

    /// <summary>One run per blob at a time: a <c>pdf</c> call and the round asking for the same blob share it.</summary>
    private Task<Outcome> RunAsync(UnlockedVault vault, byte[] blobId)
    {
        var key = (vault.Id, Convert.ToBase64String(blobId));
        while (true)
        {
            if (_running.TryGetValue(key, out var running)) return running;
            var done = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_running.TryAdd(key, done.Task)) continue;
            _ = Task.Run(async () =>
            {
                try
                {
                    done.SetResult(await GatedAsync(vault, blobId));
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
                finally
                {
                    _running.TryRemove(key, out _);
                }
            });
            return done.Task;
        }
    }

    private async Task<Outcome> GatedAsync(UnlockedVault vault, byte[] blobId)
    {
        var ct = _stop.Token;
        await _one.WaitAsync(ct);
        try
        {
            return await AnalyseAsync(vault, blobId, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The vault is being deleted or re-keyed.
            return Outcome.Skipped;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError("Analysing a PDF of vault {VaultId} failed: {Error}", vault.Id, e.GetType().Name);
            return Outcome.Skipped;
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<Outcome> AnalyseAsync(UnlockedVault vault, byte[] blobId, CancellationToken ct)
    {
        if (!_catalog.MayIndex(vault)) return Outcome.Skipped;
        if (await _analyses.ReadAsync(vault, blobId, ct) is { Current: true } stored)
        {
            await _analyses.DeleteJobAsync(vault.Id, blobId, ct);
            return new Outcome(stored.Analysis, stored.Failure, null);
        }

        byte[]? bytes;
        try
        {
            bytes = await _catalog.ReadBlobAsync(vault, blobId, ct);
        }
        catch (VaultUnavailableException)
        {
            // Changed or damaged outside Kumbu²: no try will open it.
            return await FailedAsync(vault, blobId, new SidecarException("damaged", "The content doesn't open"), ct);
        }

        if (bytes is null)
        {
            // Released meanwhile: the blob sweep takes what is left of it.
            await _analyses.DeleteJobAsync(vault.Id, blobId, ct);
            return Outcome.Skipped;
        }

        var started = Stopwatch.GetTimestamp();
        SidecarAnalysis result;
        using (var call = CancellationTokenSource.CreateLinkedTokenSource(ct, _catalog.InterruptToken(vault.Id)))
        {
            try
            {
                result = await _sidecar.AnalyseAsync(bytes, PdfAnalyses.MaxPages, call.Token);
            }
            catch (SidecarException e)
            {
                return await FailedAsync(vault, blobId, e, ct);
            }
        }

        if (_down)
        {
            _down = false;
            _log.LogInformation("The PDF sidecar answers again");
        }

        PdfAnalysis? analysis;
        try
        {
            analysis = await _analyses.StoreAsync(vault, blobId, result, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The sidecar did its part: a store that fails (too large for the database, say) would fail the same way
            // next round, after minutes of analysis, so it counts a try.
            _log.LogError("Storing the analysis of a PDF of vault {VaultId} failed: {Error}", vault.Id, e.GetType().Name);
            return await FailedAsync(vault, blobId, new SidecarException(NotStored, "The analysis couldn't be stored"), ct);
        }

        if (analysis is null) return Outcome.Skipped;
        if (analysis.Analyser != PdfAnalyses.Analyser)
        {
            _log.LogWarning("The PDF sidecar's analyser is not this server's: it may be an older or newer release");
        }

        _log.LogInformation("Analysed a PDF of vault {VaultId} in {Seconds:0.0} s", vault.Id, Stopwatch.GetElapsedTime(started).TotalSeconds);
        return new Outcome(analysis, null, null);
    }

    /// <summary>A PDF the sidecar can't read at all is recorded at once; anything else counts a try.</summary>
    private async Task<Outcome> FailedAsync(UnlockedVault vault, byte[] blobId, SidecarException e, CancellationToken ct)
    {
        if (e.Code is not (SidecarException.Encrypted or "not_pdf" or "too_large" or "damaged"))
        {
            var tries = await _analyses.CountTryAsync(vault.Id, blobId, ct);
            if (e.Transient || tries < MaxTries)
            {
                if (e.Code == SidecarException.Unavailable && !_down)
                {
                    _down = true;
                    _log.LogWarning("The PDF sidecar is unavailable; analyses wait for it");
                }
                else if (e.Code != SidecarException.Unavailable)
                {
                    _log.LogInformation("Analysing a PDF of vault {VaultId} failed ({Code}); it is tried again", vault.Id, e.Code);
                }

                return new Outcome(null, null, e.Code);
            }
        }

        var failure = Failure(e.Code);
        if (!await _analyses.StoreFailureAsync(vault, blobId, failure, ct)) return Outcome.Skipped;
        _log.LogWarning("A PDF of vault {VaultId} can't be analysed ({Code}); it isn't tried again", vault.Id, e.Code);
        return new Outcome(null, failure, null);
    }

    private static PdfFailure Failure(string code) => new(code, code switch
    {
        SidecarException.Encrypted => PasswordProtected,
        "not_pdf" => "this PDF couldn't be analysed: it isn't a PDF the analyser can read",
        "too_large" => "this PDF couldn't be analysed: it is larger than the analyser takes",
        "damaged" => "this PDF couldn't be analysed: its stored content doesn't open",
        NotStored => "this PDF couldn't be analysed: its analysis couldn't be stored",
        SidecarException.Timeout => "this PDF couldn't be analysed: it took longer than the time limit",
        _ => "this PDF couldn't be analysed: the analyser failed on it",
    });

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _catalog.Unlocked -= OnUnlocked;
        base.Dispose();
    }
}
