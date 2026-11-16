// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Notes;

/// <summary>When a note came into being: a note at revision 1 was created at its last write, an older one without a
/// recorded creation is at the latest as old as its oldest history state (spec §5).</summary>
public class NoteCreationTests
{
    private static readonly DateTime T1 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly VaultPath P = VaultPath.TryParse("/Work/a.md", out var p) ? p : throw new InvalidOperationException();

    private static DateTime T(int n) => T1.AddDays(n - 1);

    private static StoredNote Next(StoredNote note, DateTime at) =>
        NoteWriter.Next(note, note.Content + "!", "dev", WriterRef.Server, at);

    [Fact]
    public void New_note_was_created_at_its_first_write()
    {
        Assert.Equal((T1, false), NoteCreation.Of(new StoredNote(P, "x", 1, "a", T1, [])));
    }

    [Fact]
    public void Next_keeps_the_creation_of_revision_one()
    {
        var next = NoteWriter.Next(new StoredNote(P, "x", 1, "a", T1, []), "y", "dev", WriterRef.Server, T(2));

        Assert.Equal((T1, false), NoteCreation.Of(next));
        Assert.Equal(T1, next.CreatedAt);
    }

    [Fact]
    public void Old_note_takes_its_oldest_history_state_at_the_latest()
    {
        var old = new StoredNote(P, "x", 7, "a", T(8), [new("h6", "a", T(6), null, null), new("h3", "a", T(3), null, null)]);
        Assert.Equal((T(3), true), NoteCreation.Of(old));

        var next = Next(old, T(9));
        Assert.Equal(T(3), next.CreatedAt);
        Assert.True(next.CreatedAtLeast);

        var later = Next(Next(next, T(10)), T(11));
        Assert.Equal(T(3), later.CreatedAt);
        Assert.True(later.CreatedAtLeast);
        Assert.Equal((T(3), true), NoteCreation.Of(later));
    }

    [Fact]
    public void Old_note_without_history_is_at_the_latest_its_last_write()
    {
        Assert.Equal((T(4), true), NoteCreation.Of(new StoredNote(P, "x", 4, "a", T(4), [])));
    }
}
