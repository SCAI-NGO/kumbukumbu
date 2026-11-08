// SPDX-License-Identifier: AGPL-3.0-or-later
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

/// <summary>A WebDAV share as its owner sees it in a list; never its password, which is shown once at creation.
/// <c>ExpiresAt</c> is null for a share that doesn't expire.</summary>
public sealed record DavShareInfo(
    string Id, string Username, string Label, IReadOnlyDictionary<string, KeyRight> Vaults, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt,
    DateTime? ExpiresAt)
{
    /// <summary>The ids of the vaults among <see cref="Vaults"/> the share no longer has a slot for (a re-keying took it),
    /// so the list can say it lost them. Empty for a share that is revoked or expired: it opens nothing anyway.</summary>
    public IReadOnlyList<string> LostVaults { get; init; } = [];
}

/// <summary>A share that was presented and checked: whose it is, and its label (unmarked: the caller marks it "(dav)").</summary>
public sealed record ResolvedDavShare(User User, string Id, string Label);

/// <summary>
/// What a username and password presented to <c>/dav</c> are. <c>Known</c> is true only when the token and the username
/// match one share that is revoked or expired, or whose user is inactive: refused, but not a guess, so it doesn't count
/// against the wrong-key limiter. A wrong token, or a right one with another share's username, is not known.
/// </summary>
public sealed record DavShareResolution(ResolvedDavShare? Share, bool Known);

/// <summary>
/// WebDAV shares (WebDAV design §3.2, §4), built like <see cref="ApiKeys"/>: a username (the owner's, a dot and the
/// share's four-character id) and a password of 32 random bytes (<see cref="DavTokens"/>), shown once. Each share has a
/// slot on every vault it was given (<see cref="VaultCatalog.DavSlotsAsync"/>), the vault key and the right sealed
/// together under a key derived from the password. MongoDB holds only values derived from the password, and no part of
/// it; label and vaults are sealed under the user key for the owner's list, and the label once more under the password,
/// for its writer label. Everything is derived from the normalised password, never from the form it was typed in.
/// </summary>
public sealed class DavShares(IMongoDatabase db, VaultCatalog catalog, TimeProvider time, ILogger<DavShares> log)
{
    private const string Noun = "A WebDAV share";

    private const string LookupLabel = "kumbu/v1/dav-share-lookup";
    private const string WriterLabel = "kumbu/v1/dav-share-writer";
    private const string MetaLabel = "kumbu/v1/dav-share-meta";

    /// <summary>A label of its own: a share's label lives apart from the names of keys and connections, and its hash must
    /// not tell the database that a share and a key are named alike.</summary>
    private const string NameIdLabel = "kumbu/v1/dav-share-name-id";

    /// <summary>The tool a share's writes are marked with: "Work PC (dav)".</summary>
    public const string Tool = "dav";

    /// <summary>Four Crockford Base32 characters, lower case, as they appear in the username.</summary>
    private const string IdAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    private const int IdLength = 4;

    /// <summary>An id is a million-to-one draw; a share id already taken is drawn again, up to this many times in all.</summary>
    private const int IdAttempts = 5;

    /// <summary>The shares a user may hold that are neither revoked nor expired. Each may park
    /// <see cref="Dav.DavRules.ParkingCap"/> and lock in this server's memory, so their number must be bounded; twenty is a
    /// share per device and per app with room to spare (WebDAV design §3.2 expects a handful).</summary>
    public const int MaxActive = 20;

    /// <summary>A burst of requests shouldn't write on each; "last used" may therefore lag by up to this much.</summary>
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(1);

    private volatile bool _ready;

    internal const string CollectionName = "dav_shares";

    private IMongoCollection<DavShareDocument> Shares { get; } = db.GetCollection<DavShareDocument>(CollectionName);

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>For tests: the share ids drawn, in place of random ones (to make two meet).</summary>
    internal Func<string>? NextId { get; set; }

