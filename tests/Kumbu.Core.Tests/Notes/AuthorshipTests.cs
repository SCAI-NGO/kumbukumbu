// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Notes;

/// <summary>Who wrote which line (blame spec §4, §5): the record a write leaves, its budget and bound, and the
/// backfill from the history for a note from before the record.</summary>
public class AuthorshipTests
{
    private static readonly DateTime T1 = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly VaultPath P = VaultPath.TryParse("/Work/protocol.md", out var p) ? p : throw new InvalidOperationException();
    private static readonly WriterRef BritaRef = WriterRef.ForOwner(WriterRef.Session, null);
    private static readonly WriterRef VeyaltRef = WriterRef.ForOwner(WriterRef.Agent, "k1");

    private static DateTime T(int n) => T1.AddHours(n - 1);

    private static WriterRef RefOf(string writer) => writer == "Brita" ? BritaRef : VeyaltRef;

    private static StoredNote Created(string content, string writer = "Brita")
    {
        var note = new StoredNote(P, content, 1, writer, T1, [], Ref: RefOf(writer));
        return note with { Authorship = Authorship.After(null, note) };
    }

    /// <summary>A write as the store makes it: the next state, its record computed from the one it replaces.</summary>
    private static StoredNote Write(StoredNote current, string content, string writer, DateTime at)
    {
        var next = NoteWriter.Next(current, content, writer, RefOf(writer), at);
        return next with { Authorship = Authorship.After(current, next) };
    }

    /// <summary>Each line as "writer@hour", or "-" for an unrecorded one.</summary>
    private static string[] Lines(Authorship? a) =>
        [.. a!.ByLine().Select(w => w is null ? "-" : $"{w.Label}@{(int)(w.At - T1).TotalHours + 1}")];

    [Fact]
    public void A_create_gives_every_line_the_creator()
    {
        var note = Created("# Protocol\n\nstep one\nstep two\n");

        Assert.Equal(["Brita@1", "Brita@1", "Brita@1", "Brita@1"], Lines(note.Authorship));
        Assert.Equal(VersionToken.Hash(note.Content), note.Authorship!.Hash);
        Assert.Equal([new Authorship.Run(4, 0)], note.Authorship.Runs);
        Assert.Null(note.Authorship.Earlier);
        Assert.False(note.Authorship.Dropped);
    }

    [Fact]
    public void Unchanged_lines_keep_their_writer_changed_inserted_and_reindented_ones_get_the_new_one()
    {
        var note = Created("a\nb\nc\n  d\ne");
        var next = Write(note, "a\nB\nc\nnew\nd\ne", "Veyalt", T(2));

        Assert.Equal(["Brita@1", "Veyalt@2", "Brita@1", "Veyalt@2", "Veyalt@2", "Brita@1"], Lines(next.Authorship));
        Assert.Equal(VersionToken.Hash(next.Content), next.Authorship!.Hash);
    }

    [Fact]
    public void A_removed_line_goes_with_its_record()
    {
        var note = Write(Created("a\nb\nc"), "a\nb2\nc", "Veyalt", T(2));
        var next = Write(note, "a\nc", "Brita", T(3));

        Assert.Equal(["Brita@1", "Brita@1"], Lines(next.Authorship));
        // The two runs of the first write merge, and Veyalt's write, which no line names any more, goes.
        Assert.Equal([new Authorship.Run(2, 0)], next.Authorship!.Runs);
        Assert.Single(next.Authorship.Writes);
    }

    [Fact]
    public void A_trailing_newline_added_changes_nothing()
    {
        var note = Created("a\nb");
        var next = Write(note, "a\nb\n", "Veyalt", T(2));

        Assert.Equal(["Brita@1", "Brita@1"], Lines(next.Authorship));
        Assert.Equal(VersionToken.Hash("a\nb\n"), next.Authorship!.Hash);
    }

