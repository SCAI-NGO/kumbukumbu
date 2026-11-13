// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Kumbu.Core.Tests.Sh;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Calendar;

public class CalendarServiceTests
{
    private static readonly TimeZoneInfo Vienna = Zones.Find("Europe/Vienna")!;

    private const string BoardDefinition =
        "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Doing\n  - Done: [archivable]\n  - Rejected: [archivable, reason]\n---\n# Work\n";

    // Wednesday, 16 December 2026, 09:00 in Vienna.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 12, 16, 8, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private CalendarService Service(InMemoryNoteStore store, ICaller? caller = null, TimeZoneInfo? zone = null)
    {
        caller ??= new FixedCaller(Writers.Dev);
        var writer = new NoteWriter(store, _time, caller);
        var mover = new NoteMover(store, _time, caller);
        return new CalendarService(store, writer, new BoardService(store, writer, mover, _time, caller), () => _time.GetUtcNow().UtcDateTime,
            new FixedZone(zone ?? Vienna));
    }

    private static async Task<string?> Content(InMemoryNoteStore store, string path) => await store.ReadAsync(P(path), Ct);

    private static void Ok(ShellResult result) => Assert.False(result.IsError, result.Output);

    [Fact]
    public async Task Set_writes_the_keys_in_order_after_existing_ones()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "---\ntitle: x\n---\nBody\n"));

        var result = await Service(store).SetAsync("/Work/x.md", new CalendarChange(When: "2026-12-15", Task: true, Upcoming: "7d"), Ct);

        Ok(result);
        Assert.Equal("---\ntitle: x\nwhen: 2026-12-15\ntask: open\nupcoming: 7d\n---\nBody\n", await Content(store, "/Work/x.md"));
        Assert.Equal("/Work/x.md", result.Version?.Path);
        Assert.StartsWith("calendar: ", result.Output);
    }

    [Fact]
    public async Task Set_on_a_note_without_frontmatter_starts_one_in_key_order()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "Body\n"));

        Ok(await Service(store).SetAsync("/Work/x.md",
            new CalendarChange(When: "2026-12-15T14:00", Until: "2026-12-15T15:00", Task: true, Upcoming: "2w", Tz: "Europe/London"), Ct));

        Assert.Equal("---\nwhen: 2026-12-15T14:00\nuntil: 2026-12-15T15:00\ntask: open\nupcoming: 2w\ntz: Europe/London\n---\nBody\n",
            await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Set_keeps_every_other_byte()
    {
        const string before = "---\n# a comment\n\"tags\": [a]\naliases:\n  - One\n  - Two\nwhen: 2026-12-01\n---\nLine one\r\nLine two\r\n";
        var store = new InMemoryNoteStore();
        store.Put(new StoredNote(P("/Work/x.md"), before, 1, Writers.Seed, InMemoryNoteStore.Seeded, [], before, Ref: WriterRef.Server));

        Ok(await Service(store).SetAsync("/Work/x.md", new CalendarChange(When: "2026-12-15", Task: true), Ct));

        Assert.Equal("---\n# a comment\n\"tags\": [a]\naliases:\n  - One\n  - Two\nwhen: 2026-12-15\ntask: open\n---\nLine one\r\nLine two\r\n",
            await Content(store, "/Work/x.md"));
    }

    [Theory]
    [InlineData("when", "tomorrow", "calendar: --when tomorrow is not a date; give 2026-11-20, 2026-11-20T14:00 or 2026-W47")]
    [InlineData("until", "soon", "calendar: --until soon is not a date; give 2026-11-20, 2026-11-20T14:00 or 2026-W47")]
    [InlineData("upcoming", "1 month", "calendar: --upcoming 1 month is not a span; give 7d, 2w or 12h")]
    [InlineData("tz", "Vienna", "calendar: --tz Vienna is not a time zone; give an IANA name such as Europe/Vienna")]
    public async Task Set_refuses_a_bad_value_with_what_is_valid(string key, string value, string error)
    {
        const string note = "---\nwhen: 2026-12-01\n---\n";
        var store = new InMemoryNoteStore(("/Work/x.md", note));
        var change = key switch
        {
            "when" => new CalendarChange(When: value),
            "until" => new CalendarChange(Until: value),
            "upcoming" => new CalendarChange(Upcoming: value),
            _ => new CalendarChange(Tz: value),
        };

        var result = await Service(store).SetAsync("/Work/x.md", change, Ct);

        Assert.Equal(ShellResult.Error(error) with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Equal(note, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Set_refuses_an_until_that_does_not_fit_when()
    {
        const string note = "---\nwhen: 2026-12-10\n---\n";
        var store = new InMemoryNoteStore(("/Work/x.md", note));
        var service = Service(store);

        var before = await service.SetAsync("/Work/x.md", new CalendarChange(Until: "2026-12-01"), Ct);
        Assert.True(before.IsError);
        Assert.Contains("is before when", before.Output);
        var kind = await service.SetAsync("/Work/x.md", new CalendarChange(Until: "2026-12-11T10:00"), Ct);
        Assert.Contains("give both the same kind", kind.Output);
        var alone = await service.SetAsync("/Work/y.md", new CalendarChange(Until: "2026-12-11"), Ct);
        Assert.Equal(ShellErrorKind.NotFound, alone.Kind);
        Assert.Equal(note, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Set_needs_a_date_for_a_note_without_one()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "Body\n"));

        var result = await Service(store).SetAsync("/Work/x.md", new CalendarChange(Task: true), Ct);

        Assert.Equal(ShellResult.Error("calendar: /Work/x.md has no date yet; give it one with --when") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Equal("Body\n", await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Values_are_written_unquoted_and_a_reason_quoted_when_needed()
    {
        var store = new InMemoryNoteStore(("/Work/a.md", ""), ("/Work/b.md", ""), ("/Work/c.md", "---\nwhen: 2026-12-01\ntask: open\n---\n"));
        var service = Service(store);

        Ok(await service.SetAsync("/Work/a.md", new CalendarChange(When: "2026-11-20 14:00", Upcoming: "P7D"), Ct));
        Ok(await service.SetAsync("/Work/b.md", new CalendarChange(When: "2026-W47"), Ct));
        Ok(await service.CloseAsync("/Work/c.md", "a: b", Ct));

        Assert.Equal("---\nwhen: 2026-11-20T14:00\nupcoming: 7d\n---\n", await Content(store, "/Work/a.md"));
        Assert.Equal("---\nwhen: 2026-W47\n---\n", await Content(store, "/Work/b.md"));
        var closed = (await Content(store, "/Work/c.md"))!;
        Assert.Contains("\nreason: \"a: b\"\n", closed);
        Assert.Equal("a: b", Frontmatter.Read(closed).Values["reason"]);
    }

    [Fact]
    public async Task A_time_with_an_offset_is_written_as_local_time_without_it()
    {
        var store = new InMemoryNoteStore(("/Work/a.md", ""), ("/Work/b.md", "---\ntz: Europe/London\nwhen: 2026-12-01\n---\n"));
        var service = Service(store);

        Ok(await service.SetAsync("/Work/a.md", new CalendarChange(When: "2026-12-20T13:00Z"), Ct));
        Ok(await service.MoveAsync("/Work/b.md", "2026-12-20T13:00+01:00", Ct));

        Assert.Equal("---\nwhen: 2026-12-20T14:00\n---\n", await Content(store, "/Work/a.md"));
        Assert.Equal("---\ntz: Europe/London\nwhen: 2026-12-20T12:00\n---\n", await Content(store, "/Work/b.md"));
    }

    [Fact]
    public async Task Close_writes_closed_today_and_the_reason()
    {
        var store = new InMemoryNoteStore(("/Work/Pump/Certificate.md", "---\nwhen: 2026-12-15\ntask: open\nupcoming: 14d\n---\nText\n"));

        var result = await Service(store).CloseAsync("/Work/Pump/Certificate.md", "renewed until 2027", Ct);

        Assert.Equal("calendar: closed /Work/Pump/Certificate.md (16 Dec)", result.Output);
        Assert.Equal("/Work/Pump/Certificate.md", result.Version?.Path);
        Assert.Equal(VersionToken.Of((await Content(store, "/Work/Pump/Certificate.md"))!), result.Version?.Token);
        Assert.Equal("---\nwhen: 2026-12-15\ntask: closed\nupcoming: 14d\nclosed: 2026-12-16\nreason: renewed until 2027\n---\nText\n",
            await Content(store, "/Work/Pump/Certificate.md"));
    }

    [Fact]
    public async Task Close_takes_today_in_the_callers_zone()
    {
        // 23:30 UTC on the 16th is already the 17th in Vienna.
        _time.SetUtcNow(new DateTimeOffset(2026, 12, 16, 23, 30, 0, TimeSpan.Zero));
        var store = new InMemoryNoteStore(("/Work/x.md", "---\nwhen: 2026-12-15\ntask: open\n---\n"));

        Ok(await Service(store).CloseAsync("/Work/x.md", null, Ct));

        Assert.Equal("---\nwhen: 2026-12-15\ntask: closed\nclosed: 2026-12-17\n---\n", await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Close_on_a_dated_note_hints_at_set_task()
    {
        const string note = "---\nwhen: 2026-12-15\n---\n";
        var store = new InMemoryNoteStore(("/Work/x.md", note));

        var result = await Service(store).CloseAsync("/Work/x.md", null, Ct);

        Assert.Equal(ShellResult.Error("calendar: /Work/x.md is not a to-do; calendar set <note> --task makes it one") with { Kind = ShellErrorKind.Invalid },
            result);
        Assert.Equal(note, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Reopen_removes_closed_and_reason()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "---\nwhen: 2026-12-15\ntask: closed\nclosed: 2026-12-16\nreason: done early\ntags: [a]\n---\nBody\n"));

        var result = await Service(store).ReopenAsync("/Work/x.md", Ct);

        Assert.Equal("calendar: reopened /Work/x.md", result.Output);
        Assert.Equal("---\nwhen: 2026-12-15\ntask: open\ntags: [a]\n---\nBody\n", await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task A_reason_can_be_added_later()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "---\nwhen: 2026-12-15\ntask: closed\nclosed: 2026-12-16\n---\n"),
            ("/Work/open.md", "---\nwhen: 2026-12-15\ntask: open\n---\n"));
        var service = Service(store);

        Ok(await service.SetAsync("/Work/x.md", new CalendarChange(Reason: "renewed"), Ct));
        var open = await service.SetAsync("/Work/open.md", new CalendarChange(Reason: "renewed"), Ct);

        Assert.Equal("---\nwhen: 2026-12-15\ntask: closed\nclosed: 2026-12-16\nreason: renewed\n---\n", await Content(store, "/Work/x.md"));
        Assert.Equal(ShellResult.Error("calendar: /Work/open.md is not closed; calendar close <note> --reason <text> closes it with one")
            with { Kind = ShellErrorKind.Invalid }, open);
    }

    [Fact]
    public async Task No_task_removes_task_closed_and_reason()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "---\nwhen: 2026-12-15\ntask: closed\nclosed: 2026-12-16\nreason: r\n---\n"));

        Ok(await Service(store).SetAsync("/Work/x.md", new CalendarChange(Task: false), Ct));

        Assert.Equal("---\nwhen: 2026-12-15\n---\n", await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Task_keeps_a_closed_task_closed()
    {
        const string note = "---\nwhen: 2026-12-15\ntask: closed\nclosed: 2026-12-16\n---\n";
        var store = new InMemoryNoteStore(("/Work/x.md", note));

        Ok(await Service(store).SetAsync("/Work/x.md", new CalendarChange(Task: true), Ct));

        Assert.Equal(note, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Move_keeps_a_windows_length_and_a_time_of_day()
    {
        var store = new InMemoryNoteStore(
            ("/Work/urlaub.md", "---\nwhen: 2026-12-01\nuntil: 2026-12-10\n---\n"),
            ("/Work/meeting.md", "---\nwhen: 2026-12-10T14:00\nuntil: 2026-12-10T15:30\n---\n"),
            ("/Work/weeks.md", "---\nwhen: 2026-W50\nuntil: 2026-W51\n---\n"),
            ("/Work/one.md", "---\nwhen: 2026-12-10\n---\n"));
        var service = Service(store);

        var result = await service.MoveAsync("/Work/urlaub.md", "2026-12-20", Ct);
        Ok(await service.MoveAsync("/Work/meeting.md", "2026-12-12T09:00", Ct));
        Ok(await service.MoveAsync("/Work/weeks.md", "2026-W53", Ct));
        Ok(await service.MoveAsync("/Work/one.md", "2026-12-11T08:00", Ct));

        Assert.Equal("calendar: moved /Work/urlaub.md to 20 Dec", result.Output);
        Assert.Equal("---\nwhen: 2026-12-20\nuntil: 2026-12-29\n---\n", await Content(store, "/Work/urlaub.md"));
        Assert.Equal("---\nwhen: 2026-12-12T09:00\nuntil: 2026-12-12T10:30\n---\n", await Content(store, "/Work/meeting.md"));
        Assert.Equal("---\nwhen: 2026-W53\nuntil: 2027-W01\n---\n", await Content(store, "/Work/weeks.md"));
        Assert.Equal("---\nwhen: 2026-12-11T08:00\n---\n", await Content(store, "/Work/one.md"));
    }

    [Fact]
    public async Task Move_of_a_window_keeps_its_kind()
    {
        const string note = "---\nwhen: 2026-12-01\nuntil: 2026-12-10\n---\n";
        var store = new InMemoryNoteStore(("/Work/urlaub.md", note));

        var result = await Service(store).MoveAsync("/Work/urlaub.md", "2026-W52", Ct);

        Assert.Equal(ShellErrorKind.Invalid, result.Kind);
        Assert.Equal("calendar: /Work/urlaub.md runs from day to day; move it to a day such as 2026-11-20", result.Output);
        Assert.Equal(note, await Content(store, "/Work/urlaub.md"));
    }

    [Fact]
    public async Task Unset_removes_every_calendar_key_and_keeps_the_note()
    {
        var store = new InMemoryNoteStore(("/Work/x.md",
            "---\ntitle: x\nwhen: 2026-12-15\nuntil: 2026-12-16\ntask: closed\nclosed: 2026-12-16\nreason: r\nupcoming: 7d\ntz: Europe/Vienna\ntags: [a]\n---\nBody\n"));

        var result = await Service(store).UnsetAsync("/Work/x.md", Ct);

        Assert.Equal("calendar: took /Work/x.md out of the calendar; the note stays", result.Output);
        Assert.Equal("---\ntitle: x\ntags: [a]\n---\nBody\n", await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Unset_refuses_a_day_note_by_name()
    {
        var store = new InMemoryNoteStore(("/Work/Diary/2026-11-12.md", "---\ntask: open\n---\n"), ("/Work/2026-W46.md", "x\n"));
        var service = Service(store);

        Assert.Equal(ShellResult.Error("calendar: /Work/Diary/2026-11-12.md is the day note of 12 November by its name; rename it to take it out of the calendar")
            with { Kind = ShellErrorKind.Invalid }, await service.UnsetAsync("/Work/Diary/2026-11-12.md", Ct));
        Assert.Equal(ShellResult.Error("calendar: /Work/2026-W46.md is the week note of week 46 by its name; rename it to take it out of the calendar")
            with { Kind = ShellErrorKind.Invalid }, await service.UnsetAsync("/Work/2026-W46.md", Ct));
        Assert.Equal("---\ntask: open\n---\n", await Content(store, "/Work/Diary/2026-11-12.md"));
    }

    private static InMemoryNoteStore Board(string card) =>
        new(("/Work/Board/Board.md", BoardDefinition), ("/Work/Board/X.md", card));

    private const string CardText = "---\ncolumn: Doing\nrank: V\ndue: 2026-12-10\n---\nText\n";

    [Fact]
    public async Task On_a_card_close_reopen_move_and_set_refuse_and_name_the_board_tool()
    {
        var store = Board(CardText);
        var service = Service(store);
        var status = ShellResult.Error("calendar: /Work/Board/X.md is a card; on a card the column is the status: move it with the board tool")
            with { Kind = ShellErrorKind.Invalid };
        var due = ShellResult.Error("calendar: /Work/Board/X.md is a card; its date is its due: change it with the board tool")
            with { Kind = ShellErrorKind.Invalid };

        Assert.Equal(status, await service.CloseAsync("/Work/Board/X.md", null, Ct));
        Assert.Equal(status, await service.ReopenAsync("/Work/Board/X.md", Ct));
        Assert.Equal(due, await service.MoveAsync("/Work/Board/X.md", "2026-12-20", Ct));
        Assert.Equal(due, await service.SetAsync("/Work/Board/X.md", new CalendarChange(When: "2026-12-20"), Ct));
        Assert.Equal(status, await service.SetAsync("/Work/Board/X.md", new CalendarChange(Task: true), Ct));
        Assert.Equal(status, await service.SetAsync("/Work/Board/X.md", new CalendarChange(Upcoming: "7d", Reason: "r"), Ct));
        Assert.Equal(due, await service.UnsetAsync("/Work/Board/X.md", Ct));
        Assert.Equal(CardText, await Content(store, "/Work/Board/X.md"));
    }

    [Fact]
    public async Task Set_upcoming_on_a_card_is_allowed()
    {
        var store = Board(CardText);

        Ok(await Service(store).SetAsync("/Work/Board/X.md", new CalendarChange(Upcoming: "7d"), Ct));

        Assert.Equal("---\ncolumn: Doing\nrank: V\ndue: 2026-12-10\nupcoming: 7d\n---\nText\n", await Content(store, "/Work/Board/X.md"));
    }

    [Fact]
    public async Task Later_counts_from_the_later_of_today_and_when()
    {
        var store = new InMemoryNoteStore(
            ("/Work/overdue.md", "---\nwhen: 2026-12-10\ntask: open\n---\n"),
            ("/Work/upcoming.md", "---\nwhen: 2026-12-20\ntask: open\n---\n"),
            ("/Work/timed.md", "---\nwhen: 2026-12-20T14:00\ntask: open\n---\n"),
            ("/Work/overdue-timed.md", "---\nwhen: 2026-12-01T14:00\ntask: open\n---\n"),
            ("/Work/week.md", "---\nwhen: 2026-W51\ntask: open\n---\n"),
            ("/Work/old-week.md", "---\nwhen: 2026-W40\ntask: open\n---\n"));
        var service = Service(store);

        var result = await service.LaterAsync("/Work/overdue.md", LaterPart.Postpone, LaterStep.Day, null, Ct);
        Ok(await service.LaterAsync("/Work/upcoming.md", LaterPart.Postpone, LaterStep.Week, null, Ct));
        Ok(await service.LaterAsync("/Work/timed.md", LaterPart.Postpone, LaterStep.Day, null, Ct));
        Ok(await service.LaterAsync("/Work/overdue-timed.md", LaterPart.Postpone, LaterStep.Day, null, Ct));
        Ok(await service.LaterAsync("/Work/week.md", LaterPart.Postpone, LaterStep.Week, null, Ct));
        Ok(await service.LaterAsync("/Work/old-week.md", LaterPart.Postpone, LaterStep.Week, null, Ct));
        var day = await service.LaterAsync("/Work/week.md", LaterPart.Postpone, LaterStep.Day, null, Ct);

        Assert.Equal("calendar: moved /Work/overdue.md to 17 Dec", result.Output);
        Assert.Equal("---\nwhen: 2026-12-17\ntask: open\n---\n", await Content(store, "/Work/overdue.md"));
        Assert.Equal("---\nwhen: 2026-12-27\ntask: open\n---\n", await Content(store, "/Work/upcoming.md"));
        Assert.Equal("---\nwhen: 2026-12-21T14:00\ntask: open\n---\n", await Content(store, "/Work/timed.md"));
        Assert.Equal("---\nwhen: 2026-12-17T14:00\ntask: open\n---\n", await Content(store, "/Work/overdue-timed.md"));
        Assert.Equal("---\nwhen: 2026-W52\ntask: open\n---\n", await Content(store, "/Work/week.md"));
        Assert.Equal("---\nwhen: 2026-W52\ntask: open\n---\n", await Content(store, "/Work/old-week.md"));
        Assert.Equal(ShellResult.Error("calendar: /Work/week.md is a week; +1 week moves it, +1 day is not offered") with { Kind = ShellErrorKind.Invalid }, day);
    }

    [Fact]
    public async Task Later_to_a_picked_date_keeps_the_time_of_day()
    {
        var store = new InMemoryNoteStore(("/Work/timed.md", "---\nwhen: 2026-12-20T14:00\ntask: open\n---\n"),
            ("/Work/week.md", "---\nwhen: 2026-W51\ntask: open\n---\n"));
        var service = Service(store);

        Ok(await service.LaterAsync("/Work/timed.md", LaterPart.Postpone, null, "2027-01-04", Ct));
        Ok(await service.LaterAsync("/Work/week.md", LaterPart.Postpone, null, "2027-01-06", Ct));

        Assert.Equal("---\nwhen: 2027-01-04T14:00\ntask: open\n---\n", await Content(store, "/Work/timed.md"));
        Assert.Equal("---\nwhen: 2027-W01\ntask: open\n---\n", await Content(store, "/Work/week.md"));
    }

    [Fact]
    public async Task More_time_moves_only_until()
    {
        const string note = "---\nwhen: 2026-12-01\nuntil: 2026-12-10\ntask: open\n---\n";
        var more = new InMemoryNoteStore(("/Work/x.md", note), ("/Work/single.md", "---\nwhen: 2026-12-01\ntask: open\n---\n"));
        var postpone = new InMemoryNoteStore(("/Work/x.md", note));

        Ok(await Service(more).LaterAsync("/Work/x.md", LaterPart.MoreTime, LaterStep.Week, null, Ct));
        Ok(await Service(postpone).LaterAsync("/Work/x.md", LaterPart.Postpone, LaterStep.Day, null, Ct));
        var single = await Service(more).LaterAsync("/Work/single.md", LaterPart.MoreTime, LaterStep.Day, null, Ct);

        Assert.Equal("---\nwhen: 2026-12-01\nuntil: 2026-12-23\ntask: open\n---\n", await Content(more, "/Work/x.md"));
        Assert.Equal("---\nwhen: 2026-12-17\nuntil: 2026-12-26\ntask: open\n---\n", await Content(postpone, "/Work/x.md"));
        Assert.Equal(ShellErrorKind.Invalid, single.Kind);
    }

    [Fact]
    public async Task Later_on_a_card_sets_its_due_through_the_board()
    {
        var store = Board(CardText);
        var service = Service(store);

        var result = await service.LaterAsync("/Work/Board/X.md", LaterPart.Postpone, LaterStep.Day, null, Ct);
        var more = await service.LaterAsync("/Work/Board/X.md", LaterPart.MoreTime, LaterStep.Day, null, Ct);

        Assert.Equal("calendar: moved /Work/Board/X.md to 17 Dec", result.Output);
        Assert.Equal("/Work/Board/X.md", result.Version?.Path);
        Assert.Equal("---\ncolumn: Doing\nrank: V\ndue: 2026-12-17\n---\nText\n", await Content(store, "/Work/Board/X.md"));
        Assert.Equal(ShellErrorKind.Invalid, more.Kind);
    }

    [Fact]
    public async Task A_read_only_member_can_close_nothing()
    {
        const string note = "---\nwhen: 2026-12-15\ntask: open\n---\n";
        var store = new InMemoryNoteStore(("/Garten/x.md", note));
        store.Infos["Garten"] = new VaultInfo("\"Alice\"", AccessLevel.Read, true);
        var service = Service(store, new FixedCaller("bob", LevelOf: _ => AccessLevel.Read));

        var result = await service.CloseAsync("/Garten/x.md", null, Ct);
        var created = await service.AddAsync("Garten", "Beete", "2026-12-20", null, false, null, Ct);

        Assert.True(result.IsError);
        Assert.Equal(ShellErrorKind.Refused, result.Kind);
        Assert.StartsWith("calendar: ", result.Output);
        Assert.Equal(ShellErrorKind.Refused, created.Kind);
        Assert.Equal(note, await Content(store, "/Garten/x.md"));
        Assert.Single(await store.ListNotesUnderAsync(VaultPath.Root, Ct));
    }

    [Fact]
    public async Task A_concurrent_write_is_retried_not_lost()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "---\nwhen: 2026-12-15\ntask: open\n---\nText\n"));
        store.Interfere = n => n with { Content = n.Content.Replace("Text", "Synced text", StringComparison.Ordinal) };

        Ok(await Service(store).CloseAsync("/Work/x.md", null, Ct));

        Assert.Null(store.Interfere);
        Assert.Equal("---\nwhen: 2026-12-15\ntask: closed\nclosed: 2026-12-16\n---\nSynced text\n", await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task A_note_that_keeps_changing_is_a_conflict()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "---\nwhen: 2026-12-15\ntask: open\n---\n"));
        store.Contended = _ => true;

        var result = await Service(store).CloseAsync("/Work/x.md", null, Ct);

        Assert.Equal(ShellResult.Error("calendar: /Work/x.md kept changing; try again") with { Kind = ShellErrorKind.Conflict }, result);
    }

    [Fact]
    public async Task A_missing_note_is_not_found()
    {
        var result = await Service(new InMemoryNoteStore(("/Work/x.md", ""))).CloseAsync("/Work/nope.md", null, Ct);

        Assert.Equal(ShellResult.Error("calendar: /Work/nope.md: No such note") with { Kind = ShellErrorKind.NotFound }, result);
    }

    [Fact]
    public async Task Today_finds_the_day_note_anywhere_or_creates_it()
    {
        var empty = new InMemoryNoteStore(("/Work/x.md", ""));
        var created = await Service(empty).TodayAsync("Work", Ct);

        Ok(created);
        Assert.Equal("/Work/Calendar/2026-12-16.md", created.Version?.Path);
        Assert.Equal("# Wednesday, 16 December 2026\n\n", await Content(empty, "/Work/Calendar/2026-12-16.md"));

        var diary = new InMemoryNoteStore(("/Work/Diary/2026-12-16.md", "Mine\n"), ("/Work/_archive/2026-12-16.md", "Old\n"));
        var found = await Service(diary).TodayAsync("Work", Ct);

        Ok(found);
        Assert.Equal("/Work/Diary/2026-12-16.md", found.Version?.Path);
        Assert.Equal(2, (await diary.ListNotesUnderAsync(VaultPath.Root, Ct)).Count);
    }

    [Fact]
    public async Task Today_with_several_day_notes_takes_the_last_changed()
    {
        var store = new InMemoryNoteStore();
        store.Put(new StoredNote(P("/Work/A/2026-12-16.md"), "a\n", 1, Writers.Seed, InMemoryNoteStore.Seeded.AddDays(2), [], "a\n", Ref: WriterRef.Server));
        store.Put(new StoredNote(P("/Work/B/2026-12-16.md"), "b\n", 1, Writers.Seed, InMemoryNoteStore.Seeded.AddDays(5), [], "b\n", Ref: WriterRef.Server));
        store.Put(new StoredNote(P("/Work/C/2026-12-16.md"), "c\n", 1, Writers.Seed, InMemoryNoteStore.Seeded, [], "c\n", Ref: WriterRef.Server));

        Assert.Equal("/Work/B/2026-12-16.md", (await Service(store).TodayAsync("Work", Ct)).Version?.Path);
    }

    [Fact]
    public async Task This_week_creates_the_week_note_with_its_heading()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", ""));

        var result = await Service(store).ThisWeekAsync("Work", Ct);

        Assert.Equal("/Work/Calendar/2026-W51.md", result.Version?.Path);
        Assert.Equal("# Week 51, 14–20 December 2026\n\n", await Content(store, "/Work/Calendar/2026-W51.md"));
    }

    [Theory]
    [InlineData("2026-10-28T09:00:00Z", "2026-W44", "# Week 44, 26 October – 1 November 2026\n\n")]
    [InlineData("2026-12-30T09:00:00Z", "2026-W53", "# Week 53, 28 December 2026 – 3 January 2027\n\n")]
    public async Task A_week_across_months_or_years_names_both(string now, string week, string heading)
    {
        _time.AdjustTime(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
        var store = new InMemoryNoteStore(("/Work/x.md", ""));

        Ok(await Service(store).ThisWeekAsync("Work", Ct));

        Assert.Equal(heading, await Content(store, $"/Work/Calendar/{week}.md"));
    }

    [Fact]
    public async Task Add_creates_an_entry_in_Calendar_named_by_its_title()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", ""));
        var service = Service(store);

        var result = await service.AddAsync("Work", "  Zahnarzt ", "2026-12-17", "14:00", false, null, Ct);
        Ok(await service.AddAsync("Work", "Certificate", "2026-12-20", null, true, "14d", Ct));

        Assert.Equal("calendar: created /Work/Calendar/Zahnarzt.md (17 Dec 14:00)", result.Output);
        Assert.Equal("/Work/Calendar/Zahnarzt.md", result.Version?.Path);
        Assert.Equal("---\nwhen: 2026-12-17T14:00\n---\n# Zahnarzt\n", await Content(store, "/Work/Calendar/Zahnarzt.md"));
        Assert.Equal("---\nwhen: 2026-12-20\ntask: open\nupcoming: 14d\n---\n# Certificate\n", await Content(store, "/Work/Calendar/Certificate.md"));
    }

    [Fact]
    public async Task A_clash_gives_Title_2()
    {
        var store = new InMemoryNoteStore(("/Work/Calendar/Zahnarzt.md", "old\n"), ("/Work/Calendar/zahnarzt 2.md", "old\n"));

        var result = await Service(store).AddAsync("Work", "Zahnarzt", "2026-12-17", null, false, null, Ct);

        Assert.Equal("/Work/Calendar/Zahnarzt 3.md", result.Version?.Path);
        Assert.Equal("old\n", await Content(store, "/Work/Calendar/Zahnarzt.md"));
    }

    [Theory]
    [InlineData("../Board/x")]
    [InlineData("a/b")]
    [InlineData(".hidden")]
    public async Task A_title_never_leaves_Calendar(string title)
    {
        var store = new InMemoryNoteStore(("/Work/x.md", ""));

        var result = await Service(store).AddAsync("Work", title, "2026-12-17", null, false, null, Ct);

        Assert.Equal(ShellResult.Error("calendar: a title can't contain / or start with a dot") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Single(await store.ListNotesUnderAsync(VaultPath.Root, Ct));
    }

    [Fact]
    public async Task A_title_is_normalised_and_checked_as_a_name()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", ""));
        var service = Service(store);

        Ok(await service.AddAsync("Work", "Café", "2026-12-17", null, false, null, Ct));
        var bad = await service.AddAsync("Work", "a:b", "2026-12-17", null, false, null, Ct);
        var day = await service.AddAsync("Work", "x", "17.12.2026", null, false, null, Ct);

        Assert.NotNull(await Content(store, "/Work/Calendar/Café.md"));
        Assert.Equal(ShellErrorKind.Invalid, bad.Kind);
        Assert.Equal(ShellResult.Error("calendar: 17.12.2026 is not a day; give 2026-11-20") with { Kind = ShellErrorKind.Invalid }, day);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("[Doing]")]
    public async Task A_card_is_a_card_by_any_column_value(string column)
    {
        var card = $"---\ncolumn: {column}\ndue: 2026-12-10\nwhen: 2026-12-10\ntask: open\n---\nText\n";
        var store = Board(card);

        var result = await Service(store).CloseAsync("/Work/Board/X.md", null, Ct);

        Assert.Equal(ShellResult.Error("calendar: /Work/Board/X.md is a card; on a card the column is the status: move it with the board tool")
            with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Equal(card, await Content(store, "/Work/Board/X.md"));
    }

    [Fact]
    public async Task Move_refuses_an_until_beyond_the_years_a_calendar_has()
    {
        const string note = "---\nwhen: 2026-11-20\nuntil: 9998-12-01\n---\n";
        var store = new InMemoryNoteStore(("/Work/x.md", note));

        var result = await Service(store).MoveAsync("/Work/x.md", "2030-11-20", Ct);

        Assert.Equal(ShellResult.Error("calendar: a calendar has the years 2 to 9998") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Equal(note, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Set_tz_writes_the_zones_own_id()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "---\nwhen: 2026-12-01\n---\n"));
        var tz = Zones.Find("europe/vienna") is null ? "Europe/Vienna" : "europe/vienna";

        var result = await Service(store).SetAsync("/Work/x.md", new CalendarChange(Tz: $" {tz} "), Ct);

        Assert.Equal("calendar: set tz Europe/Vienna on /Work/x.md", result.Output);
        Assert.Equal("---\nwhen: 2026-12-01\ntz: Europe/Vienna\n---\n", await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task A_read_only_member_can_postpone_nothing()
    {
        const string card = "---\ncolumn: Doing\nrank: V\ndue: 2026-12-10\n---\nText\n";
        const string note = "---\nwhen: 2026-12-15\ntask: open\n---\n";
        var store = new InMemoryNoteStore(("/Garten/Board/Board.md", BoardDefinition), ("/Garten/Board/X.md", card), ("/Garten/x.md", note));
        store.Infos["Garten"] = new VaultInfo("\"Alice\"", AccessLevel.Read, true);
        var service = Service(store, new FixedCaller("bob", LevelOf: _ => AccessLevel.Read));

        var onCard = await service.LaterAsync("/Garten/Board/X.md", LaterPart.Postpone, LaterStep.Day, null, Ct);
        var onNote = await service.LaterAsync("/Garten/x.md", LaterPart.Postpone, LaterStep.Day, null, Ct);
        var missing = await service.LaterAsync("/Garten/nope.md", LaterPart.Postpone, LaterStep.Day, null, Ct);

        Assert.Equal(ShellErrorKind.Refused, onCard.Kind);
        Assert.StartsWith("calendar: ", onCard.Output);
        Assert.Equal(ShellErrorKind.Refused, onNote.Kind);
        Assert.Equal(ShellErrorKind.Refused, missing.Kind);
        Assert.Equal(card, await Content(store, "/Garten/Board/X.md"));
        Assert.Equal(note, await Content(store, "/Garten/x.md"));
    }

    [Fact]
    public async Task Unset_takes_an_emptied_crlf_block_away()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "---\r\nwhen: 2026-11-20\r\n---\r\nBody\r\n"), ("/Work/empty.md", "---\r\n---\r\nBody\r\n"));
        var service = Service(store);

        Ok(await service.UnsetAsync("/Work/x.md", Ct));
        Ok(await service.UnsetAsync("/Work/empty.md", Ct));

        Assert.Equal("Body\r\n", await Content(store, "/Work/x.md"));
        Assert.Equal("---\r\n---\r\nBody\r\n", await Content(store, "/Work/empty.md"));
    }

    [Fact]
    public async Task Set_keeps_a_crlf_block_and_replaces_a_quoted_key()
    {
        var store = new InMemoryNoteStore(("/Work/crlf.md", "---\r\ntitle: x\r\nwhen: 2026-11-20\r\n---\r\n"),
            ("/Work/quoted.md", "---\n\"when\": 2026-11-20\n---\n"));
        var service = Service(store);

        Ok(await service.SetAsync("/Work/crlf.md", new CalendarChange(Task: true), Ct));
        Ok(await service.SetAsync("/Work/quoted.md", new CalendarChange(When: "2026-12-01"), Ct));

        var crlf = (await Content(store, "/Work/crlf.md"))!;
        Assert.StartsWith("---\r\ntitle: x\r\n", crlf);
        Assert.Equal("open", Frontmatter.Read(crlf).Values["task"]);
        Assert.Equal("---\nwhen: 2026-12-01\n---\n", await Content(store, "/Work/quoted.md"));
    }

    [Fact]
    public async Task A_reason_is_echoed_on_one_line_and_cut_short()
    {
        var reason = "first line\nsecond line " + new string('x', 100);
        var store = new InMemoryNoteStore(("/Work/a.md", "---\nwhen: 2026-12-15\ntask: closed\nclosed: 2026-12-16\n---\n"),
            ("/Work/b.md", "---\nwhen: 2026-12-15\ntask: closed\nclosed: 2026-12-16\n---\n"));
        var service = Service(store);
        var shown = ("first line second line " + new string('x', 100))[..80] + "…";

        var set = await service.SetAsync("/Work/a.md", new CalendarChange(Reason: reason), Ct);
        var close = await service.CloseAsync("/Work/b.md", reason, Ct);

        Assert.Equal($"calendar: set reason {shown} on /Work/a.md", set.Output);
        Assert.Equal($"calendar: gave /Work/b.md the reason {shown}", close.Output);
        Assert.Equal(reason, Frontmatter.Read((await Content(store, "/Work/a.md"))!).Values["reason"]);
    }
}
