// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>Storage for notes, their history and the trash. The rules live in <see cref="NoteWriter"/>;
/// a store only has to make each operation atomic.</summary>
public interface INoteStore : INoteReader
{
    Task<StoredNote?> GetAsync(VaultPath path, CancellationToken ct);

    /// <summary>The changeset of the most recent write by the writer <paramref name="changesetKey"/>
    /// (<see cref="WriterRef.ChangesetKey"/>) that is still a note's current state in the vault, and when it was
    /// written; null if there is none.</summary>
    Task<(Changeset Changeset, DateTime LastWrite)?> LatestChangesetAsync(string vault, string changesetKey, CancellationToken ct);

    /// <summary>Returns false if a note already exists at the path.</summary>
    Task<bool> TryCreateAsync(StoredNote note, CancellationToken ct);

    /// <summary>Stores <paramref name="next"/> only if the stored note is still at
    /// <c>current.Revision</c>; returns false otherwise.</summary>
    Task<bool> TryReplaceAsync(StoredNote current, StoredNote next, CancellationToken ct);

    /// <summary>
    /// Writes every change of <paramref name="batch"/>, or none: each note must still be at the revision the
    /// batch saw, and no move target or created note may exist. Moves and trashed notes are recorded with
    /// <paramref name="by"/>, its reference <paramref name="byRef"/> and <paramref name="at"/>. Returns the paths that
    /// stood in the way; empty if it was written. Refuses (throws) an entry without a writer reference.
    /// </summary>
    Task<IReadOnlyList<VaultPath>> TryCommitAsync(NoteBatch batch, string by, WriterRef byRef, DateTime at, CancellationToken ct);

    /// <summary>Moves the note to the trash if it is still at <c>current.Revision</c>, with no changeset. For tests and
    /// tools only: a removal by a writer goes through <see cref="TryCommitAsync"/> with the writer's changeset
    /// (<see cref="NoteWriter.RemoveAsync"/>), so it shows in the vault's activity.</summary>
    Task<bool> TryTrashAsync(StoredNote current, string deletedBy, WriterRef deletedByRef, DateTime deletedAt, CancellationToken ct);

    /// <summary>Trashed notes at <paramref name="path"/> or below it, most recently deleted first.</summary>
    Task<IReadOnlyList<TrashedNote>> ListTrashAsync(VaultPath path, CancellationToken ct);

    /// <summary>Moves a deleted note at the path back: the most recently deleted one, or the one with
    /// <paramref name="version"/>. Records <paramref name="writer"/> and <paramref name="reference"/> as its last
    /// writer. Returns null if there is no such note in the trash, or if a note exists at the path.</summary>
    Task<StoredNote?> TryRestoreFromTrashAsync(VaultPath path, string? version, string writer, WriterRef reference, DateTime at, CancellationToken ct);

    /// <summary>The named checkpoints of every vault (root) or of one vault, oldest first.</summary>
    Task<IReadOnlyList<Checkpoint>> ListCheckpointsAsync(VaultPath vaultOrRoot, CancellationToken ct);

    /// <summary>Stores a checkpoint with the content of its notes; false if its vault has one of that name.</summary>
    Task<bool> TryAddCheckpointAsync(Checkpoint checkpoint, IReadOnlyList<Note> notes, CancellationToken ct);

    /// <summary>Removes a checkpoint, and the content only it held; false if there is none of that name.</summary>
    Task<bool> TryRemoveCheckpointAsync(string vault, string name, CancellationToken ct);

    /// <summary>The notes of a checkpoint with their content then.</summary>
    Task<IReadOnlyList<Note>> ReadCheckpointAsync(Checkpoint checkpoint, CancellationToken ct);
}