    [Fact]
    public void Adjacent_runs_of_one_write_merge_and_unused_writes_go()
    {
        var note = Write(Created("a\nb\nc\nd"), "a\nB\nc\nd", "Veyalt", T(2));
        var next = Write(note, "a\nB2\nC2\nd", "Veyalt", T(3));

        Assert.Equal(["Brita@1", "Veyalt@3", "Veyalt@3", "Brita@1"], Lines(next.Authorship));
        Assert.Equal([new Authorship.Run(1, 0), new Authorship.Run(2, 1), new Authorship.Run(1, 0)], next.Authorship!.Runs);
        Assert.Equal(["Brita", "Veyalt"], next.Authorship.Writes.Select(w => w.Label));
    }

    [Fact]
    public void A_state_carried_as_it_was_keeps_its_record()
    {
        var note = Write(Created("a\nb"), "a\nB", "Veyalt", T(2));
        var moved = note with { Path = VaultPath.TryParse("/Work/moved.md", out var to) ? to : throw new InvalidOperationException(), Revision = 1 };

        Assert.Same(note.Authorship, Authorship.After(note, moved));
    }

    [Fact]
    public void A_record_on_the_next_state_is_ignored()
    {
        var note = Created("a\nb");
        var forged = new Authorship(VersionToken.Hash("a\nX"), null, false, [new("Veyalt", VeyaltRef, T(5))], [new(2, 0)]);
        var next = NoteWriter.Next(note, "a\nX", "Veyalt", VeyaltRef, T(2)) with { Authorship = forged };

        Assert.Equal(["Brita@1", "Veyalt@2"], Lines(Authorship.After(note, next)));
    }

    [Fact]
    public void Over_the_budget_the_span_from_first_to_last_difference_goes_to_the_writer()
    {
        var before = Authorship.Of(Created("top\na\nb\nc\nd\ne\nend"));
        // Two changes far apart: the diff of the middle would need more cells than this budget allows.
        var after = before.Then("top\na\nb\nc\nd\ne\nend", "top\nA\nb\nc\nd\nE\nend", new("Veyalt", VeyaltRef, T(2)), maxCells: 10);

        Assert.Equal(["Brita@1", "Veyalt@2", "Veyalt@2", "Veyalt@2", "Veyalt@2", "Veyalt@2", "Brita@1"], Lines(after));
    }

    [Fact]
    public void Under_the_budget_the_record_is_the_same_as_without_one()
    {
        var before = Authorship.Of(Created("top\na\nb\nc\nd\ne\nend"));
        var after = before.Then("top\na\nb\nc\nd\ne\nend", "top\nA\nb\nc\nd\nE\nend", new("Veyalt", VeyaltRef, T(2)), maxCells: 1_000);

        Assert.Equal(["Brita@1", "Veyalt@2", "Brita@1", "Brita@1", "Brita@1", "Veyalt@2", "Brita@1"], Lines(after));
    }

    [Fact]
    public void A_write_leaving_more_than_the_bound_of_runs_starts_the_record_again()
    {
        // A record at the bound: the writer changes on every line.
        var content = string.Join('\n', Enumerable.Range(0, Authorship.MaxRuns).Select(i => $"line {i}"));
        var full = new Authorship(VersionToken.Hash(content), null, false,
            [new("Brita", BritaRef, T1), new("Veyalt", VeyaltRef, T(2))], [.. Enumerable.Range(0, Authorship.MaxRuns).Select(i => new Authorship.Run(1, i % 2))]);
        var note = new StoredNote(P, content, 5, "Veyalt", T(2), [], Ref: VeyaltRef, Authorship: full);

        var next = Write(note, content + "\nby a third", "Ann", T(3));

        Assert.True(next.Authorship!.Dropped);
        Assert.Equal(T(3), next.Authorship.Earlier);
        Assert.Equal(Authorship.MaxRuns + 1, next.Authorship.ByLine().Count);
        Assert.All(next.Authorship.ByLine(), w => Assert.Null(w));
        Assert.Empty(next.Authorship.Writes);

        // A later write records again on top; the lines it didn't change stay unrecorded.
        var later = Write(next, next.Content + "\nmore", "Brita", T(4));
        Assert.Equal(["-", "Brita@4"], Lines(later.Authorship)[^2..]);
        Assert.True(later.Authorship!.Dropped);
        Assert.Equal(T(3), later.Authorship.Earlier);
    }