    /// <summary>
    /// Creates a share for <paramref name="vaults"/> (vault id → right), the user's own or shared with her; returns its
    /// username and its password, formatted, the only time it is shown. Throws as <see cref="ApiKeys.CreateAsync"/> does:
    /// <see cref="ArgumentException"/> for an unusable label, no vaults, a vault the user can't give or an expiry not among
    /// <see cref="ApiKeys.ExpiryDays"/>, <see cref="DavShareLabelTakenException"/> if one of her shares that is neither
    /// revoked nor expired has that label (compared case-insensitively), <see cref="DavShareLimitException"/> if she has
    /// <see cref="MaxActive"/> such shares already, and <see cref="CredentialsChangedException"/>
    /// if her credentials changed at or after <paramref name="credentialReadAt"/> or she is inactive. On a vault shared
    /// with her, the share is derived through her membership, which records it as <c>dav:{id}</c> with its marked label.
    /// </summary>
    public async Task<(string Username, string Password, DavShareInfo Info)> CreateAsync(
        User user, byte[] userKey, DateTime credentialReadAt, string label, IReadOnlyDictionary<string, KeyRight> vaults, CancellationToken ct,
        int? expiresInDays = null)
    {
        var clean = WriterNames.Check(user, label, Noun);
        if (vaults.Count == 0) throw new ArgumentException($"{Noun} needs at least one vault.", nameof(vaults));
        if (expiresInDays is { } days && !ApiKeys.ExpiryDays.Contains(days))
        {
            throw new ArgumentException($"{Noun} expires after {string.Join(", ", ApiKeys.ExpiryDays.Order())} days, or never.", nameof(expiresInDays));
        }

        await EnsureReadyAsync(ct);

        var token = DavTokens.New();
        var meta = new DavShareMeta(clean, vaults.ToDictionary(v => v.Key, v => KeyRights.ToText(v.Value)), KeyRights.SealedVersion);
        // Whole milliseconds, as MongoDB keeps them: the time is bound into the writer seal and must read back equal.
        var expiresAt = expiresInDays is { } d ? Milliseconds(Now.AddDays(d)) : (DateTime?)null;
        // The meta and name id are sealed under this user key: it must still be the one in force (UserKeyRotation).
        var keyId = UserCatalog.KeyId(userKey, user.Id);
        for (var attempt = 1; ; attempt++)
        {
            var id = NextId?.Invoke() ?? NewId();
            // The share's id is bound into its slots: drawn again, they are sealed again.
            var (slots, vaultKeys) = await catalog.DavSlotsAsync(user.Id, userKey, id, token, vaults, ct);
            var doc = new DavShareDocument
            {
                Id = id,
                User = user.Id,
                Lookup = Lookup(token),
                NameId = NameId(userKey, user.Id, clean),
                Meta = VaultCrypto.Seal(MetaKey(userKey, user.Id), JsonSerializer.SerializeToUtf8Bytes(meta), MetaContext(id)),
                Writer = VaultCrypto.Seal(WriterKey(token), Encoding.UTF8.GetBytes(clean), WriterContext(id, expiresAt)),
                CreatedAt = Now,
                ExpiresAt = expiresAt,
            };

            try
            {
                await InsertAsync(user, userKey, credentialReadAt, keyId, clean, doc, slots, vaultKeys, ct);
            }
            catch (MongoWriteException e) when (IsDuplicateId(e) && attempt < IdAttempts)
            {
                log.LogInformation("WebDAV share id {ShareId} is taken; another is drawn", id);
                continue;
            }

            if (expiresAt is null) log.LogInformation("User {Username} created WebDAV share {ShareId} for {Count} vaults", user.Username, id, vaults.Count);
            else log.LogInformation("User {Username} created WebDAV share {ShareId} for {Count} vaults, expiring {ExpiresAt:u}", user.Username, id, vaults.Count, expiresAt);
            return (Username(user.Username, id), DavTokens.Format(token), Info(doc, meta, user.Username, new HashSet<string>(vaults.Keys, StringComparer.Ordinal)));
        }
    }

