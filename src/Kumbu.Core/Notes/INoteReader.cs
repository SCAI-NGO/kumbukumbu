// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

public sealed record Note(VaultPath Path, string Content);

/// <summary>Where a note went with <c>mv</c>; kept for a while, so an old path can say so.</summary>
public sealed record MoveRecord(VaultPath From, VaultPath To, string MovedBy, DateTime MovedAt);

/// <summary>Read-only access to the notes of the vault tree.</summary>
public interface INoteReader
{
    /// <summary>Returns the note content, or null if no such note exists.</summary>
    Task<string?> ReadAsync(VaultPath path, CancellationToken ct);

    /// <summary>Returns all notes strictly below <paramref name="dir"/>; the root yields every note.</summary>
    Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct);

    /// <summary>Like <see cref="ListNotesUnderAsync"/>, with each note's content, in no particular order.</summary>
    Task<IReadOnlyList<Note>> ReadNotesUnderAsync(VaultPath dir, CancellationToken ct);

    /// <summary>The names of the vaults the caller can see, empty ones included.</summary>
    Task<IReadOnlyList<string>> ListVaultsAsync(CancellationToken ct);

    /// <summary>The most recent move away from <paramref name="from"/>, or null.</summary>
    Task<MoveRecord?> FindMoveAsync(VaultPath from, CancellationToken ct);

    /// <summary>Whether a stored note at this path exists but doesn't open (changed or damaged outside Kumbu²).</summary>
    Task<bool> IsDamagedAsync(VaultPath path, CancellationToken ct);

    /// <summary>How many stored notes don't open, in all vaults (root) or in one vault (its root).
    /// Damaged notes can't be placed in a directory, since their paths are what doesn't open.</summary>
    Task<int> CountDamagedAsync(VaultPath vaultOrRoot, CancellationToken ct);

    /// <summary>How many vaults exist that the caller's credential doesn't open. Only meaningful while the dev
    /// token is the only credential; with per-user keys (step 7) this would tell one user about another's vaults.</summary>
    Task<int> CountLockedVaultsAsync(CancellationToken ct);
}
