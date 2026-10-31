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

    [Fact]
    public async Task A_note_last_written_by_another_user_is_marked()
    {
        // Alice's own shell: Bob's write (an empty label, a member's reference) is marked.
        var alice = TestShell.Of(SharedVaults.SharedGarten(asMember: false));

        var result = await alice.RunAsync("cat /Garten/plan.md", CancellationToken.None);

        Assert.Equal("[/Garten/plan.md: last written by another user]\nBeete planen\n", result.Output);
        // In a pipeline the mark stays out of what the pipe passes on.
        Assert.Equal("[/Garten/plan.md: last written by another user]\n1", (await alice.RunAsync("cat /Garten/plan.md | wc -l", CancellationToken.None)).Output);
        // As Bob, the owner's agent's write is another user's.
        var bob = SharedVaults.AsBob(SharedVaults.SharedGarten(asMember: true));
        Assert.StartsWith("[/Garten/beet.md: last written by another user]\n", (await bob.RunAsync("cat /Garten/beet.md", CancellationToken.None)).Output);
        Assert.StartsWith("[/Garten/plan.md: last written by another user]\n", (await bob.RunAsync("cat /Garten/plan.md", CancellationToken.None)).Output);
    }

    [Fact]
    public async Task The_callers_own_agents_writes_are_not_marked()
    {
        // Alice's agent wrote beet.md under its own label: hers.
        Assert.Equal("Beete gießen\n", (await TestShell.Of(SharedVaults.SharedGarten(asMember: false)).RunAsync("cat /Garten/beet.md", CancellationToken.None)).Output);
        // Bob's agent's write stores the empty label, as every member's: his by the writer id alone.
        Assert.Equal("Beete jäten\n", (await SharedVaults.AsBob(SharedVaults.SharedGarten(asMember: true)).RunAsync("cat /Garten/own.md", CancellationToken.None)).Output);
        // A vault nobody else reaches marks nothing.
        var alone = SharedVaults.SharedGarten(asMember: false);
        alone.Infos.Clear();
        Assert.Equal("Beete planen\n", (await TestShell.Of(alone).RunAsync("cat /Garten/plan.md", CancellationToken.None)).Output);
    }
}
