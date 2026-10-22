// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;

namespace Kumbu.Server.Notes;

public enum SeedAction
{
    /// <summary>No note at the path: create it.</summary>
    Create,

    /// <summary>The file changed and nobody wrote through MCP since the last seed: load it.</summary>
    Update,

    /// <summary>File and note already agree; only remember that this is what was seeded.</summary>
    Remember,

    /// <summary>Nothing to do (unchanged, or only the database changed).</summary>
    None,

    /// <summary>Both the file and the note changed since the last seed: keep the note, warn.</summary>
    Conflict,

    /// <summary>The note was removed (it is in the trash): don't bring it back from disk.</summary>
    SkipTrashed,
}

/// <summary>
/// A three-way comparison: the file on disk, the note in the database, and what the seeder last loaded
/// (<see cref="StoredNote.SeedContent"/>). The base tells which side changed.
/// </summary>
public static class SeedDecision
{
    public static SeedAction Decide(string disk, StoredNote? note, bool inTrash)
    {
        if (note is null) return inTrash ? SeedAction.SkipTrashed : SeedAction.Create;

        // Notes seeded before step 3 have no seed content; they could only have changed by seeding.
        var seeded = note.SeedContent ?? note.Content;
        if (disk == note.Content) return note.SeedContent == disk ? SeedAction.None : SeedAction.Remember;
        if (note.Content == seeded) return SeedAction.Update;
        if (disk == seeded) return SeedAction.None;
        return SeedAction.Conflict;
    }
}
