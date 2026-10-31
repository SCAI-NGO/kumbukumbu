// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Notes;

/// <summary>Moves and trash records carry the changeset of the write that made them.</summary>
public class ChangesetRecordTests
{
    private readonly InMemoryNoteStore _store = new(("/Work/old.md", "old\n"), ("/Work/older.md", "older\n"));
    private readonly FakeTimeProvider _time = new(InMemoryNoteStore.Seeded.AddDays(1));
    private readonly NoteWriter _writer;
    private readonly Shell _sh;

    public ChangesetRecordTests()
    {
        _store.AddVault("Work");
        _writer = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        _sh = new Shell(_store, _writer, new NoteMover(_store, _time, new FixedCaller(Writers.Dev)), new Checkpoints(_store, _writer, new Plans()));
    }

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private async Task Run(string line) => Assert.False((await _sh.RunAsync(line, Ct)).IsError, line);

    private async Task Create(string path, string content) => Assert.False((await _writer.WriteAsync(path, content, null, Ct)).IsError);

    [Fact]
    public async Task A_move_records_the_writers_changeset()
    {
        await Create("/Work/a.md", "a\n");
        _time.Advance(TimeSpan.FromMinutes(1));

        await Run("mv /Work/a.md /Work/b.md");

        var move = Assert.Single(await _store.ListMovesAsync(P("/Work"), Ct));
        var note = (await _store.GetAsync(P("/Work/b.md"), Ct))!;
        Assert.NotNull(move.Changeset);
        Assert.Equal(note.Changeset!.Id, move.Changeset!.Id);
    }

    [Fact]
    public async Task A_removal_records_the_writers_changeset()
    {
        await Create("/Work/a.md", "a\n");
        await Create("/Work/c.md", "c\n");
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.False((await _writer.RemoveAsync(P("/Work/c.md"), Ct)).IsError);

        var trashed = Assert.Single(await _store.ListTrashAsync(P("/Work"), Ct));
        var a = (await _store.GetAsync(P("/Work/a.md"), Ct))!;
        Assert.Equal(a.Changeset!.Id, trashed.Changeset?.Id);
    }

    [Fact]
    public async Task Removing_two_notes_in_a_row_is_one_changeset()
    {
        Assert.False((await _writer.RemoveAsync(P("/Work/old.md"), Ct)).IsError);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False((await _writer.RemoveAsync(P("/Work/older.md"), Ct)).IsError);

        var trash = await _store.ListTrashAsync(P("/Work"), Ct);
        Assert.Equal(2, trash.Count);
        Assert.NotNull(trash[0].Changeset);
        Assert.Equal(trash[0].Changeset!.Id, trash[1].Changeset?.Id);
    }

    [Fact]
    public async Task A_folder_renamed_one_note_at_a_time_is_one_changeset_and_keeps_each_history()
    {
        // Obsidian renames a folder as one move per note, a moment apart.
        const int count = 30;
        for (var i = 0; i < count; i++) await Create($"/Work/dir/n{i}.md", $"n{i}\n");
        await Create("/Work/hub.md", string.Concat(Enumerable.Range(0, count).Select(i => $"[[dir/n{i}]]\n")));
        _time.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < count; i++) await _writer.WriteAsync($"/Work/dir/n{i}.md", $"n{i} edited\n", VersionToken.Of($"n{i}\n"), Ct);
        _time.Advance(TimeSpan.FromHours(1));

        var mover = new NoteMover(_store, _time, new FixedCaller("chris", Human: true));
        for (var i = 0; i < count; i++)
        {
            Assert.False((await mover.MoveAsync(P($"/Work/dir/n{i}.md"), P($"/Work/renamed/n{i}.md"), rewriteLinks: true, Ct)).IsError);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var moves = await _store.ListMovesAsync(P("/Work"), Ct);
        Assert.Equal(count, moves.Count);
        Assert.Single(moves.Select(m => m.Changeset?.Id).Distinct());
        for (var i = 0; i < count; i++)
        {
            var note = (await _store.GetAsync(P($"/Work/renamed/n{i}.md"), Ct))!;
            Assert.Equal($"n{i} edited\n", note.Content);
            Assert.Equal([$"n{i}\n"], note.History.Select(h => h.Content));
        }

        // The hub's links were rewritten thirty times in one changeset: one version, the state before the rename.
        var hub = (await _store.GetAsync(P("/Work/hub.md"), Ct))!;
        Assert.Equal(string.Concat(Enumerable.Range(0, count).Select(i => $"[[renamed/n{i}]]\n")), hub.Content);
        Assert.Equal([string.Concat(Enumerable.Range(0, count).Select(i => $"[[dir/n{i}]]\n"))], hub.History.Select(h => h.Content));
    }

