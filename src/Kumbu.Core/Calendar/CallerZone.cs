// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Calendar;

/// <summary>
/// The zone of whoever asks (spec §3), so that "today" and "overdue" mean the same for a person and her agent: her setting, else
/// the server's. A shell without a caller has the server's (<see cref="ServerZone"/>).
/// </summary>
public interface ICallerZone
{
    Task<TimeZoneInfo> ZoneAsync(CancellationToken ct);
}

/// <summary>One zone for every caller: the server's <c>TZ</c>, else UTC.</summary>
public sealed class ServerZone(TimeZoneInfo zone) : ICallerZone
{
    public static readonly ServerZone Instance = new(Zones.Server());

    public Task<TimeZoneInfo> ZoneAsync(CancellationToken ct) => Task.FromResult(zone);
}
