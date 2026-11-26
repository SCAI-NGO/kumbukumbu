// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Api;

/// <summary>The places for the calls that cost seconds of CPU: per account and in all, a busy answer when none is free.</summary>
[Collection("app")]
public sealed class HeavyCallGateTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    [Fact]
    public async Task An_account_has_its_own_place_and_the_server_a_few_in_all()
    {
        var gate = new HeavyCallGate(perAccount: 1, inAll: 2) { Wait = TimeSpan.Zero };

        using var alices = await gate.TryStartAsync("alice", Ct);
        Assert.NotNull(alices);
        Assert.Null(await gate.TryStartAsync("alice", Ct));
        using var bobs = await gate.TryStartAsync("bob", Ct);
        Assert.NotNull(bobs);
        Assert.Null(await gate.TryStartAsync("carol", Ct));

        alices!.Dispose();
        alices.Dispose();
        using var again = await gate.TryStartAsync("carol", Ct);
        Assert.NotNull(again);
        Assert.Null(await gate.TryStartAsync("dana", Ct));
    }

    [Fact]
    public void The_server_keeps_fewer_places_than_cores_and_more_than_one_account_takes()
    {
        Assert.InRange(HeavyCallGate.InAll, 2, Math.Max(2, Environment.ProcessorCount - 1));
        Assert.True(HeavyCallGate.PerAccount < HeavyCallGate.InAll);
    }

    /// <summary>Runs <paramref name="check"/> while Alice's one place is taken, without waiting for it.</summary>
    private async Task WhileTakenAsync(RestClient alice, Func<Task> check)
    {
        var gate = Api.Services.GetRequiredService<HeavyCallGate>();
        var wait = gate.Wait;
        gate.Wait = TimeSpan.Zero;
        try
        {
            using var held = await gate.TryStartAsync(alice.User.Id, Ct);
            Assert.NotNull(held);
            await check();
        }
        finally
        {
            gate.Wait = wait;
        }
    }

    private static async Task BusyAsync(RestClient user, string url)
    {
        using var response = await user.GetAsync(url);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
        Assert.Equal("The server is busy; try again in a moment.", (await RestClient.JsonAsync(response))["error"]!.GetValue<string>());
    }

    private static async Task OkAsync(RestClient user, string url)
    {
        using var response = await user.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    [Fact]
    public async Task Similar_notes_and_the_graphs_similarity_are_busy_while_the_accounts_place_is_taken()
    {
        var alice = await RestClient.NewAsync(Api, "Home");
        var bob = await RestClient.NewAsync(Api, "Work");
        await alice.CreateAsync("pump.md", "# Pump\n\nThe pump moves water. See [[pipes]].\n");
        await alice.CreateAsync("pipes.md", "# Pipes\n\nWater moves through the pipes.\n");
        await bob.CreateAsync("pump.md", "# Pump\n\nThe pump moves water. See [[pipes]].\n");
        await bob.CreateAsync("pipes.md", "# Pipes\n\nWater moves through the pipes.\n");
        await app.IndexedAsync();
        static string Similar(string vault) => $"/api/similar/{vault}/pump.md";
        static string Graph(string vault) => "/api/graph/similarity?path=" + Uri.EscapeDataString($"/{vault}/pump.md");

        await WhileTakenAsync(alice, async () =>
        {
            await BusyAsync(alice, Similar("Home"));
            await BusyAsync(alice, Graph("Home"));
            // Bob's calls take a place of his own.
            await OkAsync(bob, Similar("Work"));
            await OkAsync(bob, Graph("Work"));
        });

        await OkAsync(alice, Similar("Home"));
        await OkAsync(alice, Graph("Home"));
    }

    [Fact]
    public async Task The_publication_preview_is_busy_while_the_accounts_place_is_taken()
    {
        var alice = await RestClient.NewAsync(Api, "Home");
        await alice.CreateAsync("plan.md", "# Plan\n\nSow in March.\n");

        await WhileTakenAsync(alice, () => BusyAsync(alice, "/api/publication-previews/Home/plan.md"));

        await OkAsync(alice, "/api/publication-previews/Home/plan.md");
    }

    [Fact]
    public async Task Sh_similar_says_the_server_is_busy_while_the_accounts_place_is_taken()
    {
        var alice = await RestClient.NewAsync(Api, "Home");
        await alice.CreateAsync("pump.md", "# Pump\n\nThe pump moves water.\n");
        await app.IndexedAsync();

        string? answer = null;
        await WhileTakenAsync(alice, async () =>
        {
            using var response = await alice.SendAsync(HttpMethod.Post, "/api/console", new { call = "sh", command = "similar /Home/pump.md" });
            answer = (await response.Content.ReadAsStringAsync(Ct));
        });

        Assert.Contains("similar: the server is busy comparing notes; try again in a moment", answer);
    }
}