    /// <summary>The share and its slots in one transaction, under the conditions <see cref="ApiKeys.CreateAsync"/> explains.</summary>
    private async Task InsertAsync(
        User user, byte[] userKey, DateTime credentialReadAt, byte[] keyId, string clean, DavShareDocument doc, IReadOnlyList<SlotDocument> slots,
        IReadOnlyDictionary<string, VaultCatalog.VaultKeyGrant> vaultKeys, CancellationToken ct)
    {
        using var session = await catalog.StartSessionAsync(ct);
        try
        {
            await session.WithTransactionAsync(
                async (s, token) =>
                {
                    // A password change, recovery, deactivation or rotation since the session was read: the share would
                    // outlive it. Either this commits first and the change's revocation finds the share, or the change
                    // comes first and the condition fails or this write conflicts.
                    var current = await Users.UpdateOneAsync(
                        s,
                        u => u.Id == user.Id && u.Active && (u.CredentialsChangedAt == null || u.CredentialsChangedAt < credentialReadAt) && u.KeyId == keyId,
                        Builders<UserDocument>.Update.Inc(u => u.SharesCreated, 1),
                        cancellationToken: token);
                    if (current.MatchedCount == 0) throw new CredentialsChangedException();
                    if (await LabelTakenAsync(s, user.Id, doc.NameId, Now, token)) throw new DavShareLabelTakenException(clean);
                    // Read in the transaction that writes users.sharesCreated above: two concurrent creations conflict
                    // there, so neither can slip past the limit beside the other.
                    if (await ActiveCountAsync(s, user.Id, Now, token) >= MaxActive) throw new DavShareLimitException(MaxActive);
                    await catalog.RequireVaultsAsync(s, [.. vaultKeys.Keys], Noun, token);
                    await catalog.RequireSameVaultKeysAsync(s, user.Id, userKey, vaultKeys.ToDictionary(v => v.Key, v => v.Value.Key), token);
                    // A vault shared with the user: its membership must still be active, and records the share under an id
                    // that can never meet a key's or a connection's.
                    foreach (var (vaultId, grant) in vaultKeys)
                    {
                        if (grant.Membership is { } membership)
                            await Sharing.Memberships.RecordCredentialAsync(db, s, membership, vaultId, CredentialId(doc.Id), Writers.Marked(clean, Tool), grant.Key, Noun, token);
                    }

                    await RetireExpiredAsync(s, user.Id, token);
                    await Shares.InsertOneAsync(s, doc, cancellationToken: token);
                    await catalog.Slots.InsertManyAsync(s, slots, cancellationToken: token);
                    return true;
                },
                cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey && !IsDuplicateId(e))
        {
            throw new DavShareLabelTakenException(clean);
        }
    }

    /// <summary>The share's id in a membership's sealed <c>writers</c> and <c>writerIds</c>.</summary>
    public static string CredentialId(string shareId) => $"{Tool}:{shareId}";

    /// <summary>The username a share signs in with: the owner's, a dot, the share's id.</summary>
    public static string Username(string ownerUsername, string shareId) => $"{ownerUsername}.{shareId}";

    /// <summary>The user's shares, revoked ones included (their labels still mark their writes in the history), oldest first.</summary>
    public async Task<IReadOnlyList<DavShareInfo>> ListAsync(User user, byte[] userKey, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var metaKey = MetaKey(userKey, user.Id);
        var docs = await Shares.Find(d => d.User == user.Id).SortBy(d => d.CreatedAt).ToListAsync(ct);
        var f = Builders<SlotDocument>.Filter;
        var slotVaults = (await catalog.Slots.Find(f.Eq(s => s.Kind, SlotDocument.Dav) & f.In(s => s.ShareId, docs.Select(d => d.Id)))
                .Project(s => new { s.ShareId, s.Vault }).ToListAsync(ct))
            .GroupBy(s => s.ShareId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Vault).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var result = new List<DavShareInfo>();
        foreach (var doc in docs)
        {
            try
            {
                var meta = JsonSerializer.Deserialize<DavShareMeta>(VaultCrypto.Open(metaKey, doc.Meta, MetaContext(doc.Id)))!;
                result.Add(Info(doc, meta, user.Username, slotVaults.GetValueOrDefault(doc.Id) ?? []));
            }
            catch (Exception e) when (e is CryptographicException or JsonException)
            {
                log.LogWarning("WebDAV share {ShareId} of user {Username} does not open with the user key", doc.Id, user.Username);
            }
        }

        return result;
    }

    /// <summary>Revokes one of the user's shares: marked revoked, label freed and slots deleted, in one transaction, so it
    /// opens nothing from then on. False if the user has no such share that isn't revoked.</summary>
    public async Task<bool> RevokeAsync(User user, string shareId, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        using var session = await catalog.StartSessionAsync(ct);
        session.StartTransaction();
        var marked = await Shares.UpdateOneAsync(
            session,
            d => d.Id == shareId && d.User == user.Id && d.RevokedAt == null,
            Builders<DavShareDocument>.Update.Set(d => d.RevokedAt, Now).Unset(d => d.NameId),
            cancellationToken: ct);
        if (marked.MatchedCount == 0)
        {
            await session.AbortTransactionAsync(CancellationToken.None);
            return false;
        }

        await catalog.DeleteDavSlotsAsync(session, shareId, ct);
        await session.CommitTransactionAsync(CancellationToken.None);
        log.LogInformation("User {Username} revoked WebDAV share {ShareId}", user.Username, shareId);
        return true;
    }

    /// <summary>Revokes every share of the user that isn't revoked yet, in a transaction of its own (a password change that
    /// revokes the API keys, a recovery, a re-encryption of the account). Returns how many.</summary>
    public async Task<int> RevokeAllAsync(User user, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        return await RevokeAllAsync(db, user, Now, log, ct);
    }

    /// <summary>As <see cref="RevokeAllAsync(User, CancellationToken)"/>, on the database alone, so that
    /// <see cref="UserCatalog"/> needs neither this class nor its <see cref="VaultCatalog"/>. Retried on a write conflict
    /// (<see cref="ResolveAsync"/> writes <c>lastUsedAt</c> outside any transaction).</summary>
    internal static async Task<int> RevokeAllAsync(IMongoDatabase db, User user, DateTime now, ILogger log, CancellationToken ct)
    {
        int revoked;
        using (var session = await db.Client.StartSessionAsync(cancellationToken: ct))
        {
            revoked = await session.WithTransactionAsync((s, token) => RevokeAllAsync(db, s, user.Id, now, token), cancellationToken: ct);
        }

        if (revoked > 0) log.LogInformation("User {Username} revoked all {Count} WebDAV shares", user.Username, revoked);
        return revoked;
    }

    /// <summary>Revokes every share of the user that isn't revoked yet, inside the caller's transaction (deactivating the
    /// user): marked revoked, labels freed and slots deleted, so reactivating the user revives none of them.</summary>
    internal Task<int> RevokeAllAsync(IClientSessionHandle session, string userId, CancellationToken ct) => RevokeAllAsync(db, session, userId, Now, ct);

    private static async Task<int> RevokeAllAsync(IMongoDatabase db, IClientSessionHandle session, string userId, DateTime now, CancellationToken ct)
    {
        // No index building here: it would wait for this very transaction.
        var shares = db.GetCollection<DavShareDocument>(CollectionName);
        var ids = await shares.Find(session, d => d.User == userId && d.RevokedAt == null).Project(d => d.Id).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        await shares.UpdateManyAsync(
            session,
            Builders<DavShareDocument>.Filter.In(d => d.Id, ids),
            Builders<DavShareDocument>.Update.Set(d => d.RevokedAt, now).Unset(d => d.NameId),
            cancellationToken: ct);
        var f = Builders<SlotDocument>.Filter;
        await db.GetCollection<SlotDocument>("slots").DeleteManyAsync(session, f.Eq(s => s.Kind, SlotDocument.Dav) & f.In(s => s.ShareId, ids), cancellationToken: ct);
        return ids.Count;
    }

    /// <summary>Inside a share's creation: the user's expired shares lose their name id and their slots, as API keys do.</summary>
    private async Task RetireExpiredAsync(IClientSessionHandle session, string userId, CancellationToken ct)
    {
        var now = Now;
        var ids = await Shares.Find(session, d => d.User == userId && d.RevokedAt == null && d.ExpiresAt != null && d.ExpiresAt <= now && d.NameId != null)
            .Project(d => d.Id).ToListAsync(ct);
        if (ids.Count == 0) return;
        await Shares.UpdateManyAsync(session, Builders<DavShareDocument>.Filter.In(d => d.Id, ids), Builders<DavShareDocument>.Update.Unset(d => d.NameId), cancellationToken: ct);
        foreach (var id in ids) await catalog.DeleteDavSlotsAsync(session, id, ct);
    }

    /// <summary>Every user's expired shares that aren't retired yet lose their slots and their label's hash, as
    /// <see cref="ApiKeys.RetireAllExpiredAsync"/> does for keys (<see cref="ExpiredKeySweep"/>). Returns how many.</summary>
    public async Task<int> RetireAllExpiredAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var now = Now;
        var expired = await Shares.Find(d => d.RevokedAt == null && d.ExpiresAt != null && d.ExpiresAt <= now && d.NameId != null).ToListAsync(ct);
        foreach (var doc in expired) await RetireExpiredAsync(doc, ct);
        return expired.Count;
    }

