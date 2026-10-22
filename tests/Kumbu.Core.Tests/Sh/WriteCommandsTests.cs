// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>history, restore, rm and trash, through the shell.</summary>
public class WriteCommandsTests
{
    private readonly InMemoryNoteStore _store = new(("/Demo/a.md", "one\n"), ("/Demo/Meeting Notes.md", "m\n"));
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 22, 14, 3, 0, DateTimeKind.Utc));
    private readonly NoteWriter _writer;
    private readonly Shell _sh;

    public WriteCommandsTests()
    {
        _writer = new NoteWriter(_store, _time);
        _sh = new Shell(_store, _writer, new NoteMover(_store, _time));
    }

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    [Fact]
    public async Task HistoryListsCurrentThenEarlierVersions()
    {
        await _writer.WriteAsync("/Demo/a.md", "two\nlines\n", VersionToken.Of("one\n"), Writers.Dev, CancellationToken.None);

        var result = await Run("history /Demo/a.md");

        Assert.Equal(
            $"""
            {VersionToken.Of("two\nlines\n")}  2026-10-22 14:03 UTC  dev  2 lines  (current)
            {VersionToken.Of("one\n")}  2026-10-01 12:00 UTC  seed  1 line
            """,
            result.Output);
        Assert.Equal(VersionToken.Of("two\nlines\n"), result.Version!.Token);
    }

    [Theory]
    [InlineData("history /Demo/nope.md", "history: /Demo/nope.md: No such file or directory")]
    [InlineData("history /Demo", "history: /Demo: Is a directory")]
    [InlineData("history", "history: missing operand")]
    [InlineData("rm", "rm: missing operand")]
    [InlineData("rm /Demo/a.md /Demo/b.md", "rm: too many arguments (rm removes one note at a time)")]
    [InlineData("rm -r /Demo", "rm: invalid option -- 'r'")]
    [InlineData("restore", "restore: missing operand")]
    [InlineData("restore a.md", "restore: a.md: paths must be absolute (there is no working directory)")]
    [InlineData("trash /A /B", "trash: too many arguments")]
    [InlineData("cp /Demo/a.md /Demo/b.md", "command not found: cp — cat the note, then create the copy with the write tool")]
    public async Task Errors(string line, string error) => Assert.Equal(ShellResult.Error(error), await Run(line));

    [Fact]
    public async Task RmTrashRestoreRoundTrip()
    {
        Assert.False((await Run("rm \"/Demo/Meeting Notes.md\"")).IsError);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.False((await Run("rm /Demo/a.md")).IsError);

        Assert.Equal(
            $"""
            2026-10-22 14:08 UTC  /Demo/a.md  {VersionToken.Of("one\n")}  1 line  (by dev)
            2026-10-22 14:03 UTC  /Demo/Meeting Notes.md  {VersionToken.Of("m\n")}  1 line  (by dev)
            """,
            (await Run("trash")).Output);
        // Until vaults are real objects (steps 5 and 7), a vault exists only through its notes.
        Assert.Equal(ShellResult.Error("ls: cannot access '/Demo': No such file or directory"), await Run("ls /Demo"));

        var restored = await Run("restore \"/Demo/Meeting Notes.md\"");
        Assert.Equal("restored /Demo/Meeting Notes.md from the trash", restored.Output);
        Assert.Equal($"2026-10-22 14:08 UTC  /Demo/a.md  {VersionToken.Of("one\n")}  1 line  (by dev)", (await Run("trash /Demo")).Output);
        Assert.Equal("m\n", (await Run("cat \"/Demo/Meeting Notes.md\"")).Output);
    }

    [Fact]
    public async Task RmMessageQuotesPathsWithSpaces() => Assert.Equal(
        "removed /Demo/Meeting Notes.md; it stays in the trash for 30 days ('restore \"/Demo/Meeting Notes.md\"' brings it back)",
        (await Run("rm \"/Demo/Meeting Notes.md\"")).Output);

    [Fact]
    public async Task RestoreAVersionFromHistory()
    {
        await _writer.WriteAsync("/Demo/a.md", "two\n", VersionToken.Of("one\n"), Writers.Dev, CancellationToken.None);

        var result = await Run($"restore /Demo/a.md {VersionToken.Of("one\n")}");

        Assert.False(result.IsError, result.Output);
        Assert.Equal("one\n", (await Run("cat /Demo/a.md")).Output);
    }

    [Theory]
    [InlineData("ls /Demo | rm")]
    [InlineData("ls /Demo | restore")]
    public async Task WritingCommandsDoNotReadFromAPipe(string line) =>
        Assert.EndsWith("does not read from a pipe; only rg, head, tail, wc, sort do", (await Run(line)).Output);

    [Fact]
    public async Task RestoreAnOlderRemovalByItsVersion()
    {
        await Run("rm /Demo/a.md");
        await _writer.WriteAsync("/Demo/a.md", "second\n", null, Writers.Dev, CancellationToken.None);
        await Run("rm /Demo/a.md");

        var result = await Run($"restore /Demo/a.md {VersionToken.Of("one\n")}");

        Assert.Equal("restored /Demo/a.md from the trash", result.Output);
        Assert.Equal("one\n", (await Run("cat /Demo/a.md")).Output);
        Assert.Contains(VersionToken.Of("second\n"), (await Run("trash")).Output);
    }

    [Fact]
    public async Task RestoreAVersionThatIsOnlyInTheTrashWhileTheNoteExists()
    {
        await Run("rm /Demo/a.md");
        await _writer.WriteAsync("/Demo/a.md", "new\n", null, Writers.Dev, CancellationToken.None);

        Assert.Equal(
            ShellResult.Error($"restore: version {VersionToken.Of("one\n")} of /Demo/a.md is in the trash, but a note exists at that path; rm it first, or edit it"),
            await Run($"restore /Demo/a.md {VersionToken.Of("one\n")}"));
    }

    [Fact]
    public async Task RestoreAnUnknownVersionFromTheTrash()
    {
        await Run("rm /Demo/a.md");
        Assert.Equal(
            ShellResult.Error("restore: /Demo/a.md has no version abc in the trash ('trash /Demo/a.md' lists them)"),
            await Run("restore /Demo/a.md abc"));
    }
}