    [Fact]
    public void A_note_at_revision_one_without_a_record_is_its_creators()
    {
        var note = new StoredNote(P, "a\nb", 1, "Brita", T1, [], Ref: BritaRef);

        Assert.Equal(["Brita@1", "Brita@1"], Lines(Authorship.Of(note)));
    }

    [Fact]
    public void A_history_holding_the_creation_backfills_every_line()
    {
        var note = new StoredNote(P, "a\nB\nc\nd", 3, "Brita", T(3),
        [
            new("a\nB\nc", "Veyalt", T(2), Ref: VeyaltRef),
            new("a\nb\nc", "Brita", T1, Ref: BritaRef),
        ], Ref: BritaRef, CreatedAt: T1);

        var record = Authorship.Of(note);

        Assert.Equal(["Brita@1", "Veyalt@2", "Brita@1", "Brita@3"], Lines(record));
        Assert.Null(record.Earlier);
        Assert.Equal(VeyaltRef, record.Writes.Single(w => w.Label == "Veyalt").Ref);
    }

    [Fact]
    public void A_history_that_overflowed_leaves_older_lines_earlier_than_its_oldest_state()
    {
        // Revision 12: the state of revision 1 is no longer kept.
        var note = new StoredNote(P, "a\nb\nC", 12, "Veyalt", T(12),
        [
            new("a\nb\nc", "Brita", T(5), Ref: BritaRef),
        ], Ref: VeyaltRef);

        var record = Authorship.Of(note);

        Assert.Equal(["-", "-", "Veyalt@12"], Lines(record));
        Assert.Equal(T(5), record.Earlier);
        Assert.False(record.Dropped);
    }

    [Fact]
    public void A_record_of_another_content_reads_as_missing()
    {
        var stale = new Authorship(VersionToken.Hash("other"), null, false, [new("Veyalt", VeyaltRef, T(9))], [new(1, 0)]);
        var note = new StoredNote(P, "a\nb", 1, "Brita", T1, [], Ref: BritaRef, Authorship: stale);

        Assert.Equal(["Brita@1", "Brita@1"], Lines(Authorship.Of(note)));
    }

    [Theory]
    [InlineData(1, 0)] // fewer lines than the content's
    [InlineData(3, 0)] // more
    [InlineData(2, 1)] // a write it doesn't hold
    [InlineData(2, -2)]
    public void A_record_whose_runs_do_not_fit_its_content_reads_as_missing(int lines, int write)
    {
        var broken = new Authorship(VersionToken.Hash("a\nb"), null, false, [new("Mallory", VeyaltRef, T(9))], [new(lines, write)]);
        var note = new StoredNote(P, "a\nb", 1, "Brita", T1, [], Ref: BritaRef, Authorship: broken);

        Assert.Equal(["Brita@1", "Brita@1"], Lines(Authorship.Of(note)));
        Assert.Equal(["Brita@1", "Brita@1", "Veyalt@2"], Lines(Write(note, "a\nb\nc", "Veyalt", T(2)).Authorship));
    }

    [Fact]
    public void The_first_write_after_the_record_stores_what_the_backfill_reads()
    {
        var old = new StoredNote(P, "a\nB\nc", 2, "Veyalt", T(2), [new("a\nb\nc", "Brita", T1, Ref: BritaRef)], Ref: VeyaltRef, CreatedAt: T1);
        var next = Write(old, "a\nB\nc\nd", "Brita", T(3));

        Assert.Equal(["Brita@1", "Veyalt@2", "Brita@1", "Brita@3"], Lines(next.Authorship));
        Assert.Equal(Lines(Authorship.Of(next with { Authorship = null })), Lines(next.Authorship));
    }
}
