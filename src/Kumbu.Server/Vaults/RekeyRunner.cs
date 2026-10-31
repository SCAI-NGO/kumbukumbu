// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Threading.Channels;

namespace Kumbu.Server.Vaults;

/// <summary>
/// Runs stage 2 of re-keying (<see cref="VaultCatalog.RekeyAsync"/>) in the background, never in a request's own flow
/// (rekeying design §4).
/// <list type="bullet">
/// <item>One worker takes vaults one at a time from one process-wide first-in-first-out queue: each run's batches sit
/// near the cache budget already, and runs side by side would multiply it.</item>
/// <item>Each vault has at most one entry, queued, running or backing off; it holds the user id, the user key, the
/// failures in a row and the awaiters of its next attempt.</item>
/// <item>After the n-th failure in a row an entry keeps its user key and waits min(2ⁿ⁻¹ minutes, 1 hour) before going
/// back to the queue's tail. <see cref="Start"/> on a queued or backing-off entry only swaps in the newer key and never
/// resets the backoff; <see cref="RunAsync"/> also moves a backing-off entry to the queue at once. On a running entry
/// both swap in the key for the next attempt, and a changed key (or <see cref="RunAsync"/>) makes a failure of the
/// attempt under way requeue at once instead of backing off.</item>
/// </list>
/// An entry ends, and its key is dropped, when its run succeeds, when the vault is gone, and when the key held isn't
/// one the vault's owner slots open with (<see cref="VaultNotRekeyableException"/>): the vault then stays pending for
/// the next trigger with the right key. A shared vault (<see cref="VaultSharedException"/>) ends its entry too, and
/// stays pending. An attempt that takes longer than <see cref="AttemptTimeout"/> counts as a
/// failure, so that one hung run doesn't hold up the queue. A restart loses the queue, not the state: the vault stays
/// pending in MongoDB.
/// </summary>
public sealed class RekeyRunner(VaultCatalog catalog, IHostApplicationLifetime life, TimeProvider time, ILogger<RekeyRunner> log) : IHostedService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(life.ApplicationStopping);
    private Task? _worker;

    /// <summary>How long one attempt may take before it counts as failed (tests set it lower).</summary>
    internal TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromMinutes(30);

    private enum State
    {
        Queued,
        Running,
        Backoff,
    }

    private sealed class Entry(string vaultId, string userId, byte[] userKey)
    {
        public string VaultId { get; } = vaultId;

        public string UserId { get; set; } = userId;

        public byte[] UserKey { get; set; } = userKey;

        public State State { get; set; } = State.Queued;

        public int Failures { get; set; }

        /// <summary>A <see cref="RunAsync"/> came while it ran: should the attempt fail, the next one follows at once.</summary>
        public bool RunRequested { get; set; }

        /// <summary>Changes whenever a backoff begins or ends early, so that a timer of an earlier one does nothing.</summary>
        public int Generation { get; set; }

        public TaskCompletionSource<RekeySummary> Next { get; set; } = NewAttempt();
    }

    /// <summary>Starts the worker and listens for pending vaults that requests meet.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        catalog.RekeyPendingFound += Start;
        _worker = Task.Run(() => WorkAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops listening and stops the worker; a run under way is cancelled and its vault stays pending.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        catalog.RekeyPendingFound -= Start;
        await _stop.CancelAsync();
        if (_worker is not null) await _worker.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>Queues the vault, fire and forget: a queued or backing-off entry gets the newer key and keeps its place
    /// and its backoff; a running one gets it for its next attempt, which follows at once should this one fail.</summary>
    public void Start(string vaultId, string userId, byte[] userKey)
    {
        lock (_lock) Upsert(vaultId, userId, userKey, endBackoff: false);
    }

    /// <summary>As <see cref="Start"/>, but a backing-off entry goes to the queue at once; then waits for the entry's next
    /// attempt (the one under way, if it runs) and returns its summary, or rethrows its failure. Cancelling the wait
    /// leaves the attempt going.</summary>
    public Task<RekeySummary> RunAsync(string vaultId, string userId, byte[] userKey)
    {
        lock (_lock) return Upsert(vaultId, userId, userKey, endBackoff: true).Next.Task;
    }

    public bool IsRunning(string vaultId)
    {
        lock (_lock) return _entries.TryGetValue(vaultId, out var entry) && entry.State == State.Running;
    }

    private Entry Upsert(string vaultId, string userId, byte[] userKey, bool endBackoff)
    {
        if (!_entries.TryGetValue(vaultId, out var entry))
        {
            entry = new Entry(vaultId, userId, userKey);
            _entries[vaultId] = entry;
            _queue.Writer.TryWrite(vaultId);
            return entry;
        }

        if (entry.State == State.Running)
        {
            // The next attempt takes the newer key. A rotation that committed mid-run makes the one under way fail with
            // the replaced key, so that attempt must not back off holding it.
            var newer = !string.Equals(entry.UserId, userId, StringComparison.Ordinal) || !entry.UserKey.AsSpan().SequenceEqual(userKey);
            (entry.UserId, entry.UserKey) = (userId, userKey);
            if (endBackoff || newer) entry.RunRequested = true;
            return entry;
        }

        (entry.UserId, entry.UserKey) = (userId, userKey);
        if (entry.State == State.Backoff && endBackoff)
        {
            entry.Generation++;
            entry.State = State.Queued;
            _queue.Writer.TryWrite(vaultId);
        }

        return entry;
    }

    private async Task WorkAsync(CancellationToken stopping)
    {
        try
        {
            await foreach (var vaultId in _queue.Reader.ReadAllAsync(stopping))
            {
                Entry? entry;
                lock (_lock)
                {
                    // A stale queue item: the entry ended, or went back to backoff, since.
                    if (!_entries.TryGetValue(vaultId, out entry) || entry.State != State.Queued) continue;
                    entry.State = State.Running;
                    entry.RunRequested = false;
                }

                await AttemptAsync(entry, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private async Task AttemptAsync(Entry entry, CancellationToken stopping)
    {
        string userId;
        byte[] userKey;
        lock (_lock) (userId, userKey) = (entry.UserId, entry.UserKey);
        using var timeout = new CancellationTokenSource(AttemptTimeout, time);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stopping, timeout.Token);
        try
        {
            // WaitAsync too: a run stuck somewhere that doesn't watch the token still lets the queue go on. It keeps the
            // vault (a later attempt meets it busy, and backs off) until it notices.
            var summary = await catalog.RekeyAsync(entry.VaultId, userId, userKey, attempt.Token).WaitAsync(attempt.Token);
            End(entry);
            entry.Next.TrySetResult(summary);
        }
        catch (VaultNotRekeyableException e) when (KeyReplaced(entry, userId, userKey))
        {
            // Refused for the key this attempt took, while a newer one came: not the end, the next attempt has it.
            Fail(entry, e, stopping);
        }
        catch (VaultSharedException e)
        {
            // Nothing changed, and nothing will until the vault is no longer shared: the vault stays in the owner's
            // rekeyPending, and the next trigger tries again.
            log.LogInformation("Re-keying vault {VaultId} is left pending: {Reason}", entry.VaultId, e.Message);
            End(entry);
            entry.Next.TrySetException(e);
        }
        catch (Exception e) when (e is VaultDeletedException or VaultNotRekeyableException)
        {
            log.LogInformation("Re-keying vault {VaultId} ends: {Reason}", entry.VaultId, e.Message);
            End(entry);
            if (e is VaultDeletedException) await ForgetAsync(entry.VaultId, userId, stopping);
            entry.Next.TrySetException(e);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stopping: the vault stays pending in MongoDB, for the next start of the server.
            End(entry);
            entry.Next.TrySetCanceled(stopping);
        }
        catch (Exception e)
        {
            var failure = e is OperationCanceledException && timeout.IsCancellationRequested
                ? new TimeoutException($"Re-keying vault {entry.VaultId} took longer than {AttemptTimeout.TotalMinutes} min.", e)
                : e;
            Fail(entry, failure, stopping);
        }
    }

    private bool KeyReplaced(Entry entry, string userId, byte[] userKey)
    {
        lock (_lock) return !string.Equals(entry.UserId, userId, StringComparison.Ordinal) || !entry.UserKey.AsSpan().SequenceEqual(userKey);
    }

    /// <summary>The awaiters of the attempt get its failure first; only then does the backoff begin, unless a
    /// <see cref="RunAsync"/> or a newer key came meanwhile, which ends it at once.</summary>
    private void Fail(Entry entry, Exception e, CancellationToken stopping)
    {
        TaskCompletionSource<RekeySummary> failed;
        lock (_lock)
        {
            failed = entry.Next;
            entry.Next = NewAttempt();
            entry.Failures++;
        }

        log.LogWarning("Re-keying vault {VaultId} failed ({Failures} in a row): {Message}", entry.VaultId, entry.Failures, e.Message);
        failed.TrySetException(e);

        lock (_lock)
        {
            if (entry.RunRequested)
            {
                entry.RunRequested = false;
                entry.State = State.Queued;
                _queue.Writer.TryWrite(entry.VaultId);
                return;
            }

            var wait = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, entry.Failures - 1), MaxBackoff.TotalMinutes));
            var generation = ++entry.Generation;
            // The timer exists before the entry shows as backing off (IsRunning false).
            var backoff = Task.Delay(wait, time, stopping);
            entry.State = State.Backoff;
            _ = RequeueAfterAsync(entry, generation, backoff);
        }
    }

    /// <summary>A vault that is gone is no longer pending for its owner.</summary>
    private async Task ForgetAsync(string vaultId, string userId, CancellationToken stopping)
    {
        try
        {
            await catalog.ForgetPendingAsync(vaultId, userId, stopping);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("Vault {VaultId} is gone but stays pending for user {UserId}: {Message}", vaultId, userId, e.Message);
        }
    }

    private async Task RequeueAfterAsync(Entry entry, int generation, Task backoff)
    {
        try
        {
            await backoff;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_lock)
        {
            if (entry.Generation != generation || entry.State != State.Backoff || !ReferenceEquals(_entries.GetValueOrDefault(entry.VaultId), entry)) return;
            entry.State = State.Queued;
            _queue.Writer.TryWrite(entry.VaultId);
        }
    }

    private void End(Entry entry)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_entries.GetValueOrDefault(entry.VaultId), entry)) _entries.Remove(entry.VaultId);
        }
    }

    private static TaskCompletionSource<RekeySummary> NewAttempt()
    {
        var attempt = new TaskCompletionSource<RekeySummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A failure nobody waits for is not an unobserved exception.
        _ = attempt.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        return attempt;
    }
}
