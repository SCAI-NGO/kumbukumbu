// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Notes;

public class NoteWriterTests
{
    private const string Plan = "# Plan\n\n- one\n- two\n";

    private readonly InMemoryNoteStore _store = new(("/Work/plan.md", Plan), ("/Work/sub/deep.md", "deep\n"));
    private readonly FakeTimeProvider _time = new(InMemoryNoteStore.Seeded.AddDays(1));
    private readonly NoteWriter _writer;

    public NoteWriterTests() => _writer = new NoteWriter(_store, _time);

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private async Task<StoredNote> Get(string path) => (await _store.GetAsync(P(path), Ct))!;

    private static string V(string content) => VersionToken.Of(content);

    private Task<ShellResult> Write(string path, string content, string? version, string writer = Writers.Dev) =>
        _writer.WriteAsync(path, content, version, writer, Ct);

    private Task<ShellResult> Edit(string path, string version, string? oldText, string newText, string writer = Writers.Dev) =>
        _writer.EditAsync(path, version, oldText, newText, writer, Ct);

    [Fact]
    public void TokenIsTwelveHexCharactersOfTheSha256() => Assert.Equal("2cf24dba5fb0", VersionToken.Of("hello"));

    // --- write: create ---

    [Fact]
    public async Task CreatesANoteInAnExistingVaultAndReturnsItsToken()
    {
        var result = await Write("/Work/new/idea.md", "# Idea\r\nline\r\n", null);

        Assert.Equal(ShellResult.Ok("created /Work/new/idea.md (2 lines)") with
        {
            Version = new NoteVersion("/Work/new/idea.md", V("# Idea\nline\n")),
        }, result);
        var note = await Get("/Work/new/idea.md");
        Assert.Equal("# Idea\nline\n", note.Content);
        Assert.Equal(Writers.Dev, note.LastWriter);
        Assert.Empty(note.History);
    }

    [Theory]
    [InlineData("/Nope/x.md", "write: there is no vault 'Nope' (ls lists the vaults; writing does not create new ones)")]
    [InlineData("/Work/plan.md", "write: /Work/plan.md already exists; to overwrite it, pass the version from 'cat /Work/plan.md'")]
    [InlineData("/Work/sub", "write: /Work/sub: note names end in .md")]
    [InlineData("/Work/notes.txt", "write: /Work/notes.txt: note names end in .md")]
    [InlineData("/Work.md", "write: /Work.md: notes live inside a vault, e.g. /Work/plan.md")]
    [InlineData("/Work/plan.md/x.md", "write: /Work/plan.md: Not a directory")]
    [InlineData("/Work/a:b.md", "write: /Work/a:b.md: 'a:b.md' contains ':', which Windows can't store")]
    [InlineData("/Work/q?.md", "write: /Work/q?.md: 'q?.md' contains '?', which Windows can't store")]
    [InlineData("/Work/dir./x.md", "write: /Work/dir./x.md: 'dir.' ends with a dot or space, which Windows can't store")]
    [InlineData("/Work/con.md", "write: /Work/con.md: 'con.md' is a reserved name on Windows")]
    [InlineData("/Work/LPT1/x.md", "write: /Work/LPT1/x.md: 'LPT1' is a reserved name on Windows")]
    [InlineData("/Work/.hidden.md", "write: /Work/.hidden.md: '.hidden.md' starts with '.', which hides it in Obsidian")]
    [InlineData("Work/x.md", "write: Work/x.md: paths must be absolute, e.g. /Work/plan.md (there is no working directory)")]
    [InlineData("/", "write: /: paths must be absolute, e.g. /Work/plan.md (there is no working directory)")]
    public async Task RejectsBadNewNotes(string path, string error) =>
        Assert.Equal(ShellResult.Error(error), await Write(path, "x", null));

    [Fact]
    public async Task RejectsAControlCharacter() =>
        Assert.Equal(ShellResult.Error("write: /Work/a\tb.md: 'a\tb.md' contains a control character"), await Write("/Work/a\tb.md", "x", null));

