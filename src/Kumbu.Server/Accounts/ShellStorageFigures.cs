// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Accounts;

/// <summary>
/// <c>df</c>'s figures (WebDAV design §3.12): the vaults this credential opens that its user owns, never one shared with
/// her (a member's key sees her own vaults only), each with the storage quota's figure (<see cref="StorageQuota.FigureAsync"/>,
/// the admin's aggregation, counted now) and its current notes and attachments, without the trash. The dev token's vaults
/// have no owner and no limit; they get the same aggregation for themselves.
/// </summary>
public sealed class ShellStorageFigures(EncryptedNoteStore store, RequestCaller caller, StorageQuota quota, Admin admin) : IStorageFigures
{
    private StorageFigure? _figure;

    public async Task<IReadOnlyList<VaultFigure>> OwnedAsync(CancellationToken ct)
    {
        var own = (await store.UnlockedVaultsAsync(ct)).Where(v => v.IsOwner).ToList();
        if (own.Count == 0) return [];
        IReadOnlyDictionary<string, long> bytes = caller.User is { } user
            ? (_figure = await quota.FigureAsync(user.Id, ct)).ByVault
            : await admin.BytesByVaultAsync([.. own.Select(v => v.Id)], ct);
        var figures = new List<VaultFigure>();
        foreach (var vault in own)
        {
            var root = VaultPath.Root.Child(vault.Name);
            var notes = await store.ListNotesUnderAsync(root, ct);
            var attachments = await store.ListAttachmentsUnderAsync(root, ct);
            figures.Add(new VaultFigure(vault.Name, bytes.GetValueOrDefault(vault.Id), notes.Count, attachments.Count(a => !FolderMarker.Is(a.Path))));
        }

        return figures;
    }

    public async Task<long?> LimitAsync(CancellationToken ct) => caller.User is { } user ? _figure?.Limit ?? await quota.LimitAsync(user.Id, ct) : null;
}