    /// <summary>The first time an expired share is presented: its slots go and its label is freed. Best effort, the slots
    /// first; the share is refused either way.</summary>
    private async Task RetireExpiredAsync(DavShareDocument doc, CancellationToken ct)
    {
        if (doc.NameId is null) return;
        await catalog.DeleteDavSlotsAsync(doc.Id, ct);
        await Shares.UpdateOneAsync(d => d.Id == doc.Id, Builders<DavShareDocument>.Update.Unset(d => d.NameId), cancellationToken: ct);
        log.LogInformation("WebDAV share {ShareId} of user {UserId} has expired; its slots are deleted", doc.Id, doc.User);
    }

    /// <summary>
    /// The share behind a presented username and password (see <see cref="DavShareResolution"/>). Both must match: the
    /// password finds the share, and the username must be its owner's with its id, compared case-insensitively and split
    /// at the last dot (a username may hold dots). Never logs anything of the password. The rights are not checked here:
    /// they live in the slots.
    /// </summary>
    public async Task<DavShareResolution> ResolveAsync(string username, string password, CancellationToken ct)
    {
        var refused = new DavShareResolution(null, false);
        if (DavTokens.Normalise(password) is not { } token) return refused;
        var dot = username.LastIndexOf('.');
        if (dot <= 0) return refused;
        var (owner, shareId) = (username[..dot].ToLowerInvariant(), username[(dot + 1)..].ToLowerInvariant());

        await EnsureReadyAsync(ct);
        var lookup = Lookup(token);
        var doc = await Shares.Find(d => d.Lookup == lookup).FirstOrDefaultAsync(ct);
        if (doc is null || !string.Equals(doc.Id, shareId, StringComparison.Ordinal)) return refused;
        var user = await Users.Find(u => u.Id == doc.User).FirstOrDefaultAsync(ct);
        if (user is null || !string.Equals(user.Username, owner, StringComparison.Ordinal)) return refused;

        var known = new DavShareResolution(null, true);
        var now = Now;
        if (doc.RevokedAt is not null) return known;
        if (now >= doc.ExpiresAt)
        {
            await RetireExpiredAsync(doc, ct);
            return known;
        }

        if (!user.Active) return known;

        // The expiry is bound into this seal: changed or removed in the database, the label doesn't open.
        string label;
        try
        {
            label = Encoding.UTF8.GetString(VaultCrypto.Open(WriterKey(token), doc.Writer, WriterContext(doc.Id, doc.ExpiresAt)));
        }
        catch (CryptographicException)
        {
            log.LogWarning("WebDAV share {ShareId} matched but its label does not open", doc.Id);
            return refused;
        }

        if (doc.LastUsedAt is not { } lastUsed || now - lastUsed >= LastUsedGranularity)
        {
            // Conditional, so of concurrent requests only one writes.
            var stale = now - LastUsedGranularity;
            await Shares.UpdateOneAsync(
                d => d.Id == doc.Id && (d.LastUsedAt == null || d.LastUsedAt <= stale),
                Builders<DavShareDocument>.Update.Set(d => d.LastUsedAt, now),
                cancellationToken: ct);
        }

        return new DavShareResolution(new ResolvedDavShare(user.ToUser(), doc.Id, label), false);
    }

