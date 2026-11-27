// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Crypto;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>A browser's offline mirror key, sealed (collection <c>mirror_slots</c>, offline mode design §4.2).</summary>
[BsonIgnoreExtraElements]
public sealed class MirrorSlotDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("user")]
    public string User { get; set; } = "";

    /// <summary>Finds the slot of a token without storing the token (HKDF of it, as a session's lookup).</summary>
    [BsonElement("lookup")]
    public byte[] Lookup { get; set; } = [];

    /// <summary>The mirror key, sealed under a key derived from the user key and the token: neither the database alone,
    /// nor the user key without the browser's token, opens it.</summary>
    [BsonElement("wrapped")]
    public byte[] Wrapped { get; set; } = [];

    /// <summary>What the device list calls it ("Firefox on Linux"), as the browser named itself.</summary>
    [BsonElement("label")]
    public string Label { get; set; } = "";

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    /// <summary>The browser's last contact (<see cref="MirrorSlots.ContactAsync"/>); null before the first.</summary>
    [BsonElement("contactAt"), BsonIgnoreIfNull]
    public DateTime? ContactAt { get; set; }

    /// <summary>The changes the browser had waiting to upload at its last contact: what a removal of the slot purges.
    /// Null: that contact couldn't count them (an unlock, before the copy opens), so nobody knows; never read as none.</summary>
    [BsonElement("waiting")]
    public int? Waiting { get; set; } = 0;

    /// <summary>The session (<see cref="Sessions.SubjectOf"/>) that made the slot or last contacted it: the device list
    /// marks it as this browser's for that session.</summary>
    [BsonElement("session"), BsonIgnoreIfNull]
    public string? Session { get; set; }
}

/// <summary>A slot as the device list shows it. <c>Current</c>: the session that asks made it or last contacted it.</summary>
public sealed record MirrorSlot(string Id, string Label, DateTime CreatedAt, DateTime? ContactAt, int? Waiting, bool Current);

/// <summary>
/// The offline mirror's key slots (offline mode design §4.2, §4.4). One per browser: a random mirror key, made here and
/// sealed under the user key and a random token the browser keeps in the clear, beside its sealed mirror. A signed-in tab
/// hands in the token at every contact and gets the key back, so it unwraps its mirror without asking for the password.
/// A slot ends with its removal from the device list, a rotation of the user key, the user's deactivation and the
/// account's deletion; a browser that then hands in its token is told to purge its whole mirror.
/// </summary>
public sealed class MirrorSlots(IMongoDatabase db, TimeProvider time)
{
    /// <summary>Slots per user: a person's browsers are few, and each costs a document.</summary>
    public const int Cap = 20;

    public const int MaxLabelLength = 64;

    /// <summary>The changes waiting a contact may report; more is no browser's mirror.</summary>
    public const int MaxWaiting = 1_000_000;

    private const int TokenBytes = 32;
    private const string LookupLabel = "kumbu/v1/mirror-lookup";
    private const string KekLabel = "kumbu/v1/mirror-kek";
    private const string TagLabel = "kumbu/v1/mirror-vault-tag";

    private volatile bool _ready;

    internal IMongoCollection<MirrorSlotDocument> Slots { get; } = db.GetCollection<MirrorSlotDocument>(CollectionName);

    public const string CollectionName = "mirror_slots";

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// A new slot for the user: its id, the token (Base64Url) and the mirror key. Only under the user key in force and
    /// for an active user (<see cref="CredentialsChangedException"/> if not), and at most <see cref="Cap"/> of them
    /// (<see cref="MirrorSlotLimitException"/>). In one transaction with a write to the user's document, so two at once
    /// count each other, and none is left behind by a deletion of the account or a rotation of its key at the same time.
    /// </summary>
    public async Task<(string Id, string Token, byte[] Key)> CreateAsync(string userId, byte[] userKey, string label, string? sessionSubject, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var token = RandomNumberGenerator.GetBytes(TokenBytes);
        var key = VaultCrypto.NewKey();
        var now = Now;
        var slot = new MirrorSlotDocument
        {
            Id = ObjectId.GenerateNewId(),
            User = userId,
            Lookup = Lookup(token),
            Label = label,
            CreatedAt = now,
            Session = sessionSubject,
        };
        slot.Wrapped = VaultCrypto.Seal(Kek(userId, userKey, token), key, Context(userId, slot.Id));
        var keyId = UserCatalog.KeyId(userKey, userId);

        using (var session = await db.Client.StartSessionAsync(cancellationToken: ct))
        {
            await session.WithTransactionAsync(
                async (s, t) =>
                {
                    var current = await Users.UpdateOneAsync(
                        s, u => u.Id == userId && u.Active && u.KeyId == keyId, Builders<UserDocument>.Update.Set(u => u.MirrorSlotsAt, now), cancellationToken: t);
                    if (current.MatchedCount == 0) throw new CredentialsChangedException();
                    if (await Slots.CountDocumentsAsync(s, m => m.User == userId, cancellationToken: t) >= Cap) throw new MirrorSlotLimitException();
                    await Slots.InsertOneAsync(s, slot, cancellationToken: t);
                    return true;
                },
                cancellationToken: ct);
        }

        return (slot.Id.ToString(), Base64Url.EncodeToString(token), key);
    }

