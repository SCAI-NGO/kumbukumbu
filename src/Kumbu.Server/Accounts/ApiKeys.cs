// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
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

/// <summary>An API key as its owner sees it in a list; never the key itself, which is shown once at creation.</summary>
public sealed record ApiKeyInfo(
    string Id, string Name, string Prefix, IReadOnlyDictionary<string, KeyRight> Vaults, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

/// <summary>A key that was presented and checked: whose it is, and the name its writes carry.</summary>
public sealed record ResolvedApiKey(User User, string Id, string Name);

/// <summary>
/// API keys (accounts design, "API keys"): <c>kumbu_</c> and 32 random bytes in Base62, shown once. Each key has
/// a slot on every vault it was given (<see cref="VaultCatalog.ApiKeySlotsAsync"/>), with the vault key and the
/// right sealed together under a key derived from the API key, so a key opens exactly its vaults and a right can't
/// be changed in the database. MongoDB holds only values derived from the key; the key's name and vaults are
/// sealed under the user key for the owner's list, and the name once more under the API key, for its writer label.
/// </summary>
public sealed class ApiKeys(IMongoDatabase db, VaultCatalog catalog, TimeProvider time, ILogger<ApiKeys> log)
{
    public const string KeyPrefix = "kumbu_";

    private const int SecretBytes = 32;

    /// <summary>62^43 > 2^256 > 62^42: every 32 bytes fit in 43 digits.</summary>
    private const int Base62Length = 43;

    private const int PrefixLength = 8;
    private const int MaxNameLength = 64;

    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const string LookupLabel = "kumbu/v1/api-key-lookup";
    private const string WriterLabel = "kumbu/v1/api-key-writer";
    private const string MetaLabel = "kumbu/v1/api-key-meta";
    private const string NameIdLabel = "kumbu/v1/api-key-name-id";

    /// <summary>A burst of requests shouldn't write on each; "last used" may therefore lag by up to this much.</summary>
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(1);

    private volatile bool _ready;

    private IMongoCollection<ApiKeyDocument> Keys { get; } = db.GetCollection<ApiKeyDocument>("api_keys");

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Creates a key for <paramref name="vaults"/> (vault id → right), which must all be the user's own; returns
    /// the key, the only time it is shown. Throws <see cref="ArgumentException"/> for an unusable name, no vaults or
    /// a vault that isn't the user's, <see cref="ApiKeyNameTakenException"/> if one of the user's keys that isn't
    /// revoked has that name (compared case-insensitively), and <see cref="CredentialsChangedException"/> if the
    /// user's credentials changed at or after <paramref name="credentialReadAt"/> (when the session's password was
    /// read) or the user is inactive.
    /// </summary>
    public async Task<(string Key, ApiKeyInfo Info)> CreateAsync(
        User user, byte[] userKey, DateTime credentialReadAt, string name, IReadOnlyDictionary<string, KeyRight> vaults, CancellationToken ct)
    {
        var clean = CheckName(user, name);
        if (vaults.Count == 0) throw new ArgumentException("An API key needs at least one vault.", nameof(vaults));
        await EnsureReadyAsync(ct);

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var key = KeyPrefix + EncodeBase62(RandomNumberGenerator.GetBytes(SecretBytes));
        var slots = await catalog.ApiKeySlotsAsync(user.Id, userKey, id, key, vaults, ct);
        var meta = new ApiKeyMeta(clean, vaults.ToDictionary(v => v.Key, v => KeyRights.ToText(v.Value)));
        var doc = new ApiKeyDocument
        {
            Id = id,
            User = user.Id,
            Lookup = Lookup(key),
            Prefix = key.Substring(KeyPrefix.Length, PrefixLength),
            NameId = NameId(userKey, user.Id, clean),
            Meta = VaultCrypto.Seal(MetaKey(userKey, user.Id), JsonSerializer.SerializeToUtf8Bytes(meta), MetaContext(id)),
            Writer = VaultCrypto.Seal(WriterKey(key), Encoding.UTF8.GetBytes(clean), WriterContext(id)),
            CreatedAt = Now,
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
                            u => u.Id == user.Id && u.Active && (u.CredentialsChangedAt == null || u.CredentialsChangedAt < credentialReadAt),
                            Builders<UserDocument>.Update.Inc(u => u.KeysCreated, 1),
                            cancellationToken: token);
                        if (current.MatchedCount == 0) throw new CredentialsChangedException();
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

        log.LogInformation("User {Username} created API key {KeyId} for {Count} vaults", user.Username, id, vaults.Count);
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
    /// The key behind a presented bearer, or null if it isn't one, is unknown or revoked, or its user is inactive.
    /// Never logs anything of the key. The rights are not checked here: they live in the slots.
    /// </summary>
    public async Task<ResolvedApiKey?> ResolveAsync(string presented, CancellationToken ct)
    {
        if (!IsWellFormed(presented)) return null;
        await EnsureReadyAsync(ct);
        var lookup = Lookup(presented);
        var doc = await Keys.Find(k => k.Lookup == lookup).FirstOrDefaultAsync(ct);
        if (doc is null || doc.RevokedAt is not null) return null;

        var user = await Users.Find(u => u.Id == doc.User).FirstOrDefaultAsync(ct);
        if (user is null || !user.Active) return null;

        string name;
        try
        {
            name = Encoding.UTF8.GetString(VaultCrypto.Open(WriterKey(presented), doc.Writer, WriterContext(doc.Id)));
        }
        catch (CryptographicException)
        {
            log.LogWarning("API key {KeyId} matched but its name does not open", doc.Id);
            return null;
        }

        var now = Now;
        if (doc.LastUsedAt is not { } lastUsed || now - lastUsed >= LastUsedGranularity)
        {
            // Conditional, so of concurrent requests only one writes.
            var stale = now - LastUsedGranularity;
            await Keys.UpdateOneAsync(
                k => k.Id == doc.Id && (k.LastUsedAt == null || k.LastUsedAt <= stale),
                Builders<ApiKeyDocument>.Update.Set(k => k.LastUsedAt, now),
                cancellationToken: ct);
        }

        return new ResolvedApiKey(user.ToUser(), doc.Id, name);
    }

    public static bool LooksLikeKey(string presented) => presented.StartsWith(KeyPrefix, StringComparison.Ordinal);

    private static bool IsWellFormed(string presented) =>
        presented.Length == KeyPrefix.Length + Base62Length && LooksLikeKey(presented) && presented.AsSpan(KeyPrefix.Length).IndexOfAnyExcept(Base62) < 0;

    /// <summary>
    /// The name, trimmed. Its writes carry it as their writer label, so it must stay apart from the other labels:
    /// not the user's own username (their writes in the web app) nor <c>dev</c> or <c>seed</c>, and without
    /// <c>" ("</c>, which the history puts between a writer and the tool ("laptop (mv)").
    /// </summary>
    private static string CheckName(User user, string name)
    {
        var clean = name.Normalize(NormalizationForm.FormC).Trim();
        if (clean.Length is 0 or > MaxNameLength) throw new ArgumentException($"An API key's name has 1 to {MaxNameLength} characters.", nameof(name));
        // Format characters (zero-width, bidi overrides) are invisible too: "chris\u200B" would show as "chris".
        if (clean.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
        {
            throw new ArgumentException("An API key's name can't contain control or invisible formatting characters.", nameof(name));
        }
        if (clean.Contains(" (", StringComparison.Ordinal)) throw new ArgumentException("An API key's name can't contain \" (\".", nameof(name));
        if (string.Equals(clean, user.Username, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An API key can't be named like you: its writes would look like your own.", nameof(name));
        }

        if (clean.Equals(Writers.Dev, StringComparison.OrdinalIgnoreCase) || clean.Equals(Writers.Seed, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{clean}' is reserved for the server's own writes.", nameof(name));
        }

        return clean;
    }

    private static string EncodeBase62(byte[] bytes)
    {
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var digits = new char[Base62Length];
        for (var i = Base62Length - 1; i >= 0; i--)
        {
            value = BigInteger.DivRem(value, 62, out var digit);
            digits[i] = Base62[(int)digit];
        }

        return new string(digits);
    }

    private static ApiKeyInfo Info(ApiKeyDocument doc, ApiKeyMeta meta) => new(
        doc.Id,
        meta.Name,
        doc.Prefix,
        meta.Vaults.Where(v => KeyRights.Parse(v.Value) is not null).ToDictionary(v => v.Key, v => KeyRights.Parse(v.Value)!.Value),
        doc.CreatedAt,
        doc.LastUsedAt,
        doc.RevokedAt);

    /// <summary>Finds the key without storing it; HKDF suffices, as the key is random.</summary>
    private static byte[] Lookup(string key) => VaultCrypto.Derive(key, LookupLabel, []);

    private static byte[] WriterKey(string key) => VaultCrypto.Derive(key, WriterLabel, []);

    private static byte[] MetaKey(byte[] userKey, string userId) => VaultCrypto.Derive(userKey, MetaLabel, Encoding.UTF8.GetBytes(userId));

    /// <summary>A keyed hash of the lower-cased name, so MongoDB keeps a user's key names unique without seeing one.</summary>
    private static byte[] NameId(byte[] userKey, string userId, string name) =>
        VaultCrypto.Mac(VaultCrypto.Derive(userKey, NameIdLabel, Encoding.UTF8.GetBytes(userId)), name.ToLowerInvariant());

    private static string MetaContext(string keyId) => $"api-key:{keyId}:meta";

    private static string WriterContext(string keyId) => $"api-key:{keyId}:writer";

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

    private sealed record ApiKeyMeta(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("vaults")] Dictionary<string, string> Vaults);
}

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
}

public sealed class ApiKeyNameTakenException(string name) : Exception($"You already have an API key named '{name}'.");

/// <summary>The session that asked is older than the user's last password change or recovery, or the user was
/// deactivated meanwhile: it may not make anything that outlives it.</summary>
public sealed class CredentialsChangedException() : Exception("Your session has ended; sign in again.");
