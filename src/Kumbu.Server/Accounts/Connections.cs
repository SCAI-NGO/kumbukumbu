// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kumbu.Core.Crypto;
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>
/// Where a connection stands. <see cref="Revoked"/> (by its owner, or left with no vault) and <see cref="Expired"/>
/// are final; <see cref="NeedsReconnect"/> is an authorised connection whose tokens were revoked, which the app
/// renews on the same URL after a fresh consent (Ruling 2 of the OAuth plan).
/// </summary>
public enum ConnectionState
{
    Waiting,
    Connected,
    NeedsReconnect,
    Revoked,
    Expired,
}

/// <summary>Why a connection was revoked or lost its tokens, as stored and as the web app shows it.</summary>
public static class ConnectionReasons
{
    public const string Revoked = "revoked";
    public const string VaultDeleted = "vault_deleted";
    public const string PasswordChanged = "password_changed";
    public const string Recovery = "recovery";
    public const string Deactivated = "deactivated";
    public const string RefreshReused = "refresh_reused";
    public const string CodeReused = "code_reused";
    public const string ClientRevoked = "client_revoked";
    public const string RightsChanged = "rights_changed";
    public const string Rekeyed = "rekeyed";
}

/// <summary>The app a connection was authorised for. <c>Verified</c>: identified by a client metadata document,
/// not self-registered.</summary>
public sealed record ConnectedClient(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("verified")] bool Verified);

/// <summary>A connection as its owner sees it in a list. <c>Reason</c> goes with <c>Revoked</c>, <c>Expired</c>
/// (<c>"expired"</c>) and <c>NeedsReconnect</c>; <c>Client</c> is null until an app was authorised.</summary>
public sealed record ConnectionInfo(
    string Id, string Name, IReadOnlyDictionary<string, KeyRight> Vaults, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? ExpiresAt, DateTime? RevokedAt,
    ConnectionState State, string? Reason, ConnectedClient? Client);

/// <summary>
/// Connections (OAuth connectors design): siblings of API keys, each with its own connector URL
/// <c>/mcp/c/{id}</c>, for which an app such as claude.ai obtains tokens through OAuth. A connection's name is a
/// writer label like an API key's, unique across both (<see cref="WriterNames"/>). Its name, rights and client are
/// sealed under the user key; in the clear are its owner, its times and reasons, and the ids of its vaults
/// (Ruling 17), so a vault's deletion can reach it while it has no slots.
/// </summary>
public sealed class Connections(IMongoDatabase db, VaultCatalog catalog, TimeProvider time, ILogger<Connections> log)
{
    internal const string CollectionName = "connections";

    private const string MetaLabel = "kumbu/v1/connection-meta";
    private const string Noun = "A connection";

    /// <summary>RFC 4648 Base32, lower-cased: an id that survives a URL, a host's case folding and being read aloud.</summary>
    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>80 bits, 16 Base32 characters without padding (Ruling 10).</summary>
    private const int IdBytes = 10;

    private volatile bool _ready;

