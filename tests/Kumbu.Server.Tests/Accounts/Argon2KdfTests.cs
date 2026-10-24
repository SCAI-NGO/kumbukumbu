// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;

namespace Kumbu.Server.Tests.Accounts;

public sealed class Argon2KdfTests
{
    [Fact]
    public async Task More_derivations_than_the_gate_allows_wait_and_never_run_more_than_it_at_once()
    {
        const int limit = 2;
        using var gate = new SemaphoreSlim(limit, limit);
        // Costly enough (8 MiB, several passes) that runs started together overlap.
        var kdf = new Argon2Kdf(new Argon2Parameters(MemoryKiB: 8 * 1024, Iterations: 4, Parallelism: 1), gate);
        var salt = new byte[Argon2Kdf.SaltBytes];

        var keys = await Task.WhenAll(Enumerable.Range(0, 3 * limit + 1)
            .Select(_ => Task.Run(() => kdf.DeriveAsync("correct horse battery staple", salt, TestContext.Current.CancellationToken))));

        Assert.Equal(3 * limit + 1, kdf.Runs);
        Assert.InRange(kdf.PeakConcurrency, 1, limit);
        Assert.Equal(limit, gate.CurrentCount);
        Assert.All(keys, k => Assert.Equal(keys[0], k));
    }

    [Fact]
    public void Every_kdf_of_the_process_shares_one_gate_of_a_run_per_core()
    {
        Assert.Same(new Argon2Kdf().Gate, new Argon2Kdf(new Argon2Parameters(1024, 1, 1)).Gate);
        Assert.Equal(Math.Max(1, Environment.ProcessorCount), Argon2Kdf.DefaultConcurrency);
    }
}
