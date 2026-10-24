// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Crypto;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>
/// Web sessions (accounts design, "Sessions"): a random secret in the browser's cookie and, in
/// <c>user_slots</c>, a <c>session</c> slot with the user key wrapped under a key derived from that secret. The
/// server keeps neither the secret nor the user key, so a request can only use the user key if it brings the
/// secret, and a restart signs nobody out.
/// </summary>
public sealed class Sessions(IMongoDatabase db, TimeProvider time)
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromHours(12);

    /// <summary>A burst of requests shouldn't write on each; the idle clock may therefore run up to this much early.</summary>
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(1);

    private const int SecretBytes = 32;
    private const string LookupLabel = "kumbu/v1/session-lookup";
    private const string KekLabel = "kumbu/v1/session-kek";

    private volatile bool _ready;

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    private IMongoCollection<UserSlotDocument> Slots { get; } = db.GetCollection<UserSlotDocument>("user_slots");

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Starts a session for a user just signed in and returns its secret (Base64Url) for the cookie.
    /// <paramref name="userKey"/> must come from a verified credential (<see cref="UserCatalog.TrySignInAsync"/>):
    /// nothing here checks it, and the session would hand on whatever key it got. <paramref name="credentialReadAt"/>
    /// is when that credential was read, before Argon2; it becomes the session's <c>createdAt</c>, so a password
    /// change that lands while the sign-in is still in Argon2 refuses the session too.
    /// </summary>
    public async Task<string> StartAsync(User user, byte[] userKey, DateTime credentialReadAt, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        var now = Now;
        await Slots.InsertOneAsync(
            new UserSlotDocument
            {
                User = user.Id,
                Kind = UserSlotDocument.Session,
                Lookup = Lookup(secret),
                Wrapped = VaultCrypto.Seal(Kek(secret, user.Id), userKey, SlotContext(user.Id)),
                CreatedAt = credentialReadAt,
                ExpiresAt = now + AbsoluteTimeout,
                LastUsedAt = now,
            },
            cancellationToken: ct);
        return Base64Url.EncodeToString(secret);
    }

    /// <summary>
    /// The user and their user key, or null if the session is unknown, ended, idle too long, past its absolute
    /// limit, older than the user's last password change or recovery, or the user is inactive.
    /// </summary>
    public async Task<(User User, byte[] UserKey)?> ResolveAsync(string secret, CancellationToken ct)
    {
        if (Decode(secret) is not { } bytes) return null;
        await EnsureReadyAsync(ct);
        var lookup = Lookup(bytes);
        var slot = await Slots.Find(s => s.Kind == UserSlotDocument.Session && s.Lookup == lookup).FirstOrDefaultAsync(ct);
        if (slot is null) return null;

        // The TTL index removes expired slots only eventually (MongoDB's sweep runs once a minute), and idle
        // ones not at all before their absolute limit, so the times are checked here.
        var now = Now;
        if (slot.ExpiresAt is not { } expiresAt || slot.LastUsedAt is not { } lastUsedAt || now >= expiresAt || now - lastUsedAt > IdleTimeout)
        {
            await Slots.DeleteOneAsync(s => s.Id == slot.Id, ct);
            return null;
        }

        var user = await Users.Find(u => u.Id == slot.User).FirstOrDefaultAsync(ct);
        // A password change or recovery deletes the sessions after it sets the new password; this catches the
        // ones a crash in between would have left behind, and those of a sign-in that read the old password.
        // At most equal, not only earlier: MongoDB keeps milliseconds, so a read just before the change can
        // carry the change's very time; a sign-in in the same millisecond merely has to sign in again.
        if (user is null || !user.Active || slot.CreatedAt <= user.CredentialsChangedAt)
        {
            await Slots.DeleteOneAsync(s => s.Id == slot.Id, ct);
            return null;
        }

        byte[] userKey;
        try
        {
            userKey = VaultCrypto.Open(Kek(bytes, user.Id), slot.Wrapped, SlotContext(user.Id));
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (now - lastUsedAt >= LastUsedGranularity)
        {
            // Conditional, so of concurrent requests only one writes.
            var stale = now - LastUsedGranularity;
            await Slots.UpdateOneAsync(
                s => s.Id == slot.Id && s.LastUsedAt <= stale,
                Builders<UserSlotDocument>.Update.Set(s => s.LastUsedAt, now),
                cancellationToken: ct);
        }

        return (user.ToUser(), userKey);
    }

    /// <summary>Signs out: the session's slot is deleted, so its secret opens nothing any more.</summary>
    public async Task EndAsync(string secret, CancellationToken ct)
    {
        if (Decode(secret) is not { } bytes) return;
        var lookup = Lookup(bytes);
        await Slots.DeleteOneAsync(s => s.Kind == UserSlotDocument.Session && s.Lookup == lookup, ct);
    }

    /// <summary>Ends every session of the user, except the one of <paramref name="exceptSecret"/> if given.</summary>
    public async Task EndAllAsync(string userId, string? exceptSecret, CancellationToken ct)
    {
        var filter = Builders<UserSlotDocument>.Filter;
        var sessions = filter.Eq(s => s.User, userId) & filter.Eq(s => s.Kind, UserSlotDocument.Session);
        if (exceptSecret is not null && Decode(exceptSecret) is { } bytes) sessions &= filter.Ne(s => s.Lookup, Lookup(bytes));
        await Slots.DeleteManyAsync(sessions, ct);
    }

    /// <summary>Ends every session of the user inside the caller's transaction (deactivating the user).</summary>
    internal Task EndAllAsync(IClientSessionHandle session, string userId, CancellationToken ct) =>
        Slots.DeleteManyAsync(session, s => s.User == userId && s.Kind == UserSlotDocument.Session, cancellationToken: ct);

    private static byte[]? Decode(string secret)
    {
        // Checked first: TryDecodeFromChars throws, rather than returning false, on a character outside Base64Url.
        if (secret.Length != Base64Url.GetEncodedLength(SecretBytes) || !Base64Url.IsValid(secret)) return null;
        var bytes = new byte[SecretBytes];
        return Base64Url.TryDecodeFromChars(secret, bytes, out var written) && written == SecretBytes ? bytes : null;
    }

    /// <summary>Finds the slot of a secret without storing the secret; HKDF suffices, as the secret is random.</summary>
    private static byte[] Lookup(byte[] secret) => VaultCrypto.Derive(secret, LookupLabel, []);

    private static byte[] Kek(byte[] secret, string userId) => VaultCrypto.Derive(secret, KekLabel, Encoding.UTF8.GetBytes(userId));

    /// <summary>The same associated data as the user's other slots: it opens only as this user's session slot.</summary>
    private static string SlotContext(string userId) => $"user:{userId}:{UserSlotDocument.Session}";

    /// <summary>Creates the indexes once per process; lazy, so the server starts before MongoDB is reachable.</summary>
    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        var keys = Builders<UserSlotDocument>.IndexKeys;
        await Slots.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<UserSlotDocument>(
                    keys.Ascending(s => s.Lookup),
                    new CreateIndexOptions<UserSlotDocument>
                    {
                        Unique = true,
                        PartialFilterExpression = Builders<UserSlotDocument>.Filter.Eq(s => s.Kind, UserSlotDocument.Session)
                            & Builders<UserSlotDocument>.Filter.Exists(s => s.Lookup),
                    }),

                // MongoDB deletes a slot once its expiresAt has passed; only sessions have one.
                new CreateIndexModel<UserSlotDocument>(keys.Ascending(s => s.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
            ],
            ct);
        _ready = true;
    }
}
