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
public sealed class EncryptedNoteStore(VaultCatalog catalog, ICredential credential, Search.SearchIndex? index = null)
    : INoteStore, IAttachmentStore, Kumbu.Core.Search.ISearchIndex
{
    private static FilterDefinitionBuilder<SealedDocument> F => Builders<SealedDocument>.Filter;

    private Dictionary<string, UnlockedVault>? _vaults;

    public async Task<IReadOnlyList<string>> ListVaultsAsync(CancellationToken ct) => [.. (await VaultsAsync(ct)).Keys];

    public async Task<string?> ReadAsync(VaultPath path, CancellationToken ct) => (await GetAsync(path, ct))?.Content;

    public async Task<string?> ReadVersionAsync(VaultPath path, string version, CancellationToken ct) =>
        await GetAsync(path, ct) is { } note ? Versions.Find(note, version) : null;

    public async Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct) =>
        [.. (await NotesUnderAsync(dir, ct)).Select(n => n.Path)];

    public async Task<IReadOnlyList<Note>> ReadNotesUnderAsync(VaultPath dir, CancellationToken ct) =>
        [.. (await NotesUnderAsync(dir, ct)).Select(n => new Note(n.Path, n.Content))];

    public async Task<MoveRecord?> FindMoveAsync(VaultPath from, CancellationToken ct) =>
        await VaultAsync(from, ct) is { } v ? v.Moves.Where(m => m.From == from).MaxBy(m => m.MovedAt) : null;

    public async Task<IReadOnlyList<MoveRecord>> ListMovesAsync(VaultPath dir, CancellationToken ct)
    {
        IEnumerable<UnlockedVault> vaults = dir.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(dir, ct) is { } v ? [v] : [];
        var prefix = dir.IsRoot ? "/" : dir.Value + "/";
        return [.. vaults.SelectMany(v => v.Moves)
            .Where(m => m.From.Value.StartsWith(prefix, StringComparison.Ordinal) || m.To.Value.StartsWith(prefix, StringComparison.Ordinal))];
    }

    /// <summary>Only the marker: <see cref="GetAsync"/>, which every command reads a note with first, already
    /// checked a damaged path against MongoDB again.</summary>
    public async Task<bool> IsDamagedAsync(VaultPath path, CancellationToken ct) =>
        await VaultAsync(path, ct) is { } v && !v.Notes.ContainsKey(path) && !v.Attachments.ContainsKey(path) && v.IsDamaged(path);

    public async Task<int> CountDamagedAsync(VaultPath vaultOrRoot, CancellationToken ct)
    {
        IEnumerable<UnlockedVault> vaults = vaultOrRoot.IsRoot ? (await VaultsAsync(ct)).Values.Where(v => !v.Pending) : await VaultAsync(vaultOrRoot, ct) is { } v ? [v] : [];
        return vaults.Sum(v => v.DamagedPathIds.Count);
    }

    public Task<int> CountLockedVaultsAsync(CancellationToken ct) => credential.CountLockedAsync(catalog, ct);

    public async Task<(Changeset Changeset, DateTime LastWrite)?> LatestChangesetAsync(string vault, string writer, CancellationToken ct) =>
        await VaultAsync(VaultPath.Root.Child(vault), ct) is { } v
            ? Changeset.Latest(
                v.Notes.Values.Select(n => n.Note), v.Trash.Select(t => t.Trashed), v.Moves, writer,
                v.Attachments.Values.Select(a => a.Attachment), v.AttachmentTrash.Select(t => t.Trashed))
            : null;

    /// <summary>A path marked damaged is read from MongoDB again first, so a repair or removal in the database
    /// counts on the first access, without restarting the server.</summary>
    public async Task<StoredNote?> GetAsync(VaultPath path, CancellationToken ct)
    {
        if (await VaultAsync(path, ct) is not { } vault) return null;
        if (!vault.Notes.ContainsKey(path)) await RefreshDamagedAsync(vault, path, ct);
        return vault.Notes.TryGetValue(path, out var n) ? n.Note : null;
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
        if (await VaultAsync(note.Path, ct) is not { } vault) return false;
        return await WriteAsync(vault, [note.Path], async session =>
        {
            var id = ObjectId.GenerateNewId();
            if (!await InsertNoteAsync(session, vault, id, note, ct)) return null;
            return () => vault.Notes = vault.Notes.SetItem(note.Path, (id, note));
        }, ct);
    }

    public async Task<bool> TryReplaceAsync(StoredNote current, StoredNote next, CancellationToken ct) =>
        (await TryCommitAsync(new NoteBatch { Replaces = { (current, next) } }, current.LastWriter, current.LastWriteAt, ct)).Count == 0;

    /// <summary>
    /// One transaction: replaces (compare-and-swap on the revision), notes and attachments to the trash, restored
    /// attachments out of it, then the sources of all moves (notes' and attachments') are deleted (same check) before
    /// created notes and attachments, restored attachments and move targets are inserted, so a note may move to where
    /// another one just left. Every move, of a note or an attachment, is recorded in the moves. The unique index refuses a target that exists. Any refusal aborts the transaction: nothing is written.
    /// The blobs the attachments name are put before (<see cref="PutBlobAsync"/>), outside the transaction.
    /// </summary>
    public async Task<IReadOnlyList<VaultPath>> TryCommitAsync(NoteBatch batch, string by, DateTime at, CancellationToken ct)
    {
        if (batch.IsEmpty) return [];
        var paths = batch.Paths.ToList();
        if (paths.Select(p => p.Vault).Distinct().Count() != 1) throw new ArgumentException("A batch changes notes of one vault", nameof(batch));
        if (await VaultAsync(paths[0], ct) is not { } vault) return paths;

        VaultPath? conflict = null;
        var written = await WriteAsync(vault, paths, async session =>
        {
            var apply = new List<Action>();
            foreach (var (current, next) in batch.Replaces)
            {
                if (!vault.Notes.TryGetValue(current.Path, out var entry)
                    || !await ReplaceAtRevisionAsync(session, catalog.Notes, vault, entry.Id, current.Path, current.Revision, next.Revision, vault.SealNote(entry.Id, next), ct))
                {
                    return Refuse(current.Path);
                }

                apply.Add(() => vault.Notes = vault.Notes.SetItem(current.Path, (entry.Id, next)));
            }

            foreach (var (current, next) in batch.AttachmentReplaces)
            {
                if (!vault.Attachments.TryGetValue(current.Path, out var entry)
                    || !await ReplaceAtRevisionAsync(session, catalog.Attachments, vault, entry.Id, current.Path, current.Revision, next.Revision, vault.SealAttachment(entry.Id, next), ct))
                {
                    return Refuse(current.Path);
                }

                apply.Add(() => vault.Attachments = vault.Attachments.SetItem(current.Path, (entry.Id, next)));
            }

            foreach (var current in batch.Trashes)
            {
                if (await TrashInAsync(session, vault, current, by, at, batch.Changeset, ct) is not { } trashed) return Refuse(current.Path);
                apply.Add(trashed);
            }

            foreach (var current in batch.AttachmentTrashes)
            {
                if (!vault.Attachments.TryGetValue(current.Path, out var entry)) return Refuse(current.Path);
                var deleted = await catalog.Attachments.DeleteOneAsync(session, AtRevision(entry.Id, current.Revision, vault.PathId(current.Path)), cancellationToken: ct);
                if (deleted.DeletedCount == 0) return Refuse(current.Path);

                var trashed = new TrashedAttachment(current, by, at, batch.Changeset);
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
                if (!await InsertNoteAsync(session, vault, id, created, ct)) return Refuse(created.Path);
                apply.Add(() => vault.Notes = vault.Notes.SetItem(created.Path, (id, created)));
            }

            foreach (var created in batch.AttachmentCreates.Concat(batch.AttachmentRestores.Select(r => r.Restored)))
            {
                var id = ObjectId.GenerateNewId();
                if (!await InsertAttachmentAsync(session, vault, id, created, ct)) return Refuse(created.Path);
                apply.Add(() => vault.Attachments = vault.Attachments.SetItem(created.Path, (id, created)));
            }

            foreach (var (current, target) in batch.Moves)
            {
                var id = ObjectId.GenerateNewId();
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
                if (!await InsertAttachmentAsync(session, vault, id, target, ct)) return Refuse(target.Path);
                var move = await RecordMoveAsync(session, current.Path, target.Path);
                apply.Add(() =>
                {
                    vault.Attachments = vault.Attachments.SetItem(target.Path, (id, target));
                    vault.Moves = vault.Moves.Add(move);
                });
            }

            return () => apply.ForEach(a => a());
        }, ct);

        return written ? [] : [conflict ?? paths[0]];

        Action? Refuse(VaultPath path)
        {
            conflict = path;
            return null;
        }

        async Task<MoveRecord> RecordMoveAsync(IClientSessionHandle session, VaultPath from, VaultPath to)
        {
            var move = new MoveRecord(from, to, by, at, batch.Changeset);
            var moveId = ObjectId.GenerateNewId();
            await catalog.Moves.InsertOneAsync(
                session,
                new SealedDocument { Id = moveId, Vault = vault.Id, PathId = vault.PathId(from), At = at, Blob = vault.SealMove(moveId, move) },
                cancellationToken: ct);
            return move;
        }
    }

    /// <summary>As <see cref="GetAsync"/>, a damaged path is read from MongoDB again first.</summary>
    public async Task<StoredAttachment?> GetAttachmentAsync(VaultPath path, CancellationToken ct)
    {
        if (await VaultAsync(path, ct) is not { } vault) return null;
        if (!vault.Attachments.ContainsKey(path)) await RefreshDamagedAsync(vault, path, ct);
        return vault.Attachments.TryGetValue(path, out var a) ? a.Attachment : null;
    }

    public async Task<IReadOnlyList<StoredAttachment>> ListAttachmentsUnderAsync(VaultPath folder, CancellationToken ct)
    {
        IEnumerable<UnlockedVault> vaults = folder.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(folder, ct) is { } v ? [v] : [];
        var prefix = folder.IsRoot ? "/" : folder.Value + "/";
        return [.. vaults.SelectMany(v => v.Attachments.Values.Select(a => a.Attachment)).Where(a => a.Path.Value.StartsWith(prefix, StringComparison.Ordinal))];
    }

    public async Task<IReadOnlyList<TrashedAttachment>> ListAttachmentTrashAsync(VaultPath? path, CancellationToken ct)
    {
        var scope = path ?? VaultPath.Root;
        IEnumerable<UnlockedVault> vaults = scope.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(scope, ct) is { } v ? [v] : [];
        return [.. vaults
            .SelectMany(v => v.AttachmentTrash.Select(t => t.Trashed))
            .Where(t => InScope(scope, t.Attachment.Path))
            .OrderByDescending(t => t.DeletedAt)];
    }

    /// <summary>The blob read from MongoDB and opened, one at a time; <see cref="VaultUnavailableException"/> if it
    /// doesn't open (changed or damaged outside Kumbu²).</summary>
    public async Task<Stream?> OpenBlobAsync(string vault, string blobId, CancellationToken ct)
    {
        if (await VaultAsync(VaultPath.Root.Child(vault), ct) is not { } v || !TryParseBlobId(blobId, out var id)) return null;
        var doc = await catalog.Blobs.Find(Builders<BlobDocument>.Filter.Eq(d => d.Vault, v.Id) & Builders<BlobDocument>.Filter.Eq(d => d.BlobId, id)).FirstOrDefaultAsync(ct);
        if (doc is null) return null;
        try
        {
            return new MemoryStream(v.OpenBlob(doc), writable: false);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or InvalidDataException)
        {
            throw new VaultUnavailableException("an attachment's content doesn't open: it was changed or damaged outside Kumbu²");
        }
    }

    /// <summary>
    /// Stores the bytes sealed under their keyed hash unless the vault holds them already, in which case only their time
    /// is set to now: a blob that nothing references yet stays for <see cref="BlobSweep.Grace"/> after its last put, so
    /// the record that will name it can commit. Under the vault's gate, so neither a sweep, a deletion nor a re-keying run
    /// meets it half way.
    /// </summary>
    public async Task<string> PutBlobAsync(string vault, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length > AttachmentFormat.MaxBytes) throw new ArgumentException("An attachment holds at most 15 MB.", nameof(bytes));
        if (await VaultAsync(VaultPath.Root.Child(vault), ct) is not { } v) throw new ArgumentException($"There is no vault named '{vault}'.", nameof(vault));
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

    private static bool TryParseBlobId(string blobId, out byte[] id)
    {
        id = new byte[32];
        return Convert.TryFromBase64String(blobId, id, out var written) && written == id.Length;
    }

    public async Task<bool> TryTrashAsync(StoredNote current, string deletedBy, DateTime deletedAt, CancellationToken ct)
    {
        if (await VaultAsync(current.Path, ct) is not { } vault) return false;
        return await WriteAsync(vault, [current.Path], session => TrashInAsync(session, vault, current, deletedBy, deletedAt, null, ct), ct);
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

        IEnumerable<UnlockedVault> vaults = scope.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(scope, ct) is { } v ? [v] : [];
        var groups = vaults.GroupBy(v => v.EmbeddingProvider, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (groups.Count == 0)
        {
            var driver = index.Providers.InternalProvider.Driver;
            if (driver.State != Kumbu.Core.Search.SemanticState.Ready) return new(driver.State, [], 0, driver.Reason);
            return new(Kumbu.Core.Search.SemanticState.Ready, [], 0);
        }

        var hits = new List<Kumbu.Core.Search.SemanticHit>();
        var pending = 0;
        var searched = false;
        var unavailable = new List<(Kumbu.Core.Search.SemanticState State, string Reason, IReadOnlyList<UnlockedVault> Vaults)>();
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
            foreach (var vault in group)
            {
                var embeddings = vault.Embeddings;
                foreach (var (path, (_, note)) in vault.Notes)
                {
                    if (!InScope(scope, path)) continue;
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
                    foreach (var chunk in e.Chunks) hits.Add(new(path, chunk.StartLine, Search.Vectors.Dot(vector, chunk.Vector)));
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

        static string Named((Kumbu.Core.Search.SemanticState, string Reason, IReadOnlyList<UnlockedVault> Vaults) u) =>
            $"{string.Join(", ", u.Vaults.Select(v => v.Name).Order(StringComparer.Ordinal))}: {u.Reason}";
    }

    /// <summary>The caller's vaults, unlocked (the vault endpoints show each one's embedding provider).</summary>
    internal async Task<IReadOnlyList<UnlockedVault>> UnlockedVaultsAsync(CancellationToken ct) => [.. (await VaultsAsync(ct)).Values];

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
        IEnumerable<UnlockedVault> vaults = vaultOrRoot.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(vaultOrRoot, ct) is { } v ? [v] : [];
        return [.. vaults.SelectMany(v => v.Checkpoints).Select(c => c.Checkpoint).OrderBy(c => c.Vault, StringComparer.Ordinal).ThenBy(c => c.CreatedAt).ThenBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>One transaction: the checkpoint's document, and each content it holds that the vault doesn't
    /// store yet (an upsert on the content's keyed hash, so equal texts are stored once). Its attachments name blobs the
    /// vault holds already; <see cref="BlobSweep"/> keeps them while the checkpoint exists.</summary>
    public async Task<bool> TryAddCheckpointAsync(Checkpoint checkpoint, IReadOnlyList<Note> notes, CancellationToken ct)
    {
        if (await VaultAsync(checkpoint.Path, ct) is not { } vault) return false;
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

            var stored = new StoredCheckpoint(id, checkpoint, textIds);
            return () => vault.Checkpoints = vault.Checkpoints.Add(stored);
        }, ct);
    }

    /// <summary>One transaction: the checkpoint's document, and the texts no other checkpoint holds. The blobs of its
    /// attachments that nothing else references any more go with the next <see cref="BlobSweep"/>.</summary>
    public async Task<bool> TryRemoveCheckpointAsync(string vaultName, string name, CancellationToken ct)
    {
        if (await VaultAsync(VaultPath.Root.Child(vaultName), ct) is not { } vault) return false;
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
        if (await VaultAsync(checkpoint.Path, ct) is not { } vault
            || vault.Checkpoints.FirstOrDefault(c => c.Checkpoint.Name == checkpoint.Name) is not { } stored)
        {
            return [];
        }

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

        return [.. stored.TextIds.Select(t => new Note(t.Key, texts[Convert.ToBase64String(t.Value)]))];
    }

    /// <summary>Within a transaction: deletes the note if it is still at <c>current.Revision</c> and keeps it in
    /// the trash. Returns the change to the working set, or null if the note changed.</summary>
    private async Task<Action?> TrashInAsync(
        IClientSessionHandle session, UnlockedVault vault, StoredNote current, string deletedBy, DateTime deletedAt, Changeset? changeset, CancellationToken ct)
    {
        if (!vault.Notes.TryGetValue(current.Path, out var entry)) return null;
        var deleted = await catalog.Notes.DeleteOneAsync(session, AtRevision(entry.Id, current.Revision, vault.PathId(current.Path)), cancellationToken: ct);
        if (deleted.DeletedCount == 0) return null;

        var trashed = new TrashedNote(current, deletedBy, deletedAt, changeset);
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
        IEnumerable<UnlockedVault> vaults = path.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(path, ct) is { } v ? [v] : [];
        var prefix = path.IsRoot ? "/" : path.Value + "/";
        return [.. vaults
            .SelectMany(v => v.Trash.Select(t => t.Trashed))
            .Where(t => t.Note.Path == path || t.Note.Path.Value.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(t => t.DeletedAt)];
    }

    public async Task<StoredNote?> TryRestoreFromTrashAsync(VaultPath path, string? version, string writer, DateTime at, CancellationToken ct)
    {
        if (await VaultAsync(path, ct) is not { } vault) return null;
        StoredNote? restored = null;
        var written = await WriteAsync(vault, [path], async session =>
        {
            var latest = vault.Trash
                .Where(t => t.Trashed.Note.Path == path && (version is null || t.Trashed.Note.Version == version))
                .OrderByDescending(t => t.Trashed.DeletedAt)
                .FirstOrDefault();
            if (latest.Trashed is null) return null;

            var note = latest.Trashed.Note;
            restored = note with { Revision = note.Revision + 1, LastWriter = writer, LastWriteAt = at, Changeset = null };
            var id = ObjectId.GenerateNewId();
            if (!await InsertNoteAsync(session, vault, id, restored, ct)) return null;
            var removed = await catalog.Trash.DeleteOneAsync(session, F.Eq(d => d.Id, latest.Id) & F.Eq(d => d.PathId, vault.PathId(path)), cancellationToken: ct);
            if (removed.DeletedCount == 0) return null;
            return () =>
            {
                vault.Notes = vault.Notes.SetItem(path, (id, restored));
                vault.Trash = vault.Trash.Remove(latest);
            };
        }, ct);
        return written ? restored : null;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction under the vault's gate. It returns what to change in the
    /// working set, or null to abort. The working set changes only after MongoDB committed; after an abort,
    /// <paramref name="paths"/> are read again, since someone else wrote them. A cancelled request aborts too,
    /// so it can't leave half a change behind.
    /// </summary>
    private async Task<bool> WriteAsync(UnlockedVault vault, IReadOnlyList<VaultPath> paths, Func<IClientSessionHandle, Task<Action?>> work, CancellationToken ct)
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
            index?.Enqueue(vault, paths.Where(p => !p.IsAttachment));
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
        IEnumerable<UnlockedVault> vaults = dir.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(dir, ct) is { } v ? [v] : [];
        var prefix = dir.IsRoot ? "/" : dir.Value + "/";
        return vaults.SelectMany(v => v.Notes.Values.Select(n => n.Note)).Where(n => n.Path.Value.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>The vault of a path, marked as used now: only vaults a request actually touches count for
    /// the idle timeout and the least-recently-used order, not every vault its token can open. A vault in its
    /// two-key period is named but not served: <see cref="VaultBusyException"/>.</summary>
    private async Task<UnlockedVault?> VaultAsync(VaultPath path, CancellationToken ct)
    {
        if (path.Vault is not { } name || !(await VaultsAsync(ct)).TryGetValue(name, out var vault)) return null;
        if (vault.Pending) throw VaultBusyException.Being();
        vault.Touch(catalog.Now);
        return vault;
    }

    /// <summary>All of the credential's vaults, marked as used (a search or listing over every vault); a vault in its
    /// two-key period is left out.</summary>
    private async Task<IEnumerable<UnlockedVault>> AllVaultsAsync(CancellationToken ct)
    {
        var all = (await VaultsAsync(ct)).Values.Where(v => !v.Pending).ToList();
        foreach (var v in all) v.Touch(catalog.Now);
        return all;
    }

    /// <summary>The credential's vaults by name, unlocked once per store (one request); a vault in its two-key period
    /// as a placeholder (<see cref="UnlockedVault.Pending"/>), so that the name lists keep it.</summary>
    private async Task<Dictionary<string, UnlockedVault>> VaultsAsync(CancellationToken ct)
    {
        if (_vaults is not null) return _vaults;
        var open = await credential.OpenAsync(catalog, ct);
        return _vaults = open.GroupBy(v => v.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }
}
