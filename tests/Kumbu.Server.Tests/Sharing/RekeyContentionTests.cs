// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// A member who renames her alias in a loop while the owner re-keys the vault (spec §1.3): the renames are refused
/// before they write, so the finish commits at its first try, and nobody else's vault waits for it meanwhile.
/// </summary>
[Collection("app")]
public sealed class RekeyContentionTests(KumbuAppFactory app)
{
    /// <summary>The longest any one wait of this test may take; a wait that needs it has failed.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private const int Loops = 8;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    private static async Task<TimeSpan> TimedReadAsync(RestClient user, string path)
    {
        var watch = Stopwatch.StartNew();
        using var read = await user.GetAsync(path);
        watch.Stop();
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        return watch.Elapsed;
    }

    [Fact]
    public async Task A_rename_loop_during_a_run_is_refused_and_the_finish_commits()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "# Beete\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var bobs = await alice.ShareWithAsync(bob, "garten", MemberRole.Read);
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        await carol.CreateAsync("liste.md", "# Liste\n");
        const string carolsNote = "/api/notes/Privat/liste.md";

        // Carol's read without the loops, warmed up; the slowest of a few.
        await TimedReadAsync(carol, carolsNote);
        var baseline = TimeSpan.Zero;
        for (var i = 0; i < 5; i++) baseline = TimeSpan.FromTicks(Math.Max(baseline.Ticks, (await TimedReadAsync(carol, carolsNote)).Ticks));

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var insideFinish = 0;
        Catalog.RekeyFaultAt = async p =>
        {
            // Inside the finish's transaction: a conflict, or a retry of the driver's, runs it again.
            if (p == "inside-finish") Interlocked.Increment(ref insideFinish);
            if (p == "before-finish" && reached.TrySetResult()) await release.Task;
        };

        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var answers = new ConcurrentQueue<(bool Released, HttpStatusCode Status)>();
        Task<HttpResponseMessage>? rekey = null;
        var loops = new List<Task>();

        async Task LoopAsync(int loop)
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                using var renamed = await bob.SendAsync(HttpMethod.Put, $"/api/memberships/{bobs}/alias", new { alias = $"garten-{loop}-{i}" });
                // Read after the answer: an answer seen before the flag was set came while the run was held.
                answers.Enqueue((released.Task.IsCompleted, renamed.StatusCode));
            }
        }

        try
        {
            rekey = alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/rekey", new { password = RestClient.Password });
            await Task.WhenAny(reached.Task, rekey).WaitAsync(Bound, Ct);
            Assert.True(reached.Task.IsCompleted, "The run answered before its finish.");

            for (var loop = 0; loop < Loops; loop++)
            {
                var n = loop;
                loops.Add(Task.Run(() => LoopAsync(n), Ct));
            }

            // Past the rate limit, so that both refusals are seen while the run is held.
            var waiting = Stopwatch.StartNew();
            while (answers.Count < 50 && !loops.Any(l => l.IsFaulted))
            {
                Assert.True(waiting.Elapsed < Bound, "The rename loops answered too slowly.");
                await Task.Delay(10, Ct);
            }

            Assert.False(loops.Any(l => l.IsFaulted), "A rename loop failed.");
            var held = answers.ToArray();
            var others = held.Select(a => a.Status).Where(s => s is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)).Distinct().ToArray();
            Assert.True(others.Length == 0, $"While the run was held, renames answered {string.Join(", ", others)}.");
            Assert.Contains(held, a => a.Status == HttpStatusCode.ServiceUnavailable);
            Assert.Contains(held, a => a.Status == HttpStatusCode.TooManyRequests);

            // Released with the loops still going, and kept going until the re-key has answered; Carol reads meanwhile.
            released.SetResult();
            release.SetResult();
            var during = TimeSpan.Zero;
            do
            {
                during = TimeSpan.FromTicks(Math.Max(during.Ticks, (await TimedReadAsync(carol, carolsNote).WaitAsync(Bound, Ct)).Ticks));
            }
            while (!rekey.IsCompleted);

            using var rekeyed = await rekey.WaitAsync(Bound, Ct);
            stop.Cancel();
            await Task.WhenAll(loops).WaitAsync(Bound, Ct);

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Carol's read: {baseline.TotalMilliseconds:0} ms without the loops, {during.TotalMilliseconds:0} ms during the release; {answers.Count} renames, {held.Length} while held.");
            Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
            Assert.Equal(1, Volatile.Read(ref insideFinish));
            // A rename after the commit may go through; none of them is the server's fault.
            Assert.DoesNotContain(answers, a => a.Status == HttpStatusCode.InternalServerError);
            Assert.True(during <= baseline * 2 + TimeSpan.FromSeconds(1), $"Carol's read took {during.TotalMilliseconds:0} ms during the release, {baseline.TotalMilliseconds:0} ms without the loops.");
        }
        finally
        {
            // Loops first, so a run released by a failed test doesn't meet them.
            stop.Cancel();
            await Settled(Task.WhenAll(loops));
            release.TrySetResult();
            if (rekey is not null) await Settled(rekey);
            Catalog.RekeyFaultAt = null;
        }

        // Bob still reads the vault, under whatever alias the last rename left.
        using var list = await bob.GetAsync("/api/vaults");
        var alias = (await RestClient.JsonAsync(list)).AsArray().Single(v => v!["membership"]?.GetValue<string>() == bobs)!["name"]!.GetValue<string>();
        using var read = await bob.GetAsync($"/api/notes/{alias}/plan.md");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("# Beete\n", (await RestClient.JsonAsync(read))["content"]!.GetValue<string>());
    }

    /// <summary>Waits, bounded, for a task the test has given up on; what it ends with doesn't matter any more.</summary>
    private static async Task Settled(Task task)
    {
        try
        {
            await task.WaitAsync(Bound, Ct);
        }
        catch (Exception)
        {
            // The test's own assertion, if any, is the failure to report.
        }
    }
}
