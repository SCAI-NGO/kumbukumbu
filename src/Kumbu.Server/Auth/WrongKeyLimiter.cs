// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace Kumbu.Server.Auth;

/// <summary>
/// Wrong bearer keys: at most <see cref="PermitLimit"/> per minute per client IP, in memory (one instance).
/// Only failures use up the budget; once it is used up every bearer from that IP is refused before it is
/// checked, so a guess that happens to be right can't tell itself apart from a wrong one.
/// </summary>
public sealed class WrongKeyLimiter : IDisposable
{
    public const int PermitLimit = 20;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    // Sliding, so that 20 failures at the end of one minute and 20 at the start of the next aren't 40 in a minute.
    // Idle partitions are dropped by the partitioned limiter itself, so memory stays bounded.
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(ip =>
        RateLimitPartition.GetSlidingWindowLimiter(ip, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = PermitLimit,
            Window = Window,
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    // Keys being checked right now, per IP. Checking an API key waits for MongoDB; each check holds one of the
    // IP's remaining permits meanwhile, so a burst sent at once can't all pass before any failure is counted.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _checking = new();

    /// <summary>
    /// Null if <paramref name="ip"/> may present a key: one of its permits is then held until
    /// <see cref="Failed"/> uses it up or <see cref="Release"/> gives it back. Else how long it has to wait.
    /// </summary>
    public TimeSpan? TryReserve(string ip)
    {
        var checking = _checking.AddOrUpdate(ip, 1, (_, n) => n + 1);
        if (checking <= (_limiter.GetStatistics(ip)?.CurrentAvailablePermits ?? PermitLimit)) return null;

        Release(ip);
        // Zero permits only asks whether any are left; it uses none up.
        using var probe = _limiter.AttemptAcquire(ip, 0);
        if (probe.IsAcquired) return TimeSpan.FromSeconds(1); // all that are left are held by checks still running
        return probe.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : Window;
    }

    /// <summary>The reserved check failed: its permit is used up.</summary>
    public void Failed(string ip)
    {
        // Counted before the reservation goes, so the permit is never free in between.
        _limiter.AttemptAcquire(ip, 1).Dispose();
        Release(ip);
    }

    /// <summary>The reserved check succeeded (or never finished): its permit is given back.</summary>
    public void Release(string ip)
    {
        if (_checking.AddOrUpdate(ip, 0, (_, n) => n - 1) <= 0)
        {
            // Removes the entry only if it is still zero, so a reservation made meanwhile isn't lost.
            _checking.TryRemove(new KeyValuePair<string, int>(ip, 0));
        }
    }

    /// <summary>
    /// The partition of a client address. IPv6 by its /64: one host usually gets a whole /64, and counting
    /// each of its addresses apart would let it try 2^64 times as often.
    /// </summary>
    public static string Partition(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    public void Dispose() => _limiter.Dispose();
}
