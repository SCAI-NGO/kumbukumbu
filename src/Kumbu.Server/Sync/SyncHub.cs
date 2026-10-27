// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Sync;

/// <summary>
/// The sync WebSockets (sync spec §5, "Hints"): every committed change to a vault (<see cref="VaultCatalog.NotesChanged"/>)
/// becomes <c>{"type":"changed","vault":"…"}</c> on each connection whose ticket's key has that vault, and on no
/// other. A hint is a nudge to ask for the version list, so a lost or doubled one does no harm; per connection and
/// vault at most one goes out per <see cref="Debounce"/>. Every <see cref="CheckInterval"/> each connection's key is
/// checked: revoked, expired or its owner deactivated, the socket is closed with 4401; a vault deleted is dropped.
/// A connection holds no vault open: a hint needs only the vault's id. A key holds at most <see cref="MaxPerKey"/>
/// connections (each costs a MongoDB lookup per check): a new one beyond that closes the key's oldest with
/// <see cref="Replaced"/>, so a socket the client gave up on, not yet noticed, never locks out its reconnect.
/// </summary>
public sealed class SyncHub(VaultCatalog catalog, ApiKeys keys, ILogger<SyncHub> log) : IHostedService
{
    public const WebSocketCloseStatus KeyInactive = (WebSocketCloseStatus)4401;

    /// <summary>Closed for a newer connection of the same key.</summary>
    public const WebSocketCloseStatus Replaced = (WebSocketCloseStatus)4409;

