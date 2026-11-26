// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using Kumbu.Core.Search;

namespace Kumbu.Server.Api;

/// <summary>
/// The places for the calls that cost seconds of CPU on a request's thread (singleton): comparing stored vectors (the
/// graph's similarity, Similar notes, <c>sh similar</c>), the publication preview, publishing and updating. A few in all,
/// fewer than the cores, so that the thread pool keeps threads for every other request, sign-in and the health check
/// included; and <see cref="PerAccount"/> per account, so that one account's calls at once can't take them all. A call
/// waits up to <see cref="Wait"/> for its places, then is busy: 503 with <c>Retry-After</c>. All of an account's
/// credentials share its place; the dev token has its own.
/// </summary>
public sealed class HeavyCallGate(int perAccount = HeavyCallGate.PerAccount, int? inAll = null)
{
    /// <summary>How many of an account's calls run at once.</summary>
    public const int PerAccount = 1;

    /// <summary>How many calls run at once on the server: half the cores, and at least two, so one account never holds them all.</summary>
    public static int InAll => Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>How long a call waits for its places: long enough for a call ahead of it (well under a second), so that
    /// the web app's two calls for one note both get their turn. Settable for tests.</summary>
    internal TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _all = new(inAll ?? InAll);

    /// <summary>Per account, its share of the places. One per account that has made such a call, for the life of the process.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accounts = new(StringComparer.Ordinal);

    /// <summary>The places of a call of <paramref name="account"/>'s, given back when disposed of; null when they didn't
    /// come within <see cref="Wait"/>.</summary>
    public async Task<IDisposable?> TryStartAsync(string account, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var own = _accounts.GetOrAdd(account, _ => new SemaphoreSlim(perAccount));
        if (!await own.WaitAsync(Wait, ct)) return null;
        try
        {
            var left = Wait - Stopwatch.GetElapsedTime(started);
            if (await _all.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, ct)) return new Places(this, own);
        }
        catch
        {
            own.Release();
            throw;
        }

        own.Release();
        return null;
    }

    /// <summary>As <see cref="TryStartAsync"/>, busy (503 with <c>Retry-After</c>) when the places didn't come.</summary>
    public async Task<IDisposable> StartAsync(string account, CancellationToken ct) => await TryStartAsync(account, ct) ?? throw ApiErrors.Busy();

    /// <summary>The shell's way in, for the request's account.</summary>
    internal IHeavyCalls For(string account) => new AccountCalls(this, account);

    private sealed class AccountCalls(HeavyCallGate gate, string account) : IHeavyCalls
    {
        public Task<IDisposable?> TryStartAsync(CancellationToken ct) => gate.TryStartAsync(account, ct);
    }

    private sealed class Places(HeavyCallGate gate, SemaphoreSlim own) : IDisposable
    {
        private int _given;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _given, 1) != 0) return;
            gate._all.Release();
            own.Release();
        }
    }
}
