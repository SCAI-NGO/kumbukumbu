// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>
/// The notes and attachments of a checkpoint as a read-only vault tree, so every reading command works on it unchanged
/// (<c>ls</c>, <c>cat</c>, <c>rg</c> …), as it does on the current state. An attachment appears as it was then, written
/// by the checkpoint's maker at its time; its bytes come from <paramref name="blobs"/>, the vault's store.
/// </summary>
public sealed class CheckpointView(Checkpoint checkpoint, IReadOnlyList<Note> notes, IAttachmentStore? blobs = null) : INoteReader, IAttachmentStore
{
    private readonly Dictionary<VaultPath, string> _byPath = notes.ToDictionary(n => n.Path, n => n.Content);

    private readonly Dictionary<VaultPath, StoredAttachment> _attachments = checkpoint.Attachments.ToDictionary(
        a => a.Path, a => new StoredAttachment(a.Path, a.State, 0, checkpoint.CreatedBy, checkpoint.CreatedAt, [], null));

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

    public Task<StoredAttachment?> GetAttachmentAsync(VaultPath path, CancellationToken ct) => Task.FromResult(_attachments.GetValueOrDefault(path));

    public Task<IReadOnlyList<StoredAttachment>> ListAttachmentsUnderAsync(VaultPath folder, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StoredAttachment>>([.. _attachments.Values.Where(a => Checkpoint.IsBelow(a.Path, folder))]);

    /// <summary>Only the blobs the checkpoint holds open through it.</summary>
    public async Task<Stream?> OpenBlobAsync(string vault, string blobId, CancellationToken ct) =>
        blobs is not null && vault == checkpoint.Vault && checkpoint.Attachments.Any(a => a.BlobId == blobId)
            ? await blobs.OpenBlobAsync(vault, blobId, ct)
            : null;

    public Task<string> PutBlobAsync(string vault, ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
        throw new NotSupportedException("A checkpoint is read-only");

    public Task<IReadOnlyList<TrashedAttachment>> ListAttachmentTrashAsync(VaultPath? path, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TrashedAttachment>>([]);
}
