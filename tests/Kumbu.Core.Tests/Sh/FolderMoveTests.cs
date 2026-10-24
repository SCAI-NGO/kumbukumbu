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
        await _store.TryCreateAsync(new StoredNote(P("/Demo/archive/projects/deep/spec.md"), "taken\n", 1, Writers.Seed, InMemoryNoteStore.Seeded, []), CancellationToken.None);

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
}
