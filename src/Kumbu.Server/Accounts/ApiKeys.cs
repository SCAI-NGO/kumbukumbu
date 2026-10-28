// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kumbu.Core.Crypto;
using Kumbu.Server.Vaults;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

public enum KeyRight
{
    Read,
    Write,
}

public static class KeyRights
{
    public static string ToText(KeyRight right) => right == KeyRight.Write ? "write" : "read";

    public static KeyRight? Parse(string text) => text switch
    {
        "read" => KeyRight.Read,
        "write" => KeyRight.Write,
        _ => null,
    };
}

/// <summary>Who holds a key (spec §5a): an agent, or one of the user's own devices, paired from Obsidian. A device's
/// writes are the user's own in the web app's views.</summary>
public enum ApiKeyKind
{
    Agent,
    Device,
}

/// <summary>An API key as its owner sees it in a list; never the key itself, which is shown once at creation.
/// <c>ExpiresAt</c> is null for a key that doesn't expire.</summary>
public sealed record ApiKeyInfo(
    string Id, string Name, string Prefix, IReadOnlyDictionary<string, KeyRight> Vaults, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt,
    DateTime? ExpiresAt = null, ApiKeyKind Kind = ApiKeyKind.Agent, WriteResumption? Resumed = null);

/// <summary>A key that was presented and checked: whose it is, the name its writes carry, and whether a device holds it.</summary>
public sealed record ResolvedApiKey(User User, string Id, string Name, ApiKeyKind Kind = ApiKeyKind.Agent);

/// <summary>
/// API keys (accounts design, "API keys"): <c>kumbu_</c> and 32 random bytes in Base62, shown once. Each key has
/// a slot on every vault it was given (<see cref="VaultCatalog.ApiKeySlotsAsync"/>), with the vault key and the
/// right sealed together under a key derived from the API key, so a key opens exactly its vaults and a right can't
/// be changed in the database. MongoDB holds only values derived from the key; the key's name and vaults are
/// sealed under the user key for the owner's list, and the name once more under the API key, for its writer label.
/// </summary>
public sealed class ApiKeys(IMongoDatabase db, VaultCatalog catalog, TimeProvider time, ILogger<ApiKeys> log)
{
    /// <summary>Followed by 32 random bytes in Base62, 43 characters (<see cref="Secrets"/>).</summary>
    public const string KeyPrefix = "kumbu_";

    private const int PrefixLength = 8;

    private const string LookupLabel = "kumbu/v1/api-key-lookup";
    private const string WriterLabel = "kumbu/v1/api-key-writer";
    private const string MetaLabel = "kumbu/v1/api-key-meta";

    /// <summary>The lifetimes a key can be given at creation, in days; without one it never expires.</summary>
    public static readonly IReadOnlySet<int> ExpiryDays = new HashSet<int> { 30, 90, 365 };

    /// <summary>A burst of requests shouldn't write on each; "last used" may therefore lag by up to this much.</summary>
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(1);

    private volatile bool _ready;

    internal const string CollectionName = "api_keys";