    [Fact]
    public async Task A_restore_joins_no_changeset()
    {
        // A trash restore: the note comes back without a changeset.
        Assert.False((await _writer.RemoveAsync(P("/Work/old.md"), Ct)).IsError);
        await Run("restore /Work/old.md");
        Assert.Null((await _store.GetAsync(P("/Work/old.md"), Ct))!.Changeset);

        // A checkpoint restore that trashes a note created since: its trash record has none either.
        await Run("mkcp /Work before");
        await Create("/Work/new.md", "new\n");
        await Run("mv /Work/older.md /Work/moved.md");
        var preview = await _sh.RunAsync("restore --checkpoint before /Work/", Ct);
        var plan = preview.Output.Split('\n').Single(l => l.StartsWith("plan: ", StringComparison.Ordinal))["plan: ".Length..];
        await Run($"restore --checkpoint before --plan {plan} /Work");

        var trashed = (await _store.ListTrashAsync(P("/Work/new.md"), Ct)).Single();
        Assert.Null(trashed.Changeset);
        // ... and its move back has none either.
        var back = (await _store.ListMovesAsync(P("/Work"), Ct)).Single(m => m.From == P("/Work/moved.md"));
        Assert.Null(back.Changeset);
    }

    /// <summary>A writer for <paramref name="reference"/> with the label <paramref name="label"/>, on this test's store.</summary>
    private NoteWriter WriterFor(string label, WriterRef reference) => new(_store, _time, new FixedCaller(label) { Reference = reference });

    private async Task<Changeset> WriteAsync(NoteWriter writer, string path)
    {
        Assert.False((await writer.WriteAsync(path, path + "\n", null, Ct)).IsError);
        return (await _store.GetAsync(P(path), Ct))!.Changeset!;
    }

    [Fact]
    public async Task Two_writers_with_the_same_label_get_two_changesets()
    {
        var first = WriterFor("Claude", WriterRef.ForOwner(WriterRef.Agent, "key-1"));
        var second = WriterFor("Claude", WriterRef.ForOwner(WriterRef.Agent, "key-2"));

        var a = await WriteAsync(first, "/Work/a.md");
        _time.Advance(TimeSpan.FromMinutes(1));
        var b = await WriteAsync(second, "/Work/b.md");
        _time.Advance(TimeSpan.FromMinutes(1));
        var c = await WriteAsync(first, "/Work/c.md");

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.Id, c.Id);
    }

    [Fact]
    public async Task Two_members_storing_no_label_get_two_changesets()
    {
        var first = WriterFor("", WriterRef.ForMember("w1"));
        var second = WriterFor("", WriterRef.ForMember("w2"));

        var a = await WriteAsync(first, "/Work/a.md");
        _time.Advance(TimeSpan.FromMinutes(1));
        var b = await WriteAsync(second, "/Work/b.md");
        _time.Advance(TimeSpan.FromMinutes(1));
        var c = await WriteAsync(second, "/Work/c.md");

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(b.Id, c.Id);
    }

    [Fact]
    public async Task A_legacy_entry_continues_its_writers_changeset_by_label()
    {
        var before = Changeset.New(_time.GetUtcNow().UtcDateTime);
        Assert.True(await _store.TryCreateAsync(
            new StoredNote(P("/Work/legacy.md"), "x\n", 1, "Claude (mv)", _time.GetUtcNow().UtcDateTime, [], Changeset: before, Ref: WriterRef.Legacy), Ct));
        _time.Advance(TimeSpan.FromMinutes(1));

        var continued = await WriteAsync(WriterFor("Claude", WriterRef.ForOwner(WriterRef.Agent, "key-1")), "/Work/a.md");

        Assert.Equal(before.Id, continued.Id);
    }

    [Fact]
    public async Task A_member_never_continues_a_legacy_changeset()
    {
        var before = Changeset.New(_time.GetUtcNow().UtcDateTime);
        Assert.True(await _store.TryCreateAsync(
            new StoredNote(P("/Work/legacy.md"), "x\n", 1, "Claude", _time.GetUtcNow().UtcDateTime, [], Changeset: before, Ref: WriterRef.Legacy), Ct));
        _time.Advance(TimeSpan.FromMinutes(1));

        var own = await WriteAsync(WriterFor("Claude", WriterRef.ForMember("w1")), "/Work/a.md");

        Assert.NotEqual(before.Id, own.Id);
    }
}
