// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Vaults;

/// <summary>
/// <see cref="INoteStore"/> and <see cref="IAttachmentStore"/> over encrypted vaults, for one credential (one request).
/// Reads come from the unlocked working set, but for attachments' bytes, which are read from MongoDB on demand; every
/// write is one MongoDB transaction over sealed documents (compare-and-swap on the revision), and changes the working
/// set only after MongoDB committed it.
/// </summary>
public sealed class EncryptedNoteStore(
    VaultCatalog catalog, ICredential credential, Search.SearchIndex? index = null, Accounts.ApiKeys? keys = null, Attachments.PdfAnalysisRunner? pdf = null)
    : INoteStore, IAttachmentStore, Kumbu.Core.Search.ISearchIndex
{
    private static FilterDefinitionBuilder<SealedDocument> F => Builders<SealedDocument>.Filter;

    private Dictionary<string, VaultAccess>? _vaults;

    /// <summary>The same accesses by vault id, to show a path of the vault in the caller's view.</summary>
    private Dictionary<string, VaultAccess> _byId = [];

    public async Task<IReadOnlyList<string>> ListVaultsAsync(CancellationToken ct) => [.. (await VaultsAsync(ct)).Keys];

    public async Task<string?> ReadAsync(VaultPath path, CancellationToken ct) => (await GetAsync(path, ct))?.Content;

    public async Task<string?> ReadVersionAsync(VaultPath path, string version, CancellationToken ct) =>
        await GetAsync(path, ct) is { } note ? Versions.Find(note, version) : null;

    public async Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct) =>
        [.. (await NotesUnderAsync(dir, ct)).Select(n => n.Path)];

    public async Task<IReadOnlyList<Note>> ReadNotesUnderAsync(VaultPath dir, CancellationToken ct) =>
        [.. (await NotesUnderAsync(dir, ct)).Select(n => new Note(n.Path, n.Content))];

    public async Task<MoveRecord?> FindMoveAsync(VaultPath from, CancellationToken ct)
    {
        if (await VaultAsync(from, ct) is not { } a) return null;
        var path = a.ToVault(from);
        return a.Vault.Moves.Where(m => m.From == path).MaxBy(m => m.MovedAt) is { } move ? Shown(a, move) : null;
    }

    public async Task<IReadOnlyList<MoveRecord>> ListMovesAsync(VaultPath dir, CancellationToken ct)
    {
        IEnumerable<VaultAccess> vaults = dir.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(dir, ct) is { } v ? [v] : [];
        var prefix = dir.IsRoot ? "/" : dir.Value + "/";
        return [.. vaults.SelectMany(v => v.Vault.Moves.Select(m => Shown(v, m)))
            .Where(m => m.From.Value.StartsWith(prefix, StringComparison.Ordinal) || m.To.Value.StartsWith(prefix, StringComparison.Ordinal))];
    }

    /// <summary>Only the marker: <see cref="GetAsync"/>, which every command reads a note with first, already
    /// checked a damaged path against MongoDB again.</summary>
    public async Task<bool> IsDamagedAsync(VaultPath path, CancellationToken ct) =>
        await VaultAsync(path, ct) is { } a && a.ToVault(path) is var p && !a.Vault.Notes.ContainsKey(p) && !a.Vault.Attachments.ContainsKey(p) && a.Vault.IsDamaged(p);

    public async Task<int> CountDamagedAsync(VaultPath vaultOrRoot, CancellationToken ct)
    {
        IEnumerable<UnlockedVault> vaults = vaultOrRoot.IsRoot
            ? (await VaultsAsync(ct)).Values.Select(a => a.Vault).Where(v => !v.Pending)
            : await VaultAsync(vaultOrRoot, ct) is { } a ? [a.Vault] : [];
        return vaults.Sum(v => v.DamagedPathIds.Count);
    }

    public Task<int> CountLockedVaultsAsync(CancellationToken ct) => credential.CountLockedAsync(catalog, ct);

    public async Task<(Changeset Changeset, DateTime LastWrite)?> LatestChangesetAsync(string vault, string changesetKey, CancellationToken ct) =>
        await VaultAsync(VaultPath.Root.Child(vault), ct) is { Vault: var v }
            ? Changeset.Latest(
                v.Notes.Values.Select(n => n.Note), v.Trash.Select(t => t.Trashed), v.Moves, changesetKey,
                v.Attachments.Values.Select(a => a.Attachment), v.AttachmentTrash.Select(t => t.Trashed))
            : null;

    /// <summary>A path marked damaged is read from MongoDB again first, so a repair or removal in the database
    /// counts on the first access, without restarting the server.</summary>
    public async Task<StoredNote?> GetAsync(VaultPath path, CancellationToken ct)
    {
        if (await VaultAsync(path, ct) is not { } a) return null;
        var (vault, stored) = (a.Vault, a.ToVault(path));
        if (!vault.Notes.ContainsKey(stored)) await RefreshDamagedAsync(vault, stored, ct);
        return vault.Notes.TryGetValue(stored, out var n) ? Shown(a, n.Note) : null;
    }

    /// <summary>A note or attachment path marked damaged is read from MongoDB again (<see cref="GetAsync"/>).</summary>
    private async Task RefreshDamagedAsync(UnlockedVault vault, VaultPath path, CancellationToken ct)
    {
        if (!vault.IsDamaged(path)) return;
        // The refresh writes the working set: not of an object a re-keying made old, nor during a run.
        catalog.CheckWritable(vault);
        await vault.Gate.WaitAsync(ct);
        try
        {
            catalog.CheckWritable(vault);
            await catalog.RefreshAsync(vault, path, ct);
        }
        finally
        {
            vault.Gate.Release();
        }
    }

    public async Task<bool> TryCreateAsync(StoredNote note, CancellationToken ct)
    {
        if (await VaultAsync(note.Path, ct) is not { } a) return false;
        var vault = a.Vault;
        note = Stored(a, note);
        return await WriteAsync(vault, [note.Path], async session =>
        {
            var id = ObjectId.GenerateNewId();
            WriterRef.RequireStorable(note.Ref);
            if (!await InsertNoteAsync(session, vault, id, note, ct)) return null;
            return () => vault.Notes = vault.Notes.SetItem(note.Path, (id, note));
        }, ct);
    }

    public async Task<bool> TryReplaceAsync(StoredNote current, StoredNote next, CancellationToken ct) =>
        (await TryCommitAsync(new NoteBatch { Replaces = { (current, next) } }, next.LastWriter, next.Ref ?? WriterRef.Unknown, next.LastWriteAt, ct)).Count == 0;

    /// <summary>
    /// One transaction: replaces (compare-and-swap on the revision), notes and attachments to the trash, restored
    /// attachments out of it, then the sources of all moves (notes' and attachments') are deleted (same check) before
    /// created notes and attachments, restored attachments and move targets are inserted, so a note may move to where
    /// another one just left. Every move, of a note or an attachment, is recorded in the moves. The unique index refuses a target that exists. Any refusal aborts the transaction: nothing is written.
    /// The blobs the attachments name are put before (<see cref="PutBlobAsync"/>), outside the transaction, and checked in
    /// it (<see cref="RequireBlobsAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<VaultPath>> TryCommitAsync(NoteBatch batch, string by, WriterRef byRef, DateTime at, CancellationToken ct)
    {
        if (batch.IsEmpty) return [];
        var asked = batch.Paths.ToList();
        if (asked.Select(p => p.Vault).Distinct().Count() != 1) throw new ArgumentException("A batch changes notes of one vault", nameof(batch));
        if (await VaultAsync(asked[0], ct) is not { } access) return asked;
        var vault = access.Vault;
        batch = Stored(access, batch);
        var paths = batch.Paths.ToList();

        VaultPath? conflict = null;
        var written = await WriteAsync(vault, paths, async session =>
        {
            var apply = new List<Action>();
            foreach (var (current, next) in batch.Replaces)
            {
                WriterRef.RequireStorable(next.Ref, kept: WriterRef.Keeps(current, next));
                if (!vault.Notes.TryGetValue(current.Path, out var entry)
                    || !await ReplaceAtRevisionAsync(session, catalog.Notes, vault, entry.Id, current.Path, current.Revision, next.Revision, vault.SealNote(entry.Id, next), ct))
                {
                    return Refuse(current.Path);
                }

                apply.Add(() => vault.Notes = vault.Notes.SetItem(current.Path, (entry.Id, next)));
            }

            foreach (var (current, next) in batch.AttachmentReplaces)
            {
                WriterRef.RequireStorable(next.Ref, kept: WriterRef.Keeps(current, next));
                if (!vault.Attachments.TryGetValue(current.Path, out var entry)
                    || !await ReplaceAtRevisionAsync(session, catalog.Attachments, vault, entry.Id, current.Path, current.Revision, next.Revision, vault.SealAttachment(entry.Id, next), ct))
                {
                    return Refuse(current.Path);
                }

                apply.Add(() => vault.Attachments = vault.Attachments.SetItem(current.Path, (entry.Id, next)));
            }

            foreach (var current in batch.Trashes)
            {
                if (await TrashInAsync(session, vault, current, by, byRef, at, batch.Changeset, ct) is not { } trashed) return Refuse(current.Path);
                apply.Add(trashed);
            }

            foreach (var current in batch.AttachmentTrashes)
            {
                if (!vault.Attachments.TryGetValue(current.Path, out var entry)) return Refuse(current.Path);
                var deleted = await catalog.Attachments.DeleteOneAsync(session, AtRevision(entry.Id, current.Revision, vault.PathId(current.Path)), cancellationToken: ct);
                if (deleted.DeletedCount == 0) return Refuse(current.Path);

                var trashed = new TrashedAttachment(current, by, at, batch.Changeset, byRef);
                WriterRef.RequireStorable(trashed.Ref);
                var trashId = ObjectId.GenerateNewId();
                await catalog.AttachmentTrash.InsertOneAsync(
                    session,
                    new SealedDocument { Id = trashId, Vault = vault.Id, PathId = vault.PathId(current.Path), DeletedAt = at, Blob = vault.SealAttachmentTrash(trashId, trashed) },
                    cancellationToken: ct);
                apply.Add(() =>
                {
                    vault.Attachments = vault.Attachments.Remove(current.Path);
                    vault.AttachmentTrash = vault.AttachmentTrash.Add((trashId, trashed));
                });
            }

            foreach (var (trashed, restored) in batch.AttachmentRestores)
            {
                var entry = vault.AttachmentTrash.FirstOrDefault(t => t.Trashed.IsSameEntry(trashed));
                if (entry.Trashed is null) return Refuse(restored.Path);
                var removed = await catalog.AttachmentTrash.DeleteOneAsync(
                    session, F.Eq(d => d.Id, entry.Id) & F.Eq(d => d.PathId, vault.PathId(trashed.Attachment.Path)), cancellationToken: ct);
                if (removed.DeletedCount == 0) return Refuse(restored.Path);
                apply.Add(() => vault.AttachmentTrash = vault.AttachmentTrash.Remove(entry));
            }

            foreach (var (current, _) in batch.Moves)
            {
                if (!vault.Notes.TryGetValue(current.Path, out var entry)) return Refuse(current.Path);
                var deleted = await catalog.Notes.DeleteOneAsync(session, AtRevision(entry.Id, current.Revision, vault.PathId(current.Path)), cancellationToken: ct);
                if (deleted.DeletedCount == 0) return Refuse(current.Path);
                apply.Add(() => vault.Notes = vault.Notes.Remove(current.Path));
            }

            foreach (var (current, _) in batch.AttachmentMoves)
            {
                if (!vault.Attachments.TryGetValue(current.Path, out var entry)) return Refuse(current.Path);
                var deleted = await catalog.Attachments.DeleteOneAsync(session, AtRevision(entry.Id, current.Revision, vault.PathId(current.Path)), cancellationToken: ct);
                if (deleted.DeletedCount == 0) return Refuse(current.Path);
                apply.Add(() => vault.Attachments = vault.Attachments.Remove(current.Path));
            }

            foreach (var created in batch.Creates)
            {
                var id = ObjectId.GenerateNewId();
                WriterRef.RequireStorable(created.Ref);
                if (!await InsertNoteAsync(session, vault, id, created, ct)) return Refuse(created.Path);
                apply.Add(() => vault.Notes = vault.Notes.SetItem(created.Path, (id, created)));
            }

            foreach (var created in batch.AttachmentCreates.Concat(batch.AttachmentRestores.Select(r => r.Restored)))
            {
                var id = ObjectId.GenerateNewId();
                WriterRef.RequireStorable(created.Ref);
                if (!await InsertAttachmentAsync(session, vault, id, created, ct)) return Refuse(created.Path);
                apply.Add(() => vault.Attachments = vault.Attachments.SetItem(created.Path, (id, created)));
            }

            foreach (var (current, target) in batch.Moves)
            {
                var id = ObjectId.GenerateNewId();
                WriterRef.RequireStorable(target.Ref, kept: WriterRef.Keeps(current, target));
                if (!await InsertNoteAsync(session, vault, id, target, ct)) return Refuse(target.Path);
                var move = await RecordMoveAsync(session, current.Path, target.Path);
                apply.Add(() =>
                {
                    vault.Notes = vault.Notes.SetItem(target.Path, (id, target));
                    vault.Moves = vault.Moves.Add(move);
                });
            }

            foreach (var (current, target) in batch.AttachmentMoves)
            {
                var id = ObjectId.GenerateNewId();
                WriterRef.RequireStorable(target.Ref, kept: WriterRef.Keeps(current, target));
                if (!await InsertAttachmentAsync(session, vault, id, target, ct)) return Refuse(target.Path);
                var move = await RecordMoveAsync(session, current.Path, target.Path);
                apply.Add(() =>
                {
                    vault.Attachments = vault.Attachments.SetItem(target.Path, (id, target));
                    vault.Moves = vault.Moves.Add(move);
                });
            }

            await RequireBlobsAsync(session, vault, NamedBlobs(batch), ct);
            return () => apply.ForEach(a => a());
        }, ct);

        // After the commit, which stands: a PDF whose job isn't queued now is queued at the vault's next unlock.
        if (written && pdf is not null)
        {
            var states = batch.AttachmentCreates.Concat(batch.AttachmentReplaces.Select(r => r.Next))
                .Concat(batch.AttachmentRestores.Select(r => r.Restored)).Concat(batch.AttachmentMoves.Select(m => m.Target))
                .Select(a => a.Current);
            await pdf.EnqueueAsync(vault, states, CancellationToken.None);
        }

        return written ? [] : [access.ToCaller(conflict ?? paths[0])];

        Action? Refuse(VaultPath path)
        {
            conflict = path;
            return null;
        }

        async Task<MoveRecord> RecordMoveAsync(IClientSessionHandle session, VaultPath from, VaultPath to)
        {
            var move = new MoveRecord(from, to, by, at, batch.Changeset, byRef);
            WriterRef.RequireStorable(move.Ref);
            var moveId = ObjectId.GenerateNewId();
            await catalog.Moves.InsertOneAsync(
                session,
                new SealedDocument { Id = moveId, Vault = vault.Id, PathId = vault.PathId(from), At = at, Blob = vault.SealMove(moveId, move) },
                cancellationToken: ct);
            return move;
        }
    }

    /// <summary>
    /// The blobs the batch's new attachment states name, but for those the records it replaces, moves or takes out of
    /// the trash hold already: the batch checks those records at their revision, so their blobs are still referenced.
    /// The others came from a checkpoint, another record or a put, read before the commit.
    /// </summary>
    private static IEnumerable<(VaultPath Path, string BlobId)> NamedBlobs(NoteBatch batch)
    {
        var held = batch.AttachmentReplaces.Select(r => r.Current)
            .Concat(batch.AttachmentMoves.Select(m => m.Current))
            .Concat(batch.AttachmentRestores.Select(r => r.Trashed.Attachment))
            .SelectMany(States).Select(s => s.BlobId).ToHashSet(StringComparer.Ordinal);
        return batch.AttachmentReplaces.Select(r => r.Next)
            .Concat(batch.AttachmentCreates)
            .Concat(batch.AttachmentRestores.Select(r => r.Restored))
            .Concat(batch.AttachmentMoves.Select(m => m.Target))
            .SelectMany(a => States(a).Select(s => (a.Path, s.BlobId)))
            .Where(n => !held.Contains(n.BlobId));

        static IEnumerable<AttachmentState> States(StoredAttachment a) => a.History.Select(h => h.State).Prepend(a.Current);
    }

    /// <summary>The index <see cref="RequireBlobsAsync"/> reads by: it holds all it needs, so no blob is fetched.</summary>
    internal static readonly BsonDocument BlobTimeIndex = new() { { "vault", 1 }, { "bid", 1 }, { "at", 1 } };

    /// <summary>
    /// Within the transaction, under the vault's gate: every blob <paramref name="named"/> must be stored, or the
    /// transaction is refused with <see cref="ContentMissingException"/>. A reference read before the commit (from a
    /// checkpoint, say) may name a blob that only something removed meanwhile held. Whether each is stored, and when it
    /// was last put, is read from <see cref="BlobTimeIndex"/> alone, never from the blobs. A blob that the sweep could
    /// release soon (older than half its <see cref="BlobSweep.Grace"/>) and that nothing in the working set holds any
    /// more is made young again: the touch moves it past the sweep's cutoff and conflicts with a sweep deleting it at the
    /// same time. One the working set still holds can't be released while the gate is held, since every sweep of this
    /// process takes it; only a process whose working set is behind another's removals is left to the check above.
    /// </summary>
    private async Task RequireBlobsAsync(IClientSessionHandle session, UnlockedVault vault, IEnumerable<(VaultPath Path, string BlobId)> named, CancellationToken ct)
    {
        var wanted = named.DistinctBy(n => n.BlobId).ToList();
        if (wanted.Count == 0) return;
        var ids = wanted.Select(n => TryParseBlobId(n.BlobId, out var id) ? id : []).Where(id => id.Length > 0).ToList();
        var f = Builders<BlobDocument>.Filter;
        var filter = f.Eq(d => d.Vault, vault.Id) & f.In(d => d.BlobId, ids);
        var stored = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var found = await catalog.Blobs.Find(session, filter, new FindOptions { Hint = BlobTimeIndex })
            .Project(new BsonDocument { { "bid", 1 }, { "at", 1 }, { "_id", 0 } })
            .ToListAsync(ct);
        foreach (var doc in found) stored[Convert.ToBase64String(doc["bid"].AsByteArray)] = doc["at"].ToUniversalTime();

        if (wanted.Where(n => !stored.ContainsKey(n.BlobId)).Select(n => Shown(vault, n.Path).Value).Distinct().Order(StringComparer.Ordinal).ToList() is { Count: > 0 } missing)
        {
            throw new ContentMissingException($"The content of {string.Join(", ", missing)} is no longer stored (it was removed meanwhile); nothing was written. Try again.");
        }

        var soon = catalog.Now - BlobSweep.Grace / 2;
        var old = stored.Where(b => b.Value < soon).Select(b => b.Key).ToList();
        if (old.Count == 0) return;
        var held = BlobSweep.References(
            vault.Attachments.Values.Select(a => a.Attachment), vault.AttachmentTrash.Select(t => t.Trashed), vault.Checkpoints.Select(c => c.Checkpoint));
        var touch = old.Where(id => !held.Contains(id)).Select(Convert.FromBase64String).ToList();
        if (touch.Count == 0) return;
        var touched = await catalog.Blobs.UpdateManyAsync(
            session, f.Eq(d => d.Vault, vault.Id) & f.In(d => d.BlobId, touch), Builders<BlobDocument>.Update.Set(d => d.At, catalog.Now), cancellationToken: ct);
        if (touched.MatchedCount < touch.Count)
        {
            throw new ContentMissingException("The content of a file is no longer stored (it was removed meanwhile); nothing was written. Try again.");
        }
    }

    /// <summary>As <see cref="GetAsync"/>, a damaged path is read from MongoDB again first.</summary>
    public async Task<StoredAttachment?> GetAttachmentAsync(VaultPath path, CancellationToken ct)
    {
        if (await VaultAsync(path, ct) is not { } access) return null;
        var (vault, stored) = (access.Vault, access.ToVault(path));
        if (!vault.Attachments.ContainsKey(stored)) await RefreshDamagedAsync(vault, stored, ct);
        return vault.Attachments.TryGetValue(stored, out var a) ? Shown(access, a.Attachment) : null;
    }

    public async Task<IReadOnlyList<StoredAttachment>> ListAttachmentsUnderAsync(VaultPath folder, CancellationToken ct)
    {
        IEnumerable<VaultAccess> vaults = folder.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(folder, ct) is { } v ? [v] : [];
        var prefix = folder.IsRoot ? "/" : folder.Value + "/";
        return [.. vaults.SelectMany(v => v.Vault.Attachments.Values.Select(a => Shown(v, a.Attachment))).Where(a => a.Path.Value.StartsWith(prefix, StringComparison.Ordinal))];
    }

    public async Task<IReadOnlyList<TrashedAttachment>> ListAttachmentTrashAsync(VaultPath? path, CancellationToken ct)
    {
        var scope = path ?? VaultPath.Root;
        IEnumerable<VaultAccess> vaults = scope.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(scope, ct) is { } v ? [v] : [];
        return [.. vaults
            .SelectMany(v => v.Vault.AttachmentTrash.Select(t => Shown(v, t.Trashed)))
            .Where(t => InScope(scope, t.Attachment.Path))
            .OrderByDescending(t => t.DeletedAt)];
    }

    /// <summary>
    /// The blob read from MongoDB and opened (<see cref="VaultCatalog.ReadBlobAsync"/>): the read and the decryption,
    /// when a file costs about twice its size, take one of the server's <see cref="VaultCatalog.FileReads"/> slots, so a
    /// slow download holds only its own bytes, never a slot.
    /// </summary>
    public async Task<Stream?> OpenBlobAsync(string vault, string blobId, CancellationToken ct)
    {
        if (await VaultAsync(VaultPath.Root.Child(vault), ct) is not { Vault: var v } || !TryParseBlobId(blobId, out var id)) return null;
        return await catalog.ReadBlobAsync(v, id, ct) is { } bytes ? new MemoryStream(bytes, writable: false) : null;
    }

    /// <summary>The unlocked vault of a path, as every read finds it; null if the credential has no such vault.</summary>
    internal async Task<UnlockedVault?> VaultOfAsync(VaultPath path, CancellationToken ct) => (await VaultAsync(path, ct))?.Vault;

    /// <summary>
    /// Stores the bytes sealed under their keyed hash unless the vault holds them already, in which case only their time
    /// is set to now: a blob that nothing references yet stays for <see cref="BlobSweep.Grace"/> after its last put, so
    /// the record that will name it can commit. Under the vault's gate, so neither a sweep, a deletion nor a re-keying run
    /// meets it half way.
    /// </summary>
    public async Task<string> PutBlobAsync(string vault, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length > AttachmentFormat.MaxBytes) throw new ArgumentException("An attachment holds at most 15 MB.", nameof(bytes));
        if (await VaultAsync(VaultPath.Root.Child(vault), ct) is not { Vault: var v }) throw new ArgumentException($"There is no vault named '{vault}'.", nameof(vault));
        var id = v.BlobId(bytes.Span);
        var filter = Builders<BlobDocument>.Filter.Eq(d => d.Vault, v.Id) & Builders<BlobDocument>.Filter.Eq(d => d.BlobId, id);

        catalog.CheckWritable(v);
        await v.Gate.WaitAsync(ct);
        try
        {
            catalog.CheckWritable(v);
            var touch = Builders<BlobDocument>.Update.Set(d => d.At, catalog.Now);
            if ((await catalog.Blobs.UpdateOneAsync(filter, touch, cancellationToken: ct)).MatchedCount == 0)
            {
                try
                {
                    await catalog.Blobs.InsertOneAsync(
                        new BlobDocument { Id = ObjectId.GenerateNewId(), Vault = v.Id, BlobId = id, Blob = v.SealBlob(id, bytes.Span), At = catalog.Now },
                        cancellationToken: ct);
                }
                catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
                {
                    // Another process put the same bytes meanwhile.
                    await catalog.Blobs.UpdateOneAsync(filter, touch, cancellationToken: ct);
                }
            }
        }
        finally
        {
            v.Gate.Release();
        }

        return Convert.ToBase64String(id);
    }

    internal static bool TryParseBlobId(string blobId, out byte[] id)
    {
        id = new byte[32];
        return Convert.TryFromBase64String(blobId, id, out var written) && written == id.Length;
    }

    public async Task<bool> TryTrashAsync(StoredNote current, string deletedBy, WriterRef deletedByRef, DateTime deletedAt, CancellationToken ct)
    {
        if (await VaultAsync(current.Path, ct) is not { } a) return false;
        var vault = a.Vault;
        current = Stored(a, current);
        return await WriteAsync(vault, [current.Path], session => TrashInAsync(session, vault, current, deletedBy, deletedByRef, deletedAt, null, ct), ct);
    }

    /// <summary>
    /// Search by meaning over the embeddings of the credential's vaults: the query's vector against every chunk's, in
    /// memory. Each vault is searched with its own provider's model, so the query is embedded once per provider in the
    /// scope; a provider that is unavailable leaves its vaults to the words (<see cref="Kumbu.Core.Search.SemanticResult.WordsOnlyVaults"/>),
    /// and the result says so in its reason.
    /// Notes whose current version isn't indexed yet count as pending, and so do notes embedded with another model:
    /// their vectors don't compare with the query's, so they are skipped until re-embedded.
    /// <para>
    /// The cosines of two providers' models end up in one list as they are, though they aren't on one scale. Measured
    /// (docs/notes-search-external.md), that costs about a query of 37 against the better model alone, and
    /// standardising each model's cosines didn't do better.
    /// </para>
    /// </summary>
    public async Task<Kumbu.Core.Search.SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct)
    {
        if (index is null) return new(Kumbu.Core.Search.SemanticState.Off, [], 0, "search by meaning is off on this server");

        IEnumerable<VaultAccess> vaults = scope.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(scope, ct) is { } v ? [v] : [];
        var groups = vaults.GroupBy(v => v.Vault.EmbeddingProvider, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (groups.Count == 0)
        {
            var driver = index.Providers.InternalProvider.Driver;
            if (driver.State != Kumbu.Core.Search.SemanticState.Ready) return new(driver.State, [], 0, driver.Reason);
            return new(Kumbu.Core.Search.SemanticState.Ready, [], 0);
        }

        var hits = new List<Kumbu.Core.Search.SemanticHit>();
        var pending = 0;
        var searched = false;
        var unavailable = new List<(Kumbu.Core.Search.SemanticState State, string Reason, IReadOnlyList<VaultAccess> Vaults)>();
        // One request per provider, at the same time: a search waits for the slowest provider, not for all of them in turn.
        var queries = await Task.WhenAll(groups.Select(g => EmbedQueryAsync(g.Key, query, ct)));
        foreach (var (group, (state, reason, vector)) in groups.Zip(queries))
        {
            if (vector is null)
            {
                unavailable.Add((state, reason!, [.. group]));
                continue;
            }

            searched = true;
            var model = index.Providers.Get(group.Key)!.Driver.ModelKey;
            foreach (var access in group)
            {
                var vault = access.Vault;
                var within = access.ToVault(scope);
                var embeddings = vault.Embeddings;
                foreach (var (path, (_, note)) in vault.Notes)
                {
                    if (!InScope(within, path)) continue;
                    if (!embeddings.TryGetValue(path, out var e) || e.Model != model) { pending++; continue; }

                    // Same key, other length: the provider's model changed its output (no Dimensions configured).
                    // They don't compare; the note is embedded again.
                    if (e.Chunks.Any(c => c.Vector.Length != vector.Length))
                    {
                        pending++;
                        index.Enqueue(vault, [path]);
                        continue;
                    }

                    if (e.Version != note.Version) pending++;
                    foreach (var chunk in e.Chunks) hits.Add(new(access.ToCaller(path), chunk.StartLine, Search.Vectors.Dot(vector, chunk.Vector)));
                }

                // The pages of PDFs, as embedded for their current version; any other version's pages are left out
                // (their text is no longer the PDF's). PDFs don't count as pending: semsearch --words doesn't find them.
                foreach (var (path, (_, file)) in vault.Attachments)
                {
                    if (!AttachmentFormat.IsPdf(file.Current) || !InScope(within, path)) continue;
                    if (!embeddings.TryGetValue(path, out var e) || e.Model != model || e.Version != file.Version) continue;
                    if (e.Chunks.Any(c => c.Vector.Length != vector.Length))
                    {
                        index.Enqueue(vault, [path]);
                        continue;
                    }

                    foreach (var chunk in e.Chunks) hits.Add(new(access.ToCaller(path), chunk.StartLine, Search.Vectors.Dot(vector, chunk.Vector)));
                }
            }
        }

        if (!searched)
        {
            var state = unavailable.Any(u => u.State == Kumbu.Core.Search.SemanticState.Preparing) ? Kumbu.Core.Search.SemanticState.Preparing : Kumbu.Core.Search.SemanticState.Off;
            return new(state, [], 0, unavailable.Count == 1 ? unavailable[0].Reason : string.Join("; ", unavailable.Select(Named)));
        }

        // Some vaults were searched by meaning, others not: the search ranks the others' notes by words (SectionSearch).
        var partial = unavailable.Count == 0 ? null : string.Join("; ", unavailable.Select(Named));
        return new(Kumbu.Core.Search.SemanticState.Ready, [.. hits.OrderByDescending(h => h.Score).Take(limit)], pending, partial)
        {
            WordsOnlyVaults = [.. unavailable.SelectMany(u => u.Vaults).Select(v => v.Name)],
        };

        static string Named((Kumbu.Core.Search.SemanticState, string Reason, IReadOnlyList<VaultAccess> Vaults) u) =>
            $"{string.Join(", ", u.Vaults.Select(v => v.Name).Order(StringComparer.Ordinal))}: {u.Reason}";
    }

    /// <summary>The caller's vaults, unlocked, each under the caller's name for it (the vault endpoints show each one's
    /// embedding provider; the version list reads the working set).</summary>
    internal async Task<IReadOnlyList<VaultAccess>> UnlockedVaultsAsync(CancellationToken ct) => [.. (await VaultsAsync(ct)).Values];

    private static bool InScope(VaultPath scope, VaultPath path) =>
        scope.IsRoot || path == scope || path.Value.StartsWith(scope.Value + "/", StringComparison.Ordinal);

    /// <summary>The query's vector by one provider, or why there is none.</summary>
    private async Task<(Kumbu.Core.Search.SemanticState State, string? Reason, float[]? Vector)> EmbedQueryAsync(string provider, string query, CancellationToken ct)
    {
        if (index!.Providers.Get(provider)?.Driver is not { } driver)
        {
            return (Kumbu.Core.Search.SemanticState.Off, $"its embedding provider '{provider}' isn't configured on this server any more (the vault's owner can pick another)", null);
        }

        if (driver.State != Kumbu.Core.Search.SemanticState.Ready) return (driver.State, driver.Reason ?? "search by meaning isn't available", null);
        try
        {
            return (Kumbu.Core.Search.SemanticState.Ready, null, (await driver.EmbedAsync([query], query: true, ct))[0]);
        }
        catch (Search.EmbeddingUnavailableException e)
        {
            return (Kumbu.Core.Search.SemanticState.Off, e.Message, null);
        }
    }

    public async Task<IReadOnlyList<Checkpoint>> ListCheckpointsAsync(VaultPath vaultOrRoot, CancellationToken ct)
    {
        IEnumerable<VaultAccess> vaults = vaultOrRoot.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(vaultOrRoot, ct) is { } v ? [v] : [];
        return [.. vaults.SelectMany(v => v.Vault.Checkpoints.Select(c => Shown(v, c.Checkpoint))).OrderBy(c => c.Vault, StringComparer.Ordinal).ThenBy(c => c.CreatedAt).ThenBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>One transaction: the checkpoint's document, and each content it holds that the vault doesn't
    /// store yet (an upsert on the content's keyed hash, so equal texts are stored once). Its attachments name blobs the
    /// vault holds (checked in the transaction, <see cref="RequireBlobsAsync"/>); <see cref="BlobSweep"/> keeps them while
    /// the checkpoint exists.</summary>
    public async Task<bool> TryAddCheckpointAsync(Checkpoint checkpoint, IReadOnlyList<Note> notes, CancellationToken ct)
    {
        WriterRef.RequireStorable(checkpoint.Ref);
        if (await VaultAsync(checkpoint.Path, ct) is not { } access) return false;
        var vault = access.Vault;
        checkpoint = Stored(access, checkpoint);
        notes = [.. notes.Select(n => n with { Path = access.ToVault(n.Path) })];
        var textIds = notes.ToDictionary(n => n.Path, n => vault.TextId(n.Content));
        return await WriteAsync(vault, [], async session =>
        {
            var id = ObjectId.GenerateNewId();
            try
            {
                await catalog.Checkpoints.InsertOneAsync(
                    session,
                    new SealedDocument { Id = id, Vault = vault.Id, PathId = vault.NameId(checkpoint.Name), At = checkpoint.CreatedAt, Blob = vault.SealCheckpoint(id, checkpoint, textIds) },
                    cancellationToken: ct);
            }
            catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                return null;
            }

            foreach (var note in notes.DistinctBy(n => n.Content))
            {
                var textId = textIds[note.Path];
                await catalog.CheckpointTexts.UpdateOneAsync(
                    session,
                    F.Eq(d => d.Vault, vault.Id) & F.Eq(d => d.PathId, textId),
                    Builders<SealedDocument>.Update.SetOnInsert(d => d.Blob, vault.SealText(textId, note.Content)),
                    new UpdateOptions { IsUpsert = true },
                    ct);
            }

            await RequireBlobsAsync(session, vault, checkpoint.Attachments.Select(a => (a.Path, a.State.BlobId)), ct);
            var stored = new StoredCheckpoint(id, checkpoint, textIds);
            return () => vault.Checkpoints = vault.Checkpoints.Add(stored);
        }, ct);
    }

    /// <summary>One transaction: the checkpoint's document, and the texts no other checkpoint holds. The blobs of its
    /// attachments that nothing else references any more go with the next <see cref="BlobSweep"/>.</summary>
    public async Task<bool> TryRemoveCheckpointAsync(string vaultName, string name, CancellationToken ct)
    {
        if (await VaultAsync(VaultPath.Root.Child(vaultName), ct) is not { Vault: var vault }) return false;
        return await WriteAsync(vault, [], async session =>
        {
            if (vault.Checkpoints.FirstOrDefault(c => c.Checkpoint.Name == name) is not { } stored) return null;
            // Also on the name's id: an object of the vault's old key computes one that a resealed document no longer has.
            var deleted = await catalog.Checkpoints.DeleteOneAsync(session, F.Eq(d => d.Id, stored.Id) & F.Eq(d => d.PathId, vault.NameId(name)), cancellationToken: ct);
            if (deleted.DeletedCount == 0) return null;

            var kept = vault.Checkpoints.Where(c => c.Id != stored.Id).SelectMany(c => c.TextIds.Values).Select(Convert.ToBase64String).ToHashSet();
            var unused = stored.TextIds.Values.DistinctBy(Convert.ToBase64String).Where(t => !kept.Contains(Convert.ToBase64String(t))).ToList();
            if (unused.Count > 0)
            {
                await catalog.CheckpointTexts.DeleteManyAsync(session, F.Eq(d => d.Vault, vault.Id) & F.In(d => d.PathId, unused), cancellationToken: ct);
            }

            return () => vault.Checkpoints = vault.Checkpoints.Remove(stored);
        }, ct);
    }

    public async Task<IReadOnlyList<Note>> ReadCheckpointAsync(Checkpoint checkpoint, CancellationToken ct)
    {
        if (await VaultAsync(checkpoint.Path, ct) is not { } access
            || access.Vault.Checkpoints.FirstOrDefault(c => c.Checkpoint.Name == checkpoint.Name) is not { } stored)
        {
            return [];
        }

        var vault = access.Vault;

        var ids = stored.TextIds.Values.DistinctBy(Convert.ToBase64String).ToList();
        var texts = new Dictionary<string, string>();
        foreach (var doc in await catalog.CheckpointTexts.Find(F.Eq(d => d.Vault, vault.Id) & F.In(d => d.PathId, ids)).ToListAsync(ct))
        {
            try
            {
                texts[Convert.ToBase64String(doc.PathId)] = vault.OpenText(doc);
            }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or InvalidDataException)
            {
                // Skipping it would make a restore move the note to the trash: refuse the whole checkpoint instead.
                break;
            }
        }

        if (stored.TextIds.Values.Any(t => !texts.ContainsKey(Convert.ToBase64String(t))))
        {
            throw new VaultUnavailableException(
                $"checkpoint '{checkpoint.Name}' doesn't open: content it holds is missing, or was changed or damaged outside Kumbu²");
        }

        return [.. stored.TextIds.Select(t => new Note(access.ToCaller(t.Key), texts[Convert.ToBase64String(t.Value)]))];
    }

    /// <summary>Within a transaction: deletes the note if it is still at <c>current.Revision</c> and keeps it in
    /// the trash. Returns the change to the working set, or null if the note changed.</summary>
    private async Task<Action?> TrashInAsync(
        IClientSessionHandle session, UnlockedVault vault, StoredNote current, string deletedBy, WriterRef deletedByRef, DateTime deletedAt, Changeset? changeset,
        CancellationToken ct)
    {
        if (!vault.Notes.TryGetValue(current.Path, out var entry)) return null;
        var deleted = await catalog.Notes.DeleteOneAsync(session, AtRevision(entry.Id, current.Revision, vault.PathId(current.Path)), cancellationToken: ct);
        if (deleted.DeletedCount == 0) return null;

        var trashed = new TrashedNote(current, deletedBy, deletedAt, changeset, deletedByRef);
        WriterRef.RequireStorable(trashed.Ref);
        var trashId = ObjectId.GenerateNewId();
        await catalog.Trash.InsertOneAsync(
            session,
            new SealedDocument { Id = trashId, Vault = vault.Id, PathId = vault.PathId(current.Path), DeletedAt = deletedAt, Blob = vault.SealTrash(trashId, trashed) },
            cancellationToken: ct);
        return () =>
        {
            vault.Notes = vault.Notes.Remove(current.Path);
            vault.Trash = vault.Trash.Add((trashId, trashed));
        };
    }

    public async Task<IReadOnlyList<TrashedNote>> ListTrashAsync(VaultPath path, CancellationToken ct)
    {
        IEnumerable<VaultAccess> vaults = path.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(path, ct) is { } v ? [v] : [];
        var prefix = path.IsRoot ? "/" : path.Value + "/";
        return [.. vaults
            .SelectMany(v => v.Vault.Trash.Select(t => Shown(v, t.Trashed)))
            .Where(t => t.Note.Path == path || t.Note.Path.Value.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(t => t.DeletedAt)];
    }

    public async Task<StoredNote?> TryRestoreFromTrashAsync(VaultPath path, string? version, string writer, WriterRef reference, DateTime at, CancellationToken ct)
    {
        if (await VaultAsync(path, ct) is not { } access) return null;
        var vault = access.Vault;
        path = access.ToVault(path);
        StoredNote? restored = null;
        var written = await WriteAsync(vault, [path], async session =>
        {
            var latest = vault.Trash
                .Where(t => t.Trashed.Note.Path == path && (version is null || t.Trashed.Note.Version == version))
                .OrderByDescending(t => t.Trashed.DeletedAt)
                .FirstOrDefault();
            if (latest.Trashed is null) return null;

            var note = latest.Trashed.Note;
            restored = note with { Revision = note.Revision + 1, LastWriter = writer, LastWriteAt = at, Changeset = null, Ref = reference };
            var id = ObjectId.GenerateNewId();
            WriterRef.RequireStorable(restored.Ref);
            if (!await InsertNoteAsync(session, vault, id, restored, ct)) return null;
            var removed = await catalog.Trash.DeleteOneAsync(session, F.Eq(d => d.Id, latest.Id) & F.Eq(d => d.PathId, vault.PathId(path)), cancellationToken: ct);
            if (removed.DeletedCount == 0) return null;
            return () =>
            {
                vault.Notes = vault.Notes.SetItem(path, (id, restored));
                vault.Trash = vault.Trash.Remove(latest);
            };
        }, ct);
        return written ? Shown(access, restored!) : null;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction under the vault's gate. It returns what to change in the
    /// working set, or null to abort. The working set changes only after MongoDB committed; after an abort,
    /// <paramref name="paths"/> are read again, since someone else wrote them. A cancelled request aborts too,
    /// so it can't leave half a change behind. A change of notes or attachments by an API key is recorded on the key
    /// afterwards, outside the gate (<see cref="Accounts.ApiKeys.RecordWriteAsync"/>).
    /// </summary>
    private async Task<bool> WriteAsync(UnlockedVault vault, IReadOnlyList<VaultPath> paths, Func<IClientSessionHandle, Task<Action?>> work, CancellationToken ct)
    {
        var written = await WriteUnderGateAsync(vault, paths, work, ct);
        if (written && paths.Count > 0 && credential is ApiKeyCredential key && keys is not null) await keys.RecordWriteAsync(key.KeyId);
        return written;
    }

    private async Task<bool> WriteUnderGateAsync(UnlockedVault vault, IReadOnlyList<VaultPath> paths, Func<IClientSessionHandle, Task<Action?>> work, CancellationToken ct)
    {
        // Before waiting too: nothing queues behind a re-keying run.
        catalog.CheckWritable(vault);
        await vault.Gate.WaitAsync(ct);
        try
        {
            catalog.CheckWritable(vault);
            using var session = await catalog.StartSessionAsync(ct);
            session.StartTransaction();
            Action? apply;
            try
            {
                apply = await work(session);
            }
            catch (MongoException e) when (e.HasErrorLabel("TransientTransactionError"))
            {
                // A write conflict with another transaction: someone else got there first.
                apply = null;
            }
            catch
            {
                await AbortAsync(session);
                throw;
            }

            if (apply is null)
            {
                await AbortAsync(session);
                foreach (var path in paths) await catalog.RefreshAsync(vault, path, ct);
                return false;
            }

            // Once the commit is sent, wait for it: the working set must follow what MongoDB holds.
            await session.CommitTransactionAsync(CancellationToken.None);
            apply();
            index?.Enqueue(vault, paths.Where(p => !p.IsAttachment || AttachmentFormat.IsPdf(p)));
            await RecordAsync(vault, paths);
            return true;
        }
        finally
        {
            vault.Gate.Release();
        }
    }

    /// <summary>
    /// The change record and the hint (sync spec §5), under the gate after the commit: every way a note changes
    /// commits here, so none is missed. Checkpoints made or removed change no note and record nothing. A request
    /// holding an older object of the vault (one locked and opened again meanwhile) changed only that object's working
    /// set: the current one, which every other request and a sync client read, reads the paths again from MongoDB
    /// (under the gate the two share) and records them too.
    /// </summary>
    private async Task RecordAsync(UnlockedVault vault, IReadOnlyList<VaultPath> paths)
    {
        if (paths.Count == 0) return;
        var targets = new List<UnlockedVault> { vault };
        if (catalog.Current(vault.Id) is { } current && !ReferenceEquals(current, vault))
        {
            try
            {
                foreach (var path in paths) await catalog.RefreshAsync(current, path, CancellationToken.None);
                targets.Add(current);
            }
            catch (Exception e)
            {
                // The write has committed and stands; the current object just can't show it. It is let go, so the next
                // request loads the vault afresh (and a sync cursor from before gets the full list).
                catalog.Forget(current, e);
            }
        }

        var now = catalog.Now;
        foreach (var target in targets) target.ChangedAt = target.ChangedAt.SetItems(paths.Select(p => KeyValuePair.Create(p, now)));
        catalog.RaiseNotesChanged(vault.Id);
    }

    private static async Task AbortAsync(IClientSessionHandle session)
    {
        try
        {
            if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None);
        }
        catch (MongoException)
        {
            // The server already aborted it (a failed write inside a transaction does that).
        }
    }

    /// <summary>Every caller checks the entry first (<see cref="WriterRef.RequireStorable"/>): it throws inside the
    /// transaction, so a write path that forgets a reference is refused whole (collab design §8.1, fail closed).</summary>
    private Task<bool> InsertNoteAsync(IClientSessionHandle session, UnlockedVault vault, ObjectId id, StoredNote note, CancellationToken ct) =>
        InsertAtPathAsync(session, catalog.Notes, vault, id, note.Path, note.Revision, vault.SealNote(id, note), ct);

    private Task<bool> InsertAttachmentAsync(IClientSessionHandle session, UnlockedVault vault, ObjectId id, StoredAttachment attachment, CancellationToken ct) =>
        InsertAtPathAsync(session, catalog.Attachments, vault, id, attachment.Path, attachment.Revision, vault.SealAttachment(id, attachment), ct);

    /// <summary>Inserts a sealed note or attachment record; false if one exists at that path (the unique index refuses it).</summary>
    private static async Task<bool> InsertAtPathAsync(
        IClientSessionHandle session, IMongoCollection<SealedDocument> collection, UnlockedVault vault, ObjectId id, VaultPath path, long revision, byte[] blob, CancellationToken ct)
    {
        try
        {
            await collection.InsertOneAsync(
                session, new SealedDocument { Id = id, Vault = vault.Id, PathId = vault.PathId(path), Revision = revision, Blob = blob }, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    /// <summary>Replaces a sealed note or attachment record if it is still at <paramref name="revision"/> (compare-and-swap);
    /// false if it changed.</summary>
    private static async Task<bool> ReplaceAtRevisionAsync(
        IClientSessionHandle session, IMongoCollection<SealedDocument> collection, UnlockedVault vault, ObjectId id, VaultPath path, long revision, long nextRevision, byte[] blob,
        CancellationToken ct)
    {
        var update = Builders<SealedDocument>.Update.Set(d => d.Revision, nextRevision).Set(d => d.Blob, blob);
        return (await collection.UpdateOneAsync(session, AtRevision(id, revision, vault.PathId(path)), update, cancellationToken: ct)).MatchedCount > 0;
    }

    /// <summary>
    /// The document at this revision, and still under this path id: a re-keying keeps <c>_id</c> and <c>rev</c> but not
    /// the pid, so a writer still holding the old key (another process, say) matches nothing, rather than leaving
    /// a blob sealed under the old key beside the new key's pid.
    /// </summary>
    private static FilterDefinition<SealedDocument> AtRevision(ObjectId id, long revision, byte[] pathId) =>
        F.Eq(d => d.Id, id) & F.Eq(d => d.Revision, revision) & F.Eq(d => d.PathId, pathId);

    private async Task<IEnumerable<StoredNote>> NotesUnderAsync(VaultPath dir, CancellationToken ct)
    {
        IEnumerable<VaultAccess> vaults = dir.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(dir, ct) is { } v ? [v] : [];
        var prefix = dir.IsRoot ? "/" : dir.Value + "/";
        return vaults.SelectMany(v => v.Vault.Notes.Values.Select(n => Shown(v, n.Note))).Where(n => n.Path.Value.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>The vault of a path, marked as used now: only vaults a request actually touches count for
    /// the idle timeout and the least-recently-used order, not every vault its token can open. A vault in its
    /// two-key period is named but not served: <see cref="VaultBusyException"/>.</summary>
    private async Task<VaultAccess?> VaultAsync(VaultPath path, CancellationToken ct)
    {
        if (path.Vault is not { } name || !(await VaultsAsync(ct)).TryGetValue(name, out var access)) return null;
        if (access.Vault.Pending) throw VaultBusyException.Being();
        access.Vault.Touch(catalog.Now);
        return access;
    }

    /// <summary>All of the credential's vaults, marked as used (a search or listing over every vault); a vault in its
    /// two-key period is left out.</summary>
    private async Task<IEnumerable<VaultAccess>> AllVaultsAsync(CancellationToken ct)
    {
        var all = (await VaultsAsync(ct)).Values.Where(v => !v.Vault.Pending).ToList();
        foreach (var v in all) v.Vault.Touch(catalog.Now);
        return all;
    }

    /// <summary>
    /// The credential's vaults by the caller's names for them (collab design §3.2), unlocked once per store (one request);
    /// a vault in its two-key period as a placeholder (<see cref="UnlockedVault.Pending"/>), so that the name lists keep
    /// it. Two vaults under one name can't arise (the name index arbitrates own names and aliases alike); if they do,
    /// the request fails loudly rather than hide one of them (planner's decision 12).
    /// </summary>
    private async Task<Dictionary<string, VaultAccess>> VaultsAsync(CancellationToken ct)
    {
        if (_vaults is not null) return _vaults;
        var byName = new Dictionary<string, VaultAccess>(StringComparer.Ordinal);
        foreach (var access in await credential.OpenAsync(catalog, ct))
        {
            if (!byName.TryAdd(access.Name, access) && byName[access.Name].Id != access.Id)
            {
                throw new InvalidOperationException($"Vaults {byName[access.Name].Id} and {access.Id} have one name for this caller; neither is served.");
            }
        }

        _byId = byName.Values.ToDictionary(a => a.Id, StringComparer.Ordinal);
        return _vaults = byName;
    }

    /// <summary>A path of the vault as the caller sees it, for what the store says about it.</summary>
    private VaultPath Shown(UnlockedVault vault, VaultPath path) => _byId.TryGetValue(vault.Id, out var a) ? a.ToCaller(path) : path;

    // What the store hands out is in the caller's view (Shown), what it is given back is stored in the vault's own
    // (Stored): its notes' paths are sealed under the vault's own name. For the owner both are the same objects.

    private static StoredNote Shown(VaultAccess a, StoredNote n) => a.Renamed ? n with { Path = a.ToCaller(n.Path) } : n;

    private static StoredNote Stored(VaultAccess a, StoredNote n) => a.Renamed ? n with { Path = a.ToVault(n.Path) } : n;

    private static StoredAttachment Shown(VaultAccess a, StoredAttachment f) => a.Renamed ? f with { Path = a.ToCaller(f.Path) } : f;

    private static StoredAttachment Stored(VaultAccess a, StoredAttachment f) => a.Renamed ? f with { Path = a.ToVault(f.Path) } : f;

    private static TrashedNote Shown(VaultAccess a, TrashedNote t) => a.Renamed ? t with { Note = Shown(a, t.Note) } : t;

    private static TrashedAttachment Shown(VaultAccess a, TrashedAttachment t) => a.Renamed ? t with { Attachment = Shown(a, t.Attachment) } : t;

    private static TrashedAttachment Stored(VaultAccess a, TrashedAttachment t) => a.Renamed ? t with { Attachment = Stored(a, t.Attachment) } : t;

    private static MoveRecord Shown(VaultAccess a, MoveRecord m) => a.Renamed ? m with { From = a.ToCaller(m.From), To = a.ToCaller(m.To) } : m;

    private static Checkpoint Shown(VaultAccess a, Checkpoint c) => a.Renamed ? Rebased(c, a.ToCaller) : c;

    private static Checkpoint Stored(VaultAccess a, Checkpoint c) => a.Renamed ? Rebased(c, a.ToVault) : c;

    private static Checkpoint Rebased(Checkpoint c, Func<VaultPath, VaultPath> to) => c with
    {
        Path = to(c.Path),
        Notes = [.. c.Notes.Select(n => n with { Path = to(n.Path) })],
        Attachments = [.. c.Attachments.Select(f => f with { Path = to(f.Path) })],
    };

    /// <summary>A batch in the vault's own paths: every note and attachment in it.</summary>
    private static NoteBatch Stored(VaultAccess a, NoteBatch batch)
    {
        if (!a.Renamed) return batch;
        var stored = new NoteBatch { Changeset = batch.Changeset };
        stored.Replaces.AddRange(batch.Replaces.Select(r => (Stored(a, r.Current), Stored(a, r.Next))));
        stored.Moves.AddRange(batch.Moves.Select(m => (Stored(a, m.Current), Stored(a, m.Target))));
        stored.Trashes.AddRange(batch.Trashes.Select(n => Stored(a, n)));
        stored.Creates.AddRange(batch.Creates.Select(n => Stored(a, n)));
        stored.AttachmentReplaces.AddRange(batch.AttachmentReplaces.Select(r => (Stored(a, r.Current), Stored(a, r.Next))));
        stored.AttachmentMoves.AddRange(batch.AttachmentMoves.Select(m => (Stored(a, m.Current), Stored(a, m.Target))));
        stored.AttachmentTrashes.AddRange(batch.AttachmentTrashes.Select(f => Stored(a, f)));
        stored.AttachmentCreates.AddRange(batch.AttachmentCreates.Select(f => Stored(a, f)));
        stored.AttachmentRestores.AddRange(batch.AttachmentRestores.Select(r => (Stored(a, r.Trashed), Stored(a, r.Restored))));
        return stored;
    }
}
