// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

public class HelpTests
{
    private static readonly string[] Commands = ["ls", "cat", "outline", "rg", "links", "backlinks", "board", "calendar", "head", "tail", "wc", "sort", "csv", "html", "xml", "history", "blame", "restore", "mv", "archive", "mkdir", "rm", "trash", "df", "du", "publish", "uname", "help"];

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
        Assert.Contains("right of a pipe only rg, head, tail, wc, sort, csv work", result.Output);
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
    [InlineData("ls", "ls [-l] [-a|--archive] [path]", "ls [-l] [-a|--archive] [path]", "ls [-l] [-a|--archive] [path]")]
    [InlineData("tags", "tags [--archive] [path]", "tags [--archive] [tag] [path]", "tags [--archive] [tag] [path]")]
    [InlineData("backlinks", "backlinks [--archive] [--unlinked] <path>", "backlinks [--archive] [--unlinked] <path>", "backlinks [--archive] <path>")]
    public async Task The_readers_that_hide_the_archive_take_archive(string command, string help, string listUsage, string manualUsage)
    {
        var details = (await Run($"help {command}")).Output;
        Assert.StartsWith(help, details);
        // ls has a short form too, written as its -l is: "-a, --archive".
        Assert.Matches(@"\n  (-a, )?--archive ", details);
        // The tool description keeps its lines short; the help list shows every option.
        Assert.Contains($"\n  {manualUsage} ", Manual.ToolDescription);
        Assert.Contains($"\n  {listUsage} ", (await Run("help")).Output);
    }

    /// <summary>A fragment of the synopsis is in a synopsis line of <c>help command</c> and in the command's line of
    /// the help list; one of the option list (a long form the Usage leaves out, an output's mark) in the help only.</summary>
    [Theory]
    [InlineData("head", "[--version V]", true)]
    [InlineData("sort", "[--version V]", true)]
    [InlineData("tail", "[--version V]", true)]
    [InlineData("wc", "[--version V]", true)]
    [InlineData("outline", "[--version V]", true)]
    [InlineData("links", "[--version V]", true)]
    [InlineData("semsearch", "-n, --max N", false)]
    [InlineData("restore", "--checkpoint <name> [--plan <token>] <path>", true)]
    [InlineData("df", "-h, --human-readable", false)]
    [InlineData("du", "-h, --human-readable", false)]
    [InlineData("backlinks", "[--unlinked]", true)]
    [InlineData("diff", "diff [--word] --checkpoint <name> <path>", true)]
    [InlineData("tail", "[-n N|-n +N]", true)]
    [InlineData("sort", "[-r] [-u] [-n] [-f]", true)]
    [InlineData("wc", "[-l] [-w] [-m] [-c]", true)]
    [InlineData("links", "'attachment (not a note)' for other files", false)]
    public async Task Usage_and_help_name_the_options_the_command_takes(string command, string fragment, bool synopsis)
    {
        var help = (await Run($"help {command}")).Output;
        var flat = System.Text.RegularExpressions.Regex.Replace(help, @"\s+", " ");
        var row = (await Run("help")).Output.Split('\n').Single(l => l.StartsWith($"  {command} ", StringComparison.Ordinal));

        if (synopsis)
        {
            Assert.Contains(help.Split('\n'), l => l.StartsWith($"{command} ", StringComparison.Ordinal) && l.Contains(fragment, StringComparison.Ordinal));
            Assert.Contains(fragment, row);
        }
        else
        {
            Assert.Contains(fragment, flat);
        }
    }

    [Fact]
    public async Task Help_ls_says_hiding_the_archive_is_not_access_control()
    {
        var help = System.Text.RegularExpressions.Regex.Replace((await Run("help ls")).Output, @"\s+", " ");
        Assert.Contains("_archive/ is the vault's archive (see 'help archive'); hiding it is about attention, not access: whoever may read the vault may read it.", help);
    }

    [Fact]
    public async Task Help_blame_names_the_folder_limit_and_the_section_forms()
    {
        var output = (await Run("help blame")).Output;
        var help = System.Text.RegularExpressions.Regex.Replace(output, @"\s+", " ");
        Assert.StartsWith("blame [--lines] <path>[#section]\n", output);
        Assert.Contains("note.md#Heading", help);
        Assert.Contains("note.md#^block", help);
        Assert.Contains($"At most {Kumbu.Core.Sh.Commands.Blame.MaxNotes} notes", help);
        Assert.Contains("… and N more notes; blame a folder below it", help);
        Assert.Contains("--lines takes a note, not a folder", help);
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
        Assert.Contains("ls, cat, outline, rg, links, backlinks, tags, board, calendar, head, tail, wc, sort", result.Output);
    }

