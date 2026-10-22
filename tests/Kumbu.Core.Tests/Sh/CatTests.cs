// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

public class CatTests
{
    private const string Content = "# Title\r\n\n  trailing  \n";

    private static readonly InMemoryNoteStore Reader = new(("/Work/a.md", Content), ("/Work/projects/x.md", "X"));

    private static Task<ShellResult> Run(params string[] args) =>
        new Cat(Reader).RunAsync(args, null, CancellationToken.None);

    [Fact]
    public void NameIsCat() => Assert.Equal("cat", new Cat(Reader).Name);

    [Fact]
    public async Task ReturnsContentVerbatimWithItsVersion() => Assert.Equal(
        ShellResult.Ok(Content) with { Version = new NoteVersion("/Work/a.md", VersionToken.Of(Content)) },
        await Run("/Work/a.md"));

    [Fact]
    public async Task MissingOperand() => Assert.Equal(ShellResult.Error("cat: missing operand"), await Run());

    [Fact]
    public async Task TooManyArguments() =>
        Assert.Equal(ShellResult.Error("cat: too many arguments (quote paths and sections that contain spaces: cat \"/Work/My Note.md#Next steps\")"), await Run("/Work/a.md", "/Work/a.md"));

    [Fact]
    public async Task NotFound() =>
        Assert.Equal(ShellResult.Error("cat: /Work/nope.md: No such file or directory"), await Run("/Work/nope.md"));

    [Theory]
    [InlineData("/Work/projects", "/Work/projects")]
    [InlineData("/Work", "/Work")]
    [InlineData("/", "/")]
    [InlineData("/Work/projects/", "/Work/projects")]
    public async Task IsADirectory(string arg, string shown) =>
        Assert.Equal(ShellResult.Error($"cat: {shown}: Is a directory"), await Run(arg));

    [Fact]
    public async Task RelativePath() =>
        Assert.Equal(ShellResult.Error("cat: a.md: paths must be absolute (there is no working directory)"), await Run("a.md"));

    [Fact]
    public async Task InvalidOption() =>
        Assert.Equal(ShellResult.Error("cat: invalid option -- 'n'"), await Run("-n", "/Work/a.md"));
}
