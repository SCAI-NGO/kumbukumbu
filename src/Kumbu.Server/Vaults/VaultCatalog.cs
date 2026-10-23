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

    internal DateTime Now => time.GetUtcNow().UtcDateTime;

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
            if (_open.TryGetValue(slot.Vault, out var cached))
            {
                cached.Touch(Now);
                result.Add(cached);
                continue;
            }

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

        Sweep();
        return result;
    }

    /// <summary>Creates a vault with a slot for <paramref name="secret"/> and, if asked, a recovery key,
    /// which is returned once and stored nowhere.</summary>
    public async Task<(UnlockedVault Vault, string? RecoveryKey)> CreateAsync(string name, string secret, bool withRecovery, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var vaultKey = VaultCrypto.NewKey();
        var meta = VaultCrypto.Seal(VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), JsonSerializer.SerializeToUtf8Bytes(new VaultMeta(name)), $"vault:{id}");
        await Vaults.InsertOneAsync(new VaultDocument { Id = id, Meta = meta, CreatedAt = Now }, cancellationToken: ct);
        await AddKeySlotAsync(id, vaultKey, secret, ct);

        string? recovery = null;
        if (withRecovery)
        {
            var recoveryKey = RecoveryKey.New();
            await Slots.InsertOneAsync(new SlotDocument
            {
                Vault = id,
                Kind = SlotDocument.Recovery,
                Wrapped = VaultCrypto.Seal(RecoveryKek(recoveryKey, id), vaultKey, SlotContext(id, SlotDocument.Recovery)),
                CreatedAt = Now,
            }, cancellationToken: ct);
            recovery = RecoveryKey.Format(recoveryKey);
        }

        var vault = new UnlockedVault(id, name, vaultKey, Now);
        _open[id] = vault;
        return (vault, recovery);
    }

    /// <summary>Opens every vault <paramref name="recoveryKeyText"/> belongs to and gives <paramref name="secret"/>
    /// a slot there, if it has none. Returns the names of those vaults.</summary>
    public async Task<IReadOnlyList<string>> RecoverAsync(string recoveryKeyText, string secret, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        if (!RecoveryKey.TryParse(recoveryKeyText, out var recoveryKey)) return [];

        var lookup = VaultCrypto.Derive(secret, LookupLabel, []);
        var names = new List<string>();
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

            if (!await Slots.Find(Builders<SlotDocument>.Filter.Eq(s => s.Vault, slot.Vault) & Builders<SlotDocument>.Filter.Eq(s => s.Lookup, lookup)).AnyAsync(ct))
            {
                await AddKeySlotAsync(slot.Vault, vaultKey, secret, ct);
            }

            if (await LoadAsync(slot.Vault, vaultKey, ct) is { } vault) names.Add(vault.Name);
        }

        return names;
    }

    public async Task<long> CountVaultsAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        return await Vaults.CountDocumentsAsync(FilterDefinition<VaultDocument>.Empty, cancellationToken: ct);
    }

    /// <summary>Locks vaults idle for longer than the timeout, then the least recently used ones while the
    /// working sets exceed the memory cap. Keys are dropped with the vault, not zeroed: a request still using
    /// the vault must not see its key change under it.</summary>
    public void Sweep()
    {
        var o = options.Value.Vaults;
        var now = Now;
        foreach (var (id, vault) in _open)
        {
            if (now - vault.LastUsed > TimeSpan.FromMinutes(o.IdleMinutes) && _open.TryRemove(id, out _))
            {
                log.LogInformation("Locked vault {Vault} after {Minutes} idle minutes", vault.Name, o.IdleMinutes);
            }
        }

        var cap = o.MemoryLimitMegabytes * 1024L * 1024;
        foreach (var vault in _open.Values.OrderBy(v => v.LastUsed))
        {
            if (_open.Values.Sum(v => v.ApproxBytes) <= cap) break;
            if (_open.TryRemove(vault.Id, out _)) log.LogInformation("Locked vault {Vault} to stay below the memory limit", vault.Name);
        }
    }

    /// <summary>For tests: how many vaults are unlocked now.</summary>
    public int OpenCount => _open.Count;

    /// <summary>Reads one note again after a compare-and-swap failed: someone else wrote it.</summary>
    internal async Task RefreshAsync(UnlockedVault vault, VaultPath path, CancellationToken ct)
    {
        var doc = await Notes.Find(Builders<SealedDocument>.Filter.Eq(d => d.Vault, vault.Id) & Builders<SealedDocument>.Filter.Eq(d => d.PathId, vault.PathId(path))).FirstOrDefaultAsync(ct);
        vault.Notes = doc is null ? vault.Notes.Remove(path) : vault.Notes.SetItem(path, (doc.Id, vault.OpenNote(doc)));
    }

    private async Task<UnlockedVault?> LoadAsync(string id, byte[] vaultKey, CancellationToken ct)
    {
        await _loading.WaitAsync(ct);
        try
        {
            if (_open.TryGetValue(id, out var cached)) return cached;

            var doc = await Vaults.Find(v => v.Id == id).FirstOrDefaultAsync(ct);
            if (doc is null) return null;
            var meta = JsonSerializer.Deserialize<VaultMeta>(
                VaultCrypto.Open(VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), doc.Meta, $"vault:{id}"))!;

            var vault = new UnlockedVault(id, meta.Name, vaultKey, Now);
            var notes = ImmutableDictionary.CreateBuilder<VaultPath, (MongoDB.Bson.ObjectId, StoredNote)>();
            foreach (var d in await Notes.Find(n => n.Vault == id).ToListAsync(ct))
            {
                if (TryOpen(vault, () => vault.OpenNote(d), d) is { } note) notes[note.Path] = (d.Id, note);
            }

            vault.Notes = notes.ToImmutable();
            vault.Trash = [.. (await Trash.Find(n => n.Vault == id).ToListAsync(ct))
                .Select(d => (d.Id, Trashed: TryOpen(vault, () => vault.OpenTrash(d), d)))
                .Where(t => t.Trashed is not null)
                .Select(t => (t.Id, t.Trashed!))];
            vault.Moves = [.. (await Moves.Find(n => n.Vault == id).ToListAsync(ct))
                .Select(d => TryOpen(vault, () => vault.OpenMove(d), d))
                .OfType<MoveRecord>()];

            _open[id] = vault;
            log.LogInformation("Unlocked vault {Vault} ({Notes} notes)", vault.Name, vault.Notes.Count);
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

    /// <summary>Creates the indexes and refuses plaintext notes from before encryption, once per process.
    /// Lazy, so the server starts before MongoDB is reachable.</summary>
    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;

        var legacy = new MongoDB.Bson.BsonDocument("path", new MongoDB.Bson.BsonDocument("$exists", true));
        if (await db.GetCollection<MongoDB.Bson.BsonDocument>("notes").Find(legacy).AnyAsync(ct))
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
        await Slots.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<SlotDocument>(Builders<SlotDocument>.IndexKeys.Ascending(s => s.Lookup)),
                new CreateIndexModel<SlotDocument>(Builders<SlotDocument>.IndexKeys.Ascending(s => s.Vault)),
            ],
            ct);
        _ready = true;
    }
}
