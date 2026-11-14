// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>The caller's zone, as the server would resolve it for her.</summary>
public sealed class FixedZone(TimeZoneInfo zone) : ICallerZone
{
    public Task<TimeZoneInfo> ZoneAsync(CancellationToken ct) => Task.FromResult(zone);
}

public class CalendarCommandTests
{
    private static readonly TimeZoneInfo Vienna = Zones.Find("Europe/Vienna")!;

    // Thursday, 12 November 2026, 10:00 in Vienna.
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc));

    private static readonly (string, string)[] Week46 =
    [
        ("/Work/Urlaub.md", "---\nwhen: 2026-11-03\nuntil: 2026-11-14\n---\n"),
        ("/Work/Calendar/2026-W46.md", "# Week 46\n"),
        ("/Work/Zahnarzt.md", "---\nwhen: 2026-11-10T14:00\n---\n"),
        ("/Work/Calendar/2026-11-12.md", "# Thursday\n"),
        ("/Work/Release planning.md", "---\nwhen: 2026-11-15\ntask: open\nupcoming: 7d\n---\n"),
        ("/Work/Plan.md", "---\nwhen: tomorrow\n---\n"),
    ];

    private const string Week46Output = """
        Week 46 · 9–15 November 2026 · /Work
        Spanning
          Tue 3 – Sat 14 Nov  Urlaub  /Work/Urlaub.md  ongoing · until Sat
          Week 46  2026-W46  /Work/Calendar/2026-W46.md  week note · ongoing · until Sun
        Mon 9 Nov
        Tue 10 Nov
          14:00  Zahnarzt  /Work/Zahnarzt.md
        Wed 11 Nov
        Thu 12 Nov
          2026-11-12  /Work/Calendar/2026-11-12.md  day note · ongoing · today
        Fri 13 Nov
        Sat 14 Nov
        Sun 15 Nov
          Release planning  /Work/Release planning.md  to do · upcoming · in 3 days
        Problems (1)
          /Work/Plan.md: when: tomorrow is not a date; write 2026-11-20, 2026-11-20T14:00 or 2026-W47
          calendar set <note> --when <value> writes a valid one; edit fixes the rest
        """;

    private (Shell Shell, InMemoryNoteStore Store, NoteWriter Writer) Make(TimeZoneInfo zone, params (string, string)[] notes)
    {
        var store = new InMemoryNoteStore(notes);
        var writer = new NoteWriter(store, _time, new FixedCaller(Writers.Dev));
        var shell = new Shell(store, writer, new NoteMover(store, _time, new FixedCaller(Writers.Dev)), zone: new FixedZone(zone));
        return (shell, store, writer);
    }

    private (Shell Shell, InMemoryNoteStore Store, NoteWriter Writer) Make(params (string, string)[] notes) => Make(Vienna, notes);

    private static Task<ShellResult> Run(Shell shell, string line) => shell.RunAsync(line, CancellationToken.None);

    [Fact]
    public async Task Prints_the_current_week_of_the_callers_zone()
    {
        var (shell, _, _) = Make(Week46);

        Assert.Equal(ShellResult.Ok(Week46Output), await Run(shell, "calendar /Work"));
    }

    [Fact]
    public async Task Week_46_is_of_the_current_iso_year()
    {
        var (shell, _, _) = Make(Week46);

        Assert.Equal(ShellResult.Ok(Week46Output), await Run(shell, "calendar /Work --week 46"));
        Assert.StartsWith("Week 45 · 2–8 November 2026 · /Work\nSpanning\n  Tue 3 – Sat 14 Nov  Urlaub", (await Run(shell, "calendar /Work --week 45")).Output);
        Assert.Equal(ShellResult.Error("calendar: week 54 doesn't exist in 2026 (it has 53)"), await Run(shell, "calendar /Work --week 54"));
        Assert.Equal(ShellResult.Error("calendar: week 0 doesn't exist in 2026 (it has 53)"), await Run(shell, "calendar /Work --week 0"));
    }

    [Fact]
    public async Task Week_accepts_2026_W46()
    {
        var (shell, _, _) = Make(Week46);

        Assert.Equal(ShellResult.Ok(Week46Output), await Run(shell, "calendar /Work --week 2026-W46"));
        Assert.Equal(ShellResult.Error("calendar: week 53 doesn't exist in 2027 (it has 52)"), await Run(shell, "calendar /Work --week 2027-W53"));
        Assert.Equal(ShellResult.Error("calendar: --week takes a week such as 46 or 2026-W46, not next"), await Run(shell, "calendar /Work --week next"));
    }

    [Theory]
    [InlineData("--week 9999-W01")]
    [InlineData("--week 0001-W01")]
    [InlineData("--month 9999-01")]
    [InlineData("--month 0001-12")]
    public async Task Years_outside_2_to_9998_are_refused(string option)
    {
        var (shell, _, _) = Make(Week46);

        Assert.Equal(ShellResult.Error("calendar: a calendar has the years 2 to 9998"), await Run(shell, $"calendar /Work {option}"));
    }

    [Fact]
    public async Task Weeks_across_the_years_end()
    {
        // Saturday, 2 January 2027: still in week 53 of the ISO year 2026.
        _time.SetUtcNow(new DateTime(2027, 1, 2, 12, 0, 0, DateTimeKind.Utc));
        var (shell, _, _) = Make(("/Work/New year.md", "---\nwhen: 2027-01-01\n---\n"));

        var week = (await Run(shell, "calendar /Work")).Output;
        Assert.StartsWith("Week 53 · 28 December 2026 – 3 January 2027 · /Work\nMon 28 Dec\n", week);
        Assert.Contains("\nFri 1 Jan\n  New year  /Work/New year.md\nSat 2 Jan\n", week);
        Assert.StartsWith("Week 1 · 29 December 2025 – 4 January 2026 · /Work\n", (await Run(shell, "calendar /Work --week 1")).Output);
        Assert.StartsWith("Week 1 · 4–10 January 2027 · /Work\n", (await Run(shell, "calendar /Work --week 2027-W01")).Output);
    }

    [Fact]
    public async Task The_end_of_Sunday_in_the_callers_zone()
    {
        // Sunday 15 November, 23:30 UTC: already Monday 00:30 in Vienna.
        _time.SetUtcNow(new DateTime(2026, 11, 15, 23, 30, 0, DateTimeKind.Utc));

        var (vienna, _, _) = Make(Week46);
        Assert.StartsWith("Week 47 · 16–22 November 2026 · /Work\n", (await Run(vienna, "calendar /Work")).Output);

        var (utc, _, _) = Make(TimeZoneInfo.Utc, Week46);
        Assert.StartsWith("Week 46 · 9–15 November 2026 · /Work\n", (await Run(utc, "calendar /Work")).Output);
    }

    [Fact]
    public async Task Prints_a_month_as_its_weeks()
    {
        var (shell, _, _) = Make(
            ("/Work/Urlaub.md", "---\nwhen: 2026-11-03\nuntil: 2026-11-14\n---\n"),
            ("/Work/Zahnarzt.md", "---\nwhen: 2026-11-10T14:00\n---\n"));

        Assert.Equal(ShellResult.Ok("""
            November 2026 · /Work
            Week 44 · 26 Oct – 1 Nov
              nothing yet
            Week 45 · 2–8 Nov
              Tue 3 – Sat 14 Nov  Urlaub  /Work/Urlaub.md  ongoing · until Sat
            Week 46 · 9–15 Nov
              Tue 3 – Sat 14 Nov  Urlaub  /Work/Urlaub.md  ongoing · until Sat
              Tue 10 Nov
                14:00  Zahnarzt  /Work/Zahnarzt.md
            Week 47 · 16–22 Nov
              nothing yet
            Week 48 · 23–29 Nov
              nothing yet
            Week 49 · 30 Nov – 6 Dec
              nothing yet
            """), await Run(shell, "calendar /Work --month 2026-11"));
    }

    [Theory]
    [InlineData("calendar /Work --month 2026-13", "calendar: --month takes a month such as 2026-11, not 2026-13")]
    [InlineData("calendar /Work --month november", "calendar: --month takes a month such as 2026-11, not november")]
    [InlineData("calendar /Work --week 46 --month 2026-11", "calendar: --week and --month can't go together; give one")]
    [InlineData("calendar", "calendar: missing operand (calendar <vault>, or calendar notifications)")]
    [InlineData("calendar /Work /Home", "calendar: too many arguments (quote paths and sections that contain spaces: cat \"/Work/My Note.md#Next steps\")")]
    [InlineData("calendar /Work/Plan.md", "calendar: give a vault, e.g. calendar /Work")]
    [InlineData("calendar /Nope", "calendar: /Nope: No such file or directory")]
    [InlineData("calendar notifications --week 46", "calendar: unrecognized option '--week'")]
    [InlineData("calendar /Work --version 000000000000", "calendar: --version names a version of one note; a calendar is read as it is now, or from a checkpoint")]
    public async Task Errors(string line, string error)
    {
        var (shell, _, _) = Make(Week46);

        Assert.Equal(ShellResult.Error(error), await Run(shell, line));
    }

    [Fact]
    public async Task Times_in_another_zone_name_it_and_kinds_say_what_an_entry_is()
    {
        var (shell, _, _) = Make(
            ("/Work/Board/Board.md", "---\nkumbu-board: 1\ncolumns:\n  - Doing\n  - Done: [archivable]\n---\n# Work\n"),
            ("/Work/Board/Export.md", "---\ncolumn: Doing\nrank: a\ndue: 2026-11-13\n---\n"),
            ("/Work/Call London.md", "---\nwhen: 2026-11-13T14:00\ntz: Europe/London\n---\n"),
            ("/Work/Call UTC.md", "---\nwhen: 2026-11-13T09:00Z\n---\n"),
            ("/Work/Meeting.md", "---\nwhen: 2026-11-13T11:00\nuntil: 2026-11-13T12:30\n---\n"),
            ("/Work/Certificate.md", "---\nwhen: 2026-11-11\ntask: closed\nclosed: 2026-11-11\nreason: renewed until 2027\n---\n"),
            ("/Work/Invoice.md", "---\nwhen: 2026-11-11\ntask: open\n---\n"),
            ("/Work/Trip.md", "---\nwhen: 2026-11-10T18:00\nuntil: 2026-11-12T20:00\ntz: Europe/London\n---\n"));

        Assert.Equal(ShellResult.Ok("""
            Week 46 · 9–15 November 2026 · /Work
            Spanning
              Tue 10 Nov 18:00 – Thu 12 Nov 20:00 (Europe/London)  Trip  /Work/Trip.md  ongoing · until 20:00 (Europe/London)
            Mon 9 Nov
            Tue 10 Nov
            Wed 11 Nov
              Certificate  /Work/Certificate.md  closed 11 Nov · renewed until 2027
              Invoice  /Work/Invoice.md  to do · overdue · 1 day
            Thu 12 Nov
            Fri 13 Nov
              Export  /Work/Board/Export.md  card · Doing
              10:00  Call UTC  /Work/Call UTC.md
              11:00–12:30  Meeting  /Work/Meeting.md
              14:00 (Europe/London)  Call London  /Work/Call London.md
            Sat 14 Nov
            Sun 15 Nov
            """), await Run(shell, "calendar /Work"));
    }

    [Fact]
    public async Task Tag_narrows_whole_notes()
    {
        var (shell, _, _) = Make(
            ("/Work/Report.md", "---\nwhen: 2026-11-13\nupcoming: 2d\n---\nFor #kumbu\n"),
            ("/Work/Review.md", "---\nwhen: 2026-11-13\ntags: [kumbu/report]\n---\n"),
            ("/Work/Zahnarzt.md", "---\nwhen: 2026-11-13\nupcoming: 2d\n---\n"));

        var output = (await Run(shell, "calendar /Work --tag '#KUMBU'")).Output;
        Assert.Contains("\nFri 13 Nov\n  Report  /Work/Report.md  upcoming · tomorrow\n  Review  /Work/Review.md\nSat 14 Nov\n", output);
        Assert.DoesNotContain("Zahnarzt", output);

        Assert.Equal(ShellResult.Error("calendar: no note below /Work carries #nope; 'tags /Work' lists the tags"), await Run(shell, "calendar /Work --tag nope"));
        Assert.Equal(ShellResult.Error("calendar: no note carries #nope; 'tags' lists the tags"), await Run(shell, "calendar notifications --tag nope"));
        Assert.Equal(ShellResult.Error("calendar: 'a b' can't be a tag (a tag is letters, digits, _, - and / without spaces, and not only digits)"),
            await Run(shell, "calendar /Work --tag 'a b'"));
        Assert.Equal(ShellResult.Ok("What's on (1)\n  upcoming · tomorrow  Report  /Work/Report.md"), await Run(shell, "calendar notifications --tag kumbu"));
    }

    [Fact]
    public async Task A_vault_without_Calendar_answers_like_any_other()
    {
        var (shell, _, _) = Make(("/Home/Zahnarzt.md", "---\nwhen: 2026-11-13T09:00\n---\n"), ("/Home/Shopping.md", "milk\n"));

        Assert.Equal(ShellResult.Ok("""
            Week 46 · 9–15 November 2026 · /Home
            Mon 9 Nov
            Tue 10 Nov
            Wed 11 Nov
            Thu 12 Nov
            Fri 13 Nov
              09:00  Zahnarzt  /Home/Zahnarzt.md
            Sat 14 Nov
            Sun 15 Nov
            """), await Run(shell, "calendar /Home"));
    }

    [Fact]
    public async Task Notifications_list_whats_on_over_every_vault()
    {
        var (shell, _, _) = Make(
            ("/Work/Pump/Certificate.md", "---\nwhen: 2026-11-11\ntask: open\n---\n"),
            ("/Work/Release planning.md", "---\nwhen: 2026-11-19\nupcoming: 7d\n---\n"),
            ("/Work/Later.md", "---\nwhen: 2026-12-19\n---\n"),
            ("/Work/Done.md", "---\nwhen: 2026-11-01\ntask: closed\n---\n"),
            ("/Work/Plan.md", "---\nwhen: tomorrow\n---\n"),
            ("/Home/Urlaub.md", "---\nwhen: 2026-11-09\nuntil: 2026-11-13\n---\n"),
            ("/Home/Garden.md", "---\ntask: maybe\nwhen: 2026-11-12\n---\n"));

        Assert.Equal(ShellResult.Ok("""
            What's on (3)
              overdue · 1 day  Certificate  /Work/Pump/Certificate.md  to do
              ongoing · until Fri  Urlaub  /Home/Urlaub.md
              upcoming · in 7 days  Release planning  /Work/Release planning.md
            Problems: 2 dates can't be read (/Work 1, /Home 1); calendar /Work lists them
            """), await Run(shell, "calendar notifications"));
        Assert.Equal("What's on (1)\n  ongoing · until Fri  Urlaub  /Home/Urlaub.md\nProblems: 1 date can't be read (/Home 1); calendar /Home lists it",
            (await Run(shell, "calendar notifications /Home")).Output);
    }

    [Fact]
    public async Task Notifications_say_today_and_the_time_before_it()
    {
        var (shell, _, _) = Make(
            ("/Work/Zahnarzt.md", "---\nwhen: 2026-11-12T14:00\n---\n"),
            ("/Work/Standup.md", "---\nwhen: 2026-11-12T09:30\nuntil: 2026-11-12T11:00\n---\n"));

        Assert.Equal(ShellResult.Ok("""
            What's on (2)
              ongoing · until 11:00  Standup  /Work/Standup.md
              today · 14:00  Zahnarzt  /Work/Zahnarzt.md
            """), await Run(shell, "calendar notifications"));
    }

    [Fact]
    public async Task Notifications_say_nothing_on()
    {
        var (shell, _, _) = Make(("/Work/Later.md", "---\nwhen: 2026-12-19\n---\n"), ("/Work/Note.md", "text\n"));

        Assert.Equal(ShellResult.Ok("Nothing on."), await Run(shell, "calendar notifications"));
    }

    [Fact]
    public async Task Notifications_name_the_problems_and_how_to_see_them()
    {
        var (shell, _, _) = Make(("/Work/Plan.md", "---\nwhen: tomorrow\n---\n"), ("/Home/Note.md", "text\n"));

        Assert.Equal(ShellResult.Ok("Nothing on.\nProblems: 1 date can't be read (/Work 1); calendar /Work lists it"), await Run(shell, "calendar notifications"));
    }

    [Fact]
    public async Task Runs_on_a_checkpoint()
    {
        var (shell, _, writer) = Make(Week46);
        Assert.False((await Run(shell, "mkcp /Work cp")).IsError);
        var moved = "---\nwhen: 2026-11-11T14:00\n---\n";
        Assert.False((await writer.WriteAsync("/Work/Zahnarzt.md", moved, VersionToken.Of("---\nwhen: 2026-11-10T14:00\n---\n"), CancellationToken.None)).IsError);

        Assert.Contains("\nWed 11 Nov\n  14:00  Zahnarzt", (await Run(shell, "calendar /Work")).Output);
        var old = await Run(shell, "calendar /Work --checkpoint cp");
        Assert.Equal(ShellResult.Ok(Week46Output), old);
        Assert.Null(old.Version);
    }

    [Fact]
    public async Task Notifications_on_a_checkpoint_cover_its_vault()
    {
        var (shell, _, writer) = Make(
            ("/Work/Pump/Certificate.md", "---\nwhen: 2026-11-11\ntask: open\n---\n"),
            ("/Home/Urlaub.md", "---\nwhen: 2026-11-09\nuntil: 2026-11-13\n---\n"));
        Assert.False((await Run(shell, "mkcp /Work cp")).IsError);
        Assert.False((await writer.WriteAsync("/Work/Pump/Certificate.md", "---\nwhen: 2026-11-11\ntask: closed\n---\n",
            VersionToken.Of("---\nwhen: 2026-11-11\ntask: open\n---\n"), CancellationToken.None)).IsError);

        Assert.Equal(ShellResult.Ok("What's on (1)\n  overdue · 1 day  Certificate  /Work/Pump/Certificate.md  to do"),
            await Run(shell, "calendar notifications --checkpoint cp /Work"));
        Assert.Equal(ShellResult.Error("calendar: --checkpoint needs a path in the checkpoint's vault, e.g. calendar --checkpoint cp notifications /Work"),
            await Run(shell, "calendar notifications --checkpoint cp"));
    }

    [Theory]
    [InlineData("set", "calendar set /Work/Plan.md --when 2026-11-20 --checkpoint cp")]
    [InlineData("move", "calendar move /Work/Plan.md 2026-11-20 --checkpoint cp")]
    [InlineData("close", "calendar close /Work/x.md --checkpoint cp")]
    [InlineData("reopen", "calendar reopen /Work/x.md --checkpoint=cp")]
    [InlineData("unset", "calendar --checkpoint cp unset /Work/x.md")]
    public async Task The_write_forms_refuse_a_checkpoint(string verb, string line)
    {
        var (shell, _, _) = Make(Week46);
        Assert.False((await Run(shell, "mkcp /Work cp")).IsError);

        Assert.Equal(
            ShellResult.Error($"calendar: {verb} changes a note, and a checkpoint can't be changed; --checkpoint works with calendar <vault> and calendar notifications"),
            await Run(shell, line));
    }

    [Fact]
    public async Task Set_close_and_reopen_from_sh_report_the_version()
    {
        var (shell, store, _) = Make(("/Work/Pump/Certificate.md", "Text\n"));

        var set = await Run(shell, "calendar set /Work/Pump/Certificate.md --when 2026-11-20 --task --upcoming 14d");
        Assert.Equal("calendar: set when 2026-11-20, task open, upcoming 14d on /Work/Pump/Certificate.md", set.Output);
        Assert.Equal("---\nwhen: 2026-11-20\ntask: open\nupcoming: 14d\n---\nText\n", await store.ReadAsync(P("/Work/Pump/Certificate.md"), CancellationToken.None));
        Assert.Equal("/Work/Pump/Certificate.md", set.Version?.Path);

        var close = await Run(shell, "calendar close /Work/Pump/Certificate.md --reason \"renewed until 2027\"");
        Assert.Equal("calendar: closed /Work/Pump/Certificate.md (12 Nov)", close.Output);
        var content = await store.ReadAsync(P("/Work/Pump/Certificate.md"), CancellationToken.None);
        Assert.Equal("---\nwhen: 2026-11-20\ntask: closed\nupcoming: 14d\nclosed: 2026-11-12\nreason: renewed until 2027\n---\nText\n", content);
        Assert.Equal(new NoteVersion("/Work/Pump/Certificate.md", VersionToken.Of(content!)), close.Version);

        var reopen = await Run(shell, "calendar reopen /Work/Pump/Certificate.md");
        Assert.Equal("calendar: reopened /Work/Pump/Certificate.md", reopen.Output);
        Assert.NotNull(reopen.Version);

        Assert.Equal("calendar: moved /Work/Pump/Certificate.md to 27 Nov", (await Run(shell, "calendar move /Work/Pump/Certificate.md 2026-11-27")).Output);
        Assert.False((await Run(shell, "calendar set /Work/Pump/Certificate.md --until 2026-11-30")).IsError);
        Assert.False((await Run(shell, "calendar set /Work/Pump/Certificate.md --no-until --no-task --tz Europe/London")).IsError);
        Assert.Equal("---\nwhen: 2026-11-27\nupcoming: 14d\ntz: Europe/London\n---\nText\n", await store.ReadAsync(P("/Work/Pump/Certificate.md"), CancellationToken.None));
        Assert.Equal("calendar: took /Work/Pump/Certificate.md out of the calendar; the note stays", (await Run(shell, "calendar unset /Work/Pump/Certificate.md")).Output);
        Assert.Equal("Text\n", await store.ReadAsync(P("/Work/Pump/Certificate.md"), CancellationToken.None));
    }

    [Fact]
    public async Task A_write_from_sh_names_the_board_tool_on_a_card()
    {
        var (shell, _, _) = Make(
            ("/Work/Board/Board.md", "---\nkumbu-board: 1\ncolumns:\n  - Doing\n  - Done: [archivable]\n---\n"),
            ("/Work/Board/X.md", "---\ncolumn: Doing\ndue: 2026-11-20\n---\n"));

        Assert.Equal(ShellResult.Error("calendar: /Work/Board/X.md is a card; on a card the column is the status: move it with the board tool")
            with { Kind = ShellErrorKind.Invalid }, await Run(shell, "calendar close /Work/Board/X.md"));
    }

    [Theory]
    [InlineData("calendar close", "calendar: close needs a note, e.g. calendar close /Work/Plan.md")]
    [InlineData("calendar move /Work/x.md", "calendar: move needs a note and its new when, e.g. calendar move /Work/Plan.md 2026-11-20")]
    [InlineData("calendar close /Work/x.md /Work/y.md", "calendar: too many arguments (quote paths and sections that contain spaces: cat \"/Work/My Note.md#Next steps\")")]
    [InlineData("calendar set /Work/x.md", "calendar: set needs at least one of --when, --until, --no-until, --task, --no-task, --upcoming, --tz, --reason")]
    [InlineData("calendar set /Work/x.md --task --no-task", "calendar: --task and --no-task can't go together; give one")]
    [InlineData("calendar set /Work/x.md --until 2026-11-20 --no-until", "calendar: --until and --no-until can't go together; give one")]
    [InlineData("calendar reopen /Work/x.md --reason r", "calendar: unrecognized option '--reason'")]
    [InlineData("calendar close Work/x.md", "calendar: Work/x.md: paths must be absolute (there is no working directory)")]
    [InlineData("calendar close /Work", "calendar: close takes a note, e.g. calendar close /Work/Plan.md")]
    [InlineData("calendar close /Work/nope.md", "calendar: /Work/nope.md: No such note")]
    [InlineData("calendar done /Work/x.md", "calendar: done is not a form of calendar: calendar <vault>, calendar notifications, or set, move, close, reopen, unset (help calendar)")]
    public async Task Unknown_verb_or_missing_note_says_how(string line, string error)
    {
        var (shell, _, _) = Make(("/Work/x.md", "---\nwhen: 2026-11-20\ntask: open\n---\n"));

        var result = await Run(shell, line);

        Assert.True(result.IsError);
        Assert.Equal(error, result.Output);
    }

    [Fact]
    public async Task Help_lists_calendar_once()
    {
        var (shell, _, _) = Make(Week46);
        var help = (await Run(shell, "help")).Output;

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(help, @"\n  calendar "));
    }

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    [Fact]
    public async Task Help_names_every_form()
    {
        var (shell, _, _) = Make(Week46);
        var help = (await Run(shell, "help calendar")).Output;

        Assert.StartsWith("calendar <vault> [--week 2026-W46 | --week 46 | --month 2026-11] [--tag <tag>]\n", help);
        foreach (var form in new[]
                 {
                     "calendar notifications [--tag <tag>]",
                     "calendar set <note> --when <value> [--until <value>] [--task | --no-task] [--upcoming 7d] [--tz <zone>] [--reason <text>]",
                     "calendar move <note> <when>",
                     "calendar close <note> [--reason <text>]",
                     "calendar reopen <note>",
                     "calendar unset <note>",
                 })
        {
            Assert.Contains("\n" + form + "\n", help);
        }

        Assert.EndsWith("--checkpoint <name>: as a checkpoint holds it; see 'help checkpoints'", help);
        Assert.Contains("\n  calendar <vault>                ", Manual.ToolDescription);
    }
}