    /// <summary>
    /// The browser of <paramref name="token"/> is in contact: its slot records the time, the changes it has waiting and
    /// the session, and the slot's id and mirror key are answered. Null when the user has no slot of this token (another
    /// user's token is none of hers, and is left untouched: <see cref="ExistsAsync"/> tells the two apart), or when it
    /// doesn't open (sealed under a user key rotated since); such a slot is deleted, and the browser purges its mirror.
    /// </summary>
    public async Task<(string Id, byte[] Key)?> ContactAsync(
        string userId, byte[] userKey, byte[] token, int? waiting, string? sessionSubject, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var lookup = Lookup(token);
        var slot = await Slots.FindOneAndUpdateAsync(
            m => m.User == userId && m.Lookup == lookup,
            Builders<MirrorSlotDocument>.Update.Set(m => m.ContactAt, Now).Set(m => m.Waiting, waiting).Set(m => m.Session, sessionSubject),
            cancellationToken: ct);
        if (slot is null) return null;
        try
        {
            return (slot.Id.ToString(), VaultCrypto.Open(Kek(userId, userKey, token), slot.Wrapped, Context(userId, slot.Id)));
        }
        catch (CryptographicException)
        {
            await Slots.DeleteOneAsync(m => m.Id == slot.Id, ct);
            return null;
        }
    }

    /// <summary>The user's slots for the device list, oldest first.</summary>
    public async Task<IReadOnlyList<MirrorSlot>> ListAsync(string userId, string? sessionSubject, CancellationToken ct)
    {
        var slots = await Slots.Find(m => m.User == userId).SortBy(m => m.CreatedAt).ToListAsync(ct);
        return [.. slots.Select(m => new MirrorSlot(m.Id.ToString(), m.Label, m.CreatedAt, m.ContactAt, m.Waiting, sessionSubject is not null && m.Session == sessionSubject))];
    }

    /// <summary>Renames the user's slot <paramref name="id"/> in the device list; false if the user has none of that id.</summary>
    public async Task<bool> RenameAsync(string userId, string id, string label, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var objectId)) return false;
        var update = Builders<MirrorSlotDocument>.Update.Set(m => m.Label, label);
        return (await Slots.UpdateOneAsync(m => m.Id == objectId && m.User == userId, update, cancellationToken: ct)).MatchedCount > 0;
    }

    /// <summary>Deletes the user's slot <paramref name="id"/> ("Remove offline copy"); false if the user has none of that id.</summary>
    public async Task<bool> DeleteAsync(string userId, string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var objectId)) return false;
        return (await Slots.DeleteOneAsync(m => m.Id == objectId && m.User == userId, ct)).DeletedCount > 0;
    }

    /// <summary>Every slot of the user, inside the caller's transaction (a rotation of the user key, a deactivation, the account's deletion).</summary>
    internal static Task DeleteAllAsync(IMongoDatabase db, IClientSessionHandle session, string userId, CancellationToken ct) =>
        db.GetCollection<MirrorSlotDocument>(CollectionName).DeleteManyAsync(session, m => m.User == userId, cancellationToken: ct);

    /// <summary>Whether a slot of <paramref name="token"/> exists, whoever's: for the 401 of a browser no longer signed in,
    /// which can't say whose it is, and for a contact with another user's token. The token is random, so this tells nothing to whoever doesn't hold it.</summary>
    public async Task<bool> ExistsAsync(byte[] token, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var lookup = Lookup(token);
        return await Slots.Find(m => m.Lookup == lookup).AnyAsync(ct);
    }

    /// <summary>
    /// What the browser of <paramref name="token"/> compares to the tag it mirrored a vault under: it changes when the
    /// vault's key does (a re-keying), and then the mirror of that vault is purged. Keyed by the token, so two browsers'
    /// tags of one vault can't be matched, and nothing of the vault key is told.
    /// </summary>
    public static string VaultTag(byte[] token, string vaultId, byte[] vaultKeyId)
    {
        var mac = VaultCrypto.Mac(VaultCrypto.Derive(token, TagLabel, []), [.. Encoding.UTF8.GetBytes(vaultId), 0, .. vaultKeyId]);
        return Base64Url.EncodeToString(mac.AsSpan(0, 16));
    }

    /// <summary>The token a browser hands in, or null for anything that isn't one.</summary>
    public static byte[]? DecodeToken(string? token)
    {
        // Checked first: TryDecodeFromChars throws, rather than returning false, on a character outside Base64Url.
        if (token is null || token.Length != Base64Url.GetEncodedLength(TokenBytes) || !Base64Url.IsValid(token)) return null;
        var bytes = new byte[TokenBytes];
        return Base64Url.TryDecodeFromChars(token, bytes, out var written) && written == TokenBytes ? bytes : null;
    }

    private static byte[] Lookup(byte[] token) => VaultCrypto.Derive(token, LookupLabel, []);

    private static byte[] Kek(string userId, byte[] userKey, byte[] token) =>
        VaultCrypto.Derive(userKey, KekLabel, [.. Encoding.UTF8.GetBytes(userId), 0, .. token]);

    private static string Context(string userId, ObjectId id) => $"mirror:{userId}:{id}";

    /// <summary>Creates the indexes once per process; lazy, so the server starts before MongoDB is reachable.</summary>
    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        var keys = Builders<MirrorSlotDocument>.IndexKeys;
        await Slots.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<MirrorSlotDocument>(keys.Ascending(m => m.Lookup), new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<MirrorSlotDocument>(keys.Ascending(m => m.User)),
            ],
            ct);
        _ready = true;
    }
}

/// <summary>The user has <see cref="MirrorSlots.Cap"/> slots already; nothing was made.</summary>
public sealed class MirrorSlotLimitException() : Exception($"You have {MirrorSlots.Cap} offline copies already; remove one in the device list first.");
