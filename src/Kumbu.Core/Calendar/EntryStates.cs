// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace Kumbu.Core.Calendar;

public enum EntryState { NotYet, Upcoming, Ongoing, Overdue, Done, Past }

/// <param name="Span">The human span beside the state: "in 7 days", "until Fri", "1 day", or the clock "14:00" before a time; null for
/// NotYet, Done and Past.</param>
/// <param name="BeforeTime">Ongoing on the day of <c>when</c>'s time, before it: shown as "today · 14:00".</param>
public sealed record EntryStatus(EntryState State, string? Span, bool BeforeTime = false);

/// <summary>
/// An entry's state at an instant, for a viewer in a zone (spec §2.4). A day or week is the same day for everyone, bounded by the
/// viewer's midnights; a time is in the note's <c>tz</c>, else the viewer's zone (spec §3). Spans count the viewer's local days.
/// </summary>
public static class EntryStates
{
    public static EntryStatus Of(Entry entry, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (entry.Task == TaskMark.Closed) return new EntryStatus(EntryState.Done, null);
        if (nowUtc < RelevantFromUtc(entry, zone)) return new EntryStatus(EntryState.NotYet, null);

        var today = Zones.ToLocal(nowUtc, zone).Day;
        if (nowUtc < OngoingFromUtc(entry, zone))
            return new EntryStatus(EntryState.Upcoming, Ahead(FirstDay(entry.When, entry, zone).DayNumber - today.DayNumber));

        var end = entry.Until ?? entry.When;
        var endUtc = EndUtc(entry, zone);
        var lastDay = LastDay(end, entry, zone);
        if (nowUtc < endUtc)
        {
            // Before its time on the day it begins, a time reads as the clock it is at; within a window, as where it ends.
            if (entry.When.Kind == TimeKind.Time && nowUtc < entry.When.StartUtc(ZoneOf(entry.When, entry, zone)))
                return new EntryStatus(EntryState.Ongoing, EntryText.Clock(entry, zone, withEnd: false), BeforeTime: true);
            return new EntryStatus(EntryState.Ongoing, Until(end, endUtc, lastDay, today, entry, zone));
        }

        if (entry.Task == TaskMark.Open)
            return new EntryStatus(EntryState.Overdue, Behind(today.DayNumber - lastDay.DayNumber, nowUtc - endUtc));
        return new EntryStatus(EntryState.Past, null);
    }

    /// <summary>
    /// When the entry turns ongoing: the start of <c>when</c>'s first day; for a time, with or without <c>until</c>, the start of the
    /// viewer's day it falls on (spec §2.4, amended: a timed entry is relevant from the start of its day).
    /// </summary>
    public static DateTime OngoingFromUtc(Entry entry, TimeZoneInfo zone)
    {
        if (entry.When.Kind != TimeKind.Time) return entry.When.StartUtc(ZoneOf(entry.When, entry, zone));
        return Zones.ToUtc(FirstDay(entry.When, entry, zone), TimeOnly.MinValue, zone);
    }

    /// <summary>The end of <c>until</c>, else of <c>when</c>: a day's next midnight, a week's after its Sunday, a time at that time.</summary>
    public static DateTime EndUtc(Entry entry, TimeZoneInfo zone)
    {
        var end = entry.Until ?? entry.When;
        return end.EndUtc(ZoneOf(end, entry, zone));
    }

    /// <summary>When the entry starts to matter: <c>upcoming</c> before the start of <c>when</c>, or when it turns ongoing if that is earlier.</summary>
    public static DateTime RelevantFromUtc(Entry entry, TimeZoneInfo zone)
    {
        // Never after the start of when: ongoing begins there at the latest.
        var ongoing = OngoingFromUtc(entry, zone);
        return entry.Upcoming is { } upcoming ? Min(ongoing, Before(entry, upcoming, zone)) : ongoing;
    }

