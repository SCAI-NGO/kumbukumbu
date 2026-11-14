// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tags;
using Kumbu.Core.Tests.Fakes;
using Kumbu.Core.Tests.Sh;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Tags;

public class FlagServiceTests
{
    private static readonly TimeZoneInfo Vienna = Zones.Find("Europe/Vienna")!;

    // Friday, 13 November 2026, 23:30 UTC: already Saturday the 14th in Vienna.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 23, 30, 0, TimeSpan.Zero));

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private FlagService Service(InMemoryNoteStore store, ICaller? caller = null)
    {
        caller ??= new FixedCaller(Writers.Dev);
        return new FlagService(store, new NoteWriter(store, _time, caller), () => _time.GetUtcNow().UtcDateTime, new FixedZone(Vienna));
    }

    private static async Task<string?> Content(InMemoryNoteStore store, string path) => await store.ReadAsync(P(path), Ct);

    private static async Task<long> Revision(InMemoryNoteStore store, string path) => (await store.GetAsync(P(path), Ct))!.Revision;

    private static void Ok(ShellResult result) => Assert.False(result.IsError, result.Output);

    private const string Task = "---\ntitle: Pump\ntags: [pump, flag/red, \"spare parts\"] # parts\nwhen: 2026-11-20\ntask: open\n---\nCheck the certificate.\r\n";

    [Fact]
    public async Task Sets_a_flag_and_keeps_every_other_byte()
    {
        const string before = "---\n# a comment\naliases:\n  - One\ntags: [a, \"b\"]   # mine\n---\nLine one\r\nLine two\r\n";
        var store = new InMemoryNoteStore(("/Work/x.md", before));

        var result = await Service(store).SetAsync("/Work/x.md", "red", FlagTask.None, Ct);

        Ok(result);
        Assert.Equal("---\n# a comment\naliases:\n  - One\ntags: [a, \"b\", flag/red]   # mine\n---\nLine one\r\nLine two\r\n", await Content(store, "/Work/x.md"));
        Assert.Equal("/Work/x.md", result.Version?.Path);
        Assert.Equal((await store.GetAsync(P("/Work/x.md"), Ct))!.Version, result.Version?.Token);
        Assert.Equal("red", Flags.Of((await Content(store, "/Work/x.md"))!)?.Name);
    }

