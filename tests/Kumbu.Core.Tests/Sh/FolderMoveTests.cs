// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>mv of a whole folder: one batch, links in and out rewritten, move records for every note.</summary>
public class FolderMoveTests
{
    private readonly InMemoryNoteStore _store = new(
        ("/Demo/projects/kumbu.md", "# Kumbu\nSee [[projects/deep/spec]] and [[../welcome]].\n"),
        ("/Demo/projects/deep/spec.md", "spec, back to [[kumbu]]\n"),
        ("/Demo/welcome.md", "[[projects/kumbu]] [[spec]]\n"),
        ("/Demo/archive/old.md", "old\n"),
        ("/Ideas/x.md", "[[projects/kumbu]]\n"));

    private readonly Shell _sh;

    public FolderMoveTests()
    {
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        _sh = new Shell(_store, new NoteWriter(_store, time, new FixedCaller(Writers.Dev)), new NoteMover(_store, time, new FixedCaller(Writers.Dev)));
    }

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private async Task<string?> Content(string path) => await _store.ReadAsync(P(path), CancellationToken.None);

    [Fact]
    public async Task RenamesAFolderWithItsSubfoldersAndRewritesLinks()
    {
        var result = await Run("mv /Demo/projects/ /Demo/work/");

        Assert.False(result.IsError, result.Output);
        Assert.StartsWith("moved /Demo/projects/ to /Demo/work/ (2 notes)", result.Output);
        Assert.Null(await Content("/Demo/projects/kumbu.md"));
        Assert.NotNull(await Content("/Demo/work/deep/spec.md"));
        Assert.Equal("[[work/kumbu]] [[spec]]\n", await Content("/Demo/welcome.md"));
        Assert.Equal("# Kumbu\nSee [[work/deep/spec]] and [[../welcome]].\n", await Content("/Demo/work/kumbu.md"));
        Assert.Equal("spec, back to [[kumbu]]\n", await Content("/Demo/work/deep/spec.md"));
        // Another vault never pointed here.
        Assert.Equal("[[projects/kumbu]]\n", await Content("/Ideas/x.md"));
    }

    [Fact]
    public async Task AFolderMoveRewritesMarkdownLinksAbsoluteAndKeepsInnerRelativeOnes()
    {
        _store.AddVault("W");
        await Seed("/W/p/a.md", "[b](./b.md) [o](../o.md)\n");
        await Seed("/W/p/b.md", "b\n");
        await Seed("/W/o.md", "o\n");
        await Seed("/W/x.md", "[a](p/a.md)\n");

        var result = await Run("mv /W/p/ /W/q/");

        Assert.False(result.IsError, result.Output);
        // ./b.md moves along with a.md and ../o.md is still one folder up: both still reach the same notes.
        Assert.Equal("[b](./b.md) [o](../o.md)\n", await Content("/W/q/a.md"));
        Assert.Equal("[a](/q/a.md)\n", await Content("/W/x.md"));
    }

    private Task Seed(string path, string content) =>
        _store.TryCreateAsync(new StoredNote(P(path), content, 1, Writers.Seed, InMemoryNoteStore.Seeded, [], Ref: WriterRef.Server), CancellationToken.None);

    [Fact]
    public async Task MovesIntoAnExistingFolder()
    {
        var result = await Run("mv /Demo/projects/ /Demo/archive/");

        Assert.False(result.IsError, result.Output);
        Assert.NotNull(await Content("/Demo/archive/projects/kumbu.md"));
        Assert.NotNull(await Content("/Demo/archive/projects/deep/spec.md"));
        Assert.Equal("old\n", await Content("/Demo/archive/old.md"));
    }

    [Fact]
    public async Task EveryOldPathSaysWhereItWent()
    {
        await Run("mv /Demo/projects/ /Demo/work/");

        Assert.Contains("it was moved to /Demo/work/deep/spec.md", (await Run("cat /Demo/projects/deep/spec.md")).Output);
        Assert.Contains("it was moved to /Demo/work/kumbu.md", (await Run("cat /Demo/projects/kumbu.md")).Output);
    }

    [Fact]
    public async Task ARaceMovesNothing()
    {
        _store.Interfere = n => n with { Content = "changed meanwhile\n" };

        var result = await Run("mv /Demo/projects/ /Demo/work/");

        Assert.True(result.IsError);
        Assert.StartsWith("mv: nothing was moved", result.Output);
        Assert.NotNull(await Content("/Demo/projects/deep/spec.md"));
        Assert.Null(await Content("/Demo/work/deep/spec.md"));
        Assert.DoesNotContain("work/", await Content("/Demo/welcome.md"));
    }

    [Fact]
    public async Task ANameCollisionMovesNothing()
    {
        await _store.TryCreateAsync(new StoredNote(P("/Demo/archive/projects/deep/spec.md"), "taken\n", 1, Writers.Seed, InMemoryNoteStore.Seeded, [], Ref: WriterRef.Server), CancellationToken.None);

        var result = await Run("mv /Demo/projects/ /Demo/archive/");

        Assert.Equal(ShellResult.Error("mv: /Demo/archive/projects/deep/spec.md already exists; rm it first, or choose another name"), result);
        Assert.NotNull(await Content("/Demo/projects/kumbu.md"));
        Assert.Null(await Content("/Demo/archive/projects/kumbu.md"));
    }

