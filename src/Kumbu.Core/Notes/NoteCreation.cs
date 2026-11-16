// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Notes;

public static class NoteCreation
{
    /// <summary>When the note came into being. A note at revision 1 was created at its last write; an older one that
    /// never recorded its creation is at the latest as old as its oldest history state, and says so (<c>AtLeast</c>).</summary>
    public static (DateTime At, bool AtLeast) Of(StoredNote note) =>
        note.CreatedAt is { } created ? (created, note.CreatedAtLeast)
        : note.Revision == 1 ? (note.LastWriteAt, false)
        : (note.History.Count > 0 ? note.History[^1].WrittenAt : note.LastWriteAt, true);
}
