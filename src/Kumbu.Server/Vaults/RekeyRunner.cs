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
/// resets the backoff; <see cref="RunAsync"/> also moves a backing-off entry to the queue at once.</item>
/// </list>
/// An entry ends, and its key is dropped, when its run succeeds, when the vault is gone, and when the key held isn't
/// one the vault's owner slots open with (<see cref="VaultNotRekeyableException"/>): the vault then stays pending for
/// the next trigger with the right key. A restart loses the queue, not the state: the vault stays pending in MongoDB.
/// </summary>
public sealed class RekeyRunner : IHostedService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly VaultCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly ILogger<RekeyRunner> _log;
    private readonly CancellationToken _stopping;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public RekeyRunner(VaultCatalog catalog, IHostApplicationLifetime life, TimeProvider time, ILogger<RekeyRunner> log)
    {
        (_catalog, _time, _log, _stopping) = (catalog, time, log, life.ApplicationStopping);
        catalog.RekeyPendingFound += Start;
        _ = Task.Run(() => WorkAsync(_stopping));
    }

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

        /// <summary>Changes whenever a backoff begins or ends early, so that a timer of an earlier one does nothing.</summary>
        public int Generation { get; set; }

        public TaskCompletionSource<RekeySummary> Next { get; set; } = NewAttempt();
    }

    /// <summary>Queues the vault, fire and forget: a queued or backing-off entry gets the newer key and keeps its place
    /// and its backoff; a running one is left alone.</summary>
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

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Entry Upsert(string vaultId, string userId, byte[] userKey, bool endBackoff)
    {
        if (!_entries.TryGetValue(vaultId, out var entry))
        {
            entry = new Entry(vaultId, userId, userKey);
            _entries[vaultId] = entry;
            _queue.Writer.TryWrite(vaultId);
            return entry;
        }

        if (entry.State == State.Running) return entry;
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
        try
        {
            var summary = await _catalog.RekeyAsync(entry.VaultId, userId, userKey, stopping);
            End(entry);
            entry.Next.TrySetResult(summary);
        }
        catch (Exception e) when (e is VaultDeletedException or VaultNotRekeyableException)
        {
            _log.LogInformation("Re-keying vault {VaultId} ends: {Reason}", entry.VaultId, e.Message);
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
            TaskCompletionSource<RekeySummary> failed;
            TimeSpan wait;
            int generation;
            Task backoff;
            lock (_lock)
            {
                failed = entry.Next;
                entry.Failures++;
                wait = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, entry.Failures - 1), MaxBackoff.TotalMinutes));
                entry.State = State.Backoff;
                // The awaiters of this attempt get its failure; a later RunAsync waits for the next one.
                entry.Next = NewAttempt();
                generation = ++entry.Generation;
                // The wait counts from now, before anyone hears of the failure.
                backoff = Task.Delay(wait, _time, stopping);
            }

            _log.LogWarning("Re-keying vault {VaultId} failed ({Failures} in a row), trying again in {Minutes} min: {Message}", entry.VaultId, entry.Failures, wait.TotalMinutes, e.Message);
            failed.TrySetException(e);
            _ = RequeueAfterAsync(entry, generation, backoff);
        }
    }

    /// <summary>A vault that is gone is no longer pending for its owner.</summary>
    private async Task ForgetAsync(string vaultId, string userId, CancellationToken stopping)
    {
        try
        {
            await _catalog.ForgetPendingAsync(vaultId, userId, stopping);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning("Vault {VaultId} is gone but stays pending for user {UserId}: {Message}", vaultId, userId, e.Message);
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
