// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;

namespace Kumbu.Server.Attachments;

/// <summary>
/// One <c>pdfimages</c> call per account at a time (singleton): the sidecar extracts in one worker behind a short queue,
/// and one account's agents calling in parallel would otherwise keep everyone else's busy. All of an account's
/// credentials share its place; the dev token has one of its own.
/// </summary>
public sealed class ExtractionGate
{
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);

    /// <summary>The account's place, held until disposed of; null while another call holds it.</summary>
    public IDisposable? TryStart(string account) => _running.TryAdd(account, 0) ? new Place(this, account) : null;

    private sealed class Place(ExtractionGate gate, string account) : IDisposable
    {
        private int _given;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _given, 1) == 0) gate._running.TryRemove(account, out _);
        }
    }
}
