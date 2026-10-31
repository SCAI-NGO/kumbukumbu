// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Metrics;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Kumbu.Server.Vaults;

/// <summary>
/// Vaults and their key slots (concept §7). Unlocks the vaults a secret has a slot for, keeps them as an
/// in-memory working set with an idle timeout and a memory cap, and creates new vaults. Vault keys exist
/// only here, in memory; MongoDB holds them only wrapped.
/// </summary>
public sealed partial class VaultCatalog(IMongoDatabase db, IOptions<KumbuOptions> options, TimeProvider time, ILogger<VaultCatalog> log)
{
    private const string LookupLabel = "kumbu/v1/key-lookup";
    private const string KeyKekLabel = "kumbu/v1/key-kek";
    private const string RecoveryKekLabel = "kumbu/v1/recovery-kek";
    private const string UserLookupLabel = "kumbu/v1/user-slot";
    private const string UserKekLabel = "kumbu/v1/user-slot-kek";
    private const string VaultNameLabel = "kumbu/v1/vault-name-id";
    private const string ApiKeyKekLabel = "kumbu/v1/api-key-kek";
    private const string ConnectionKekLabel = "kumbu/v1/oauth-kek";

    private readonly ConcurrentDictionary<string, UnlockedVault> _open = new();
    private readonly SemaphoreSlim _loading = new(1, 1);

    /// <summary>The attachments' blobs read and decrypted at once, server-wide (<see cref="VaultOptions.ConcurrentFileReads"/>):
    /// a slot from before a blob is read until its bytes are open.</summary>
    internal SemaphoreSlim FileReads { get; } = new(Math.Max(1, options.Value.Vaults.ConcurrentFileReads));

    /// <summary>Tombstones: the ids of vaults deleted in this process (<see cref="DeleteVaultsAsync"/>). Random ids,
    /// one entry per deleted vault, kept for the life of the process.</summary>
    private readonly ConcurrentDictionary<string, byte> _deleted = new();

    /// <summary>Per vault, what cancels its embedding calls when it is deleted or re-keyed (<see cref="InterruptToken"/>).</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _interrupts = new();
    private volatile bool _ready;

    internal IMongoCollection<VaultDocument> Vaults { get; } = db.GetCollection<VaultDocument>("vaults");

    private IMongoCollection<Accounts.UserDocument> Users { get; } = db.GetCollection<Accounts.UserDocument>("users");

    internal IMongoCollection<SlotDocument> Slots { get; } = db.GetCollection<SlotDocument>("slots");

    internal IMongoCollection<SealedDocument> Notes { get; } = db.GetCollection<SealedDocument>("notes");

    internal IMongoCollection<SealedDocument> Trash { get; } = db.GetCollection<SealedDocument>("trash");

    internal IMongoCollection<SealedDocument> Moves { get; } = db.GetCollection<SealedDocument>("moves");

    /// <summary>The embeddings of each note, one document per note under its path id.</summary>
    internal IMongoCollection<SealedDocument> Embeddings { get; } = db.GetCollection<SealedDocument>("embeddings");

    /// <summary>Raised when a vault is unlocked (or created), so the search index can catch up with it.</summary>
    public event Action<UnlockedVault>? Unlocked;

    /// <summary>
    /// Raised with a vault's id after every committed change to its notes or trash, and when it is deleted: what the
    /// sync hints are made of. Handlers run on the writer's thread, under the vault's gate: they must only enqueue.
    /// </summary>
    public event Action<string>? NotesChanged;

