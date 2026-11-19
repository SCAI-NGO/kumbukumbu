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
/// again). The analyses and jobs of PDFs go with their blobs, a large file's chunks with its head; chunks no head names
/// go once the newest of them is older than <see cref="Grace"/> (an upload that stopped half way). Runs from
/// <see cref="VaultCatalog.Sweep"/> for the unlocked vaults, under the vault's gate, which every write and every put of
/// a blob takes; a vault that is busy, deleted or being re-keyed is left for the next round.
/// </summary>
public sealed class BlobSweep(VaultCatalog catalog, ILogger log)
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    private static FilterDefinitionBuilder<BlobDocument> F => Builders<BlobDocument>.Filter;

    /// <summary>The index the candidates are read from: it holds their ids, so no blob (up to 15 MB each) is fetched to
    /// find them, on every round of every unlocked vault.</summary>
    internal static readonly MongoDB.Bson.BsonDocument CandidateIndex = new() { { "vault", 1 }, { "at", 1 }, { "bid", 1 } };

    /// <summary>The candidates' query, covered by <see cref="CandidateIndex"/>.</summary>
    internal static readonly MongoDB.Bson.BsonDocument CandidateProjection = new() { { "bid", 1 }, { "_id", 0 } };

    private static FilterDefinitionBuilder<BlobChunkDocument> C => Builders<BlobChunkDocument>.Filter;

    /// <summary>Returns how many blobs, and uploads that stopped half way, were released.</summary>
    public async Task<int> RunAsync(UnlockedVault vault, DateTime now, CancellationToken ct)
    {
        if (!catalog.MayIndex(vault) || !await vault.Gate.WaitAsync(0, ct)) return 0;
        try
        {
            if (!catalog.MayIndex(vault)) return 0;
            var released = await ReleaseAsync(vault, now, ct);
            released += await ReleaseHeadlessChunksAsync(vault, now, ct);
            await ReleaseAnalysesAsync(vault, now, ct);
            return released;
        }
        finally
        {
            vault.Gate.Release();
        }
    }

    private async Task<int> ReleaseAsync(UnlockedVault vault, DateTime now, CancellationToken ct)
    {
        await ExpireTrashAsync(vault, now, ct);

        var old = F.Eq(d => d.Vault, vault.Id) & F.Lt(d => d.At, now - Grace);
        var candidates = (await catalog.Blobs.Find(old, new FindOptions { Hint = CandidateIndex }).Project(CandidateProjection).ToListAsync(ct))
            .Select(d => Convert.ToBase64String(d["bid"].AsByteArray)).ToHashSet();
        candidates.ExceptWith(References(
            vault.Attachments.Values.Select(a => a.Attachment), vault.AttachmentTrash.Select(t => t.Trashed), vault.Checkpoints.Select(c => c.Checkpoint)));
        if (candidates.Count == 0) return 0;

        // The working set may not know what another process wrote: only what MongoDB holds decides.
        if (await StoredReferencesAsync(vault, ct) is not { } stored) return 0;
        candidates.ExceptWith(stored);
        if (candidates.Count == 0) return 0;

        var release = old & F.In(d => d.BlobId, candidates.Select(Convert.FromBase64String));
        var heads = await catalog.Blobs.Find(release & F.Exists(d => d.Chunks)).Project(d => d.Id).ToListAsync(ct);
        var released = await catalog.Blobs.DeleteManyAsync(release, ct);
        if (heads.Count > 0)
        {
            // The chunks of the heads that went (another process may have made one young again meanwhile).
            var kept = await catalog.Blobs.Find(F.Eq(d => d.Vault, vault.Id) & F.In(d => d.Id, heads)).Project(d => d.Id).ToListAsync(ct);
            await catalog.BlobChunks.DeleteManyAsync(C.Eq(d => d.Vault, vault.Id) & C.In(d => d.Head, heads.Except(kept)), ct);
        }

        log.LogInformation("Released {Count} unreferenced blobs of vault {VaultId}", released.DeletedCount, vault.Id);
        return (int)released.DeletedCount;
    }

    /// <summary>
    /// The chunks of uploads that stopped half way: no head names them, and the newest is older than <see cref="Grace"/>,
    /// so a slow upload still under way keeps its chunks (an upload's head counts them before it goes in, and is refused
    /// if some went). No head is put while the sweep holds the gate in this process; one another process put meanwhile
    /// is looked for once more right before the deletion. Read from the indexes, the heads' ids alone. Returns for how
    /// many uploads chunks went.
    /// </summary>
    private async Task<int> ReleaseHeadlessChunksAsync(UnlockedVault vault, DateTime now, CancellationToken ct)
    {
        var ofVault = C.Eq(d => d.Vault, vault.Id);
        var heads = await (await catalog.BlobChunks.DistinctAsync(d => d.Head, ofVault, cancellationToken: ct)).ToListAsync(ct);
        if (heads.Count == 0) return 0;
        var headless = heads.Except(await HeldAsync(vault, heads, ct)).ToList();
        if (headless.Count == 0) return 0;
        var young = await (await catalog.BlobChunks.DistinctAsync(d => d.Head, ofVault & C.In(d => d.Head, headless) & C.Gte(d => d.At, now - Grace), cancellationToken: ct)).ToListAsync(ct);
        var stale = headless.Except(young).ToList();
        if (stale.Count == 0) return 0;
        stale = [.. stale.Except(await HeldAsync(vault, stale, ct))];
        if (stale.Count == 0) return 0;

        var deleted = await catalog.BlobChunks.DeleteManyAsync(ofVault & C.In(d => d.Head, stale), ct);
        log.LogInformation("Deleted {Chunks} chunks of {Uploads} unfinished uploads of vault {VaultId}", deleted.DeletedCount, stale.Count, vault.Id);
        return stale.Count;
    }

    private async Task<List<MongoDB.Bson.ObjectId>> HeldAsync(UnlockedVault vault, IEnumerable<MongoDB.Bson.ObjectId> heads, CancellationToken ct) =>
        await catalog.Blobs.Find(F.Eq(d => d.Vault, vault.Id) & F.In(d => d.Id, heads)).Project(d => d.Id).ToListAsync(ct);

    /// <summary>
    /// The chunks of vaults that no longer exist: an upload that was under way when its vault was deleted, in another
    /// process or past the deletion's own sweep, may write some after it. None can be written for a vault before it
    /// exists, so every chunk of a vault without its document goes. Over every vault, not only the unlocked ones;
    /// read from the indexes.
    /// </summary>
    internal async Task ReleaseChunksOfDeletedVaultsAsync(CancellationToken ct)
    {
        var vaults = await (await catalog.BlobChunks.DistinctAsync(d => d.Vault, C.Empty, cancellationToken: ct)).ToListAsync(ct);
        if (vaults.Count == 0) return;
        var existing = await catalog.Vaults.Find(Builders<VaultDocument>.Filter.In(v => v.Id, vaults)).Project(v => v.Id).ToListAsync(ct);
        var gone = vaults.Except(existing, StringComparer.Ordinal).ToList();
        if (gone.Count == 0) return;
        var deleted = await catalog.BlobChunks.DeleteManyAsync(C.In(d => d.Vault, gone), ct);
        log.LogInformation("Deleted {Chunks} chunks of {Vaults} deleted vaults", deleted.DeletedCount, gone.Count);
    }

    /// <summary>
    /// The analyses and jobs of PDFs whose blob the vault no longer holds: those just released, and any a process left
    /// behind; and text parts older than <see cref="Grace"/> without their part 0, which a store that stopped half way
    /// left (<see cref="Attachments.PdfAnalyses"/>). Under the gate, which every analysis is stored under, and only after
    /// its final transaction found the blob, so none is stored for a blob gone meanwhile. Read from the indexes alone.
    /// </summary>
    private async Task ReleaseAnalysesAsync(UnlockedVault vault, DateTime now, CancellationToken ct)
    {
        var a = Builders<PdfAnalysisDocument>.Filter;
        var j = Builders<PdfJobDocument>.Filter;
        var headed = (await (await catalog.PdfParts.DistinctAsync(d => d.BlobId, a.Eq(d => d.Vault, vault.Id) & a.Eq(d => d.Part, 0), cancellationToken: ct)).ToListAsync(ct))
            .Select(Convert.ToBase64String).ToHashSet();
        var stale = a.Eq(d => d.Vault, vault.Id) & a.Gt(d => d.Part, 0) & a.Lt(d => d.At, now - Grace);
        var headless = (await (await catalog.PdfParts.DistinctAsync(d => d.BlobId, stale, cancellationToken: ct)).ToListAsync(ct))
            .Where(b => !headed.Contains(Convert.ToBase64String(b))).ToList();
        if (headless.Count > 0)
        {
            var left = await catalog.PdfParts.DeleteManyAsync(stale & a.In(d => d.BlobId, headless), ct);
            log.LogInformation("Deleted {Parts} parts of unfinished PDF analyses of vault {VaultId}", left.DeletedCount, vault.Id);
        }

        var named = (await (await catalog.PdfParts.DistinctAsync(d => d.BlobId, a.Eq(d => d.Vault, vault.Id), cancellationToken: ct)).ToListAsync(ct))
            .Concat(await (await catalog.PdfJobs.DistinctAsync(d => d.BlobId, j.Eq(d => d.Vault, vault.Id), cancellationToken: ct)).ToListAsync(ct))
            .DistinctBy(Convert.ToBase64String).ToList();
        if (named.Count == 0) return;

        var held = (await catalog.Blobs.Find(F.Eq(d => d.Vault, vault.Id) & F.In(d => d.BlobId, named), new FindOptions { Hint = EncryptedNoteStore.BlobTimeIndex })
            .Project(new MongoDB.Bson.BsonDocument { { "bid", 1 }, { "_id", 0 } }).ToListAsync(ct))
            .Select(d => Convert.ToBase64String(d["bid"].AsByteArray)).ToHashSet();
        var gone = named.Where(b => !held.Contains(Convert.ToBase64String(b))).ToList();
        if (gone.Count == 0) return;

        var parts = await catalog.PdfParts.DeleteManyAsync(a.Eq(d => d.Vault, vault.Id) & a.In(d => d.BlobId, gone), ct);
        var jobs = await catalog.PdfJobs.DeleteManyAsync(j.Eq(d => d.Vault, vault.Id) & j.In(d => d.BlobId, gone), ct);
        log.LogInformation("Deleted {Parts} analysis parts and {Jobs} jobs of released blobs of vault {VaultId}", parts.DeletedCount, jobs.DeletedCount, vault.Id);
    }

    /// <summary>
    /// The blob ids that must stay: every state of the attachments (current and history), of the trash entries within
    /// their retention, and of the checkpoints for as long as they exist. Every kind of record that holds attachment
    /// states adds its states here.
    /// </summary>
    internal static HashSet<string> References(
        IEnumerable<StoredAttachment> attachments, IEnumerable<TrashedAttachment> trash, IEnumerable<Checkpoint> checkpoints) =>
        [
            .. attachments.Concat(trash.Select(t => t.Attachment)).SelectMany(States).Select(s => s.BlobId),
            .. checkpoints.SelectMany(c => c.Attachments).Select(a => a.BlobId),
        ];

    private static IEnumerable<AttachmentState> States(StoredAttachment a) => a.History.Select(h => h.State).Prepend(a.Current);

    private async Task ExpireTrashAsync(UnlockedVault vault, DateTime now, CancellationToken ct)
    {
        var cutoff = now - NoteWriter.TrashRetention;
        var f = Builders<SealedDocument>.Filter;
        await catalog.AttachmentTrash.DeleteManyAsync(f.Eq(d => d.Vault, vault.Id) & f.Lt(d => d.DeletedAt, cutoff), ct);
        vault.AttachmentTrash = vault.AttachmentTrash.RemoveAll(t => t.Trashed.DeletedAt < cutoff);
    }

    /// <summary>
    /// The references in the vault's records, trash and checkpoints as MongoDB holds them; null if one of them doesn't
    /// open, since the blobs it names can't be known then. The collections are read at one point in time (a snapshot
    /// session): a restore, removal or move commits a record and a trash entry in one transaction, so a state that
    /// passes from one collection to the other between two plain reads would be missed by both. A commit after the
    /// snapshot names either a blob the records it replaces held (still referenced, so in the snapshot), or one its own
    /// transaction checks (<see cref="EncryptedNoteStore.TryCommitAsync"/>, <see cref="EncryptedNoteStore.TryAddCheckpointAsync"/>):
    /// gone already, the commit is refused; held by the working set, it can't go while the commit holds the gate; held by
    /// nothing else and near the cutoff, the commit touches it, so its time is past the cutoff the deletion filters on.
    /// </summary>
    private async Task<HashSet<string>?> StoredReferencesAsync(UnlockedVault vault, CancellationToken ct)
    {
        var f = Builders<SealedDocument>.Filter.Eq(d => d.Vault, vault.Id);
        try
        {
            using var session = await catalog.StartSnapshotSessionAsync(ct);
            var attachments = (await catalog.Attachments.Find(session, f).ToListAsync(ct)).Select(vault.OpenAttachment).ToList();
            var trash = (await catalog.AttachmentTrash.Find(session, f).ToListAsync(ct)).Select(vault.OpenAttachmentTrash).ToList();
            var checkpoints = (await catalog.Checkpoints.Find(session, f).ToListAsync(ct)).Select(d => vault.OpenCheckpoint(d).Checkpoint).ToList();
            return References(attachments, trash, checkpoints);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or InvalidDataException)
        {
            log.LogWarning("An attachment record or checkpoint of vault {VaultId} does not open; its blobs are kept", vault.Id);
            return null;
        }
    }
}
