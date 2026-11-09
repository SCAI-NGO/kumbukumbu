// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sync;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.SyncSockets;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>
/// The sync sockets of a web session and discreet vaults (discreet vaults design, Decision 8 as the plan review ruled):
/// a ticket asked while hidden holds none; one asked while revealed holds them until the reveal ends (✕, the time-out,
/// or a check that finds it over), when the socket is told <c>hidden</c> and closed with <see cref="SyncHub.Hidden"/>.
/// API keys' sockets are untouched.
/// </summary>
[Collection("app")]
public sealed class DiscreetSocketTests(KumbuAppFactory app) : IAsyncDisposable
{
    private const string Pin = "4711";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly string HiddenFrame = new JsonObject { ["type"] = "hidden" }.ToJsonString();

    private static readonly string RevealedFrame = new JsonObject { ["type"] = "revealed" }.ToJsonString();

    private static readonly string ClosedHidden = $"close {(int)SyncHub.Hidden}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The real time, in whole seconds: MongoDB's TTL monitor sweeps slots made at a past fake time.</summary>
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

    /// <summary>Alice's world, her PIN set, hints at once.</summary>
    private async Task<HiddenWorld> WorldAsync()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.HideAsync();
        using var pin = await w.Alice.SendAsync(HttpMethod.Post, "/api/account/discreet/pin", new { password = RestClient.Password, pin = Pin });
        Assert.Equal(HttpStatusCode.NoContent, pin.StatusCode);
        Hub.HintDebounce = TimeSpan.Zero;
        return w;
    }

    private static async Task RevealAsync(RestClient session, int? minutes = null)
    {
        using var response = await session.SendAsync(HttpMethod.Post, "/api/account/discreet/reveal", new { pin = Pin, minutes });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task HideAsync(RestClient session)
    {
        using var response = await session.SendAsync(HttpMethod.Post, "/api/account/discreet/hide");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task MarkAsync(RestClient session, string vault, bool discreet)
    {
        using var response = await session.SendAsync(HttpMethod.Put, $"/api/account/discreet/vaults/{vault}", new { discreet });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>A new note in <paramref name="vault"/>, through the session or <paramref name="bearer"/>'s key.</summary>
    private static async Task WriteAsync(RestClient client, string vault, string? bearer = null)
    {
        var path = $"n{Guid.NewGuid():N}.md";
        using var put = await client.SendAsync(HttpMethod.Put, $"/api/notes/{vault}/{path}", new { content = "neu\n" }, bearer: bearer);
        Assert.True(put.IsSuccessStatusCode, $"PUT {vault}/{path}: {put.StatusCode}");
    }

    private static string SubjectOf(RestClient session) => Sessions.SubjectOf(session.Session)!;

    /// <summary>The vault names the hub holds for the sockets of <paramref name="subject"/> (a session's or a key's id).</summary>
    private string[] HeldBy(string subject) =>
        [.. Hub.Held.Where(h => h.Subject.Id == subject).Select(h => h.Name).Distinct().Order(StringComparer.Ordinal)];

    /// <summary>The id of the one key socket listed, once it is.</summary>
    private async Task<string> KeySubjectAsync()
    {
        var until = DateTime.UtcNow + Wait;
        while (true)
        {
            if (Hub.Held.FirstOrDefault(h => h.Subject.Kind == SubjectKind.Key).Subject is { } subject) return subject.Id;
            Assert.True(DateTime.UtcNow < until, "the hub never listed the key's socket");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>Waits until the hub lists <paramref name="sockets"/> sockets in all and one of <paramref name="subject"/>.</summary>
    private async Task ListedAsync(string subject, int sockets)
    {
        var until = DateTime.UtcNow + Wait;
        while (Hub.Count < sockets || !Hub.Held.Any(h => h.Subject.Id == subject))
        {
            Assert.True(DateTime.UtcNow < until, "the hub never listed the sockets");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>Writes to <c>Work</c> through the session: the hint marks the end of what came before.</summary>
    private static async Task<IReadOnlyList<string>> UntilWorkHintAsync(HiddenWorld w, WebSocket socket)
    {
        await WriteAsync(w.Alice, "Work");
        var seen = new List<string>();
        while (await ReceiveAsync(socket, Wait) is { } message)
        {
            seen.Add(message);
            if (message == Changed("Work") || message.StartsWith("close", StringComparison.Ordinal)) break;
        }

        return seen;
    }

    [Fact]
    public async Task A_ticket_asked_while_hidden_holds_no_discreet_vault_and_hears_nothing_of_one()
    {
        var w = await WorldAsync();
        var key = await w.In("Tagebuch").KeyAsync("Agent", KeyRight.Write);
        using var socket = await OpenAsync(Api, w.Alice);
        await ListedAsync(SubjectOf(w.Alice), 1);

        Assert.Equal(["Skills", "Work"], HeldBy(SubjectOf(w.Alice)));
        await WriteAsync(w.Alice, "Tagebuch", bearer: key);
        Assert.Equal([Changed("Work")], await UntilWorkHintAsync(w, socket));
    }

    [Fact]
    public async Task Hiding_tells_and_closes_every_revealed_socket_of_the_session_and_leaves_the_key_alone()
    {
        var w = await WorldAsync();
        var key = await w.In("Tagebuch").KeyAsync("Agent", KeyRight.Write);
        await RevealAsync(w.Alice);
        using var first = await OpenAsync(Api, w.Alice);
        using var second = await OpenAsync(Api, w.Alice);
        using var agent = await OpenAsync(Api, w.Alice, key);
        await ListedAsync(SubjectOf(w.Alice), 3);
        var keySubject = await KeySubjectAsync();

        Assert.Equal(["Familie", "Skills", "Tagebuch", "Work"], HeldBy(SubjectOf(w.Alice)));
        await WriteAsync(w.Alice, "Tagebuch", bearer: key);
        Assert.Equal(Changed("Tagebuch"), await ReceiveAsync(first, Wait));
        Assert.Equal(Changed("Tagebuch"), await ReceiveAsync(second, Wait));
        Assert.Equal(Changed("Tagebuch"), await ReceiveAsync(agent, Wait));

        await HideAsync(w.Alice);

        Assert.Equal([HiddenFrame, ClosedHidden], await DrainAsync(first, Wait));
        Assert.Equal([HiddenFrame, ClosedHidden], await DrainAsync(second, Wait));
        Assert.Empty(HeldBy(SubjectOf(w.Alice)));
        // The key keeps its vault and its hints.
        Assert.Equal(["Tagebuch"], HeldBy(keySubject));
        await WriteAsync(w.Alice, "Tagebuch", bearer: key);
        Assert.Equal(Changed("Tagebuch"), await ReceiveAsync(agent, Wait));
        await Hub.CheckAsync(Ct);
        Assert.Equal(["Tagebuch"], HeldBy(keySubject));
        Assert.Equal(WebSocketState.Open, agent.State);
    }

    [Fact]
    public async Task A_timed_reveal_closes_its_sockets_when_it_ends()
    {
        var w = await WorldAsync();
        await RevealAsync(w.Alice, minutes: 5);
        using var socket = await OpenAsync(Api, w.Alice);
        await ListedAsync(SubjectOf(w.Alice), 1);

        _time.Advance(TimeSpan.FromMinutes(4));
        await Hub.CheckAsync(Ct);
        Assert.Equal(["Familie", "Skills", "Tagebuch", "Work"], HeldBy(SubjectOf(w.Alice)));

        // The timer, not the 30-second check.
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal([HiddenFrame, ClosedHidden], await DrainAsync(socket, Wait));
        Assert.Empty(HeldBy(SubjectOf(w.Alice)));
    }

    [Fact]
    public async Task The_regular_check_closes_a_socket_whose_session_no_longer_reveals()
    {
        var w = await WorldAsync();
        await RevealAsync(w.Alice);
        using var socket = await OpenAsync(Api, w.Alice);
        await ListedAsync(SubjectOf(w.Alice), 1);

        // Hidden without telling the hub.
        await Api.Services.GetRequiredService<Sessions>().HideAsync(w.Alice.Session, Ct);
        await Hub.CheckAsync(Ct);

        Assert.Equal([HiddenFrame, ClosedHidden], await DrainAsync(socket, Wait));
    }

    [Fact]
    public async Task A_revealed_ticket_used_after_the_reveal_ended_closes_at_once()
    {
        var w = await WorldAsync();
        await RevealAsync(w.Alice);
        var ticket = await TicketAsync(w.Alice);
        await Api.Services.GetRequiredService<Sessions>().HideAsync(w.Alice.Session, Ct);

        using var socket = await ConnectAsync(Api, ticket);

        Assert.Equal([HiddenFrame, ClosedHidden], await DrainAsync(socket, Wait));
    }

    [Fact]
    public async Task Marking_closes_the_sockets_of_sessions_that_hide_now_and_the_revealed_ones_at_the_end()
    {
        var w = await WorldAsync();
        var key = await w.Alice.KeyAsync("Agent", KeyRight.Write);
        var other = await w.Alice.SignInAgainAsync();
        await RevealAsync(w.Alice);
        using var revealed = await OpenAsync(Api, w.Alice);
        using var hidden = await OpenAsync(Api, other);
        using var agent = await OpenAsync(Api, w.Alice, key);
        await ListedAsync(SubjectOf(w.Alice), 3);
        await ListedAsync(SubjectOf(other), 3);

        await MarkAsync(w.Alice, "Work", discreet: true);

        // No hidden frame for a session that shows nothing discreet: it only takes a new ticket.
        Assert.Equal([ClosedHidden], await DrainAsync(hidden, Wait));
        // A ticket asked after the mark, not in its very clock tick.
        _time.Advance(TimeSpan.FromSeconds(1));
        using var again = await OpenAsync(Api, other);
        await ListedAsync(SubjectOf(other), 1);
        Assert.Equal(["Skills"], HeldBy(SubjectOf(other)));
        // The revealed session and the key keep it.
        Assert.Equal([Changed("Work")], await UntilWorkHintAsync(w, revealed));
        Assert.Equal(Changed("Work"), await ReceiveAsync(agent, Wait));

        await HideAsync(w.Alice);

        Assert.Equal([HiddenFrame, ClosedHidden], await DrainAsync(revealed, Wait));
        Assert.Equal(WebSocketState.Open, agent.State);
        await WriteAsync(w.Alice, "Work", bearer: key);
        Assert.Equal(Changed("Work"), await ReceiveAsync(agent, Wait));
    }

    [Fact]
    public async Task A_ticket_asked_before_a_mark_and_used_after_it_closes_at_once()
    {
        var w = await WorldAsync();
        var other = await w.Alice.SignInAgainAsync();
        var elsewhere = await TicketAsync(other);
        // Her own, asked before she showed the discreet vaults.
        var own = await TicketAsync(w.Alice);
        await RevealAsync(w.Alice);
        await MarkAsync(w.Alice, "Work", discreet: true);

        using var late = await ConnectAsync(Api, elsewhere);
        using var lateOwn = await ConnectAsync(Api, own);

        Assert.Equal([ClosedHidden], await DrainAsync(late, Wait));
        Assert.Equal([ClosedHidden], await DrainAsync(lateOwn, Wait));
        // One asked after the mark holds what the session shows now.
        _time.Advance(TimeSpan.FromSeconds(1));
        using var again = await OpenAsync(Api, other);
        await ListedAsync(SubjectOf(other), 1);
        Assert.Equal(["Skills"], HeldBy(SubjectOf(other)));
        Assert.Equal(WebSocketState.Open, again.State);
    }

    [Fact]
    public async Task A_reveal_renewed_while_shown_closes_its_sockets_so_they_end_with_it()
    {
        var w = await WorldAsync();
        await RevealAsync(w.Alice, minutes: 5);
        using var socket = await OpenAsync(Api, w.Alice);
        await ListedAsync(SubjectOf(w.Alice), 1);

        await RevealAsync(w.Alice, minutes: 10);

        Assert.Equal([RevealedFrame, ClosedHidden], await DrainAsync(socket, Wait));
        using var again = await OpenAsync(Api, w.Alice);
        Assert.Equal([Changed("Work")], await UntilWorkHintAsync(w, again));
        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal([Changed("Work")], await UntilWorkHintAsync(w, again));

        // The timer of the new ticket, not the 30-second check.
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal([HiddenFrame, ClosedHidden], await DrainAsync(again, Wait));
    }

    [Fact]
    public async Task A_reveal_timer_that_finds_the_reveal_still_shown_waits_for_its_end()
    {
        var w = await WorldAsync();
        await RevealAsync(w.Alice, minutes: 5);
        using var socket = await OpenAsync(Api, w.Alice);
        // Up and checked: hints go out only after the first check.
        Assert.Equal([Changed("Work")], await UntilWorkHintAsync(w, socket));
        // Renewed without telling the hub, as a clock running behind the database's would leave it.
        await Api.Services.GetRequiredService<Sessions>().RevealAsync(w.Alice.Session, TimeSpan.FromMinutes(10), Ct);
        var checkedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Hub.BeforeRecompute = _ =>
        {
            checkedOnce.TrySetResult();
            return Task.CompletedTask;
        };

        _time.Advance(TimeSpan.FromMinutes(5));
        await checkedOnce.Task.WaitAsync(Wait, Ct);
        Assert.Equal([Changed("Work")], await UntilWorkHintAsync(w, socket));

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal([HiddenFrame, ClosedHidden], await DrainAsync(socket, Wait));
    }

    [Fact]
    public async Task Revealing_tells_the_sessions_sockets()
    {
        var w = await WorldAsync();
        var other = await w.Alice.SignInAgainAsync();
        using var socket = await OpenAsync(Api, w.Alice);
        using var elsewhere = await OpenAsync(Api, other);
        await ListedAsync(SubjectOf(w.Alice), 2);
        await ListedAsync(SubjectOf(other), 2);

        await RevealAsync(w.Alice);

        Assert.Equal(RevealedFrame, await ReceiveAsync(socket, Wait));
        // Another session of hers is told nothing.
        Assert.Equal([Changed("Work")], await UntilWorkHintAsync(w, elsewhere));
    }
}