    public static bool IsOn(EntryState state) => state is EntryState.Upcoming or EntryState.Ongoing or EntryState.Overdue;

    // Days and weeks back on the wall clock, so that "7d" before a day in summer time is still local midnight; hours are hours.
    private static DateTime Before(Entry entry, Upcoming upcoming, TimeZoneInfo zone)
    {
        var whenZone = ZoneOf(entry.When, entry, zone);
        var start = entry.When.StartUtc(whenZone);
        if (upcoming.Unit == 'h')
            return start.Ticks > upcoming.Length.Ticks ? start - upcoming.Length : DateTime.MinValue;

        var (day, clock) = entry.When.Kind == TimeKind.Time ? Zones.ToLocal(start, whenZone) : (entry.When.FirstDay, TimeOnly.MinValue);
        var number = day.DayNumber - (int)upcoming.Length.TotalDays;
        // Up to 9999 weeks before the year 2: further back than a zone can convert, so it matters from the start of time.
        if (number < FirstReadable.DayNumber) return DateTime.MinValue;
        return Zones.ToUtc(DateOnly.FromDayNumber(number), clock, whenZone);
    }

    private static readonly DateOnly FirstReadable = new(2, 1, 1);

    private static string Ahead(int days) => days switch
    {
        <= 0 => "today",
        1 => "tomorrow",
        < 14 => $"in {days} days",
        _ => $"in {days / 7} weeks",
    };

    private static string Until(CalendarTime end, DateTime endUtc, DateOnly lastDay, DateOnly today, Entry entry, TimeZoneInfo zone)
    {
        var days = lastDay.DayNumber - today.DayNumber;
        if (days <= 0) return end.Kind == TimeKind.Time ? "until " + Clock(end, endUtc, entry, zone) : "today";
        if (days <= 6) return "until " + lastDay.ToString("ddd", CultureInfo.InvariantCulture);
        // A day and month alone would be read as the coming one: an end a year or more away names its year.
        var form = lastDay >= today.AddYears(1) ? "d MMM yyyy" : "d MMM";
        return "until " + lastDay.ToString(form, CultureInfo.InvariantCulture);
    }

    // A time pinned to another zone than the viewer's shows that zone (spec §3); every other time is the viewer's clock.
    private static string Clock(CalendarTime end, DateTime endUtc, Entry entry, TimeZoneInfo zone)
    {
        if (entry.Zone is { } pinned && end.Offset is null && pinned.Id != zone.Id)
            return Zones.ToLocal(endUtc, pinned).Clock.ToString("HH:mm", CultureInfo.InvariantCulture) + $" ({pinned.Id})";
        return Zones.ToLocal(endUtc, zone).Clock.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Behind(int days, TimeSpan since) => days switch
    {
        <= 0 => Count(Math.Max(1, (int)since.TotalHours), "hour"),
        < 14 => Count(days, "day"),
        < 60 => Count(days / 7, "week"),
        _ when days / 30 < 12 => Count(days / 30, "month"),
        _ => Count(Math.Max(1, days / 365), "year"),
    };

    private static string Count(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";

    // The viewer's local day of a value: a day or week as written, a time where its instant falls for the viewer.
    private static DateOnly FirstDay(CalendarTime value, Entry entry, TimeZoneInfo zone) =>
        value.Kind == TimeKind.Time ? Zones.ToLocal(value.StartUtc(ZoneOf(value, entry, zone)), zone).Day : value.FirstDay;

    private static DateOnly LastDay(CalendarTime value, Entry entry, TimeZoneInfo zone) =>
        value.Kind == TimeKind.Time ? FirstDay(value, entry, zone) : value.LastDay;

    // The note's tz is the zone of its times only; a day stays a day (spec §3).
    private static TimeZoneInfo ZoneOf(CalendarTime value, Entry entry, TimeZoneInfo zone) =>
        value.Kind == TimeKind.Time ? entry.Zone ?? zone : zone;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
