// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Sync;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Api;

/// <summary>WebSocket hints (sync spec §5): a ticket per connection, a "changed" message per vault of the key.</summary>
[Collection("app")]
public sealed class SyncSocketTests(KumbuAppFactory app) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<Program> Api => _api ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    public async ValueTask DisposeAsync()
    {
        if (_api is not null) await _api.DisposeAsync();
    }

    private static async Task<string> TicketAsync(RestClient user, string key)
    {
        using var response = await user.SendAsync(HttpMethod.Post, "/api/ws-ticket", bearer: key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.NotNull(body["expiresAt"]);
        return body["ticket"]!.GetValue<string>();
    }

    private async Task<WebSocket> ConnectAsync(string ticket)
    {
        var client = Api.Server.CreateWebSocketClient();
        return await client.ConnectAsync(new Uri($"wss://kumbu.test/api/ws?ticket={Uri.EscapeDataString(ticket)}"), Ct);
    }

    /// <summary>The next text message, or null if none arrives within <paramref name="wait"/>.</summary>
    private static async Task<string?> ReceiveAsync(WebSocket socket, TimeSpan wait)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(wait);
        var buffer = new byte[4096];
        try
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            return result.MessageType == WebSocketMessageType.Close ? $"close {(int?)result.CloseStatus}" : Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string Changed(string vault) => new JsonObject { ["type"] = "changed", ["vault"] = vault }.ToJsonString();

    [Fact]
    public async Task A_write_reaches_the_socket_as_a_hint()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        using var socket = await ConnectAsync(await TicketAsync(user, key));

        await user.CreateAsync("plan.md", "x\n");

        Assert.Equal(Changed("Arbeit"), await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Hints_never_name_another_vault()
    {
        var user = await NewAsync(Api);
        using var second = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Privat" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var other = await NewAsync(Api, "Fremd");
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        using var socket = await ConnectAsync(await TicketAsync(user, key));

        await other.CreateAsync("theirs.md", "x\n");
        using var privat = await user.SendAsync(HttpMethod.Put, "/api/notes/Privat/mine.md", new { content = "y\n" });
        Assert.Equal(HttpStatusCode.Created, privat.StatusCode);

        Assert.Null(await ReceiveAsync(socket, TimeSpan.FromSeconds(1.5)));
        await user.CreateAsync("plan.md", "x\n");
        Assert.Equal(Changed("Arbeit"), await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Thirty_writes_make_few_hints()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        using var socket = await ConnectAsync(await TicketAsync(user, key));
        var catalog = Api.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>();
        var vault = (await catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single();

        // The events of thirty commits within 100 ms, as an agent writing many notes makes them.
        for (var i = 0; i < 30; i++)
        {
            catalog.RaiseNotesChanged(vault.Id);
            await Task.Delay(3, Ct);
        }

        var hints = 0;
        while (await ReceiveAsync(socket, TimeSpan.FromSeconds(1.5)) is { } message)
        {
            Assert.Equal(Changed("Arbeit"), message);
            hints++;
        }

        Assert.InRange(hints, 1, 2);
    }

    [Fact]
    public async Task A_ticket_works_once_and_for_30_seconds()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        var ticket = await TicketAsync(user, key);
        using var first = await ConnectAsync(ticket);

        var again = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(ticket));
        var late = await TicketAsync(user, key);
        _time.Advance(TimeSpan.FromSeconds(31));
        var expired = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(late));

        Assert.Contains("401", again.Message, StringComparison.Ordinal);
        Assert.Contains("401", expired.Message, StringComparison.Ordinal);
        Assert.Equal(WebSocketState.Open, first.State);
    }

    [Fact]
    public async Task Only_api_keys_and_sessions_get_tickets()
    {
        var user = await NewAsync(Api);

        using var asSession = await user.SendAsync(HttpMethod.Post, "/api/ws-ticket");
        using var asDev = await user.SendAsync(HttpMethod.Post, "/api/ws-ticket", bearer: KumbuAppFactory.Token);

        Assert.Equal(HttpStatusCode.OK, asSession.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asDev.StatusCode);
    }

    private SyncHub Hub => Api.Services.GetRequiredService<SyncHub>();

    /// <summary>Alice's "Gartenarbeit", shared with Bob (who has his own "Work") as "garten".</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId, string Membership)> SharedAsync(MemberRole role = MemberRole.Write)
    {
        var alice = await NewAsync(Api, vault: "Gartenarbeit");
        var bob = await NewAsync(Api, vault: "Work");
        var membership = await alice.ShareWithAsync(bob, "garten", role);
        return (alice, bob, await alice.VaultIdAsync(), membership);
    }

    /// <summary>The vaults the hub holds for <paramref name="user"/>'s sessions, by their names, with the levels.</summary>
    private IReadOnlyList<(string Name, AccessLevel Level)> SessionVaults(RestClient user) =>
        [.. Hub.Held.Where(h => h.Subject.Kind == SubjectKind.Session && h.Subject.User == user.User.Id).Select(h => (h.Name, h.Level)).Distinct().OrderBy(h => h.Name, StringComparer.Ordinal)];

    [Fact]
    public async Task A_session_gets_a_ticket_and_hears_hints_under_its_names()
    {
        var (alice, bob, _, _) = await SharedAsync();
        using var socket = await SyncSockets.OpenAsync(Api, bob);

        await alice.CreateAsync("plan.md", "x\n");
        Assert.Equal(Changed("garten"), await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
        await bob.CreateAsync("eigen.md", "y\n");
        Assert.Equal(Changed("Work"), await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
        Assert.Equal([("Work", AccessLevel.Owner), ("garten", AccessLevel.Write)], SessionVaults(bob));
    }

    [Fact]
    public async Task A_fifth_tab_replaces_the_oldest_of_that_session()
    {
        var user = await NewAsync(Api);
        var sockets = new List<WebSocket>();
        for (var i = 0; i <= SyncHub.MaxPerKey; i++) sockets.Add(await SyncSockets.OpenAsync(Api, user));

        Assert.Equal("close 4409", await ReceiveAsync(sockets[0], TimeSpan.FromSeconds(2)));
        await user.CreateAsync("plan.md", "x\n");
        foreach (var socket in sockets.Skip(1)) Assert.Equal(Changed("Arbeit"), await ReceiveAsync(socket, TimeSpan.FromSeconds(3)));
        foreach (var socket in sockets) socket.Dispose();
    }

    [Fact]
    public async Task Two_sessions_of_one_user_are_two_subjects()
    {
        var user = await NewAsync(Api);
        var second = await user.SignInAgainAsync();
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        var sockets = new List<WebSocket>();
        for (var i = 0; i < SyncHub.MaxPerKey; i++) sockets.Add(await SyncSockets.OpenAsync(Api, user));
        using var others = await SyncSockets.OpenAsync(Api, second);
        using var keys = await SyncSockets.OpenAsync(Api, user, key);

        // Six connections of one person: none is replaced, each subject holds its own four.
        await user.CreateAsync("plan.md", "x\n");
        foreach (var socket in sockets.Append(others).Append(keys)) Assert.Equal(Changed("Arbeit"), await ReceiveAsync(socket, TimeSpan.FromSeconds(3)));
        Assert.Equal(SyncHub.MaxPerKey + 2, Hub.Held.Count(h => h.Subject.User == user.User.Id));

        // A fifth tab of the first session replaces that session's oldest, not the other session's.
        using var fifth = await SyncSockets.OpenAsync(Api, user);
        Assert.Equal("close 4409", await ReceiveAsync(sockets[0], TimeSpan.FromSeconds(2)));
        Assert.Null(await ReceiveAsync(others, TimeSpan.FromMilliseconds(300)));
        foreach (var socket in sockets) socket.Dispose();
    }

    [Fact]
    public async Task A_ticket_of_a_session_ended_since_opens_nothing()
    {
        var user = await NewAsync(Api);
        var ticket = await SyncSockets.TicketAsync(user);
        using (var signedOut = await user.SendAsync(HttpMethod.Delete, "/api/session")) Assert.True(signedOut.IsSuccessStatusCode);

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(ticket));

        Assert.Contains("401", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_check_drops_a_vault_whose_membership_was_revoked_behind_the_servers_back()
    {
        var (alice, bob, _, membership) = await SharedAsync();
        using var socket = await SyncSockets.OpenAsync(Api, bob);

        await Share.SetStatusAsync(app.Database, membership, MembershipStatus.Revoked);
        await Hub.CheckAsync(Ct);

        Assert.Equal([("Work", AccessLevel.Owner)], SessionVaults(bob));
        await alice.CreateAsync("plan.md", "x\n");
        Assert.Null(await ReceiveAsync(socket, TimeSpan.FromSeconds(1.5)));
        await bob.CreateAsync("eigen.md", "y\n");
        Assert.Equal(Changed("Work"), await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task The_check_follows_a_role_changed_behind_the_servers_back()
    {
        var (_, bob, _, membership) = await SharedAsync(MemberRole.Read);
        using var socket = await SyncSockets.OpenAsync(Api, bob);
        Assert.Contains(("garten", AccessLevel.Read), SessionVaults(bob));

        await Share.SetRoleAsync(app.Database, membership, MemberRole.Maintain);
        await Hub.CheckAsync(Ct);

        Assert.Contains(("garten", AccessLevel.Maintain), SessionVaults(bob));
    }

    [Fact]
    public async Task The_check_closes_a_members_key_socket_left_with_no_vault()
    {
        var (_, bob, vaultId, membership) = await SharedAsync();
        var (key, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "garten – Obsidian Laptop", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct, kind: ApiKeyKind.Device);
        using var socket = await SyncSockets.OpenAsync(Api, bob, key);

        await Share.SetStatusAsync(app.Database, membership, MembershipStatus.Left);
        await Hub.CheckAsync(Ct);

        Assert.Equal("close 4401", await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    private IMongoCollection<UserSlotDocument> UserSlots => app.Database.GetCollection<UserSlotDocument>("user_slots");

    [Fact]
    public async Task An_open_tab_does_not_keep_its_session_alive()
    {
        var user = await NewAsync(Api);
        using var socket = await SyncSockets.OpenAsync(Api, user);
        var usedAt = (await UserSlots.Find(s => s.User == user.User.Id && s.Kind == UserSlotDocument.Session).SingleAsync(Ct)).LastUsedAt;

        _time.Advance(TimeSpan.FromMinutes(29));
        await Hub.CheckAsync(Ct);
        Assert.Null(await ReceiveAsync(socket, TimeSpan.FromMilliseconds(300)));
        Assert.Equal(usedAt, (await UserSlots.Find(s => s.User == user.User.Id && s.Kind == UserSlotDocument.Session).SingleAsync(Ct)).LastUsedAt);

        // The session's idle time is up: the socket closes with it.
        _time.Advance(TimeSpan.FromMinutes(2));
        await Hub.CheckAsync(Ct);
        Assert.Equal("close 4401", await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task A_deactivated_users_session_socket_closes_with_4401()
    {
        var user = await NewAsync(Api);
        using var socket = await SyncSockets.OpenAsync(Api, user);

        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == user.User.Id, Builders<UserDocument>.Update.Set(u => u.Active, false), cancellationToken: Ct);
        await Hub.CheckAsync(Ct);

        Assert.Equal("close 4401", await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task A_key_holds_ten_open_tickets_and_a_new_one_drops_the_oldest()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        var other = await user.KeyAsync("agent", KeyRight.Read);
        var others = await TicketAsync(user, other);

        var tickets = new List<string>();
        for (var i = 0; i <= SyncTickets.MaxPerKey; i++) tickets.Add(await TicketAsync(user, key));

        Assert.Equal(SyncTickets.MaxPerKey + 1, Api.Services.GetRequiredService<SyncTickets>().Count);
        Assert.Null(Api.Services.GetRequiredService<SyncTickets>().Take(tickets[0]));
        Assert.NotNull(Api.Services.GetRequiredService<SyncTickets>().Take(tickets[^1]));
        Assert.NotNull(Api.Services.GetRequiredService<SyncTickets>().Take(others));
    }

    [Fact]
    public async Task A_fifth_connection_of_a_key_closes_its_oldest()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        var other = await user.KeyAsync("agent", KeyRight.Read);
        using var others = await ConnectAsync(await TicketAsync(user, other));

        var sockets = new List<WebSocket>();
        for (var i = 0; i <= SyncHub.MaxPerKey; i++) sockets.Add(await ConnectAsync(await TicketAsync(user, key)));

        Assert.Equal("close 4409", await ReceiveAsync(sockets[0], TimeSpan.FromSeconds(2)));
        await user.CreateAsync("plan.md", "x\n");
        Assert.Equal(Changed("Arbeit"), await ReceiveAsync(sockets[^1], TimeSpan.FromSeconds(3)));
        Assert.Equal(Changed("Arbeit"), await ReceiveAsync(others, TimeSpan.FromSeconds(3)));
        Assert.Equal(SyncHub.MaxPerKey + 1, Api.Services.GetRequiredService<SyncHub>().Count);
        foreach (var socket in sockets) socket.Dispose();
    }

    [Fact]
    public async Task A_key_revokes_itself_and_its_socket_closes_at_the_next_check()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        var other = await user.KeyAsync("agent", KeyRight.Read);
        using var socket = await ConnectAsync(await TicketAsync(user, key));

        using var revoked = await user.SendAsync(HttpMethod.Delete, "/api/key", bearer: key);
        using var after = await user.GetAsync($"/api/vaults/{user.Vault}/versions", key);
        using var otherAfter = await user.GetAsync($"/api/vaults/{user.Vault}/versions", other);
        await Api.Services.GetRequiredService<SyncHub>().CheckAsync(Ct);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherAfter.StatusCode);
        Assert.Equal("close 4401", await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Only_an_api_key_revokes_itself()
    {
        var user = await NewAsync(Api);

        using var asSession = await user.SendAsync(HttpMethod.Delete, "/api/key");
        using var asDev = await user.SendAsync(HttpMethod.Delete, "/api/key", bearer: KumbuAppFactory.Token);

        Assert.Equal(HttpStatusCode.Forbidden, asSession.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asDev.StatusCode);
    }

    [Fact]
    public async Task A_revoked_key_is_disconnected()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        using var socket = await ConnectAsync(await TicketAsync(user, key));
        var keys = Api.Services.GetRequiredService<ApiKeys>();
        var id = Assert.Single(await keys.ListAsync(user.User, user.UserKey, Ct)).Id;

        Assert.True(await keys.RevokeAsync(user.User, id, Ct));
        await Api.Services.GetRequiredService<SyncHub>().CheckAsync(Ct);

        Assert.Equal("close 4401", await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task A_ticket_of_a_key_revoked_since_opens_nothing()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        var ticket = await TicketAsync(user, key);
        var keys = Api.Services.GetRequiredService<ApiKeys>();
        Assert.True(await keys.RevokeAsync(user.User, Assert.Single(await keys.ListAsync(user.User, user.UserKey, Ct)).Id, Ct));

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(ticket));

        Assert.Contains("401", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A client that completed the upgrade and then stopped reading: every send to it hangs, whatever the token says.</summary>
    private sealed class StuckSocket : WebSocket
    {
        private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;

        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _aborted.TrySetResult();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => new TaskCompletionSource().Task;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => new TaskCompletionSource().Task;

        public override void Dispose()
        {
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.WhenAny(_aborted.Task, Task.Delay(Timeout.Infinite, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            throw new WebSocketException("aborted");
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SendStarted.TrySetResult();
            return new TaskCompletionSource().Task;
        }
    }

    [Fact]
    public async Task A_client_that_stops_reading_holds_up_neither_the_check_nor_other_connections()
    {
        var user = await NewAsync(Api);
        await user.KeyAsync("Arbeit – Obsidian Stuck", KeyRight.Write, ApiKeyKind.Device);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        using var socket = await ConnectAsync(await TicketAsync(user, key));
        var hub = Api.Services.GetRequiredService<SyncHub>();
        var catalog = Api.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>();
        var vault = (await catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single();
        var keys = Api.Services.GetRequiredService<ApiKeys>();
        var listed = await keys.ListAsync(user.User, user.UserKey, Ct);
        var stuck = new StuckSocket();
        var grant = new TicketGrant(
            new Subject(SubjectKind.Key, listed.Single(k => k.Name.EndsWith("Stuck", StringComparison.Ordinal)).Id, user.User.Id),
            [new TicketVault(vault.Id, vault.Name, null, Kumbu.Core.Notes.AccessLevel.Write)]);
        var running = hub.RunAsync(stuck, grant, Ct);
        catalog.RaiseNotesChanged(vault.Id);
        await stuck.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(Changed("Arbeit"), await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
        foreach (var k in listed) Assert.True(await keys.RevokeAsync(user.User, k.Id, Ct));

        await hub.CheckAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);

        Assert.Equal("close 4401", await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
        Assert.Equal(WebSocketState.Aborted, stuck.State);
        await running.WaitAsync(TimeSpan.FromSeconds(15), Ct);
    }

    [Fact]
    public async Task A_deleted_vault_is_dropped_from_the_socket()
    {
        var user = await NewAsync(Api);
        using var second = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Privat" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var catalog = Api.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>();
        var vaults = await catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct);
        var (key, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, "Both – Obsidian Laptop", vaults.ToDictionary(v => v.Id, _ => KeyRight.Write), Ct, kind: ApiKeyKind.Device);
        using var socket = await ConnectAsync(await TicketAsync(user, key));
        var privat = vaults.Single(v => v.Name == "Privat");

        using var deleted = await user.SendAsync(HttpMethod.Post, "/api/vaults/Privat/delete", new { password = Password, confirm = "Privat" });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        // Dropped with the answer, not at the next check: not even the deletion's own change is hinted.
        catalog.RaiseNotesChanged(privat.Id);

        Assert.Null(await ReceiveAsync(socket, TimeSpan.FromSeconds(1.5)));
        await user.CreateAsync("plan.md", "x\n");
        Assert.Equal(Changed("Arbeit"), await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task A_request_without_an_upgrade_is_400()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        var ticket = await TicketAsync(user, key);

        using var response = await user.GetAsync($"/api/ws?ticket={Uri.EscapeDataString(ticket)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_ticket_is_401()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync("kumbu_wst_" + new string('A', 43)));

        Assert.Contains("401", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoking_drops_the_vault_from_Bobs_tab_before_the_answer()
    {
        var (alice, bob, _, membership) = await SharedAsync();
        using var socket = await SyncSockets.OpenAsync(Api, bob);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Hub.BeforeAccessChange = () => gate.Task;
        try
        {
            var revoking = alice.SendAsync(HttpMethod.Post, $"/api/vaults/Gartenarbeit/memberships/{membership}/revoke");

            // The answer waits for the hub.
            Assert.NotSame(revoking, await Task.WhenAny(revoking, Task.Delay(500, Ct)));
            Assert.Contains(("garten", AccessLevel.Write), SessionVaults(bob));
            gate.SetResult();
            using var revoked = await revoking;
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }
        finally
        {
            Hub.BeforeAccessChange = null;
        }

        Assert.Equal([("Work", AccessLevel.Owner)], SessionVaults(bob));
        await alice.CreateAsync("plan.md", "x\n");
        Assert.Null(await ReceiveAsync(socket, TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public async Task Lowering_to_read_stops_Bobs_presence_frames_at_once()
    {
        var (alice, bob, _, membership) = await SharedAsync();
        using var alices = await SyncSockets.OpenAsync(Api, alice);
        using var bobs = await SyncSockets.OpenAsync(Api, bob);
        await SyncSockets.EditingAsync(bobs, "garten", "plan.md", on: true);
        Assert.Equal([bob.Username], SyncSockets.Editors((await SyncSockets.PresenceAsync(alices, TimeSpan.FromSeconds(3)))!));

        using (var lowered = await alice.SendAsync(HttpMethod.Put, $"/api/vaults/Gartenarbeit/memberships/{membership}/role", new { role = "read" }))
        {
            Assert.Equal(HttpStatusCode.OK, lowered.StatusCode);
        }

        // Gone with the answer, and nothing new from him.
        Assert.Empty(SyncSockets.Editors((await SyncSockets.PresenceAsync(alices, TimeSpan.FromMilliseconds(300)))!));
        Assert.Contains(("garten", AccessLevel.Read), SessionVaults(bob));
        await SyncSockets.EditingAsync(bobs, "garten", "beete.md", on: true);
        Assert.Null(await SyncSockets.PresenceAsync(alices, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Deactivating_the_owner_re_levels_members_to_read()
    {
        var (alice, bob, _, _) = await SharedAsync();
        var carol = await NewAsync(Api, vault: "Notizen");
        await alice.ShareWithAsync(carol, "beete");
        using var bobs = await SyncSockets.OpenAsync(Api, bob);
        using var carols = await SyncSockets.OpenAsync(Api, carol);
        await SyncSockets.EditingAsync(bobs, "garten", "plan.md", on: true);
        Assert.Equal([Kumbu.Server.Api.WriterLabels.AnotherUser], SyncSockets.Editors((await SyncSockets.PresenceAsync(carols, TimeSpan.FromSeconds(3)))!));
        var admin = await NewAsync(Api, vault: "Admin", role: Roles.Admin);

        using (var deactivated = await admin.SendAsync(HttpMethod.Patch, $"/api/admin/users/{alice.User.Id}", new { active = false }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);
        }

        Assert.Empty(SyncSockets.Editors((await SyncSockets.PresenceAsync(carols, TimeSpan.FromMilliseconds(300)))!));
        Assert.Contains(("garten", AccessLevel.Read), SessionVaults(bob));
        Assert.Contains(("beete", AccessLevel.Read), SessionVaults(carol));
        // Her own sockets went with her sessions; reactivated, the members are at their roles again.
        Assert.DoesNotContain(Hub.Held, h => h.Subject.User == alice.User.Id);
        using (var reactivated = await admin.SendAsync(HttpMethod.Patch, $"/api/admin/users/{alice.User.Id}", new { active = true }))
        {
            Assert.Equal(HttpStatusCode.NoContent, reactivated.StatusCode);
        }

        Assert.Contains(("garten", AccessLevel.Write), SessionVaults(bob));
    }

    [Fact]
    public async Task A_renamed_alias_names_the_hints_at_once()
    {
        var (alice, bob, vaultId, membership) = await SharedAsync();
        var (key, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "garten – Obsidian Laptop", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct, kind: ApiKeyKind.Device);
        using var tab = await SyncSockets.OpenAsync(Api, bob);
        using var device = await SyncSockets.OpenAsync(Api, bob, key);

        using (var renamed = await bob.SendAsync(HttpMethod.Put, $"/api/memberships/{membership}/alias", new { alias = "Beet" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        await alice.CreateAsync("plan.md", "x\n");
        Assert.Equal(Changed("Beet"), await ReceiveAsync(tab, TimeSpan.FromSeconds(2)));
        Assert.Equal(Changed("Beet"), await ReceiveAsync(device, TimeSpan.FromSeconds(2)));
    }

    /// <summary>Alice's "Gartenarbeit" with <c>plan.md</c>, shared with Bob (who owns "Work") as "Beet"; her key on it.
    /// The id and the key are taken before any rename, as <see cref="RestClient.Vault"/> keeps the old name.</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId, string Key)> RenamableAsync()
    {
        var alice = await NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "Beete\n");
        var bob = await NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "Beet");
        var vaultId = await alice.VaultIdAsync();
        var (key, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            alice.User, alice.UserKey, DateTime.UtcNow, "Gartenarbeit – Obsidian Laptop", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct, kind: ApiKeyKind.Device);
        return (alice, bob, vaultId, key);
    }

    private static async Task RenameToGartenAsync(RestClient alice)
    {
        using var renamed = await alice.SendAsync(HttpMethod.Put, "/api/vaults/Gartenarbeit/name", new { name = "Garten" });
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
    }

    private static async Task WriteAsync(RestClient alice)
    {
        using var written = await alice.SendAsync(HttpMethod.Put, $"/api/notes/Garten/{Guid.NewGuid():N}.md", new { content = "Beete\n" });
        Assert.Equal(HttpStatusCode.Created, written.StatusCode);
    }

    /// <summary>Until the hub holds <paramref name="count"/> connections of <paramref name="vaultId"/>: a socket is
    /// connected before the server lists it.</summary>
    private async Task HeldAsync(string vaultId, int count)
    {
        var waiting = System.Diagnostics.Stopwatch.StartNew();
        while (Hub.Held.Count(h => h.VaultId == vaultId) < count)
        {
            Assert.True(waiting.Elapsed < TimeSpan.FromSeconds(10), "The hub doesn't hold the connections.");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task A_renamed_vault_names_the_hints_at_once()
    {
        var (alice, bob, vaultId, key) = await RenamableAsync();
        using var tab = await SyncSockets.OpenAsync(Api, alice);
        using var device = await SyncSockets.OpenAsync(Api, alice, key);
        using var bobs = await SyncSockets.OpenAsync(Api, bob);
        await HeldAsync(vaultId, 3);

        await RenameToGartenAsync(alice);

        // Unprompted: her open tabs ask for the vault list again, and take a new ticket.
        Assert.Equal(Renamed("Garten", "Gartenarbeit"), await ReceiveAsync(tab, TimeSpan.FromSeconds(2)));
        Assert.Equal(Changed("Garten"), await ReceiveAsync(tab, TimeSpan.FromSeconds(2)));
        Assert.Equal(Renamed("Garten", "Gartenarbeit"), await ReceiveAsync(device, TimeSpan.FromSeconds(2)));
        Assert.Equal(Changed("Garten"), await ReceiveAsync(device, TimeSpan.FromSeconds(2)));
        Assert.Equal(["Garten", "Garten"], Hub.Held.Where(h => h.VaultId == vaultId && h.Subject.User == alice.User.Id).Select(h => h.Name).ToArray());
        Assert.Equal("Beet", Assert.Single(Hub.Held, h => h.VaultId == vaultId && h.Subject.User == bob.User.Id).Name);
        // Bob's alias didn't change: nothing to tell him.
        Assert.Null(await ReceiveAsync(bobs, TimeSpan.FromSeconds(1.5)));

        await WriteAsync(alice);
        Assert.Equal(Changed("Garten"), await ReceiveAsync(tab, TimeSpan.FromSeconds(2)));
        Assert.Equal(Changed("Garten"), await ReceiveAsync(device, TimeSpan.FromSeconds(2)));
        Assert.Equal(Changed("Beet"), await ReceiveAsync(bobs, TimeSpan.FromSeconds(2)));
    }

    private static string Renamed(string vault, string from) => new JsonObject { ["type"] = "renamed", ["vault"] = vault, ["from"] = from }.ToJsonString();

    /// <summary>Her tab's, her agent's and her device's sockets alike, the old and the new name ahead of the hint: a
    /// second tab on the old name follows.</summary>
    [Fact]
    public async Task A_rename_tells_her_sockets_the_old_and_the_new_name()
    {
        var (alice, _, vaultId, key) = await RenamableAsync();
        var (agentKey, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            alice.User, alice.UserKey, DateTime.UtcNow, "Agent", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Read }, Ct);
        using var tab = await SyncSockets.OpenAsync(Api, alice);
        using var agent = await SyncSockets.OpenAsync(Api, alice, agentKey);
        using var device = await SyncSockets.OpenAsync(Api, alice, key);
        await HeldAsync(vaultId, 3);

        await RenameToGartenAsync(alice);

        foreach (var socket in new[] { tab, agent, device })
        {
            Assert.Equal([Renamed("Garten", "Gartenarbeit"), Changed("Garten")], await SyncSockets.DrainAsync(socket, TimeSpan.FromSeconds(1.5)));
        }
    }

    [Fact]
    public async Task A_member_gets_no_renamed_frame()
    {
        var (alice, bob, vaultId, _) = await RenamableAsync();
        using var bobs = await SyncSockets.OpenAsync(Api, bob);
        await HeldAsync(vaultId, 1);

        await RenameToGartenAsync(alice);

        Assert.Empty(await SyncSockets.DrainAsync(bobs, TimeSpan.FromSeconds(1.5)));
    }

    /// <summary>A rename that committed but answers as if it failed: her sockets are told all the same, so her tabs ask
    /// for the vault list again and take a new ticket. The catalog forgot the vault, so the hint still names it as the
    /// socket held it; the new ticket names it as it is.</summary>
    [Fact]
    public async Task A_rename_whose_outcome_is_unknown_tells_her_sockets()
    {
        var (alice, _, vaultId, key) = await RenamableAsync();
        using var tab = await SyncSockets.OpenAsync(Api, alice);
        using var device = await SyncSockets.OpenAsync(Api, alice, key);
        await HeldAsync(vaultId, 2);
        var catalog = Api.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>();
        catalog.MetaFaultAt = point => point == "after-rename-commit" ? throw new InvalidOperationException("The answer was lost.") : Task.CompletedTask;
        try
        {
            using var renamed = await alice.SendAsync(HttpMethod.Put, "/api/vaults/Gartenarbeit/name", new { name = "Garten" });
            Assert.Equal(HttpStatusCode.InternalServerError, renamed.StatusCode);
        }
        finally
        {
            catalog.MetaFaultAt = null;
        }

        Assert.Equal(Changed("Gartenarbeit"), await ReceiveAsync(tab, TimeSpan.FromSeconds(2)));
        Assert.Equal(Changed("Gartenarbeit"), await ReceiveAsync(device, TimeSpan.FromSeconds(2)));

        // What the tab does on the hint: the list, read again, has the committed name, and so has the new ticket.
        using (var vaults = await alice.GetAsync("/api/vaults"))
        {
            Assert.Contains("\"Garten\"", await vaults.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using var reopened = await SyncSockets.OpenAsync(Api, alice);
        await HeldAsync(vaultId, 3);
        await WriteAsync(alice);
        Assert.Contains(Changed("Garten"), await SyncSockets.DrainAsync(reopened, TimeSpan.FromSeconds(1.5)));
    }

    /// <summary>Not knowing the names, the hub sends nothing that would move a tab: only the hint.</summary>
    [Fact]
    public async Task A_rename_whose_outcome_is_unknown_sends_no_renamed_frame()
    {
        var (alice, _, vaultId, key) = await RenamableAsync();
        using var tab = await SyncSockets.OpenAsync(Api, alice);
        using var device = await SyncSockets.OpenAsync(Api, alice, key);
        await HeldAsync(vaultId, 2);
        var catalog = Api.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>();
        catalog.MetaFaultAt = point => point == "after-rename-commit" ? throw new InvalidOperationException("The answer was lost.") : Task.CompletedTask;
        try
        {
            using var renamed = await alice.SendAsync(HttpMethod.Put, "/api/vaults/Gartenarbeit/name", new { name = "Garten" });
            Assert.Equal(HttpStatusCode.InternalServerError, renamed.StatusCode);
        }
        finally
        {
            catalog.MetaFaultAt = null;
        }

        Assert.Equal([Changed("Gartenarbeit")], await SyncSockets.DrainAsync(tab, TimeSpan.FromSeconds(1.5)));
        Assert.Equal([Changed("Gartenarbeit")], await SyncSockets.DrainAsync(device, TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public async Task A_ticket_from_before_a_rename_names_the_new_name()
    {
        var (alice, bob, vaultId, key) = await RenamableAsync();
        var tabTicket = await SyncSockets.TicketAsync(alice);
        var deviceTicket = await SyncSockets.TicketAsync(alice, key);
        var bobsTicket = await SyncSockets.TicketAsync(bob);

        await RenameToGartenAsync(alice);
        using var tab = await SyncSockets.ConnectAsync(Api, tabTicket);
        using var device = await SyncSockets.ConnectAsync(Api, deviceTicket);
        using var bobs = await SyncSockets.ConnectAsync(Api, bobsTicket);
        await HeldAsync(vaultId, 3);

        await WriteAsync(alice);
        foreach (var (socket, name) in new[] { (tab, "Garten"), (device, "Garten"), (bobs, "Beet") })
        {
            var messages = await SyncSockets.DrainAsync(socket, TimeSpan.FromSeconds(1.5));
            Assert.Contains(Changed(name), messages);
            Assert.DoesNotContain(messages, m => m.Contains("Gartenarbeit", StringComparison.Ordinal));
        }
    }
}
