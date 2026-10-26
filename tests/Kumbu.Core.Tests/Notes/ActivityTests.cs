// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Notes;

/// <summary>A vault's note states, trash and move records, folded into changeset rows.</summary>
public class ActivityTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Changeset X = new("x0000000", T0);
    private static readonly Changeset Y = new("y0000000", T0.AddHours(2));

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static string V(string content) => VersionToken.Of(content);

    private static HistoryEntry H(string content, DateTime at, Changeset? cs = null, string by = "liz") => new(content, by, at, cs);

    private static StoredNote Note(string path, string content, DateTime at, Changeset? cs, params HistoryEntry[] history) =>
        new(P(path), content, history.Length + 1, "liz", at, history, Changeset: cs);

    private static IReadOnlyList<ActivityRow> Rows(
        IReadOnlyList<StoredNote> notes, IReadOnlyList<TrashedNote>? trash = null, IReadOnlyList<MoveRecord>? moves = null, DateTime? before = null, int limit = 50) =>
        Activity.Rows(notes, trash ?? [], moves ?? [], before, limit);

    [Fact]
    public void Writes_of_one_changeset_are_one_row_with_counts_by_kind()
    {
        var at = T0.AddMinutes(5);
        var seeded = T0.AddDays(-1);
        var notes = new[]
        {
            Note("/W/a.md", "a2", at, X, H("a1", seeded)),
            Note("/W/b.md", "b", at, X),
            // Moved without a new text: the note keeps its state.
            Note("/W/d.md", "c", seeded, null),
        };
        var trash = new[] { new TrashedNote(Note("/W/e.md", "e", seeded, null), "liz", at, X) };
        var moves = new[] { new MoveRecord(P("/W/c.md"), P("/W/d.md"), "liz", at, X) };

        var row = Rows(notes, trash, moves).First(r => r.Id == X.Id);

        Assert.Equal("liz", row.Writer);
        Assert.Equal(
            [("/W/a.md", ActivityFileKind.Changed, (string?)null), ("/W/b.md", ActivityFileKind.Added, null), ("/W/d.md", ActivityFileKind.Renamed, "/W/c.md"), ("/W/e.md", ActivityFileKind.Deleted, null)],
            row.Files.Select(f => (f.Path, f.Kind, f.From)));
        var a = row.Files[0];
        Assert.Equal((V("a2"), V("a1"), false), (a.Version, a.Before, a.BeforeLost));
        Assert.Null(row.Files[1].Before);
        var e = row.Files[3];
        Assert.Equal((null, V("e"), true), (e.Version, e.Before, e.Trashed));
    }

    [Fact]
    public void An_edit_and_a_rename_in_one_changeset_are_one_file()
    {
        var at = T0.AddMinutes(5);
        var notes = new[] { Note("/W/plan.md", "new text", at, X, H("old text", T0.AddDays(-1))) };
        var moves = new[] { new MoveRecord(P("/W/old.md"), P("/W/plan.md"), "liz", at, X) };

        var row = Assert.Single(Rows(notes, moves: moves), r => r.Id == X.Id);

        var file = Assert.Single(row.Files);
        Assert.Equal(("/W/plan.md", ActivityFileKind.Renamed, "/W/old.md"), (file.Path, file.Kind, file.From));
        Assert.Equal((V("new text"), V("old text")), (file.Version, file.Before));
    }

    [Fact]
    public void Edits_of_a_note_deleted_later_still_show()
    {
        var trashed = Note("/W/a.md", "a2", T0.AddMinutes(1), X, H("a1", T0.AddDays(-1)));
        var trash = new[] { new TrashedNote(trashed, "liz", Y.Started, Y) };

        var rows = Rows([], trash);

        var x = Assert.Single(rows, r => r.Id == X.Id);
        var edit = Assert.Single(x.Files);
        Assert.Equal(("/W/a.md", ActivityFileKind.Changed, true), (edit.Path, edit.Kind, edit.Trashed));
        Assert.Equal((V("a2"), V("a1")), (edit.Version, edit.Before));
        var y = Assert.Single(rows, r => r.Id == Y.Id);
        Assert.Equal(ActivityFileKind.Deleted, Assert.Single(y.Files).Kind);
        // Newest first.
        Assert.Equal(Y.Id, rows[0].Id);
    }

    [Fact]
    public void A_full_history_marks_the_oldest_change_as_before_lost()
    {
        var history = Enumerable.Range(0, NoteWriter.HistoryLimit)
            .Select(i => H($"v{i}", T0.AddHours(-i - 1), new Changeset($"c{i:D7}", T0.AddHours(-i - 1))))
            .ToArray();
        var notes = new[] { Note("/W/a.md", "now", T0, X, history) };

        var rows = Rows(notes);

        Assert.Equal(NoteWriter.HistoryLimit + 1, rows.Count);
        var oldest = Assert.Single(rows[^1].Files);
        Assert.Equal((ActivityFileKind.Changed, true, (string?)null, V("v9")), (oldest.Kind, oldest.BeforeLost, oldest.Before, oldest.Version));
        Assert.All(rows.Take(rows.Count - 1), r => Assert.False(Assert.Single(r.Files).BeforeLost));
    }

    [Fact]
    public void A_restore_is_a_row_of_its_own()
    {
        var notes = new[] { Note("/W/a.md", "a1", T0.AddMinutes(30), null, H("a2", T0.AddMinutes(1), X), H("a1", T0.AddDays(-1))) with { LastWriter = "liz (restore)" } };

        var rows = Rows(notes);

        Assert.Equal(3, rows.Count);
        var restore = rows[0];
        Assert.Null(restore.Id);
        Assert.Equal("liz", restore.Writer);
        Assert.Equal((ActivityFileKind.Changed, V("a1"), V("a2")), (restore.Files[0].Kind, restore.Files[0].Version, restore.Files[0].Before));
    }

    [Fact]
    public void Rows_are_newest_first_and_paged_by_before()
    {
        var notes = new[]
        {
            Note("/W/a.md", "a", T0, new Changeset("c1", T0)),
            Note("/W/b.md", "b", T0.AddMinutes(20), new Changeset("c2", T0.AddMinutes(20))),
            Note("/W/c.md", "c", T0.AddMinutes(40), new Changeset("c3", T0.AddMinutes(40))),
        };

        var first = Rows(notes, limit: 2);
        Assert.Equal(["c3", "c2"], first.Select(r => r.Id));

        var next = Rows(notes, before: first[1].To, limit: 2);
        Assert.Equal(["c1"], next.Select(r => r.Id));
    }

    [Fact]
    public void A_pure_rename_compares_the_unchanged_text()
    {
        var notes = new[] { Note("/W/d.md", "c", T0.AddDays(-1), null) };
        var moves = new[] { new MoveRecord(P("/W/c.md"), P("/W/d.md"), "liz", T0, X) };

        var file = Assert.Single(Assert.Single(Rows(notes, moves: moves), r => r.Id == X.Id).Files);

        Assert.Equal((ActivityFileKind.Renamed, V("c"), V("c")), (file.Kind, file.Version, file.Before));
    }

    [Fact]
    public void Moves_and_removals_without_a_changeset_are_rows_of_their_own()
    {
        var moves = new[]
        {
            new MoveRecord(P("/W/a.md"), P("/W/b.md"), "liz", T0),
            new MoveRecord(P("/W/c.md"), P("/W/d.md"), "liz", T0),
        };

        var rows = Rows([], moves: moves);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.Id));
    }
}
