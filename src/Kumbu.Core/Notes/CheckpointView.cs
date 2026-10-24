// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>
/// The notes of a checkpoint as a read-only vault tree, so every reading command works on it unchanged
/// (<c>ls</c>, <c>cat</c>, <c>rg</c> …), as it does on the current state.
/// </summary>
public sealed class CheckpointView(Checkpoint checkpoint, IReadOnlyList<Note> notes) : INoteReader
{
    private readonly Dictionary<VaultPath, string> _byPath = notes.ToDictionary(n => n.Path, n => n.Content);

    public Checkpoint Checkpoint => checkpoint;

    public Task<string?> ReadAsync(VaultPath path, CancellationToken ct) => Task.FromResult(_byPath.GetValueOrDefault(path));

    public Task<string?> ReadVersionAsync(VaultPath path, string version, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VaultPath>>([.. _byPath.Keys.Where(p => Checkpoint.IsBelow(p, dir))]);

    public Task<IReadOnlyList<Note>> ReadNotesUnderAsync(VaultPath dir, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Note>>([.. notes.Where(n => Checkpoint.IsBelow(n.Path, dir))]);

    public Task<IReadOnlyList<string>> ListVaultsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([checkpoint.Vault]);

    public Task<MoveRecord?> FindMoveAsync(VaultPath from, CancellationToken ct) => Task.FromResult<MoveRecord?>(null);

    public Task<IReadOnlyList<MoveRecord>> ListMovesAsync(VaultPath dir, CancellationToken ct) => Task.FromResult<IReadOnlyList<MoveRecord>>([]);

    public Task<bool> IsDamagedAsync(VaultPath path, CancellationToken ct) => Task.FromResult(false);

    public Task<int> CountDamagedAsync(VaultPath vaultOrRoot, CancellationToken ct) => Task.FromResult(0);

    public Task<int> CountLockedVaultsAsync(CancellationToken ct) => Task.FromResult(0);
}
