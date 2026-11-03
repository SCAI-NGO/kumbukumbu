// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using Kumbu.Core.Crypto;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// A re-keying's start and finish hold <c>_loading</c> only around the state they change, not across their transactions
/// (spec §7): another vault opens meanwhile, a load of the same vault waits for the transition and comes back under the
/// right key, and whatever ends a transition, nothing waits for it for ever.
/// </summary>
[Collection("app")]
public sealed class RekeyTransitionTests(KumbuAppFactory app)
{
    /// <summary>The longest any one wait of this test may take; a wait that needs it has failed.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>What "opens meanwhile" allows: a stalled transaction lasts until the test lets it go, far longer.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(5);

    /// <summary>How long a request that waits for a transition is watched staying unanswered.</summary>
    private static readonly TimeSpan Watch = TimeSpan.FromMilliseconds(500);

    private const string Plan = "# Beete\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    private async Task<(RestClient Alice, string Id)> AliceAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", Plan);
        return (alice, await alice.VaultIdAsync());
    }

    /// <summary>Dave, with no slot on Alice's vault.</summary>
    private async Task<RestClient> DaveAsync()
    {
        var dave = await RestClient.NewAsync(Api, vault: "Work");
        await dave.CreateAsync("liste.md", "# Liste\n");
        return dave;
    }

    private static byte[] KeyIdOf(byte[] vaultKey) => VaultCrypto.Derive(vaultKey, "kumbu/v1/key-id", []);

    [Fact]
    public Task Another_vault_opens_during_a_stalled_finish() => AnotherVaultOpensAsync("inside-finish");

    [Fact]
    public Task Another_vault_opens_during_a_stalled_start() => AnotherVaultOpensAsync("inside-start");

    private async Task AnotherVaultOpensAsync(string point)
    {
        var (alice, id) = await AliceAsync();
        var dave = await DaveAsync();
        var hold = new Hold().At(point);
        var run = Run(alice, id, hold);
        try
        {
            await ReachedAsync(hold, point, run);
            using var read = await dave.GetAsync(dave.Url("liste.md")).WaitAsync(Quick, Ct);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            hold.Release(point);
            await run.WaitAsync(Bound, Ct);
        }
        finally
        {
            await EndAsync(hold, run, id);
        }
    }

    [Fact]
    public async Task A_load_of_the_same_vault_waits_for_the_finish_and_gets_the_new_key()
    {
        var (alice, id) = await AliceAsync();
        var old = await alice.VaultKeyAsync();
        var hold = new Hold().At("inside-finish");
        var run = Run(alice, id, hold);
        try
        {
            await ReachedAsync(hold, "inside-finish", run);
            // Her slot read gives K still: the finish hasn't committed.
            var read = alice.GetAsync(alice.Url("plan.md"));
            await Task.Delay(Watch, Ct);
            Assert.False(read.IsCompleted, "Alice's read was answered during the finish.");

            hold.Release("inside-finish");
            await run.WaitAsync(Bound, Ct);
            using var response = await read.WaitAsync(Bound, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Plan, (await RestClient.JsonAsync(response))["content"]!.GetValue<string>());

            var current = KeyIdOf(await alice.VaultKeyAsync());
            Assert.NotEqual(KeyIdOf(old), current);
            Assert.Equal(current, Catalog.Current(id)!.KeyId);
        }
        finally
        {
            await EndAsync(hold, run, id);
        }
    }

    [Fact]
    public async Task A_load_of_the_same_vault_during_a_start_serves_no_old_working_set()
    {
        var (alice, id) = await AliceAsync();
        // Her write loaded it, under K.
        Assert.True(Catalog.IsInWorkingSet(id));
        var hold = new Hold().At("inside-start").At("after-start");
        var run = Run(alice, id, hold);
        try
        {
            await ReachedAsync(hold, "inside-start", run);
            Assert.False(Catalog.IsInWorkingSet(id));
            // The vault document read now would still be outside its period: the start hasn't committed.
            var read = alice.GetAsync(alice.Url("plan.md"));
            await Task.Delay(Watch, Ct);
            Assert.False(read.IsCompleted, "Alice's read was answered during the start.");
            Assert.False(Catalog.IsInWorkingSet(id));

            hold.Release("inside-start");
            await ReachedAsync(hold, "after-start", run);
            using var response = await read.WaitAsync(Bound, Ct);
            // A placeholder, "being re-encrypted": never the working set made with K.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.False(Catalog.IsInWorkingSet(id));

            hold.Release("after-start");
            await run.WaitAsync(Bound, Ct);
        }
        finally
        {
            await EndAsync(hold, run, id);
        }
    }

    [Fact]
    public Task A_failed_start_completes_the_transition() => FailedTransitionAsync("inside-start");

    [Fact]
    public Task A_failed_finish_completes_the_transition() => FailedTransitionAsync("inside-finish");

    private async Task FailedTransitionAsync(string point)
    {
        var (alice, id) = await AliceAsync();
        var dave = await DaveAsync();
        var hold = new Hold().At(point, () => throw new InvalidOperationException($"a failure at {point}"));
        var run = Run(alice, id, hold);
        try
        {
            await ReachedAsync(hold, point, run);
            var read = alice.GetAsync(alice.Url("plan.md"));
            await Task.Delay(Watch, Ct);
            Assert.False(read.IsCompleted, "Alice's read was answered during the transaction.");

            hold.Release(point);
            await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(Bound, Ct));
            await AnsweredAsync(read);
            await AnsweredAsync(alice.GetAsync(alice.Url("plan.md")));
            using var daves = await dave.GetAsync(dave.Url("liste.md")).WaitAsync(Bound, Ct);
            Assert.Equal(HttpStatusCode.OK, daves.StatusCode);
        }
        finally
        {
            await EndAsync(hold, run, id);
        }
    }

    [Theory]
    [InlineData("inside-start")]
    [InlineData("inside-finish")]
    public async Task A_run_cancelled_in_its_transaction_completes_the_transition(string point)
    {
        var (alice, id) = await AliceAsync();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var hold = new Hold().At(point, cancel.Cancel);
        var run = Run(alice, id, hold, cancel.Token);
        try
        {
            await ReachedAsync(hold, point, run);
            var read = alice.GetAsync(alice.Url("plan.md"));
            await Task.Delay(Watch, Ct);
            Assert.False(read.IsCompleted, "Alice's read was answered during the transaction.");

            hold.Release(point);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound, Ct));
            await AnsweredAsync(read);
            await AnsweredAsync(alice.GetAsync(alice.Url("plan.md")));
        }
        finally
        {
            await EndAsync(hold, run, id);
        }
    }

    [Fact]
    public async Task A_finish_retried_after_changed_memberships_completes()
    {
        var (alice, id) = await AliceAsync();
        var hold = new Hold().At("inside-finish", () => throw new MembershipsChangedException());
        var run = Run(alice, id, hold);
        try
        {
            await ReachedAsync(hold, "inside-finish", run);
            var read = alice.GetAsync(alice.Url("plan.md"));
            await Task.Delay(Watch, Ct);
            Assert.False(read.IsCompleted, "Alice's read was answered during the finish.");

            hold.Release("inside-finish");
            await run.WaitAsync(Bound, Ct);
            // Between the two attempts it may have met the vault still in its period.
            await AnsweredAsync(read);

            // Loaded again, under K′: right after a finish the working set holds nothing of it.
            Assert.Equal(Plan, (await alice.ReadAsync("plan.md"))["content"]!.GetValue<string>());
            Assert.Equal(KeyIdOf(await alice.VaultKeyAsync()), Catalog.Current(id)!.KeyId);
        }
        finally
        {
            await EndAsync(hold, run, id);
        }
    }

    [Fact]
    public async Task A_cancelled_caller_leaves_no_transition_behind()
    {
        var (alice, id) = await AliceAsync();
        var dave = await DaveAsync();
        var hold = new Hold().At("inside-finish");
        var run = Run(alice, id, hold);
        try
        {
            await ReachedAsync(hold, "inside-finish", run);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var open = Catalog.OpenForUserAsync(alice.User.Id, alice.UserKey, cancel.Token);
            await Task.Delay(Watch, Ct);
            Assert.False(open.IsCompleted, "Alice's vaults opened during the finish.");
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open.WaitAsync(Bound, Ct));

            // The cancelled wait took nothing with it: another vault opens, and the finish goes on.
            using (var daves = await dave.GetAsync(dave.Url("liste.md")).WaitAsync(Quick, Ct)) Assert.Equal(HttpStatusCode.OK, daves.StatusCode);
            hold.Release("inside-finish");
            await run.WaitAsync(Bound, Ct);
            Assert.Equal(Plan, (await alice.ReadAsync("plan.md").WaitAsync(Bound, Ct))["content"]!.GetValue<string>());
        }
        finally
        {
            await EndAsync(hold, run, id);
        }
    }

    [Fact]
    public async Task A_rename_during_a_run_answers_503_and_a_deletion_completes_after_it()
    {
        var (alice, id) = await AliceAsync();
        // Before the finish, not inside it: a request would wait for the transition in its load before reaching either.
        var hold = new Hold().At("before-finish");
        var run = Run(alice, id, hold);
        try
        {
            await ReachedAsync(hold, "before-finish", run);
            using (var renamed = await alice.SendAsync(HttpMethod.Put, "/api/vaults/Gartenarbeit/name", new { name = "Garten" }).WaitAsync(Bound, Ct))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, renamed.StatusCode);
            }

            var deletion = alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/delete", new { password = RestClient.Password, confirm = "Gartenarbeit" });
            await Task.Delay(Watch, Ct);
            Assert.False(deletion.IsCompleted, "The deletion was answered while the run held the vault.");

            hold.Release("before-finish");
            await run.WaitAsync(Bound, Ct);
            using (var deleted = await deletion.WaitAsync(Bound, Ct)) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            using var gone = await alice.GetAsync(alice.Url("plan.md")).WaitAsync(Bound, Ct);
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
        finally
        {
            await EndAsync(hold, run, id);
        }
    }

    private Task<RekeySummary> Run(RestClient alice, string id, Hold hold, CancellationToken? ct = null)
    {
        Catalog.RekeyFaultAt = hold.Hook(Catalog, id);
        return Task.Run(() => Catalog.RekeyAsync(id, alice.User.Id, alice.UserKey, ct ?? Ct), CancellationToken.None);
    }

    private static async Task ReachedAsync(Hold hold, string point, Task run)
    {
        await Task.WhenAny(hold.Reached(point), run).WaitAsync(Bound, Ct);
        // A run that failed before the point reports its own failure.
        if (!hold.Reached(point).IsCompleted) await run;
        Assert.True(hold.Reached(point).IsCompleted, $"The run ended before {point}.");
    }

    /// <summary>After a failed run a read may meet the vault in its two-key period, or the runner's resumed run's result:
    /// either answer is right, only a hang is wrong.</summary>
    private static async Task AnsweredAsync(Task<HttpResponseMessage> read)
    {
        using var response = await read.WaitAsync(Bound, Ct);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable });
    }

    /// <summary>Releases the run and waits for it, bounded; and for a run the runner resumed after a read met the vault in
    /// its period, so it doesn't overlap the next test.</summary>
    private async Task EndAsync(Hold hold, Task run, string id)
    {
        hold.ReleaseAll();
        try
        {
            await run.WaitAsync(Bound, Ct);
        }
        catch (Exception)
        {
            // The test's own assertion, if any, is the failure to report.
        }

        var waiting = Stopwatch.StartNew();
        while (Catalog.IsRekeying(id) && waiting.Elapsed < Bound) await Task.Delay(50, CancellationToken.None);
        Catalog.RekeyFaultAt = null;
    }

    /// <summary>
    /// A run of one vault held at each of some points, the first time it gets there, until released; then the point's
    /// action, if any, runs there (a throw is a failure at that point). Other tests' vaults share the fixture, so the
    /// hook acts only while this vault is held by a run; a retry of the driver's passes a point already held.
    /// </summary>
    private sealed class Hold
    {
        private readonly Dictionary<string, (TaskCompletionSource Reached, TaskCompletionSource Release, Action? Then)> _points = new(StringComparer.Ordinal);

        public Hold At(string point, Action? then = null)
        {
            _points[point] = (new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously), then);
            return this;
        }

        public Task Reached(string point) => _points[point].Reached.Task;

        public void Release(string point) => _points[point].Release.TrySetResult();

        public void ReleaseAll()
        {
            foreach (var point in _points.Values) point.Release.TrySetResult();
        }

        public Func<string, Task> Hook(VaultCatalog catalog, string id) => async point =>
        {
            if (!_points.TryGetValue(point, out var hold) || !catalog.IsRekeying(id) || !hold.Reached.TrySetResult()) return;
            await hold.Release.Task;
            hold.Then?.Invoke();
        };
    }
}
