// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Kumbu.Core.Crypto;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Vaults;

/// <summary>The write and indexing gates of one vault, shared by every <see cref="UnlockedVault"/> of its id.</summary>
public sealed class VaultLocks
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public SemaphoreSlim IndexGate { get; } = new(1, 1);
}

/// <summary>
/// What a re-keying run did: how many documents of the vault open under the new key, how many open under neither key
/// and were left as they are, and the API keys revoked and connections disconnected by its start (empty for a resumed run).
/// </summary>
public sealed record RekeySummary(long Documents, long Damaged, IReadOnlyList<string> RevokedApiKeys, IReadOnlyList<string> DisconnectedConnections);

/// <summary>
/// Stage 2 of re-keying (rekeying design §3): a vault gets a new vault key K′, and everything sealed under its key K is
/// sealed again under K′, in batches, with a two-key period between a start and a finish transaction. A crash at any
/// point leaves every document under exactly one of K and K′ (or damaged), and the next run resumes.
/// <para>
/// In this process nothing writes or indexes the vault while a run is under way: the run holds both of the vault's
/// gates (<see cref="LocksFor"/>) from its mark to its finish, every write and indexing checks
/// <see cref="CheckWritable"/> or <see cref="MayIndex"/> under them, and an object made with K is refused once K′ is
/// current. Against anything outside the gates, the writes filter on the <c>pid</c>, which a resealed document no
/// longer has under K.
/// </para>
/// </summary>
public sealed partial class VaultCatalog
{
    /// <summary>The order of the passes: checkpoint texts first, since checkpoints hold their ids.</summary>
    private static readonly string[] RekeyOrder = ["checkpoint_texts", "notes", "trash", "moves", "embeddings", "checkpoints"];

    /// <summary>How many documents are read at once in a pass; each read is cut into batches by <see cref="BatchBytes"/>.</summary>
    private const int RekeyReadChunk = 100;

    /// <summary>A pass reads a collection again after a batch failed; past this many times something keeps changing it.</summary>
    private const int MaxRekeyRestarts = 50;

    /// <summary>Passes repeat until one finds nothing to change: normally two.</summary>
    private const int MaxRekeyPasses = 10;

    private readonly ConcurrentDictionary<string, VaultLocks> _locks = new();

    /// <summary>The ids of the vaults a run holds now, from its mark to its end.</summary>
    private readonly ConcurrentDictionary<string, byte> _rekeying = new();

    /// <summary>Per vault, the key id of its current vault key, as last loaded, created or re-keyed in this process.</summary>
    private readonly ConcurrentDictionary<string, byte[]> _keyIds = new();

    /// <summary>
    /// The ids of the vaults whose two-key period may have begun in this process: marked under <c>_loading</c> before
    /// the start transaction, whatever its outcome, and cleared by a finish, by a start known to have failed, or by a
    /// load that finds the vault outside its period. Meanwhile an object made with the old key is refused, also after
    /// the run that began it stopped, so nothing is written under that key during the period.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _pending = new();

    /// <summary>The ids of the vaults re-keyed in this process, for the life of the process.</summary>
    private readonly ConcurrentDictionary<string, byte> _rekeyed = new();

    /// <summary>Raised when a user's request meets a vault in its two-key period (vault id, user id, user key), so that
    /// the runner resumes it.</summary>
    public event Action<string, string, byte[]>? RekeyPendingFound;

    /// <summary>For tests: awaited at <c>"after-start"</c>, <c>"after-batch"</c>, <c>"inside-finish"</c> and
    /// <c>"after-finish"</c>; a throw there is a crash at that point.</summary>
    internal Func<string, Task>? RekeyFaultAt { get; set; }

    /// <summary>For tests: called after each batch of a run commits, with the vault id, its documents and sealed bytes.</summary>
    internal Action<string, int, long>? RekeyBatchCommitted { get; set; }

    /// <summary>At most this many documents per batch transaction.</summary>
    internal int BatchDocuments { get; set; } = 500;

    /// <summary>At most this many sealed bytes per batch transaction; a larger document goes alone in its batch.</summary>
    internal long BatchBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>The gates of a vault: one pair per id for the life of the process, shared by every object of the vault.</summary>
    internal VaultLocks LocksFor(string vaultId) => _locks.GetOrAdd(vaultId, _ => new VaultLocks());

    /// <summary>Whether a run holds the vault now.</summary>
    internal bool IsRekeying(string vaultId) => _rekeying.ContainsKey(vaultId);