    private IMongoCollection<ConnectionDocument> Docs { get; } = db.GetCollection<ConnectionDocument>(CollectionName);

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Creates a connection for <paramref name="vaults"/> (vault id → right), which must all be the user's own;
    /// it waits for an app to authorise it. Refuses as <see cref="ApiKeys.CreateAsync"/> does:
    /// <see cref="ArgumentException"/> for an unusable name, no vaults, a vault that isn't the user's or an expiry
    /// other than <see cref="ApiKeys.ExpiryDays"/>; <see cref="ConnectionNameTakenException"/> if an API key or
    /// connection of the user that is neither revoked nor expired has that name; <see cref="CredentialsChangedException"/>
    /// if the user's credentials changed at or after <paramref name="credentialReadAt"/> or the user is inactive.
    /// </summary>
    public async Task<ConnectionInfo> CreateAsync(
        User user, byte[] userKey, DateTime credentialReadAt, string name, IReadOnlyDictionary<string, KeyRight> vaults, CancellationToken ct,
        int? expiresInDays = null)
    {
        var clean = WriterNames.Check(user, name, Noun);
        if (vaults.Count == 0) throw new ArgumentException("A connection needs at least one vault.", nameof(vaults));
        if (expiresInDays is { } days && !ApiKeys.ExpiryDays.Contains(days))
        {
            throw new ArgumentException($"A connection expires after {string.Join(", ", ApiKeys.ExpiryDays.Order())} days, or never.", nameof(expiresInDays));
        }

        await EnsureReadyAsync(ct);
        // Only checked: the slots come with the tokens, wrapped under each of them.
        await catalog.UserVaultKeysAsync(user.Id, userKey, [.. vaults.Keys], Noun, ct);

        var id = NewId();
        var meta = new ConnectionMeta(clean, vaults.ToDictionary(v => v.Key, v => KeyRights.ToText(v.Value)), null);
        // Sealed under this user key: it must still be the one in force (UserKeyRotation).
        var keyId = UserCatalog.KeyId(userKey, user.Id);
        var now = Now;
        var doc = new ConnectionDocument
        {
            Id = id,
            User = user.Id,
            NameId = WriterNames.NameId(userKey, user.Id, clean),
            Meta = SealMeta(userKey, user.Id, id, meta),
            Vaults = [.. vaults.Keys],
            CreatedAt = now,
            // Whole milliseconds, as MongoDB keeps them, so the expiry the tokens get reads back equal.
            ExpiresAt = expiresInDays is { } d ? Milliseconds(now.AddDays(d)) : null,
        };

        // As for an API key (ApiKeys.CreateAsync): the user document is checked and written in the transaction, so
        // a credentials change can't slip in between, and a key made at the same time with the same name conflicts
        // on that document; retried, the later of the two sees the other in the name check.
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
                            Builders<UserDocument>.Update.Inc(u => u.ConnectionsCreated, 1),
                            cancellationToken: token);
                        if (current.MatchedCount == 0) throw new CredentialsChangedException();
                        if (await ApiKeys.NameTakenAsync(s, db, user.Id, doc.NameId, now, token)) throw new ConnectionNameTakenException(clean);
                        // Checked above outside the transaction; a vault deleted since is seen here, or its deletion
                        // conflicts on the user document written above.
                        await catalog.RequireVaultsAsync(s, doc.Vaults, Noun, token);
                        await RetireExpiredAsync(s, user.Id, now, token);
                        await Docs.InsertOneAsync(s, doc, cancellationToken: token);
                        return true;
                    },
                    cancellationToken: ct);
            }
            catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new ConnectionNameTakenException(clean);
            }
        }

        if (doc.ExpiresAt is null) log.LogInformation("User {Username} created connection {ConnectionId} for {Count} vaults", user.Username, id, vaults.Count);
        else log.LogInformation("User {Username} created connection {ConnectionId} for {Count} vaults, expiring {ExpiresAt:u}", user.Username, id, vaults.Count, doc.ExpiresAt);
        return Info(doc, meta, now);
    }

    /// <summary>The user's connections, revoked and expired ones included, oldest first.</summary>
    public async Task<IReadOnlyList<ConnectionInfo>> ListAsync(User user, byte[] userKey, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var now = Now;
        var result = new List<ConnectionInfo>();
        foreach (var doc in await Docs.Find(c => c.User == user.Id).SortBy(c => c.CreatedAt).ToListAsync(ct))
        {
            if (OpenMeta(user, userKey, doc) is { } meta) result.Add(Info(doc, meta, now));
        }

        return result;
    }

    /// <summary>
    /// The owner's Revoke, final: marked revoked, name freed, its tokens revoked (slots deleted, records kept as tombstones), in one transaction.
    /// False if the user has no such connection that isn't revoked.
    /// </summary>
    public async Task<bool> RevokeAsync(User user, string id, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        bool revoked;
        using (var session = await catalog.StartSessionAsync(ct))
        {
            // Retried on a write conflict: a token in use writes lastUsedAt on this document outside any transaction.
            revoked = await session.WithTransactionAsync(
                async (s, token) =>
                {
                    var now = Now;
                    var marked = await Docs.UpdateOneAsync(
                        s,
                        c => c.Id == id && c.User == user.Id && c.RevokedAt == null,
                        Builders<ConnectionDocument>.Update.Set(c => c.RevokedAt, now).Set(c => c.RevokedReason, ConnectionReasons.Revoked).Unset(c => c.NameId),
                        cancellationToken: token);
                    if (marked.MatchedCount == 0) return false;
                    await ConnectionRevocation.RevokeTokensAsync(db, s, Builders<ConnectionDocument>.Filter.Eq(c => c.Id, id), ConnectionReasons.Revoked, now, token);
                    return true;
                },
                cancellationToken: ct);
        }

        if (revoked) log.LogInformation("User {Username} revoked connection {ConnectionId}", user.Username, id);
        return revoked;
    }

    /// <summary>
    /// Gives the connection new rights (vault id → right, all the user's own, else <see cref="ArgumentException"/>)
    /// and revokes its tokens (Ruling 4): the server never holds a refresh token, so it can't re-wrap the slots,
    /// and the list never shows rights the tokens don't have. An authorised connection then needs a reconnect
    /// (<see cref="ConnectionReasons.RightsChanged"/>). Null if the user has no such connection that is neither
    /// revoked nor expired. <see cref="CredentialsChangedException"/>, as at creation, if the user is inactive, their
    /// credentials changed at or after <paramref name="credentialReadAt"/>, or the user key isn't the one in force.
    /// </summary>
    public async Task<ConnectionInfo?> SetVaultsAsync(
        User user, byte[] userKey, DateTime credentialReadAt, string id, IReadOnlyDictionary<string, KeyRight> vaults, CancellationToken ct)
    {
        if (vaults.Count == 0) throw new ArgumentException("A connection needs at least one vault.", nameof(vaults));
        await EnsureReadyAsync(ct);
        if (!await IsLiveAsync(user.Id, id, ct)) return null;
        await catalog.UserVaultKeysAsync(user.Id, userKey, [.. vaults.Keys], Noun, ct);

        bool changed;
        using (var session = await catalog.StartSessionAsync(ct))
        {
            changed = await session.WithTransactionAsync(
                async (s, token) =>
                {
                    // Read in the transaction: a concurrent re-seal of the meta (the client, at consent) conflicts, and
                    // the retry starts from it rather than overwriting it.
                    var now = Now;
                    var doc = await Docs.Find(s, c => c.Id == id && c.User == user.Id).FirstOrDefaultAsync(token);
                    if (doc is null || doc.RevokedAt is not null || now >= doc.ExpiresAt || OpenMeta(user, userKey, doc) is not { } meta) return false;
                    var next = meta with { Vaults = vaults.ToDictionary(v => v.Key, v => KeyRights.ToText(v.Value)) };
                    // As at creation: the user document is checked and written here, so a credentials change, a
                    // deactivation or a rotation of the user key can't slip in between; a vault deleted since the check
                    // above is seen here, or its deletion conflicts on the user document.
                    var keyId = UserCatalog.KeyId(userKey, user.Id);
                    var owner = await Users.UpdateOneAsync(
                        s,
                        u => u.Id == user.Id && u.Active && (u.CredentialsChangedAt == null || u.CredentialsChangedAt < credentialReadAt) && u.KeyId == keyId,
                        Builders<UserDocument>.Update.Inc(u => u.ConnectionsChanged, 1),
                        cancellationToken: token);
                    if (owner.MatchedCount == 0) throw new CredentialsChangedException();
                    await catalog.RequireVaultsAsync(s, [.. vaults.Keys], Noun, token);
                    await Docs.UpdateOneAsync(
                        s,
                        c => c.Id == id,
                        Builders<ConnectionDocument>.Update.Set(c => c.Meta, SealMeta(userKey, user.Id, id, next)).Set(c => c.Vaults, vaults.Keys.ToList()),
                        cancellationToken: token);
                    await ConnectionRevocation.RevokeTokensAsync(db, s, Builders<ConnectionDocument>.Filter.Eq(c => c.Id, id), ConnectionReasons.RightsChanged, now, token);
                    return true;
                },
                cancellationToken: ct);
        }

        if (!changed) return null;
        log.LogInformation("User {Username} changed the rights of connection {ConnectionId} to {Count} vaults", user.Username, id, vaults.Count);
        var updated = await FindAsync(id, ct);
        return updated is not null && OpenMeta(user, userKey, updated) is { } opened ? Info(updated, opened, Now) : null;
    }

    /// <summary>
    /// Revokes the tokens of every connection of the user that isn't revoked, inside the caller's transaction (a
    /// password change that revokes API keys, a recovery, a deactivation): those already authorised then need a
    /// reconnect with <paramref name="reason"/>. Returns how many connections.
    /// </summary>
    internal Task<int> RevokeAllTokensAsync(IClientSessionHandle session, string userId, string reason, CancellationToken ct) =>
        // No EnsureReadyAsync here: building an index while this transaction is open would wait for the transaction.
        ConnectionRevocation.RevokeTokensAsync(
            db, session, Builders<ConnectionDocument>.Filter.Eq(c => c.User, userId) & Builders<ConnectionDocument>.Filter.Eq(c => c.RevokedAt, null), reason, Now, ct);

    /// <summary>As the other overload, in a transaction of its own, retried on a write conflict like <see cref="ApiKeys.RevokeAllAsync(User, CancellationToken)"/>.</summary>
    public async Task<int> RevokeAllTokensAsync(User user, string reason, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        int count;
        using (var session = await catalog.StartSessionAsync(ct))
        {
            count = await session.WithTransactionAsync((s, token) => RevokeAllTokensAsync(s, user.Id, reason, token), cancellationToken: ct);
        }

        if (count > 0) log.LogInformation("The tokens of all {Count} connections of user {Username} are revoked ({Reason})", count, user.Username, reason);
        return count;
    }

    /// <summary>Revokes one connection's tokens (slots deleted, records kept as tombstones), inside the caller's transaction (a reused refresh token or code, the client's revocation).</summary>
    internal Task RevokeConnectionTokensAsync(IClientSessionHandle session, string connectionId, string reason, CancellationToken ct) =>
        ConnectionRevocation.RevokeTokensAsync(db, session, Builders<ConnectionDocument>.Filter.Eq(c => c.Id, connectionId), reason, Now, ct);

    internal async Task<ConnectionDocument?> FindAsync(string id, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        return await Docs.Find(c => c.Id == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>The sealed meta, or null if it doesn't open with this user key (another user's, or changed or moved in the database).</summary>
    internal ConnectionMeta? OpenMeta(User user, byte[] userKey, ConnectionDocument doc)
    {
        if (doc.User != user.Id) return null;
        try
        {
            return JsonSerializer.Deserialize<ConnectionMeta>(VaultCrypto.Open(MetaKey(userKey, user.Id), doc.Meta, MetaContext(doc.Id)));
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            log.LogWarning("Connection {ConnectionId} of user {Username} does not open with the user key", doc.Id, user.Username);
            return null;
        }
    }

    /// <summary>Seals <paramref name="client"/> into the meta at consent, in a transaction of its own.</summary>
    internal async Task SetClientAsync(User user, byte[] userKey, string id, ConnectedClient client, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        using var session = await catalog.StartSessionAsync(ct);
        await session.WithTransactionAsync(
            async (s, token) =>
            {
                await SetClientAsync(s, user, userKey, id, client, token);
                return true;
            },
            cancellationToken: ct);
    }

    /// <summary>Seals <paramref name="client"/> into the meta, inside the caller's transaction (issuing a code).
    /// <see cref="InvalidOperationException"/> if the connection is revoked, gone or doesn't open.</summary>
    internal async Task SetClientAsync(IClientSessionHandle session, User user, byte[] userKey, string id, ConnectedClient client, CancellationToken ct)
    {
        var doc = await Docs.Find(session, c => c.Id == id && c.User == user.Id && c.RevokedAt == null).FirstOrDefaultAsync(ct);
        if (doc is null || OpenMeta(user, userKey, doc) is not { } meta) throw new InvalidOperationException($"Connection {id} is revoked, gone or does not open.");
        await Docs.UpdateOneAsync(
            session, c => c.Id == id, Builders<ConnectionDocument>.Update.Set(c => c.Meta, SealMeta(userKey, user.Id, id, meta with { Client = client })), cancellationToken: ct);
    }

    /// <summary>10 random bytes in lower-case Base32 without padding: 16 characters (Ruling 10).</summary>
    public static string NewId()
    {
        var bytes = RandomNumberGenerator.GetBytes(IdBytes);
        var chars = new char[IdBytes * 8 / 5];
        int buffer = 0, bits = 0, next = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                chars[next++] = Base32[(buffer >> bits) & 31];
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// Inside an API key's creation: whether a connection of the user that is neither revoked nor expired has the
    /// name with <paramref name="nameId"/>. An expired one's name is free, as an expired key's.
    /// </summary>
    internal static Task<bool> NameTakenAsync(IClientSessionHandle session, IMongoDatabase db, string userId, byte[]? nameId, DateTime now, CancellationToken ct)
    {
        var f = Builders<ConnectionDocument>.Filter;
        return db.GetCollection<ConnectionDocument>(CollectionName)
            .Find(session, f.Eq(c => c.User, userId) & f.Eq(c => c.NameId, nameId) & f.Eq(c => c.RevokedAt, null) & (f.Eq(c => c.ExpiresAt, null) | f.Gt(c => c.ExpiresAt, now)))
            .AnyAsync(ct);
    }

    /// <summary>Inside a connection's creation: the user's expired connections lose their name id, so the name is
    /// free again for the unique index. They stay expired whether or not this ran.</summary>
    private Task RetireExpiredAsync(IClientSessionHandle session, string userId, DateTime now, CancellationToken ct) =>
        Docs.UpdateManyAsync(
            session,
            c => c.User == userId && c.RevokedAt == null && c.ExpiresAt != null && c.ExpiresAt <= now && c.NameId != null,
            Builders<ConnectionDocument>.Update.Unset(c => c.NameId),
            cancellationToken: ct);

    private async Task<bool> IsLiveAsync(string userId, string id, CancellationToken ct)
    {
        var now = Now;
        var doc = await Docs.Find(c => c.Id == id && c.User == userId).FirstOrDefaultAsync(ct);
        return doc is not null && doc.RevokedAt is null && !(now >= doc.ExpiresAt);
    }

    /// <summary>Revoked, else expired, else by whether it was ever authorised and whether its tokens were revoked since
    /// they were last issued: the token endpoint unsets <c>tokensRevokedAt</c> when it issues a pair, every
    /// revocation of the tokens sets it, and the refresh token lives as long as the connection.</summary>
    private static (ConnectionState State, string? Reason) StateOf(ConnectionDocument doc, DateTime now)
    {
        if (doc.RevokedAt is not null) return (ConnectionState.Revoked, doc.RevokedReason ?? ConnectionReasons.Revoked);
        if (now >= doc.ExpiresAt) return (ConnectionState.Expired, "expired");
        // An authorised connection without tokens needs a reconnect; one never authorised has nothing to reconnect,
        // whatever revoked "its" tokens (a rights change, a password change) meanwhile.
        if (doc.AuthorisedAt is null) return (ConnectionState.Waiting, null);
        return doc.TokensRevokedAt is null ? (ConnectionState.Connected, null) : (ConnectionState.NeedsReconnect, doc.TokensRevokedReason);
    }

    private static ConnectionInfo Info(ConnectionDocument doc, ConnectionMeta meta, DateTime now)
    {
        var (state, reason) = StateOf(doc, now);
        return new ConnectionInfo(
            doc.Id,
            meta.Name,
            // A vault deleted since is gone from the clear list, not from the sealed meta: it isn't listed.
            meta.Vaults.Where(v => KeyRights.Parse(v.Value) is not null && doc.Vaults.Contains(v.Key)).ToDictionary(v => v.Key, v => KeyRights.Parse(v.Value)!.Value),
            doc.CreatedAt,
            doc.LastUsedAt,
            doc.ExpiresAt,
            doc.RevokedAt,
            state,
            reason,
            meta.Client);
    }

    private static byte[] SealMeta(byte[] userKey, string userId, string id, ConnectionMeta meta) =>
        VaultCrypto.Seal(MetaKey(userKey, userId), JsonSerializer.SerializeToUtf8Bytes(meta), MetaContext(id));

    private static byte[] MetaKey(byte[] userKey, string userId) => VaultCrypto.Derive(userKey, MetaLabel, Encoding.UTF8.GetBytes(userId));

    private static string MetaContext(string id) => $"connection:{id}:meta";

    private static DateTime Milliseconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);

    /// <summary>Creates the indexes once per process; lazy, so the server starts before MongoDB is reachable.</summary>
    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        var keys = Builders<ConnectionDocument>.IndexKeys;
        await Docs.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<ConnectionDocument>(keys.Ascending(c => c.User).Ascending(c => c.CreatedAt)),
                // A vault's deletion finds the connections that had it.
                new CreateIndexModel<ConnectionDocument>(keys.Ascending(c => c.Vaults)),
                // One name per user among connections that are neither revoked nor retired: both unset the name id.
                new CreateIndexModel<ConnectionDocument>(
                    keys.Ascending(c => c.User).Ascending(c => c.NameId),
                    new CreateIndexOptions<ConnectionDocument>
                    {
                        Unique = true,
                        PartialFilterExpression = new BsonDocument("nameId", new BsonDocument("$type", "binData")),
                    }),
            ],
            ct);
        _ready = true;
    }

    /// <summary>
    /// For a rotation of the user key (<see cref="UserKeyRotation"/>), as <see cref="ApiKeys.PrepareResealAsync"/>: every
    /// connection of the user, revoked and expired ones included, its meta sealed again under <paramref name="newUserKey"/>
    /// and its name id recomputed where it is set; one whose meta doesn't open is logged and kept as it is. Writes nothing.
    /// </summary>
    internal static async Task<IReadOnlyList<MetaReseal>> PrepareResealAsync(
        IMongoDatabase db, string userId, byte[] oldUserKey, byte[] newUserKey, ILogger log, CancellationToken ct)
    {
        var (oldKey, newKey) = (MetaKey(oldUserKey, userId), MetaKey(newUserKey, userId));
        var result = new List<MetaReseal>();
        foreach (var doc in await db.GetCollection<ConnectionDocument>(CollectionName).Find(c => c.User == userId).ToListAsync(ct))
        {
            try
            {
                var plain = VaultCrypto.Open(oldKey, doc.Meta, MetaContext(doc.Id));
                var name = JsonSerializer.Deserialize<ConnectionMeta>(plain)!.Name;
                result.Add(new MetaReseal(
                    doc.Id, doc.Meta, doc.NameId, VaultCrypto.Seal(newKey, plain, MetaContext(doc.Id)), doc.NameId is null ? null : WriterNames.NameId(newUserKey, userId, name)));
            }
            catch (Exception e) when (e is CryptographicException or JsonException)
            {
                log.LogWarning("Connection {ConnectionId} of user {UserId} does not open with the user key; it is left as it is", doc.Id, userId);
                result.Add(new MetaReseal(doc.Id, doc.Meta, doc.NameId, doc.Meta, doc.NameId));
            }
        }

        return result;
    }

    /// <summary>What is sealed under the user key: the name, the rights (vault id → <c>read</c>/<c>write</c>) and,
    /// once authorised, the client.</summary>
    internal sealed record ConnectionMeta(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("vaults")] Dictionary<string, string> Vaults,
        [property: JsonPropertyName("client"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConnectedClient? Client);
}

