// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>Storage for notes, their history and the trash. The rules live in <see cref="NoteWriter"/>;
/// a store only has to make each operation atomic.</summary>
public interface INoteStore : INoteReader
{
    Task<StoredNote?> GetAsync(VaultPath path, CancellationToken ct);

    /// <summary>Returns false if a note already exists at the path.</summary>
    Task<bool> TryCreateAsync(StoredNote note, CancellationToken ct);

    /// <summary>Stores <paramref name="next"/> only if the stored note is still at
    /// <c>current.Revision</c>; returns false otherwise.</summary>
    Task<bool> TryReplaceAsync(StoredNote current, StoredNote next, CancellationToken ct);

    /// <summary>Moves the note to the trash if it is still at <c>current.Revision</c>.</summary>
    Task<bool> TryTrashAsync(StoredNote current, string deletedBy, DateTime deletedAt, CancellationToken ct);

    /// <summary>Trashed notes at <paramref name="path"/> or below it, most recently deleted first.</summary>
    Task<IReadOnlyList<TrashedNote>> ListTrashAsync(VaultPath path, CancellationToken ct);

    /// <summary>Moves a deleted note at the path back: the most recently deleted one, or the one with
    /// <paramref name="version"/>. Records <paramref name="writer"/> as its last writer. Returns null if
    /// there is no such note in the trash, or if a note exists at the path.</summary>
    Task<StoredNote?> TryRestoreFromTrashAsync(VaultPath path, string? version, string writer, DateTime at, CancellationToken ct);
}
