// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

/// <summary>A vault's archive (<c>/&lt;vault&gt;/_archive/</c>) stays out of ls, tags, backlinks and rg unless asked with
/// --archive or by naming a path inside it; a folder <c>_archive</c> elsewhere is an ordinary folder.</summary>
public class ArchiveHiddenTests
{
    private static InMemoryNoteStore Store()
    {
        var store = new InMemoryNoteStore(
            ("/Work/Overview.md", "# Overview\n#pump\n"),
            ("/Work/Inbox/note.md", "see [[Overview]] and [[Gone]] #pump\n"),
            ("/Work/Inbox/_archive/kept.md", "kept #pump\n"),
            ("/Work/_archive/Inbox/old.md", "old [[Overview]] and [[Gone]] #pump #old\n"),
            ("/Work/_archive/Gone.md", "gone\n"));
        store.AddAttachment("/Work/_archive/shot.png");
        return store;
    }

    private static Kumbu.Core.Paths.VaultPath Path(string path) =>
        Kumbu.Core.Paths.VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);

    private static Task<ShellResult> Run(string line, InMemoryNoteStore? store = null) =>
        TestShell.Of(store ?? Store()).RunAsync(line, CancellationToken.None);

    [Fact]
    public async Task Ls_leaves_the_archive_out_of_a_vaults_listing()
    {
        Assert.Equal(ShellResult.Ok("Inbox/\nOverview.md"), await Run("ls /Work"));

        var asked = await Run("ls --archive /Work");
        Assert.False(asked.IsError, asked.Output);
        Assert.Equal(["Inbox/", "Overview.md", "_archive/"], asked.Output.Split('\n').Order(StringComparer.Ordinal));
        Assert.Equal(asked, await Run("ls /Work --archive"));
    }

    [Fact]
    public async Task Ls_inside_the_archive_shows_everything()
    {
        var archive = await Run("ls /Work/_archive/");
        Assert.False(archive.IsError, archive.Output);
        Assert.Equal(["Gone.md", "Inbox/", "shot.png  image"], archive.Output.Split('\n').Select(l => l.StartsWith("shot.png", StringComparison.Ordinal) ? "shot.png  image" : l).Order(StringComparer.Ordinal));
        Assert.Equal(ShellResult.Ok("old.md"), await Run("ls /Work/_archive/Inbox"));
    }

    [Fact]
    public async Task A_folder_named_archive_below_the_vaults_root_is_ordinary()
    {
        var inbox = await Run("ls /Work/Inbox");
        Assert.False(inbox.IsError, inbox.Output);
        Assert.Contains("_archive/", inbox.Output.Split('\n'));
        Assert.Equal(ShellResult.Ok("kept.md"), await Run("ls /Work/Inbox/_archive/"));
    }

    [Fact]
    public async Task A_vault_holding_only_its_archive_lists_as_empty()
    {
        var store = new InMemoryNoteStore(("/Old/_archive/a.md", "a #pump\n"));
        store.AddAttachment("/Pics/_archive/shot.png");

        Assert.Equal(ShellResult.Ok(""), await Run("ls /Old", store));
        Assert.Equal(ShellResult.Ok(""), await Run("ls /Pics", store));
        Assert.Equal(ShellResult.Ok("(no tags below /Old outside the archive; tags --archive /Old counts those of archived notes)"), await Run("tags /Old", store));
        Assert.Equal(ShellResult.Ok("#pump (1)"), await Run("tags --archive /Old", store));
    }

    [Fact]
    public async Task Ls_refuses_another_option()
    {
        Assert.True((await Run("ls --all /Work")).IsError);
        Assert.Equal(ShellResult.Error("ls: too many arguments"), await Run("ls --archive /Work /Work/Inbox"));
    }

    [Fact]
    public async Task Tags_counts_archived_notes_only_when_asked()
    {
        Assert.Equal(ShellResult.Ok("#pump (3)"), await Run("tags /Work"));
        Assert.Equal(ShellResult.Ok("#pump (4)\n#old (1)"), await Run("tags --archive /Work"));
        Assert.Equal(ShellResult.Ok("#pump (3)"), await Run("tags"));
        Assert.Equal(ShellResult.Ok("#old (1)\n#pump (1)"), await Run("tags /Work/_archive"));
        Assert.Equal(ShellResult.Ok("#old (1)\n#pump (1)"), await Run("tags /Work/_archive/Inbox/old.md"));

        Assert.Equal(
            ShellResult.Ok("/Work/Inbox/_archive/kept.md:1:kept #pump\n/Work/Inbox/note.md:1:see [[Overview]] and [[Gone]] #pump\n/Work/Overview.md:2:#pump"),
            await Run("tags pump /Work"));
        Assert.Contains("/Work/_archive/Inbox/old.md:1:", (await Run("tags --archive pump /Work")).Output);
        Assert.Equal(ShellResult.Ok("/Work/_archive/Inbox/old.md:1:old [[Overview]] and [[Gone]] #pump #old"), await Run("tags pump /Work/_archive/"));
    }

    [Fact]
    public async Task Backlinks_leave_archived_sources_out_unless_asked()
    {
        Assert.Equal("1 archived note links here too: backlinks --archive /Work/Overview.md\n/Work/Inbox/note.md:1:see [[Overview]] and [[Gone]] #pump", (await Run("backlinks /Work/Overview.md")).Output);
        Assert.Equal(
            ShellResult.Ok("/Work/_archive/Inbox/old.md:1:old [[Overview]] and [[Gone]] #pump #old\n/Work/Inbox/note.md:1:see [[Overview]] and [[Gone]] #pump"),
            (await Run("backlinks --archive /Work/Overview.md")) with { Version = null });
    }

    [Fact]
    public async Task Backlinks_of_an_archived_target_list_archived_sources()
    {
        Assert.Equal(
            ShellResult.Ok("/Work/_archive/Inbox/old.md:1:old [[Overview]] and [[Gone]] #pump #old\n/Work/Inbox/note.md:1:see [[Overview]] and [[Gone]] #pump"),
            (await Run("backlinks /Work/_archive/Gone.md")) with { Version = null });
    }

    [Fact]
    public async Task Backlinks_say_when_only_archived_notes_link_here()
    {
        var store = new InMemoryNoteStore(
            ("/Work/Overview.md", "# Overview\n"),
            ("/Work/Plan.md", "plan\n"),
            ("/Work/note.md", "see [[Overview]]\n"),
            ("/Work/_archive/a.md", "[[Plan]] and [[Overview]]\n"),
            ("/Work/_archive/b.md", "[[Plan]]\n[[Plan]] again\n"));

        Assert.Equal(
            ShellResult.Ok("(no notes outside the archive link here; 2 archived notes do: backlinks --archive /Work/Plan.md)"),
            (await Run("backlinks /Work/Plan.md", store)) with { Version = null });
        Assert.Equal("1 archived note links here too: backlinks --archive /Work/Overview.md\n/Work/note.md:1:see [[Overview]]", (await Run("backlinks /Work/Overview.md", store)).Output);
        Assert.Equal("1 archived note links here too: backlinks --archive /Work/Overview.md\n1", (await Run("backlinks /Work/Overview.md | wc -l", store)).Output);
        Assert.Equal(ShellResult.Ok("(no notes link here)"), (await Run("backlinks /Work/note.md", store)) with { Version = null });
    }

    [Fact]
    public async Task A_tag_only_archived_notes_carry_says_so()
    {
        Assert.Equal(ShellResult.Error("tags: only archived notes below /Work carry #old; tags --archive old /Work lists them"), await Run("tags old /Work"));
        Assert.Equal(ShellResult.Error("tags: only archived notes carry #old; tags --archive old lists them"), await Run("tags old"));
        Assert.Equal(ShellResult.Error("rg: only archived notes below /Work carry #old; rg --archive -i --tag old pump /Work searches them"), await Run("rg --tag old -i pump /Work"));
        Assert.Equal(ShellResult.Error("rg: only archived notes below /Work carry #old; rg --files --archive --tag old /Work lists them"), await Run("rg --files --tag old /Work"));
        Assert.Equal(ShellResult.Error("rg: only archived notes carry #old; rg --files --archive --tag old lists them"), await Run("rg --files --tag old"));
        Assert.Equal(ShellResult.Error("tags: no note below /Work carries #nothing; 'tags /Work' lists the tags"), await Run("tags nothing /Work"));
        Assert.False((await Run("rg --tag old --archive pump /Work")).IsError);
    }

    [Fact]
    public async Task Rg_says_when_only_archived_notes_match()
    {
        Assert.Equal(ShellResult.Ok("no match outside the archive; 1 archived note matches (rg --archive -F '#old' /Work searches it)"), await Run("rg -F '#old' /Work"));
        Assert.Equal(ShellResult.Ok(""), await Run("rg -F '#old' /Work | head"));
        Assert.Equal(ShellResult.Ok(""), await Run("rg nowhere /Work"));
        Assert.Equal(
            ShellResult.Ok("no match outside the archive; 1 archived note matches (rg --archive -F --tag pump '#old' /Work searches it)"),
            await Run("rg --tag pump -F '#old' /Work"));
        Assert.Equal(
            ShellResult.Ok("no match outside the archive; 1 archived note matches (rg --archive -i -e nothing -e '#OLD' /Work/Overview.md /Work searches it)"),
            await Run("rg -i -e nothing -e '#OLD' /Work/Overview.md /Work"));
    }

    [Fact]
    public async Task Rg_l_and_c_say_when_only_archived_notes_match()
    {
        Assert.Equal(ShellResult.Ok("no match outside the archive; 1 archived note matches (rg --archive -l -F '#old' /Work searches it)"), await Run("rg -l -F '#old' /Work"));
        Assert.Equal(ShellResult.Ok("no match outside the archive; 1 archived note matches (rg --archive -c -i '#OLD' searches it)"), await Run("rg -c -i '#OLD'"));
        Assert.Equal("/Work/_archive/Inbox/old.md", (await Run("rg --archive -l -F '#old' /Work")).Output);
    }

    [Fact]
    public async Task Rg_counts_archived_matches_only_up_to_past_what_it_names()
    {
        InMemoryNoteStore Archived(int count) => new([.. Enumerable.Range(1, count).Select(i => ($"/V/_archive/n{i}.md", "pump\n"))]);

        Assert.Equal(ShellResult.Ok("no match outside the archive; 5 archived notes match (rg --archive pump /V searches them)"), await Run("rg pump /V", Archived(5)));
        Assert.Equal(ShellResult.Ok("no match outside the archive; more than 5 archived notes match (rg --archive pump /V searches them)"), await Run("rg pump /V", Archived(6)));
    }

    [Fact]
    public void The_archived_hint_stops_counting_past_what_it_names()
    {
        var notes = Enumerable.Range(1, 9).Select(i => new Kumbu.Core.Notes.Note(Path($"/V/_archive/n{i}.md"), "pump")).ToList();
        var read = 0;

        var hint = Kumbu.Core.Sh.Commands.Rg.ArchivedHint(notes, _ => { read++; return true; }, "rg --archive pump", CancellationToken.None);

        Assert.Equal("no match outside the archive; more than 5 archived notes match (rg --archive pump searches them)", hint);
        Assert.Equal(6, read);
    }

    [Fact]
    public void The_archived_hint_gives_up_on_a_search_that_takes_too_long()
    {
        var notes = Enumerable.Range(1, 3).Select(i => new Kumbu.Core.Notes.Note(Path($"/V/_archive/n{i}.md"), "pump")).ToList();
        var read = 0;

        var hint = Kumbu.Core.Sh.Commands.Rg.ArchivedHint(
            notes, _ => ++read == 1 ? true : throw new System.Text.RegularExpressions.RegexMatchTimeoutException("pump", "pump", TimeSpan.FromSeconds(2)), "rg --archive pump", CancellationToken.None);

        Assert.Null(hint);
        Assert.Equal(2, read);
    }

    [Fact]
    public void The_archived_hint_stops_when_cancelled()
    {
        var notes = Enumerable.Range(1, 3).Select(i => new Kumbu.Core.Notes.Note(Path($"/V/_archive/n{i}.md"), "pump")).ToList();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var read = 0;

        Assert.Throws<OperationCanceledException>(() => Kumbu.Core.Sh.Commands.Rg.ArchivedHint(notes, _ => { read++; return true; }, "rg --archive pump", cancelled.Token));
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task Rg_names_archived_and_named_matches_in_one_voice()
    {
        var store = new InMemoryNoteStore(("/Work/plan-old.md", "nothing here\n"), ("/Work/_archive/x.md", "see plan-old\n"));

        Assert.Equal(
            ShellResult.Ok("no match outside the archive; 1 archived note matches (rg --archive -F plan-old /Work searches it)\n1 note name matches: /Work/plan-old.md (rg --files | rg -F plan-old lists them)"),
            await Run("rg -F plan-old /Work", store));
    }

    [Fact]
    public async Task Du_shows_the_archive_as_its_own_line()
    {
        var du = await Run("du /Work");

        Assert.False(du.IsError, du.Output);
        Assert.Contains(du.Output.Split('\n'), l => l.EndsWith("  /Work/_archive/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rg_leaves_archived_notes_out_unless_asked()
    {
        const string Living = "/Work/Inbox/_archive/kept.md:1:kept #pump\n/Work/Inbox/note.md:1:see [[Overview]] and [[Gone]] #pump\n/Work/Overview.md:2:#pump";
        Assert.Equal(Living, (await Run("rg pump /Work")).Output);
        Assert.Equal(Living, (await Run("rg pump")).Output);

        var asked = await Run("rg --archive pump /Work");
        Assert.Equal(
            "/Work/_archive/Inbox/old.md:1:old [[Overview]] and [[Gone]] #pump #old\n/Work/Inbox/_archive/kept.md:1:kept #pump\n/Work/Inbox/note.md:1:see [[Overview]] and [[Gone]] #pump\n/Work/Overview.md:2:#pump",
            asked.Output);
        Assert.Equal(asked.Output, (await Run("rg pump --archive")).Output);
    }

    [Fact]
    public async Task Rg_inside_the_archive_searches_it()
    {
        const string Old = "/Work/_archive/Inbox/old.md:1:old [[Overview]] and [[Gone]] #pump #old";
        Assert.Equal(Old, (await Run("rg pump /Work/_archive/")).Output);
        Assert.Equal(Old, (await Run("rg pump /Work/_archive/Inbox/old.md")).Output);
        Assert.Equal($"{Old}\n/Work/Overview.md:2:#pump", (await Run("rg pump /Work/Overview.md /Work/_archive/Inbox")).Output);
    }

    [Fact]
    public async Task Rg_names_no_archived_note_in_its_name_hint()
    {
        Assert.Equal(ShellResult.Ok(""), await Run("rg -F Gone.md /Work"));
        Assert.Equal(
            ShellResult.Ok("no match in the text; 1 note name matches: /Work/_archive/Gone.md (rg --files --archive | rg -F Gone.md lists them)"),
            await Run("rg --archive -F Gone.md /Work"));
        Assert.Equal(
            ShellResult.Ok("no match in the text; 1 note name matches: /Work/_archive/Gone.md (rg --files --archive | rg -i gone.md lists them)"),
            await Run("rg -i gone.md /Work/_archive/"));
        Assert.Equal(
            ShellResult.Ok("no match in the text; 1 note name matches: /Work/Overview.md (rg --files | rg -i overview.md lists them)"),
            await Run("rg -i overview.md /Work"));
    }

    [Fact]
    public async Task Rg_files_leaves_archived_notes_out_unless_asked()
    {
        Assert.Equal(ShellResult.Ok("/Work/Inbox/_archive/kept.md\n/Work/Inbox/note.md\n/Work/Overview.md"), await Run("rg --files /Work"));
        Assert.Equal(
            ShellResult.Ok("/Work/_archive/Gone.md\n/Work/_archive/Inbox/old.md\n/Work/Inbox/_archive/kept.md\n/Work/Inbox/note.md\n/Work/Overview.md"),
            await Run("rg --files --archive /Work"));
        Assert.Equal(ShellResult.Ok("/Work/_archive/Gone.md\n/Work/_archive/Inbox/old.md"), await Run("rg --files /Work/_archive/"));
        Assert.Equal(ShellResult.Ok("/Work/_archive/Gone.md"), await Run("rg --files /Work/_archive/Gone.md"));
    }

    [Fact]
    public async Task Rg_files_with_a_tag_leaves_archived_notes_out_unless_asked()
    {
        Assert.Equal(ShellResult.Ok("/Work/Inbox/_archive/kept.md\n/Work/Inbox/note.md\n/Work/Overview.md"), await Run("rg --files --tag pump /Work"));
        Assert.Equal(
            ShellResult.Ok("/Work/_archive/Inbox/old.md\n/Work/Inbox/_archive/kept.md\n/Work/Inbox/note.md\n/Work/Overview.md"),
            await Run("rg --files --archive --tag pump /Work"));
        Assert.True((await Run("rg --files --tag old /Work")).IsError);
        Assert.Equal(ShellResult.Ok("/Work/_archive/Inbox/old.md"), await Run("rg --files --tag old /Work/_archive/"));
        Assert.Equal(ShellResult.Ok("/Work/_archive/Inbox/old.md"), await Run("rg --files --archive --tag old /Work"));
    }

    [Fact]
    public async Task Rg_over_a_vault_holding_only_its_archive_finds_nothing()
    {
        var store = new InMemoryNoteStore(("/Old/_archive/a.md", "a #pump\n"));

        Assert.Equal(ShellResult.Ok("no match outside the archive; 1 archived note matches (rg --archive pump /Old searches it)"), await Run("rg pump /Old", store));
        Assert.Equal(ShellResult.Ok("no match outside the archive; 1 archived note matches (rg --archive pump searches it)"), await Run("rg pump", store));
        Assert.Equal(ShellResult.Ok(""), await Run("rg nowhere /Old", store));
        Assert.Equal(ShellResult.Ok(""), await Run("rg --files /Old", store));
        Assert.Equal(ShellResult.Ok(""), await Run("rg --files", store));
        Assert.Equal("/Old/_archive/a.md:1:a #pump", (await Run("rg --archive pump /Old", store)).Output);
        Assert.Equal(ShellResult.Ok("/Old/_archive/a.md"), await Run("rg --files --archive /Old", store));
    }

    [Fact]
    public async Task Help_rg_and_semsearch_name_archive()
    {
        Assert.Contains("\n  --archive ", (await Run("help rg")).Output);
        var semsearch = (await Run("help semsearch")).Output;
        Assert.StartsWith("semsearch [-n N] [-C|-l] [--words|--meaning] [--tag T] [--archive] <query> [path]", semsearch);
        Assert.Contains("\n  --archive ", semsearch);
        Assert.Contains("--archive   take in the notes in a vault's archive (_archive/) too; a path inside it always does", (await Run("help tags")).Output);
        // The column of the commands' summaries stays where it is; an option that doesn't fit goes into the summary.
        var manual = Kumbu.Core.Sh.Manual.ToolDescription.Split('\n');
        Assert.Contains(manual, l => l.StartsWith("  semsearch <query> [path]        ", StringComparison.Ordinal) && l.Contains("--archive", StringComparison.Ordinal));
        Assert.All(manual.Where(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Length > 36 && l[2] != ' '), l => Assert.True(l[33] == ' ' && l[34] != ' ', l));
        Assert.Contains("\n  semsearch [-n N] [-C|-l] [--words|--meaning] [--tag T] [--archive] <query> [path] ", (await Run("help")).Output);
    }
}
