// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>Read-only access to the notes of the vault tree.</summary>
public interface INoteReader
{
    /// <summary>Returns the note content, or null if no such note exists.</summary>
    Task<string?> ReadAsync(VaultPath path, CancellationToken ct);

    /// <summary>Returns all notes strictly below <paramref name="dir"/>; the root yields every note.</summary>
    Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct);
}
