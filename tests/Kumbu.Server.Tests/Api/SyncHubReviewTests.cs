// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Sharing;
using Kumbu.Server.Sync;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Kumbu.Server.Tests.Api.SyncSockets;

namespace Kumbu.Server.Tests.Api;

/// <summary>
/// The sync hub fails closed (review of tasks 5.1–5.5): a ticket asked for before an access change opens the vault as
/// it is now, a check that can't be made closes the socket, one connection's presence is bounded, and a session ended
/// closes its sockets at once.
/// </summary>
[Collection("app")]
public sealed class SyncHubReviewTests(KumbuAppFactory app) : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);

    private WebApplicationFactory<Program>? _api;

    // A factory of its own: the fault seams are set on its hub only.
    private WebApplicationFactory<Program> Api => _api ??= app.PublicApi.WithWebHostBuilder(_ => { });

    private SyncHub Hub => Api.Services.GetRequiredService<SyncHub>();

    public async ValueTask DisposeAsync()
    {
        if (_api is not null) await _api.DisposeAsync();
    }

    /// <summary>Alice's "Gartenarbeit", shared with Bob as "garten" (her label "Bob") and Carol as "beete".</summary>
    private async Task<(RestClient Alice, RestClient Bob, RestClient Carol, string Bobs)> SharedAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        var bobs = await alice.ShareWithAsync(bob, "garten", MemberRole.Write, label: "Bob");
        await alice.ShareWithAsync(carol, "beete", MemberRole.Write, label: "Carol");
        return (alice, bob, carol, bobs);
    }

    [Fact]
    public async Task A_ticket_taken_before_the_revoke_opens_nothing_of_the_vault()
    {
        var (alice, bob, carol, membership) = await SharedAsync();
        using var carols = await OpenAsync(Api, carol);
        await EditingAsync(carols, "beete", "plan.md", on: true);
        // Carol's own line shows her announcement went through.
        Assert.NotNull(await PresenceAsync(carols, Wait));
        var ticket = await TicketAsync(bob);

        using (var revoked = await alice.SendAsync(HttpMethod.Post, $"/api/vaults/Gartenarbeit/memberships/{membership}/revoke"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }

        using var bobs = await ConnectAsync(Api, ticket);
        await alice.CreateAsync("plan.md", "x\n");
        // His own vault stays, so the socket does: neither Carol's presence nor the write reaches it.
        Assert.Empty(await DrainAsync(bobs, TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public async Task A_write_ticket_used_after_lowering_to_read_announces_nothing()
    {
        var (alice, bob, _, membership) = await SharedAsync();
        using var alices = await OpenAsync(Api, alice);
        var ticket = await TicketAsync(bob);
        using (var lowered = await alice.SendAsync(HttpMethod.Put, $"/api/vaults/Gartenarbeit/memberships/{membership}/role", new { role = "read" }))
        {
            Assert.Equal(HttpStatusCode.OK, lowered.StatusCode);
        }

        using var bobs = await ConnectAsync(Api, ticket);
        await EditingAsync(bobs, "garten", "plan.md", on: true);

        Assert.Null(await PresenceAsync(alices, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_check_that_fails_closes_the_socket_to_connect_again_and_ends_its_presence()
    {
        var (alice, bob, _, _) = await SharedAsync();
        using var alices = await OpenAsync(Api, alice);
        using var bobs = await OpenAsync(Api, bob);
        await EditingAsync(bobs, "garten", "plan.md", on: true);
        Assert.Equal(["Bob"], Editors((await PresenceAsync(alices, Wait))!));

        Hub.BeforeRecompute = s => s.User == bob.User.Id ? throw new TimeoutException("the database didn't answer") : Task.CompletedTask;
        try
        {
            await Hub.CheckAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Hub.BeforeRecompute = null;
        }

        Assert.Equal("close 1013", (await DrainAsync(bobs, Wait))[^1]);
        Assert.Empty(Editors((await PresenceAsync(alices, Wait))!));
    }

    [Fact]
    public async Task A_notification_that_fails_closes_the_socket_too()
    {
        var (alice, bob, _, membership) = await SharedAsync();
        using var bobs = await OpenAsync(Api, bob);
        Hub.BeforeRecompute = s => s.User == bob.User.Id ? throw new TimeoutException("the database didn't answer") : Task.CompletedTask;
        try
        {
            using var revoked = await alice.SendAsync(HttpMethod.Post, $"/api/vaults/Gartenarbeit/memberships/{membership}/revoke");
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }
        finally
        {
            Hub.BeforeRecompute = null;
        }

        Assert.Equal("close 1013", (await DrainAsync(bobs, Wait))[^1]);
    }

    [Fact]
    public async Task A_connection_is_shown_editing_at_most_8_notes()
    {
        var (alice, bob, _, _) = await SharedAsync();
        using var alices = await OpenAsync(Api, alice);
        using var bobs = await OpenAsync(Api, bob);

        for (var i = 1; i <= Presence.MaxPerConnection + 1; i++) await EditingAsync(bobs, "garten", $"n{i}.md", on: true);
        var shown = new List<string>();
        while (await PresenceAsync(alices, TimeSpan.FromSeconds(1)) is { } presence) shown.Add(presence["path"]!.GetValue<string>());
        Assert.Equal(8, Presence.MaxPerConnection);
        // The send loop takes the notes in no particular order; which ones it is is what counts.
        Assert.Equal([.. Enumerable.Range(1, 8).Select(i => $"n{i}.md")], shown.Order(StringComparer.Ordinal));

        // One ends, and a further one is taken.
        await EditingAsync(bobs, "garten", "n1.md", on: false);
        await EditingAsync(bobs, "garten", "n9.md", on: true);
        var after = new Dictionary<string, string[]>();
        while (await PresenceAsync(alices, TimeSpan.FromSeconds(1)) is { } presence) after[presence["path"]!.GetValue<string>()] = Editors(presence);
        Assert.Equal(["n1.md", "n9.md"], after.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(after["n1.md"]);
        Assert.Equal(["Bob"], after["n9.md"]);
    }

    [Fact]
    public async Task Signing_out_closes_the_tabs_socket_at_once()
    {
        var (_, bob, _, _) = await SharedAsync();
        using var bobs = await OpenAsync(Api, bob);

        using (var signedOut = await bob.SendAsync(HttpMethod.Delete, "/api/session"))
        {
            Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
        }

        // No check runs here: the sign-out told the hub.
        Assert.Equal("close 4401", (await DrainAsync(bobs, Wait))[^1]);
    }
}
