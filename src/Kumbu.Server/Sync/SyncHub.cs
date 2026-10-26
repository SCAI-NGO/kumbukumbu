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
/// A connection holds no vault open: a hint needs only the vault's id.
/// </summary>
public sealed class SyncHub(VaultCatalog catalog, ApiKeys keys, ILogger<SyncHub> log) : IHostedService
{
    public const WebSocketCloseStatus KeyInactive = (WebSocketCloseStatus)4401;

    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
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
        foreach (var connection in _connections.Keys) await connection.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "server shutting down");
        if (_checks is not null) await _checks.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Serves one accepted socket until it closes: hints out, anything the client sends read and ignored.</summary>
    public async Task RunAsync(WebSocket socket, TicketGrant grant, CancellationToken ct)
    {
        var connection = new Connection(socket, grant);
        _connections[connection] = 0;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token, connection.Closed);
            var sending = connection.SendLoopAsync(linked.Token);
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
            await sending;
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // The client went away, or the server is stopping.
        }
        finally
        {
            _connections.TryRemove(connection, out _);
            connection.Complete();
        }
    }

    /// <summary>One round of the 30-second check, for every connection; the loop runs it, tests call it directly.</summary>
    internal async Task CheckAsync(CancellationToken ct)
    {
        foreach (var connection in _connections.Keys)
        {
            try
            {
                if (!await keys.IsActiveAsync(connection.Grant.KeyId, ct))
                {
                    log.LogInformation("Closed the sync connection of API key {KeyId}: revoked, expired or its owner deactivated", connection.Grant.KeyId);
                    await connection.CloseAsync(KeyInactive, "key revoked or expired");
                    continue;
                }

                connection.DropVaults(catalog.IsDeleted);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Checking the sync connection of API key {KeyId} failed; it is checked again next time", connection.Grant.KeyId);
            }
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

    /// <summary>On the writer's thread, under its vault's gate: only marks and enqueues.</summary>
    private void OnNotesChanged(string vaultId)
    {
        foreach (var connection in _connections.Keys) connection.Hint(vaultId);
    }

    private sealed class Connection(WebSocket socket, TicketGrant grant)
    {
        private readonly Channel<string> _out = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        private readonly ConcurrentDictionary<string, byte> _waiting = new();
        private readonly SemaphoreSlim _sending = new(1, 1);
        private readonly CancellationTokenSource _closed = new();
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
                    await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, ct);
                }
                finally
                {
                    _sending.Release();
                }
            }
        }

        public async Task CloseAsync(WebSocketCloseStatus status, string reason)
        {
            _vaults = new Dictionary<string, string>();
            Complete();
            await _sending.WaitAsync();
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.CloseOutputAsync(status, reason, timeout.Token);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
            finally
            {
                _sending.Release();
                await _closed.CancelAsync();
            }
        }
    }
}
