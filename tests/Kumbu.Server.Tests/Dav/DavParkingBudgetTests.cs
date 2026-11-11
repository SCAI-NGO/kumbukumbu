// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Dav;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Dav;

/// <summary>
/// Parked bodies are read within a server-wide budget, and a user's shares within her part of it (<see cref="DavParkingBudget"/>):
/// a PUT that finds no room waits for it rather than being refused. A server of their own, and alone, so that the
/// budget and the memory measured are theirs only.
/// </summary>
[Collection(nameof(DavParkingBudgetTests))]
public sealed class DavParkingBudgetTests(KumbuAppFactory app)
{
    private const int Mib = 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>A body made as it is sent, so the test holds no copy of it: <paramref name="first"/> bytes, then, once
    /// <paramref name="release"/> completes, the rest up to <paramref name="total"/>. Sent with its length if
    /// <paramref name="declare"/>, in chunks otherwise. It never throws: a test that gives up ends it short.</summary>
    private sealed class Held(long first, long total, bool declare, Task release) : HttpContent
    {
        private static readonly byte[] Chunk = new byte[64 * 1024];

        private readonly TaskCompletionSource _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the first part has been sent. The in-memory server takes a request's bytes as it reads
        /// them, a few KiB ahead, so a first part of a MiB or more has been read too: its room was taken.</summary>
        public Task Delivered => _delivered.Task;

        private static async Task SendAsync(Stream stream, long count)
        {
            while (count > 0)
            {
                var n = (int)Math.Min(Chunk.Length, count);
                await stream.WriteAsync(Chunk.AsMemory(0, n));
                count -= n;
            }

            await stream.FlushAsync();
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await SendAsync(stream, first);
            _delivered.TrySetResult();
            await release;
            await SendAsync(stream, total - first);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = total;
            return declare;
        }
    }

