// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Sharing;
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Sync;

/// <summary>
/// The sync WebSockets (sync spec §5, "Hints"; collab design §9): every committed change to a vault
/// (<see cref="VaultCatalog.NotesChanged"/>) becomes <c>{"type":"changed","vault":"…"}</c>, the vault under the
/// subject's own name for it, on each connection whose subject has that vault, and on no other. A hint is a nudge to
/// ask for the version list, so a lost or doubled one does no harm; per connection and vault at most one goes out per
/// <see cref="Debounce"/>. A subject is an API key or a web session (<see cref="Subject"/>). Every
/// <see cref="CheckInterval"/> each connection is checked: a key revoked, expired or its owner deactivated, or a
/// session ended, idle too long (it is never touched here, so an open tab keeps no session alive) or its user
/// deactivated, closes with 4401; and the subject's vaults are worked out again from its slots and the memberships
/// active now, so a vault it lost is dropped and a role changed is its level. The requests that change access tell the
/// hub at once (<see cref="MembershipChangedAsync"/>, <see cref="UserChangedAsync"/>, <see cref="VaultsChangedAsync"/>);
/// the check is only the backstop. A connection holds no vault open: a hint needs only the vault's id. A subject holds
/// at most <see cref="MaxPerKey"/> connections (each costs MongoDB lookups per check): a new one beyond that closes the
/// subject's oldest with <see cref="Replaced"/>, so a socket the client gave up on, not yet noticed, never locks out
/// its reconnect.
/// <para>
/// Presence (collab design §9.2): a session with at least <c>write</c> on a vault announces
/// <c>{"type":"editing","vault":…,"path":…,"on":true|false}</c> (at most <see cref="Presence.MaxFrame"/> bytes;
/// anything else is ignored); the sessions with the vault get <c>{"type":"presence","vault":…,"path":…,"editors":[…]}</c>,
/// rendered for each receiving connection (<see cref="Presence.Render"/>), on a channel of its own that keeps the last
/// state per note, so no burst of hints ever pushes out an "off".
/// </para>
/// </summary>
public sealed class SyncHub(VaultCatalog catalog, ApiKeys keys, Sessions sessions, TimeProvider time, ILogger<SyncHub> log) : IHostedService
{
    public const WebSocketCloseStatus KeyInactive = (WebSocketCloseStatus)4401;

    /// <summary>Closed for a newer connection of the same subject.</summary>
    public const WebSocketCloseStatus Replaced = (WebSocketCloseStatus)4409;

    /// <summary>Connections per subject: a person with several tabs has four in one session.</summary>
    public const int MaxPerKey = 4;

    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>How often presence entries past <see cref="Presence.Expiry"/> are removed.</summary>
    public static readonly TimeSpan PresenceSweep = TimeSpan.FromSeconds(5);

    /// <summary>How long one send may take. A client that stops reading fills the buffers and then blocks every send;
    /// past this its socket is aborted, so it can hold up neither its own close nor anyone else's.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a close waits for a send under way and then for the close frame, before the socket is aborted.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions FrameOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private readonly Presence _presence = new(time);
    private long _opened;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _checks;
    private Task? _expiry;

    /// <summary>For tests: awaited first by every notification of an access change, so a test can hold one up.</summary>
    internal Func<Task>? BeforeAccessChange { get; set; }

