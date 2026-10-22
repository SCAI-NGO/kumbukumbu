// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

public sealed record Note(VaultPath Path, string Content);

/// <summary>Read-only access to the notes of the vault tree.</summary>
public interface INoteReader
{
    /// <summary>Returns the note content, or null if no such note exists.</summary>
    Task<string?> ReadAsync(VaultPath path, CancellationToken ct);

    /// <summary>Returns all notes strictly below <paramref name="dir"/>; the root yields every note.</summary>
    Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct);

    /// <summary>Like <see cref="ListNotesUnderAsync"/>, with each note's content, in no particular order.</summary>
    Task<IReadOnlyList<Note>> ReadNotesUnderAsync(VaultPath dir, CancellationToken ct);
}