    private IMongoCollection<ApiKeyDocument> Keys { get; } = db.GetCollection<ApiKeyDocument>(CollectionName);

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Creates a key for <paramref name="vaults"/> (vault id → right), which must all be the user's own; returns
    /// the key, the only time it is shown. Throws <see cref="ArgumentException"/> for an unusable name, no vaults or
    /// a vault that isn't the user's, <see cref="ApiKeyNameTakenException"/> if one of the user's keys that isn't
    /// revoked has that name (compared case-insensitively), and <see cref="CredentialsChangedException"/> if the
    /// user's credentials changed at or after <paramref name="credentialReadAt"/> (when the session's password was
    /// read) or the user is inactive. With <paramref name="expiresInDays"/> (one of <see cref="ExpiryDays"/>, else
    /// <see cref="ArgumentException"/>) the key is refused from that moment on, like a revoked one. The
    /// <paramref name="kind"/> is sealed into the meta, so it can't be changed afterwards.
    /// </summary>
    public async Task<(string Key, ApiKeyInfo Info)> CreateAsync(
        User user, byte[] userKey, DateTime credentialReadAt, string name, IReadOnlyDictionary<string, KeyRight> vaults, CancellationToken ct,
        int? expiresInDays = null, ApiKeyKind kind = ApiKeyKind.Agent)
    {
        var clean = WriterNames.Check(user, name, "An API key");
        if (vaults.Count == 0) throw new ArgumentException("An API key needs at least one vault.", nameof(vaults));
        if (expiresInDays is { } days && !ExpiryDays.Contains(days))
        {
            throw new ArgumentException($"An API key expires after {string.Join(", ", ExpiryDays.Order())} days, or never.", nameof(expiresInDays));
        }

        await EnsureReadyAsync(ct);

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var key = Secrets.New(KeyPrefix);
        var (slots, vaultKeys) = await catalog.ApiKeySlotsAsync(user.Id, userKey, id, key, vaults, ct);
        // Written for devices only: an agent key's meta stays what every key's was before kinds existed.
        var meta = new ApiKeyMeta(clean, vaults.ToDictionary(v => v.Key, v => KeyRights.ToText(v.Value)), kind == ApiKeyKind.Device ? DeviceKind : null);
        // Whole milliseconds, as MongoDB keeps them: the time is bound into the writer seal and must read back equal.
        var expiresAt = expiresInDays is { } d ? Milliseconds(Now.AddDays(d)) : (DateTime?)null;
        // The meta and name id are sealed under this user key: it must still be the one in force (UserKeyRotation).
        var keyId = UserCatalog.KeyId(userKey, user.Id);
        var doc = new ApiKeyDocument
        {
            Id = id,
            User = user.Id,
            Lookup = Lookup(key),
            Prefix = key.Substring(KeyPrefix.Length, PrefixLength),
            NameId = WriterNames.NameId(userKey, user.Id, clean),
            Meta = VaultCrypto.Seal(MetaKey(userKey, user.Id), JsonSerializer.SerializeToUtf8Bytes(meta), MetaContext(id)),
            Writer = VaultCrypto.Seal(WriterKey(key), Encoding.UTF8.GetBytes(clean), WriterContext(id, expiresAt, kind)),
            CreatedAt = Now,
            ExpiresAt = expiresAt,
        };

        // The key and its slots in one transaction: a key never exists with only some of its vaults. The session
        // was checked when the request began; a password change, recovery or deactivation may have landed since,
        // and a key made now would outlive it. So the user document is checked again and written in the same
        // transaction: either this commits first, and the change's revocation (which runs after its stamp) finds
        // the key, or the change's stamp comes first, and the condition fails or the write conflicts (retried by
        // WithTransactionAsync, then failing the condition).
        using (var session = await catalog.StartSessionAsync(ct))
        {
            try
            {
                await session.WithTransactionAsync(
                    async (s, token) =>
                    {
                        var current = await Users.UpdateOneAsync(
                            s,
                            u => u.Id == user.Id && u.Active && (u.CredentialsChangedAt == null || u.CredentialsChangedAt < credentialReadAt) && u.KeyId == keyId,
                            Builders<UserDocument>.Update.Inc(u => u.KeysCreated, 1),
                            cancellationToken: token);
                        if (current.MatchedCount == 0) throw new CredentialsChangedException();
                        // Names are unique across keys and connections. A connection made at the same time with
                        // this name writes the user document too: one of the two conflicts and, retried, sees the other.
                        if (await Connections.NameTakenAsync(s, db, user.Id, doc.NameId, Now, token)) throw new ApiKeyNameTakenException(clean);
                        // The slots were wrapped before the transaction: a vault deleted since is seen here, or its
                        // deletion conflicts on the user document written above, so no slot outlives its vault.
                        await catalog.RequireVaultsAsync(s, [.. vaults.Keys], "An API key", token);
                        // A re-keying of a vault that started or finished since its key was opened: the slot would
                        // carry the old key. Its start and finish write the user document (a rise of vaultsRekeyed),
                        // which conflicts with the rise of keysCreated above, so a retry reads them.
                        await catalog.RequireSameVaultKeysAsync(s, user.Id, userKey, vaultKeys, token);
                        await RetireExpiredAsync(s, user.Id, token);
                        await Keys.InsertOneAsync(s, doc, cancellationToken: token);
                        await catalog.Slots.InsertManyAsync(s, slots, cancellationToken: token);
                        return true;
                    },
                    cancellationToken: ct);
            }
            catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new ApiKeyNameTakenException(clean);
            }
        }

