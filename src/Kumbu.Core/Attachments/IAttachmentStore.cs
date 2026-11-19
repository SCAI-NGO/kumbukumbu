// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Attachments;

/// <summary>Storage for attachment records, their bytes and their trash. Records change only through
/// <see cref="INoteStore.TryCommitAsync"/>, in the same transaction as notes.</summary>
public interface IAttachmentStore
{
    Task<StoredAttachment?> GetAttachmentAsync(VaultPath path, CancellationToken ct);

    /// <summary>The attachments below <paramref name="folder"/> (every one for the root).</summary>
    Task<IReadOnlyList<StoredAttachment>> ListAttachmentsUnderAsync(VaultPath folder, CancellationToken ct);

    /// <summary>The bytes of a blob of the vault, read on demand; null if there is none with that id.</summary>
    Task<Stream?> OpenBlobAsync(string vault, string blobId, CancellationToken ct);

    /// <summary>Stores the bytes unless the vault holds them already, and returns their blob id: the same bytes always
    /// get the same id within a vault.</summary>
    Task<string> PutBlobAsync(string vault, ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>As the bytes' overload, from <paramref name="content"/>, read once to its end, and of any size up to the
    /// server's bound (<see cref="FileTooLargeException"/> beyond it, before a byte is read when
    /// <paramref name="declaredLength"/> says so).</summary>
    Task<string> PutBlobAsync(string vault, Stream content, long? declaredLength, CancellationToken ct);

    /// <summary>As the stream's overload, for a body whose start, <paramref name="head"/>, was read already: its bytes,
    /// then <paramref name="rest"/>'s. A body known to be larger than 15 MB (a head longer than that, or a declared length
    /// above it) is streamed on from the head as it is, without copying it.</summary>
    Task<string> PutBlobAsync(string vault, ReadOnlyMemory<byte> head, Stream rest, long? declaredLength, CancellationToken ct);

    /// <summary>Trashed attachments at <paramref name="path"/> or below it (every one for null), most recently deleted
    /// first.</summary>
    Task<IReadOnlyList<TrashedAttachment>> ListAttachmentTrashAsync(VaultPath? path, CancellationToken ct);
}