    /// <summary>Whether a run holds the vault, or its two-key period may have begun (<see cref="_pending"/>).</summary>
    private bool IsBusy(string vaultId) => _rekeying.ContainsKey(vaultId) || _pending.ContainsKey(vaultId);

    /// <summary>Whether the vault was re-keyed in this process.</summary>
    internal bool WasRekeyed(string vaultId) => _rekeyed.ContainsKey(vaultId);

    private void RecordKey(UnlockedVault vault) => _keyIds[vault.Id] = vault.KeyId;

    /// <summary>Made with the vault's current key. A vault never loaded in this process counts as current.</summary>
    private bool HasCurrentKey(UnlockedVault vault) =>
        !_keyIds.TryGetValue(vault.Id, out var current) || CryptographicOperations.FixedTimeEquals(current, vault.KeyId);

    /// <summary>
    /// Throws unless <paramref name="vault"/> may be written now: <see cref="VaultDeletedException"/> for a vault deleted,
    /// <see cref="VaultBusyException"/> for a placeholder or a vault a run holds ("being re-encrypted") and for an
    /// object made with a key that is no longer the vault's ("just re-encrypted"). Called before waiting for a gate,
    /// so that nothing queues behind a run, and again under it.
    /// </summary>
    internal void CheckWritable(UnlockedVault vault)
    {
        if (vault.Deleted || IsDeleted(vault.Id)) throw new VaultDeletedException();
        if (vault.Pending || IsBusy(vault.Id)) throw VaultBusyException.Being();
        if (!HasCurrentKey(vault)) throw VaultBusyException.Just();
    }

    /// <summary>The condition of <see cref="CheckWritable"/>, for indexing, which does nothing rather than throw.</summary>
    internal bool MayIndex(UnlockedVault vault) =>
        !vault.Pending && !vault.Deleted && !IsDeleted(vault.Id) && !IsBusy(vault.Id) && HasCurrentKey(vault);

