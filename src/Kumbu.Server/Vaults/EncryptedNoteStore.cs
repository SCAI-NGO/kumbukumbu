// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Vaults;

/// <summary>
/// <see cref="INoteStore"/> over encrypted vaults, for one credential (one request). Reads come from the
/// unlocked working set; every write is one atomic MongoDB operation on a sealed document (compare-and-swap
/// on the revision), and changes the working set only after MongoDB took it.
/// </summary>
public sealed class EncryptedNoteStore(VaultCatalog catalog, ICredential credential) : INoteStore
{
    private static FilterDefinitionBuilder<SealedDocument> F => Builders<SealedDocument>.Filter;

    private Dictionary<string, UnlockedVault>? _vaults;

    public async Task<IReadOnlyList<string>> ListVaultsAsync(CancellationToken ct) => [.. (await VaultsAsync(ct)).Keys];

    public async Task<string?> ReadAsync(VaultPath path, CancellationToken ct) =>
        await VaultAsync(path, ct) is { } v && v.Notes.TryGetValue(path, out var n) ? n.Note.Content : null;

    public async Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct) =>
        [.. (await NotesUnderAsync(dir, ct)).Select(n => n.Path)];

    public async Task<IReadOnlyList<Note>> ReadNotesUnderAsync(VaultPath dir, CancellationToken ct) =>
        [.. (await NotesUnderAsync(dir, ct)).Select(n => new Note(n.Path, n.Content))];

    public async Task<MoveRecord?> FindMoveAsync(VaultPath from, CancellationToken ct) =>
        await VaultAsync(from, ct) is { } v ? v.Moves.Where(m => m.From == from).MaxBy(m => m.MovedAt) : null;

    /// <summary>Checks a path known to be damaged against MongoDB again, so a repair or removal in the database
    /// counts at once, without restarting the server.</summary>
    public async Task<bool> IsDamagedAsync(VaultPath path, CancellationToken ct)
    {
        if (await VaultAsync(path, ct) is not { } v || v.Notes.ContainsKey(path) || !v.IsDamaged(path)) return false;
        await catalog.RefreshAsync(v, path, ct);
        return !v.Notes.ContainsKey(path) && v.IsDamaged(path);
    }

    public async Task<int> CountDamagedAsync(VaultPath vaultOrRoot, CancellationToken ct)
    {
        IEnumerable<UnlockedVault> vaults = vaultOrRoot.IsRoot ? (await VaultsAsync(ct)).Values : await VaultAsync(vaultOrRoot, ct) is { } v ? [v] : [];
        return vaults.Sum(v => v.DamagedPathIds.Count);
    }

    public Task<int> CountLockedVaultsAsync(CancellationToken ct) => catalog.CountLockedAsync(credential.Secret, ct);

    public async Task<StoredNote?> GetAsync(VaultPath path, CancellationToken ct) =>
        await VaultAsync(path, ct) is { } v && v.Notes.TryGetValue(path, out var n) ? n.Note : null;

    public async Task<bool> TryCreateAsync(StoredNote note, CancellationToken ct)
    {
        if (await VaultAsync(note.Path, ct) is not { } vault) return false;
        await vault.Gate.WaitAsync(ct);
        try
        {
            return await InsertAsync(vault, note, ct);
        }
        finally
        {
            vault.Gate.Release();
        }
    }

    public async Task<bool> TryReplaceAsync(StoredNote current, StoredNote next, CancellationToken ct)
    {
        if (await VaultAsync(current.Path, ct) is not { } vault || !vault.Notes.TryGetValue(current.Path, out var entry)) return false;
        await vault.Gate.WaitAsync(ct);
        try
        {
            var update = Builders<SealedDocument>.Update
                .Set(d => d.Revision, next.Revision)
                .Set(d => d.Blob, vault.SealNote(entry.Id, next));
            var result = await catalog.Notes.UpdateOneAsync(AtRevision(entry.Id, current.Revision), update, cancellationToken: ct);
            if (result.MatchedCount == 0)
            {
                await catalog.RefreshAsync(vault, current.Path, CancellationToken.None);
                return false;
            }

            vault.Notes = vault.Notes.SetItem(current.Path, (entry.Id, next));
            return true;
        }
        finally
        {
            vault.Gate.Release();
        }
    }

    public async Task<bool> TryMoveAsync(StoredNote current, StoredNote target, string movedBy, DateTime at, CancellationToken ct)
    {
        if (await VaultAsync(current.Path, ct) is not { } vault || !vault.Notes.TryGetValue(current.Path, out var entry)) return false;
        if (target.Path.Vault != current.Path.Vault) return false;
        await vault.Gate.WaitAsync(ct);
        try
        {
            // Insert at the target first (the unique index refuses an existing note), then delete the source
            // only if nobody wrote in between; otherwise take the copy back. Nothing is ever lost.
            if (!await InsertAsync(vault, target, ct)) return false;

            // The first write is committed: from here on, finish or undo it even if the request is cancelled.
            var deleted = await catalog.Notes.DeleteOneAsync(AtRevision(entry.Id, current.Revision), CancellationToken.None);
            if (deleted.DeletedCount == 0)
            {
                await catalog.Notes.DeleteOneAsync(F.Eq(d => d.Id, vault.Notes[target.Path].Id), CancellationToken.None);
                vault.Notes = vault.Notes.Remove(target.Path);
                await catalog.RefreshAsync(vault, current.Path, CancellationToken.None);
                return false;
            }

            var move = new MoveRecord(current.Path, target.Path, movedBy, at);
            var moveId = ObjectId.GenerateNewId();
            vault.Notes = vault.Notes.Remove(current.Path);
            await catalog.Moves.InsertOneAsync(
                new SealedDocument { Id = moveId, Vault = vault.Id, PathId = vault.PathId(current.Path), At = at, Blob = vault.SealMove(moveId, move) },
                cancellationToken: CancellationToken.None);
            vault.Moves = vault.Moves.Add(move);
            return true;
        }
        finally
        {
            vault.Gate.Release();
        }
    }

    public async Task<bool> TryTrashAsync(StoredNote current, string deletedBy, DateTime deletedAt, CancellationToken ct)
    {
        if (await VaultAsync(current.Path, ct) is not { } vault || !vault.Notes.TryGetValue(current.Path, out var entry)) return false;
        await vault.Gate.WaitAsync(ct);
        try
        {
            // Copy into the trash first, then delete only if nobody wrote in between.
            var trashed = new TrashedNote(current, deletedBy, deletedAt);
            var trashId = ObjectId.GenerateNewId();
            await catalog.Trash.InsertOneAsync(
                new SealedDocument { Id = trashId, Vault = vault.Id, PathId = vault.PathId(current.Path), DeletedAt = deletedAt, Blob = vault.SealTrash(trashId, trashed) },
                cancellationToken: ct);

            // The copy is committed: from here on, finish or undo it even if the request is cancelled.
            var deleted = await catalog.Notes.DeleteOneAsync(AtRevision(entry.Id, current.Revision), CancellationToken.None);
            if (deleted.DeletedCount == 0)
            {
                await catalog.Trash.DeleteOneAsync(F.Eq(d => d.Id, trashId), CancellationToken.None);
                await catalog.RefreshAsync(vault, current.Path, CancellationToken.None);
                return false;
            }

            vault.Notes = vault.Notes.Remove(current.Path);
            vault.Trash = vault.Trash.Add((trashId, trashed));
            return true;
        }
        finally
        {
            vault.Gate.Release();
        }
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
        await vault.Gate.WaitAsync(ct);
        try
        {
            var latest = vault.Trash
                .Where(t => t.Trashed.Note.Path == path && (version is null || t.Trashed.Note.Version == version))
                .OrderByDescending(t => t.Trashed.DeletedAt)
                .FirstOrDefault();
            if (latest.Trashed is null) return null;

            var note = latest.Trashed.Note;
            var restored = note with { Revision = note.Revision + 1, LastWriter = writer, LastWriteAt = at };
            if (!await InsertAsync(vault, restored, ct)) return null;

            // The note is back: remove it from the trash even if the request is cancelled now.
            await catalog.Trash.DeleteOneAsync(F.Eq(d => d.Id, latest.Id), CancellationToken.None);
            vault.Trash = vault.Trash.Remove(latest);
            return restored;
        }
        finally
        {
            vault.Gate.Release();
        }
    }

    /// <summary>Inserts a sealed note; false if one exists at that path. Call with the vault's gate held.</summary>
    private async Task<bool> InsertAsync(UnlockedVault vault, StoredNote note, CancellationToken ct)
    {
        var id = ObjectId.GenerateNewId();
        try
        {
            await catalog.Notes.InsertOneAsync(
                new SealedDocument { Id = id, Vault = vault.Id, PathId = vault.PathId(note.Path), Revision = note.Revision, Blob = vault.SealNote(id, note) },
                cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            await catalog.RefreshAsync(vault, note.Path, ct);
            return false;
        }

        vault.Notes = vault.Notes.SetItem(note.Path, (id, note));
        return true;
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