/// <summary>
/// Revoking connections' tokens, on the collections directly: <see cref="Connections"/> calls it, and so does
/// <see cref="VaultCatalog"/>, which <see cref="Connections"/> depends on and so can't be called back (it works on
/// <c>api_keys</c> directly for the same reason).
/// </summary>
internal static class ConnectionRevocation
{
    internal const string TokensCollection = "oauth_tokens";

    /// <summary>The kind of the slots a connection's tokens have in <c>slots</c>, and the field naming the connection.</summary>
    internal const string SlotKind = "connection";

    internal const string ConnectionField = "connection";

    /// <summary>How long a revoked code's or token's record stays as a tombstone: long enough for an app to give up on
    /// it, and for a reused code to be recognised.</summary>
    internal static readonly TimeSpan TombstoneRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// Revokes the tokens of the connections <paramref name="which"/> selects: their slots are deleted and their records
    /// become tombstones (<see cref="TombstoneAsync"/>), and <c>tokensRevokedAt</c> and <c>tokensRevokedReason</c> are
    /// stamped, inside the caller's transaction. Returns how many connections matched.
    /// </summary>
    public static async Task<int> RevokeTokensAsync(
        IMongoDatabase db, IClientSessionHandle session, FilterDefinition<ConnectionDocument> which, string reason, DateTime now, CancellationToken ct)
    {
        var connections = db.GetCollection<ConnectionDocument>(Connections.CollectionName);
        var ids = await connections.Find(session, which).Project(c => c.Id).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        var ofThese = Builders<BsonDocument>.Filter.In(ConnectionField, ids);
        await TombstoneAsync(db, session, ofThese, now, ct);
        await db.GetCollection<BsonDocument>("slots").DeleteManyAsync(session, Builders<BsonDocument>.Filter.Eq("kind", SlotKind) & ofThese, cancellationToken: ct);
        await connections.UpdateManyAsync(
            session,
            Builders<ConnectionDocument>.Filter.In(c => c.Id, ids),
            Builders<ConnectionDocument>.Update.Set(c => c.TokensRevokedAt, now).Set(c => c.TokensRevokedReason, reason),
            cancellationToken: ct);
        return ids.Count;
    }

