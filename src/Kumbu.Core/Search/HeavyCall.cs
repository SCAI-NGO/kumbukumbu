// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Search;

/// <summary>Where a call that costs seconds of CPU takes its place: the server keeps a few per account and in all, so
/// that such calls can't hold every thread while other requests wait.</summary>
public interface IHeavyCalls
{
    /// <summary>The caller's place, held until disposed of; null while none is free.</summary>
    Task<IDisposable?> TryStartAsync(CancellationToken ct);
}

/// <summary>
/// The token a call that costs seconds of CPU on its thread runs under (comparing stored vectors): the caller's, so that
/// the work ends when its client leaves, and ended at <paramref name="limit"/> too, so that no call holds a thread for
/// long. A call that ran out of time is answered as busy: alone it takes well under a second.
/// </summary>
public sealed class HeavyCall(CancellationToken caller, TimeSpan limit, TimeProvider time) : IDisposable
{
    /// <summary>How long one call may take: several times what the largest call the bounds allow takes alone.</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _limit = new(limit, time);
    private CancellationTokenSource? _linked;

    public CancellationToken Token => (_linked ??= CancellationTokenSource.CreateLinkedTokenSource(caller, _limit.Token)).Token;

    /// <summary>Whether the call ran out of time, rather than its caller leaving.</summary>
    public bool TimedOut => _limit.IsCancellationRequested && !caller.IsCancellationRequested;

    public void Dispose()
    {
        _linked?.Dispose();
        _limit.Dispose();
    }
}
