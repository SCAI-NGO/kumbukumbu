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
    /// <summary>The limits of a normal session, and of the normal window inside a remembered one.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromHours(12);

    /// <summary>The limits of a remembered session ("keep me signed in").</summary>
    public static readonly TimeSpan RememberedIdleTimeout = TimeSpan.FromDays(7);
    public static readonly TimeSpan RememberedAbsoluteTimeout = TimeSpan.FromDays(30);

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
    /// <see cref="CredentialsChangedException"/> if the user no longer exists (deleted meanwhile), is inactive, or has
    /// another user key now (rotated meanwhile). A <paramref name="remember"/>ed session lasts longer and opens its
    /// normal window with the password just given.
    /// </summary>
    public async Task<string> StartAsync(User user, byte[] userKey, DateTime credentialReadAt, CancellationToken ct, bool remember = false)
    {
        await EnsureReadyAsync(ct);
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        var keyId = UserCatalog.KeyId(userKey, user.Id);
        var now = Now;
        var slot = new UserSlotDocument
        {
            User = user.Id,
            Kind = UserSlotDocument.Session,
            Lookup = Lookup(secret),
            Wrapped = VaultCrypto.Seal(Kek(secret, user.Id), userKey, SlotContext(user.Id)),
            CreatedAt = credentialReadAt,
            ExpiresAt = now + (remember ? RememberedAbsoluteTimeout : AbsoluteTimeout),
            LastUsedAt = now,
        };
        if (remember)
        {
            slot.Remembered = true;
            slot.ConfirmedAt = slot.WindowUsedAt = now;
        }

        using (var session = await db.Client.StartSessionAsync(cancellationToken: ct))
        {
            await session.WithTransactionAsync(
                async (s, token) =>
                {
                    // A real write to the user's document, as for a new vault: a deletion of the account at the same
                    // time conflicts with it, so no session slot is left behind for a user who is gone. And only for the
                    // user key in force: a sign-in that read the password slot just before a rotation committed holds
                    // the old key, which opens no vault any more (UserKeyRotation).
                    var current = await Users.UpdateOneAsync(
                        s, u => u.Id == user.Id && u.Active && u.KeyId == keyId, Builders<UserDocument>.Update.Inc(u => u.SessionsStarted, 1), cancellationToken: token);
                    if (current.MatchedCount == 0) throw new CredentialsChangedException();
                    await Slots.InsertOneAsync(s, slot, cancellationToken: token);
                    return true;
                },
                cancellationToken: ct);
        }

        return Base64Url.EncodeToString(secret);
    }

    /// <summary>
    /// The user, their user key, when the session's credential was read (its <c>createdAt</c>) and whether its normal
    /// window is open, or null if the session is unknown, ended, idle too long, past its absolute limit, older than the
    /// user's last password change or recovery, or the user is inactive. A normal session ends after 30 minutes idle
    /// and 12 hours at most, and its window is always open; a remembered one ends after 7 days idle and 30 days at
    /// most, and its window is open for 30 minutes idle and 12 hours at most since the password was last given
    /// (<see cref="OpenWindowAsync"/>). Without <paramref name="touch"/> the request doesn't count as the session's use
    /// and moves no clock: the web app's poll in the background must not keep a session alive that nobody uses. The
    /// first touching request after 30 minutes without one (only a remembered session lives that long) begins a visit:
    /// the user's <c>lastSignInAt</c> moves to now, and the returned user carries it.
    /// </summary>
    public async Task<(User User, byte[] UserKey, DateTime CredentialReadAt, bool WindowOpen)?> ResolveAsync(string secret, CancellationToken ct, bool touch = true)
    {
        if (Decode(secret) is not { } bytes) return null;
        await EnsureReadyAsync(ct);
        var lookup = Lookup(bytes);
        var slot = await Slots.Find(s => s.Kind == UserSlotDocument.Session && s.Lookup == lookup).FirstOrDefaultAsync(ct);
        if (slot is null) return null;

        // The TTL index removes expired slots only eventually (MongoDB's sweep runs once a minute), and idle
        // ones not at all before their absolute limit, so the times are checked here.
        var now = Now;
        if (slot.ExpiresAt is not { } expiresAt || slot.LastUsedAt is not { } lastUsedAt || now >= expiresAt || now - lastUsedAt > IdleLimit(slot))
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

        // Once closed, the window stays closed until the password is given again: using the session doesn't reopen it.
        var windowOpen = !slot.Remembered
            || (slot.ConfirmedAt is { } confirmedAt && slot.WindowUsedAt is { } windowUsedAt
                && now < confirmedAt + AbsoluteTimeout && now - windowUsedAt <= IdleTimeout);

        if (touch && now - lastUsedAt >= LastUsedGranularity)
        {
            // Conditional on the exact value read, so of concurrent requests only one writes, and only that one can
            // begin a visit.
            var update = Builders<UserSlotDocument>.Update.Set(s => s.LastUsedAt, now);
            if (slot.Remembered && windowOpen) update = update.Set(s => s.WindowUsedAt, now);
            var touched = await Slots.UpdateOneAsync(s => s.Id == slot.Id && s.LastUsedAt == lastUsedAt, update, cancellationToken: ct);
            if (touched.ModifiedCount > 0 && now - lastUsedAt > IdleTimeout)
            {
                user.PreviousSignInAt = user.LastSignInAt;
                user.LastSignInAt = now;
                await Users.UpdateOneAsync(
                    u => u.Id == user.Id,
                    Builders<UserDocument>.Update.Set(u => u.LastSignInAt, now).Set(u => u.PreviousSignInAt, user.PreviousSignInAt),
                    cancellationToken: ct);
            }
        }

        return (user.ToUser(), userKey, slot.CreatedAt, windowOpen);
    }

    /// <summary>
    /// Opens the normal window of a remembered session again, with both its clocks from now; a normal session's
    /// window is always open, so nothing is written for one. Callers have resolved the session of
    /// <paramref name="secret"/> for this request and verified the user's password; the filter still matches only a
    /// remembered slot within both its limits, so a session that lapsed in between isn't brought back.
    /// </summary>
    public async Task OpenWindowAsync(string secret, CancellationToken ct)
    {
        if (Decode(secret) is not { } bytes) return;
        var lookup = Lookup(bytes);
        var now = Now;
        var idleSince = now - RememberedIdleTimeout;
        await Slots.UpdateOneAsync(
            s => s.Kind == UserSlotDocument.Session && s.Lookup == lookup && s.Remembered && s.ExpiresAt > now && s.LastUsedAt >= idleSince,
            Builders<UserSlotDocument>.Update.Set(s => s.ConfirmedAt, now).Set(s => s.WindowUsedAt, now).Set(s => s.LastUsedAt, now).Unset(s => s.WrongPasswords),
            cancellationToken: ct);
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

    private static TimeSpan IdleLimit(UserSlotDocument slot) => slot.Remembered ? RememberedIdleTimeout : IdleTimeout;

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

    /// <summary>
    /// For a rotation of the user key (<see cref="UserKeyRotation"/>): the session of <paramref name="secret"/>, its slot
    /// sealed again under the same secret with <paramref name="newUserKey"/>. Writes nothing. Null if the secret is no
    /// live session of this user (ended, expired or refused meanwhile): then no session is kept. The rotation moves the
    /// kept slot's <c>createdAt</c> past its stamp, so a session already refused must not be kept, or it would come back.
    /// </summary>
    internal static async Task<KeptSession?> PrepareKeepAsync(IMongoDatabase db, string userId, string secret, byte[] newUserKey, DateTime now, CancellationToken ct)
    {
        if (Decode(secret) is not { } bytes) return null;
        var lookup = Lookup(bytes);
        var slot = await db.GetCollection<UserSlotDocument>("user_slots")
            .Find(s => s.Kind == UserSlotDocument.Session && s.Lookup == lookup && s.User == userId).FirstOrDefaultAsync(ct);
        var user = await db.GetCollection<UserDocument>("users").Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        if (slot is null || user is null || slot.CreatedAt <= user.CredentialsChangedAt || !(now < slot.ExpiresAt) || slot.LastUsedAt is not { } lastUsed || now - lastUsed > IdleLimit(slot))
        {
            return null;
        }

        return new KeptSession(slot.Id, slot.Wrapped, VaultCrypto.Seal(Kek(bytes, userId), newUserKey, SlotContext(userId)));
    }

    /// <summary>The wrong passwords in a row that end a remembered session (<see cref="PasswordWrongAsync"/>).</summary>
    public const int WrongPasswordLimit = 5;

    /// <summary>
    /// Counts a wrong password given through the remembered session of <paramref name="secret"/>; the
    /// <see cref="WrongPasswordLimit"/>th in a row ends it (its slot deleted) and answers its user's id. Without this a
    /// stolen cookie would be a password-guessing oracle for 30 days at the rate of password checks; with it, it is
    /// good for at most <see cref="WrongPasswordLimit"/> guesses. A normal session counts nothing: its own limits are
    /// short, and the rate limit bounds it as a sign-in.
    /// </summary>
    public async Task<string?> PasswordWrongAsync(string secret, CancellationToken ct)
    {
        if (Decode(secret) is not { } bytes) return null;
        var lookup = Lookup(bytes);
        // Atomic, and decided on the count it returns: of concurrent wrong passwords, each sees its own.
        var slot = await Slots.FindOneAndUpdateAsync(
            s => s.Kind == UserSlotDocument.Session && s.Lookup == lookup && s.Remembered,
            Builders<UserSlotDocument>.Update.Inc(s => s.WrongPasswords, 1),
            new FindOneAndUpdateOptions<UserSlotDocument> { ReturnDocument = ReturnDocument.After },
            ct);
        if (slot is null || slot.WrongPasswords < WrongPasswordLimit) return null;
        var ended = await Slots.DeleteOneAsync(s => s.Id == slot.Id, ct);
        return ended.DeletedCount > 0 ? slot.User : null;
    }
}