    /// <summary>
    /// Inside a vault deletion: the vault ids go from every connection that had them, a real write to each, so that a
    /// code or token being issued for one of them at the same time (which writes the connection too) conflicts and,
    /// retried, wraps no slot for a deleted vault (<c>OAuthTokens</c>). A connection left with no vault is revoked for
    /// good, as by its owner (Ruling 2): <see cref="ConnectionReasons.VaultDeleted"/>, name freed, its tokens revoked.
    /// Its slots on the deleted vaults are deleted by the caller, with every slot on them. Returns the revoked ones' ids.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> RemoveVaultsAsync(
        IMongoDatabase db, IClientSessionHandle session, IReadOnlyCollection<string> vaultIds, DateTime now, CancellationToken ct)
    {
        var connections = db.GetCollection<ConnectionDocument>(Connections.CollectionName);
        var f = Builders<ConnectionDocument>.Filter;
        var having = await connections.Find(session, f.AnyIn(c => c.Vaults, vaultIds)).Project(c => c.Id).ToListAsync(ct);
        if (having.Count == 0) return [];
        await connections.UpdateManyAsync(session, f.In(c => c.Id, having), Builders<ConnectionDocument>.Update.PullAll(c => c.Vaults, vaultIds), cancellationToken: ct);
        var left = await connections.Find(session, f.In(c => c.Id, having) & f.Size(c => c.Vaults, 0) & f.Eq(c => c.RevokedAt, null)).Project(c => c.Id).ToListAsync(ct);
        if (left.Count == 0) return [];
        await connections.UpdateManyAsync(
            session,
            f.In(c => c.Id, left),
            Builders<ConnectionDocument>.Update.Set(c => c.RevokedAt, now).Set(c => c.RevokedReason, ConnectionReasons.VaultDeleted).Unset(c => c.NameId),
            cancellationToken: ct);
        await RevokeTokensAsync(db, session, f.In(c => c.Id, left), ConnectionReasons.VaultDeleted, now, ct);
        return left;
    }

    /// <summary>
    /// Inside an account's deletion: the user's connections with every code and token of theirs, tombstones included,
    /// their slots and their pending authorisation requests. Returns how many connections.
    /// </summary>
    internal static async Task<int> DeleteAllAsync(IMongoDatabase db, IClientSessionHandle session, string userId, CancellationToken ct)
    {
        var connections = db.GetCollection<ConnectionDocument>(Connections.CollectionName);
        var ids = await connections.Find(session, c => c.User == userId).Project(c => c.Id).ToListAsync(ct);
        var b = Builders<BsonDocument>.Filter;
        var ofThese = b.In(ConnectionField, ids);
        await db.GetCollection<BsonDocument>(TokensCollection).DeleteManyAsync(session, ofThese | b.Eq("user", userId), cancellationToken: ct);
        await db.GetCollection<BsonDocument>("slots").DeleteManyAsync(session, b.Eq("kind", SlotKind) & ofThese, cancellationToken: ct);
        await db.GetCollection<BsonDocument>(OAuth.AuthorizationRequests.CollectionName).DeleteManyAsync(session, ofThese, cancellationToken: ct);
        await connections.DeleteManyAsync(session, c => c.User == userId, cancellationToken: ct);
        return ids.Count;
    }

    /// <summary>
    /// Turns the code and token records <paramref name="which"/> selects, unless already revoked, into tombstones, inside
    /// the caller's transaction: <c>revokedAt</c> set, the lookup kept, the grant and a used refresh token's kept pair
    /// dropped, gone after <see cref="TombstoneRetention"/>, or later for a used refresh token whose record was to stay
    /// longer to recognise its reuse. A revoked token stays recognised (Ruling 12): from Anthropic's shared range a
    /// revoked token counted as a wrong key would lock every claude.ai user of the instance out. A tombstone opens
    /// nothing: its slots are deleted by the caller.
    /// </summary>
    internal static async Task TombstoneAsync(IMongoDatabase db, IClientSessionHandle session, FilterDefinition<BsonDocument> which, DateTime now, CancellationToken ct)
    {
        var tokens = db.GetCollection<BsonDocument>(TokensCollection);
        var f = Builders<BsonDocument>.Filter;
        var live = which & f.Exists("revokedAt", false);
        var used = f.Eq("kind", "refresh") & f.Exists("usedAt");
        var update = Builders<BsonDocument>.Update.Set("revokedAt", now).Unset("grant").Unset("successor");
        await tokens.UpdateManyAsync(session, live & used, update.Max("purgeAt", now + TombstoneRetention), cancellationToken: ct);
        await tokens.UpdateManyAsync(session, live & !used, update.Set("purgeAt", now + TombstoneRetention), cancellationToken: ct);
    }
}

