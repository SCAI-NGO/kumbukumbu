// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tags;
using Kumbu.Core.Tests.Fakes;
using Kumbu.Core.Tests.Sh;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Notes;

/// <summary>The record each write path leaves (blame spec §5.4): computed by the store from the state it replaces,
/// whoever built the next state, and whatever record that state carries.</summary>
public class AuthorshipWriteTests
{
    private const string Protocol = "# Protocol\nstep one\nstep two\nstep three\n";

    private readonly InMemoryNoteStore _store = new(("/Work/protocol.md", Protocol), ("/Work/links.md", "intro\nsee [[protocol]]\nend\n"));
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc));

    private static readonly FixedCaller Brita = new("Brita", Human: true) { Reference = WriterRef.ForOwner(WriterRef.Session, null) };
    private static readonly FixedCaller Veyalt = new("Veyalt") { Reference = WriterRef.ForOwner(WriterRef.Agent, "k1") };

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private NoteWriter Writer(ICaller caller) => new(_store, _time, caller);

    private Shell Shell(ICaller caller)
    {
        var writer = Writer(caller);
        return new Shell(_store, writer, new NoteMover(_store, _time, caller), new Checkpoints(_store, writer, new Plans()));
    }

    private async Task Run(ICaller caller, string line)
    {
        _time.Advance(TimeSpan.FromMinutes(1));
        var result = await Shell(caller).RunAsync(line, Ct);
        Assert.False(result.IsError, result.Output);
    }

    private async Task<string> Version(string path) => (await _store.GetAsync(P(path), Ct))!.Version;

    private async Task Write(ICaller caller, string path, string content)
    {
        _time.Advance(TimeSpan.FromMinutes(1));
        var result = await Writer(caller).WriteAsync(path, content, await Version(path), Ct);
        Assert.False(result.IsError, result.Output);
    }

    /// <summary>The stored record's writer of each line; the record must describe the stored content.</summary>
    private async Task<string[]> Lines(string path)
    {
        var note = (await _store.GetAsync(P(path), Ct))!;
        Assert.NotNull(note.Authorship);
        Assert.Equal(VersionToken.Hash(note.Content), note.Authorship.Hash);
        return [.. note.Authorship.ByLine().Select(w => w?.Label ?? "-")];
    }

    [Fact]
    public async Task A_write_gives_the_changed_lines_to_the_writer()
    {
        await Write(Veyalt, "/Work/protocol.md", "# Protocol\nstep one\nstep 2\nstep three\nstep four\n");

        Assert.Equal(["seed", "seed", "Veyalt", "seed", "Veyalt"], await Lines("/Work/protocol.md"));
    }

    [Fact]
    public async Task An_edit_gives_the_edited_line_to_the_editor()
    {
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False((await Writer(Brita).EditAsync("/Work/protocol.md", await Version("/Work/protocol.md"), "step two", "step two, done", Ct)).IsError);

        Assert.Equal(["seed", "seed", "Brita", "seed"], await Lines("/Work/protocol.md"));
    }

    [Fact]
    public async Task A_replace_across_notes_gives_the_changed_lines_to_the_replacer_marked()
    {
        var replacer = new Replacer(_store, _time, new Plans(), Veyalt);
        var preview = await replacer.RunAsync("/Work", "step", "Step", false, null, Ct);
        var plan = preview.Output.Split('\n').Single(l => l.StartsWith("plan: ", StringComparison.Ordinal))["plan: ".Length..];
        Assert.False((await replacer.RunAsync("/Work", "step", "Step", false, plan, Ct)).IsError);

        Assert.Equal(["seed", "Veyalt (replace)", "Veyalt (replace)", "Veyalt (replace)"], await Lines("/Work/protocol.md"));
    }

    [Fact]
    public async Task A_plain_move_keeps_the_record()
    {
        await Write(Veyalt, "/Work/protocol.md", "# Protocol\nstep one\nstep 2\nstep three\n");
        var before = (await _store.GetAsync(P("/Work/protocol.md"), Ct))!.Authorship;

        await Run(Brita, "mv --keep-links /Work/protocol.md /Work/Done/protocol.md");

        Assert.Same(before, (await _store.GetAsync(P("/Work/Done/protocol.md"), Ct))!.Authorship);
        Assert.Equal(["seed", "seed", "Veyalt", "seed"], await Lines("/Work/Done/protocol.md"));
    }

    [Fact]
    public async Task A_move_gives_the_lines_whose_links_it_rewrites_to_the_mover_marked()
    {
        _store.Put(new StoredNote(P("/Work/b.md"), "# B\nsee [[b]]\nend\n", 1, "seed", InMemoryNoteStore.Seeded, [], Ref: WriterRef.Server));

        // The moved note's link to itself, and a linking note's link to the renamed one.
        await Run(Brita, "mv /Work/b.md /Work/c.md");
        await Run(Veyalt, "mv /Work/protocol.md /Work/minutes.md");

        Assert.Equal(["seed", "Brita (mv)", "seed"], await Lines("/Work/c.md"));
        Assert.Equal(["seed", "Veyalt (mv)", "seed"], await Lines("/Work/links.md"));
        // A note from before the record, moved as it is: what its history said at revision 1, though the move counted it up.
        Assert.Equal(["seed", "seed", "seed", "seed"], await Lines("/Work/minutes.md"));
    }

    [Fact]
    public async Task Archive_moves_the_note_with_its_record()
    {
        await Write(Veyalt, "/Work/protocol.md", "# Protocol\nstep one\nstep 2\nstep three\n");

        await Run(Brita, "archive /Work/protocol.md");

        var archived = (await _store.ListNotesUnderAsync(P("/Work"), Ct)).Single(p => p.Value.StartsWith("/Work/_archive/protocol ", StringComparison.Ordinal));
        Assert.Equal(["seed", "seed", "Veyalt", "seed"], await Lines(archived.Value));
    }

    [Fact]
    public async Task Rm_and_restore_from_the_trash_bring_the_record_back()
    {
        await Write(Veyalt, "/Work/protocol.md", "# Protocol\nstep one\nstep 2\nstep three\n");

        await Run(Brita, "rm /Work/protocol.md");
        await Run(Brita, "restore /Work/protocol.md");

        Assert.Equal(["seed", "seed", "Veyalt", "seed"], await Lines("/Work/protocol.md"));
    }

    [Fact]
    public async Task A_restore_of_a_version_gives_only_the_changed_lines_to_the_restorer_marked()
    {
        var first = await Version("/Work/protocol.md");
        await Write(Veyalt, "/Work/protocol.md", "# Protocol\nstep one\nstep 2\nstep three\n");
        await Write(Brita, "/Work/protocol.md", "# Protocol!\nstep one\nstep 2\nstep three\n");

        await Run(Brita, $"restore /Work/protocol.md {first}");

        Assert.Equal(["Brita (restore)", "seed", "Brita (restore)", "seed"], await Lines("/Work/protocol.md"));
    }

    [Fact]
    public async Task A_checkpoint_restore_gives_the_changed_lines_to_the_restorer_marked()
    {
        await Run(Brita, "mkcp /Work before");
        await Write(Veyalt, "/Work/protocol.md", "# Protocol\nstep one\nstep 2\nstep three\n");

        await Run(Brita, "restore --checkpoint before /Work/protocol.md");

        Assert.Equal(["seed", "seed", "Brita (restore)", "seed"], await Lines("/Work/protocol.md"));
    }

    [Fact]
    public async Task A_checkpoint_restore_of_a_removed_note_is_a_new_note_of_the_restorer()
    {
        await Run(Brita, "mkcp /Work before");
        await Run(Veyalt, "rm /Work/protocol.md");
        _store.Put(new StoredNote(P("/Work/other.md"), "x\n", 1, "seed", InMemoryNoteStore.Seeded, [], Ref: WriterRef.Server));

        await Run(Brita, "restore --checkpoint before /Work/protocol.md");

        Assert.All(await Lines("/Work/protocol.md"), l => Assert.Equal("Brita (restore)", l));
    }

    [Fact]
    public async Task A_copy_is_a_new_note_every_line_the_copiers()
    {
        await Write(Veyalt, "/Work/protocol.md", "# Protocol\nstep one\nstep 2\nstep three\n");

        var copied = await new TreeWriter(_store, _time, Brita).CopyAsync(P("/Work/protocol.md"), P("/Work/copy.md"), deep: false, overwrite: false, Ct);
        Assert.False(copied.Result.IsError, copied.Result.Output);

        Assert.All(await Lines("/Work/copy.md"), l => Assert.Equal("Brita", l));
        Assert.Equal(["seed", "seed", "Veyalt", "seed"], await Lines("/Work/protocol.md"));
    }

    [Fact]
    public async Task Calendar_set_gives_only_the_front_matter_lines_it_writes()
    {
        await Run(Veyalt, "calendar set /Work/protocol.md --when 2026-11-20");

        var lines = await Lines("/Work/protocol.md");
        Assert.Equal(["seed", "seed", "seed", "seed"], lines[^4..]);
        Assert.All(lines[..^4], l => Assert.Equal("Veyalt", l));
    }

    [Fact]
    public async Task A_board_move_gives_only_the_column_line_to_the_mover()
    {
        const string definition = "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Doing\n---\n# Work\n";
        _store.Put(new StoredNote(P("/Work/Board/Board.md"), definition, 1, "seed", InMemoryNoteStore.Seeded, [], Ref: WriterRef.Server));
        _store.Put(new StoredNote(P("/Work/Board/Card.md"), "---\ncolumn: Ideas\nrank: m\n---\nText\n", 1, "seed", InMemoryNoteStore.Seeded, [], Ref: WriterRef.Server));
        var board = new BoardService(_store, Writer(Veyalt), new NoteMover(_store, _time, Veyalt), _time, Veyalt);

        _time.Advance(TimeSpan.FromMinutes(1));
        var moved = await board.MoveAsync("Work", "Card", "Doing", null, null, Ct);
        Assert.False(moved.IsError, moved.Output);

        var note = (await _store.GetAsync(P("/Work/Board/Card.md"), Ct))!;
        var lines = await Lines("/Work/Board/Card.md");
        var split = Kumbu.Core.Sh.Lines.Split(note.Content);
        for (var i = 0; i < split.Count; i++)
        {
            Assert.Equal(split[i] is "---" or "Text" ? "seed" : "Veyalt", lines[i]);
        }
    }

    [Fact]
    public async Task A_flag_gives_only_the_tags_line_to_the_flagger()
    {
        _store.Put(new StoredNote(P("/Work/flagged.md"), "---\ntags: [a]\n---\nText\n", 1, "seed", InMemoryNoteStore.Seeded, [], Ref: WriterRef.Server));
        var flags = new FlagService(_store, Writer(Brita), () => _time.GetUtcNow().UtcDateTime, new FixedZone(TimeZoneInfo.Utc));

        Assert.False((await flags.SetAsync("/Work/flagged.md", "red", FlagTask.None, Ct)).IsError);

        Assert.Equal(["seed", "Brita", "seed", "seed"], await Lines("/Work/flagged.md"));
    }

    [Fact]
    public async Task A_record_on_the_state_a_caller_stores_is_ignored()
    {
        var forged = new Authorship(VersionToken.Hash("mine\n"), null, false, [new("Brita", Brita.Reference, _time.GetUtcNow().UtcDateTime)], [new(1, 0)]);
        var current = (await _store.GetAsync(P("/Work/protocol.md"), Ct))!;
        var next = NoteWriter.Next(current, Protocol + "more\n", "Veyalt", Veyalt.Reference, _time.GetUtcNow().UtcDateTime) with { Authorship = forged };
        var created = new StoredNote(P("/Work/new.md"), "a\nb\n", 1, "Veyalt", _time.GetUtcNow().UtcDateTime, [], Ref: Veyalt.Reference, Authorship: forged);

        Assert.True(await _store.TryReplaceAsync(current, next, Ct));
        Assert.True(await _store.TryCreateAsync(created, Ct));

        Assert.Equal(["seed", "seed", "seed", "seed", "Veyalt"], await Lines("/Work/protocol.md"));
        Assert.Equal(["Veyalt", "Veyalt"], await Lines("/Work/new.md"));
    }
}
