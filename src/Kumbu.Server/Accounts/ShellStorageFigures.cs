// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Accounts;

/// <summary>
/// <c>df</c>'s figures (WebDAV design §3.12): the vaults this credential opens that its user owns, never one shared with
/// her (a member's key sees her own vaults only), each with the storage quota's figure (<see cref="StorageQuota.RecentFigureAsync"/>,
/// the admin's aggregation, at most 30 seconds old) and its current notes and attachments, without the trash. The dev token's vaults
/// have no owner and no limit; they get the same aggregation for themselves. A web session's hidden vaults (discreet vaults
/// design, Decision 16) have no row but count as owned, and its total is that of every vault she owns, as the quota counts.
/// </summary>
public sealed class ShellStorageFigures(EncryptedNoteStore store, RequestCaller caller, StorageQuota quota, Admin admin) : IStorageFigures
{
    public async Task<OwnFigures> OwnedAsync(CancellationToken ct)
    {
        var own = (await store.UnlockedVaultsAsync(ct)).Where(v => v.IsOwner).ToList();
        if (own.Count == 0) return OwnFigures.None;
        IReadOnlyDictionary<string, long> bytes;
        long? limit = null;
        long? total = null;
        var allOwned = true;
        if (caller.User is { } user)
        {
            var figure = await quota.RecentFigureAsync(user.Id, ct);
            (bytes, limit) = (figure.ByVault, figure.Limit);
            var hidden = await caller.HiddenIdsAsync(ct);
            // Only whether more vaults count against the limit, never which or how much.
            allOwned = figure.ByVault.Keys.All(id => own.Any(v => v.Id == id) || hidden.Contains(id));
            // Keys and MCP keep the sum of their rows.
            if (caller.Session is not null) total = figure.ByVault.Values.Sum();
        }
        else bytes = await admin.BytesByVaultAsync([.. own.Select(v => v.Id)], ct);

        var figures = new List<VaultFigure>();
        foreach (var vault in own)
        {
            var root = VaultPath.Root.Child(vault.Name);
            var notes = await store.ListNotesUnderAsync(root, ct);
            var attachments = await store.ListAttachmentsUnderAsync(root, ct);
            figures.Add(new VaultFigure(vault.Name, bytes.GetValueOrDefault(vault.Id), notes.Count, attachments.Count(a => !FolderMarker.Is(a.Path))));
        }

        return new OwnFigures(figures, limit, allOwned, total);
    }
}