    [Theory]
    [InlineData("mv /Demo/projects/ /Demo/projects/deep/x/", "mv: /Demo/projects/deep/x/ is inside /Demo/projects/; a folder can't move into itself")]
    [InlineData("mv /Demo/ /Demo2/", "mv: /Demo/ is a vault; a vault can't be moved")]
    [InlineData("mv /Demo/projects/ /Ideas/projects/", "mv: notes can't move between vaults (Demo → Ideas); a vault is its own space")]
    [InlineData("mv /Demo/nope/ /Demo/x/", "mv: /Demo/nope/: No such file or directory")]
    [InlineData("mv /Demo/welcome.md/ /Demo/x/", "mv: /Demo/welcome.md/: Not a directory (to move the note, leave out the '/')")]
    [InlineData("mv /Demo/projects/ /Demo/welcome.md", "mv: /Demo/welcome.md: Not a directory")]
    public async Task Errors(string line, string error) => Assert.Equal(ShellResult.Error(error), await Run(line));

    [Fact]
    public async Task Several_notes_move_into_a_folder_and_their_links_follow()
    {
        _store.AddVault("W");
        await Seed("/W/a.md", "see [[b]]\n");
        await Seed("/W/b.md", "b\n");
        await Seed("/W/c.md", "[b](/b.md)\n");

        var result = await Run("mv /W/a.md /W/b.md /W/Old/");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(2, result.Output.Split('\n').Length);
        Assert.Null(await Content("/W/a.md"));
        Assert.Null(await Content("/W/b.md"));
        Assert.NotNull(await Content("/W/Old/a.md"));
        Assert.NotNull(await Content("/W/Old/b.md"));
        Assert.Equal("[b](/Old/b.md)\n", await Content("/W/c.md"));
        // a.md moved before b.md did, so its link was rewritten to keep leading to b.
        Assert.Equal("see [[b]]\n", await Content("/W/Old/a.md"));
    }

    [Fact]
    public async Task A_failing_source_leaves_the_others_moved()
    {
        _store.AddVault("W");
        await Seed("/W/a.md", "a\n");
        await Seed("/W/b.md", "b\n");

        var result = await Run("mv /W/a.md /W/missing.md /W/b.md /W/Old/");

        Assert.True(result.IsError);
        var lines = result.Output.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("missing.md: No such file or directory", lines[1]);
        Assert.NotNull(await Content("/W/Old/a.md"));
        Assert.NotNull(await Content("/W/Old/b.md"));
        Assert.Null(await Content("/W/a.md"));
        Assert.Null(await Content("/W/b.md"));
    }

    [Fact]
    public async Task Several_sources_need_a_folder_target()
    {
        _store.AddVault("W");
        await Seed("/W/a.md", "a\n");
        await Seed("/W/b.md", "b\n");

        var result = await Run("mv /W/a.md /W/b.md /W/Old");

        Assert.Equal(ShellResult.Error("mv: with several sources the target must be a folder, ending in '/'"), result);
        Assert.NotNull(await Content("/W/a.md"));
        Assert.NotNull(await Content("/W/b.md"));
    }

    [Fact]
    public async Task Notes_and_folders_mix()
    {
        _store.AddVault("W");
        await Seed("/W/a.md", "a\n");
        await Seed("/W/Sub/s.md", "s\n");

        var result = await Run("mv /W/a.md /W/Sub/ /W/Old/");

        Assert.False(result.IsError, result.Output);
        Assert.NotNull(await Content("/W/Old/a.md"));
        Assert.NotNull(await Content("/W/Old/Sub/s.md"));
        Assert.Null(await Content("/W/Sub/s.md"));
    }

    [Fact]
    public async Task A_folder_that_already_exists_in_the_target_is_a_plain_collision()
    {
        _store.AddVault("W");
        await Seed("/W/Sub/s.md", "s\n");
        await Seed("/W/X/x.md", "x\n");
        await Seed("/W/Old/Sub/t.md", "t\n");

        var result = await Run("mv /W/Sub/ /W/X/ /W/Old/");

        Assert.True(result.IsError);
        var lines = result.Output.Split('\n');
        Assert.Equal("mv: /W/Old/Sub already exists; rm it first, or choose another name", lines[0]);
        Assert.StartsWith("moved /W/X/ to /W/Old/X/", lines[1]);
        Assert.NotNull(await Content("/W/Sub/s.md"));
        Assert.NotNull(await Content("/W/Old/X/x.md"));
    }

    [Fact]
    public async Task Two_folders_land_inside_a_target_that_does_not_exist_yet()
    {
        _store.AddVault("W");
        await Seed("/W/A/a.md", "a\n");
        await Seed("/W/B/b.md", "b\n");

        var result = await Run("mv /W/A/ /W/B/ /W/New/");

        Assert.False(result.IsError, result.Output);
        Assert.NotNull(await Content("/W/New/A/a.md"));
        Assert.NotNull(await Content("/W/New/B/b.md"));
    }

    [Fact]
    public async Task Several_sources_with_keep_links_leave_links_as_they_are()
    {
        _store.AddVault("W");
        await Seed("/W/a.md", "a\n");
        await Seed("/W/b.md", "b\n");
        await Seed("/W/c.md", "[b](/b.md) [[a]]\n");

        var result = await Run("mv --keep-links /W/a.md /W/b.md /W/Old/");

        Assert.False(result.IsError, result.Output);
        Assert.NotNull(await Content("/W/Old/a.md"));
        Assert.Equal("[b](/b.md) [[a]]\n", await Content("/W/c.md"));
    }
}
