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
}