/// <summary>A connection (collection <c>connections</c>). Name, rights and client are sealed; the vault ids are not (Ruling 17).</summary>
[BsonIgnoreExtraElements]
public sealed class ConnectionDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("user")]
    public string User { get; set; } = "";

    /// <summary>Until revoked or retired after expiry: the keyed name hash API keys use too, for the unique index.</summary>
    [BsonElement("nameId"), BsonIgnoreIfNull]
    public byte[]? NameId { get; set; }

    /// <summary>Name, rights and client, sealed under the user key: for the owner's list and the consent page.</summary>
    [BsonElement("meta")]
    public byte[] Meta { get; set; } = [];

    /// <summary>The ids of the vaults it covers, in the clear, so a vault's deletion finds it without any key.</summary>
    [BsonElement("vaults")]
    public List<string> Vaults { get; set; } = [];

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    [BsonElement("lastUsedAt"), BsonIgnoreIfNull]
    public DateTime? LastUsedAt { get; set; }

    [BsonElement("expiresAt"), BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }

    [BsonElement("revokedAt"), BsonIgnoreIfNull]
    public DateTime? RevokedAt { get; set; }

    [BsonElement("revokedReason"), BsonIgnoreIfNull]
    public string? RevokedReason { get; set; }

    [BsonElement("tokensRevokedAt"), BsonIgnoreIfNull]
    public DateTime? TokensRevokedAt { get; set; }

    [BsonElement("tokensRevokedReason"), BsonIgnoreIfNull]
    public string? TokensRevokedReason { get; set; }

    /// <summary>When a code was first exchanged for tokens: from then on, a connection without tokens needs a reconnect.</summary>
    [BsonElement("authorisedAt"), BsonIgnoreIfNull]
    public DateTime? AuthorisedAt { get; set; }

    /// <summary>How many times slots were wrapped for its codes and tokens. Incremented in every issuing transaction (a
    /// code, an exchange, a refresh): a write that always changes the document, unlike setting a value it may already
    /// have, so that the issuing conflicts with a vault deletion, which writes the document too
    /// (<see cref="ConnectionRevocation.RemoveVaultsAsync"/>).</summary>
    [BsonElement("wraps")]
    public long Wraps { get; set; }
}

public sealed class ConnectionNameTakenException(string name) : Exception($"You already have an API key or connection named '{name}'.");
