// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Attachments;

/// <summary>The attachments of a note store, for code that is handed one: a store that keeps attachments implements
/// <see cref="IAttachmentStore"/> as well (so does a checkpoint's view, with the attachments it holds); a reader that
/// doesn't has none to offer.</summary>
public static class StoreAttachments
{
    public static Task<StoredAttachment?> GetAsync(INoteReader notes, VaultPath path, CancellationToken ct) =>
        notes is IAttachmentStore store ? store.GetAttachmentAsync(path, ct) : Task.FromResult<StoredAttachment?>(null);

    public static async Task<IReadOnlyList<StoredAttachment>> UnderAsync(INoteReader notes, VaultPath folder, CancellationToken ct) =>
        notes is IAttachmentStore store ? await store.ListAttachmentsUnderAsync(folder, ct) : [];

    /// <summary>The paths of the attachments below <paramref name="folder"/>; null if the reader can't tell, so links to
    /// attachments can't be resolved.</summary>
    public static async Task<IReadOnlyList<VaultPath>?> PathsUnderAsync(INoteReader notes, VaultPath folder, CancellationToken ct) =>
        notes is IAttachmentStore store ? [.. (await store.ListAttachmentsUnderAsync(folder, ct)).Select(a => a.Path)] : null;
}