    [Fact]
    public async Task RejectsMoreThanOneMebibyte()
    {
        var result = await Write("/Work/big.md", new string('ä', 600_000), null);
        Assert.Equal(ShellResult.Error("write: the note would be 1200000 bytes; the limit is 1048576 (1 MiB)"), result);
    }

    // --- write: overwrite ---

    [Fact]
    public async Task OverwritesWithTheCurrentTokenAndKeepsTheOldStateInTheHistory()
    {
        var result = await Write("/Work/plan.md", "new\n", V(Plan));

        Assert.Equal("wrote /Work/plan.md (1 line)", result.Output);
        Assert.Equal(V("new\n"), result.Version!.Token);
        var note = await Get("/Work/plan.md");
        Assert.Equal("new\n", note.Content);
        var entry = Assert.Single(note.History);
        Assert.Equal(new HistoryEntry(Plan, Writers.Seed, InMemoryNoteStore.Seeded), entry);
        Assert.Equal(Plan, note.SeedContent);
    }

    [Fact]
    public async Task StaleTokenIsAConflict() => Assert.Equal(
        ShellResult.Error($"write: /Work/plan.md has changed since version 000000000000 (it is now {V(Plan)}); cat it again and redo the change"),
        await Write("/Work/plan.md", "new", "000000000000"));

    [Fact]
    public async Task OverwritingAMissingNotePointsToTheTrash() => Assert.Equal(
        ShellResult.Error("write: /Work/gone.md: No such file or directory (if it was removed, 'trash' lists it)"),
        await Write("/Work/gone.md", "x", V("x")));

    [Fact]
    public async Task WritingTheSameContentChangesNothing()
    {
        var result = await Write("/Work/plan.md", Plan, V(Plan));
        Assert.Equal("/Work/plan.md is unchanged", result.Output);
        Assert.Equal(1, (await Get("/Work/plan.md")).Revision);
    }

    [Fact]
    public async Task AWriterThatGetsThereFirstWins()
    {
        _store.Interfere = note => note with { Content = "someone else\n" };

        var result = await Write("/Work/plan.md", "mine\n", V(Plan));

        Assert.Equal(
            ShellResult.Error($"write: /Work/plan.md has changed since version {V(Plan)} (it is now {V("someone else\n")}); cat it again and redo the change"),
            result);
        Assert.Equal("someone else\n", (await Get("/Work/plan.md")).Content);
    }

    // --- edit ---

    [Fact]
    public async Task ReplacesTheOneOccurrence()
    {
        var result = await Edit("/Work/plan.md", V(Plan), "- two", "- zwei\n- drei");

        Assert.Equal("edited /Work/plan.md at line 4", result.Output);
        Assert.Equal("# Plan\n\n- one\n- zwei\n- drei\n", (await Get("/Work/plan.md")).Content);
        Assert.Equal(V("# Plan\n\n- one\n- zwei\n- drei\n"), result.Version!.Token);
    }

    [Fact]
    public async Task OldTextWithCrLfMatchesLf()
    {
        await Edit("/Work/plan.md", V(Plan), "- one\r\n- two", "- both");
        Assert.Equal("# Plan\n\n- both\n", (await Get("/Work/plan.md")).Content);
    }

    [Fact]
    public async Task AppendsOnANewLine()
    {
        var result = await Edit("/Work/plan.md", V(Plan), null, "- three\n");
        Assert.Equal("appended to /Work/plan.md (now 5 lines)", result.Output);
        Assert.Equal(Plan + "- three\n", (await Get("/Work/plan.md")).Content);
    }

    [Fact]
    public async Task AppendAddsTheMissingLineBreak()
    {
        var created = await Write("/Work/raw.md", "no newline", null);
        await Edit("/Work/raw.md", created.Version!.Token, null, "next");
        Assert.Equal("no newline\nnext", (await Get("/Work/raw.md")).Content);
    }