    /// <summary>A body that declares <paramref name="length"/> and ends at once, short of it: the length alone refuses it.</summary>
    private sealed class Declared(long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long declared)
        {
            declared = length;
            return true;
        }
    }

    private static HttpContent Small() => new ByteArrayContent([1, 2, 3]);

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
    }

    private static void AssertParked(HttpStatusCode status) => Assert.Contains(status, new[] { HttpStatusCode.Created, HttpStatusCode.NoContent });

    /// <summary>Waits until no more of <paramref name="bodies"/> are delivered for a while: those the budget let in.</summary>
    private static async Task<int> SettledAsync(IReadOnlyList<Held> bodies)
    {
        var delivered = -1;
        for (var stable = 0; stable < 4;)
        {
            await Task.Delay(500, Ct);
            var now = bodies.Count(b => b.Delivered.IsCompleted);
            stable = now == delivered && now > 0 ? stable + 1 : 0;
            delivered = now;
        }

        return delivered;
    }

    [Fact]
    public async Task Parked_bodies_being_received_hold_no_more_than_the_budget()
    {
        const int shares = 8, each = 2;
        const long size = 40 * Mib, half = 20 * Mib;
        var davs = new List<DavClient>();
        for (var i = 0; i < shares; i++) davs.Add(await DavClient.NewAsync(Api));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodies = Enumerable.Range(0, shares * each).Select(_ => new Held(half, size, declare: true, release.Task)).ToList();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        var puts = bodies.Select((b, i) => davs[i % shares].SendAsync(HttpMethod.Put, $"/dav/Work/._big{i}", b)).ToList();
        var delivered = await SettledAsync(bodies).WaitAsync(TimeSpan.FromSeconds(120), Ct);
        var held = GC.GetTotalMemory(forceFullCollection: true) - baseline;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{delivered} of {bodies.Count} parked PUTs of {size / Mib} MiB let in, {half / Mib} MiB each received: {held / Mib} MiB held; budget {DavRules.ParkingReadBudget / Mib} MiB");

        release.SetResult();
        foreach (var status in await Task.WhenAll(puts.Select(StatusAsync)).WaitAsync(TimeSpan.FromSeconds(120), Ct)) AssertParked(status);
        Assert.True(held < DavRules.ParkingReadBudget + 64 * Mib, $"{held / Mib} MiB held, over the budget of {DavRules.ParkingReadBudget / Mib} MiB");
        for (var i = 0; i < bodies.Count; i++)
        {
            using var removed = await davs[i % shares].SendAsync(HttpMethod.Delete, $"/dav/Work/._big{i}");
            bodies[i].Dispose();
        }
    }

    [Fact]
    public async Task A_parked_put_waits_for_room_rather_than_being_refused()
    {
        var dav = await DavClient.NewAsync(Api);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Without a length, a body takes all the room a share has.
        using var first = new Held(Mib, Mib + 1, declare: false, release.Task);
        var holding = dav.SendAsync(HttpMethod.Put, "/dav/Work/.first", first);
        await first.Delivered.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var waiting = dav.SendAsync(HttpMethod.Put, "/dav/Work/.second", Small());
        var answered = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(2), Ct));

        Assert.NotSame(waiting, answered);
        release.SetResult();
        AssertParked(await StatusAsync(holding).WaitAsync(TimeSpan.FromSeconds(30), Ct));
        AssertParked(await StatusAsync(waiting).WaitAsync(TimeSpan.FromSeconds(30), Ct));
    }

    [Fact]
    public async Task One_user_with_several_shares_cannot_hold_the_whole_budget()
    {
        var first = await DavClient.NewAsync(Api);
        var other = await DavClient.NewAsync(Api);
        // Enough shares and bodies without a length to take the whole budget, were a user allowed to; one body each.
        var count = (int)(DavRules.ParkingReadBudget / DavRules.ParkingCap) + 1;
        var shares = new List<DavClient> { first };
        for (var i = 1; i < count; i++) shares.Add(await DavClient.NewAsync(Api, owner: first.Owner, label: $"PC {i}"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodies = Enumerable.Range(0, count).Select(_ => new Held(Mib, Mib + 1, declare: false, release.Task)).ToList();
        var puts = bodies.Select((b, i) => shares[i].SendAsync(HttpMethod.Put, $"/dav/Work/.slow{i}", b)).ToList();
        await Task.WhenAny(bodies.Select(b => b.Delivered)).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await Task.Delay(500, Ct);

        try
        {
            using var put = await other.SendAsync(HttpMethod.Put, "/dav/Work/.DS_Store", Small()).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            AssertParked(put.StatusCode);
        }
        finally
        {
            release.TrySetResult();
        }

        foreach (var status in await Task.WhenAll(puts.Select(StatusAsync)).WaitAsync(TimeSpan.FromSeconds(60), Ct)) AssertParked(status);
        foreach (var b in bodies) b.Dispose();
    }

    [Fact]
    public async Task Parked_puts_one_after_another_all_succeed()
    {
        var dav = await DavClient.NewAsync(Api);
        for (var i = 0; i < 12; i++)
        {
            // In turn without a length, which takes the share's whole part of the budget, and with one.
            var body = i % 2 == 0 ? new DavClient.Unsized([1, 2, 3]) : Small();
            AssertParked(await StatusAsync(dav.SendAsync(HttpMethod.Put, "/dav/Work/" + Uri.EscapeDataString($".~lock.{i}#"), body)).WaitAsync(TimeSpan.FromSeconds(30), Ct));
            body.Dispose();
        }

        for (var i = 0; i < 12; i++)
        {
            using var got = await dav.SendAsync(HttpMethod.Get, "/dav/Work/" + Uri.EscapeDataString($".~lock.{i}#"));
            Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        }
    }

    [Fact]
    public async Task A_wait_given_up_leaves_its_place_and_takes_no_room()
    {
        var budget = new DavParkingBudget();
        var held = await budget.TakeAsync("a", DavRules.ParkingReadPerUser, Ct);
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var given = budget.TakeAsync("a", 1, giveUp.Token);
        var after = budget.TakeAsync("a", 1, Ct);

        await giveUp.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => given);
        Assert.False(after.IsCompleted);
        held.Dispose();
        (await after.WaitAsync(TimeSpan.FromSeconds(30), Ct)).Dispose();
        using var whole = await budget.TakeAsync("a", DavRules.ParkingReadPerUser, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    [Fact]
    public async Task A_user_held_back_by_her_part_is_not_overtaken_by_her_smaller_requests()
    {
        var budget = new DavParkingBudget();
        var held = await budget.TakeAsync("a", DavRules.ParkingReadPerUser / 2, Ct);
        var large = budget.TakeAsync("a", DavRules.ParkingReadPerUser, Ct);
        var small = budget.TakeAsync("a", 1, Ct);
        // Another user goes past both.
        (await budget.TakeAsync("b", DavRules.ParkingReadPerUser, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct)).Dispose();

        Assert.False(large.IsCompleted);
        Assert.False(small.IsCompleted);
        held.Dispose();
        var room = await large.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(small.IsCompleted);
        room.Dispose();
        (await small.WaitAsync(TimeSpan.FromSeconds(30), Ct)).Dispose();
    }

    [Fact]
    public async Task An_empty_parked_put_beside_a_large_one_is_parked()
    {
        // An empty body takes no room, so one that ends after a large one beside it gives back nothing.
        var budget = new DavParkingBudget();
        var none = await budget.TakeAsync("a", 0, Ct);
        var whole = await budget.TakeAsync("a", DavRules.ParkingReadPerUser, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        whole.Dispose();
        none.Dispose();
        (await budget.TakeAsync("a", DavRules.ParkingReadPerUser, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct)).Dispose();

        var dav = await DavClient.NewAsync(Api);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var large = new Held(Mib, Mib + 1, declare: false, release.Task);
        var largePut = dav.SendAsync(HttpMethod.Put, "/dav/Work/.large", large);
        await large.Delivered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        try
        {
            var empties = Enumerable.Range(0, 4).Select(i => StatusAsync(dav.SendAsync(HttpMethod.Put, $"/dav/Work/.empty{i}", new ByteArrayContent([]))));
            foreach (var status in await Task.WhenAll(empties).WaitAsync(TimeSpan.FromSeconds(30), Ct)) AssertParked(status);
        }
        finally
        {
            release.TrySetResult();
        }

        AssertParked(await StatusAsync(largePut).WaitAsync(TimeSpan.FromSeconds(30), Ct));
    }

    [Fact]
    public async Task A_parked_put_over_the_cap_is_refused_at_once_while_its_share_waits_for_room()
    {
        var dav = await DavClient.NewAsync(Api);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new Held(Mib, Mib + 1, declare: false, release.Task);
        var holding = dav.SendAsync(HttpMethod.Put, "/dav/Work/.first", first);
        await first.Delivered.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        try
        {
            using var large = new Declared(DavRules.ParkingCap + 1);
            using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.large", large).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, put.StatusCode);
        }
        finally
        {
            release.TrySetResult();
        }

        AssertParked(await StatusAsync(holding).WaitAsync(TimeSpan.FromSeconds(30), Ct));
    }
}

[CollectionDefinition(nameof(DavParkingBudgetTests), DisableParallelization = true)]
public sealed class DavParkingBudgetCollection : ICollectionFixture<KumbuAppFactory>;
