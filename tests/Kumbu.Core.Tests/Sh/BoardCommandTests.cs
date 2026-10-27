// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

public class BoardCommandTests
{
    private const string Definition = "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Today\n  - Doing\n  - Done: [archivable]\n  - Rejected: [archivable, reason]\n---\n# Work\n\n## Instructions\nDon't let ideas go stale.\n";

    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc));

    private static string Card(string column, string rank, string extra = "") => $"---\ncolumn: {column}\nrank: {rank}\n{extra}---\nText\n";

    private (Shell Shell, InMemoryNoteStore Store, NoteWriter Writer) Make(params (string, string)[] notes)
    {
        var store = new InMemoryNoteStore([("/Work/Board/Board.md", Definition), .. notes]);
        var writer = new NoteWriter(store, _time, new FixedCaller(Writers.Dev));
        var shell = new Shell(store, writer, new NoteMover(store, _time, new FixedCaller(Writers.Dev)));
        return (shell, store, writer);
    }

    private static Task<ShellResult> Run(Shell shell, string line) => shell.RunAsync(line, CancellationToken.None);

    [Fact]
    public async Task Shows_instructions_lanes_and_titles()
    {
        var (shell, _, _) = Make(
            ("/Work/Board/Export needs the password.md", Card("Doing", "a", "due: 2026-11-03\ntags: [security, ops]\n")),
            ("/Work/Board/Kanban spec.md", Card("Doing", "b")),
            ("/Work/Board/Plan.md", Card("Today", "a", "tags:\n  - Q4\n")),
            ("/Work/Board/Old.md", Card("Done", "a")),
            ("/Work/Board/Archive/Gone.md", Card("Done", "b")));

        Assert.Equal(ShellResult.Ok("""
            ## Instructions
            Don't let ideas go stale.
            Ideas (0)
            Today (1)
              Plan  #Q4
            Doing (2)
              Export needs the password  due 2026-11-03  #security #ops
              Kanban spec
            Done (1) · Rejected (0, needs a reason): archivable; board /Work --column Done
            """), await Run(shell, "board /Work"));
    }

    [Fact]
    public async Task Cuts_long_lanes_at_twenty_with_a_hint()
    {
        var cards = Enumerable.Range(0, 25).Select(i => ($"/Work/Board/Idea {i:00}.md", Card("Ideas", $"{(char)('a' + i)}"))).ToArray();
        var (shell, _, _) = Make(cards);

        var lines = (await Run(shell, "board /Work")).Output.Split('\n');
        var start = Array.IndexOf(lines, "Ideas (25): first 20");
        Assert.True(start >= 0);
        Assert.Equal("  Idea 00", lines[start + 1]);
        Assert.Equal("  Idea 19", lines[start + 20]);
        Assert.Equal("  board /Work --column Ideas for all", lines[start + 21]);
        Assert.Equal("Today (0)", lines[start + 22]);

        var all = (await Run(shell, "board /Work --column ideas")).Output.Split('\n');
        Assert.Equal("Ideas (25)", all[0]);
        Assert.Equal("  Idea 24", all[^1]);
        Assert.Equal(26, all.Length);
    }

    [Fact]
    public async Task Archivable_lanes_show_counts_unless_asked()
    {
        var (shell, _, _) = Make(
            ("/Work/Board/A.md", Card("Done", "a")), ("/Work/Board/B.md", Card("Done", "b")), ("/Work/Board/C.md", Card("Rejected", "a", "reason: no\n")));

        var plain = (await Run(shell, "board /Work")).Output;
        Assert.DoesNotContain("  A", plain);
        Assert.EndsWith("Done (2) · Rejected (1, needs a reason): archivable; board /Work --column Done", plain);
        Assert.Equal("Rejected (1, needs a reason)\n  C", (await Run(shell, "board /Work --column Rejected")).Output);
        Assert.Equal("Done (2)\n  A\n  B", (await Run(shell, "board /Work --column Done")).Output);
    }

    [Fact]
    public async Task Marks_overdue()
    {
        var (shell, _, _) = Make(
            ("/Work/Board/Late.md", Card("Doing", "a", "due: 2026-10-26\n")),
            ("/Work/Board/Today.md", Card("Doing", "b", "due: 2026-10-27\n")),
            ("/Work/Board/Soon.md", Card("Doing", "c", "due: 2026-10-28\n")));

        Assert.Equal("Doing (3)\n  Late   overdue 2026-10-26\n  Today  due 2026-10-27\n  Soon   due 2026-10-28",
            (await Run(shell, "board /Work --column Doing")).Output);
    }

    [Fact]
    public async Task Filters_by_tag()
    {
        var (shell, _, _) = Make(
            ("/Work/Board/A.md", Card("Doing", "a", "tags: [Security]\n")),
            ("/Work/Board/B.md", Card("Doing", "b", "tags: [ops]\n")),
            ("/Work/Board/C.md", Card("Today", "a", "tags: [security]\n")));

        var output = (await Run(shell, "board /Work --tag '#security'")).Output;
        Assert.Contains("Doing (1)\n  A  #Security", output);
        Assert.Contains("Today (1)\n  C  #security", output);
        Assert.DoesNotContain("  B", output);
    }

    [Fact]
    public async Task Lists_problems_last()
    {
        var (shell, _, _) = Make(("/Work/Board/Old idea.md", "No frontmatter\n"), ("/Work/Board/A.md", Card("Doing", "a")));

        var output = (await Run(shell, "board /Work")).Output;
        Assert.EndsWith("Problems (1)\n  /Work/Board/Old idea.md: no column\n  the board tool's move puts a card with no column or an unknown one in a column", output);
    }

    [Fact]
    public async Task A_problem_move_cannot_fix_says_to_fix_the_note()
    {
        var (shell, _, _) = Make(("/Work/Board/Broken.md", "---\ncolumn: [\n---\n"), ("/Work/Board/Sub/Deep.md", Card("Doing", "a")));

        var output = (await Run(shell, "board /Work")).Output;
        Assert.EndsWith("  frontmatter that does not parse or a note in a sub-folder: fix the note (edit it, or mv it into Board)", output);
        Assert.DoesNotContain("the board tool's move", output);
    }

    [Fact]
    public async Task Without_a_board_says_how_to_make_one()
    {
        var store = new InMemoryNoteStore(("/Work/a.md", "x\n"));
        var shell = TestShell.Of(store);
        Assert.Equal(ShellResult.Ok("no board in /Work; the board tool's create makes one when the user asks for it"), await Run(shell, "board /Work"));
    }

    [Fact]
    public async Task A_broken_definition_is_an_error()
    {
        var shell = TestShell.Of(("/Work/Board/Board.md", "---\nkumbu-board: 2\ncolumns: [A]\n---\n"));
        var result = await Run(shell, "board /Work");
        Assert.True(result.IsError);
        Assert.StartsWith("board: Board.md has kumbu-board version 2", result.Output);
    }

    [Theory]
    [InlineData("board", "board: missing operand (board <vault>)")]
    [InlineData("board /Work --column Nope", "board: no column 'Nope' on the board (columns: Ideas, Today, Doing, Done, Rejected)")]
    [InlineData("board /Work/Board", "board: give a vault, e.g. board /Work")]
    [InlineData("board Work", "board: Work: paths must be absolute (there is no working directory)")]
    [InlineData("board /Work /Other", "board: too many arguments (quote paths and sections that contain spaces: cat \"/Work/My Note.md#Next steps\")")]
    public async Task Errors(string line, string error)
    {
        var (shell, _, _) = Make();
        Assert.Equal(ShellResult.Error(error), await Run(shell, line));
    }

    [Fact]
    public async Task Reads_a_checkpoint()
    {
        var (shell, _, writer) = Make(("/Work/Board/A.md", Card("Doing", "a")));
        Assert.False((await Run(shell, "mkcp /Work cp")).IsError);
        var current = Card("Doing", "a");
        Assert.False((await writer.WriteAsync("/Work/Board/A.md", Card("Today", "a"), VersionToken.Of(current), CancellationToken.None)).IsError);

        Assert.Contains("Today (1)\n  A", (await Run(shell, "board /Work")).Output);
        var old = await Run(shell, "board --checkpoint cp /Work");
        Assert.False(old.IsError);
        Assert.Contains("Doing (1)\n  A", old.Output);
        Assert.Contains("Today (0)", old.Output);
        Assert.Null(old.Version);
    }

    [Fact]
    public async Task Reads_a_checkpoint_of_the_board_folder()
    {
        var (shell, _, writer) = Make(("/Work/Board/A.md", Card("Doing", "a")));
        Assert.False((await Run(shell, "mkcp /Work/Board/ cp")).IsError);
        Assert.False((await writer.WriteAsync("/Work/Board/A.md", Card("Today", "a"), VersionToken.Of(Card("Doing", "a")), CancellationToken.None)).IsError);

        Assert.Contains("Doing (1)\n  A", (await Run(shell, "board --checkpoint cp /Work")).Output);
    }

    [Fact]
    public async Task Help_ends_with_the_checkpoint_pointer()
    {
        var (shell, _, _) = Make();
        var help = (await Run(shell, "help board")).Output;
        Assert.StartsWith("board <vault>", help);
        Assert.EndsWith("--checkpoint <name>: as a checkpoint holds it; see 'help checkpoints'", help.TrimEnd());
    }
}