    [Fact]
    public async Task OldTextNotFound() => Assert.Equal(
        ShellResult.Error("edit: old_text not found in /Work/plan.md; it must match exactly, including whitespace and line breaks (cat it to see the current text)"),
        await Edit("/Work/plan.md", V(Plan), "- three", "x"));

    [Fact]
    public async Task OldTextMustBeUnique() => Assert.Equal(
        ShellResult.Error("edit: old_text occurs 2 times in /Work/plan.md; include more of the surrounding text so it matches once, or put the section in the path (note.md#Heading)"),
        await Edit("/Work/plan.md", V(Plan), "- ", "* "));

    [Fact]
    public async Task EmptyOldTextIsAnError() =>
        Assert.Equal(ShellResult.Error("edit: old_text is empty; leave it out to append"), await Edit("/Work/plan.md", V(Plan), "", "x"));

    [Fact]
    public async Task EditNeedsTheCurrentToken() =>
        Assert.StartsWith("edit: /Work/plan.md has changed since version abc", (await Edit("/Work/plan.md", "abc", "- one", "x")).Output);

    [Fact]
    public async Task EditOfAMissingNote() => Assert.Equal(
        ShellResult.Error("edit: /Work/nope.md: No such file or directory (if it was removed, 'trash' lists it)"),
        await Edit("/Work/nope.md", "abc", null, "x"));

    [Fact]
    public async Task EditTooLarge() =>
        Assert.StartsWith("edit: the note would be", (await Edit("/Work/plan.md", V(Plan), null, new string('x', 1_048_576))).Output);

    // --- history and coalescing ---

    [Fact]
    public async Task WritesBySameWriterWithinTenMinutesCoalesce()
    {
        var v1 = (await Edit("/Work/plan.md", V(Plan), null, "a")).Version!.Token;
        _time.Advance(TimeSpan.FromMinutes(9));
        var v2 = (await Edit("/Work/plan.md", v1, null, "b")).Version!.Token;
        _time.Advance(TimeSpan.FromMinutes(9));
        await Edit("/Work/plan.md", v2, null, "c");

        // Only the state before the session survives; the intermediate ones were one session's steps.
        var note = await Get("/Work/plan.md");
        Assert.Equal([Plan], note.History.Select(h => h.Content));
    }

    [Fact]
    public async Task APauseOrAnotherWriterStartsANewVersion()
    {
        var v1 = (await Edit("/Work/plan.md", V(Plan), null, "a")).Version!.Token;
        _time.Advance(TimeSpan.FromMinutes(10));
        var v2 = (await Edit("/Work/plan.md", v1, null, "b")).Version!.Token;
        await Edit("/Work/plan.md", v2, null, "c", writer: "other");

        var note = await Get("/Work/plan.md");
        Assert.Equal([Plan + "a\nb", Plan + "a", Plan], note.History.Select(h => h.Content));
    }

    [Fact]
    public async Task HistoryKeepsTheLastTen()
    {
        var version = V(Plan);
        for (var i = 0; i < 12; i++)
        {
            _time.Advance(TimeSpan.FromHours(1));
            version = (await Write("/Work/plan.md", $"v{i}", version)).Version!.Token;
        }

        var note = await Get("/Work/plan.md");
        Assert.Equal(NoteWriter.HistoryLimit, note.History.Count);
        Assert.Equal("v10", note.History[0].Content);
        Assert.Equal("v1", note.History[^1].Content);
    }

    [Fact]
    public async Task RestoreMakesAVersionCurrentAndKeepsTheReplacedOne()
    {
        var v1 = (await Write("/Work/plan.md", "bad\n", V(Plan))).Version!.Token;

        var result = await _writer.RestoreAsync(P("/Work/plan.md"), V(Plan), Writers.Dev, Ct);

        Assert.Equal($"restored /Work/plan.md to version {V(Plan)}", result.Output);
        var note = await Get("/Work/plan.md");
        Assert.Equal(Plan, note.Content);
        // Not coalesced, although it is the same writer within ten minutes: a restore can be undone.
        Assert.Equal(["bad\n", Plan], note.History.Select(h => h.Content));
        Assert.Equal(v1, VersionToken.Of(note.History[0].Content));
    }