    /// <summary>Inside a share's creation: whether a share of the user that is neither revoked nor expired has the label
    /// with <paramref name="nameId"/>. An expired share's label is free even before it is retired.</summary>
    private Task<bool> LabelTakenAsync(IClientSessionHandle session, string userId, byte[]? nameId, DateTime now, CancellationToken ct)
    {
        var f = Builders<DavShareDocument>.Filter;
        return Shares.Find(session, f.Eq(d => d.User, userId) & f.Eq(d => d.NameId, nameId) & f.Eq(d => d.RevokedAt, null) & (f.Eq(d => d.ExpiresAt, null) | f.Gt(d => d.ExpiresAt, now)))
            .AnyAsync(ct);
    }

    /// <summary>Inside a share's creation: how many shares of the user are neither revoked nor expired.</summary>
    private Task<long> ActiveCountAsync(IClientSessionHandle session, string userId, DateTime now, CancellationToken ct)
    {
        var f = Builders<DavShareDocument>.Filter;
        return Shares.CountDocumentsAsync(session, f.Eq(d => d.User, userId) & f.Eq(d => d.RevokedAt, null) & (f.Eq(d => d.ExpiresAt, null) | f.Gt(d => d.ExpiresAt, now)), cancellationToken: ct);
    }

    /// <summary>A vault the share was given but has no slot for any more is lost, while the share could still open it.</summary>
    private DavShareInfo Info(DavShareDocument doc, DavShareMeta meta, string ownerUsername, IReadOnlySet<string> slotVaults)
    {
        var vaults = meta.Vaults.Where(v => KeyRights.ParseSealed(v.Value, meta.V) is not null).ToDictionary(v => v.Key, v => KeyRights.ParseSealed(v.Value, meta.V)!.Value);
        var live = doc.RevokedAt is null && !(Now >= doc.ExpiresAt);
        return new(doc.Id, Username(ownerUsername, doc.Id), meta.Label, vaults, doc.CreatedAt, doc.LastUsedAt, doc.RevokedAt, doc.ExpiresAt)
        {
            LostVaults = live ? [.. vaults.Keys.Where(v => !slotVaults.Contains(v)).Order(StringComparer.Ordinal)] : [],
        };
    }

