// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using MongoDB.Driver;

namespace Kumbu.Server.Vaults;

/// <summary>
/// Releases the blobs of a vault that nothing references any more, after expiring its attachment trash past
/// <see cref="NoteWriter.TrashRetention"/>: attachment trash has no TTL index, so that the blobs only a trash entry held
/// can go with it. A blob younger than <see cref="Grace"/> always stays: its upload may still be on the way to the commit
/// of its record (<see cref="EncryptedNoteStore.PutBlobAsync"/> puts the bytes first, and makes a blob it finds young
/// again). Runs from <see cref="VaultCatalog.Sweep"/> for the unlocked vaults, under the vault's gate, which every write
/// and every put of a blob takes; a vault that is busy, deleted or being re-keyed is left for the next round.
/// </summary>
public sealed class BlobSweep(VaultCatalog catalog, ILogger log)
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    private static FilterDefinitionBuilder<BlobDocument> F => Builders<BlobDocument>.Filter;

    /// <summary>Returns how many blobs were released.</summary>
    public async Task<int> RunAsync(UnlockedVault vault, DateTime now, CancellationToken ct)
    {
        if (!catalog.MayIndex(vault) || !await vault.Gate.WaitAsync(0, ct)) return 0;
        try
        {
            if (!catalog.MayIndex(vault)) return 0;
            await ExpireTrashAsync(vault, now, ct);

            var old = F.Eq(d => d.Vault, vault.Id) & F.Lt(d => d.At, now - Grace);
            var candidates = (await catalog.Blobs.Find(old).Project(d => d.BlobId).ToListAsync(ct)).Select(Convert.ToBase64String).ToHashSet();
            candidates.ExceptWith(References(vault.Attachments.Values.Select(a => a.Attachment), vault.AttachmentTrash.Select(t => t.Trashed)));
            if (candidates.Count == 0) return 0;

            // The working set may not know what another process wrote: only what MongoDB holds decides.
            if (await StoredReferencesAsync(vault, ct) is not { } stored) return 0;
            candidates.ExceptWith(stored);
            if (candidates.Count == 0) return 0;

            var released = await catalog.Blobs.DeleteManyAsync(old & F.In(d => d.BlobId, candidates.Select(Convert.FromBase64String)), ct);
            log.LogInformation("Released {Count} unreferenced blobs of vault {VaultId}", released.DeletedCount, vault.Id);
            return (int)released.DeletedCount;
        }
        finally
        {
            vault.Gate.Release();
        }
    }

    /// <summary>
    /// The blob ids that must stay: every state of the attachments (current and history) and of the trash entries within
    /// their retention. Every kind of record that holds attachment states adds its states here.
    /// </summary>
    internal static HashSet<string> References(IEnumerable<StoredAttachment> attachments, IEnumerable<TrashedAttachment> trash) =>
        [.. attachments.Concat(trash.Select(t => t.Attachment)).SelectMany(States).Select(s => s.BlobId)];

    private static IEnumerable<AttachmentState> States(StoredAttachment a) => a.History.Select(h => h.State).Prepend(a.Current);

    private async Task ExpireTrashAsync(UnlockedVault vault, DateTime now, CancellationToken ct)
    {
        var cutoff = now - NoteWriter.TrashRetention;
        var f = Builders<SealedDocument>.Filter;
        await catalog.AttachmentTrash.DeleteManyAsync(f.Eq(d => d.Vault, vault.Id) & f.Lt(d => d.DeletedAt, cutoff), ct);
        vault.AttachmentTrash = vault.AttachmentTrash.RemoveAll(t => t.Trashed.DeletedAt < cutoff);
    }

    /// <summary>The references in the vault's records and trash as MongoDB holds them; null if one of them doesn't open,
    /// since the blobs it names can't be known then.</summary>
    private async Task<HashSet<string>?> StoredReferencesAsync(UnlockedVault vault, CancellationToken ct)
    {
        var f = Builders<SealedDocument>.Filter.Eq(d => d.Vault, vault.Id);
        try
        {
            var attachments = (await catalog.Attachments.Find(f).ToListAsync(ct)).Select(vault.OpenAttachment).ToList();
            var trash = (await catalog.AttachmentTrash.Find(f).ToListAsync(ct)).Select(vault.OpenAttachmentTrash).ToList();
            return References(attachments, trash);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or InvalidDataException)
        {
            log.LogWarning("An attachment record of vault {VaultId} does not open; its blobs are kept", vault.Id);
            return null;
        }
    }
}
