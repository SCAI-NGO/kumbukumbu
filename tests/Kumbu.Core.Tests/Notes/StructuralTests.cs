// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Notes;

/// <summary>A vault's structure (its <c>Board.md</c>, its checkpoints) changes only for those who may maintain it.</summary>
public class StructuralTests
{
    private static readonly string Board = BoardTemplate.Text("Work");

    private readonly InMemoryNoteStore _store = new(
        ("/Work/Board/Board.md", Board), ("/Work/Board/card.md", "a card\n"), ("/Work/Old/Board/Board.md", "an old board\n"),
        ("/Work/plan.md", "one\n"));

    private readonly FakeTimeProvider _time = new(InMemoryNoteStore.Seeded.AddDays(1));

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static ICaller At(AccessLevel level) => new FixedCaller(level.ToString().ToLowerInvariant(), LevelOf: _ => level);

    private static readonly ICaller Writer = At(AccessLevel.Write);
    private static readonly ICaller Maintainer = At(AccessLevel.Maintain);

    private Shell ShellFor(ICaller caller) => new(_store, new NoteWriter(_store, _time, caller), new NoteMover(_store, _time, caller));

    private async Task<string> SnapshotAsync()
    {
        var notes = await _store.ReadNotesUnderAsync(VaultPath.Root, Ct);
        var trash = await _store.ListTrashAsync(VaultPath.Root, Ct);
        var checkpoints = await _store.ListCheckpointsAsync(VaultPath.Root, Ct);
        return string.Join('|', notes.OrderBy(n => n.Path.Value, StringComparer.Ordinal).Select(n => $"{n.Path.Value}={n.Content}"))
            + $"|trash={trash.Count}|checkpoints={string.Join(',', checkpoints.Select(c => c.Name))}";
    }

    private async Task<ShellResult> RunAsync(string command, ICaller caller)
    {
        var writer = new NoteWriter(_store, _time, caller);
        var version = VersionToken.Of(Board);
        return command switch
        {
            "write" => await writer.WriteAsync("/Work/Board/Board.md", "---\nkumbu-board: 1\ncolumns: [A, B]\n---\n", version, Ct),
            "edit" => await writer.EditAsync("/Work/Board/Board.md", version, "Doing", "Working", Ct),
            "replace" => await new Replacer(_store, _time, new Plans(), caller).RunAsync("/Work/Board/Board.md", "Doing", "Working", false, null, Ct),
            "rm" => await ShellFor(caller).RunAsync("rm /Work/Board/Board.md", Ct),
            "upload" => (await new AttachmentWriter(_store, _store, _time, caller).WriteAsync("/Work/Board/Board.md", "x"u8.ToArray(), null, null, Ct)).Result,
            _ => throw new ArgumentException(command),
        };
    }

    private static void AssertStructural(ShellResult result, string prefix)
    {
        Assert.True(result.IsError, result.Output);
        Assert.Equal(Refusals.Structural, result.Code);
        Assert.StartsWith($"{prefix}: Only those who may maintain *Work* can", result.Output);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("replace")]
    [InlineData("rm")]
    [InlineData("upload")]
    public async Task A_writer_is_refused_every_write_to_Board_md(string command)
    {
        var before = await SnapshotAsync();

        var result = await RunAsync(command, Writer);

        AssertStructural(result, command);
        Assert.Equal($"{command}: Only those who may maintain *Work* can change its board's columns.", result.Output);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task A_writer_may_not_replace_text_in_Board_md_through_its_folder()
    {
        var before = await SnapshotAsync();
        var plans = new Plans();

        var refused = await new Replacer(_store, _time, plans, Writer).RunAsync("/Work", "Doing", "Working", false, null, Ct);
        var preview = await new Replacer(_store, _time, plans, Maintainer).RunAsync("/Work", "Doing", "Working", false, null, Ct);
        var token = preview.Output.Split('\n').Single(l => l.StartsWith("plan: ", StringComparison.Ordinal))["plan: ".Length..];
        var applied = await new Replacer(_store, _time, plans, Writer).RunAsync("/Work", "Doing", "Working", false, token, Ct);

        AssertStructural(refused, "replace");
        AssertStructural(applied, "replace");
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task A_writer_is_refused_mkcp_and_rmcp()
    {
        await ShellFor(Maintainer).RunAsync("mkcp /Work before", Ct);
        var before = await SnapshotAsync();
        var sh = ShellFor(Writer);

        var made = await sh.RunAsync("mkcp /Work/plan.md after", Ct);
        var removed = await sh.RunAsync("rmcp /Work before", Ct);

        AssertStructural(made, "mkcp");
        Assert.Equal("mkcp: Only those who may maintain *Work* can make or remove its checkpoints.", made.Output);
        AssertStructural(removed, "rmcp");
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task A_maintainer_may_do_all_of_it()
    {
        var sh = ShellFor(Maintainer);

        Assert.False((await RunAsync("edit", Maintainer)).IsError);
        Assert.Contains("Working", await _store.ReadAsync(P("/Work/Board/Board.md"), Ct));
        Assert.False((await sh.RunAsync("mkcp /Work before", Ct)).IsError);
        Assert.False((await sh.RunAsync("rmcp /Work before", Ct)).IsError);
        Assert.False((await RunAsync("rm", Maintainer)).IsError);
        Assert.Null(await _store.ReadAsync(P("/Work/Board/Board.md"), Ct));
    }

    [Fact]
    public async Task A_reader_gets_read_only_not_structural()
    {
        var reader = At(AccessLevel.Read);

        var written = await RunAsync("write", reader);
        var made = await ShellFor(reader).RunAsync("mkcp /Work before", Ct);

        Assert.Equal(Refusals.ReadOnly, written.Code);
        Assert.Equal("write: this key can only read vault Work", written.Output);
        Assert.Equal(Refusals.ReadOnly, made.Code);
    }

    [Fact]
    public async Task A_writer_may_write_other_notes_in_the_board_folder()
    {
        var writer = new NoteWriter(_store, _time, Writer);

        var card = await writer.WriteAsync("/Work/Board/card.md", "a moved card\n", VersionToken.Of("a card\n"), Ct);
        var old = await writer.WriteAsync("/Work/Old/Board/Board.md", "still old\n", VersionToken.Of("an old board\n"), Ct);

        Assert.False(card.IsError, card.Output);
        Assert.False(old.IsError, old.Output);
        Assert.Equal("a moved card\n", await _store.ReadAsync(P("/Work/Board/card.md"), Ct));
    }
}