    private static string NewId() => string.Concat(Enumerable.Range(0, IdLength).Select(_ => IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)]));

    private static bool IsDuplicateId(MongoWriteException e) =>
        e.WriteError.Category == ServerErrorCategory.DuplicateKey && e.WriteError.Message.Contains("index: _id_ ", StringComparison.Ordinal);

    /// <summary>Finds the share without storing its password; HKDF suffices, as the password is random.</summary>
    private static byte[] Lookup(string token) => VaultCrypto.Derive(token, LookupLabel, []);

    private static byte[] WriterKey(string token) => VaultCrypto.Derive(token, WriterLabel, []);

    private static byte[] MetaKey(byte[] userKey, string userId) => VaultCrypto.Derive(userKey, MetaLabel, Encoding.UTF8.GetBytes(userId));

    private static byte[] NameId(byte[] userKey, string userId, string label) =>
        VaultCrypto.Mac(VaultCrypto.Derive(userKey, NameIdLabel, Encoding.UTF8.GetBytes(userId)), label.ToLowerInvariant());

    private static string MetaContext(string shareId) => $"dav-share:{shareId}:meta";

    private static string WriterContext(string shareId, DateTime? expiresAt) =>
        expiresAt is { } at ? $"dav-share:{shareId}:writer:{new DateTimeOffset(at.Ticks, TimeSpan.Zero).ToUnixTimeMilliseconds()}" : $"dav-share:{shareId}:writer";

    private static DateTime Milliseconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);

    /// <summary>Creates the indexes once per process; lazy, so the server starts before MongoDB is reachable.</summary>
    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        var keys = Builders<DavShareDocument>.IndexKeys;
        await Shares.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<DavShareDocument>(keys.Ascending(d => d.Lookup), new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<DavShareDocument>(keys.Ascending(d => d.User)),
                // One label per user among shares that aren't revoked or retired: both unset the name id.
                new CreateIndexModel<DavShareDocument>(
                    keys.Ascending(d => d.User).Ascending(d => d.NameId),
                    new CreateIndexOptions<DavShareDocument>
                    {
                        Unique = true,
                        PartialFilterExpression = new MongoDB.Bson.BsonDocument("nameId", new MongoDB.Bson.BsonDocument("$type", "binData")),
                    }),
            ],
            ct);
        _ready = true;
    }

    /// <summary>
    /// For a rotation of the user key (<see cref="UserKeyRotation"/>), as <see cref="ApiKeys.PrepareResealAsync"/>: every
    /// share of the user, revoked and expired ones included, with its meta sealed again under <paramref name="newUserKey"/>
    /// and its label's hash recomputed where it is set. A meta that doesn't open is logged and kept as it is. Writes nothing.
    /// </summary>
    internal static async Task<IReadOnlyList<MetaReseal>> PrepareResealAsync(
        IMongoDatabase db, string userId, byte[] oldUserKey, byte[] newUserKey, ILogger log, CancellationToken ct)
    {
        var (oldKey, newKey) = (MetaKey(oldUserKey, userId), MetaKey(newUserKey, userId));
        var result = new List<MetaReseal>();
        foreach (var doc in await db.GetCollection<DavShareDocument>(CollectionName).Find(d => d.User == userId).ToListAsync(ct))
        {
            try
            {
                var plain = VaultCrypto.Open(oldKey, doc.Meta, MetaContext(doc.Id));
                var label = JsonSerializer.Deserialize<DavShareMeta>(plain)!.Label;
                result.Add(new MetaReseal(
                    doc.Id, doc.Meta, doc.NameId, VaultCrypto.Seal(newKey, plain, MetaContext(doc.Id)), doc.NameId is null ? null : NameId(newUserKey, userId, label)));
            }
            catch (Exception e) when (e is CryptographicException or JsonException)
            {
                log.LogWarning("WebDAV share {ShareId} of user {UserId} does not open with the user key; it is left as it is", doc.Id, userId);
                result.Add(new MetaReseal(doc.Id, doc.Meta, doc.NameId, doc.Meta, doc.NameId));
            }
        }

        return result;
    }

    private sealed record DavShareMeta(
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("vaults")] Dictionary<string, string> Vaults,
        [property: JsonPropertyName("v")] int? V = null);
}

