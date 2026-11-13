// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Calendar;

public sealed record DayEntry(Entry Entry, EntryStatus Status);

/// <param name="AlsoOngoing">The entries spanning several days that touch this one (spec §7.2), in the order of the week's spanning ones.</param>
public sealed record CalendarDay(DateOnly Day, IReadOnlyList<DayEntry> Entries, IReadOnlyList<DayEntry> AlsoOngoing);

/// <param name="Spanning">Week entries and entries over several days touching the week: by first day, then title.</param>
/// <param name="Days">Always seven, Monday to Sunday.</param>
public sealed record CalendarWeek(int IsoYear, int Week, DateOnly Monday, IReadOnlyList<DayEntry> Spanning, IReadOnlyList<CalendarDay> Days);

/// <summary>
/// A vault's entries as an ISO week or a month of weeks, for a viewer in a zone (spec §7.2, §7.4). An entry spanning several days
/// stands above each week it touches and in Also ongoing of each of its days; every other entry stands on its day. Days count as
/// <see cref="EntryStates"/> counts them: a day or week as written, a time on the viewer's local day of its instant.
/// </summary>
public static class CalendarLayout
{
    public static CalendarWeek Week(VaultCalendar calendar, int isoYear, int week, DateTime nowUtc, TimeZoneInfo zone) =>
        WeekOf(Place(calendar, nowUtc, zone), isoYear, week);

    /// <summary>The ISO weeks from that of the 1st to that of the last day, so that every day of the month is in a week shown.</summary>
    public static IReadOnlyList<CalendarWeek> Month(VaultCalendar calendar, int year, int month, DateTime nowUtc, TimeZoneInfo zone)
    {
        var placed = Place(calendar, nowUtc, zone);
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var weeks = new List<CalendarWeek>();
        for (var monday = MondayOf(new DateOnly(year, month, 1)); monday <= last; monday = monday.AddDays(7))
        {
            var day = monday.ToDateTime(TimeOnly.MinValue);
            weeks.Add(WeekOf(placed, ISOWeek.GetYear(day), ISOWeek.GetWeekOfYear(day)));
        }

        return weeks;
    }

    /// <summary>The local day an entry stands on: a day as written, a week its Monday, a time the viewer's day of its instant
    /// (in the note's <c>tz</c>, else <paramref name="zone"/>).</summary>
    public static DateOnly Day(Entry entry, TimeZoneInfo zone) =>
        entry.When.Kind == TimeKind.Time ? Zones.ToLocal(StartUtc(entry, zone), zone).Day : entry.When.FirstDay;

    /// <summary>The instant <c>when</c> begins: a time's instant, a day's or week's first local midnight in <paramref name="zone"/>.</summary>
    internal static DateTime StartUtc(Entry entry, TimeZoneInfo zone) =>
        entry.When.StartUtc(entry.When.Kind == TimeKind.Time ? entry.Zone ?? zone : zone);

    private sealed record Placed(DayEntry Item, DateOnly First, DateOnly Last, bool Spans, bool Timed, DateTime Start);

    // Each entry's days and state once, for every week laid out from them; sorted once for the days and once for the spans.
    private sealed record Places(IReadOnlyList<Placed> OnDays, IReadOnlyList<Placed> Spanning);

    private static Places Place(VaultCalendar calendar, DateTime nowUtc, TimeZoneInfo zone)
    {
        var placed = calendar.Entries.Select(entry =>
        {
            var (first, last) = DaysOf(entry, zone);
            return new Placed(new DayEntry(entry, EntryStates.Of(entry, nowUtc, zone)), first, last,
                entry.When.Kind == TimeKind.Week || last > first, entry.When.Kind == TimeKind.Time, StartUtc(entry, zone));
        }).ToList();

        return new Places(
            [.. placed.Where(p => !p.Spans).OrderBy(p => p.First).ThenBy(p => p.Timed).ThenBy(p => p.Timed ? p.Start : default)
                .ThenBy(p => p.Item.Entry.Title, TextOrder.IgnoreCase).ThenBy(p => p.Item.Entry.Path.Value, StringComparer.Ordinal)],
            [.. placed.Where(p => p.Spans).OrderBy(p => p.First)
                .ThenBy(p => p.Item.Entry.Title, TextOrder.IgnoreCase).ThenBy(p => p.Item.Entry.Path.Value, StringComparer.Ordinal)]);
    }

    private static CalendarWeek WeekOf(Places places, int isoYear, int week)
    {
        var monday = DateOnly.FromDateTime(ISOWeek.ToDateTime(isoYear, week, DayOfWeek.Monday));
        var sunday = monday.AddDays(6);
        var spanning = places.Spanning.Where(p => p.First <= sunday && p.Last >= monday).ToList();
        var days = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).Select(day => new CalendarDay(day,
            [.. places.OnDays.Where(p => p.First == day).Select(p => p.Item)],
            [.. spanning.Where(p => p.First <= day && p.Last >= day).Select(p => p.Item)])).ToList();
        return new CalendarWeek(isoYear, week, monday, [.. spanning.Select(p => p.Item)], days);
    }

    // The viewer's first and last day of an entry. A time window ends at its instant, so one ending at midnight doesn't touch
    // the day that midnight opens.
    private static (DateOnly First, DateOnly Last) DaysOf(Entry entry, TimeZoneInfo zone)
    {
        var first = Day(entry, zone);
        var end = entry.Until ?? entry.When;
        if (end.Kind != TimeKind.Time) return (first, end.LastDay);

        var (day, clock) = Zones.ToLocal(EntryStates.EndUtc(entry, zone), zone);
        var last = clock == TimeOnly.MinValue && day > first ? day.AddDays(-1) : day;
        return (first, last < first ? first : last);
    }

    private static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}

public sealed record WhatsOnItem(Entry Entry, EntryStatus Status);

/// <summary>What's on (spec §5): every entry that is upcoming, ongoing or overdue, across the calendars given.</summary>
public static class WhatsOn
{
    /// <summary>Overdue (longest overdue first), then ongoing (ending soonest first), then upcoming (soonest first); ties by title, then path.</summary>
    public static IReadOnlyList<WhatsOnItem> Of(IEnumerable<VaultCalendar> calendars, DateTime nowUtc, TimeZoneInfo zone) =>
        [.. calendars.SelectMany(c => c.Entries)
            .Select(entry => new WhatsOnItem(entry, EntryStates.Of(entry, nowUtc, zone)))
            .Where(item => EntryStates.IsOn(item.Status.State))
            .OrderBy(item => item.Status.State switch { EntryState.Overdue => 0, EntryState.Ongoing => 1, _ => 2 })
            .ThenBy(item => item.Status.State == EntryState.Upcoming
                ? CalendarLayout.StartUtc(item.Entry, zone)
                : EntryStates.EndUtc(item.Entry, zone))
            .ThenBy(item => item.Entry.Title, TextOrder.IgnoreCase)
            .ThenBy(item => item.Entry.Path.Value, StringComparer.Ordinal)];
}
