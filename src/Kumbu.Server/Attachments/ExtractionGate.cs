// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;

namespace Kumbu.Server.Attachments;

/// <summary>
/// An account's places at the sidecar (singleton). One <c>pdfimages</c> call per account at a time: the sidecar extracts
/// in one worker behind a short queue. And two of the account's calls to the sidecar at a time, whatever they draw; a
/// further one waits for a place, up to <c>renderWait</c>, and is then busy: the sidecar's pool is shared, and one
/// account's agents calling in parallel would otherwise keep everyone else's waiting. All of an account's credentials
/// share its places; the dev token has its own.
/// </summary>
public sealed class ExtractionGate(TimeSpan? renderWait = null)
{
    /// <summary>How many of an account's calls the sidecar works on at once.</summary>
    public const int RenderPlaces = 2;

    private readonly TimeSpan _renderWait = renderWait ?? TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _renders = new(StringComparer.Ordinal);

    /// <summary>The account's place, held until disposed of; null while another call holds it.</summary>
    public IDisposable? TryStart(string account) => _running.TryAdd(account, 0) ? new Place(this, account) : null;

    /// <summary>
    /// <paramref name="call"/> run in one of the account's render places; a <see cref="SidecarException.Busy"/> when
    /// none is free in time. An account's semaphore is kept once made: one per account that ever drew, a few bytes each.
    /// </summary>
    public async Task<T> InRenderPlaceAsync<T>(string account, Func<Task<T>> call, CancellationToken ct)
    {
        var places = _renders.GetOrAdd(account, _ => new SemaphoreSlim(RenderPlaces, RenderPlaces));
        if (!await places.WaitAsync(_renderWait, ct))
            throw new SidecarException(SidecarException.Busy, "This account's earlier pictures are still being drawn.");
        try
        {
            return await call();
        }
        finally
        {
            places.Release();
        }
    }

    private sealed class Place(ExtractionGate gate, string account) : IDisposable
    {
        private int _given;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _given, 1) == 0) gate._running.TryRemove(account, out _);
        }
    }
}
