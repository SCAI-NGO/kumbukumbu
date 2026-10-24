// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Kumbu.Server.Vaults;

/// <summary>
/// Vaults and their key slots (concept §7). Unlocks the vaults a secret has a slot for, keeps them as an
/// in-memory working set with an idle timeout and a memory cap, and creates new vaults. Vault keys exist
/// only here, in memory; MongoDB holds them only wrapped.
/// </summary>
public sealed class VaultCatalog(IMongoDatabase db, IOptions<KumbuOptions> options, TimeProvider time, ILogger<VaultCatalog> log)
{
    private const string LookupLabel = "kumbu/v1/key-lookup";
    private const string KeyKekLabel = "kumbu/v1/key-kek";
    private const string RecoveryKekLabel = "kumbu/v1/recovery-kek";

    private readonly ConcurrentDictionary<string, UnlockedVault> _open = new();
    private readonly SemaphoreSlim _loading = new(1, 1);
    private volatile bool _ready;

    internal IMongoCollection<VaultDocument> Vaults { get; } = db.GetCollection<VaultDocument>("vaults");

    internal IMongoCollection<SlotDocument> Slots { get; } = db.GetCollection<SlotDocument>("slots");

    internal IMongoCollection<SealedDocument> Notes { get; } = db.GetCollection<SealedDocument>("notes");

    internal IMongoCollection<SealedDocument> Trash { get; } = db.GetCollection<SealedDocument>("trash");

    internal IMongoCollection<SealedDocument> Moves { get; } = db.GetCollection<SealedDocument>("moves");

    /// <summary>The embeddings of each note, one document per note under its path id.</summary>
    internal IMongoCollection<SealedDocument> Embeddings { get; } = db.GetCollection<SealedDocument>("embeddings");

    /// <summary>Raised when a vault is unlocked (or created), so the search index can catch up with it.</summary>
    public event Action<UnlockedVault>? Unlocked;

    /// <summary>Named checkpoints: one document each, the name as a keyed hash in "pid".</summary>
    internal IMongoCollection<SealedDocument> Checkpoints { get; } = db.GetCollection<SealedDocument>("checkpoints");

    /// <summary>The content checkpoints hold, once per vault and text, the keyed hash of the content in "pid".</summary>
    internal IMongoCollection<SealedDocument> CheckpointTexts { get; } = db.GetCollection<SealedDocument>("checkpoint_texts");

    internal DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>A session for one transaction over several documents.</summary>
    internal Task<IClientSessionHandle> StartSessionAsync(CancellationToken ct) => db.Client.StartSessionAsync(cancellationToken: ct);

    /// <summary>The vaults <paramref name="secret"/> has a slot for, unlocked. A vault in the cache is used only
    /// for a secret whose slot was found here, so the cache saves work but never replaces authorization.</summary>
    public async Task<IReadOnlyList<UnlockedVault>> OpenAsync(string secret, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var lookup = VaultCrypto.Derive(secret, LookupLabel, []);
        var slots = await Slots.Find(Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.ApiKey) & Builders<SlotDocument>.Filter.Eq(s => s.Lookup, lookup)).ToListAsync(ct);

        var result = new List<UnlockedVault>();
        foreach (var slot in slots)
        {
            // Always open the slot, also for a vault already in memory: a slot that matches the lookup but
            // doesn't unwrap to that vault's key (inserted by someone with write access to the database) gets nothing.
            byte[] vaultKey;
            try
            {
                vaultKey = VaultCrypto.Open(KeyKek(secret, slot.Vault), slot.Wrapped, SlotContext(slot.Vault, slot.Kind));
            }
            catch (CryptographicException)
            {
                log.LogWarning("A key slot of vault {Vault} matched the credential but did not open", slot.Vault);
                continue;
            }

            if (await LoadAsync(slot.Vault, vaultKey, ct) is { } vault) result.Add(vault);
        }

