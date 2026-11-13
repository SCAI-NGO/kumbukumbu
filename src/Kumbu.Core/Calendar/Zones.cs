// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Calendar;

/// <summary>IANA time zones and local times (spec §3). Inside, the calendar computes in UTC.</summary>
public static class Zones
{
    /// <summary>The zone with the IANA id <paramref name="iana"/>; <c>null</c> when it is empty, longer than 64 characters or unknown.</summary>
    public static TimeZoneInfo? Find(string? iana)
    {
        // Only the characters of IANA ids, so that an id from a note never reaches the zone lookup as a path.
        if (string.IsNullOrEmpty(iana) || iana.Length > 64 || !iana.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+'))
            return null;
        return TimeZoneInfo.TryFindSystemTimeZoneById(iana, out var zone) ? zone : null;
    }

    /// <summary>
    /// The instant of a local time in <paramref name="zone"/>. A time that does not exist (in the spring gap) is read with the offset
    /// before the gap, so 02:30 becomes 03:30 when the clocks go forward an hour; a time that exists twice (in the autumn overlap) is the
    /// earlier instant.
    /// </summary>
    public static DateTime ToUtc(DateOnly day, TimeOnly clock, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(clock, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(local))
            return DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc);
        if (zone.IsInvalidTime(local))
        {
            // The first local minute after the gap is its end; the offset an instant before it is the one before the gap.
            var after = local;
            do after = after.AddMinutes(1); while (zone.IsInvalidTime(after));
            var end = TimeZoneInfo.ConvertTimeToUtc(after, zone);
            return DateTime.SpecifyKind(local - zone.GetUtcOffset(end.AddTicks(-1)), DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static (DateOnly Day, TimeOnly Clock) ToLocal(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return (DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local));
    }

    /// <summary>The server's zone: <c>TZ</c> as an IANA id, else the system's local zone, else UTC.</summary>
    public static TimeZoneInfo Server()
    {
        if (Find(Environment.GetEnvironmentVariable("TZ")?.TrimStart(':')) is { } zone) return zone;
        try { return TimeZoneInfo.Local; }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }
}