        if (expiresAt is null) log.LogInformation("User {Username} created API key {KeyId} for {Count} vaults", user.Username, id, vaults.Count);
        else log.LogInformation("User {Username} created API key {KeyId} for {Count} vaults, expiring {ExpiresAt:u}", user.Username, id, vaults.Count, expiresAt);
        return (key, Info(doc, meta));
    }

    /// <summary>The user's keys, revoked ones included (their names still mark their writes in the history), oldest first.</summary>
    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(User user, byte[] userKey, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var metaKey = MetaKey(userKey, user.Id);
        var result = new List<ApiKeyInfo>();
        foreach (var doc in await Keys.Find(k => k.User == user.Id).SortBy(k => k.CreatedAt).ToListAsync(ct))
        {
            try
            {
                result.Add(Info(doc, JsonSerializer.Deserialize<ApiKeyMeta>(VaultCrypto.Open(metaKey, doc.Meta, MetaContext(doc.Id)))!));
            }
            catch (Exception e) when (e is CryptographicException or JsonException)
            {
                log.LogWarning("API key {KeyId} of user {Username} does not open with the user key", doc.Id, user.Username);
            }
        }

        return result;
    }

    /// <summary>Revokes one of the user's keys: marked revoked and its slots deleted, in one transaction, so it
    /// opens nothing from then on, also for a vault still in memory. False if the user has no such key that isn't revoked.</summary>
    public async Task<bool> RevokeAsync(User user, string keyId, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        using var session = await catalog.StartSessionAsync(ct);
        session.StartTransaction();
        var marked = await Keys.UpdateOneAsync(
            session,
            k => k.Id == keyId && k.User == user.Id && k.RevokedAt == null,
            // Without its name id the name is free for a new key.
            Builders<ApiKeyDocument>.Update.Set(k => k.RevokedAt, Now).Unset(k => k.NameId),
            cancellationToken: ct);
        if (marked.MatchedCount == 0)
        {
            await session.AbortTransactionAsync(CancellationToken.None);
            return false;
        }

        await catalog.DeleteApiKeySlotsAsync(session, keyId, ct);
        await session.CommitTransactionAsync(CancellationToken.None);
        log.LogInformation("User {Username} revoked API key {KeyId}", user.Username, keyId);
        return true;
    }

    /// <summary>
    /// Revokes every key of the user that isn't revoked yet, in one transaction, the way <see cref="RevokeAsync"/>
    /// revokes one: after a password change that asks for it, and after a recovery. Returns how many.
    /// </summary>
    public async Task<int> RevokeAllAsync(User user, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        int revoked;
        using (var session = await catalog.StartSessionAsync(ct))
        {
            // WithTransactionAsync retries a write conflict, which is a real possibility here: ResolveAsync writes
            // lastUsedAt on these very documents, outside any transaction, whenever a key is used.
            revoked = await session.WithTransactionAsync((s, token) => RevokeAllAsync(s, user.Id, token), cancellationToken: ct);
        }

        if (revoked > 0) log.LogInformation("User {Username} revoked all {Count} API keys", user.Username, revoked);
        return revoked;
    }

    /// <summary>
    /// Revokes every key of the user that isn't revoked yet, inside the caller's transaction (deactivating the
    /// user): marked revoked, names freed and slots deleted, so reactivating the user revives none of them.
    /// </summary>
    internal async Task<int> RevokeAllAsync(IClientSessionHandle session, string userId, CancellationToken ct)
    {
        // No EnsureReadyAsync here: building an index while this transaction is open would wait for the transaction.
        var ids = await Keys.Find(session, k => k.User == userId && k.RevokedAt == null).Project(k => k.Id).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        await Keys.UpdateManyAsync(
            session,
            Builders<ApiKeyDocument>.Filter.In(k => k.Id, ids),
            Builders<ApiKeyDocument>.Update.Set(k => k.RevokedAt, Now).Unset(k => k.NameId),
            cancellationToken: ct);
        foreach (var id in ids) await catalog.DeleteApiKeySlotsAsync(session, id, ct);
        return ids.Count;
    }

    /// <summary>
    /// Inside a key's creation: the user's expired keys lose their name id (so the name is free again, as after a
    /// revocation) and their slots. They are refused whether or not this ran; it only tidies up.
    /// </summary>
    private async Task RetireExpiredAsync(IClientSessionHandle session, string userId, CancellationToken ct)
    {
        var now = Now;
        var ids = await Keys.Find(session, k => k.User == userId && k.RevokedAt == null && k.ExpiresAt != null && k.ExpiresAt <= now && k.NameId != null)
            .Project(k => k.Id).ToListAsync(ct);
        if (ids.Count == 0) return;
        await Keys.UpdateManyAsync(session, Builders<ApiKeyDocument>.Filter.In(k => k.Id, ids), Builders<ApiKeyDocument>.Update.Unset(k => k.NameId), cancellationToken: ct);
        foreach (var id in ids) await catalog.DeleteApiKeySlotsAsync(session, id, ct);
    }

    /// <summary>
    /// Every user's expired keys that aren't retired yet lose their slots and their name id, as the first time one is
    /// presented again: run at start and every hour (<see cref="ExpiredKeySweep"/>), so that a key never presented
    /// again doesn't keep its slots until its owner makes another key. A slot of an expired key opens a vault for nobody
    /// (the key is refused before its slots are read), so deleting it loses nothing. Returns how many keys.
    /// </summary>
    public async Task<int> RetireAllExpiredAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var now = Now;
        var expired = await Keys.Find(k => k.RevokedAt == null && k.ExpiresAt != null && k.ExpiresAt <= now && k.NameId != null).ToListAsync(ct);
        foreach (var doc in expired) await RetireExpiredAsync(doc, ct);
        return expired.Count;
    }

    /// <summary>
    /// The first time an expired key is presented: its slots go and its name is freed, as when the next key is made.
    /// Best effort and outside a transaction: the slots first, so the name id, which marks a key not yet retired,
    /// goes only once they are gone; the key is refused either way.
    /// </summary>
    private async Task RetireExpiredAsync(ApiKeyDocument doc, CancellationToken ct)
    {
        if (doc.NameId is null) return;
        await catalog.DeleteApiKeySlotsAsync(doc.Id, ct);
        await Keys.UpdateOneAsync(k => k.Id == doc.Id, Builders<ApiKeyDocument>.Update.Unset(k => k.NameId), cancellationToken: ct);
        log.LogInformation("API key {KeyId} of user {UserId} has expired; its slots are deleted", doc.Id, doc.User);
    }

    /// <summary>
    /// The key behind a presented bearer, or null if it isn't one, is unknown, revoked or expired, or its user is
    /// inactive. Never logs anything of the key. The rights are not checked here: they live in the slots.
    /// </summary>
    public async Task<ResolvedApiKey?> ResolveAsync(string presented, CancellationToken ct)
    {
        if (!Secrets.IsWellFormed(presented, KeyPrefix)) return null;
        await EnsureReadyAsync(ct);
        var lookup = Lookup(presented);
        var doc = await Keys.Find(k => k.Lookup == lookup).FirstOrDefaultAsync(ct);
        var now = Now;
        if (doc is null || doc.RevokedAt is not null) return null;
        if (now >= doc.ExpiresAt)
        {
            await RetireExpiredAsync(doc, ct);
            return null;
        }

        var user = await Users.Find(u => u.Id == doc.User).FirstOrDefaultAsync(ct);
        if (user is null || !user.Active) return null;

        // The expiry and the kind are bound into this seal: changed or removed in the database, the name doesn't open.
        // The meta's kind is sealed under the user key, which a request with the key doesn't have.
        string? name = null;
        var kind = ApiKeyKind.Agent;
        foreach (var candidate in (ApiKeyKind[])[ApiKeyKind.Agent, ApiKeyKind.Device])
        {
            try
            {
                name = Encoding.UTF8.GetString(VaultCrypto.Open(WriterKey(presented), doc.Writer, WriterContext(doc.Id, doc.ExpiresAt, candidate)));
                kind = candidate;
                break;
            }
            catch (CryptographicException)
            {
            }
        }

        if (name is null)
        {
            log.LogWarning("API key {KeyId} matched but its name does not open", doc.Id);
            return null;
        }

        if (doc.LastUsedAt is not { } lastUsed || now - lastUsed >= LastUsedGranularity)
        {
            // Conditional, so of concurrent requests only one writes.
            var stale = now - LastUsedGranularity;
            await Keys.UpdateOneAsync(
                k => k.Id == doc.Id && (k.LastUsedAt == null || k.LastUsedAt <= stale),
                Builders<ApiKeyDocument>.Update.Set(k => k.LastUsedAt, now),
                cancellationToken: ct);
        }

        return new ResolvedApiKey(user.ToUser(), doc.Id, name, kind);
    }

    /// <summary>Whether the key may still be used: not revoked, not expired, its owner active. For a WebSocket held
    /// open past the request that made its ticket (<see cref="Sync.SyncHub"/>).</summary>
    public async Task<bool> IsActiveAsync(string keyId, CancellationToken ct)
    {
        var doc = await Keys.Find(k => k.Id == keyId).FirstOrDefaultAsync(ct);
        if (doc is null || doc.RevokedAt is not null || Now >= doc.ExpiresAt) return false;
        return await Users.Find(u => u.Id == doc.User && u.Active).AnyAsync(ct);
    }

    /// <summary>
    /// Inside a connection's creation: whether a key of the user that is neither revoked nor expired has the name
    /// with <paramref name="nameId"/>. An expired key's name is free even before it is retired.
    /// </summary>
    internal static Task<bool> NameTakenAsync(IClientSessionHandle session, IMongoDatabase db, string userId, byte[]? nameId, DateTime now, CancellationToken ct)
    {
        var f = Builders<ApiKeyDocument>.Filter;
        return db.GetCollection<ApiKeyDocument>(CollectionName)
            .Find(session, f.Eq(k => k.User, userId) & f.Eq(k => k.NameId, nameId) & f.Eq(k => k.RevokedAt, null) & (f.Eq(k => k.ExpiresAt, null) | f.Gt(k => k.ExpiresAt, now)))
            .AnyAsync(ct);
    }

    public static bool LooksLikeKey(string presented) => presented.StartsWith(KeyPrefix, StringComparison.Ordinal);

    private static ApiKeyInfo Info(ApiKeyDocument doc, ApiKeyMeta meta) => new(
        doc.Id,
        meta.Name,
        doc.Prefix,
        meta.Vaults.Where(v => KeyRights.Parse(v.Value) is not null).ToDictionary(v => v.Key, v => KeyRights.Parse(v.Value)!.Value),
        doc.CreatedAt,
        doc.LastUsedAt,
        doc.RevokedAt,
        doc.ExpiresAt,
        meta.Kind == DeviceKind ? ApiKeyKind.Device : ApiKeyKind.Agent, Resumption(doc));

    private const string DeviceKind = "device";

    /// <summary>Finds the key without storing it; HKDF suffices, as the key is random.</summary>
    private static byte[] Lookup(string key) => VaultCrypto.Derive(key, LookupLabel, []);

    private static byte[] WriterKey(string key) => VaultCrypto.Derive(key, WriterLabel, []);

    private static byte[] MetaKey(byte[] userKey, string userId) => VaultCrypto.Derive(userKey, MetaLabel, Encoding.UTF8.GetBytes(userId));

    private static string MetaContext(string keyId) => $"api-key:{keyId}:meta";

    /// <summary>For tests: a meta sealed as <see cref="CreateAsync"/> seals it.</summary>
    internal static byte[] SealMeta(byte[] userKey, string userId, string keyId, byte[] plain) => VaultCrypto.Seal(MetaKey(userKey, userId), plain, MetaContext(keyId));

    /// <summary>For tests: a meta's plaintext.</summary>
    internal static byte[] OpenMeta(byte[] userKey, string userId, string keyId, byte[] sealedMeta) => VaultCrypto.Open(MetaKey(userKey, userId), sealedMeta, MetaContext(keyId));

    /// <summary>A key without expiry keeps the context every key had before expiry existed, and an agent's the one every
    /// key had before kinds existed. A device key paired before then reads as an agent's here (only its changesets differ).</summary>
    private static string WriterContext(string keyId, DateTime? expiresAt, ApiKeyKind kind) =>
        (expiresAt is { } at ? $"api-key:{keyId}:writer:{new DateTimeOffset(at.Ticks, TimeSpan.Zero).ToUnixTimeMilliseconds()}" : $"api-key:{keyId}:writer")
        + (kind == ApiKeyKind.Device ? ":device" : "");

    private static DateTime Milliseconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);

    /// <summary>Creates the indexes once per process; lazy, so the server starts before MongoDB is reachable.</summary>
    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        var keys = Builders<ApiKeyDocument>.IndexKeys;
        await Keys.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<ApiKeyDocument>(keys.Ascending(k => k.Lookup), new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<ApiKeyDocument>(keys.Ascending(k => k.User)),
                // One name per user among keys that aren't revoked: revoking unsets the name id.
                new CreateIndexModel<ApiKeyDocument>(
                    keys.Ascending(k => k.User).Ascending(k => k.NameId),
                    new CreateIndexOptions<ApiKeyDocument>
                    {
                        Unique = true,
                        PartialFilterExpression = new MongoDB.Bson.BsonDocument("nameId", new MongoDB.Bson.BsonDocument("$type", "binData")),
                    }),
            ],
            ct);
        _ready = true;
    }

    /// <summary>
    /// For a rotation of the user key (<see cref="UserKeyRotation"/>): every key of the user, revoked and expired ones
    /// included, with its meta sealed again under <paramref name="newUserKey"/> and its name id recomputed where it is
    /// set. A meta that doesn't open is logged and kept as it is (new values equal to the old). Writes nothing.
    /// Static, on the collection, so that <see cref="UserKeyRotation"/> needs neither this class nor its <see cref="VaultCatalog"/>,
    /// as <see cref="ConnectionRevocation"/> does.
    /// </summary>
    internal static async Task<IReadOnlyList<MetaReseal>> PrepareResealAsync(
        IMongoDatabase db, string userId, byte[] oldUserKey, byte[] newUserKey, ILogger log, CancellationToken ct)
    {
        var (oldKey, newKey) = (MetaKey(oldUserKey, userId), MetaKey(newUserKey, userId));
        var result = new List<MetaReseal>();
        foreach (var doc in await db.GetCollection<ApiKeyDocument>(CollectionName).Find(k => k.User == userId).ToListAsync(ct))
        {
            try
            {
                var plain = VaultCrypto.Open(oldKey, doc.Meta, MetaContext(doc.Id));
                var name = JsonSerializer.Deserialize<ApiKeyMeta>(plain)!.Name;
                result.Add(new MetaReseal(
                    doc.Id, doc.Meta, doc.NameId, VaultCrypto.Seal(newKey, plain, MetaContext(doc.Id)), doc.NameId is null ? null : WriterNames.NameId(newUserKey, userId, name)));
            }
            catch (Exception e) when (e is CryptographicException or JsonException)
            {
                log.LogWarning("API key {KeyId} of user {UserId} does not open with the user key; it is left as it is", doc.Id, userId);
                result.Add(new MetaReseal(doc.Id, doc.Meta, doc.NameId, doc.Meta, doc.NameId));
            }
        }

        return result;
    }

    private sealed record ApiKeyMeta(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("vaults")] Dictionary<string, string> Vaults,
        [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null);

    /// <summary>A key that wrote nothing for longer than this and writes again has resumed (<see cref="RecordWriteAsync"/>).</summary>
    public static readonly TimeSpan LongPause = TimeSpan.FromDays(14);

    /// <summary>
    /// After a write by the key has committed: its <c>lastWriteAt</c>, at most once a minute like <c>lastUsedAt</c>
    /// (a device polls all the time, so only a write says that someone wrote with it). A write more than
    /// <see cref="LongPause"/> after the previous one is a resumption, kept until the next one: the web app makes a
    /// device's writes since then stand out. A key's first write starts the record and resumes nothing. Best effort:
    /// the write stands whatever happens here.
    /// </summary>
    public async Task RecordWriteAsync(string keyId)
    {
        try
        {
            var now = Now;
            var last = await Keys.Find(k => k.Id == keyId).Project(k => k.LastWriteAt).FirstOrDefaultAsync(CancellationToken.None);
            if (last is { } previous && now - previous < LastUsedGranularity) return;
            var update = Builders<ApiKeyDocument>.Update.Set(k => k.LastWriteAt, now);
            if (last is { } quiet && now - quiet > LongPause) update = update.Set(k => k.QuietSince, quiet).Set(k => k.ResumedAt, now);
            // Conditional on the value read, so of concurrent writes only one records a resumption.
            await Keys.UpdateOneAsync(k => k.Id == keyId && k.LastWriteAt == last, update, cancellationToken: CancellationToken.None);
        }
        catch (MongoException e)
        {
            log.LogWarning(e, "The write of API key {KeyId} could not be recorded", keyId);
        }
    }

    private static WriteResumption? Resumption(ApiKeyDocument doc) =>
        doc is { QuietSince: { } quiet, ResumedAt: { } resumed } ? new WriteResumption(quiet, resumed) : null;
}

