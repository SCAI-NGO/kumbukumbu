// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Threading.RateLimiting;
using Kumbu.Server.Auth;

namespace Kumbu.Server.OAuth;

/// <summary>
/// The OAuth endpoints' rate limits (Ruling 13), in memory like <see cref="WrongKeyLimiter"/>: registrations per
/// network, failed token requests per client id. Failures are not counted per address because Anthropic's
/// connectors all come from one egress range (Ruling 12).
/// </summary>
public sealed class OAuthLimits : IDisposable
{
    public const int RegistrationsPerHour = 30;

    /// <summary>A client id longer than this is cut, so a caller can't fill the memory with long keys.</summary>
    public const int MaxClientIdKey = 256;

    private static readonly TimeSpan RegistrationWindow = TimeSpan.FromHours(1);

    // Every attempt counts, successful or not: registering is what costs a row.
    private readonly PartitionedRateLimiter<string> _registrations = PartitionedRateLimiter.Create<string, string>(partition =>
        RateLimitPartition.GetSlidingWindowLimiter(partition, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = RegistrationsPerHour,
            Window = RegistrationWindow,
            SegmentsPerWindow = 12,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    // 20 failures a minute, with the reserve/fail/release protocol of wrong API keys: same budget, other key.
    private readonly WrongKeyLimiter _tokens = new();

    /// <summary>Null if this caller may register; else how long it has to wait.</summary>
    public TimeSpan? Registration(HttpContext http)
    {
        using var lease = _registrations.AttemptAcquire(WrongKeyLimiter.Partition(http.Connection.RemoteIpAddress));
        if (lease.IsAcquired) return null;
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : RegistrationWindow;
    }

    /// <summary>Null if a token request for the client may be checked (a permit is then held until <see cref="TokenFailed"/> or <see cref="TokenRelease"/>); else how long to wait.</summary>
    public TimeSpan? TokenReserve(string clientId) => _tokens.TryReserve(Key(clientId));

    public void TokenFailed(string clientId) => _tokens.Failed(Key(clientId));

    public void TokenRelease(string clientId) => _tokens.Release(Key(clientId));

    private static string Key(string clientId) => clientId.Length <= MaxClientIdKey ? clientId : clientId[..MaxClientIdKey];

    public void Dispose()
    {
        _registrations.Dispose();
        _tokens.Dispose();
    }
}
