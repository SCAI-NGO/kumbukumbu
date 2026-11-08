// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

/// <summary>A vault's archive (<c>/&lt;vault&gt;/_archive/</c>) stays out of ls, tags and backlinks unless asked with
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
        Assert.Equal(ShellResult.Ok("(no tags below /Old)"), await Run("tags /Old", store));
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
        Assert.Equal(ShellResult.Ok("/Work/Inbox/note.md:1:see [[Overview]] and [[Gone]] #pump"), (await Run("backlinks /Work/Overview.md")) with { Version = null });
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
    public async Task Du_shows_the_archive_as_its_own_line()
    {
        var du = await Run("du /Work");

        Assert.False(du.IsError, du.Output);
        Assert.Contains(du.Output.Split('\n'), l => l.EndsWith("  /Work/_archive/", StringComparison.Ordinal));
    }
}