/// <summary>A WebDAV share (collection <c>dav_shares</c>). Nothing here is the password, nor any part of it; label and
/// vaults are sealed.</summary>
[BsonIgnoreExtraElements]
public sealed class DavShareDocument
{
    /// <summary>The share's id, the part of its username after the last dot.</summary>
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("user")]
    public string User { get; set; } = "";

    [BsonElement("lookup")]
    public byte[] Lookup { get; set; } = [];

    /// <summary>Until revoked or retired: a keyed hash of the label, for the unique index.</summary>
    [BsonElement("nameId"), BsonIgnoreIfNull]
    public byte[]? NameId { get; set; }

    /// <summary>Label and vaults with their rights, sealed under the user key: for the owner's list only. The rights that
    /// count are those sealed into the slots.</summary>
    [BsonElement("meta")]
    public byte[] Meta { get; set; } = [];

    /// <summary>The label, sealed under the password: the writer label of the share's writes.</summary>
    [BsonElement("writer")]
    public byte[] Writer { get; set; } = [];

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    [BsonElement("lastUsedAt"), BsonIgnoreIfNull]
    public DateTime? LastUsedAt { get; set; }

    [BsonElement("revokedAt"), BsonIgnoreIfNull]
    public DateTime? RevokedAt { get; set; }

    /// <summary>From then on the share is refused, as if revoked. Bound into the seal of <see cref="Writer"/>.</summary>
    [BsonElement("expiresAt"), BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }
}

public sealed class DavShareLabelTakenException(string label) : Exception($"You already have a WebDAV share labelled '{label}'.");

/// <summary>The user holds <see cref="DavShares.MaxActive"/> shares that are neither revoked nor expired.</summary>
public sealed class DavShareLimitException(int limit) : Exception($"You can have at most {limit} WebDAV shares; revoke one you no longer use first.");