    [Fact]
    public async Task RestoreOfAnUnknownVersion() => Assert.Equal(
        ShellResult.Error("restore: /Work/plan.md has no version abc ('history /Work/plan.md' lists them)"),
        await _writer.RestoreAsync(P("/Work/plan.md"), "abc", Writers.Dev, Ct));

    [Fact]
    public async Task RestoreToTheCurrentVersionChangesNothing() => Assert.Equal(
        $"/Work/plan.md is already at version {V(Plan)}",
        (await _writer.RestoreAsync(P("/Work/plan.md"), V(Plan), Writers.Dev, Ct)).Output);

    // --- trash ---

    [Fact]
    public async Task RemoveMovesToTheTrashAndRestoreBringsItBack()
    {
        var removed = await _writer.RemoveAsync(P("/Work/plan.md"), Writers.Dev, Ct);
        Assert.Equal("removed /Work/plan.md; it stays in the trash for 30 days ('restore /Work/plan.md' brings it back)", removed.Output);
        Assert.Null(await _store.GetAsync(P("/Work/plan.md"), Ct));

        var restored = await _writer.RestoreAsync(P("/Work/plan.md"), null, Writers.Dev, Ct);

        Assert.Equal("restored /Work/plan.md from the trash", restored.Output);
        Assert.Equal(V(Plan), restored.Version!.Token);
        Assert.Equal(Plan, (await Get("/Work/plan.md")).Content);
    }

    [Fact]
    public async Task RestoreFromTheTrashDoesNotOverwriteANewNote()
    {
        await _writer.RemoveAsync(P("/Work/plan.md"), Writers.Dev, Ct);
        await Write("/Work/plan.md", "new\n", null);

        Assert.Equal(
            ShellResult.Error("restore: /Work/plan.md exists; to go back to an earlier version, give one from 'history /Work/plan.md'"),
            await _writer.RestoreAsync(P("/Work/plan.md"), null, Writers.Dev, Ct));
    }

    [Fact]
    public async Task RestoreOfSomethingNotInTheTrash() => Assert.Equal(
        ShellResult.Error("restore: /Work/none.md is not in the trash ('trash' lists what is)"),
        await _writer.RestoreAsync(P("/Work/none.md"), null, Writers.Dev, Ct));

    [Theory]
    [InlineData("/Work/sub", "rm: /Work/sub: Is a directory (rm removes one note at a time)")]
    [InlineData("/Work/none.md", "rm: /Work/none.md: No such file or directory")]
    public async Task RemoveErrors(string path, string error) =>
        Assert.Equal(ShellResult.Error(error), await _writer.RemoveAsync(P(path), Writers.Dev, Ct));

    [Fact]
    public async Task AWriteRightAfterARestoreDoesNotSwallowIt()
    {
        var bad = (await Write("/Work/plan.md", "bad\n", V(Plan))).Version!.Token;
        var restored = (await _writer.RestoreAsync(P("/Work/plan.md"), V(Plan), Writers.Dev, Ct)).Version!.Token;
        Assert.Equal("dev (restore)", (await Get("/Work/plan.md")).LastWriter);

        await Write("/Work/plan.md", "after\n", restored);

        // The restored state stays in the history although the same writer wrote within ten minutes.
        Assert.Equal([Plan, "bad\n", Plan], (await Get("/Work/plan.md")).History.Select(h => h.Content));
        Assert.NotEqual(bad, restored);
    }

    [Fact]
    public void AStateAlreadyOnTopOfTheHistoryIsNotStoredTwice()
    {
        var note = new StoredNote(P("/Work/a.md"), "x", 3, "other", InMemoryNoteStore.Seeded, [new HistoryEntry("x", "seed", InMemoryNoteStore.Seeded)]);
        var next = NoteWriter.Next(note, "y", Writers.Dev, InMemoryNoteStore.Seeded.AddDays(1));
        Assert.Equal(["x"], next.History.Select(h => h.Content));
    }
}
