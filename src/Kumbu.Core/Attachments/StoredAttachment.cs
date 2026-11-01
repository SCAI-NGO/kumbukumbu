// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Attachments;

/// <summary>What one version of an attachment is: its format, size, displayed pixel size (images only), version
/// token and the blob holding its bytes.</summary>
/// <param name="Version"><see cref="VersionToken.Of(ReadOnlySpan{byte})"/> of the bytes.</param>
/// <param name="BlobId">The store's id for the bytes (<see cref="IAttachmentStore.PutBlobAsync"/>); identical bytes in a
/// vault share one.</param>
public sealed record AttachmentState(string MediaType, long Size, int? Width, int? Height, string Version, string BlobId);

/// <summary>An earlier state of an attachment, who wrote it when, and in which changeset.</summary>
public sealed record AttachmentVersion(AttachmentState State, string WrittenBy, DateTime WrittenAt, Changeset? Changeset, WriterRef? Ref = null);

/// <summary>An attachment's record as stored: small, kept with the notes; the bytes live in blobs.</summary>
/// <param name="Revision">Increases with every write; the store replaces a record only at the revision the writer saw
/// (compare-and-swap), as for <see cref="StoredNote"/>.</param>
/// <param name="History">Earlier states, newest first.</param>
/// <param name="Changeset">The changeset of the current state; null for a state that joins none (a restore).</param>
/// <param name="Ref">The reference sealed beside <paramref name="LastWriter"/>, as for <see cref="StoredNote"/>.</param>
public sealed record StoredAttachment(
    VaultPath Path,
    AttachmentState Current,
    long Revision,
    string LastWriter,
    DateTime LastWriteAt,
    IReadOnlyList<AttachmentVersion> History,
    Changeset? Changeset,
    WriterRef? Ref = null)
{
    public string Version => Current.Version;
}
