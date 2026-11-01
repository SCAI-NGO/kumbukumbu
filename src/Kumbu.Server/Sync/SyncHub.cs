// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Sync;

/// <summary>
/// The sync WebSockets (sync spec §5, "Hints"; collab design §9.1): every committed change to a vault
/// (<see cref="VaultCatalog.NotesChanged"/>) becomes <c>{"type":"changed","vault":"…"}</c>, the vault under the
/// subject's own name for it, on each connection whose subject has that vault, and on no other. A hint is a nudge to
/// ask for the version list, so a lost or doubled one does no harm; per connection and vault at most one goes out per
/// <see cref="Debounce"/>. A subject is an API key or a web session (<see cref="Subject"/>). Every
/// <see cref="CheckInterval"/> each connection's subject is checked: a key revoked, expired or its owner deactivated,
/// or a session ended or its user deactivated, closes with 4401; a vault deleted is dropped. A connection holds no vault open: a hint needs only the vault's id. A subject holds
/// at most <see cref="MaxPerKey"/> connections (each costs MongoDB lookups per check): a new one beyond that closes the
/// subject's oldest with <see cref="Replaced"/>, so a socket the client gave up on, not yet noticed, never locks out
/// its reconnect.
/// </summary>
public sealed class SyncHub(VaultCatalog catalog, ApiKeys keys, Sessions sessions, ILogger<SyncHub> log) : IHostedService
{
    public const WebSocketCloseStatus KeyInactive = (WebSocketCloseStatus)4401;

    /// <summary>Closed for a newer connection of the same subject.</summary>
    public const WebSocketCloseStatus Replaced = (WebSocketCloseStatus)4409;

    /// <summary>Connections per subject: a person with several tabs has four in one session.</summary>
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

    /// <summary>Whether the subject may hold a socket now: the key active (its user too), or the session alive and its user active.</summary>
    public async Task<bool> IsAliveAsync(Subject subject, CancellationToken ct) => subject.Kind switch
    {
        SubjectKind.Key => await keys.IsActiveAsync(subject.Id, ct),
        _ => await sessions.AliveAsync(subject.Id, ct) == subject.User,
    };

