// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

public class HelpTests
{
    private static readonly string[] Commands = ["ls", "cat", "outline", "rg", "links", "backlinks", "board", "head", "tail", "wc", "sort", "history", "restore", "mv", "archive", "mkdir", "rm", "trash", "df", "du", "publish", "uname", "help"];

    // A shell without the pixels has none of these; the tool description names them all the same.
    private static readonly string[] AttachmentCommands = ["img", "pdf", "pdfimages"];

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

    [Theory]
    [InlineData("ls", "ls [--archive] [path]", "ls [--archive] [path]")]
    [InlineData("tags", "tags [--archive] [path]", "tags [--archive] [tag] [path]")]
    [InlineData("backlinks", "backlinks [--archive] [--unlinked] <path>", "backlinks [--archive] <path>")]
    public async Task The_readers_that_hide_the_archive_take_archive(string command, string help, string usage)
    {
        var details = (await Run($"help {command}")).Output;
        Assert.StartsWith(help, details);
        Assert.Contains("\n  --archive ", details);
        Assert.Contains($"\n  {usage} ", Manual.ToolDescription);
        Assert.Contains($"\n  {usage} ", (await Run("help")).Output);
    }

    [Fact]
    public async Task Help_ls_says_hiding_the_archive_is_not_access_control()
    {
        var help = System.Text.RegularExpressions.Regex.Replace((await Run("help ls")).Output, @"\s+", " ");
        Assert.Contains("_archive/ is the vault's archive (see 'help archive'); hiding it is about attention, not access: whoever may read the vault may read it.", help);
    }

    [Fact]
    public async Task Help_mkdir_says_an_empty_folder_stays()
    {
        var result = await Run("help mkdir");
        Assert.False(result.IsError);
        Assert.StartsWith("mkdir <path>", result.Output);
        Assert.Contains("An empty folder stays until you remove it", result.Output);
        Assert.Contains("There are no options.", result.Output);
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
    public async Task Help_lists_the_attachments_topic()
    {
        var topic = (await Run("help attachments")).Output;
        Assert.StartsWith("attachments", topic);
        Assert.Contains("15 MB each", topic);
        Assert.Contains("shot.png: image (PNG), 1440×3120, 412 KB", topic);
        Assert.Contains("semsearch  ranks the pages of PDFs (not scans) by meaning", topic);
        Assert.Contains("semsearch --words and --tag find notes only", topic);
        Assert.Contains("img        shows an image to the model", topic);
        Assert.Contains("pdf        reads a PDF", topic);
        // The search commands are items of the same list, their continuation under their description.
        Assert.Contains("\n    pdf        reads a PDF", topic);
        Assert.Contains("\n    rg         searches the text of PDFs", topic);
        Assert.Contains("\n    semsearch  ranks the pages of PDFs", topic);
        Assert.Contains("\n                 1. /Work/paper.pdf p7  score 0.61", topic);

        // The stub is gone: a shell without the pixels has no pdf to explain.
        Assert.Equal(ShellResult.Error("help: no such command: pdf — try 'help'"), await Run("help pdf"));

        Assert.Contains("'help attachments'", (await Run("help")).Output);
        Assert.Contains("help attachments", Manual.ToolDescription);
    }

    [Fact]
    public async Task Help_archive_shows_the_command_and_then_the_archive_topic()
    {
        var help = (await Run("help archive")).Output;
        Assert.StartsWith("archive <path>", help);
        var topic = help.IndexOf("\n\nthe archive\n", StringComparison.Ordinal);
        Assert.True(topic > 0, help);
        Assert.True(help.IndexOf("archive /Work/Inbox/Manual/", StringComparison.Ordinal) < topic);

        var text = System.Text.RegularExpressions.Regex.Replace(help[topic..], @"\s+", " ");
        Assert.Contains("Each vault has its own archive: the folder _archive/ directly in its root (/Work/_archive/).", text);
        Assert.Contains("A folder of that name anywhere else (/Work/Inbox/_archive/) is an ordinary folder.", text);
        Assert.Contains("rg searches neither its notes nor its PDFs; --files and --tag leave its notes out", text);
        Assert.Contains("semsearch ranks neither its notes nor its PDF pages", text);
        Assert.Contains("tags counts no tag of its notes", text);
        Assert.Contains("backlinks lists no note in it that links to the path", text);
        Assert.Contains("the web app's tree and search leave it out too", text);
        Assert.Contains("Asking: --archive on each of them, or a path inside the archive: ls /Work/_archive/", text);
        Assert.Contains("Not hidden: history, trash, du (the archive is a line of its own), df, restore, mv, checkpoints, the board; nor WebDAV, sync, the export, publishing or sharing.", text);
        Assert.Contains("Links prefer what is outside the archive", text);
        Assert.Contains("only when nothing else matches does it lead in (a full path still leads where it says; a note in the archive resolves its links as before).", text);
        Assert.Contains("adding the time, in the server's time zone, to its name", text);
        Assert.Contains("/Work/_archive/Inbox/report 2026-11-08 213005.pdf", text);
        Assert.Contains("To restore, mv it back without the time (mv never overwrites).", text);
        Assert.Contains("Hiding the archive is about attention, not access: whoever may read the vault may read it.", text);
    }

    [Fact]
    public async Task Archive_help_points_to_the_topic()
    {
        var help = (await Run("archive --help")).Output;
        Assert.StartsWith("archive <path>", help);
        Assert.DoesNotContain("\nthe archive\n", help);
        Assert.EndsWith("\n  'help archive' adds the archive topic", help);
    }

    [Fact]
    public async Task Help_lists_the_archive_topic()
    {
        Assert.Contains("'help archive' for what a vault's _archive/ hides.", (await Run("help")).Output);
        Assert.Contains("'help archive' the command and the vault's archive", (await Run("help help")).Output);
        Assert.Contains("\n  archive <path>                  move a note, attachment or folder (/Work/old/) into the vault's archive; links follow (help archive)\n", Manual.ToolDescription);
    }

    [Fact]
    public async Task UnknownTopic() =>
        Assert.Equal(ShellResult.Error("help: no such command: grep — try 'help'"), await Run("help grep"));

    [Fact]
    public async Task TooManyArguments() => Assert.Equal(ShellResult.Error("help: too many arguments"), await Run("help ls cat"));

    [Fact]
    public void ToolDescriptionMentionsEveryCommand()
    {
        foreach (var command in Commands.Concat(AttachmentCommands))
        {
            Assert.Contains("\n  " + command + " ", Manual.ToolDescription);
        }
    }

    [Fact]
    public async Task Help_mentions_shared_vaults()
    {
        var result = await Run("help");

        Assert.Contains("garten/ (shared by \"Alice\", read)", result.Output);
        Assert.Contains("last written by another user", result.Output);
        Assert.Contains("shared by", (await Run("help ls")).Output);
        Assert.Contains("last written by another user", (await Run("help cat")).Output);
    }
}