    /// <summary>For tests: the debounce of hints (zero: each change is its own hint, at once).</summary>
    internal TimeSpan HintDebounce { get; set; } = Debounce;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        catalog.NotesChanged += OnNotesChanged;
        _checks = CheckLoopAsync(_stopping.Token);
        _expiry = ExpiryLoopAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        catalog.NotesChanged -= OnNotesChanged;
        await _stopping.CancelAsync();
        // All at once: each close is bounded, and none waits for another.
        await Task.WhenAll(_connections.Keys.Select(c => c.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "server shutting down")));
        foreach (var loop in new[] { _checks, _expiry }.OfType<Task>()) await loop.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Whether the subject may hold a socket now: the key active (its user too), or the session alive and its user active.</summary>
    public async Task<bool> IsAliveAsync(Subject subject, CancellationToken ct) => subject.Kind switch
    {
        SubjectKind.Key => await keys.IsActiveAsync(subject.Id, ct),
        _ => await sessions.AliveAsync(subject.Id, ct) == subject.User,
    };

    /// <summary>Serves one accepted socket until it closes: hints and presence out, presence frames in, anything else the client sends read and ignored.</summary>
    public async Task RunAsync(WebSocket socket, TicketGrant grant, CancellationToken ct)
    {
        var connection = new Connection(socket, grant, Interlocked.Increment(ref _opened), HintDebounce);
        _connections[connection] = 0;
        try
        {
            var older = _connections.Keys.Where(c => c.Grant.Subject == grant.Subject).OrderByDescending(c => c.Number).Skip(MaxPerKey).ToList();
            foreach (var replaced in older)
            {
                // Removed now, so the count is right for the next connection; the close finishes on its own.
                if (_connections.TryRemove(replaced, out _))
                {
                    ClearPresence(replaced, null);
                    _ = Observed(replaced.CloseAsync(Replaced, "replaced by a newer connection of this key"));
                }
            }

            // Who is editing already, as this connection is shown it.
            foreach (var vaultId in connection.Vaults.Keys)
            {
                foreach (var path in _presence.PathsOf(vaultId)) Tell(connection, vaultId, path, _presence.EditorsOf(vaultId, path));
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Closed);
            var sending = Observed(connection.SendLoopAsync(linked.Token));
            var buffer = new byte[Presence.MaxFrame];
            var frame = new byte[Presence.MaxFrame];
            var length = 0;
            var tooLong = false;
            while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, linked.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "");
                    break;
                }

                // A frame longer than the limit is ignored whole, however it is split.
                if (!tooLong && length + result.Count <= frame.Length)
                {
                    Array.Copy(buffer, 0, frame, length, result.Count);
                    length += result.Count;
                }
                else tooLong = true;

                if (!result.EndOfMessage) continue;
                if (!tooLong && result.MessageType == WebSocketMessageType.Text) OnFrame(connection, frame.AsSpan(0, length));
                length = 0;
                tooLong = false;
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
            ClearPresence(connection, null);
        }
    }

    /// <summary>
    /// Told by the request that changed membership <paramref name="membershipId"/> of <paramref name="vaultId"/>
    /// (revoked, left, its role changed; collab design §6.1, §6.3, §9.1), before it answers, and by the sweep after a
    /// cleanup it finished, in case that request never got here. Every connection of <paramref name="memberId"/>'s
    /// subjects (her sessions, her keys) with the vault is checked at once: the vault goes where the membership it came
    /// through is no longer active, and its level follows the role where it is; her presence on the vault goes with
    /// it. A socket left with no vault is closed with <see cref="KeyInactive"/>. Each connection holds the vault through
    /// the membership its ticket had, so a late call for an old membership never drops it from a socket of the member's
    /// newer one (invited again since).
    /// </summary>
    public async Task MembershipChangedAsync(string vaultId, string memberId, string membershipId, CancellationToken ct)
    {
        if (BeforeAccessChange is { } before) await before();
        var checkedCount = await RefreshAsync(c => c.Grant.Subject.User == memberId && c.Has(vaultId), ct);
        if (checkedCount > 0) log.LogInformation("Checked {Count} sync connections of user {UserId}: membership {MembershipId} changed", checkedCount, memberId, membershipId);
    }

    /// <summary>
    /// Told after a user was deactivated, reactivated or deleted (collab design §7.2–§7.4, §9.1), before the request
    /// answers: her own connections are checked (and close, her keys revoked and sessions ended), and so is every
    /// connection with a vault she shares, whose members are read-only while she is inactive.
    /// </summary>
    public async Task UserChangedAsync(string userId, CancellationToken ct)
    {
        if (BeforeAccessChange is { } before) await before();
        var shared = await catalog.SharedVaultIdsAsync(userId, ct);
        await RefreshAsync(c => c.Grant.Subject.User == userId || shared.Any(c.Has), ct);
    }

    /// <summary>Told when vaults closed or went for everyone (an owner's deletion, the end of a closing period): every
    /// connection with one of them is checked at once.</summary>
    public async Task VaultsChangedAsync(IReadOnlyCollection<string> vaultIds, CancellationToken ct)
    {
        if (vaultIds.Count == 0) return;
        if (BeforeAccessChange is { } before) await before();
        await RefreshAsync(c => vaultIds.Any(c.Has), ct);
    }

    /// <summary>A member renamed her vault (collab design §2.3): her connections that hold it through
    /// <paramref name="membershipId"/> name it so in their hints and presence from now on.</summary>
    public void AliasRenamed(string memberId, string membershipId, string alias)
    {
        foreach (var connection in _connections.Keys.Where(c => c.Grant.Subject.User == memberId)) connection.Rename(membershipId, alias);
    }

    /// <summary>For tests: how many connections are held.</summary>
    internal int Count => _connections.Count;

    /// <summary>For tests: every connection's vaults, as each holds them now.</summary>
    internal IReadOnlyList<(Subject Subject, string VaultId, string Name, AccessLevel Level)> Held =>
        [.. _connections.Keys.SelectMany(c => c.Vaults.Select(v => (c.Grant.Subject, v.Key, v.Value.Name, v.Value.Level)))];

    /// <summary>One round of the 30-second check, for every connection; the loop runs it, tests call it directly.</summary>
    internal Task CheckAsync(CancellationToken ct) => Task.WhenAll(_connections.Keys.Select(c => CheckAsync(c, ct)));

    /// <summary>The connections <paramref name="which"/> picks, checked now; how many.</summary>
    private async Task<int> RefreshAsync(Func<Connection, bool> which, CancellationToken ct)
    {
        var picked = _connections.Keys.Where(which).ToList();
        await Task.WhenAll(picked.Select(c => CheckAsync(c, ct)));
        return picked.Count;
    }

    /// <summary>One connection; in parallel with the others, so a slow close holds up only its own.</summary>
    private async Task CheckAsync(Connection connection, CancellationToken ct)
    {
        var subject = connection.Grant.Subject;
        try
        {
            if (!await IsAliveAsync(subject, ct))
            {
                log.LogInformation("Closed the sync connection of {Subject}: revoked, ended, expired or its user deactivated", Describe(subject));
                ClearPresence(connection, null);
                await connection.CloseAsync(KeyInactive, subject.Kind == SubjectKind.Key ? "key revoked or expired" : "session ended");
                return;
            }

            var before = connection.Vaults;
            var after = await VaultsNowAsync(subject, before, ct);
            connection.SetVaults(after);
            ClearPresence(connection, v => !connection.CanEdit(v));
            if (before.Count > 0 && after.Count == 0) await connection.CloseAsync(KeyInactive, "no vault left");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Checking the sync connection of {Subject} failed; it is checked again next time", Describe(subject));
        }
    }

    /// <summary>
    /// Of the vaults a connection holds, those its subject still has, at the level it has now (collab design §9.1):
    /// read afresh from its slots (a key's own; a session's user slots and member slots) and the memberships active
    /// now. A vault deleted goes; one reached through a membership goes once that membership isn't active, of this
    /// user, on this vault, and otherwise takes the role's level, <c>read</c> while the owner is inactive or the vault
    /// closing, and a key's never above its ticket's. Vaults gained since the ticket come with the next ticket.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, VaultState>> VaultsNowAsync(Subject subject, IReadOnlyDictionary<string, VaultState> held, CancellationToken ct)
    {
        var live = held.Where(v => !catalog.IsDeleted(v.Key)).ToList();
        if (live.Count == 0) return new Dictionary<string, VaultState>(StringComparer.Ordinal);
        var via = live.Select(v => v.Value.Membership).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var f = Builders<SlotDocument>.Filter;
        var mine = subject.Kind == SubjectKind.Key
            ? f.Eq(s => s.Kind, SlotDocument.ApiKey) & f.Eq(s => s.KeyId, subject.Id)
            : (f.Eq(s => s.Kind, SlotDocument.User) & f.Eq(s => s.Owner, subject.User)) | (f.Eq(s => s.Kind, SlotDocument.Member) & f.In(s => s.Membership, via));
        var slots = await catalog.Slots.Find(f.In(s => s.Vault, live.Select(v => v.Key)) & mine)
            .Project(s => new { s.Vault, s.Kind, s.GrantedVia, s.Membership }).ToListAsync(ct);
        var active = via.Count == 0 ? null : await catalog.ActiveMembershipsAsync(via, ct);

        var result = new Dictionary<string, VaultState>(StringComparer.Ordinal);
        foreach (var (vaultId, state) in live)
        {
            var slotHeld = slots.Any(s => s.Vault == vaultId && (subject.Kind == SubjectKind.Key
                ? s.GrantedVia == state.Membership
                : state.Membership is null ? s.Kind == SlotDocument.User : s.Kind == SlotDocument.Member && s.Membership == state.Membership));
            if (!slotHeld) continue;
            if (state.Membership is null)
            {
                result[vaultId] = state;
                continue;
            }

            if (active is null || !active.TryGetValue(state.Membership, out var membership) || membership.Vault != vaultId || membership.Member != subject.User) continue;
            var level = membership.OwnerInactive ? AccessLevel.Read : VaultCatalog.LevelOf(membership);
            result[vaultId] = state with { Level = level < state.Ceiling ? level : state.Ceiling };
        }

        return result;
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

    private async Task ExpiryLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PresenceSweep, time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) ExpirePresence();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Removes presence entries past <see cref="Presence.Expiry"/>, and tells the receivers; the loop runs it, tests call it directly.</summary>
    internal void ExpirePresence()
    {
        foreach (var (vaultId, path) in _presence.Expire()) Broadcast(vaultId, path);
    }

    /// <summary>
    /// An inbound frame: only <c>editing</c>, only from a session, only for a vault it holds at <c>write</c> or above,
    /// and only for a note path; anything else is ignored, as the hints protocol always ignored what clients sent.
    /// </summary>
    private void OnFrame(Connection connection, ReadOnlySpan<byte> bytes)
    {
        if (connection.Grant.Subject.Kind != SubjectKind.Session) return;
        EditingFrame? frame;
        try
        {
            frame = JsonSerializer.Deserialize<EditingFrame>(bytes, FrameOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (frame is not { Type: "editing", Vault: { } name, Path: { } path, On: { } on }) return;
        if (connection.IdOf(name) is not { } vaultId || !connection.CanEdit(vaultId)) return;
        string note;
        try
        {
            note = string.Join('/', ApiPaths.Parse(name, path.TrimStart('/'), note: true).Segments.Skip(1));
        }
        catch (Exception e) when (e is ApiException or ArgumentException)
        {
            return;
        }

        if (connection.Editor(vaultId) is { } editor && _presence.Set(vaultId, note, editor, on)) Broadcast(vaultId, note);
    }

    private sealed record EditingFrame(string? Type, string? Vault, string? Path, bool? On);

    /// <summary>The connection's presence entries on the vaults <paramref name="gone"/> picks (all without it) go, and the receivers are told.</summary>
    private void ClearPresence(Connection connection, Func<string, bool>? gone)
    {
        foreach (var (vaultId, path) in _presence.Remove(connection.Number, gone)) Broadcast(vaultId, path);
    }

    /// <summary>The editors of a note, to every session connection that has its vault, each rendered for that connection.</summary>
    private void Broadcast(string vaultId, string path)
    {
        var editors = _presence.EditorsOf(vaultId, path);
        foreach (var connection in _connections.Keys) Tell(connection, vaultId, path, editors);
    }

    private static void Tell(Connection connection, string vaultId, string path, IReadOnlyList<PresenceEditor> editors)
    {
        if (connection.Grant.Subject.Kind != SubjectKind.Session || !connection.Vaults.TryGetValue(vaultId, out var vault)) return;
        var shown = Presence.Render(new PresenceViewer(connection.Number, connection.Grant.Subject.User, vault.Membership is null), editors);
        connection.Presence(vaultId, path, JsonSerializer.Serialize(new { type = "presence", vault = vault.Name, path, editors = shown }));
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
    /// the owner), its level now, the most it may have (a key's ticket level; a member's session follows her role), and
    /// the owner's label for a member.</summary>
    private sealed record VaultState(string Name, string? Membership, AccessLevel Level, AccessLevel Ceiling, string Label);

    private sealed class Connection(WebSocket socket, TicketGrant grant, long number, TimeSpan debounce)
    {
        /// <summary>In the order connections opened.</summary>
        public long Number { get; } = number;

        private readonly Channel<string> _out = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

        /// <summary>Presence, apart from the hints: the last state per note, which a newer one replaces before it is sent.</summary>
        private readonly ConcurrentDictionary<(string Vault, string Path), string> _presence = new();

        /// <summary>Wakes the send loop for either; one pending wake is enough, since the loop takes all there is.</summary>
        private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        private readonly ConcurrentDictionary<string, byte> _waiting = new();
        private readonly SemaphoreSlim _sending = new(1, 1);
        private readonly CancellationTokenSource _closed = new();

        /// <summary>Cancels a send under way when the connection is closed.</summary>
        private readonly CancellationTokenSource _sends = new();

        private volatile IReadOnlyDictionary<string, VaultState> _vaults = grant.Vaults.ToDictionary(
            v => v.VaultId,
            v => new VaultState(v.VaultName, v.Membership, v.Level, grant.Subject.Kind == SubjectKind.Session && v.Membership is not null ? AccessLevel.Maintain : v.Level, v.Label),
            StringComparer.Ordinal);

        public TicketGrant Grant { get; } = grant;

        public IReadOnlyDictionary<string, VaultState> Vaults => _vaults;

        /// <summary>Cancelled once the server sent its close: the reading stops then, whether or not the client answers.</summary>
        public CancellationToken Closed => _closed.Token;

        public void Hint(string vaultId)
        {
            if (!_vaults.ContainsKey(vaultId)) return;
            if (debounce <= TimeSpan.Zero)
            {
                Enqueue(vaultId);
                return;
            }

            // Trailing: the first change of a burst waits out the debounce, the rest of the burst joins it.
            if (!_waiting.TryAdd(vaultId, 0)) return;
            _ = Task.Delay(debounce).ContinueWith(
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
            _wake.Writer.TryWrite(0);
        }

        public void Presence(string vaultId, string path, string message)
        {
            _presence[(vaultId, path)] = message;
            _wake.Writer.TryWrite(0);
        }

        public bool Has(string vaultId) => _vaults.ContainsKey(vaultId);

        /// <summary>The id of the vault the subject names <paramref name="name"/>, or null.</summary>
        public string? IdOf(string name) => _vaults.FirstOrDefault(v => string.Equals(v.Value.Name, name, StringComparison.Ordinal)).Key;

        /// <summary>A session at <c>write</c> or above may announce editing (collab design §9.2).</summary>
        public bool CanEdit(string vaultId) => Grant.Subject.Kind == SubjectKind.Session && _vaults.TryGetValue(vaultId, out var vault) && vault.Level >= AccessLevel.Write;

        public PresenceEditor? Editor(string vaultId) =>
            _vaults.TryGetValue(vaultId, out var vault) ? new PresenceEditor(Number, Grant.Subject.User, vault.Membership is null, vault.Label, Grant.Display) : null;

        public void SetVaults(IReadOnlyDictionary<string, VaultState> vaults) => _vaults = vaults;

        public void Rename(string membershipId, string alias) =>
            _vaults = _vaults.ToDictionary(v => v.Key, v => v.Value.Membership == membershipId ? v.Value with { Name = alias } : v.Value, StringComparer.Ordinal);

        public bool IsEmpty => _vaults.Count == 0;

        public void Complete()
        {
            _out.Writer.TryComplete();
            _wake.Writer.TryComplete();
        }

        /// <summary>Presence first, the last state of each note, then the hints in their order.</summary>
        public async Task SendLoopAsync(CancellationToken ct)
        {
            while (await _wake.Reader.WaitToReadAsync(ct))
            {
                // Taken before what it woke for: whatever comes after this is woken for again.
                _wake.Reader.TryRead(out _);
                foreach (var key in _presence.Keys)
                {
                    if (_presence.TryRemove(key, out var message) && !await SendAsync(message, ct)) return;
                }

                while (_out.Reader.TryRead(out var hint))
                {
                    if (!await SendAsync(hint, ct)) return;
                }
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
