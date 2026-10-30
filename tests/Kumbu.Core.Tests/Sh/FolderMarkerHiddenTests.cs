// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>A kept folder's marker is never shown in the shell: listings, searches and links leave it out, and where a
/// write to it must be named, its folder is named instead.</summary>
public class FolderMarkerHiddenTests
{
    private readonly InMemoryNoteStore _store = new(("/Work/plan.md", "plan about drafts\n"));
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 30, 11, 0, 0, DateTimeKind.Utc));

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);

    private NoteWriter Writer => new(_store, _time, new FixedCaller(Writers.Dev));

    private Task<ShellResult> Run(string line) =>
        new Shell(_store, Writer, new NoteMover(_store, _time, new FixedCaller(Writers.Dev))).RunAsync(line, Ct);

    [Fact]
    public async Task Ls_shows_a_kept_folder_and_never_its_marker()
    {
        await Run("mkdir /Work/Drafts");

        Assert.Equal(ShellResult.Ok("Drafts/\nplan.md"), await Run("ls /Work"));
        Assert.Equal(ShellResult.Ok(""), await Run("ls /Work/Drafts"));

        await Writer.WriteAsync("/Work/Drafts/a.md", "text\n", null, Ct);
        Assert.Equal(ShellResult.Ok("a.md"), await Run("ls /Work/Drafts"));
    }

    [Theory]
    [InlineData("ls /Work/Drafts/.vaultkeep")]
    [InlineData("cat /Work/Drafts/.vaultkeep")]
    [InlineData("cat \"/Work/Drafts/.vaultkeep#Part\"")]
    [InlineData("history /Work/Drafts/.vaultkeep")]
    [InlineData("rm /Work/Drafts/.vaultkeep")]
    [InlineData("outline /Work/Drafts/.vaultkeep")]
    [InlineData("mkdir /Work/Drafts/.vaultkeep")]
    [InlineData("mv /Work/Drafts/.vaultkeep /Work/x/.vaultkeep")]
    public async Task A_marker_path_is_no_such_file(string line)
    {
        await Run("mkdir /Work/Drafts");

        var result = await Run(line);

        Assert.True(result.IsError, result.Output);
        Assert.Contains(": No such file or directory", result.Output);
        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/Drafts/.vaultkeep"), Ct));
    }

    [Theory]
    [InlineData("rg --files /Work")]
    [InlineData("rg -l . /Work")]
    [InlineData("rg -n vaultkeep /Work")]
    [InlineData("semsearch drafts /Work")]
    [InlineData("tags /Work")]
    public async Task No_search_or_listing_names_the_marker(string line)
    {
        await Run("mkdir /Work/Drafts");
        await Run("mkdir /Work/Empty");
        await Writer.WriteAsync("/Work/Drafts/a.md", "a draft #idea\n", null, Ct);

        Assert.DoesNotContain(".vaultkeep", (await Run(line)).Output);
    }

    [Fact]
    public async Task History_of_a_folder_names_the_folder()
    {
        await Run("mkdir /Work/Drafts");
        await Run("mkdir /Work/Gone");
        await Run("rm /Work/Gone");

        var rows = (await Run("history /Work")).Output.Split('\n');

        Assert.Contains(rows, r => r.Contains("/Work/Drafts/") && r.Contains("  folder"));
        Assert.Contains(rows, r => r.Contains("/Work/Gone/ → trash"));
        Assert.DoesNotContain(rows, r => r.Contains(".vaultkeep"));
    }

    [Fact]
    public async Task History_names_a_moved_folder_as_the_folder()
    {
        await Run("mkdir /Work/Drafts");
        await Run("mv /Work/Drafts/ /Work/Old/");

        var output = (await Run("history /Work")).Output;

        Assert.Contains("/Work/Drafts/ → /Work/Old/", output);
        Assert.DoesNotContain(".vaultkeep", output);

        await Writer.WriteAsync("/Work/Old/a.md", "text\n", null, Ct);
        await Run("mv /Work/Old/ /Work/Older/");
        Assert.Contains(
            (await Run("history /Work")).Output.Split('\n'),
            r => r.Contains("/Work/Old/ → /Work/Older/") && r.EndsWith("  1 note", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Trash_names_a_removed_folder()
    {
        await Run("mkdir /Work/Drafts");
        await Run("rm /Work/Drafts");

        var output = (await Run("trash /Work")).Output;

        Assert.Contains("/Work/Drafts/  folder  (by dev)", output);
        Assert.DoesNotContain(".vaultkeep", output);
    }

    [Fact]
    public async Task A_link_to_vaultkeep_leads_nowhere()
    {
        await Run("mkdir /Work/Drafts");
        await Writer.WriteAsync("/Work/n.md", "[[.vaultkeep]] ![[Drafts/.vaultkeep]]\n", null, Ct);

        Assert.Equal(ShellResult.Ok("1: [[.vaultkeep]] → not found\n1: ![[Drafts/.vaultkeep]] → not found"), (await Run("links /Work/n.md")) with { Version = null });
        Assert.Contains("[[.vaultkeep]] → not found", (await Run("links /Work")).Output);
    }

    [Fact]
    public async Task Moving_a_kept_folder_moves_its_marker()
    {
        await Run("mkdir /Work/Drafts");

        Assert.Equal(ShellResult.Ok("moved /Work/Drafts/ to /Work/Old/; no links needed rewriting"), await Run("mv /Work/Drafts/ /Work/Old/"));
        Assert.Equal(ShellResult.Ok("Old/\nplan.md"), await Run("ls /Work"));
        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/Old/.vaultkeep"), Ct));

        await Run("mkdir /Work/A/B");
        await Writer.WriteAsync("/Work/A/n.md", "text\n", null, Ct);
        Assert.Equal(ShellResult.Ok("moved /Work/A/ to /Work/Z/ (1 note); no links needed rewriting"), await Run("mv /Work/A/ /Work/Z/"));
        Assert.Equal(ShellResult.Ok("B/\nn.md"), await Run("ls /Work/Z"));
        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/Z/B/.vaultkeep"), Ct));
    }
}
