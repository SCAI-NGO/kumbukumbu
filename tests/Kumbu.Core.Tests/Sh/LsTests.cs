// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

public class LsTests
{
    private static readonly InMemoryNoteStore Reader = new(
        ("/Work/a.md", "A"), ("/Work/projects/x.md", "X"), ("/Work/projects/y.md", "Y"),
        ("/Workshop/w.md", "W"), ("/Ideas/Meeting Notes.md", "M"));

    private static Task<ShellResult> Run(params string[] args) =>
        new Ls(Reader).RunAsync(args, null, CancellationToken.None);

    [Fact]
    public void NameIsLs() => Assert.Equal("ls", new Ls(Reader).Name);

    [Theory]
    [InlineData]
    [InlineData("/")]
    public async Task ListsVaults(params string[] args) =>
        Assert.Equal(ShellResult.Ok("Ideas/\nWork/\nWorkshop/"), await Run(args));

    [Theory]
    [InlineData("/Work")]
    [InlineData("/Work/")]
    public async Task ListsDirectory(string arg)
    {
        var result = await Run(arg);
        Assert.Equal(ShellResult.Ok("a.md\nprojects/"), result);
        Assert.DoesNotContain("w.md", result.Output);
    }

    [Fact]
    public async Task NoteEchoesPath() =>
        Assert.Equal(ShellResult.Ok("/Work/projects/x.md"), await Run("/Work/projects/x.md"));

    [Fact]
    public async Task NotFound() =>
        Assert.Equal(ShellResult.Error("ls: cannot access '/Nope': No such file or directory"), await Run("/Nope"));

    [Fact]
    public async Task NormalizesDotDot() =>
        Assert.Equal(ShellResult.Ok("Meeting Notes.md"), await Run("/Work/../Ideas"));

    [Fact]
    public async Task WithoutVaultsItSaysSo() =>
        Assert.Equal(ShellResult.Ok("(no vaults)"), await new Ls(new InMemoryNoteStore()).RunAsync([], null, CancellationToken.None));

    [Fact]
    public async Task LockedVaultsAreMentioned()
    {
        var store = new InMemoryNoteStore { LockedVaults = 3 };
        Assert.Equal(
            ShellResult.Ok("(no vaults)\n(3 vaults in the database don't open with this token; the server log says how to recover them)"),
            await new Ls(store).RunAsync([], null, CancellationToken.None));
    }

    [Fact]
    public async Task RelativePath() =>
        Assert.Equal(ShellResult.Error("ls: Work/a.md: paths must be absolute (there is no working directory)"), await Run("Work/a.md"));

    [Fact]
    public async Task InvalidOption() =>
        Assert.Equal(ShellResult.Error("ls: invalid option -- 'l'"), await Run("-la", "/Work"));

    [Fact]
    public async Task TooManyArguments() =>
        Assert.Equal(ShellResult.Error("ls: too many arguments"), await Run("/Work", "/Ideas"));
}
