// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Vaults;

/// <summary>
/// <see cref="INoteStore"/> over encrypted vaults, for one credential (one request). Reads come from the
/// unlocked working set; every write is one MongoDB transaction over sealed documents (compare-and-swap on
/// the revision), and changes the working set only after MongoDB committed it.
/// </summary>
public sealed class EncryptedNoteStore(VaultCatalog catalog, ICredential credential) : INoteStore
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

    /// <summary>Only the marker: <see cref="GetAsync"/>, which every command reads a note with first, already
    /// checked a damaged path against MongoDB again.</summary>
    public async Task<bool> IsDamagedAsync(VaultPath path, CancellationToken ct) =>
        await VaultAsync(path, ct) is { } v && !v.Notes.ContainsKey(path) && v.IsDamaged(path);

    public async Task<int> CountDamagedAsync(VaultPath vaultOrRoot, CancellationToken ct)
    {
        IEnumerable<UnlockedVault> vaults = vaultOrRoot.IsRoot ? (await VaultsAsync(ct)).Values : await VaultAsync(vaultOrRoot, ct) is { } v ? [v] : [];
        return vaults.Sum(v => v.DamagedPathIds.Count);
    }

    public Task<int> CountLockedVaultsAsync(CancellationToken ct) => catalog.CountLockedAsync(credential.Secret, ct);

    /// <summary>A path marked damaged is read from MongoDB again first, so a repair or removal in the database
    /// counts on the first access, without restarting the server.</summary>
    public async Task<StoredNote?> GetAsync(VaultPath path, CancellationToken ct)
    {
        if (await VaultAsync(path, ct) is not { } vault) return null;
        if (!vault.Notes.ContainsKey(path) && vault.IsDamaged(path))
        {
            await vault.Gate.WaitAsync(ct);
            try
            {
                await catalog.RefreshAsync(vault, path, ct);
            }
            finally
            {
                vault.Gate.Release();
            }
        }

        return vault.Notes.TryGetValue(path, out var n) ? n.Note : null;
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
    /// One transaction: replaces (compare-and-swap on the revision), then the sources of all moves are deleted
    /// (same check) before their targets are inserted, so a note may move to where another one just left. The
    /// unique index refuses a target that exists. Any refusal aborts the transaction: nothing is written.
    /// </summary>
    public async Task<IReadOnlyList<VaultPath>> TryCommitAsync(NoteBatch batch, string movedBy, DateTime at, CancellationToken ct)
    {
        if (batch.IsEmpty) return [];
        var paths = batch.Replaces.Select(r => r.Current.Path).Concat(batch.Moves.SelectMany(m => new[] { m.Current.Path, m.Target.Path })).Distinct().ToList();
        if (paths.Select(p => p.Vault).Distinct().Count() != 1) throw new ArgumentException("A batch changes notes of one vault", nameof(batch));
        if (await VaultAsync(paths[0], ct) is not { } vault) return paths;

        VaultPath? conflict = null;
        var written = await WriteAsync(vault, paths, async session =>
        {
            var apply = new List<Action>();
            foreach (var (current, next) in batch.Replaces)
            {
                if (!vault.Notes.TryGetValue(current.Path, out var entry)) return Refuse(current.Path);
                var update = Builders<SealedDocument>.Update
                    .Set(d => d.Revision, next.Revision)
                    .Set(d => d.Blob, vault.SealNote(entry.Id, next));
                var result = await catalog.Notes.UpdateOneAsync(session, AtRevision(entry.Id, current.Revision), update, cancellationToken: ct);
                if (result.MatchedCount == 0) return Refuse(current.Path);
                apply.Add(() => vault.Notes = vault.Notes.SetItem(current.Path, (entry.Id, next)));
            }

            foreach (var (current, _) in batch.Moves)
            {
                if (!vault.Notes.TryGetValue(current.Path, out var entry)) return Refuse(current.Path);
                var deleted = await catalog.Notes.DeleteOneAsync(session, AtRevision(entry.Id, current.Revision), cancellationToken: ct);
                if (deleted.DeletedCount == 0) return Refuse(current.Path);
                apply.Add(() => vault.Notes = vault.Notes.Remove(current.Path));
            }

            foreach (var (current, target) in batch.Moves)
            {
                var id = ObjectId.GenerateNewId();
                if (!await InsertNoteAsync(session, vault, id, target, ct)) return Refuse(target.Path);

                var move = new MoveRecord(current.Path, target.Path, movedBy, at);
                var moveId = ObjectId.GenerateNewId();
                await catalog.Moves.InsertOneAsync(
                    session,
                    new SealedDocument { Id = moveId, Vault = vault.Id, PathId = vault.PathId(current.Path), At = at, Blob = vault.SealMove(moveId, move) },
                    cancellationToken: ct);
                apply.Add(() =>
                {
                    vault.Notes = vault.Notes.SetItem(target.Path, (id, target));
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
    }

    public async Task<bool> TryTrashAsync(StoredNote current, string deletedBy, DateTime deletedAt, CancellationToken ct)
    {
        if (await VaultAsync(current.Path, ct) is not { } vault) return false;
        return await WriteAsync(vault, [current.Path], async session =>
        {
            if (!vault.Notes.TryGetValue(current.Path, out var entry)) return null;
            var deleted = await catalog.Notes.DeleteOneAsync(session, AtRevision(entry.Id, current.Revision), cancellationToken: ct);
            if (deleted.DeletedCount == 0) return null;

            var trashed = new TrashedNote(current, deletedBy, deletedAt);
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
        }, ct);
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
            restored = note with { Revision = note.Revision + 1, LastWriter = writer, LastWriteAt = at };
            var id = ObjectId.GenerateNewId();
            if (!await InsertNoteAsync(session, vault, id, restored, ct)) return null;
            var removed = await catalog.Trash.DeleteOneAsync(session, F.Eq(d => d.Id, latest.Id), cancellationToken: ct);
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
        await vault.Gate.WaitAsync(ct);
        try
        {
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
            return true;
        }
        finally
        {
            vault.Gate.Release();
        }
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

    /// <summary>Inserts a sealed note; false if one exists at that path (the unique index refuses it).</summary>
    private async Task<bool> InsertNoteAsync(IClientSessionHandle session, UnlockedVault vault, ObjectId id, StoredNote note, CancellationToken ct)
    {
        try
        {
            await catalog.Notes.InsertOneAsync(
                session,
                new SealedDocument { Id = id, Vault = vault.Id, PathId = vault.PathId(note.Path), Revision = note.Revision, Blob = vault.SealNote(id, note) },
                cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    private static FilterDefinition<SealedDocument> AtRevision(ObjectId id, long revision) =>
        F.Eq(d => d.Id, id) & F.Eq(d => d.Revision, revision);

    private async Task<IEnumerable<StoredNote>> NotesUnderAsync(VaultPath dir, CancellationToken ct)
    {
        IEnumerable<UnlockedVault> vaults = dir.IsRoot ? await AllVaultsAsync(ct) : await VaultAsync(dir, ct) is { } v ? [v] : [];
        var prefix = dir.IsRoot ? "/" : dir.Value + "/";
        return vaults.SelectMany(v => v.Notes.Values.Select(n => n.Note)).Where(n => n.Path.Value.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>The vault of a path, marked as used now: only vaults a request actually touches count for
    /// the idle timeout and the least-recently-used order, not every vault its token can open.</summary>
    private async Task<UnlockedVault?> VaultAsync(VaultPath path, CancellationToken ct)
    {
        if (path.Vault is not { } name || !(await VaultsAsync(ct)).TryGetValue(name, out var vault)) return null;
        vault.Touch(catalog.Now);
        return vault;
    }

    /// <summary>All of the credential's vaults, marked as used (a search or listing over every vault).</summary>
    private async Task<IEnumerable<UnlockedVault>> AllVaultsAsync(CancellationToken ct)
    {
        var all = (await VaultsAsync(ct)).Values;
        foreach (var v in all) v.Touch(catalog.Now);
        return all;
    }

    /// <summary>The credential's vaults by name, unlocked once per store (one request).</summary>
    private async Task<Dictionary<string, UnlockedVault>> VaultsAsync(CancellationToken ct)
    {
        if (_vaults is not null) return _vaults;
        var open = await catalog.OpenAsync(credential.Secret, ct);
        return _vaults = open.GroupBy(v => v.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }
}
