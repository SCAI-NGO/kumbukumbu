// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Calendar;

public class CalendarLayoutTests
{
    private static readonly TimeZoneInfo Vienna = Zones.Find("Europe/Vienna")!;
    private static readonly DateTime Now = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);

    internal static VaultCalendar CalendarOf(string vault, params (string Path, string Content)[] notes)
    {
        var entries = new List<Entry>();
        foreach (var (path, content) in notes)
        {
            var (entry, problem) = EntryReader.Read(new Note(VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path), content), null);
            Assert.Null(problem);
            entries.Add(Assert.IsType<Entry>(entry));
        }

        return new VaultCalendar(vault, entries, []);
    }

    private static IEnumerable<string> Titles(IEnumerable<DayEntry> entries) => entries.Select(e => e.Entry.Title);

    private static readonly VaultCalendar November = CalendarOf("Work",
        ("/Work/Calendar/2026-W46.md", "# Week 46\n"),
        ("/Work/Zahnarzt.md", "---\nwhen: 2026-11-10T14:00\n---\n"),
        ("/Work/Urlaub.md", "---\nwhen: 2026-11-03\nuntil: 2026-11-14\n---\n"),
        ("/Work/Ideas/Pump.md", "---\nwhen: 2026-11-12\n---\n"));

    [Fact]
    public void A_week_has_seven_days_and_spanning_entries_on_top()
    {
        var week = CalendarLayout.Week(November, 2026, 46, Now, Vienna);

        Assert.Equal((2026, 46, new DateOnly(2026, 11, 9)), (week.IsoYear, week.Week, week.Monday));
        Assert.Equal(Enumerable.Range(9, 7).Select(d => new DateOnly(2026, 11, d)), week.Days.Select(d => d.Day));
        Assert.Equal(["Urlaub", "2026-W46"], Titles(week.Spanning));
        Assert.Equal(EntryState.Ongoing, week.Spanning[0].Status.State);

        Assert.Empty(week.Days[0].Entries);
        Assert.Equal(["Zahnarzt"], Titles(week.Days[1].Entries));
        Assert.Empty(week.Days[2].Entries);
        Assert.Equal(["Pump"], Titles(week.Days[3].Entries));
        Assert.All(week.Days.Skip(4), d => Assert.Empty(d.Entries));

        Assert.All(week.Days.Take(6), d => Assert.Equal(["Urlaub", "2026-W46"], Titles(d.AlsoOngoing)));
        Assert.Equal(["2026-W46"], Titles(week.Days[6].AlsoOngoing));
    }

    [Fact]
    public void A_span_shows_once_in_each_week_it_touches()
    {
        string[] Spanning(int week) => [.. Titles(CalendarLayout.Week(November, 2026, week, Now, Vienna).Spanning)];

        Assert.Empty(Spanning(44));
        Assert.Equal(["Urlaub"], Spanning(45));
        Assert.Equal(["Urlaub", "2026-W46"], Spanning(46));
        Assert.Empty(Spanning(47));

        var week45 = CalendarLayout.Week(November, 2026, 45, Now, Vienna);
        Assert.Empty(week45.Days[0].AlsoOngoing);
        Assert.All(week45.Days.Skip(1), d => Assert.Equal(["Urlaub"], Titles(d.AlsoOngoing)));
        Assert.All(week45.Days, d => Assert.Empty(d.Entries));
    }

    [Fact]
    public void Untimed_before_timed_within_a_day()
    {
        var calendar = CalendarOf("Work",
            ("/Work/Zebra.md", "---\nwhen: 2026-11-12\n---\n"),
            ("/Work/Apple.md", "---\nwhen: 2026-11-12T15:00\n---\n"),
            ("/Work/Mango.md", "---\nwhen: 2026-11-12T09:00\n---\n"),
            ("/Work/banana.md", "---\nwhen: 2026-11-12\n---\n"),
            ("/Work/Calendar/2026-11-12.md", "# Thursday\n"));

        var thursday = CalendarLayout.Week(calendar, 2026, 46, Now, Vienna).Days[3];

        Assert.Equal(["2026-11-12", "banana", "Zebra", "Mango", "Apple"], Titles(thursday.Entries));
        Assert.Empty(thursday.AlsoOngoing);
    }

    [Fact]
    public void A_time_stands_on_the_viewers_day_of_its_instant()
    {
        // 23:30 in London is 00:30 the next day in Vienna; a day stays the day it is.
        var calendar = CalendarOf("Work",
            ("/Work/Call.md", "---\nwhen: 2026-11-11T23:30\ntz: Europe/London\n---\n"),
            ("/Work/Early.md", "---\nwhen: 2026-11-12T08:00\n---\n"),
            ("/Work/Day.md", "---\nwhen: 2026-11-11\n---\n"));

        Assert.Equal(new DateOnly(2026, 11, 12), CalendarLayout.Day(calendar.Entries[0], Vienna));
        Assert.Equal(new DateOnly(2026, 11, 11), CalendarLayout.Day(calendar.Entries[0], Zones.Find("Europe/London")!));
        Assert.Equal(new DateOnly(2026, 11, 11), CalendarLayout.Day(calendar.Entries[2], Zones.Find("Pacific/Kiritimati")!));

        var week = CalendarLayout.Week(calendar, 2026, 46, Now, Vienna);
        Assert.Equal(["Day"], Titles(week.Days[2].Entries));
        Assert.Equal(["Call", "Early"], Titles(week.Days[3].Entries));
    }

    [Fact]
    public void A_month_is_its_weeks()
    {
        static (int, int, DateOnly)[] Weeks(int year, int month) =>
            [.. CalendarLayout.Month(November, year, month, Now, Vienna).Select(w => (w.IsoYear, w.Week, w.Monday))];

        // Week 44 holds 1 November alone: every day of the month is in a week shown.
        Assert.Equal([.. Enumerable.Range(44, 6).Select(w => (2026, w, new DateOnly(2026, 10, 26).AddDays(7 * (w - 44))))], Weeks(2026, 11));
        Assert.Equal([.. Enumerable.Range(49, 5).Select(w => (2026, w, new DateOnly(2026, 11, 30).AddDays(7 * (w - 49))))], Weeks(2026, 12));
        Assert.Equal(
            [(2026, 53, new DateOnly(2026, 12, 28)), .. Enumerable.Range(1, 4).Select(w => (2027, w, new DateOnly(2027, 1, 4).AddDays(7 * (w - 1))))],
            Weeks(2027, 1));

        var november = CalendarLayout.Month(November, 2026, 11, Now, Vienna);
        Assert.Equal(["Urlaub"], Titles(november[1].Spanning));
        Assert.Equal(["Zahnarzt"], Titles(november[2].Days[1].Entries));
        Assert.All(november.Skip(3), w => Assert.Empty(w.Spanning));
    }
}
