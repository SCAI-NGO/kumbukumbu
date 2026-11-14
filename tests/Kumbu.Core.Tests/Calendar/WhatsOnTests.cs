// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Calendar;

public class WhatsOnTests
{
    private static readonly TimeZoneInfo Vienna = Zones.Find("Europe/Vienna")!;

    // Wednesday, 16 December 2026, 09:00 in Vienna.
    private static readonly DateTime Now = new(2026, 12, 16, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Whats_on_orders_overdue_ongoing_upcoming()
    {
        var calendar = CalendarLayoutTests.CalendarOf("Work",
            ("/Work/Party.md", "---\nwhen: 2026-12-23\nupcoming: 7d\n---\n"),
            ("/Work/Workshop.md", "---\nwhen: 2026-12-14\nuntil: 2026-12-18\n---\n"),
            ("/Work/Invoice.md", "---\nwhen: 2026-12-15\ntask: open\n---\n"),
            ("/Work/Later.md", "---\nwhen: 2026-12-30\n---\n"),
            ("/Work/Release.md", "---\nwhen: 2026-12-18\nupcoming: 7d\n---\n"),
            ("/Work/Report.md", "---\nwhen: 2026-11-24\ntask: open\n---\n"),
            ("/Work/Closed.md", "---\nwhen: 2026-12-01\ntask: closed\nclosed: 2026-12-02\n---\n"),
            ("/Work/Visit.md", "---\nwhen: 2026-12-15\nuntil: 2026-12-17\n---\n"),
            ("/Work/Alpha.md", "---\nwhen: 2026-12-10\nuntil: 2026-12-18\n---\n"),
            ("/Work/Past.md", "---\nwhen: 2026-12-01\n---\n"));

        var on = WhatsOn.Of([calendar], Now, Vienna);

        Assert.Equal(
            [
                ("Report", EntryState.Overdue, "3 weeks"),
                ("Invoice", EntryState.Overdue, "1 day"),
                ("Visit", EntryState.Ongoing, "until Thu"),
                ("Alpha", EntryState.Ongoing, "until Fri"),
                ("Workshop", EntryState.Ongoing, "until Fri"),
                ("Release", EntryState.Upcoming, "in 2 days"),
                ("Party", EntryState.Upcoming, "in 7 days"),
            ],
            on.Select(i => (i.Entry.Title, i.Status.State, i.Status.Span)));
    }

    [Fact]
    public void Ties_go_by_title_then_path()
    {
        var work = CalendarLayoutTests.CalendarOf("Work", ("/Work/b/Same.md", "---\nwhen: 2026-12-16\n---\n"));
        var home = CalendarLayoutTests.CalendarOf("Home",
            ("/Home/a/Same.md", "---\nwhen: 2026-12-16\n---\n"),
            ("/Home/Other.md", "---\nwhen: 2026-12-16\n---\n"));

        var on = WhatsOn.Of([work, home], Now, Vienna);

        Assert.Equal(["/Home/Other.md", "/Home/a/Same.md", "/Work/b/Same.md"], on.Select(i => i.Entry.Path.Value));
    }

    [Fact]
    public async Task Whats_on_covers_every_vault_with_or_without_a_Calendar_folder()
    {
        var store = new InMemoryNoteStore(
            ("/Work/Calendar/2026-12-16.md", "---\ntask: open\n---\n# Wednesday\n"),
            ("/Home/Zahnarzt.md", "---\nwhen: 2026-12-16T14:00\n---\n"),
            ("/Tagebuch/Deep/Down/Letter.md", "---\nwhen: 2026-12-10\ntask: open\n---\n"),
            ("/Tagebuch/_archive/Old.md", "---\nwhen: 2026-12-10\ntask: open\n---\n"));
        store.AddVault("Empty");

        var on = WhatsOn.Of(await CalendarReader.ReadAllAsync(store, null, CancellationToken.None), Now, Vienna);

        Assert.Equal(
            [("Tagebuch", "Letter", EntryState.Overdue), ("Home", "Zahnarzt", EntryState.Ongoing), ("Work", "2026-12-16", EntryState.Ongoing)],
            on.Select(i => (i.Entry.Vault, i.Entry.Title, i.Status.State)));
    }

    // Saturday, 14 November 2026, 10:00 in Vienna.
    private static readonly DateTime Saturday = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_day_note_today_without_task_is_not_on()
    {
        var calendar = CalendarLayoutTests.CalendarOf("Work", ("/Work/Calendar/2026-11-14.md", "# Saturday\n"));

        Assert.Empty(WhatsOn.Of([calendar], Saturday, Vienna));
        var day = CalendarLayout.Week(calendar, 2026, 46, Saturday, Vienna).Days.Single(d => d.Day == new DateOnly(2026, 11, 14));
        Assert.Equal(["2026-11-14"], day.Entries.Select(e => e.Entry.Title));
    }

    [Fact]
    public void A_day_note_today_with_task_open_is_on()
    {
        var calendar = CalendarLayoutTests.CalendarOf("Work", ("/Work/Calendar/2026-11-14.md", "---\ntask: open\n---\n# Saturday\n"));

        Assert.Equal([("2026-11-14", EntryState.Ongoing)], WhatsOn.Of([calendar], Saturday, Vienna).Select(i => (i.Entry.Title, i.Status.State)));
    }

    [Fact]
    public void A_week_note_without_task_is_not_on()
    {
        var calendar = CalendarLayoutTests.CalendarOf("Work", ("/Work/Calendar/2026-W46.md", "# Week 46\n"));

        Assert.Empty(WhatsOn.Of([calendar], Saturday, Vienna));
        Assert.Equal(["2026-W46"], CalendarLayout.Week(calendar, 2026, 46, Saturday, Vienna).Spanning.Select(e => e.Entry.Title));
    }
}