/// <summary>A key wrote again at <paramref name="At"/> after writing nothing since <paramref name="QuietSince"/>, more
/// than <see cref="ApiKeys.LongPause"/> before: its latest return after a long pause (<see cref="ApiKeys.RecordWriteAsync"/>).</summary>
public sealed record WriteResumption(DateTime QuietSince, DateTime At);

/// <summary>An API key (collection <c>api_keys</c>). Nothing here is the key; name and vaults are sealed.</summary>
[BsonIgnoreExtraElements]
public sealed class ApiKeyDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("user")]
    public string User { get; set; } = "";

    [BsonElement("lookup")]
    public byte[] Lookup { get; set; } = [];

    /// <summary>The first characters after <c>kumbu_</c>, so the owner can tell their keys apart in the list.</summary>
    [BsonElement("prefix")]
    public string Prefix { get; set; } = "";

    /// <summary>Until revoked: a keyed hash of the name, for the unique index.</summary>
    [BsonElement("nameId"), BsonIgnoreIfNull]
    public byte[]? NameId { get; set; }

    /// <summary>Name and vaults with their rights, sealed under the user key: for the owner's list only. The
    /// rights that count are those sealed into the slots.</summary>
    [BsonElement("meta")]
    public byte[] Meta { get; set; } = [];

    /// <summary>The name, sealed under the API key: the writer label of the key's writes.</summary>
    [BsonElement("writer")]
    public byte[] Writer { get; set; } = [];

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    [BsonElement("lastUsedAt"), BsonIgnoreIfNull]
    public DateTime? LastUsedAt { get; set; }

    [BsonElement("revokedAt"), BsonIgnoreIfNull]
    public DateTime? RevokedAt { get; set; }

    /// <summary>The key's latest write, up to a minute late (<see cref="ApiKeys.RecordWriteAsync"/>).</summary>
    [BsonElement("lastWriteAt"), BsonIgnoreIfNull]
    public DateTime? LastWriteAt { get; set; }

    /// <summary>With <see cref="ResumedAt"/>: the key's latest return after a long pause in writing, and its last write before it.</summary>
    [BsonElement("quietSince"), BsonIgnoreIfNull]
    public DateTime? QuietSince { get; set; }

    [BsonElement("resumedAt"), BsonIgnoreIfNull]
    public DateTime? ResumedAt { get; set; }

    /// <summary>From then on the key is refused, as if revoked. Bound into the seal of <see cref="Writer"/>.</summary>
    [BsonElement("expiresAt"), BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }
}

public sealed class ApiKeyNameTakenException(string name) : Exception($"You already have an API key or connection named '{name}'.");

/// <summary>The session that asked is older than the user's last password change or recovery, or the user was
/// deactivated meanwhile: it may not make anything that outlives it.</summary>
public sealed class CredentialsChangedException() : Exception("Your session has ended; sign in again.");
