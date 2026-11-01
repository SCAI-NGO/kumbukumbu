// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Server.Api;
using Kumbu.Server.Sharing;
using Kumbu.Server.Sync;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.SyncSockets;

namespace Kumbu.Server.Tests.Api;

/// <summary>
/// Presence on the sync socket (collab design §9.2): who is editing which note, announced by sessions that may write,
/// told to the sessions with the vault, rendered for each receiver by §8.2's rules, and on a channel of its own.
/// </summary>
[Collection("app")]
public sealed class PresenceTests(KumbuAppFactory app) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<Program> Api => _api ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    private SyncHub Hub => Api.Services.GetRequiredService<SyncHub>();

    public async ValueTask DisposeAsync()
    {
        if (_api is not null) await _api.DisposeAsync();
    }

    /// <summary>Alice's "Gartenarbeit", shared with Bob as "garten" (her label for him "Bob") and Carol as "beete" ("Carol").</summary>
    private async Task<(RestClient Alice, RestClient Bob, RestClient Carol)> SharedAsync(MemberRole carolsRole = MemberRole.Write)
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        await alice.ShareWithAsync(bob, "garten", MemberRole.Write, label: "Bob");
        await alice.ShareWithAsync(carol, "beete", carolsRole, label: "Carol");
        return (alice, bob, carol);
    }

    private static async Task<string[]> EditorsAsync(WebSocket socket) => Editors((await PresenceAsync(socket, Wait)) ?? throw new Xunit.Sdk.XunitException("no presence came"));

    [Fact]
    public async Task The_owner_sees_Bob_editing()
    {
        var (alice, bob, _) = await SharedAsync();
        using var alices = await OpenAsync(Api, alice);
        using var bobs = await OpenAsync(Api, bob);

        await EditingAsync(bobs, "garten", "plan.md", on: true);

        var presence = (await PresenceAsync(alices, Wait))!;
        Assert.Equal(new JsonObject { ["type"] = "presence", ["vault"] = "Gartenarbeit", ["path"] = "plan.md", ["editors"] = new JsonArray("Bob") }.ToJsonString(), presence.ToJsonString());
        // Bob is not shown himself.
        Assert.Empty(Editors((await PresenceAsync(bobs, Wait))!));
    }

    [Fact]
    public async Task Bob_sees_Alice_and_one_other_user()
    {
        var (alice, bob, carol) = await SharedAsync();
        var dora = await RestClient.NewAsync(Api, vault: "Dora");
        await alice.ShareWithAsync(dora, "dorasgarten", MemberRole.Write, label: "Dora");
        using var alices = await OpenAsync(Api, alice);
        using var bobs = await OpenAsync(Api, bob);
        using var carols = await OpenAsync(Api, carol);
        using var doras = await OpenAsync(Api, dora);

        await EditingAsync(alices, "Gartenarbeit", "plan.md", on: true);
        Assert.Equal([$"\"{alice.User.Display}\""], await EditorsAsync(bobs));
        await EditingAsync(carols, "beete", "plan.md", on: true);

        // One state, rendered for each: Bob sees the owner by name and Carol as another user; Alice sees Carol's label.
        Assert.Equal([$"\"{alice.User.Display}\"", WriterLabels.AnotherUser], await EditorsAsync(bobs));
        Assert.Equal(["Carol"], (await DrainPresenceAsync(alices))[^1]);
        await EditingAsync(doras, "dorasgarten", "plan.md", on: true);
        Assert.Equal([$"\"{alice.User.Display}\"", "2 other users"], await EditorsAsync(bobs));
        Assert.Equal(["Carol", "Dora"], (await DrainPresenceAsync(alices))[^1]);
    }

    /// <summary>Every presence state that arrives until a second passes without one, in order.</summary>
    private static async Task<List<string[]>> DrainPresenceAsync(WebSocket socket)
    {
        var states = new List<string[]>();
        while (await PresenceAsync(socket, TimeSpan.FromSeconds(1)) is { } presence) states.Add(Editors(presence));
        return states;
    }

    [Fact]
    public async Task A_read_member_cannot_announce_editing()
    {
        var (alice, _, carol) = await SharedAsync(MemberRole.Read);
        using var alices = await OpenAsync(Api, alice);
        using var carols = await OpenAsync(Api, carol);

        await EditingAsync(carols, "beete", "plan.md", on: true);
        Assert.Null(await PresenceAsync(alices, TimeSpan.FromSeconds(1)));

        // Nor does a key, whatever its right.
        var key = await alice.KeyAsync("Claude", Kumbu.Server.Accounts.KeyRight.Write);
        using var keys = await OpenAsync(Api, alice, key);
        await EditingAsync(keys, "Gartenarbeit", "plan.md", on: true);
        Assert.Null(await PresenceAsync(alices, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task An_entry_expires_after_60_seconds()
    {
        var (alice, bob, _) = await SharedAsync();
        using var alices = await OpenAsync(Api, alice);
        using var bobs = await OpenAsync(Api, bob);
        await EditingAsync(bobs, "garten", "plan.md", on: true);
        Assert.Equal(["Bob"], await EditorsAsync(alices));

        // A refresh keeps it, and isn't told again.
        _time.Advance(TimeSpan.FromSeconds(50));
        await EditingAsync(bobs, "garten", "plan.md", on: true);
        Assert.Null(await PresenceAsync(alices, TimeSpan.FromMilliseconds(500)));
        _time.Advance(TimeSpan.FromSeconds(59));
        Hub.ExpirePresence();
        Assert.Null(await PresenceAsync(alices, TimeSpan.FromMilliseconds(500)));

        _time.Advance(TimeSpan.FromSeconds(2));
        Hub.ExpirePresence();
        Assert.Empty(await EditorsAsync(alices));
    }

    [Fact]
    public async Task Closing_the_tab_ends_its_presence()
    {
        var (alice, bob, _) = await SharedAsync();
        using var alices = await OpenAsync(Api, alice);
        var bobs = await OpenAsync(Api, bob);
        await EditingAsync(bobs, "garten", "plan.md", on: true);
        Assert.Equal(["Bob"], await EditorsAsync(alices));

        await bobs.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Ct);
        bobs.Dispose();

        Assert.Empty(await EditorsAsync(alices));
    }

    [Fact]
    public async Task A_frame_over_1_KiB_is_ignored()
    {
        var (alice, bob, _) = await SharedAsync();
        using var alices = await OpenAsync(Api, alice);
        using var bobs = await OpenAsync(Api, bob);
        var frame = new JsonObject { ["type"] = "editing", ["vault"] = "garten", ["path"] = "plan.md", ["on"] = true, ["pad"] = new string('x', 1000) }.ToJsonString();
        Assert.True(Encoding.UTF8.GetByteCount(frame) > Presence.MaxFrame);

        await SendAsync(bobs, frame);
        Assert.Null(await PresenceAsync(alices, TimeSpan.FromSeconds(1)));

        // The same frame within the limit is taken, and so is one sent in pieces.
        await EditingAsync(bobs, "garten", "plan.md", on: true);
        Assert.Equal(["Bob"], await EditorsAsync(alices));
        var off = Encoding.UTF8.GetBytes(new JsonObject { ["type"] = "editing", ["vault"] = "garten", ["path"] = "plan.md", ["on"] = false }.ToJsonString());
        await bobs.SendAsync(off.AsMemory(0, 10), WebSocketMessageType.Text, endOfMessage: false, Ct);
        await bobs.SendAsync(off.AsMemory(10), WebSocketMessageType.Text, endOfMessage: true, Ct);
        Assert.Empty(await EditorsAsync(alices));
    }

    /// <summary>A client whose sends are held until <see cref="Gate"/> opens, recording what it was sent.</summary>
    private sealed class GatedSocket : WebSocket
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Sent { get; } = new();

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _closed.TrySetResult();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            _closed.TrySetResult();
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.WhenAny(_closed.Task, Task.Delay(Timeout.Infinite, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "");
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var text = Encoding.UTF8.GetString(buffer);
            FirstSend.TrySetResult();
            await Gate.Task.WaitAsync(cancellationToken);
            Sent.Enqueue(text);
        }
    }

    [Fact]
    public async Task An_off_is_never_lost_in_a_burst_of_hints()
    {
        var (alice, bob, _) = await SharedAsync();
        using var bobs = await OpenAsync(Api, bob);
        // Alice's tab reads nothing until the burst is over.
        var grant = Api.Services.GetRequiredService<SyncTickets>().Take(await TicketAsync(alice))!;
        var alices = new GatedSocket();
        var catalog = Api.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>();
        var vaultId = await alice.VaultIdAsync();
        Hub.HintDebounce = TimeSpan.Zero;
        try
        {
            var running = Hub.RunAsync(alices, grant, Ct);
            await EditingAsync(bobs, "garten", "plan.md", on: true);
            await alices.FirstSend.Task.WaitAsync(Wait, Ct);
            await EditingAsync(bobs, "garten", "plan.md", on: false);
            // The off is in before the burst: Bob's own presence line shows it went through.
            Assert.Empty(Editors((await PresenceAsync(bobs, Wait))!));
            Assert.Empty(Editors((await PresenceAsync(bobs, Wait))!));
            for (var i = 0; i < 1000; i++) catalog.RaiseNotesChanged(vaultId);

            alices.Gate.SetResult();
            await Task.Delay(500, Ct);
            await alices.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", Ct);
            await running.WaitAsync(Wait, Ct);
        }
        finally
        {
            Hub.HintDebounce = SyncHub.Debounce;
        }

        var sent = alices.Sent.Select(m => JsonNode.Parse(m)!).ToList();
        Assert.Contains(sent, m => m["type"]!.GetValue<string>() == "changed");
        Assert.Equal([["Bob"], []], sent.Where(m => m["type"]!.GetValue<string>() == "presence").Select(Editors).ToList());
    }
}