    /// <summary>A handler that throws is logged; the write it reports has committed and stands.</summary>
    internal void RaiseNotesChanged(string vaultId)
    {
        if (NotesChanged is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(vaultId);
            }
            catch (Exception e)
            {
                log.LogError(e, "A handler of NotesChanged failed for vault {VaultId}", vaultId);
            }
        }
    }

    /// <summary>Named checkpoints: one document each, the name as a keyed hash in "pid".</summary>
    internal IMongoCollection<SealedDocument> Checkpoints { get; } = db.GetCollection<SealedDocument>("checkpoints");

    /// <summary>The content checkpoints hold, once per vault and text, the keyed hash of the content in "pid".</summary>
    internal IMongoCollection<SealedDocument> CheckpointTexts { get; } = db.GetCollection<SealedDocument>("checkpoint_texts");

    /// <summary>The attachments' records, one per path, as notes are: loaded with the vault.</summary>
    internal IMongoCollection<SealedDocument> Attachments { get; } = db.GetCollection<SealedDocument>("attachments");

    /// <summary>Trashed attachments. No TTL index: <see cref="BlobSweep"/> expires them, so it can release their blobs.</summary>
    internal IMongoCollection<SealedDocument> AttachmentTrash { get; } = db.GetCollection<SealedDocument>("attachment_trash");

    /// <summary>The attachments' bytes, once per vault and content; never loaded with the vault, read on demand only.</summary>
    internal IMongoCollection<BlobDocument> Blobs { get; } = db.GetCollection<BlobDocument>("blobs");

    /// <summary><see cref="Blobs"/> for what reads only the <c>vault</c> field (<see cref="ContentCollections"/>).</summary>
    private IMongoCollection<SealedDocument> BlobsByVault { get; } = db.GetCollection<SealedDocument>("blobs");

    /// <summary>The analyses of PDFs, in parts under their blob's id (<see cref="Attachments.PdfAnalyses"/>); they go with the blob.</summary>
    internal IMongoCollection<PdfAnalysisDocument> PdfParts { get; } = db.GetCollection<PdfAnalysisDocument>("pdf_analyses");

    /// <summary>The PDFs' blobs waiting for an analysis (<see cref="Attachments.PdfAnalysisRunner"/>).</summary>
    internal IMongoCollection<PdfJobDocument> PdfJobs { get; } = db.GetCollection<PdfJobDocument>("pdf_jobs");

    /// <summary><see cref="PdfParts"/> and <see cref="PdfJobs"/> for what reads only the <c>vault</c> field (<see cref="ContentCollections"/>).</summary>
    private IMongoCollection<SealedDocument> PdfPartsByVault { get; } = db.GetCollection<SealedDocument>("pdf_analyses");

    private IMongoCollection<SealedDocument> PdfJobsByVault { get; } = db.GetCollection<SealedDocument>("pdf_jobs");

    internal DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>A session for one transaction over several documents.</summary>
    internal Task<IClientSessionHandle> StartSessionAsync(CancellationToken ct) => db.Client.StartSessionAsync(cancellationToken: ct);

    /// <summary>A session whose reads all see one point in time (snapshot read concern), for reads of several collections
    /// that must agree with each other.</summary>
    internal Task<IClientSessionHandle> StartSnapshotSessionAsync(CancellationToken ct) =>
        db.Client.StartSessionAsync(new ClientSessionOptions { Snapshot = true }, ct);

    /// <summary>The vaults <paramref name="secret"/> has a slot for, unlocked. A vault in the cache is used only
    /// for a secret whose slot was found here, so the cache saves work but never replaces authorization.</summary>
    public async Task<IReadOnlyList<UnlockedVault>> OpenAsync(string secret, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var lookup = VaultCrypto.Derive(secret, LookupLabel, []);
        var slots = await Slots.Find(Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.ApiKey)
            & Builders<SlotDocument>.Filter.Eq(s => s.Lookup, lookup) & Builders<SlotDocument>.Filter.Exists(s => s.KeyId, false)).ToListAsync(ct);

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
                log.LogWarning("A key slot of vault {VaultId} matched the credential but did not open", slot.Vault);
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

        var vault = new UnlockedVault(id, name, vaultKey, Now, LocksFor(id)) { EmbeddingProvider = DefaultEmbeddingProvider };
        RecordKey(vault);
        _open[id] = vault;
        KumbuMetrics.VaultUnlocked();
        Unlocked?.Invoke(vault);
        return vault;
    }

    /// <summary>The vaults of a user, unlocked with their user key. Opens slots as <see cref="OpenAsync"/> does.</summary>
    public async Task<IReadOnlyList<UnlockedVault>> OpenForUserAsync(string userId, byte[] userKey, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var lookup = UserLookup(userKey, userId);
        var slots = await Slots.Find(Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.User) & Builders<SlotDocument>.Filter.Eq(s => s.Lookup, lookup)).ToListAsync(ct);

        var result = new List<UnlockedVault>();
        foreach (var slot in slots)
        {
            if (OpenUserSlot(slot) is not { } vaultKey) continue;
            UnlockedVault? vault;
            try
            {
                vault = await LoadAsync(slot.Vault, vaultKey, ct);
            }
            catch (VaultBusyException)
            {
                // A finish committed between the slot read and the load: the slot read gave the old key, the vault is
                // now under the new one. Its slot, read again, wraps the new key. Once only: a second failure is real.
                var again = await Slots.Find(Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.User)
                    & Builders<SlotDocument>.Filter.Eq(s => s.Lookup, lookup) & Builders<SlotDocument>.Filter.Eq(s => s.Vault, slot.Vault)).FirstOrDefaultAsync(ct);
                if (again is null || OpenUserSlot(again) is not { } newKey) continue;
                vault = await LoadAsync(slot.Vault, newKey, ct);
            }

            if (vault is null) continue;
            result.Add(vault);
            // Only a session meets a placeholder: the start of a run deleted every API key and connection slot.
            if (vault.Pending) RekeyPendingFound?.Invoke(vault.Id, userId, userKey);
        }

        return result;

        byte[]? OpenUserSlot(SlotDocument slot)
        {
            try
            {
                return VaultCrypto.Open(UserKek(userKey, slot.Vault), slot.Wrapped, SlotContext(slot.Vault, slot.Kind));
            }
            catch (CryptographicException)
            {
                log.LogWarning("A user slot of vault {VaultId} matched the user key but did not open", slot.Vault);
                return null;
            }
        }
    }

    /// <summary>
    /// Creates a vault owned by a user: its key wrapped by <paramref name="userKey"/> and nothing else, so the
    /// user's recovery key reaches it through the user key. Throws <see cref="VaultNameTakenException"/> if the
    /// user has a vault of that name (compared case-insensitively), also when another create won a race, and
    /// <see cref="Accounts.CredentialsChangedException"/> if the user no longer exists or is inactive, or, with
    /// <paramref name="credentialReadAt"/> (when the session's password was read), if the user's credentials changed at
    /// or after it: a rotation of the user key (<see cref="Accounts.UserKeyRotation"/>) may have made
    /// <paramref name="userKey"/> one that no slot uses any more, and a vault under it would be lost.
    /// </summary>
    public async Task<UnlockedVault> CreateForUserAsync(string name, string userId, byte[] userKey, CancellationToken ct, DateTime? credentialReadAt = null)
    {
        await EnsureReadyAsync(ct);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var vaultKey = VaultCrypto.NewKey();

        // The slot goes first: its unique index is the arbiter of the name.
        var slot = new SlotDocument
        {
            Vault = id,
            Kind = SlotDocument.User,
            Lookup = UserLookup(userKey, userId),
            Owner = userId,
            NameId = VaultCrypto.Mac(VaultCrypto.Derive(userKey, VaultNameLabel, []), NormaliseVaultName(name)),
            Wrapped = VaultCrypto.Seal(UserKek(userKey, id), vaultKey, SlotContext(id, SlotDocument.User)),
            CreatedAt = Now,
        };
        try
        {
            await Slots.InsertOneAsync(slot, cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // A slot whose vault document doesn't exist is an orphan of a create that failed half way: replace it.
            // Only an old one: a young one may belong to a create that is still between its two inserts.
            var holder = await Slots.Find(Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.User)
                & Builders<SlotDocument>.Filter.Eq(s => s.Lookup, slot.Lookup) & Builders<SlotDocument>.Filter.Eq(s => s.NameId, slot.NameId)).FirstOrDefaultAsync(ct);
            if (holder is null || Now - holder.CreatedAt < OrphanGrace || await Vaults.Find(v => v.Id == holder.Vault).AnyAsync(ct)) throw new VaultNameTakenException(name);

            await Slots.DeleteOneAsync(s => s.Id == holder.Id, ct);
            try
            {
                await Slots.InsertOneAsync(slot, cancellationToken: ct);
            }
            catch (MongoWriteException e2) when (e2.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new VaultNameTakenException(name);
            }
        }

        try
        {
            var meta = VaultCrypto.Seal(VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), JsonSerializer.SerializeToUtf8Bytes(new VaultMeta(name)), $"vault:{id}");
            var doc = new VaultDocument { Id = id, Meta = meta, CreatedAt = Now };
            using var session = await StartSessionAsync(ct);
            await session.WithTransactionAsync(
                async (s, token) =>
                {
                    // A real write to the owner's document, in the vault's transaction: it conflicts with a deletion of
                    // the account running at the same time (retried, then refused), so no vault outlives its owner, and
                    // with a rotation of the user key, whose slot check then sees this vault's slot, or whose stamp
                    // refuses it here.
                    var owner = await Users.UpdateOneAsync(
                        s, OwnerFilter(userId, userKey, credentialReadAt), Builders<Accounts.UserDocument>.Update.Inc(u => u.VaultsCreated, 1), cancellationToken: token);
                    if (owner.MatchedCount == 0) throw new Accounts.CredentialsChangedException();
                    await Vaults.InsertOneAsync(s, doc, cancellationToken: token);
                    return true;
                },
                cancellationToken: ct);
        }
        catch
        {
            // Don't leave a slot that blocks the name for a vault that doesn't exist.
            await Slots.DeleteOneAsync(s => s.Id == slot.Id, CancellationToken.None);
            throw;
        }

        var vault = new UnlockedVault(id, name, vaultKey, Now, LocksFor(id)) { EmbeddingProvider = DefaultEmbeddingProvider };
        RecordKey(vault);
        _open[id] = vault;
        KumbuMetrics.VaultUnlocked();
        Unlocked?.Invoke(vault);
        return vault;
    }

    /// <summary>
    /// The id of the user's vault named exactly <paramref name="name"/>, or null. Finds its slot by the keyed name hash
    /// and opens only that one vault's name, without unlocking it or any other vault of the user.
    /// </summary>
    internal async Task<string?> FindForUserAsync(string userId, byte[] userKey, string name, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var f = Builders<SlotDocument>.Filter;
        var slot = await Slots.Find(f.Eq(s => s.Kind, SlotDocument.User) & f.Eq(s => s.Lookup, UserLookup(userKey, userId))
            & f.Eq(s => s.NameId, VaultCrypto.Mac(VaultCrypto.Derive(userKey, VaultNameLabel, []), NormaliseVaultName(name)))).FirstOrDefaultAsync(ct);
        if (slot is null || await Vaults.Find(v => v.Id == slot.Vault).FirstOrDefaultAsync(ct) is not { } doc) return null;
        try
        {
            var vaultKey = VaultCrypto.Open(UserKek(userKey, slot.Vault), slot.Wrapped, SlotContext(slot.Vault, slot.Kind));
            var meta = JsonSerializer.Deserialize<VaultMeta>(VaultCrypto.Open(VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), doc.Meta, $"vault:{doc.Id}"))!;
            // The name hash is case-insensitive; the name asked for must match exactly.
            return string.Equals(meta.Name, name, StringComparison.Ordinal) ? doc.Id : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            log.LogWarning("A user slot of vault {VaultId} matched the user key but did not open", slot.Vault);
            return null;
        }
    }

    /// <summary>
    /// The vault keys of <paramref name="vaultIds"/>, opened from the user's own slots: <see cref="ArgumentException"/>
    /// (its message starting with <paramref name="noun"/>, "An API key", "A connection") if a vault isn't the user's
    /// or no longer exists. What an API key or a connection is given must be the user's own.
    /// <see cref="VaultBusyException"/> for a vault in its two-key period: its user slot still wraps the old key, which
    /// must not go into a new slot.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, byte[]>> UserVaultKeysAsync(
        string userId, byte[] userKey, IReadOnlyCollection<string> vaultIds, string noun, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var ids = vaultIds.ToList();
        var userSlots = await Slots.Find(Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.User)
            & Builders<SlotDocument>.Filter.Eq(s => s.Lookup, UserLookup(userKey, userId)) & Builders<SlotDocument>.Filter.In(s => s.Vault, ids)).ToListAsync(ct);
        var existing = (await Vaults.Find(Builders<VaultDocument>.Filter.In(v => v.Id, ids)).Project(v => new { v.Id, v.RekeyStartedAt }).ToListAsync(ct))
            .ToDictionary(v => v.Id, v => v.RekeyStartedAt is not null, StringComparer.Ordinal);

        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var vaultId in ids)
        {
            byte[]? vaultKey = null;
            foreach (var slot in userSlots.Where(s => s.Vault == vaultId))
            {
                try
                {
                    vaultKey = VaultCrypto.Open(UserKek(userKey, vaultId), slot.Wrapped, SlotContext(vaultId, slot.Kind));
                    break;
                }
                catch (CryptographicException)
                {
                }
            }

            if (vaultKey is null || !existing.TryGetValue(vaultId, out var pending)) throw new ArgumentException($"{noun} can only be given vaults of your own.", nameof(vaultIds));
            if (pending) throw VaultBusyException.Being();
            result[vaultId] = vaultKey;
        }

        return result;
    }

    /// <summary>
    /// The slots a new API key gets: one per vault in <paramref name="vaults"/> (vault id → right), sealing the vault
    /// key and the right together, under a key derived from the API key and bound to the vault and the key's id.
    /// The vault keys come from the user's own slots, so <see cref="ArgumentException"/> if a vault isn't the user's.
    /// Not stored here: <see cref="Accounts.ApiKeys"/> inserts them with the key, in one transaction, after checking
    /// the returned vault keys again there (<see cref="RequireSameVaultKeysAsync"/>).
    /// </summary>
    internal async Task<(IReadOnlyList<SlotDocument> Slots, IReadOnlyDictionary<string, byte[]> VaultKeys)> ApiKeySlotsAsync(
        string userId, byte[] userKey, string keyId, string apiKey, IReadOnlyDictionary<string, Accounts.KeyRight> vaults, CancellationToken ct)
    {
        var vaultKeys = await UserVaultKeysAsync(userId, userKey, [.. vaults.Keys], "An API key", ct);
        IReadOnlyList<SlotDocument> slots = [.. vaults.Select(v => new SlotDocument
        {
            Vault = v.Key,
            Kind = SlotDocument.ApiKey,
            KeyId = keyId,
            Wrapped = VaultCrypto.Seal(
                ApiKeyKek(apiKey, v.Key), JsonSerializer.SerializeToUtf8Bytes(new ApiKeySlotPayload(vaultKeys[v.Key], Accounts.KeyRights.ToText(v.Value), Accounts.KeyRights.SealedVersion)), ApiKeySlotContext(v.Key, keyId)),
            CreatedAt = Now,
        })];
        await IssueFaultAsync("after-wrap");
        return (slots, vaultKeys);
    }

    /// <summary>
    /// The slots one token of a connection gets when the user consents (the code): one per vault in
    /// <paramref name="vaults"/> (vault id → right), sealing the vault key and the right together as an API key's slot
    /// does, under <c>HKDF(token, "kumbu/v1/oauth-kek", vault id)</c> and bound to the vault, the connection and the
    /// token's id, so a slot can't be moved to another token or connection, nor read as an API key's. With
    /// <paramref name="expiresAt"/> the slot carries it for the TTL index. The vault keys come from the user's own
    /// slots, so <see cref="ArgumentException"/> if a vault isn't the user's. Not stored here: the caller inserts them
    /// with the token's record, in one transaction, after checking the returned vault keys again there
    /// (<see cref="RequireSameVaultKeysAsync"/>).
    /// </summary>
    internal async Task<(IReadOnlyList<SlotDocument> Slots, IReadOnlyDictionary<string, byte[]> VaultKeys)> ConnectionSlotsFromUserAsync(
        string userId, byte[] userKey, string connectionId, string tokenId, string token, IReadOnlyDictionary<string, Accounts.KeyRight> vaults,
        DateTime? expiresAt, CancellationToken ct)
    {
        var vaultKeys = await UserVaultKeysAsync(userId, userKey, [.. vaults.Keys], "A connection", ct);
        var slots = WrapConnectionSlots([.. vaults.Select(v => new ConnectionVaultKey(v.Key, vaultKeys[v.Key], v.Value))], connectionId, tokenId, token, expiresAt);
        await IssueFaultAsync("after-wrap");
        return (slots, vaultKeys);
    }

    /// <summary>A vault key opened from a token's slot, with the right sealed into it.</summary>
    internal sealed record ConnectionVaultKey(string Vault, byte[] VaultKey, Accounts.KeyRight Right);

    /// <summary>
    /// The vault keys a connection's token has slots for, each with its right. Every slot is opened, and one past its
    /// <c>expiresAt</c> is refused here (MongoDB's TTL index only tidies up, once a minute); a slot that doesn't open
    /// (changed or moved in the database) gives nothing.
    /// </summary>
    internal async Task<IReadOnlyList<ConnectionVaultKey>> OpenConnectionSlotsAsync(string connectionId, string tokenId, string token, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var f = Builders<SlotDocument>.Filter;
        var slots = await Slots.Find(f.Eq(s => s.Kind, SlotDocument.Connection) & f.Eq(s => s.TokenId, tokenId) & f.Eq(s => s.ConnectionId, connectionId)).ToListAsync(ct);
        var now = Now;
        var result = new List<ConnectionVaultKey>();
        foreach (var slot in slots)
        {
            if (now >= slot.ExpiresAt) continue;
            try
            {
                var payload = JsonSerializer.Deserialize<ApiKeySlotPayload>(
                    VaultCrypto.Open(ConnectionKek(token, slot.Vault), slot.Wrapped, ConnectionSlotContext(slot.Vault, connectionId, tokenId)))!;
                result.Add(new ConnectionVaultKey(slot.Vault, payload.VaultKey, RightOf(payload)));
            }
            catch (Exception e) when (e is CryptographicException or JsonException)
            {
                log.LogWarning("A slot of connection {ConnectionId} on vault {VaultId} did not open", connectionId, slot.Vault);
            }
        }

        return result;
    }

    /// <summary>Wraps opened vault keys for a token of the connection, under <c>HKDF(token, "kumbu/v1/oauth-kek", vault id)</c>
    /// and bound to the vault, the connection and the token's id, so a slot can't be moved to another token or
    /// connection, nor read as an API key's.</summary>
    internal IReadOnlyList<SlotDocument> WrapConnectionSlots(
        IReadOnlyList<ConnectionVaultKey> keys, string connectionId, string tokenId, string token, DateTime? expiresAt) =>
        [.. keys.Select(k => new SlotDocument
        {
            Vault = k.Vault,
            Kind = SlotDocument.Connection,
            ConnectionId = connectionId,
            TokenId = tokenId,
            ExpiresAt = expiresAt,
            Wrapped = VaultCrypto.Seal(
                ConnectionKek(token, k.Vault),
                JsonSerializer.SerializeToUtf8Bytes(new ApiKeySlotPayload(k.VaultKey, Accounts.KeyRights.ToText(k.Right), Accounts.KeyRights.SealedVersion)),
                ConnectionSlotContext(k.Vault, connectionId, tokenId)),
            CreatedAt = Now,
        })];

    /// <summary>
    /// The vaults an access token of a connection has slots for, unlocked, each with its right: opened afresh on every
    /// request, as <see cref="OpenForApiKeyAsync"/>, so a revoked or expired token (whose slots are gone or past their
    /// expiry) opens nothing, whatever is in memory.
    /// </summary>
    internal async Task<IReadOnlyList<(UnlockedVault Vault, Accounts.KeyRight Right)>> OpenForConnectionAsync(
        string connectionId, string tokenId, string token, CancellationToken ct)
    {
        var result = new List<(UnlockedVault, Accounts.KeyRight)>();
        foreach (var key in await OpenConnectionSlotsAsync(connectionId, tokenId, token, ct))
        {
            if (await LoadAsync(key.Vault, key.VaultKey, ct) is { } vault) result.Add((vault, key.Right));
        }

        return result;
    }

    /// <summary>Deletes the slots of these tokens of connections, inside the caller's transaction.</summary>
    internal Task DeleteTokenSlotsAsync(IClientSessionHandle session, IReadOnlyCollection<string> tokenIds, CancellationToken ct) =>
        Slots.DeleteManyAsync(
            session, Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.Connection) & Builders<SlotDocument>.Filter.In(s => s.TokenId, tokenIds), cancellationToken: ct);

    /// <summary>
    /// The vaults an API key has slots for, unlocked, each with the right sealed into its slot. As in
    /// <see cref="OpenAsync"/>, every slot is opened, also for a vault in memory: a revoked key has no slots left,
    /// and a slot that was changed in the database (its right, say) doesn't open and gives nothing.
    /// </summary>
    internal async Task<IReadOnlyList<(UnlockedVault Vault, Accounts.KeyRight Right)>> OpenForApiKeyAsync(string keyId, string apiKey, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var slots = await Slots.Find(Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.ApiKey) & Builders<SlotDocument>.Filter.Eq(s => s.KeyId, keyId)).ToListAsync(ct);

        var result = new List<(UnlockedVault, Accounts.KeyRight)>();
        foreach (var slot in slots)
        {
            ApiKeySlotPayload payload;
            Accounts.KeyRight right;
            try
            {
                payload = JsonSerializer.Deserialize<ApiKeySlotPayload>(VaultCrypto.Open(ApiKeyKek(apiKey, slot.Vault), slot.Wrapped, ApiKeySlotContext(slot.Vault, keyId)))!;
                right = RightOf(payload);
            }
            catch (Exception e) when (e is CryptographicException or JsonException)
            {
                log.LogWarning("A slot of API key {KeyId} on vault {VaultId} did not open", keyId, slot.Vault);
                continue;
            }

            if (await LoadAsync(slot.Vault, payload.VaultKey, ct) is { } vault) result.Add((vault, right));
        }

        return result;
    }

    /// <summary>The collections that hold a vault's documents (under their <c>vault</c> field), for storage figures.</summary>
    internal IEnumerable<IMongoCollection<SealedDocument>> ContentCollections =>
        [Notes, Trash, Moves, Embeddings, Checkpoints, CheckpointTexts, Attachments, AttachmentTrash, BlobsByVault, PdfPartsByVault, PdfJobsByVault];

    /// <summary>
    /// Deletes vaults for good: the vault documents, every slot on them (the owner's, API keys', the dev token's,
    /// recovery) and every document they hold (notes with their history, trash, moves, embeddings, checkpoints and
    /// their texts, attachments with their trash, blobs and the analyses and jobs of PDFs), in one transaction together with <paramref name="alsoDelete"/>, which may throw to abort it all.
    /// <para>
    /// Nothing may bring a deleted vault back or write for it afterwards. The ids are tombstoned first
    /// (<see cref="IsDeleted"/>), and an embedding of the vault under way is cancelled (<see cref="InterruptToken"/>):
    /// from then on the vault isn't loaded, written or indexed, whatever <see cref="UnlockedVault"/> object a request
    /// or the search index's queue still holds, and no more of its text goes to an external provider. Then the vault in
    /// memory is taken off writes and indexing (both its gates), which waits for a write or an embedding call already
    /// under way. Loading is held off (<c>_loading</c>) only after that, for the transaction, so a request that read
    /// the slot and the vault document just before the commit can't put the vault back into the working set; taking
    /// it last keeps a slow provider from stalling every other vault's requests. After the commit the vault in memory
    /// is marked deleted and dropped, and the content collections are swept once more for a write of an older object
    /// that was already past its tombstone check.
    /// </para>
    /// Returns how many documents went, slots included.
    /// </summary>
    internal async Task<long> DeleteVaultsAsync(
        IReadOnlyCollection<string> vaultIds, Func<IClientSessionHandle, CancellationToken, Task> alsoDelete, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var ids = vaultIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var held = new List<VaultLocks>();
        var seen = new List<UnlockedVault>();
        var tombstoned = ids.Where(id => _deleted.TryAdd(id, 0)).ToList();
        foreach (var id in ids) Interrupt(id);

        IReadOnlyList<string> revoked = [];
        IReadOnlyList<string> revokedConnections = [];
        var loading = false;
        try
        {
            // In id order, so two deletions can't each hold a gate the other waits for. A vault loaded after this
            // look is held by nobody, but the tombstone keeps it from writing and indexing already. A re-keying run
            // holds both gates; it sees the tombstone at its next batch and lets go of them.
            foreach (var id in ids)
            {
                var open = _open.TryGetValue(id, out var vault);
                if (!open && !IsRekeying(id)) continue;
                if (open) seen.Add(vault!);
                var locks = LocksFor(id);
                await locks.Gate.WaitAsync(ct);
                try
                {
                    await locks.IndexGate.WaitAsync(ct);
                }
                catch
                {
                    locks.Gate.Release();
                    throw;
                }

                held.Add(locks);
            }

            // After the gates: nothing that holds _loading waits for a gate, so this order can't deadlock.
            await _loading.WaitAsync(ct);
            loading = true;
            long deleted;
            using (var session = await StartSessionAsync(ct))
            {
                // WithTransactionAsync retries a write conflict, e.g. with lastUsedAt written on a key or session in use.
                deleted = await session.WithTransactionAsync(
                    async (s, token) =>
                    {
                        var slot = Builders<SlotDocument>.Filter;
                        var keyIds = await (await Slots.DistinctAsync(
                            s, sl => sl.KeyId, slot.Eq(sl => sl.Kind, SlotDocument.ApiKey) & slot.In(sl => sl.Vault, ids) & slot.Exists(sl => sl.KeyId), cancellationToken: token))
                            .ToListAsync(token);
                        await MarkOwnersAsync(s, ids, token);
                        long count = (await Vaults.DeleteManyAsync(s, Builders<VaultDocument>.Filter.In(v => v.Id, ids), cancellationToken: token)).DeletedCount;
                        // The connections' slots on these vaults go here too, by vault.
                        count += (await Slots.DeleteManyAsync(s, slot.In(sl => sl.Vault, ids), cancellationToken: token)).DeletedCount;
                        revoked = await RevokeKeysWithoutVaultsAsync(s, keyIds, token);
                        revokedConnections = await Accounts.ConnectionRevocation.RemoveVaultsAsync(db, s, ids, Now, token);
                        foreach (var collection in ContentCollections)
                        {
                            count += (await collection.DeleteManyAsync(s, Builders<SealedDocument>.Filter.In(d => d.Vault, ids), cancellationToken: token)).DeletedCount;
                        }

                        await alsoDelete(s, token);
                        return count;
                    },
                    cancellationToken: ct);
            }

            tombstoned.Clear();
            foreach (var keyId in revoked) log.LogInformation("API key {KeyId} has no vault left and is revoked", keyId);
            foreach (var connectionId in revokedConnections) log.LogInformation("Connection {ConnectionId} has no vault left and is revoked", connectionId);
            foreach (var vault in seen) vault.Deleted = true;
            foreach (var id in ids)
            {
                if (_open.TryRemove(id, out var vault)) vault.Deleted = true;
                RaiseNotesChanged(id);
            }

            foreach (var collection in ContentCollections)
            {
                deleted += (await collection.DeleteManyAsync(Builders<SealedDocument>.Filter.In(d => d.Vault, ids), CancellationToken.None)).DeletedCount;
            }

            return deleted;
        }
        finally
        {
            // A deletion that failed takes its tombstones back: the vault is still there.
            foreach (var id in tombstoned) _deleted.TryRemove(id, out _);
            foreach (var locks in held)
            {
                locks.IndexGate.Release();
                locks.Gate.Release();
            }

            if (loading) _loading.Release();
        }
    }

    /// <summary>
    /// Inside a deletion: a real write to the user document of each owner of the vaults (<c>vaultsDeleted</c>, and the
    /// vaults out of <c>rekeyPending</c>), so that an API key or connection created, or a connection given new rights,
    /// at the same time, which writes that document too, conflicts with the deletion and, retried, finds the vault gone
    /// (<see cref="RequireVaultsAsync"/>).
    /// </summary>
    private async Task MarkOwnersAsync(IClientSessionHandle session, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var slot = Builders<SlotDocument>.Filter;
        var owners = await (await Slots.DistinctAsync(
            session, sl => sl.Owner, slot.Eq(sl => sl.Kind, SlotDocument.User) & slot.In(sl => sl.Vault, ids) & slot.Exists(sl => sl.Owner), cancellationToken: ct))
            .ToListAsync(ct);
        if (owners.Count == 0) return;
        await db.GetCollection<Accounts.UserDocument>("users").UpdateManyAsync(
            session,
            Builders<Accounts.UserDocument>.Filter.In(u => u.Id, owners),
            // A vault that is gone has nothing left to re-key.
            Builders<Accounts.UserDocument>.Update.Inc(u => u.VaultsDeleted, 1).PullAll(u => u.RekeyPending, ids),
            cancellationToken: ct);
    }

    /// <summary>
    /// Inside the transaction of an API key's or connection's creation or a connection's rights change, which also
    /// writes the user document: every one of <paramref name="vaultIds"/> still exists, else <see cref="ArgumentException"/>.
    /// A deletion that committed first is seen here; one under way conflicts on the user document (<see cref="MarkOwnersAsync"/>).
    /// </summary>
    internal async Task RequireVaultsAsync(IClientSessionHandle session, IReadOnlyCollection<string> vaultIds, string noun, CancellationToken ct)
    {
        var ids = vaultIds.Distinct(StringComparer.Ordinal).ToList();
        var existing = await Vaults.CountDocumentsAsync(session, Builders<VaultDocument>.Filter.In(v => v.Id, ids), cancellationToken: ct);
        if (existing != ids.Count) throw new ArgumentException($"{noun} can only be given vaults of your own.", nameof(vaultIds));
    }

    /// <summary>
    /// Inside a deletion: of the API keys that had slots on the deleted vaults, those left with none (they can open
    /// nothing any more) are revoked, as <see cref="Accounts.ApiKeys.RevokeAsync"/> would, name freed. Returns their ids.
    /// </summary>
    private async Task<IReadOnlyList<string>> RevokeKeysWithoutVaultsAsync(IClientSessionHandle session, IReadOnlyList<string?> keyIds, CancellationToken ct)
    {
        if (keyIds.Count == 0) return [];
        var slot = Builders<SlotDocument>.Filter;
        var left = (await (await Slots.DistinctAsync(session, sl => sl.KeyId, slot.In(sl => sl.KeyId, keyIds), cancellationToken: ct)).ToListAsync(ct)).ToHashSet();
        var orphans = keyIds.Where(k => k is not null && !left.Contains(k)).Select(k => k!).ToList();
        if (orphans.Count == 0) return [];
        var keys = db.GetCollection<Accounts.ApiKeyDocument>(Accounts.ApiKeys.CollectionName);
        var unrevoked = await keys.Find(session, k => orphans.Contains(k.Id) && k.RevokedAt == null).Project(k => k.Id).ToListAsync(ct);
        if (unrevoked.Count == 0) return [];
        await keys.UpdateManyAsync(
            session,
            Builders<Accounts.ApiKeyDocument>.Filter.In(k => k.Id, unrevoked),
            Builders<Accounts.ApiKeyDocument>.Update.Set(k => k.RevokedAt, Now).Unset(k => k.NameId),
            cancellationToken: ct);
        return unrevoked;
    }

    /// <summary>Whether the vault was deleted (or is being deleted) in this process: such a vault is never loaded,
    /// written or indexed again, whatever <see cref="UnlockedVault"/> object a caller still holds.</summary>
    public bool IsDeleted(string vaultId) => _deleted.ContainsKey(vaultId);

    /// <summary>
    /// Cancelled when the vault is deleted or a re-keying run marks it: an embedding call for it stops, rather than
    /// holding up the deletion or the run for minutes. Already cancelled while either is in force. After a run (finished
    /// or not), the next call gets a fresh token, so indexing of the vault resumes.
    /// </summary>
    internal CancellationToken InterruptToken(string vaultId)
    {
        var token = _interrupts.GetOrAdd(vaultId, _ => new CancellationTokenSource()).Token;
        // After the lookup: a deletion tombstones first and a run marks first, each cancelling second, so one of the
        // two catches it.
        return IsDeleted(vaultId) || IsRekeying(vaultId) ? new CancellationToken(canceled: true) : token;
    }

    /// <summary>Cancels the vault's interrupt token and forgets it, after the tombstone or the mark is in place.</summary>
    private void Interrupt(string vaultId)
    {
        if (_interrupts.TryRemove(vaultId, out var source)) source.Cancel();
    }

    /// <summary>Deletes an API key's slots, outside a transaction (an expired key, on its first use after expiry).</summary>
    internal Task DeleteApiKeySlotsAsync(string keyId, CancellationToken ct) =>
        Slots.DeleteManyAsync(Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.ApiKey) & Builders<SlotDocument>.Filter.Eq(s => s.KeyId, keyId), ct);

    /// <summary>Deletes an API key's slots, inside the caller's transaction (revoking the key).</summary>
    internal Task DeleteApiKeySlotsAsync(IClientSessionHandle session, string keyId, CancellationToken ct) =>
        Slots.DeleteManyAsync(session, Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.ApiKey) & Builders<SlotDocument>.Filter.Eq(s => s.KeyId, keyId), cancellationToken: ct);

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

    /// <summary>
    /// Vaults without a user slot that <paramref name="secret"/> has no slot for: the dev-token vaults it can't open
    /// (after the token was changed). A user's vault was never the token's to open; counting it would make a
    /// dev-token agent think something is broken, and tell it how many vaults the users have.
    /// </summary>
    public async Task<int> CountLockedAsync(string secret, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var owned = (await Slots.Distinct(s => s.Vault, Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.User), cancellationToken: ct).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var unowned = await Vaults.CountDocumentsAsync(Builders<VaultDocument>.Filter.Nin(v => v.Id, owned), cancellationToken: ct);
        var opened = (await OpenAsync(secret, ct)).Select(v => v.Id).Where(id => !owned.Contains(id)).Distinct().Count();
        return (int)(unowned - opened);
    }

    public async Task<long> CountVaultsAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        return await Vaults.CountDocumentsAsync(FilterDefinition<VaultDocument>.Empty, cancellationToken: ct);
    }

    private static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(1);

    /// <summary>A vault used this recently is never locked: a request may still be working with it.</summary>
    public static readonly TimeSpan InUse = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Runs once a minute (<see cref="VaultSweeper"/>). Locks vaults idle for longer than the timeout, then the
    /// least recently used ones while the working sets exceed the memory cap; a vault used within
    /// <see cref="InUse"/> stays, even above the cap. Also drops trash and move records MongoDB has expired, and change
    /// times older than any cursor the version list answers, and starts <see cref="BlobSweep"/> over the vaults still
    /// unlocked (in the background; one round at a time).
    /// Keys go with the vault, not zeroed: a request still holding it must not see its key change.
    /// </summary>
    public void Sweep() => SweepUntil(CancellationToken.None);

    /// <summary><see cref="Sweep"/>, whose blob sweep stops with <paramref name="ct"/> (the server shutting down).</summary>
    public void SweepUntil(CancellationToken ct)
    {
        var o = options.Value.Vaults;
        var now = Now;
        foreach (var (id, vault) in _open)
        {
            if (now - vault.LastUsed > TimeSpan.FromMinutes(o.IdleMinutes) && now - vault.LastUsed > InUse && _open.TryRemove(id, out _))
            {
                KumbuMetrics.VaultLocked(KumbuMetrics.LockReason.Idle);
                log.LogInformation("Locked vault {VaultId} after {Minutes} idle minutes", vault.Id, o.IdleMinutes);
            }
        }

        var cap = o.MemoryLimitMegabytes * 1024L * 1024;
        foreach (var vault in _open.Values.Where(v => now - v.LastUsed > InUse).OrderBy(v => v.LastUsed))
        {
            if (_open.Values.Sum(v => v.ApproxBytes) <= cap) break;
            if (_open.TryRemove(vault.Id, out _))
            {
                KumbuMetrics.VaultLocked(KumbuMetrics.LockReason.Memory);
                log.LogInformation("Locked vault {VaultId} to stay below the memory limit", vault.Id);
            }
        }

        foreach (var vault in _open.Values)
        {
            vault.Trash = vault.Trash.RemoveAll(t => now - t.Trashed.DeletedAt > NoteWriter.TrashRetention);
            vault.Moves = vault.Moves.RemoveAll(m => now - m.MovedAt > Kumbu.Core.Notes.Moves.Retention);
            // No cursor reaches back further than this (older ones get the full list): the change record needn't either.
            var forgotten = now - (Api.SyncApi.MaxCursorAge + Api.SyncApi.Overlap);
            // Under the gate, which writes record under, so no new time is lost; if a write holds it, next minute.
            if (vault.ChangedAt.Values.Any(t => t < forgotten) && vault.Gate.Wait(0))
            {
                try
                {
                    vault.ChangedAt = vault.ChangedAt.RemoveRange(vault.ChangedAt.Where(c => c.Value < forgotten).Select(c => c.Key));
                }
                finally
                {
                    vault.Gate.Release();
                }
            }
        }

        if (Interlocked.Exchange(ref _sweepingBlobs, 1) == 0)
        {
            var vaults = _open.Values.ToList();
            _ = Task.Run(() => SweepBlobsAsync(vaults, now, ct), CancellationToken.None);
        }
    }

    private int _sweepingBlobs;

    private async Task SweepBlobsAsync(IReadOnlyList<UnlockedVault> vaults, DateTime now, CancellationToken ct)
    {
        try
        {
            var sweep = new BlobSweep(this, log);
            foreach (var vault in vaults)
            {
                try
                {
                    await sweep.RunAsync(vault, now, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    log.LogError(e, "Sweeping the blobs of vault {VaultId} failed", vault.Id);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _sweepingBlobs, 0);
        }
    }

    /// <summary>The embedding provider of a vault whose owner never chose one; its meta keeps no name for it.</summary>
    public string DefaultEmbeddingProvider => options.Value.Embeddings.Default;

    /// <summary>
    /// Points the vault at another embedding provider: its meta is sealed again with the choice, then the working set
    /// follows. The search index re-embeds the vault afterwards (<see cref="Search.SearchIndex.CatchUp"/>).
    /// </summary>
    internal async Task SetEmbeddingProviderAsync(UnlockedVault vault, string provider, CancellationToken ct)
    {
        CheckWritable(vault);
        await vault.Gate.WaitAsync(ct);
        try
        {
            // Deleted since the request read it (here, or in another process: then only its document is gone), or
            // re-keyed meanwhile.
            CheckWritable(vault);
            var result = await Vaults.UpdateOneAsync(v => v.Id == vault.Id, Builders<VaultDocument>.Update.Set(v => v.Meta, vault.SealMeta(provider)), cancellationToken: ct);
            if (result.MatchedCount == 0) throw new VaultDeletedException();
            vault.EmbeddingProvider = provider;
        }
        finally
        {
            vault.Gate.Release();
        }
    }

    /// <summary>How many vaults are unlocked now.</summary>
    public int UnlockedCount => _open.Count;

    /// <summary>The unlocked vaults that may be written and indexed now (<see cref="MayIndex"/>).</summary>
    internal IReadOnlyList<UnlockedVault> UnlockedVaults => [.. _open.Values.Where(MayIndex)];

    /// <summary>
    /// A blob's bytes, read from MongoDB and opened; null if the vault doesn't hold it, <see cref="VaultUnavailableException"/>
    /// if it doesn't open (changed or damaged outside Kumbu²). The read and the decryption take one of the server's
    /// <see cref="FileReads"/> slots, waiting for one if all are taken; the slot goes back before the bytes are returned.
    /// </summary>
    internal async Task<byte[]?> ReadBlobAsync(UnlockedVault vault, byte[] blobId, CancellationToken ct)
    {
        await FileReads.WaitAsync(ct);
        try
        {
            var doc = await Blobs.Find(Builders<BlobDocument>.Filter.Eq(d => d.Vault, vault.Id) & Builders<BlobDocument>.Filter.Eq(d => d.BlobId, blobId)).FirstOrDefaultAsync(ct);
            return doc is null ? null : vault.OpenBlob(doc);
        }
        catch (Exception e) when (e is CryptographicException or InvalidDataException)
        {
            throw new VaultUnavailableException("an attachment's content doesn't open: it was changed or damaged outside Kumbu²");
        }
        finally
        {
            FileReads.Release();
        }
    }

    /// <summary>For tests: how many vaults are unlocked now.</summary>
    public int OpenCount => UnlockedCount;

    /// <summary>Reads one note again after a compare-and-swap failed: someone else wrote it.</summary>
    /// <summary>Takes <paramref name="vault"/> out of the working set (if it is still the one there) after it failed to follow
    /// a committed write: the next request loads the vault again.</summary>
    internal void Forget(UnlockedVault vault, Exception reason)
    {
        if (_open.TryRemove(KeyValuePair.Create(vault.Id, vault))) log.LogWarning(reason, "Vault {VaultId} is read again: it couldn't follow a write made through an older copy", vault.Id);
    }

    /// <summary>For tests: runs at the start of <see cref="RefreshAsync"/>, to make it fail.</summary>
    internal Func<Task>? RefreshFaultAt { get; set; }

    internal async Task RefreshAsync(UnlockedVault vault, VaultPath path, CancellationToken ct)
    {
        if (RefreshFaultAt is { } fault) await fault();
        var pathId = vault.PathId(path);
        var key = Convert.ToBase64String(pathId);
        if (path.IsAttachment)
        {
            var record = await Attachments.Find(Builders<SealedDocument>.Filter.Eq(d => d.Vault, vault.Id) & Builders<SealedDocument>.Filter.Eq(d => d.PathId, pathId)).FirstOrDefaultAsync(ct);
            if (record is null)
            {
                vault.Attachments = vault.Attachments.Remove(path);
                vault.DamagedPathIds = vault.DamagedPathIds.Remove(key);
                vault.DamagedAttachmentIds = vault.DamagedAttachmentIds.Remove(key);
            }
            else if (TryOpen(vault, () => vault.OpenAttachment(record), record) is { } attachment)
            {
                vault.Attachments = vault.Attachments.SetItem(path, (record.Id, attachment));
                vault.DamagedPathIds = vault.DamagedPathIds.Remove(key);
                vault.DamagedAttachmentIds = vault.DamagedAttachmentIds.Remove(key);
            }
            else
            {
                vault.Attachments = vault.Attachments.Remove(path);
                vault.DamagedPathIds = vault.DamagedPathIds.Add(key);
                vault.DamagedAttachmentIds = vault.DamagedAttachmentIds.Add(key);
            }

            return;
        }

        var doc = await Notes.Find(Builders<SealedDocument>.Filter.Eq(d => d.Vault, vault.Id) & Builders<SealedDocument>.Filter.Eq(d => d.PathId, pathId)).FirstOrDefaultAsync(ct);
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
            if (IsDeleted(id)) return null;
            if (_open.TryGetValue(id, out var cached))
            {
                if (CryptographicOperations.FixedTimeEquals(cached.KeyId, VaultCrypto.Derive(vaultKey, "kumbu/v1/key-id", []))) return cached;
                // A key held from before a re-keying here (a request that opened its slot just before the finish, say).
                if (WasRekeyed(id)) throw VaultBusyException.Just();
                return null;
            }

            var doc = await Vaults.Find(v => v.Id == id).FirstOrDefaultAsync(ct);
            if (doc is null) return null;
            VaultMeta meta;
            try
            {
                meta = JsonSerializer.Deserialize<VaultMeta>(VaultCrypto.Open(VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), doc.Meta, $"vault:{id}"))!;
            }
            catch (CryptographicException) when (WasRekeyed(id))
            {
                throw VaultBusyException.Just();
            }

            // Never chosen: the instance's default, so the vault follows it when the admin changes it.
            var provider = meta.Embeddings ?? DefaultEmbeddingProvider;

            // In its two-key period: named, so that it isn't reported missing or created again, and nothing else.
            // Never in the working set, never with a current key id, never announced to the search index.
            if (doc.RekeyStartedAt is not null) return new UnlockedVault(id, meta.Name, vaultKey, Now, LocksFor(id)) { Pending = true, EmbeddingProvider = provider };
            // Outside its period (under _loading, as every start and finish): a start whose commit went unanswered didn't
            // commit after all.
            _pending.TryRemove(id, out _);

            var vault = new UnlockedVault(id, meta.Name, vaultKey, Now, LocksFor(id)) { EmbeddingProvider = provider };
            var notes = ImmutableDictionary.CreateBuilder<VaultPath, (MongoDB.Bson.ObjectId, StoredNote)>();
            var damaged = ImmutableHashSet.CreateBuilder<string>();
            foreach (var d in await Notes.Find(n => n.Vault == id).ToListAsync(ct))
            {
                if (TryOpen(vault, () => vault.OpenNote(d), d) is { } note) notes[note.Path] = (d.Id, note);
                else damaged.Add(Convert.ToBase64String(d.PathId));
            }

            vault.Notes = notes.ToImmutable();
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
            // The records only: blobs are read on demand, never held with the vault. A record that doesn't open is marked
            // damaged under its path id, as a note is.
            var attachments = ImmutableDictionary.CreateBuilder<VaultPath, (MongoDB.Bson.ObjectId, Kumbu.Core.Attachments.StoredAttachment)>();
            var damagedAttachments = ImmutableHashSet.CreateBuilder<string>();
            foreach (var d in await Attachments.Find(n => n.Vault == id).ToListAsync(ct))
            {
                if (TryOpen(vault, () => vault.OpenAttachment(d), d) is { } attachment) attachments[attachment.Path] = (d.Id, attachment);
                else damagedAttachments.Add(Convert.ToBase64String(d.PathId));
            }

            vault.Attachments = attachments.ToImmutable();
            damaged.UnionWith(damagedAttachments);
            vault.DamagedPathIds = damaged.ToImmutable();
            vault.DamagedAttachmentIds = damagedAttachments.ToImmutable();
            vault.AttachmentTrash = [.. (await AttachmentTrash.Find(n => n.Vault == id).ToListAsync(ct))
                .Select(d => (d.Id, Trashed: TryOpen(vault, () => vault.OpenAttachmentTrash(d), d)))
                .Where(t => t.Trashed is not null)
                .Select(t => (t.Id, t.Trashed!))];

            RecordKey(vault);
            _open[id] = vault;
            log.LogInformation("Unlocked vault {VaultId} ({Notes})", vault.Id, vault.Notes.Count == 1 ? "1 note" : $"{vault.Notes.Count} notes");
            KumbuMetrics.VaultUnlocked();
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
            log.LogError("Document {Id} of vault {VaultId} does not open; it was changed or damaged outside Kumbu²", doc.Id, vault.Id);
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

    private static byte[] UserLookup(byte[] userKey, string userId) => VaultCrypto.Derive(userKey, UserLookupLabel, Encoding.UTF8.GetBytes(userId));

    private static byte[] UserKek(byte[] userKey, string vaultId) => VaultCrypto.Derive(userKey, UserKekLabel, Encoding.UTF8.GetBytes(vaultId));

    private static string NormaliseVaultName(string name) => name.Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant();

    private static string SlotContext(string vaultId, string kind) => $"slot:{vaultId}:{kind}";

    private static byte[] ApiKeyKek(string apiKey, string vaultId) => VaultCrypto.Derive(apiKey, ApiKeyKekLabel, Encoding.UTF8.GetBytes(vaultId));

    /// <summary>Also bound to the key's id, so a slot can't be moved to another key, nor read as a dev-token slot.</summary>
    private static string ApiKeySlotContext(string vaultId, string keyId) => $"slot:{vaultId}:{SlotDocument.ApiKey}:{keyId}";

    private static byte[] ConnectionKek(string token, string vaultId) => VaultCrypto.Derive(token, ConnectionKekLabel, Encoding.UTF8.GetBytes(vaultId));

    /// <summary>Bound to the connection and the token's id: a slot can't be moved to another token, connection or kind.</summary>
    private static string ConnectionSlotContext(string vaultId, string connectionId, string tokenId) =>
        $"slot:{vaultId}:{SlotDocument.Connection}:{connectionId}:{tokenId}";

    /// <summary>What an API key's or a connection's slot seals: the vault key and the right on the vault, together;
    /// <c>v</c> is <see cref="Accounts.KeyRights.SealedVersion"/> from 0.3.0 on, absent before.</summary>
    private sealed record ApiKeySlotPayload(
        [property: System.Text.Json.Serialization.JsonPropertyName("vaultKey")] byte[] VaultKey,
        [property: System.Text.Json.Serialization.JsonPropertyName("rights")] string Rights,
        [property: System.Text.Json.Serialization.JsonPropertyName("v")] int? V = null);

    /// <summary>The right a slot grants: a <c>write</c> sealed before 0.3.0 reads as <c>maintain</c> (<see cref="Accounts.KeyRights.ParseSealed"/>).</summary>
    private static Accounts.KeyRight RightOf(ApiKeySlotPayload payload) =>
        Accounts.KeyRights.ParseSealed(payload.Rights, payload.V) ?? throw new JsonException("unknown right");

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
        await Attachments.Indexes.CreateOneAsync(
            new CreateIndexModel<SealedDocument>(sealedKeys.Ascending(d => d.Vault).Ascending(d => d.PathId), new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
        // No TTL on deletedAt: the blob sweep expires attachment trash, and releases the blobs only it held.
        await AttachmentTrash.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<SealedDocument>(sealedKeys.Ascending(d => d.Vault).Ascending(d => d.PathId)),
                new CreateIndexModel<SealedDocument>(sealedKeys.Ascending(d => d.Vault).Ascending(d => d.DeletedAt)),
            ],
            ct);
        await Blobs.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BlobDocument>(Builders<BlobDocument>.IndexKeys.Ascending(d => d.Vault).Ascending(d => d.BlobId), new CreateIndexOptions { Unique = true }),
                // The sweep's candidates by age, without fetching them (BlobSweep.CandidateIndex).
                new CreateIndexModel<BlobDocument>(BlobSweep.CandidateIndex),
                // Whether blobs exist, and since when, without fetching them (EncryptedNoteStore.RequireBlobsAsync).
                new CreateIndexModel<BlobDocument>(EncryptedNoteStore.BlobTimeIndex),
            ],
            ct);
        await PdfParts.Indexes.CreateOneAsync(
            new CreateIndexModel<PdfAnalysisDocument>(
                Builders<PdfAnalysisDocument>.IndexKeys.Ascending(d => d.Vault).Ascending(d => d.BlobId).Ascending(d => d.Part), new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
        await PdfJobs.Indexes.CreateOneAsync(
            new CreateIndexModel<PdfJobDocument>(Builders<PdfJobDocument>.IndexKeys.Ascending(d => d.Vault).Ascending(d => d.BlobId), new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
        await Slots.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<SlotDocument>(Builders<SlotDocument>.IndexKeys.Ascending(s => s.Lookup)),
                new CreateIndexModel<SlotDocument>(Builders<SlotDocument>.IndexKeys.Ascending(s => s.Vault)),
                new CreateIndexModel<SlotDocument>(
                    Builders<SlotDocument>.IndexKeys.Ascending(s => s.Owner),
                    new CreateIndexOptions<SlotDocument> { PartialFilterExpression = Builders<SlotDocument>.Filter.Exists(s => s.Owner) }),
                new CreateIndexModel<SlotDocument>(
                    Builders<SlotDocument>.IndexKeys.Ascending(s => s.KeyId),
                    new CreateIndexOptions<SlotDocument> { PartialFilterExpression = Builders<SlotDocument>.Filter.Exists(s => s.KeyId) }),
                // A connection's token finds its slots by its id; revoking a connection finds them by the connection.
                new CreateIndexModel<SlotDocument>(
                    Builders<SlotDocument>.IndexKeys.Ascending(s => s.TokenId),
                    new CreateIndexOptions<SlotDocument> { PartialFilterExpression = Builders<SlotDocument>.Filter.Exists(s => s.TokenId) }),
                new CreateIndexModel<SlotDocument>(
                    Builders<SlotDocument>.IndexKeys.Ascending(s => s.ConnectionId),
                    new CreateIndexOptions<SlotDocument> { PartialFilterExpression = Builders<SlotDocument>.Filter.Exists(s => s.ConnectionId) }),
                // A token's slots vanish physically when it expires (spec §5); only slots with expiresAt carry one.
                new CreateIndexModel<SlotDocument>(Builders<SlotDocument>.IndexKeys.Ascending(s => s.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
                // One run at a time per vault: at most one slot with its new key.
                new CreateIndexModel<SlotDocument>(
                    Builders<SlotDocument>.IndexKeys.Ascending(s => s.Vault),
                    new CreateIndexOptions<SlotDocument>
                    {
                        Unique = true,
                        Name = "vault_user_next",
                        PartialFilterExpression = Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.UserNext),
                    }),
                // One vault name per user: only user slots carry a nameId.
                new CreateIndexModel<SlotDocument>(
                    Builders<SlotDocument>.IndexKeys.Ascending(s => s.Lookup).Ascending(s => s.NameId),
                    new CreateIndexOptions<SlotDocument>
                    {
                        Unique = true,
                        PartialFilterExpression = new MongoDB.Bson.BsonDocument("nameId", new MongoDB.Bson.BsonDocument("$type", "binData")),
                    }),
            ],
            ct);
        _ready = true;
    }

    /// <summary>
    /// The owner, active. With the session's <paramref name="credentialReadAt"/> (every request's creation passes it),
    /// also: credentials unchanged since then, and <paramref name="userKey"/> still the user key in force (its key id),
    /// so a key a rotation made dead can't create a vault under it. Without it (the seeding and tests, which give
    /// vaults to users made directly in the database) only the first two hold.
    /// </summary>
    private static FilterDefinition<Accounts.UserDocument> OwnerFilter(string userId, byte[] userKey, DateTime? credentialReadAt)
    {
        var f = Builders<Accounts.UserDocument>.Filter;
        var owner = f.Eq(u => u.Id, userId) & f.Eq(u => u.Active, true);
        return credentialReadAt is { } readAt
            ? owner & (f.Eq(u => u.CredentialsChangedAt, null) | f.Lt(u => u.CredentialsChangedAt, readAt)) & f.Eq(u => u.KeyId, Accounts.UserCatalog.KeyId(userKey, userId))
            : owner;
    }

    /// <summary>The lookup of a user's slots under <paramref name="userKey"/>.</summary>
    internal static byte[] UserSlotLookup(byte[] userKey, string userId) => UserLookup(userKey, userId);

    /// <summary>What a rotation of the user key does to the vault slots (<see cref="Accounts.UserKeyRotation"/>).</summary>
    internal sealed record PreparedUserSlots(
        IReadOnlyList<Accounts.SlotRewrap> Rewraps, IReadOnlyList<MongoDB.Bson.ObjectId> Skipped, IReadOnlyList<MongoDB.Bson.ObjectId> Orphans, IReadOnlyList<string> VaultIds);

    /// <summary>
    /// For a rotation of the user key: every slot found by the old key's lookup, whatever its kind, wrapped again under
    /// <paramref name="newUserKey"/> with the AAD of its own kind; a <c>user</c> slot also gets the vault name hash
    /// under the new key, from the name in the vault's meta. Writes nothing. A slot that doesn't open is skipped and
    /// logged (it opened nothing before either); one whose meta doesn't open is wrapped again without a name hash and
    /// logged; one without a vault document is an orphan of a creation that failed half way, to be deleted if older
    /// than <see cref="OrphanGrace"/>, else <see cref="Accounts.RotationConflictException"/>: it may belong to a
    /// creation still under way. <c>VaultIds</c> are the vaults of the user's <c>user</c> slots, for stage 2.
    /// Static, on the collections, so that the rotation needs no <see cref="VaultCatalog"/> (rekeying design §9).
    /// </summary>
    internal static async Task<PreparedUserSlots> PrepareUserSlotsAsync(
        IMongoDatabase db, string userId, byte[] oldUserKey, byte[] newUserKey, DateTime now, ILogger log, CancellationToken ct)
    {
        var slots = await db.GetCollection<SlotDocument>("slots").Find(Builders<SlotDocument>.Filter.Eq(s => s.Lookup, UserLookup(oldUserKey, userId))).ToListAsync(ct);
        var ids = slots.Select(s => s.Vault).Distinct().ToList();
        var vaults = (await db.GetCollection<VaultDocument>("vaults").Find(Builders<VaultDocument>.Filter.In(v => v.Id, ids)).ToListAsync(ct)).ToDictionary(v => v.Id);
        var nameKey = VaultCrypto.Derive(newUserKey, VaultNameLabel, []);
        var (rewraps, skipped, orphans, vaultIds) = (new List<Accounts.SlotRewrap>(), new List<MongoDB.Bson.ObjectId>(), new List<MongoDB.Bson.ObjectId>(), new List<string>());
        foreach (var slot in slots)
        {
            byte[] vaultKey;
            try
            {
                vaultKey = VaultCrypto.Open(UserKek(oldUserKey, slot.Vault), slot.Wrapped, SlotContext(slot.Vault, slot.Kind));
            }
            catch (CryptographicException)
            {
                log.LogWarning("A slot of vault {VaultId} matched the user key of user {UserId} but did not open; it is left as it is", slot.Vault, userId);
                skipped.Add(slot.Id);
                continue;
            }

            if (!vaults.TryGetValue(slot.Vault, out var vault))
            {
                if (now - slot.CreatedAt < OrphanGrace) throw new Accounts.RotationConflictException();
                orphans.Add(slot.Id);
                continue;
            }

            byte[]? nameId = null;
            if (slot.Kind == SlotDocument.User)
            {
                vaultIds.Add(slot.Vault);
                try
                {
                    var meta = JsonSerializer.Deserialize<VaultMeta>(VaultCrypto.Open(VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), vault.Meta, $"vault:{vault.Id}"))!;
                    nameId = VaultCrypto.Mac(nameKey, NormaliseVaultName(meta.Name));
                }
                catch (Exception e) when (e is CryptographicException or JsonException)
                {
                    log.LogWarning("The meta of vault {VaultId} of user {UserId} does not open; its slot is wrapped again without its name", slot.Vault, userId);
                }
            }

            rewraps.Add(new Accounts.SlotRewrap(slot.Id, slot.Wrapped, VaultCrypto.Seal(UserKek(newUserKey, slot.Vault), vaultKey, SlotContext(slot.Vault, slot.Kind)), nameId));
        }

        return new PreparedUserSlots(rewraps, skipped, orphans, [.. vaultIds.Distinct()]);
    }
}

/// <summary>The user already has a vault of this name.</summary>
public sealed class VaultNameTakenException(string name) : Exception($"You already have a vault named '{name}'.");
