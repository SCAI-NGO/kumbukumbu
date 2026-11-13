// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Calendar;

public class EntryReaderTests
{
    private static readonly BoardDefinition Board = BoardDefinition.Parse(
        "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Done: [archivable]\n  - Rejected: [archivable, reason]\n---\n").Definition!;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);

    private static (Entry? Entry, CalendarProblem? Problem) Read(string path, string content, BoardDefinition? board = null) =>
        EntryReader.Read(new Note(P(path), content), board);

    private static Entry EntryOf(string path, string content, BoardDefinition? board = null)
    {
        var (entry, problem) = Read(path, content, board);
        Assert.Null(problem);
        return Assert.IsType<Entry>(entry);
    }

    private static CalendarProblem ProblemOf(string content, string path = "/Work/Zahnarzt.md")
    {
        var (entry, problem) = Read(path, content);
        Assert.Null(entry);
        return Assert.IsType<CalendarProblem>(problem);
    }

    [Fact]
    public void A_note_with_when_is_a_dated_entry()
    {
        var entry = EntryOf("/Work/Zahnarzt.md", "---\nwhen: 2026-11-20T14:00\n---\nBring the card.\n");

        Assert.Equal(EntryKind.Dated, entry.Kind);
        Assert.Equal(TaskMark.None, entry.Task);
        Assert.Equal("Zahnarzt", entry.Title);
        Assert.Equal("Work", entry.Vault);
        Assert.Equal(new CalendarTime(TimeKind.Time, new DateOnly(2026, 11, 20), new TimeOnly(14, 0)), entry.When);
        Assert.Null(entry.Until);
        Assert.Null(entry.Zone);
        Assert.Null(entry.Upcoming);
        Assert.False(entry.ByName);
        Assert.Null(entry.Column);
        Assert.False(entry.Spans);
    }

    [Fact]
    public void Task_open_makes_a_task()
    {
        var entry = EntryOf("/Work/Renew passport.md", "---\nwhen: 2026-11-20\ntask: open\nupcoming: 7d\ntz: Europe/Vienna\n---\n");

        Assert.Equal(EntryKind.Task, entry.Kind);
        Assert.Equal(TaskMark.Open, entry.Task);
        Assert.Equal(new Upcoming(7, 'd'), entry.Upcoming);
        Assert.Equal("Europe/Vienna", entry.Zone?.Id);
    }

    [Fact]
    public void A_closed_task_keeps_closed_and_reason()
    {
        var entry = EntryOf("/Work/Insurance.md",
            "---\nwhen: 2026-12-01\ntask: closed\nclosed: 2026-11-16\nreason: renewed until 2027\n---\n");

        Assert.Equal(EntryKind.Task, entry.Kind);
        Assert.Equal(TaskMark.Closed, entry.Task);
        Assert.Equal(new DateOnly(2026, 11, 16), entry.Closed);
        Assert.Equal("renewed until 2027", entry.Reason);
    }

    [Fact]
    public void A_day_note_by_name_anywhere()
    {
        var entry = EntryOf("/Work/Diary/Week 2026-46/2026-11-12.md", "Standup went well.\n");

        Assert.Equal(new CalendarTime(TimeKind.Day, new DateOnly(2026, 11, 12)), entry.When);
        Assert.True(entry.ByName);
        Assert.Equal(EntryKind.Dated, entry.Kind);
        Assert.Equal("2026-11-12", entry.Title);
    }

    [Fact]
    public void A_week_note_by_name()
    {
        var entry = EntryOf("/Work/Calendar/2026-W46.md", "");

        Assert.Equal(TimeKind.Week, entry.When.Kind);
        Assert.Equal(46, entry.When.IsoWeek);
        Assert.Equal(new DateOnly(2026, 11, 9), entry.When.FirstDay);
        Assert.True(entry.ByName);
        Assert.True(entry.Spans);
    }

    [Theory]
    [InlineData("/Work/2026-11-12 Standup.md")]
    [InlineData("/Work/2026-11 Budget.md")]
    [InlineData("/Work/2026-13-40.md")]
    [InlineData("/Work/2026-W54.md")]
    public void A_near_name_is_no_entry(string path)
    {
        Assert.Equal((null, null), Read(path, "Notes.\n"));
        Assert.False(EntryReader.IsDayOrWeekName(P(path).Name, out _));
    }

    [Fact]
    public void When_in_the_frontmatter_wins_over_the_name()
    {
        var entry = EntryOf("/Work/Calendar/Deep/Sub/2026-11-12.md", "---\nwhen: 2026-11-20\n---\n");

        Assert.Equal(new CalendarTime(TimeKind.Day, new DateOnly(2026, 11, 20)), entry.When);
        Assert.False(entry.ByName);
    }

    [Fact]
    public void A_day_note_may_carry_until_and_task()
    {
        var entry = EntryOf("/Work/2026-11-12.md", "---\nuntil: 2026-11-14\ntask: open\n---\n");

        Assert.Equal(new DateOnly(2026, 11, 12), entry.When.Date);
        Assert.Equal(new DateOnly(2026, 11, 14), entry.Until?.Date);
        Assert.Equal(TaskMark.Open, entry.Task);
        Assert.True(entry.Spans);
    }

    [Fact]
    public void Values_the_calendar_cannot_read_are_problems()
    {
        var when = ProblemOf("---\nwhen: tomorrow\n---\n");
        Assert.Equal("when: tomorrow is not a date; write 2026-11-20, 2026-11-20T14:00 or 2026-W47", when.What);
        Assert.Equal("`when: tomorrow` – I need a date like 2026-11-20", when.Plain);
        Assert.Equal("Zahnarzt", when.Title);
        Assert.Equal(P("/Work/Zahnarzt.md"), when.Path);

        var task = ProblemOf("---\nwhen: 2026-11-20\ntask: maybe\n---\n");
        Assert.Equal("task: maybe is neither open nor closed", task.What);
        Assert.Equal("`task: maybe` – I need open or closed", task.Plain);
        Assert.Equal(new Dictionary<string, string> { ["when"] = "2026-11-20" }, task.Readable);

        var before = ProblemOf("---\nwhen: 2026-11-20\nuntil: 2026-11-19\n---\n");
        Assert.Equal("until 2026-11-19 is before when 2026-11-20", before.What);
        Assert.Equal("The end, 19 Nov, is before the start, 20 Nov", before.Plain);

        var zone = ProblemOf("---\nwhen: 2026-11-20T14:00\ntz: Mars/Olympus\n---\n");
        Assert.Equal("tz: Mars/Olympus is not a time zone (an IANA name such as Europe/Vienna)", zone.What);
        Assert.Equal("`tz: Mars/Olympus` – I need a time zone like Europe/Vienna", zone.Plain);
        Assert.Equal(new Dictionary<string, string> { ["when"] = "2026-11-20T14:00" }, zone.Readable);

        var kinds = ProblemOf("---\nwhen: 2026-11-20T14:00\nuntil: 2026-11-20\n---\n");
        Assert.Equal("until is a day but when is a time; give both the same kind", kinds.What);

        var upcoming = ProblemOf("---\nwhen: 2026-11-20\nupcoming: 1m\n---\n");
        Assert.StartsWith("upcoming: 1m", upcoming.What);

        var alone = ProblemOf("---\nuntil: 2026-11-20\n---\n");
        Assert.StartsWith("until without when", alone.What);
    }

    [Fact]
    public void A_bad_value_on_a_day_note_is_a_problem_too()
    {
        var problem = ProblemOf("---\ntask: maybe\n---\n", "/Work/2026-11-12.md");

        Assert.Equal("task: maybe is neither open nor closed", problem.What);
    }

    [Fact]
    public void Empty_values_are_no_values()
    {
        Assert.Equal((null, null), Read("/Work/From a template.md", "---\nwhen:\ntask:\n---\n"));
    }

    [Fact]
    public void YAML_dates_and_timestamps_read_as_strings()
    {
        Assert.Equal(TimeKind.Day, EntryOf("/Work/a.md", "---\nwhen: 2026-11-20\n---\n").When.Kind);
        var quoted = EntryOf("/Work/b.md", "---\nwhen: \"2026-11-20T14:00\"\n---\n").When;
        Assert.Equal(TimeKind.Time, quoted.Kind);
        Assert.Equal(new TimeOnly(14, 0), quoted.Clock);
        Assert.Equal(TimeKind.Time, EntryOf("/Work/c.md", "---\nwhen: 2026-11-20T14:00\n---\n").When.Kind);
    }

    [Fact]
    public void A_card_with_due_is_a_task_whose_column_is_its_status()
    {
        var ideas = EntryOf("/Work/Board/Offsite.md", "---\ncolumn: Ideas\ndue: 2026-11-20\n---\n", Board);
        Assert.Equal(EntryKind.Card, ideas.Kind);
        Assert.Equal(TaskMark.Open, ideas.Task);
        Assert.Equal("Ideas", ideas.Column);
        Assert.Equal(new CalendarTime(TimeKind.Day, new DateOnly(2026, 11, 20)), ideas.When);

        var done = EntryOf("/Work/Board/Offsite.md", "---\ncolumn: done\ndue: 2026-11-20\n---\n", Board);
        Assert.Equal(TaskMark.Closed, done.Task);
        Assert.Equal("Done", done.Column);

        var rejected = EntryOf("/Work/Board/Offsite.md", "---\ncolumn: Rejected\ndue: 2026-11-20\nreason: not needed\n---\n", Board);
        Assert.Equal(TaskMark.Closed, rejected.Task);
        Assert.Equal("not needed", rejected.Reason);
    }

    [Fact]
    public void A_card_with_a_when_but_no_due_is_a_problem_and_a_when_beside_due_is_ignored()
    {
        Assert.Equal((null, null), Read("/Work/Board/Offsite.md", "---\ncolumn: Ideas\n---\n", Board));
        var (none, problem) = Read("/Work/Board/Offsite.md", "---\ncolumn: Ideas\nwhen: 2026-11-20\n---\n", Board);
        Assert.Null(none);
        Assert.Equal("on a card the date is its due; set it with the board tool", problem?.What);

        var entry = EntryOf("/Work/Board/Offsite.md", "---\ncolumn: Ideas\ndue: 2026-11-20\nwhen: 2026-12-24\n---\n", Board);
        Assert.Equal(new DateOnly(2026, 11, 20), entry.When.Date);
    }

    [Fact]
    public void A_card_whose_due_cannot_be_read_is_a_problem()
    {
        var (entry, problem) = Read("/Work/Board/Offsite.md", "---\ncolumn: Ideas\ndue: soon\ntask: open\n---\n", Board);

        Assert.Null(entry);
        Assert.StartsWith("due: soon is not a date", problem?.What);
        Assert.Contains("on a card the column is the status", problem?.What);
    }

    [Fact]
    public void A_value_shown_back_is_one_line_and_cut_short()
    {
        var (_, problem) = Read("/Work/x.md", $"---\nwhen: |\n  line one\n  line two {new string('x', 500)}\n---\n", null);

        Assert.DoesNotContain('\n', problem!.Plain);
        Assert.Contains("line one line two", problem.Plain);
        Assert.True(problem.Plain.Length < 140, problem.Plain);
    }

    [Fact]
    public void A_card_with_task_has_it_ignored_and_listed()
    {
        var (entry, problem) = Read("/Work/Board/Offsite.md", "---\ncolumn: Ideas\ndue: 2026-11-20\ntask: closed\n---\n", Board);

        Assert.Equal(TaskMark.Open, entry?.Task);
        Assert.Equal(EntryKind.Card, entry?.Kind);
        Assert.Equal("on a card the column is the status; move it with the board tool", problem?.What);
    }

    [Fact]
    public void Without_a_board_a_note_in_Board_is_an_ordinary_note()
    {
        var entry = EntryOf("/Work/Board/x.md", "---\ncolumn: Ideas\nwhen: 2026-11-20\n---\n");

        Assert.Equal(EntryKind.Dated, entry.Kind);
        Assert.Null(entry.Column);
    }

    [Fact]
    public void Broken_frontmatter_with_when_is_a_problem_not_lost()
    {
        var problem = ProblemOf("---\nwhen: 2026-11-20\ntags: [a\n---\n");
        Assert.StartsWith("the frontmatter does not parse: ", problem.What);
        Assert.Equal("I can't read this note's properties; fix them in the note", problem.Plain);

        ProblemOf("---\ntags: [a\n---\n", "/Work/2026-11-12.md");
        Assert.Equal((null, null), Read("/Work/Other.md", "---\ntags: [a\n---\n"));
    }

    [Fact]
    public void Archived_notes_are_left_out()
    {
        Assert.Equal((null, null), Read("/Work/_archive/x.md", "---\nwhen: 2026-11-20\n---\n"));
        Assert.Equal((null, null), Read("/Work/_archive/2026-11-12.md", ""));
        Assert.NotNull(Read("/Work/Inbox/_archive/x.md", "---\nwhen: 2026-11-20\n---\n").Entry);
    }

    [Fact]
    public void The_frontmatter_is_read_once_per_content()
    {
        var content = "---\nwhen: 2026-11-20\n---\n";

        Assert.Same(EntryReader.Parsed(content), EntryReader.Parsed(content));
        Assert.NotSame(EntryReader.Parsed(content), EntryReader.Parsed(new string(content.AsSpan())));
    }

    [Fact]
    public void A_card_of_a_board_that_cannot_be_read_is_a_problem()
    {
        var (entry, problem) = Read("/Work/Board/Offsite.md", "---\ncolumn: Ideas\ndue: 2026-11-20\n---\n", null);

        Assert.Null(entry);
        Assert.Contains("board that can't be read", problem?.What);
    }
}
