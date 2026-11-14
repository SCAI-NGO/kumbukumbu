// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Calendar;

public class EntryStatesTests
{
    private static readonly TimeZoneInfo Vienna = Zones.Find("Europe/Vienna")!;
    private static readonly TimeZoneInfo NewYork = Zones.Find("America/New_York")!;

    private static readonly BoardDefinition Board = BoardDefinition.Parse(
        "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Done: [archivable]\n---\n").Definition!;

    private static Entry EntryOf(string frontmatter, string path = "/Work/Zahnarzt.md", BoardDefinition? board = null)
    {
        var (entry, problem) = EntryReader.Read(
            new Note(VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path), $"---\n{frontmatter}---\n"), board);
        Assert.Null(problem);
        return Assert.IsType<Entry>(entry);
    }

    private static DateTime Utc(string text) =>
        DateTime.SpecifyKind(DateTime.Parse(text.TrimEnd('Z'), System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static EntryStatus At(Entry entry, string nowUtc, TimeZoneInfo? zone = null) => EntryStates.Of(entry, Utc(nowUtc), zone ?? Vienna);

    private static void Is(EntryState state, string? span, EntryStatus status, bool beforeTime = false) =>
        Assert.Equal(new EntryStatus(state, span, beforeTime), status);

    [Fact]
    public void An_appointment_is_upcoming_then_ongoing_then_past()
    {
        var entry = EntryOf("when: 2026-12-15\nupcoming: 7d\n");

        Is(EntryState.NotYet, null, At(entry, "2026-12-07T22:59Z"));
        Is(EntryState.Upcoming, "in 7 days", At(entry, "2026-12-08T08:00Z"));
        Is(EntryState.Upcoming, "tomorrow", At(entry, "2026-12-14T08:00Z"));
        Is(EntryState.Ongoing, "today", At(entry, "2026-12-15T10:00Z"));
        Is(EntryState.Past, null, At(entry, "2026-12-16T08:00Z"));
    }

    [Fact]
    public void Without_upcoming_an_entry_starts_to_matter_at_when()
    {
        var entry = EntryOf("when: 2026-12-15\n");

        Is(EntryState.NotYet, null, At(entry, "2026-12-14T22:59Z"));
        Is(EntryState.Ongoing, "today", At(entry, "2026-12-14T23:00Z"));
    }

    [Fact]
    public void Upcoming_is_counted_in_weeks_from_two_weeks_on()
    {
        var entry = EntryOf("when: 2026-12-31\nupcoming: 4w\n");

        Is(EntryState.Upcoming, "in 4 weeks", At(entry, "2026-12-03T08:00Z"));
        Is(EntryState.Upcoming, "in 2 weeks", At(entry, "2026-12-16T08:00Z"));
        Is(EntryState.Upcoming, "in 13 days", At(entry, "2026-12-18T08:00Z"));
    }

    [Fact]
    public void Upcoming_in_days_keeps_the_wall_clock_across_summer_time()
    {
        // 1 April is in summer time, 25 March not yet: seven days before is local midnight, not 23:00.
        var entry = EntryOf("when: 2026-04-01\nupcoming: 7d\n");

        Assert.Equal(Utc("2026-03-24T23:00Z"), EntryStates.RelevantFromUtc(entry, Vienna));
        Is(EntryState.NotYet, null, At(entry, "2026-03-24T22:30Z"));
        Is(EntryState.Upcoming, "in 7 days", At(entry, "2026-03-24T23:00Z"));
    }

    [Fact]
    public void A_task_is_overdue_after_its_end_calmly_counted()
    {
        var entry = EntryOf("when: 2026-12-15\ntask: open\nupcoming: 7d\n");

        Is(EntryState.Upcoming, "in 7 days", At(entry, "2026-12-08T08:00Z"));
        Is(EntryState.Ongoing, "today", At(entry, "2026-12-15T10:00Z"));
        Is(EntryState.Overdue, "1 day", At(entry, "2026-12-16T08:00Z"));
        Is(EntryState.Overdue, "2 days", At(entry, "2026-12-17T08:00Z"));
        Is(EntryState.Overdue, "13 days", At(entry, "2026-12-28T08:00Z"));
        Is(EntryState.Overdue, "2 weeks", At(entry, "2026-12-29T08:00Z"));
        Is(EntryState.Overdue, "3 weeks", At(entry, "2027-01-06T08:00Z"));
        Is(EntryState.Overdue, "8 weeks", At(entry, "2027-02-12T08:00Z"));
        Is(EntryState.Overdue, "2 months", At(entry, "2027-02-13T08:00Z"));
        Is(EntryState.Overdue, "1 year", At(entry, "2028-01-15T08:00Z"));
    }

    [Fact]
    public void The_end_of_a_day_is_local_midnight()
    {
        var entry = EntryOf("when: 2026-11-20\ntask: open\n");

        Assert.Equal(EntryState.Ongoing, At(entry, "2026-11-20T22:59Z").State);
        Is(EntryState.Overdue, "1 day", At(entry, "2026-11-20T23:00Z"));
    }

    [Fact]
    public void The_end_of_a_week_is_the_end_of_its_Sunday()
    {
        var entry = EntryOf("when: 2026-W46\ntask: open\n");

        Is(EntryState.NotYet, null, At(entry, "2026-11-08T22:59Z"));
        Is(EntryState.Ongoing, "until Sun", At(entry, "2026-11-09T08:00Z"));
        Is(EntryState.Ongoing, "today", At(entry, "2026-11-15T22:59Z"));
        Is(EntryState.Overdue, "1 day", At(entry, "2026-11-15T23:00Z"));
    }

    [Fact]
    public void A_timed_appointment_is_ongoing_on_its_day_until_its_time()
    {
        var entry = EntryOf("when: 2026-11-20T14:00\n");

        Is(EntryState.NotYet, null, At(entry, "2026-11-19T22:59Z"));
        Is(EntryState.Ongoing, "14:00", At(entry, "2026-11-19T23:30Z"), beforeTime: true);
        Is(EntryState.Ongoing, "14:00", At(entry, "2026-11-20T12:59Z"), beforeTime: true);
        Is(EntryState.Past, null, At(entry, "2026-11-20T13:01Z"));
    }

    [Fact]
    public void A_timed_task_is_overdue_after_its_time()
    {
        var entry = EntryOf("when: 2026-11-20T14:00\ntask: open\n");

        Is(EntryState.Ongoing, "14:00", At(entry, "2026-11-20T08:00Z"), beforeTime: true);
        Is(EntryState.Overdue, "1 hour", At(entry, "2026-11-20T13:00:01Z"));
        Is(EntryState.Overdue, "1 hour", At(entry, "2026-11-20T13:30Z"));
        Is(EntryState.Overdue, "3 hours", At(entry, "2026-11-20T16:10Z"));
        Is(EntryState.Overdue, "1 day", At(entry, "2026-11-21T08:00Z"));
    }

    [Fact]
    public void A_timed_window_is_upcoming_until_the_start_of_its_day()
    {
        var entry = EntryOf("when: 2026-11-20T14:00\nuntil: 2026-11-22T10:00\nupcoming: 1d\n");

        Assert.Equal(Utc("2026-11-19T23:00Z"), EntryStates.OngoingFromUtc(entry, Vienna));
        Assert.Equal(Utc("2026-11-22T09:00Z"), EntryStates.EndUtc(entry, Vienna));
        Is(EntryState.NotYet, null, At(entry, "2026-11-19T12:59Z"));
        Is(EntryState.Upcoming, "tomorrow", At(entry, "2026-11-19T13:00Z"));
        Is(EntryState.Upcoming, "tomorrow", At(entry, "2026-11-19T22:59Z"));
        Is(EntryState.Ongoing, "14:00", At(entry, "2026-11-20T12:00Z"), beforeTime: true);
        Is(EntryState.Ongoing, "until Sun", At(entry, "2026-11-20T13:00Z"));
        Is(EntryState.Ongoing, "until 10:00", At(entry, "2026-11-22T08:00Z"));
        Is(EntryState.Past, null, At(entry, "2026-11-22T09:00Z"));
    }

    [Fact]
    public void A_window_is_ongoing_all_along()
    {
        var entry = EntryOf("when: 2026-11-03\nuntil: 2026-11-14\nupcoming: 2w\n");

        Is(EntryState.Upcoming, "in 7 days", At(entry, "2026-10-27T10:00Z"));
        Is(EntryState.Ongoing, "until 14 Nov", At(entry, "2026-11-05T10:00Z"));
        Is(EntryState.Ongoing, "until 14 Nov", At(entry, "2026-11-07T10:00Z"));
        Is(EntryState.Ongoing, "until Sat", At(entry, "2026-11-08T10:00Z"));
        Is(EntryState.Ongoing, "until Sat", At(entry, "2026-11-10T10:00Z"));
        Is(EntryState.Ongoing, "until Sat", At(entry, "2026-11-13T10:00Z"));
        Is(EntryState.Ongoing, "today", At(entry, "2026-11-14T10:00Z"));
        Is(EntryState.Past, null, At(entry, "2026-11-14T23:00Z"));
    }

    [Fact]
    public void An_end_a_year_or_more_away_names_its_year()
    {
        var entry = EntryOf("when: 2026-11-03\nuntil: 2027-11-14\n");

        Is(EntryState.Ongoing, "until 14 Nov 2027", At(entry, "2026-11-05T10:00Z"));
        Is(EntryState.Ongoing, "until 14 Nov", At(entry, "2026-11-15T10:00Z"));
    }

    [Fact]
    public void Closed_early_is_done()
    {
        var entry = EntryOf("when: 2026-12-15\ntask: closed\nclosed: 2026-11-13\nupcoming: 7d\n");

        Is(EntryState.Done, null, At(entry, "2026-11-13T10:00Z"));
        Is(EntryState.Done, null, At(entry, "2026-12-15T10:00Z"));
        Is(EntryState.Done, null, At(entry, "2027-01-15T10:00Z"));
    }

    [Fact]
    public void A_card_is_a_task_its_column_the_status()
    {
        var open = EntryOf("column: Ideas\ndue: 2026-11-20\n", "/Work/Board/Renew.md", Board);
        var done = EntryOf("column: Done\ndue: 2026-11-20\n", "/Work/Board/Renewed.md", Board);

        Is(EntryState.Overdue, "2 days", At(open, "2026-11-22T10:00Z"));
        Is(EntryState.Done, null, At(done, "2026-11-22T10:00Z"));
    }

    [Fact]
    public void The_same_instant_for_two_zones()
    {
        var timed = EntryOf("when: 2026-11-20T14:00\n");

        Assert.Equal(EntryState.Ongoing, At(timed, "2026-11-20T12:30Z", Vienna).State);
        Is(EntryState.Ongoing, "14:00", At(timed, "2026-11-20T12:30Z", NewYork), beforeTime: true);
        Is(EntryState.Past, null, At(timed, "2026-11-20T14:30Z", Vienna));
        Is(EntryState.Ongoing, "14:00", At(timed, "2026-11-20T14:30Z", NewYork), beforeTime: true);

        // A day is 20 November for both: from each one's midnight to each one's next.
        var day = EntryOf("when: 2026-11-20\n");
        Assert.Equal(Utc("2026-11-19T23:00Z"), EntryStates.OngoingFromUtc(day, Vienna));
        Assert.Equal(Utc("2026-11-20T05:00Z"), EntryStates.OngoingFromUtc(day, NewYork));
        Is(EntryState.Ongoing, "today", At(day, "2026-11-20T12:00Z", Vienna));
        Is(EntryState.Ongoing, "today", At(day, "2026-11-20T12:00Z", NewYork));
        Is(EntryState.Past, null, At(day, "2026-11-20T23:30Z", Vienna));
        Is(EntryState.Ongoing, "today", At(day, "2026-11-20T23:30Z", NewYork));
    }

    [Fact]
    public void A_tz_on_the_note_pins_its_time()
    {
        var entry = EntryOf("when: 2026-11-20T14:00\ntz: Europe/London\n");

        Assert.Equal(Utc("2026-11-20T14:00Z"), EntryStates.EndUtc(entry, Vienna));
        Assert.Equal(Utc("2026-11-19T23:00Z"), EntryStates.OngoingFromUtc(entry, Vienna));
        Is(EntryState.Ongoing, "14:00 (Europe/London)", At(entry, "2026-11-20T13:30Z"), beforeTime: true);
        Is(EntryState.Ongoing, "14:00", At(entry, "2026-11-20T13:30Z", Zones.Find("Europe/London")!), beforeTime: true);
        Is(EntryState.Past, null, At(entry, "2026-11-20T14:00Z"));
    }

    [Fact]
    public void An_offset_written_by_hand_is_shown_in_the_viewers_clock()
    {
        var entry = EntryOf("when: 2026-11-20T14:00Z\n");

        Assert.Equal(Utc("2026-11-20T14:00Z"), EntryStates.EndUtc(entry, Vienna));
        Is(EntryState.Ongoing, "15:00", At(entry, "2026-11-20T10:00Z"), beforeTime: true);
    }

    [Fact]
    public void Summer_time_both_sides()
    {
        Assert.Equal(Utc("2026-03-28T11:00Z"), EntryStates.EndUtc(EntryOf("when: 2026-03-28T12:00\ntask: open\n"), Vienna));
        Assert.Equal(Utc("2026-03-30T10:00Z"), EntryStates.EndUtc(EntryOf("when: 2026-03-30T12:00\ntask: open\n"), Vienna));
    }

    [Fact]
    public void A_time_in_the_spring_gap_is_read_as_the_hour_after()
    {
        var entry = EntryOf("when: 2026-03-29T02:30\ntask: open\n");

        // 02:30 does not exist in Vienna that night; it is 03:30 summer time, 01:30Z.
        Assert.Equal(Utc("2026-03-29T01:30Z"), EntryStates.EndUtc(entry, Vienna));
        Is(EntryState.Ongoing, "03:30", At(entry, "2026-03-29T01:00Z"), beforeTime: true);
        Is(EntryState.Overdue, "1 hour", At(entry, "2026-03-29T01:30Z"));
    }

    [Fact]
    public void Relevant_from_is_the_earlier_of_upcoming_and_ongoing()
    {
        var timed = EntryOf("when: 2026-11-20T14:00\nupcoming: 2h\n");
        Assert.Equal(Utc("2026-11-19T23:00Z"), EntryStates.RelevantFromUtc(timed, Vienna));

        var early = EntryOf("when: 2026-11-20T14:00\nupcoming: 1d\n");
        Assert.Equal(Utc("2026-11-19T13:00Z"), EntryStates.RelevantFromUtc(early, Vienna));
        Is(EntryState.Upcoming, "tomorrow", At(early, "2026-11-19T13:00Z"));
    }

    [Fact]
    public void A_long_upcoming_at_the_edge_of_the_years_is_relevant_from_the_start_of_time()
    {
        var entry = EntryOf("when: 0002-01-10\nupcoming: 9999w\n");

        Assert.Equal(DateTime.MinValue, EntryStates.RelevantFromUtc(entry, Vienna));
        Is(EntryState.Upcoming, "in 9 days", At(entry, "0002-01-01T08:00Z"));
    }

    // Saturday, 14 November 2026, in Vienna (UTC+1): 08:00 local is 07:00Z.
    [Fact]
    public void A_timed_window_is_ongoing_from_the_start_of_its_day_before_its_time()
    {
        var entry = EntryOf("when: 2026-11-14T14:00\nuntil: 2026-11-14T15:00\n");

        Assert.Equal(Utc("2026-11-13T23:00Z"), EntryStates.OngoingFromUtc(entry, Vienna));
        Is(EntryState.Ongoing, "14:00", At(entry, "2026-11-14T07:00Z"), beforeTime: true);
    }

    [Fact]
    public void A_timed_window_within_its_time_says_until()
    {
        var entry = EntryOf("when: 2026-11-14T14:00\nuntil: 2026-11-14T15:00\n");

        Is(EntryState.Ongoing, "until 15:00", At(entry, "2026-11-14T13:30Z"));
    }

    [Fact]
    public void A_timed_window_after_its_time_is_past()
    {
        Is(EntryState.Past, null, At(EntryOf("when: 2026-11-14T14:00\nuntil: 2026-11-14T15:00\n"), "2026-11-14T14:30Z"));
        Is(EntryState.Overdue, "1 hour", At(EntryOf("when: 2026-11-14T14:00\nuntil: 2026-11-14T15:00\ntask: open\n"), "2026-11-14T14:30Z"));
    }

    [Fact]
    public void A_time_without_until_reads_today_before_its_time()
    {
        var entry = EntryOf("when: 2026-11-14T14:00\n");

        Is(EntryState.Ongoing, "14:00", At(entry, "2026-11-14T08:00Z"), beforeTime: true);
    }

    [Fact]
    public void A_timed_window_the_day_before_is_not_yet()
    {
        var entry = EntryOf("when: 2026-11-14T14:00\nuntil: 2026-11-14T15:00\n");

        Is(EntryState.NotYet, null, At(entry, "2026-11-13T19:00Z"));
    }

    private static readonly DateOnly Today = new(2026, 11, 14);

    private static EntryStatus OverdueSince(DateOnly when) =>
        At(EntryOf($"when: {when:yyyy-MM-dd}\ntask: open\n"), "2026-11-14T09:00Z");

    [Fact]
    public void Overdue_says_months_until_11() => Is(EntryState.Overdue, "11 months", OverdueSince(Today.AddMonths(-11)));

    [Fact]
    public void Overdue_says_a_year_at_12_months() => Is(EntryState.Overdue, "1 year", OverdueSince(Today.AddMonths(-12)));

    [Fact]
    public void Overdue_says_years() => Is(EntryState.Overdue, "3 years", OverdueSince(Today.AddYears(-3)));

    [Fact]
    public void On_is_upcoming_ongoing_and_overdue()
    {
        Assert.Equal(
            new[] { EntryState.Upcoming, EntryState.Ongoing, EntryState.Overdue },
            Enum.GetValues<EntryState>().Where(EntryStates.IsOn));
    }
}