    [Fact]
    public async Task Removes_it_again()
    {
        const string before = "Body without frontmatter\n";
        var store = new InMemoryNoteStore(("/Work/x.md", before));
        var service = Service(store);

        Ok(await service.SetAsync("/Work/x.md", "important", FlagTask.None, Ct));
        Assert.Equal("---\ntags: [flag/important]\n---\nBody without frontmatter\n", await Content(store, "/Work/x.md"));
        Ok(await service.SetAsync("/Work/x.md", null, FlagTask.None, Ct));

        Assert.Equal(before, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task The_same_flag_again_writes_nothing()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", Task));

        var result = await Service(store).SetAsync("/Work/x.md", "red", FlagTask.None, Ct);

        Ok(result);
        Assert.Equal(1, await Revision(store, "/Work/x.md"));
        Assert.Equal(VersionToken.Of(Task), result.Version?.Token);
    }

    [Fact]
    public async Task A_read_only_member_can_not_flag()
    {
        const string note = "---\ntags: [a]\n---\n";
        var store = new InMemoryNoteStore(("/Garten/x.md", note));
        store.Infos["Garten"] = new VaultInfo("\"Alice\"", AccessLevel.Read, true);

        var result = await Service(store, new FixedCaller("bob", LevelOf: _ => AccessLevel.Read)).SetAsync("/Garten/x.md", "red", FlagTask.None, Ct);

        Assert.True(result.IsError);
        Assert.Equal(ShellErrorKind.Refused, result.Kind);
        Assert.StartsWith("flag: ", result.Output);
        Assert.Equal(note, await Content(store, "/Garten/x.md"));
        Assert.Equal(1, await Revision(store, "/Garten/x.md"));
    }

    [Fact]
    public async Task A_flag_in_the_text_is_left_alone()
    {
        const string note = "---\ntags: [a]\n---\nLook at this #flag/Red later.\n";
        var store = new InMemoryNoteStore(("/Work/x.md", note));
        var service = Service(store);

        var set = await service.SetAsync("/Work/x.md", "blue", FlagTask.None, Ct);
        var removed = await service.SetAsync("/Work/x.md", null, FlagTask.None, Ct);

        Assert.Equal(ShellResult.Error("flag: This flag is written in the note's text (#flag/Red); change it there.") with { Kind = ShellErrorKind.Invalid }, set);
        Assert.Equal(set, removed);
        Assert.Equal(note, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Mark_done_closes_the_task_in_the_same_write()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", Task));

        Ok(await Service(store).SetAsync("/Work/x.md", "done", FlagTask.Close, Ct));

        Assert.Equal("---\ntitle: Pump\ntags: [pump, flag/done, \"spare parts\"] # parts\nwhen: 2026-11-20\ntask: closed\nclosed: 2026-11-14\n---\nCheck the certificate.\r\n",
            await Content(store, "/Work/x.md"));
        Assert.Equal(2, await Revision(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Mark_done_keeps_the_spelling_of_the_task_key()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "---\nWhen: 2026-11-20\nTask: Open\n---\n"));

        Ok(await Service(store).SetAsync("/Work/x.md", "done", FlagTask.Close, Ct));

        Assert.Equal("---\nWhen: 2026-11-20\nTask: closed\ntags: [flag/done]\nclosed: 2026-11-14\n---\n", await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Undo_takes_both_back()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", Task));
        var service = Service(store);

        Ok(await service.SetAsync("/Work/x.md", "done", FlagTask.Close, Ct));
        Ok(await service.SetAsync("/Work/x.md", "red", FlagTask.Reopen, Ct));

        Assert.Equal(Task, await Content(store, "/Work/x.md"));
        Assert.Equal(3, await Revision(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Mark_done_and_undo_keep_crlf()
    {
        const string before = "---\r\ntags: [flag/red]\r\nwhen: 2026-11-20\r\ntask: open\r\n---\r\nText\r\n";
        var store = new InMemoryNoteStore(("/Work/x.md", before));
        var service = Service(store);

        Ok(await service.SetAsync("/Work/x.md", "done", FlagTask.Close, Ct));
        Assert.Equal("---\r\ntags: [flag/done]\r\nwhen: 2026-11-20\r\ntask: closed\r\nclosed: 2026-11-14\r\n---\r\nText\r\n", await Content(store, "/Work/x.md"));
        Ok(await service.SetAsync("/Work/x.md", "red", FlagTask.Reopen, Ct));

        Assert.Equal(before, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Mark_done_leaves_a_line_without_cr_as_it_was()
    {
        const string before = "---\r\ntitle: x\nwhen: 2026-11-20\r\ntask: open\r\n---\r\n";
        var store = new InMemoryNoteStore(("/Work/x.md", before));

        Ok(await Service(store).SetAsync("/Work/x.md", "done", FlagTask.Close, Ct));

        Assert.Equal("---\r\ntitle: x\nwhen: 2026-11-20\r\ntask: closed\r\ntags: [flag/done]\r\nclosed: 2026-11-14\r\n---\r\n", await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Undo_without_a_flag_before_gives_the_note_back()
    {
        const string before = "---\nwhen: 2026-11-20\ntask: open\n---\nText\n";
        var store = new InMemoryNoteStore(("/Work/x.md", before));
        var service = Service(store);

        Ok(await service.SetAsync("/Work/x.md", "done", FlagTask.Close, Ct));
        Ok(await service.SetAsync("/Work/x.md", null, FlagTask.Reopen, Ct));

        Assert.Equal(before, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Reopen_keeps_a_reason()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "---\ntags: [flag/done]\nwhen: 2026-11-20\ntask: closed\nclosed: 2026-11-12\nreason: sold\n---\n"));

        Ok(await Service(store).SetAsync("/Work/x.md", null, FlagTask.Reopen, Ct));

        Assert.Equal("---\nwhen: 2026-11-20\ntask: open\nreason: sold\n---\n", await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Close_on_a_card_is_refused()
    {
        const string definition = "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Done\n---\n# Work\n";
        const string card = "---\ncolumn: Ideas\nrank: V\ntask: open\n---\nText\n";
        var store = new InMemoryNoteStore(("/Work/Board/Board.md", definition), ("/Work/Board/X.md", card));

        var result = await Service(store).SetAsync("/Work/Board/X.md", "done", FlagTask.Close, Ct);

        Assert.True(result.IsError);
        Assert.Equal(ShellErrorKind.Invalid, result.Kind);
        Assert.Equal(card, await Content(store, "/Work/Board/X.md"));
    }

    [Theory]
    [InlineData("red")]
    [InlineData(null)]
    public async Task Close_comes_only_with_the_done_flag(string? flag)
    {
        var store = new InMemoryNoteStore(("/Work/x.md", Task));

        var result = await Service(store).SetAsync("/Work/x.md", flag, FlagTask.Close, Ct);

        Assert.Equal(ShellResult.Error("flag: only the done flag also marks the to-do done; choose done, or leave the calendar as it is") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Equal(Task, await Content(store, "/Work/x.md"));
    }

    [Theory]
    [InlineData("---\nwhen: 2026-11-20\ntask: closed\nclosed: 2026-11-12\n---\n")]
    [InlineData("---\nwhen: 2026-11-20\n---\n")]
    public async Task Close_on_a_closed_task_is_refused(string note)
    {
        var store = new InMemoryNoteStore(("/Work/x.md", note));

        var result = await Service(store).SetAsync("/Work/x.md", "done", FlagTask.Close, Ct);

        Assert.True(result.IsError);
        Assert.Equal(ShellErrorKind.Invalid, result.Kind);
        Assert.Equal(note, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Reopen_on_an_open_task_is_refused()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", Task));

        var result = await Service(store).SetAsync("/Work/x.md", "red", FlagTask.Reopen, Ct);

        Assert.Equal(ShellErrorKind.Invalid, result.Kind);
        Assert.Equal(Task, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task A_write_in_between_is_retried()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", Task));
        store.Interfere = n => n with { Content = n.Content.Replace("Check", "Synced: check", StringComparison.Ordinal) };

        Ok(await Service(store).SetAsync("/Work/x.md", "green", FlagTask.None, Ct));

        Assert.Null(store.Interfere);
        Assert.Equal(Task.Replace("flag/red", "flag/green").Replace("Check", "Synced: check"), await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task A_note_that_keeps_changing_is_a_conflict()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", Task));
        store.Contended = _ => true;

        var result = await Service(store).SetAsync("/Work/x.md", "green", FlagTask.None, Ct);

        Assert.Equal(ShellErrorKind.Conflict, result.Kind);
        Assert.Contains("kept changing", result.Output);
    }

    [Theory]
    [InlineData("orange")]
    [InlineData("Red")]
    [InlineData("")]
    public async Task An_unknown_flag_is_refused(string flag)
    {
        var store = new InMemoryNoteStore(("/Work/x.md", Task));

        var result = await Service(store).SetAsync("/Work/x.md", flag, FlagTask.None, Ct);

        Assert.Equal(ShellErrorKind.Invalid, result.Kind);
        Assert.Equal(Task, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task A_broken_frontmatter_is_refused()
    {
        const string note = "---\ntags: [a\nwhen: : :\n---\nText\n";
        var store = new InMemoryNoteStore(("/Work/x.md", note));

        var result = await Service(store).SetAsync("/Work/x.md", "red", FlagTask.None, Ct);

        Assert.Equal(ShellErrorKind.Conflict, result.Kind);
        Assert.Equal(note, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task Tags_it_can_not_edit_are_refused()
    {
        const string note = "---\ntags:\n  work:\n    - a\n---\nText\n";
        var store = new InMemoryNoteStore(("/Work/x.md", note));

        var result = await Service(store).SetAsync("/Work/x.md", "red", FlagTask.None, Ct);

        Assert.Equal(ShellResult.Error("flag: I can't change this note's tags; edit them in the note") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Equal(note, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task No_flag_that_would_leave_one_is_refused()
    {
        // YAML reads the item after the comment as one of the list; the line editor stops at the comment.
        const string note = "---\ntags:\n  - a\n# c\n  - flag/red\n---\n";
        var store = new InMemoryNoteStore(("/Work/x.md", note));

        var result = await Service(store).SetAsync("/Work/x.md", null, FlagTask.None, Ct);

        Assert.Equal(ShellResult.Error("flag: I can't change this note's tags; edit them in the note") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Equal(note, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task A_note_over_the_limit_is_refused()
    {
        var note = "Body " + new string('x', NoteWriter.MaxBytes - 10);
        var store = new InMemoryNoteStore(("/Work/x.md", note));

        var result = await Service(store).SetAsync("/Work/x.md", "red", FlagTask.None, Ct);

        Assert.Equal(ShellErrorKind.Invalid, result.Kind);
        Assert.Contains("1 MiB", result.Output);
        Assert.Equal(note, await Content(store, "/Work/x.md"));
    }

    [Fact]
    public async Task A_missing_note_is_not_found()
    {
        var result = await Service(new InMemoryNoteStore(("/Work/x.md", ""))).SetAsync("/Work/nope.md", "red", FlagTask.None, Ct);

        Assert.Equal(ShellErrorKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task Help_tags_names_the_flags()
    {
        var help = System.Text.RegularExpressions.Regex.Replace(
            (await TestShell.Of().RunAsync("help tags", Ct)).Output, @"\s+", " ");

        Assert.Contains("flag/red, flag/green, flag/blue, flag/purple, flag/yellow, flag/white, flag/done and flag/important, one per note", help);
        Assert.Contains("'tags flag' finds every flagged note, rg --tag flag/red the red ones", help);
        Assert.Contains("Set or change one by editing the frontmatter's tags.", help);
    }
}