    public const int MaxPerKey = 4;

    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long one send may take. A client that stops reading fills the buffers and then blocks every send;
    /// past this its socket is aborted, so it can hold up neither its own close nor anyone else's.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a close waits for a send under way and then for the close frame, before the socket is aborted.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private long _opened;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _checks;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        catalog.NotesChanged += OnNotesChanged;
        _checks = CheckLoopAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        catalog.NotesChanged -= OnNotesChanged;
        await _stopping.CancelAsync();
        // All at once: each close is bounded, and none waits for another.
        await Task.WhenAll(_connections.Keys.Select(c => c.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "server shutting down")));
        if (_checks is not null) await _checks.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Serves one accepted socket until it closes: hints out, anything the client sends read and ignored.</summary>
    public async Task RunAsync(WebSocket socket, TicketGrant grant, CancellationToken ct)
    {
        var connection = new Connection(socket, grant, Interlocked.Increment(ref _opened));
        _connections[connection] = 0;
        try
        {
            var older = _connections.Keys.Where(c => c.Grant.KeyId == grant.KeyId).OrderByDescending(c => c.Number).Skip(MaxPerKey).ToList();
            foreach (var replaced in older)
            {
                // Removed now, so the count is right for the next connection; the close finishes on its own.
                if (_connections.TryRemove(replaced, out _)) _ = Observed(replaced.CloseAsync(Replaced, "replaced by a newer connection of this key"));
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Closed);
            var sending = Observed(connection.SendLoopAsync(linked.Token));
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, linked.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "");
                    break;
                }
            }

            connection.Complete();
            // A send that ignores its cancellation (a socket stuck for good) mustn't keep this connection listed.
            await sending.WaitAsync(SendTimeout);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or TimeoutException)
        {
            // The client went away, stopped reading, or the server is stopping.
        }
        finally
        {
            _connections.TryRemove(connection, out _);
            connection.Complete();
        }
    }

    /// <summary>For tests: how many connections are held.</summary>
    internal int Count => _connections.Count;

    /// <summary>One round of the 30-second check, for every connection; the loop runs it, tests call it directly.</summary>
    internal Task CheckAsync(CancellationToken ct) => Task.WhenAll(_connections.Keys.Select(c => CheckAsync(c, ct)));

    /// <summary>One connection; in parallel with the others, so a slow close holds up only its own.</summary>
    private async Task CheckAsync(Connection connection, CancellationToken ct)
    {
        try
        {
            if (!await keys.IsActiveAsync(connection.Grant.KeyId, ct))
            {
                log.LogInformation("Closed the sync connection of API key {KeyId}: revoked, expired or its owner deactivated", connection.Grant.KeyId);
                await connection.CloseAsync(KeyInactive, "key revoked or expired");
                return;
            }

            connection.DropVaults(catalog.IsDeleted);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Checking the sync connection of API key {KeyId} failed; it is checked again next time", connection.Grant.KeyId);
        }
    }

    private async Task CheckLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await CheckAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A task that may be left behind (a send given up on): its fault, if any, is observed rather than left to the finalizer.</summary>
    private static Task Observed(Task task)
    {
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>On the writer's thread, under its vault's gate: only marks and enqueues.</summary>
    private void OnNotesChanged(string vaultId)
    {
        foreach (var connection in _connections.Keys) connection.Hint(vaultId);
    }

    private sealed class Connection(WebSocket socket, TicketGrant grant, long number)
    {
        /// <summary>In the order connections opened.</summary>
        public long Number { get; } = number;

        private readonly Channel<string> _out = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        private readonly ConcurrentDictionary<string, byte> _waiting = new();
        private readonly SemaphoreSlim _sending = new(1, 1);
        private readonly CancellationTokenSource _closed = new();

        /// <summary>Cancels a send under way when the connection is closed.</summary>
        private readonly CancellationTokenSource _sends = new();
        private volatile IReadOnlyDictionary<string, string> _vaults = grant.Vaults.ToDictionary(v => v.VaultId, v => v.VaultName, StringComparer.Ordinal);

        public TicketGrant Grant { get; } = grant;

        /// <summary>Cancelled once the server sent its close: the reading stops then, whether or not the client answers.</summary>
        public CancellationToken Closed => _closed.Token;

        public void Hint(string vaultId)
        {
            if (!_vaults.TryGetValue(vaultId, out var name)) return;
            // Trailing: the first change of a burst waits out the debounce, the rest of the burst joins it.
            if (!_waiting.TryAdd(vaultId, 0)) return;
            _ = Task.Delay(Debounce).ContinueWith(
                delay =>
                {
                    _waiting.TryRemove(vaultId, out _);
                    if (_vaults.ContainsKey(vaultId)) _out.Writer.TryWrite(JsonSerializer.Serialize(new { type = "changed", vault = name }));
                },
                TaskScheduler.Default);
        }

        public void DropVaults(Func<string, bool> gone) =>
            _vaults = _vaults.Where(v => !gone(v.Key)).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

        public void Complete() => _out.Writer.TryComplete();

        public async Task SendLoopAsync(CancellationToken ct)
        {
            await foreach (var message in _out.Reader.ReadAllAsync(ct))
            {
                await _sending.WaitAsync(ct);
                try
                {
                    if (socket.State != WebSocketState.Open) return;
                    using var send = CancellationTokenSource.CreateLinkedTokenSource(ct, _sends.Token);
                    send.CancelAfter(SendTimeout);
                    try
                    {
                        await Observed(socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, send.Token)).WaitAsync(send.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Timed out, or the connection is being closed: a send that can't finish leaves the socket unusable.
                        socket.Abort();
                        return;
                    }
                }
                finally
                {
                    _sending.Release();
                }
            }
        }

        /// <summary>Bounded: a send under way is cancelled first, and a socket that won't take the close frame in time is aborted.</summary>
        public async Task CloseAsync(WebSocketCloseStatus status, string reason)
        {
            _vaults = new Dictionary<string, string>();
            Complete();
            await _sends.CancelAsync();
            if (!await _sending.WaitAsync(CloseTimeout))
            {
                socket.Abort();
                await _closed.CancelAsync();
                return;
            }

            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(CloseTimeout);
                    await socket.CloseOutputAsync(status, reason, timeout.Token).WaitAsync(timeout.Token);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                socket.Abort();
            }
            finally
            {
                _sending.Release();
                await _closed.CancelAsync();
            }
        }
    }
}