    [Fact]
    public async Task Help_lists_the_attachments_topic()
    {
        var topic = (await Run("help attachments")).Output;
        Assert.StartsWith("attachments", topic);
        Assert.Contains("every file type, up to the server's bound each (1 GiB unless its", topic);
        Assert.Contains("larger than 15 MB is kept", topic);
        Assert.Contains("cat prints it and rg searches it", topic);
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
        // The readers of text files' formats are items too, each pointing to its own help.
        Assert.Contains("\n    csv        reads a text file as a table", topic);
        Assert.Contains("\n    html       reads a text file as HTML", topic);
        Assert.Contains("\n    xml        reads a text file as XML", topic);

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
        Assert.Contains("rg searches none of its notes, text files or PDFs, and --files lists none of them", text);
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

    [Fact]
    public void Write_description_says_an_SVG_and_a_skill_file_keep_no_message()
    {
        var text = System.Text.RegularExpressions.Regex.Replace(Manual.WriteDescription, @"\s+", " ");
        Assert.Contains("kept with this version in the note's history; an SVG's or a skill file's history keeps none", text);
    }

    [Fact]
    public async Task Archive_topic_names_graph_and_calendar()
    {
        var help = (await Run("help archive")).Output;
        var topic = help[help.IndexOf("\n\nthe archive\n", StringComparison.Ordinal)..];
        Assert.Contains("\n    graph      leaves its notes and attachments out; --archive takes them in\n", topic);
        Assert.Contains("\n    calendar   leaves archived notes out\n", topic);
        var text = System.Text.RegularExpressions.Regex.Replace(topic, @"\s+", " ");
        Assert.Contains("calendar has no --archive; a note in the archive has no place in the calendar.", text);
        var notHidden = text[text.IndexOf("Not hidden:", StringComparison.Ordinal)..text.IndexOf("Links prefer", StringComparison.Ordinal)];
        Assert.DoesNotContain("graph", notHidden);
        Assert.DoesNotContain("calendar", notHidden);
    }

    [Fact]
    public async Task Archive_help_names_graph_and_calendar()
    {
        var help = (await Run("archive --help")).Output;
        Assert.Contains("""

              ls, rg, semsearch, tags, backlinks and graph leave the archive out unless given --archive
              or a path inside the archive, and calendar always does (it has no --archive); hiding the
              archive is about attention, not access: whoever may read the vault may read it.

            """.ReplaceLineEndings("\n"), help);
        var text = System.Text.RegularExpressions.Regex.Replace(help, @"\s+", " ");
        Assert.Contains("ls, rg, semsearch, tags, backlinks and graph leave the archive out unless given --archive or a path inside the archive, and calendar always does (it has no --archive);", text);
    }

    [Fact]
    public async Task Attachments_topic_says_rg_files_lists_text_files_too()
    {
        var text = System.Text.RegularExpressions.Regex.Replace((await Run("help attachments")).Output, @"\s+", " ");
        Assert.Contains("'rg --files' lists notes and text files only (with --tag, notes only);", text);
        // Wrapped to the paragraph's width, no short line left over.
        Assert.Contains("\n  only (with --tag, notes only); neither rg nor semsearch searches images (an SVG's source neither) or\n", (await Run("help attachments")).Output);
        Assert.DoesNotContain("'rg --files' lists notes only", text);
    }

    [Fact]
    public async Task Attachments_topic_says_what_write_takes()
    {
        var text = System.Text.RegularExpressions.Regex.Replace((await Run("help attachments")).Output, @"\s+", " ");
        Assert.Contains("People upload them (the web app, Obsidian, the upload tool, which takes an SVG too).", text);
        Assert.Contains("The write tool takes notes, SVGs and a skill's text files, and everything else is uploaded; the edit and replace tools change notes only.", text);
        Assert.Contains("An SVG and a skill's text file are written whole, as a note is (no version creates one, the version replaces it);", text);
        Assert.DoesNotContain("since they only write text", text);
    }

    [Fact]
    public async Task No_usage_is_longer_than_graphs()
    {
        // help pads every row to the longest Usage: a longer one would push every summary to the right.
        var graph = new Kumbu.Core.Sh.Commands.GraphCommand(new InMemoryNoteStore());

        Assert.Contains($"\n  {graph.Usage}  {graph.Summary}\n", (await Run("help")).Output);
    }

    [Fact]
    public async Task Help_cat_says_what_each_kind_of_file_prints_and_when_the_embeds_block_is_left_out()
    {
        var help = (await Run("help cat")).Output;
        var flat = System.Text.RegularExpressions.Regex.Replace(help, @"\s+", " ");

        Assert.StartsWith("cat [--version V] <path>[#section] [<path>…]\n", help);
        Assert.Contains("An image or a PDF prints one line instead of its bytes", flat);
        Assert.Contains("An SVG prints its line and then its source", flat);
        Assert.Contains("A text file (a script, a CSV, an HTML or XML file) prints its content, wherever it is.", flat);
        Assert.Contains("Any other file prints one line: its kind and file size.", flat);
        Assert.Contains("No block in a pipeline, with --version or with --checkpoint.", flat);
        Assert.DoesNotContain("A text file in a skill's folder", flat);
        Assert.DoesNotContain("An attachment (an image or a PDF) prints one line", flat);
    }

    private static async Task<string> Flat(string line) =>
        System.Text.RegularExpressions.Regex.Replace((await Run(line)).Output, @"\s+", " ");

    [Fact]
    public async Task Help_calendar_names_the_vault_of_notifications_and_the_write_forms()
    {
        var row = (await Run("help")).Output.Split('\n').Single(l => l.StartsWith("  calendar ", StringComparison.Ordinal));
        Assert.StartsWith("  calendar <vault> [--week W|--month M|--from D --to D] [--tag T] | notifications [--tag T] [vault] | set|move|… <note>  ", row);
        Assert.Contains("\ncalendar notifications [--tag <tag>] [vault]\n", (await Run("help calendar")).Output);
    }

    [Fact]
    public async Task Help_calendar_names_today_before_a_time_and_the_year_of_a_week_number()
    {
        var help = await Flat("help calendar");
        Assert.Contains("'upcoming', 'ongoing' or 'overdue' with a span, or 'today · 14:00' for a time later today", help);
        Assert.Contains("--week W that week: 46 of the current week's ISO year, or 2026-W46", help);
        Assert.DoesNotContain("46 of the current year", help);
    }

    [Fact]
    public async Task Help_rg_names_text_files_in_l_and_c_and_that_l_needs_paths()
    {
        var help = (await Run("help rg")).Output;
        Assert.Contains("\n  -l, --files-with-matches   print only the paths of notes, text files and PDFs with a match (not piped)\n", help);
        Assert.Contains("\n  -c, --count                print path:count per note, text file or PDF (piped: one total)\n", help);
        Assert.Contains("\n  -m, --max-count N          at most N matching lines per note, text file or PDF\n", help);
    }

    [Fact]
    public async Task Help_trash_names_a_folders_row()
    {
        var help = await Flat("help trash");
        Assert.StartsWith("trash [path] List removed notes, attachments and folders", help);
        Assert.Contains("A removed folder (rm takes one that holds nothing) lists as its path with a '/' and 'folder': /Demo/Drafts/ folder (by Alice).", help);
    }

    [Fact]
    public async Task Help_history_says_any_attachment_and_how_a_notes_row_ends()
    {
        var help = await Flat("help history");
        Assert.Contains("version, time written (UTC), writer, lines, then '— message' if the write gave one; the current version ends in (current)", help);
        Assert.Contains("An attachment (any file: an image, a PDF, a text file …) lists the same way, with its kind and size instead of lines and no message.", help);
        Assert.DoesNotContain("(an image or a PDF)", help);
    }

    [Fact]
    public async Task Help_lscp_says_any_path_but_the_root_tells_how_many_the_vault_can_keep()
    {
        var help = await Flat("help lscp");
        Assert.Contains("Any path but / ends with how many checkpoints its vault has of the most it can keep: <n> of <limit> checkpoints in <vault>.", help);
        Assert.DoesNotContain("With a vault, that vault's checkpoints and how many more it can keep", help);
    }

    [Fact]
    public async Task Help_ls_l_says_it_changes_nothing_in_the_list_of_vaults()
    {
        var help = await Flat("help ls");
        Assert.Contains("an attachment its summary; columns aligned; the list of vaults stays as it is", help);
    }

    [Fact]
    public async Task Help_history_and_checkpoints_take_the_history_limit_from_its_constant()
    {
        Assert.Contains($"({Kumbu.Core.Notes.NoteWriter.HistoryLimit} versions per note", await Flat("help history"));
        Assert.Contains($"({Kumbu.Core.Notes.NoteWriter.HistoryLimit} versions per note", await Flat("help mkcp"));
    }

    [Fact]
    public async Task Help_rm_and_mv_take_any_file_as_an_attachment()
    {
        var rm = await Flat("help rm");
        Assert.Contains("Move notes and attachments (any file: an image, a PDF, a text file …) to the trash, or folders", rm);

        var mv = await Flat("help mv");
        Assert.Contains("An attachment (any file: an image, a PDF, a text file …) moves the same way", mv);
        Assert.Contains("an image's, a PDF's or an SVG's new name keeps the extension of its format (shot.jpg may become shot.jpeg, never shot.png: a new name doesn't change the bytes); no other file's may end in .md or in one of theirs.", mv);
        Assert.DoesNotContain("images or PDFs", rm);
        Assert.DoesNotContain("(an image or a PDF)", mv);

        var restore = await Flat("help restore");
        Assert.Contains("An attachment (any file) comes back the same way", restore);
        Assert.DoesNotContain("An image or a PDF comes back", restore);
    }
}