    /// <summary>Serves one accepted socket until it closes: hints out, anything the client sends read and ignored.</summary>
    public async Task RunAsync(WebSocket socket, TicketGrant grant, CancellationToken ct)
    {
        var connection = new Connection(socket, grant, Interlocked.Increment(ref _opened));
        _connections[connection] = 0;
        try
        {
            var older = _connections.Keys.Where(c => c.Grant.Subject == grant.Subject).OrderByDescending(c => c.Number).Skip(MaxPerKey).ToList();
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

    /// <summary>
    /// Told by the request that changed membership <paramref name="membershipId"/> of <paramref name="vaultId"/>
    /// (collab design §6.1, §6.3), before it answers: unless the membership is still active, the vault goes from every
    /// socket of <paramref name="memberId"/>'s subjects at once, not at the next check, and a socket left with no vault is
    /// closed with <see cref="KeyInactive"/>. The sweep tells it too, after a cleanup it finished, in case the request
    /// that ended the membership never got here. Nothing is dropped while she holds another active membership of the
    /// vault (invited again since).
    /// </summary>
    public async Task MembershipChangedAsync(string vaultId, string memberId, string membershipId, CancellationToken ct)
    {
        if ((await catalog.ActiveMembershipsAsync([membershipId], ct)).ContainsKey(membershipId)) return;
        if (await catalog.HasActiveMembershipAsync(vaultId, memberId, ct)) return;
        var closing = new List<Task>();
        foreach (var connection in _connections.Keys.Where(c => c.Grant.Subject.User == memberId && c.Has(vaultId)).ToList())
        {
            connection.DropVaults(v => v == vaultId);
            if (connection.IsEmpty) closing.Add(connection.CloseAsync(KeyInactive, "no vault left"));
        }

        await Task.WhenAll(closing);
        if (closing.Count > 0) log.LogInformation("Closed {Count} sync connections of user {UserId}: membership {MembershipId} ended", closing.Count, memberId, membershipId);
    }

    /// <summary>For tests: how many connections are held.</summary>    /// <summary>For tests: how many connections are held.</summary>
    internal int Count => _connections.Count;

    /// <summary>For tests: every connection's vaults, as each holds them now.</summary>
    internal IReadOnlyList<(Subject Subject, string VaultId, string Name, AccessLevel Level)> Held =>
        [.. _connections.Keys.SelectMany(c => c.Vaults.Select(v => (c.Grant.Subject, v.Key, v.Value.Name, v.Value.Level)))];

    /// <summary>One round of the 30-second check, for every connection; the loop runs it, tests call it directly.</summary>
    internal Task CheckAsync(CancellationToken ct) => Task.WhenAll(_connections.Keys.Select(c => CheckAsync(c, ct)));

    /// <summary>One connection; in parallel with the others, so a slow close holds up only its own.</summary>
    private async Task CheckAsync(Connection connection, CancellationToken ct)
    {
        var subject = connection.Grant.Subject;
        try
        {
            if (!await IsAliveAsync(subject, ct))
            {
                log.LogInformation("Closed the sync connection of {Subject}: revoked, ended, expired or its user deactivated", Describe(subject));
                await connection.CloseAsync(KeyInactive, subject.Kind == SubjectKind.Key ? "key revoked or expired" : "session ended");
                return;
            }

            connection.DropVaults(catalog.IsDeleted);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Checking the sync connection of {Subject} failed; it is checked again next time", Describe(subject));
        }
    }

    private static string Describe(Subject subject) => subject.Kind == SubjectKind.Key ? $"API key {subject.Id}" : $"a session of user {subject.User}";

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

    /// <summary>A vault as a connection holds it: the subject's name for it, the membership it came through (null for
    /// the owner), and its level.</summary>
    private sealed record VaultState(string Name, string? Membership, AccessLevel Level);

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

        private volatile IReadOnlyDictionary<string, VaultState> _vaults = grant.Vaults.ToDictionary(
            v => v.VaultId,
            v => new VaultState(v.VaultName, v.Membership, v.Level),
            StringComparer.Ordinal);

        public TicketGrant Grant { get; } = grant;

        public IReadOnlyDictionary<string, VaultState> Vaults => _vaults;

        /// <summary>Cancelled once the server sent its close: the reading stops then, whether or not the client answers.</summary>
        public CancellationToken Closed => _closed.Token;

        public void Hint(string vaultId)
        {
            if (!_vaults.ContainsKey(vaultId)) return;
            // Trailing: the first change of a burst waits out the debounce, the rest of the burst joins it.
            if (!_waiting.TryAdd(vaultId, 0)) return;
            _ = Task.Delay(Debounce).ContinueWith(
                delay =>
                {
                    _waiting.TryRemove(vaultId, out _);
                    Enqueue(vaultId);
                },
                TaskScheduler.Default);
        }

        /// <summary>Under the name the vault has when the hint goes out: a rename since the change counts.</summary>
        private void Enqueue(string vaultId)
        {
            if (!_vaults.TryGetValue(vaultId, out var vault)) return;
            _out.Writer.TryWrite(JsonSerializer.Serialize(new { type = "changed", vault = vault.Name }));
        }

        public bool Has(string vaultId) => _vaults.ContainsKey(vaultId);

        public void DropVaults(Func<string, bool> gone) =>
            _vaults = _vaults.Where(v => !gone(v.Key)).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

        public bool IsEmpty => _vaults.Count == 0;

        public void Complete() => _out.Writer.TryComplete();

        public async Task SendLoopAsync(CancellationToken ct)
        {
            await foreach (var message in _out.Reader.ReadAllAsync(ct))
            {
                if (!await SendAsync(message, ct)) return;
            }
        }

        /// <summary>False once the socket can't be sent to any more.</summary>
        private async Task<bool> SendAsync(string message, CancellationToken ct)
        {
            await _sending.WaitAsync(ct);
            try
            {
                if (socket.State != WebSocketState.Open) return false;
                using var send = CancellationTokenSource.CreateLinkedTokenSource(ct, _sends.Token);
                send.CancelAfter(SendTimeout);
                try
                {
                    await Observed(socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, send.Token)).WaitAsync(send.Token);
                    return true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timed out, or the connection is being closed: a send that can't finish leaves the socket unusable.
                    socket.Abort();
                    return false;
                }
            }
            finally
            {
                _sending.Release();
            }
        }

        /// <summary>Bounded: a send under way is cancelled first, and a socket that won't take the close frame in time is aborted.</summary>
        public async Task CloseAsync(WebSocketCloseStatus status, string reason)
        {
            _vaults = new Dictionary<string, VaultState>();
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