    /// <summary>The user's vaults (by their user slots under <paramref name="userKey"/>) in their two-key period.</summary>
    internal async Task<IReadOnlyList<string>> PendingVaultIdsAsync(string userId, byte[] userKey, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var ids = await (await Slots.DistinctAsync(
            s => s.Vault, Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.User) & Builders<SlotDocument>.Filter.Eq(s => s.Lookup, UserLookup(userKey, userId)),
            cancellationToken: ct)).ToListAsync(ct);
        return await Vaults.Find(Builders<VaultDocument>.Filter.In(v => v.Id, ids) & Builders<VaultDocument>.Filter.Ne(v => v.RekeyStartedAt, null))
            .Project(v => v.Id).ToListAsync(ct);
    }

    /// <summary>Takes a vault that no longer exists out of the user's pending vaults; one that exists stays.</summary>
    internal async Task ForgetPendingAsync(string vaultId, string userId, CancellationToken ct)
    {
        if (await Vaults.Find(v => v.Id == vaultId).AnyAsync(ct)) return;
        await Users.UpdateOneAsync(u => u.Id == userId, Builders<Accounts.UserDocument>.Update.Pull(u => u.RekeyPending, vaultId), cancellationToken: ct);
    }

    /// <summary>What a run of the vault would take access from: the API keys with a slot on it, and the connections
    /// that list it and aren't revoked.</summary>
    internal async Task<(IReadOnlyList<string> ApiKeyIds, IReadOnlyList<string> ConnectionIds)> AccessAsync(string vaultId, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var keys = await (await Slots.DistinctAsync(
            s => s.KeyId,
            Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.ApiKey) & Builders<SlotDocument>.Filter.Eq(s => s.Vault, vaultId) & Builders<SlotDocument>.Filter.Exists(s => s.KeyId),
            cancellationToken: ct)).ToListAsync(ct);
        var connections = await db.GetCollection<Accounts.ConnectionDocument>(Accounts.Connections.CollectionName)
            .Find(Builders<Accounts.ConnectionDocument>.Filter.AnyEq(c => c.Vaults, vaultId) & Builders<Accounts.ConnectionDocument>.Filter.Eq(c => c.RevokedAt, null))
            .Project(c => c.Id).ToListAsync(ct);
        return ([.. keys.OfType<string>().Order(StringComparer.Ordinal)], [.. connections.Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Re-keys the vault, or resumes a run that stopped in its two-key period (rekeying design §3). Only with a user slot
    /// of <paramref name="userId"/> on the vault that opens with <paramref name="userKey"/>, the user key in force, else
    /// <see cref="VaultNotRekeyableException"/>. <see cref="VaultDeletedException"/> for a vault deleted, or being deleted
    /// (a deletion wins within one batch); <see cref="VaultBusyException"/> while another run holds it, or when the finish
    /// finds something changed: the vault then stays in its two-key period, for the next run to resume.
    /// </summary>
    internal async Task<RekeySummary> RekeyAsync(string vaultId, string userId, byte[] userKey, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        if (IsDeleted(vaultId)) throw new VaultDeletedException();
        if (!_rekeying.TryAdd(vaultId, 0)) throw VaultBusyException.Being();
        var locks = LocksFor(vaultId);
        var (gate, indexGate) = (false, false);
        try
        {
            // The mark is in place: an embedding call under way stops instead of holding the index gate for minutes.
            Interrupt(vaultId);
            await locks.Gate.WaitAsync(ct);
            gate = true;
            await locks.IndexGate.WaitAsync(ct);
            indexGate = true;
            if (IsDeleted(vaultId)) throw new VaultDeletedException();
            return await RunRekeyAsync(vaultId, userId, userKey, ct);
        }
        finally
        {
            // Out of the run before the gates open: a write waiting for them then checks the key id, not the mark.
            _rekeying.TryRemove(vaultId, out _);
            if (indexGate) locks.IndexGate.Release();
            if (gate) locks.Gate.Release();
        }
    }

    /// <summary>What the start transaction found or made.</summary>
    private sealed record RekeyStart(
        byte[] OldKey, byte[] NewKey, ObjectId UserSlotId, byte[] UserSlotWrapped, ObjectId NextSlotId, bool Resumed,
        IReadOnlyList<string> RevokedApiKeys, IReadOnlyList<string> Disconnected);

    private async Task<RekeySummary> RunRekeyAsync(string id, string userId, byte[] userKey, CancellationToken ct)
    {
        var started = Now;
        RekeyStart start;
        await _loading.WaitAsync(ct);
        try
        {
            // Before the transaction, so that neither a lost answer to its commit nor a later failure of the run leaves
            // the working set made with K served or an object made with K writing during the period.
            _open.TryRemove(id, out _);
            // Whether this start set the mark: one set before (a run that stopped in its two-key period) stays, also
            // when this start is refused, since the vault may still be in that period.
            var marked = _pending.TryAdd(id, 0);
            using var session = await StartSessionAsync(ct);
            try
            {
                start = await session.WithTransactionAsync((s, token) => StartRekeyAsync(s, id, userId, userKey, token), cancellationToken: ct);
            }
            catch (Exception e) when (e is VaultDeletedException or VaultNotRekeyableException or MongoWriteException)
            {
                // Refused inside the transaction, which was aborted: this start began nothing. A resume refused (a key
                // a rotation replaced, say) leaves the vault in its period, and the mark of the run that began it.
                if (marked) _pending.TryRemove(id, out _);
                throw;
            }
        }
        finally
        {
            _loading.Release();
        }

        foreach (var keyId in start.RevokedApiKeys) log.LogInformation("API key {KeyId} had a slot on vault {VaultId}, which is re-keyed; it is revoked", keyId, id);
        foreach (var connectionId in start.Disconnected) log.LogInformation("Connection {ConnectionId} lists vault {VaultId}, which is re-keyed; it needs a reconnect", connectionId, id);
        log.LogInformation(start.Resumed ? "Resuming the re-keying of vault {VaultId}" : "Re-keying vault {VaultId}", id);
        await FaultAsync("after-start");

        var locks = LocksFor(id);
        var old = new UnlockedVault(id, "", start.OldKey, Now, locks);
        var next = new UnlockedVault(id, "", start.NewKey, Now, locks);
        var (documents, damaged) = await RekeyPassesAsync(old, next, ct);

        await _loading.WaitAsync(ct);
        try
        {
            using (var session = await StartSessionAsync(ct))
            {
                await session.WithTransactionAsync((s, token) => FinishRekeyAsync(s, id, userId, userKey, start, old, next, token), cancellationToken: ct);
            }

            // Under _loading, so a load after this sees K′ current, and an object made with K is refused from now on.
            _keyIds[id] = next.KeyId;
            _rekeyed[id] = 0;
            _open.TryRemove(id, out _);
            _pending.TryRemove(id, out _);
        }
        finally
        {
            _loading.Release();
        }

        log.LogInformation(
            "Re-keyed vault {VaultId}: {Documents} documents under the new key, {Damaged} damaged, in {Seconds:0.0} s",
            id, documents, damaged, (Now - started).TotalSeconds);
        await FaultAsync("after-finish");
        return new RekeySummary(documents, damaged, start.RevokedApiKeys, start.Disconnected);
    }

    private Task FaultAsync(string point) => RekeyFaultAt?.Invoke(point) ?? Task.CompletedTask;

    /// <summary>
    /// The start transaction: a new key in a <c>user-next</c> slot, <c>rekeyStartedAt</c> on the vault, every API key
    /// with a slot on it revoked whole, every connection listing it disconnected (<see cref="Accounts.ConnectionReasons.Rekeyed"/>),
    /// and the vault pending for its owner. A vault that has a <c>user-next</c> slot is resumed with that slot's key.
    /// </summary>
    private async Task<RekeyStart> StartRekeyAsync(IClientSessionHandle s, string id, string userId, byte[] userKey, CancellationToken ct)
    {
        var f = Builders<SlotDocument>.Filter;
        var lookup = UserLookup(userKey, userId);
        var doc = await Vaults.Find(s, v => v.Id == id).FirstOrDefaultAsync(ct) ?? throw new VaultDeletedException();
        var userSlot = await Slots.Find(s, f.Eq(sl => sl.Vault, id) & f.Eq(sl => sl.Kind, SlotDocument.User) & f.Eq(sl => sl.Lookup, lookup)).FirstOrDefaultAsync(ct)
            ?? throw new VaultNotRekeyableException();
        var oldKey = OpenUserSlot(userKey, userSlot) ?? throw new VaultNotRekeyableException();

        // A real write to the owner's document, and only with the user key in force: a rotation of it or a creation
        // at the same time conflicts, and a key a rotation replaced can't start a run.
        var owner = await Users.UpdateOneAsync(
            s,
            Builders<Accounts.UserDocument>.Filter.Eq(u => u.Id, userId) & Builders<Accounts.UserDocument>.Filter.Eq(u => u.KeyId, Accounts.UserCatalog.KeyId(userKey, userId)),
            Builders<Accounts.UserDocument>.Update.AddToSet(u => u.RekeyPending, id).Inc(u => u.VaultsRekeyed, 1),
            cancellationToken: ct);
        if (owner.MatchedCount == 0) throw new VaultNotRekeyableException();

        if (await Slots.Find(s, f.Eq(sl => sl.Vault, id) & f.Eq(sl => sl.Kind, SlotDocument.UserNext)).FirstOrDefaultAsync(ct) is { } nextSlot)
        {
            if (nextSlot.Lookup is null || !CryptographicOperations.FixedTimeEquals(nextSlot.Lookup, lookup) || OpenUserSlot(userKey, nextSlot) is not { } resumedKey)
            {
                throw new VaultNotRekeyableException();
            }

            if (doc.RekeyStartedAt is null) await Vaults.UpdateOneAsync(s, v => v.Id == id, Builders<VaultDocument>.Update.Set(v => v.RekeyStartedAt, Now), cancellationToken: ct);
            return new RekeyStart(oldKey, resumedKey, userSlot.Id, userSlot.Wrapped, nextSlot.Id, Resumed: true, [], []);
        }

        var newKey = VaultCrypto.NewKey();
        var next = new SlotDocument
        {
            Vault = id,
            Kind = SlotDocument.UserNext,
            Lookup = lookup,
            Owner = userId,
            Wrapped = VaultCrypto.Seal(UserKek(userKey, id), newKey, SlotContext(id, SlotDocument.UserNext)),
            CreatedAt = Now,
        };
        await Slots.InsertOneAsync(s, next, cancellationToken: ct);
        await Vaults.UpdateOneAsync(s, v => v.Id == id, Builders<VaultDocument>.Update.Set(v => v.RekeyStartedAt, Now), cancellationToken: ct);

        // The server can't wrap K′ for an API key without the key (Ruling 20): each one with a slot here goes whole,
        // as ApiKeys.RevokeAsync revokes it, its name freed and its slots on every vault deleted.
        var keyIds = (await (await Slots.DistinctAsync(
            s, sl => sl.KeyId, f.Eq(sl => sl.Kind, SlotDocument.ApiKey) & f.Eq(sl => sl.Vault, id) & f.Exists(sl => sl.KeyId), cancellationToken: ct)).ToListAsync(ct))
            .OfType<string>().ToList();
        var keys = db.GetCollection<Accounts.ApiKeyDocument>(Accounts.ApiKeys.CollectionName);
        var revoked = keyIds.Count == 0 ? [] : await keys.Find(s, k => keyIds.Contains(k.Id) && k.RevokedAt == null).Project(k => k.Id).ToListAsync(ct);
        if (revoked.Count > 0)
        {
            await keys.UpdateManyAsync(
                s, Builders<Accounts.ApiKeyDocument>.Filter.In(k => k.Id, revoked), Builders<Accounts.ApiKeyDocument>.Update.Set(k => k.RevokedAt, Now).Unset(k => k.NameId), cancellationToken: ct);
        }

        if (keyIds.Count > 0) await Slots.DeleteManyAsync(s, f.Eq(sl => sl.Kind, SlotDocument.ApiKey) & f.In(sl => sl.KeyId, keyIds), cancellationToken: ct);

        // Its tokens become tombstones, so a revoked token stays recognised (Ruling 12); the app reconnects.
        var connections = db.GetCollection<Accounts.ConnectionDocument>(Accounts.Connections.CollectionName);
        var c = Builders<Accounts.ConnectionDocument>.Filter;
        var disconnected = await connections.Find(s, c.AnyEq(x => x.Vaults, id) & c.Eq(x => x.RevokedAt, null)).Project(x => x.Id).ToListAsync(ct);
        if (disconnected.Count > 0)
        {
            await Accounts.ConnectionRevocation.RevokeTokensAsync(db, s, c.In(x => x.Id, disconnected), Accounts.ConnectionReasons.Rekeyed, Now, ct);
            await connections.UpdateManyAsync(s, c.In(x => x.Id, disconnected), Builders<Accounts.ConnectionDocument>.Update.Inc(x => x.Wraps, 1), cancellationToken: ct);
        }

        // Nothing but the owner's slots may wrap K from here on.
        var others = await Slots.DeleteManyAsync(s, f.Eq(sl => sl.Vault, id) & f.Nin(sl => sl.Kind, [SlotDocument.User, SlotDocument.UserNext]), cancellationToken: ct);
        if (others.DeletedCount > 0) log.LogWarning("Vault {VaultId} had {Count} more slots besides its owner's; they were deleted with its re-keying", id, others.DeletedCount);

        return new RekeyStart(oldKey, newKey, userSlot.Id, userSlot.Wrapped, next.Id, Resumed: false, [.. revoked.Order(StringComparer.Ordinal)], [.. disconnected.Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The finish transaction: the user slot wraps K′ (if it still wraps what the run started from), the <c>user-next</c>
    /// slot goes, the vault's meta is sealed under K′ without <c>rekeyStartedAt</c>, and the vault is no longer pending.
    /// The three slot and vault writes must each match exactly one document: otherwise the transaction aborts and the
    /// vault stays in its two-key period.
    /// </summary>
    private async Task<bool> FinishRekeyAsync(
        IClientSessionHandle s, string id, string userId, byte[] userKey, RekeyStart start, UnlockedVault old, UnlockedVault next, CancellationToken ct)
    {
        var doc = await Vaults.Find(s, v => v.Id == id).FirstOrDefaultAsync(ct) ?? throw new VaultDeletedException();
        var f = Builders<SlotDocument>.Filter;
        // Still exactly what the run started from: a rotation of the user key meanwhile, say, wrapped it again.
        var slot = await Slots.UpdateOneAsync(
            s,
            f.Eq(sl => sl.Id, start.UserSlotId) & f.Eq(sl => sl.Kind, SlotDocument.User) & f.Eq(sl => sl.Wrapped, start.UserSlotWrapped),
            Builders<SlotDocument>.Update.Set(sl => sl.Wrapped, VaultCrypto.Seal(UserKek(userKey, id), start.NewKey, SlotContext(id, SlotDocument.User))),
            cancellationToken: ct);
        if (slot.MatchedCount != 1) throw Mismatch("the user slot");
        var nextSlot = await Slots.DeleteOneAsync(s, f.Eq(sl => sl.Id, start.NextSlotId) & f.Eq(sl => sl.Kind, SlotDocument.UserNext), cancellationToken: ct);
        if (nextSlot.DeletedCount != 1) throw Mismatch("the slot of the new key");

        VaultMeta meta;
        try
        {
            meta = JsonSerializer.Deserialize<VaultMeta>(VaultCrypto.Open(VaultCrypto.Derive(start.OldKey, "kumbu/v1/content", []), doc.Meta, $"vault:{id}"))!;
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            throw Mismatch("the vault's meta");
        }

        var vault = await Vaults.UpdateOneAsync(
            s,
            Builders<VaultDocument>.Filter.Eq(v => v.Id, id) & Builders<VaultDocument>.Filter.Eq(v => v.Meta, doc.Meta),
            Builders<VaultDocument>.Update
                .Set(v => v.Meta, VaultCrypto.Seal(VaultCrypto.Derive(start.NewKey, "kumbu/v1/content", []), JsonSerializer.SerializeToUtf8Bytes(meta), $"vault:{id}"))
                .Unset(v => v.RekeyStartedAt),
            cancellationToken: ct);
        if (vault.MatchedCount != 1) throw Mismatch("the vault document");

        await Users.UpdateOneAsync(
            s,
            Builders<Accounts.UserDocument>.Filter.Eq(u => u.Id, userId),
            Builders<Accounts.UserDocument>.Update.Pull(u => u.RekeyPending, id).Inc(u => u.VaultsRekeyed, 1),
            cancellationToken: ct);
        // Codes and tokens issued meanwhile (there should be none) conflict with this, and are refused when retried.
        var connections = db.GetCollection<Accounts.ConnectionDocument>(Accounts.Connections.CollectionName);
        await connections.UpdateManyAsync(
            s, Builders<Accounts.ConnectionDocument>.Filter.AnyEq(c => c.Vaults, id), Builders<Accounts.ConnectionDocument>.Update.Inc(c => c.Wraps, 1), cancellationToken: ct);
        await FaultAsync("inside-finish");
        return true;

        VaultBusyException Mismatch(string what)
        {
            log.LogWarning("The finish of the re-keying of vault {VaultId} found {What} changed; the vault stays pending", id, what);
            return VaultBusyException.Being();
        }
    }

    /// <summary>
    /// Passes over every collection of the vault, until one finds nothing to change. Returns how many documents open
    /// under K′ and how many are damaged, as the last pass found them.
    /// </summary>
    private async Task<(long Documents, long Damaged)> RekeyPassesAsync(UnlockedVault old, UnlockedVault next, CancellationToken ct)
    {
        var textIds = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var logged = new HashSet<ObjectId>();
        for (var pass = 0; pass < MaxRekeyPasses; pass++)
        {
            var (changed, documents, damaged) = (0L, 0L, 0L);
            foreach (var name in RekeyOrder)
            {
                var result = await RekeyCollectionAsync(name, old, next, textIds, logged, ct);
                (changed, documents, damaged) = (changed + result.Changed, documents + result.Current, damaged + result.Damaged);
            }

            if (changed == 0) return (documents, damaged);
        }

        throw VaultBusyException.Being();
    }

    private IMongoCollection<SealedDocument> RekeyCollection(string name) => name switch
    {
        "notes" => Notes,
        "trash" => Trash,
        "moves" => Moves,
        "embeddings" => Embeddings,
        "checkpoints" => Checkpoints,
        _ => CheckpointTexts,
    };

    /// <summary>
    /// One pass over one collection. Each document is classified by its seal first and its pid second: under K′ with
    /// the right pid it stays; under K′ with another pid, or under K, it is sealed again under K′ with the pid computed
    /// from what it holds; under neither it is damaged, left and counted. The changes go in batches of at most
    /// <see cref="BatchDocuments"/> documents and <see cref="BatchBytes"/> sealed bytes, each a transaction whose writes
    /// are conditional on the blob that was read; if any of them fails, the collection is read again.
    /// </summary>
    private async Task<(long Changed, long Current, long Damaged)> RekeyCollectionAsync(
        string name, UnlockedVault old, UnlockedVault next, Dictionary<string, byte[]> textIds, HashSet<ObjectId> logged, CancellationToken ct)
    {
        var collection = RekeyCollection(name);
        var id = old.Id;
        long changed = 0;
        for (var attempt = 0; attempt <= MaxRekeyRestarts; attempt++)
        {
            var (current, damaged) = (0L, 0L);
            var batch = new List<(SealedDocument Doc, UnlockedVault.Resealed New)>();
            var batchPids = new HashSet<string>(StringComparer.Ordinal);
            long batchBytes = 0;
            var failed = false;

            // Only the ids first: documents move in the (vault, pid) index as they are resealed, never in _id's.
            var ids = await collection.Find(Builders<SealedDocument>.Filter.Eq(d => d.Vault, id)).Project(d => d.Id).ToListAsync(ct);
            ids.Sort();
            foreach (var chunk in ids.Chunk(RekeyReadChunk))
            {
                // Also in a pass that changes nothing: a deletion waits for one read at most.
                if (IsDeleted(id)) throw new VaultDeletedException();
                var docs = await collection.Find(Builders<SealedDocument>.Filter.In(d => d.Id, chunk)).SortBy(d => d.Id).ToListAsync(ct);
                foreach (var doc in docs)
                {
                    var resealed = Classify(name, doc, old, next, textIds, logged);
                    if (resealed is null)
                    {
                        damaged++;
                        continue;
                    }

                    if (resealed.Value.Unchanged)
                    {
                        current++;
                        continue;
                    }

                    var change = resealed.Value.New!;
                    var pid = Convert.ToBase64String(change.PathId);
                    // Two documents of one batch that claim one pid: the second waits for the next batch, where the
                    // unique index tells which one stays.
                    if (batch.Count > 0 && (batch.Count >= BatchDocuments || batchBytes + change.Blob.Length > BatchBytes || batchPids.Contains(pid)))
                    {
                        if (!await CommitRekeyBatchAsync(name, collection, batch, batchBytes, old, next, ct)) { failed = true; break; }
                        changed += batch.Count;
                        (batch, batchPids, batchBytes) = ([], new HashSet<string>(StringComparer.Ordinal), 0);
                    }

                    batch.Add((doc, change));
                    batchPids.Add(pid);
                    batchBytes += change.Blob.Length;
                }

                if (failed) break;
            }

            if (!failed && batch.Count > 0)
            {
                failed = !await CommitRekeyBatchAsync(name, collection, batch, batchBytes, old, next, ct);
                if (!failed) changed += batch.Count;
            }

            if (!failed) return (changed, current, damaged);
            changed++;
        }

        throw VaultBusyException.Being();
    }

    /// <summary>A document classified: null if damaged, else whether it is current under K′, or what to write.</summary>
    private (bool Unchanged, UnlockedVault.Resealed? New)? Classify(
        string name, SealedDocument doc, UnlockedVault old, UnlockedVault next, Dictionary<string, byte[]> textIds, HashSet<ObjectId> logged)
    {
        // Opened and its pid computed, but not sealed: in a confirming pass nearly every document is current.
        if (next.Reseal(name, doc, next, textIds, seal: false) is { } current)
        {
            if (current.Content is { } text) textIds[Convert.ToBase64String(old.TextId(text))] = next.TextId(text);
            if (CryptographicOperations.FixedTimeEquals(current.PathId, doc.PathId)) return (true, null);
            log.LogWarning("Document {Id} of vault {VaultId} ({Collection}) is under the new key with another pid; its pid is corrected", doc.Id, doc.Vault, name);
            return (false, next.Reseal(name, doc, next, textIds));
        }

        if (old.Reseal(name, doc, next, textIds) is not { } resealed)
        {
            if (logged.Add(doc.Id)) log.LogError("Document {Id} of vault {VaultId} ({Collection}) opens under neither key; it is left as it is", doc.Id, doc.Vault, name);
            return null;
        }

        if (resealed.Content is { } content) textIds[Convert.ToBase64String(old.TextId(content))] = next.TextId(content);
        if (!CryptographicOperations.FixedTimeEquals(resealed.FromPathId, doc.PathId) && logged.Add(doc.Id))
        {
            log.LogWarning("Document {Id} of vault {VaultId} ({Collection}) had a pid that doesn't match what it holds; it gets the one that does", doc.Id, doc.Vault, name);
        }

        if (resealed.Unmapped.Count > 0 && logged.Add(doc.Id))
        {
            log.LogWarning("Checkpoint {Id} of vault {VaultId} holds {Count} texts that are missing or damaged; they stay so", doc.Id, doc.Vault, resealed.Unmapped.Count);
        }

        return (false, resealed);
    }

    /// <summary>
    /// One batch, one transaction: each document replaced only if it still has the blob that was read. False if
    /// anything failed (nothing of the batch is then written): the caller reads the collection again. A write that
    /// would give two documents one pid keeps the one with the higher revision (for checkpoints the later one, for
    /// texts either) and deletes the other, after logging it.
    /// </summary>
    private async Task<bool> CommitRekeyBatchAsync(
        string name, IMongoCollection<SealedDocument> collection, List<(SealedDocument Doc, UnlockedVault.Resealed New)> batch, long bytes,
        UnlockedVault old, UnlockedVault next, CancellationToken ct)
    {
        var vaultId = batch[0].Doc.Vault;
        // Between batches: a deletion of the vault waits for this run at most one batch.
        if (IsDeleted(vaultId)) throw new VaultDeletedException();
        using var session = await StartSessionAsync(ct);
        session.StartTransaction();
        (SealedDocument Doc, UnlockedVault.Resealed New)? duplicate = null;
        try
        {
            foreach (var (doc, change) in batch)
            {
                UpdateResult result;
                try
                {
                    result = await collection.UpdateOneAsync(
                        session,
                        Builders<SealedDocument>.Filter.Eq(d => d.Id, doc.Id) & Builders<SealedDocument>.Filter.Eq(d => d.Blob, doc.Blob),
                        Builders<SealedDocument>.Update.Set(d => d.PathId, change.PathId).Set(d => d.Blob, change.Blob),
                        cancellationToken: ct);
                }
                catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
                {
                    duplicate = (doc, change);
                    break;
                }
                catch (MongoCommandException e) when (e.Code == 11000)
                {
                    duplicate = (doc, change);
                    break;
                }

                if (result.MatchedCount == 0)
                {
                    await AbortAsync(session);
                    return false;
                }
            }

            if (duplicate is not null)
            {
                await AbortAsync(session);
                await KeepOneAsync(name, collection, duplicate.Value.Doc, duplicate.Value.New, old, next, ct);
                return false;
            }

            await session.CommitTransactionAsync(ct);
        }
        catch (MongoException e) when (e.HasErrorLabel("TransientTransactionError") || e.HasErrorLabel("UnknownTransactionCommitResult"))
        {
            // Someone else wrote meanwhile, or the commit's outcome is unknown: the pass reads again and finds out.
            await AbortAsync(session);
            return false;
        }
        catch
        {
            await AbortAsync(session);
            throw;
        }

        RekeyBatchCommitted?.Invoke(vaultId, batch.Count, bytes);
        await FaultAsync("after-batch");
        return true;
    }

    /// <summary>
    /// Of two documents that claim one pid, deletes the one with the lower revision (checkpoints: the earlier). The
    /// one being resealed opened; a holder that opens under neither key never wins over it (it may be a forgery with
    /// the new key's pid, made to push a note out): it is deleted, and logged as damaged.
    /// </summary>
    private async Task KeepOneAsync(
        string name, IMongoCollection<SealedDocument> collection, SealedDocument doc, UnlockedVault.Resealed change, UnlockedVault old, UnlockedVault next, CancellationToken ct)
    {
        var f = Builders<SealedDocument>.Filter;
        var holder = await collection.Find(f.Eq(d => d.Vault, doc.Vault) & f.Eq(d => d.PathId, change.PathId) & f.Ne(d => d.Id, doc.Id)).FirstOrDefaultAsync(ct);
        if (holder is null) return;
        var none = new Dictionary<string, byte[]>();
        if (next.Reseal(name, holder, next, none, seal: false) is null && old.Reseal(name, holder, next, none, seal: false) is null)
        {
            log.LogError(
                "Document {Lost} of vault {VaultId} ({Collection}) opens under neither key and claims the pid of {Kept}, which does; it is deleted",
                holder.Id, doc.Vault, name, doc.Id);
            await collection.DeleteOneAsync(f.Eq(d => d.Id, holder.Id) & f.Eq(d => d.Blob, holder.Blob), ct);
            return;
        }

        var keepDoc = name switch
        {
            "checkpoints" => doc.At > holder.At,
            "checkpoint_texts" => false,
            _ => doc.Revision > holder.Revision,
        };
        var (kept, lost) = keepDoc ? (doc, holder) : (holder, doc);
        log.LogWarning("Documents {Kept} and {Lost} of vault {VaultId} ({Collection}) claim one pid; the second is deleted", kept.Id, lost.Id, doc.Vault, name);
        await collection.DeleteOneAsync(f.Eq(d => d.Id, lost.Id) & f.Eq(d => d.Blob, lost.Blob), ct);
    }

    private static async Task AbortAsync(IClientSessionHandle session)
    {
        try
        {
            if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None);
        }
        catch (MongoException)
        {
            // The server already aborted it.
        }
    }

    /// <summary>The vault key in a slot under the user key (<c>user</c> or <c>user-next</c>), or null if it doesn't open.</summary>
    private static byte[]? OpenUserSlot(byte[] userKey, SlotDocument slot)
    {
        try
        {
            return VaultCrypto.Open(UserKek(userKey, slot.Vault), slot.Wrapped, SlotContext(slot.Vault, slot.Kind));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
