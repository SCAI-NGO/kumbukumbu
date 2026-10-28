// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

public class HelpTests
{
    private static readonly string[] Commands = ["ls", "cat", "outline", "rg", "links", "backlinks", "board", "head", "tail", "wc", "sort", "history", "restore", "mv", "rm", "trash", "help"];

    private static Task<ShellResult> Run(string line) => TestShell.Of().RunAsync(line, CancellationToken.None);

    [Fact]
    public async Task ListsEveryCommandInOrder()
    {
        var result = await Run("help");
        Assert.False(result.IsError);
        var positions = Commands.Select(c => result.Output.IndexOf("\n  " + c + " ", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
        Assert.Contains("right of a pipe only rg, head, tail, wc, sort work", result.Output);
    }

    [Theory]
    [InlineData("help rg")]
    [InlineData("rg --help")]
    [InlineData("rg -i --help")]
    [InlineData("ls / | rg --help")]
    public async Task DetailsOfOneCommand(string line)
    {
        var result = await Run(line);
        Assert.False(result.IsError);
        Assert.StartsWith("rg [options] <regex> [path...]", result.Output);
        Assert.Contains("--files-with-matches", result.Output);
    }

    [Fact]
    public async Task EveryCommandHasHelpThatStartsWithItsName()
    {
        foreach (var command in Commands)
        {
            var result = await Run($"help {command}");
            Assert.False(result.IsError);
            Assert.StartsWith(command, result.Output);
        }
    }

    [Fact]
    public async Task HelpAfterDoubleDashIsAnOperand()
    {
        var shell = TestShell.Of(("/W/a.md", "use --help\n"));
        Assert.Equal(ShellResult.Ok("/W/a.md:1:use --help"), await shell.RunAsync("rg -- --help /W", CancellationToken.None));
    }

    [Theory]
    [InlineData("help links")]
    [InlineData("links --help")]
    [InlineData("help sort")]
    public async Task ReadersPointToTheCheckpointTopic(string line)
    {
        var result = await Run(line);
        Assert.EndsWith("--checkpoint <name>: as a checkpoint holds it; see 'help checkpoints'", result.Output);
    }

    [Fact]
    public async Task OtherCommandsDont() => Assert.DoesNotContain("help checkpoints", (await Run("help mv")).Output);

    [Fact]
    public async Task CheckpointTopicNamesEveryReader()
    {
        var result = await Run("help checkpoints");
        Assert.False(result.IsError);
        Assert.StartsWith("checkpoints", result.Output);
        Assert.Contains("ls, cat, outline, rg, links, backlinks, tags, board, head, tail, wc, sort", result.Output);
    }

    [Fact]
    public async Task Help_lists_the_attachments_img_and_pdf_topics()
    {
        var topic = (await Run("help attachments")).Output;
        Assert.StartsWith("attachments", topic);
        Assert.Contains("15 MB each", topic);
        Assert.Contains("shot.png: image (PNG), 1440×3120, 412 KB", topic);
        Assert.Contains("rg and semsearch skip attachments", topic);

        Assert.StartsWith("img", (await Run("help img")).Output);
        Assert.Contains("Coming in 0.2.5", (await Run("help img")).Output);
        Assert.StartsWith("pdf", (await Run("help pdf")).Output);
        Assert.Contains("Coming in 0.2.5", (await Run("help pdf")).Output);

        Assert.Contains("'help attachments'", (await Run("help")).Output);
        Assert.Contains("help attachments", Manual.ToolDescription);
    }

    [Fact]
    public async Task UnknownTopic() =>
        Assert.Equal(ShellResult.Error("help: no such command: grep — try 'help'"), await Run("help grep"));

    [Fact]
    public async Task TooManyArguments() => Assert.Equal(ShellResult.Error("help: too many arguments"), await Run("help ls cat"));

    [Fact]
    public void ToolDescriptionMentionsEveryCommand()
    {
        foreach (var command in Commands)
        {
            Assert.Contains("\n  " + command + " ", Manual.ToolDescription);
        }
    }
}