        return result;
    }

    /// <summary>
    /// Creates a vault with a slot for <paramref name="secret"/>. With <paramref name="saveRecoveryKey"/> it also
    /// gets a recovery key, handed to that callback (vault id, key) <em>before</em> anything is stored: if saving
    /// fails, no vault exists, rather than a vault whose recovery key is lost.
    /// </summary>
    public async Task<UnlockedVault> CreateAsync(
        string name, string secret, Func<string, string, Task>? saveRecoveryKey, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var vaultKey = VaultCrypto.NewKey();
        var recoveryKey = saveRecoveryKey is null ? null : RecoveryKey.New();
        if (recoveryKey is not null) await saveRecoveryKey!(id, RecoveryKey.Format(recoveryKey));

        var meta = VaultCrypto.Seal(VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), JsonSerializer.SerializeToUtf8Bytes(new VaultMeta(name)), $"vault:{id}");
        await Vaults.InsertOneAsync(new VaultDocument { Id = id, Meta = meta, CreatedAt = Now }, cancellationToken: ct);
        await AddKeySlotAsync(id, vaultKey, secret, ct);
        if (recoveryKey is not null)
        {
            await Slots.InsertOneAsync(new SlotDocument
            {
                Vault = id,
                Kind = SlotDocument.Recovery,
                Wrapped = VaultCrypto.Seal(RecoveryKek(recoveryKey, id), vaultKey, SlotContext(id, SlotDocument.Recovery)),
                CreatedAt = Now,
            }, cancellationToken: ct);
        }

        var vault = new UnlockedVault(id, name, vaultKey, Now);
        _open[id] = vault;
        Unlocked?.Invoke(vault);
        return vault;
    }

    /// <summary>Opens every vault <paramref name="recoveryKeyText"/> belongs to and gives <paramref name="secret"/>
    /// a slot there, if it has none. Returns those vaults' names, and whether a slot was added.</summary>
    public async Task<IReadOnlyList<(string Name, bool Added)>> RecoverAsync(string recoveryKeyText, string secret, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        if (!RecoveryKey.TryParse(recoveryKeyText, out var recoveryKey)) return [];

        var lookup = VaultCrypto.Derive(secret, LookupLabel, []);
        var names = new List<(string, bool)>();
        foreach (var slot in await Slots.Find(s => s.Kind == SlotDocument.Recovery).ToListAsync(ct))
        {
            byte[] vaultKey;
            try
            {
                vaultKey = VaultCrypto.Open(RecoveryKek(recoveryKey, slot.Vault), slot.Wrapped, SlotContext(slot.Vault, slot.Kind));
            }
            catch (CryptographicException)
            {
                continue;
            }

            var added = !await Slots.Find(Builders<SlotDocument>.Filter.Eq(s => s.Vault, slot.Vault) & Builders<SlotDocument>.Filter.Eq(s => s.Lookup, lookup)).AnyAsync(ct);
            if (added) await AddKeySlotAsync(slot.Vault, vaultKey, secret, ct);

            if (await LoadAsync(slot.Vault, vaultKey, ct) is { } vault) names.Add((vault.Name, added));
        }

        return names;
    }

    /// <summary>Vaults <paramref name="secret"/> has no slot for.</summary>
    public async Task<int> CountLockedAsync(string secret, CancellationToken ct) =>
        (int)(await CountVaultsAsync(ct) - (await OpenAsync(secret, ct)).Select(v => v.Id).Distinct().Count());

    public async Task<long> CountVaultsAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        return await Vaults.CountDocumentsAsync(FilterDefinition<VaultDocument>.Empty, cancellationToken: ct);
    }

    /// <summary>A vault used this recently is never locked: a request may still be working with it.</summary>
    public static readonly TimeSpan InUse = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Runs once a minute (<see cref="VaultSweeper"/>). Locks vaults idle for longer than the timeout, then the
    /// least recently used ones while the working sets exceed the memory cap; a vault used within
    /// <see cref="InUse"/> stays, even above the cap. Also drops trash and move records MongoDB has expired.
    /// Keys go with the vault, not zeroed: a request still holding it must not see its key change.
    /// </summary>
    public void Sweep()
    {
        var o = options.Value.Vaults;
        var now = Now;
        foreach (var (id, vault) in _open)
        {
            if (now - vault.LastUsed > TimeSpan.FromMinutes(o.IdleMinutes) && now - vault.LastUsed > InUse && _open.TryRemove(id, out _))
            {
                log.LogInformation("Locked vault {Vault} after {Minutes} idle minutes", vault.Name, o.IdleMinutes);
            }
        }

        var cap = o.MemoryLimitMegabytes * 1024L * 1024;
        foreach (var vault in _open.Values.Where(v => now - v.LastUsed > InUse).OrderBy(v => v.LastUsed))
        {
            if (_open.Values.Sum(v => v.ApproxBytes) <= cap) break;
            if (_open.TryRemove(vault.Id, out _)) log.LogInformation("Locked vault {Vault} to stay below the memory limit", vault.Name);
        }

        foreach (var vault in _open.Values)
        {
            vault.Trash = vault.Trash.RemoveAll(t => now - t.Trashed.DeletedAt > NoteWriter.TrashRetention);
            vault.Moves = vault.Moves.RemoveAll(m => now - m.MovedAt > Kumbu.Core.Notes.Moves.Retention);
        }
    }

    /// <summary>For tests: how many vaults are unlocked now.</summary>
    public int OpenCount => _open.Count;

    /// <summary>Reads one note again after a compare-and-swap failed: someone else wrote it.</summary>
    internal async Task RefreshAsync(UnlockedVault vault, VaultPath path, CancellationToken ct)
    {
        var pathId = vault.PathId(path);
        var doc = await Notes.Find(Builders<SealedDocument>.Filter.Eq(d => d.Vault, vault.Id) & Builders<SealedDocument>.Filter.Eq(d => d.PathId, pathId)).FirstOrDefaultAsync(ct);
        var key = Convert.ToBase64String(pathId);
        if (doc is null)
        {
            vault.Notes = vault.Notes.Remove(path);
            vault.DamagedPathIds = vault.DamagedPathIds.Remove(key);
        }
        else if (TryOpen(vault, () => vault.OpenNote(doc), doc) is { } note)
        {
            vault.Notes = vault.Notes.SetItem(path, (doc.Id, note));
            vault.DamagedPathIds = vault.DamagedPathIds.Remove(key);
        }
        else
        {
            vault.Notes = vault.Notes.Remove(path);
            vault.DamagedPathIds = vault.DamagedPathIds.Add(key);
        }
    }

    private async Task<UnlockedVault?> LoadAsync(string id, byte[] vaultKey, CancellationToken ct)
    {
        await _loading.WaitAsync(ct);
        try
        {
            if (_open.TryGetValue(id, out var cached))
            {
                return CryptographicOperations.FixedTimeEquals(cached.KeyId, VaultCrypto.Derive(vaultKey, "kumbu/v1/key-id", [])) ? cached : null;
            }

            var doc = await Vaults.Find(v => v.Id == id).FirstOrDefaultAsync(ct);
            if (doc is null) return null;
            var meta = JsonSerializer.Deserialize<VaultMeta>(
                VaultCrypto.Open(VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), doc.Meta, $"vault:{id}"))!;

            var vault = new UnlockedVault(id, meta.Name, vaultKey, Now);
            var notes = ImmutableDictionary.CreateBuilder<VaultPath, (MongoDB.Bson.ObjectId, StoredNote)>();
            var damaged = ImmutableHashSet.CreateBuilder<string>();
            foreach (var d in await Notes.Find(n => n.Vault == id).ToListAsync(ct))
            {
                if (TryOpen(vault, () => vault.OpenNote(d), d) is { } note) notes[note.Path] = (d.Id, note);
                else damaged.Add(Convert.ToBase64String(d.PathId));
            }

            vault.Notes = notes.ToImmutable();
            vault.DamagedPathIds = damaged.ToImmutable();
            vault.Trash = [.. (await Trash.Find(n => n.Vault == id).ToListAsync(ct))
                .Select(d => (d.Id, Trashed: TryOpen(vault, () => vault.OpenTrash(d), d)))
                .Where(t => t.Trashed is not null)
                .Select(t => (t.Id, t.Trashed!))];
            vault.Moves = [.. (await Moves.Find(n => n.Vault == id).ToListAsync(ct))
                .Select(d => TryOpen(vault, () => vault.OpenMove(d), d))
                .OfType<MoveRecord>()];
            vault.Embeddings = (await Embeddings.Find(n => n.Vault == id).ToListAsync(ct))
                .Select(d => TryOpen(vault, () => (object)vault.OpenEmbeddings(d), d))
                .OfType<(VaultPath Path, NoteEmbeddings Embeddings)>()
                .ToImmutableDictionary(e => e.Path, e => e.Embeddings);
            vault.Checkpoints = [.. (await Checkpoints.Find(n => n.Vault == id).ToListAsync(ct))
                .Select(d => TryOpen(vault, () => vault.OpenCheckpoint(d), d))
                .OfType<StoredCheckpoint>()
                .OrderBy(c => c.Checkpoint.CreatedAt)];

            _open[id] = vault;
            log.LogInformation("Unlocked vault {Vault} ({Notes})", vault.Name, vault.Notes.Count == 1 ? "1 note" : $"{vault.Notes.Count} notes");
            Unlocked?.Invoke(vault);
            return vault;
        }
        finally
        {
            _loading.Release();
        }
    }

    private T? TryOpen<T>(UnlockedVault vault, Func<T> open, SealedDocument doc) where T : class
    {
        try
        {
            return open();
        }
        catch (Exception e) when (e is CryptographicException or JsonException or InvalidDataException)
        {
            log.LogError("Document {Id} of vault {Vault} does not open; it was changed or damaged outside Kumbu²", doc.Id, vault.Name);
            return null;
        }
    }

    private async Task AddKeySlotAsync(string vaultId, byte[] vaultKey, string secret, CancellationToken ct) =>
        await Slots.InsertOneAsync(new SlotDocument
        {
            Vault = vaultId,
            Kind = SlotDocument.ApiKey,
            Lookup = VaultCrypto.Derive(secret, LookupLabel, []),
            Wrapped = VaultCrypto.Seal(KeyKek(secret, vaultId), vaultKey, SlotContext(vaultId, SlotDocument.ApiKey)),
            CreatedAt = Now,
        }, cancellationToken: ct);

    private static byte[] KeyKek(string secret, string vaultId) => VaultCrypto.Derive(secret, KeyKekLabel, Encoding.UTF8.GetBytes(vaultId));

    private static byte[] RecoveryKek(byte[] recoveryKey, string vaultId) =>
        VaultCrypto.Derive(recoveryKey, RecoveryKekLabel, Encoding.UTF8.GetBytes(vaultId));

    private static string SlotContext(string vaultId, string kind) => $"slot:{vaultId}:{kind}";

    /// <summary>Creates the indexes, refuses a MongoDB without transactions and plaintext notes from before
    /// encryption, once per process. Lazy, so the server starts before MongoDB is reachable.</summary>
    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;

        // Changes to several notes (mv, replace) run as transactions, which MongoDB has only on a replica set.
        var hello = await db.Client.GetDatabase("admin").RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("hello", 1), cancellationToken: ct);
        if (!hello.Contains("setName"))
        {
            throw new VaultUnavailableException(
                "MongoDB runs standalone, but Kumbu² needs a replica set for transactions (a single node is enough). "
                + "Start it with '--replSet rs0' and initiate the set, as compose.yaml does.");
        }

        // Before step 5, notes and trash held a plaintext "path", with a unique index on it.
        var legacy = new MongoDB.Bson.BsonDocument("path", new MongoDB.Bson.BsonDocument("$exists", true));
        var oldIndexes = await (await db.GetCollection<MongoDB.Bson.BsonDocument>("notes").Indexes.ListAsync(ct)).ToListAsync(ct);
        if (await db.GetCollection<MongoDB.Bson.BsonDocument>("notes").Find(legacy).AnyAsync(ct)
            || await db.GetCollection<MongoDB.Bson.BsonDocument>("trash").Find(legacy).AnyAsync(ct)
            || oldIndexes.Any(i => i["key"].AsBsonDocument.Contains("path")))
        {
            throw new VaultUnavailableException(
                "The database holds plaintext notes from before encryption (step 5). Wipe it with 'docker compose down -v'; "
                + "the next start seeds the vaults again, encrypted.");
        }

        var sealedKeys = Builders<SealedDocument>.IndexKeys;
        await Notes.Indexes.CreateOneAsync(
            new CreateIndexModel<SealedDocument>(sealedKeys.Ascending(d => d.Vault).Ascending(d => d.PathId), new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
        await Trash.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<SealedDocument>(sealedKeys.Ascending(d => d.Vault).Ascending(d => d.PathId)),
                new CreateIndexModel<SealedDocument>(sealedKeys.Ascending(d => d.DeletedAt), new CreateIndexOptions { ExpireAfter = NoteWriter.TrashRetention }),
            ],
            ct);
        await Moves.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<SealedDocument>(sealedKeys.Ascending(d => d.Vault).Ascending(d => d.PathId)),
                new CreateIndexModel<SealedDocument>(sealedKeys.Ascending(d => d.At), new CreateIndexOptions { ExpireAfter = Kumbu.Core.Notes.Moves.Retention }),
            ],
            ct);
        await Embeddings.Indexes.CreateOneAsync(
            new CreateIndexModel<SealedDocument>(sealedKeys.Ascending(d => d.Vault).Ascending(d => d.PathId), new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
        await Checkpoints.Indexes.CreateOneAsync(
            new CreateIndexModel<SealedDocument>(sealedKeys.Ascending(d => d.Vault).Ascending(d => d.PathId), new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
        await CheckpointTexts.Indexes.CreateOneAsync(
            new CreateIndexModel<SealedDocument>(sealedKeys.Ascending(d => d.Vault).Ascending(d => d.PathId), new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
        await Slots.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<SlotDocument>(Builders<SlotDocument>.IndexKeys.Ascending(s => s.Lookup)),
                new CreateIndexModel<SlotDocument>(Builders<SlotDocument>.IndexKeys.Ascending(s => s.Vault)),
            ],
            ct);
        _ready = true;
    }
}
