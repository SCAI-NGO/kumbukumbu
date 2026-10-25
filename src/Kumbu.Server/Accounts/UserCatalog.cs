// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Crypto;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>
/// Users and the slots of their user key (accounts design §3). Each user has a random user key that will wrap
/// the keys of their vaults; it is itself wrapped by the password (Argon2id) and by the recovery key. MongoDB
/// holds the user key only wrapped, and never a password or recovery key.
/// </summary>
public sealed class UserCatalog(IMongoDatabase db, Argon2Kdf kdf, ApiKeys keys, TimeProvider time, ILogger<UserCatalog> log)
{
    private const string KeyIdLabel = "kumbu/v1/user-key-id";
    private const string RecoveryKekLabel = "kumbu/v1/user-recovery-kek";
    private const int MaxUsernameLength = 64;

    private const string UsernameRule = "A username has 1 to 64 characters: letters a-z, digits, '.', '-' or '_'.";

    /// <summary>Signing in as an unknown user runs Argon2 against this, so it takes as long as a wrong password.</summary>
    private static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(Argon2Kdf.SaltBytes);

    /// <summary>No user has this id (ids are hex): the slot lookup for an unknown username runs against it.</summary>
    private const string DummyId = "-";

    private volatile bool _ready;

    internal IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    internal IMongoCollection<UserSlotDocument> Slots { get; } = db.GetCollection<UserSlotDocument>("user_slots");

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>The partial unique index that allows one <c>primary_admin</c> at most, whichever way users are created.</summary>
    private const string OnePrimaryAdminIndex = "one_primary_admin";

    /// <summary>
    /// Creates a user with a new user key and returns the recovery key, formatted for humans. Throws
    /// <see cref="ArgumentException"/> for an unusable username, role or password,
    /// <see cref="UsernameTakenException"/> if the username exists, also when another create won a race for it,
    /// and <see cref="PrimaryAdminExistsException"/> for a second primary admin.
    /// </summary>
    public async Task<(User User, string RecoveryKey)> CreateAsync(string username, string password, string role, CancellationToken ct)
    {
        var name = NormaliseUsername(username) ?? throw new ArgumentException(UsernameRule, nameof(username));
        if (!Roles.IsKnown(role)) throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        if (PasswordPolicy.Check(password) is { } reason) throw new ArgumentException(reason, nameof(password));
        await EnsureReadyAsync(ct);

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var userKey = VaultCrypto.NewKey();
        var recoveryKey = RecoveryKey.New();

        // Slots first, under a fresh id: if the username turns out to be taken they are removed again, and a
        // crash in between leaves only slots of no user, never a user without a way in.
        await Slots.InsertManyAsync([await PasswordSlotAsync(id, userKey, password, ct), RecoverySlot(id, userKey, recoveryKey)], cancellationToken: ct);
        var user = new UserDocument
        {
            Id = id,
            Username = name,
            // The typed form only if it is plain ASCII: "K" through the Kelvin sign or "İ" lowers to the ASCII
            // username but would show as something else, which defeats the ASCII-only rule.
            Display = Ascii.IsValid(username) ? username : name,
            Role = role,
            Active = true,
            KeyId = KeyId(userKey, id),
            CreatedAt = Now,
        };
        try
        {
            await Users.InsertOneAsync(user, cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            await Slots.DeleteManyAsync(s => s.User == id, ct);
            if (e.WriteError.Message.Contains(OnePrimaryAdminIndex, StringComparison.Ordinal)) throw new PrimaryAdminExistsException();
            throw new UsernameTakenException(name);
        }

        log.LogInformation("Created user {Username} ({Role})", name, role);
        return (user.ToUser(), RecoveryKey.Format(recoveryKey));
    }

    /// <summary>
    /// The user and their user key if the password is right and the user active; else null after the same work.
    /// <c>CredentialReadAt</c> is taken before the password slot is read, for <see cref="Sessions.StartAsync"/>.
    /// </summary>
    public async Task<(User User, byte[] UserKey, DateTime CredentialReadAt)?> TrySignInAsync(string username, string password, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var readAt = Now;
        // The same two lookups for every username, known, unknown or invalid, so the round trips don't tell either.
        var name = NormaliseUsername(username) ?? "";
        var user = await Users.Find(u => u.Username == name).FirstOrDefaultAsync(ct);
        var slot = await FindSlotAsync(user?.Id ?? DummyId, UserSlotDocument.Password, ct);
        if (user is null || slot?.Salt is null)
        {
            if (password.Length > 0) await kdf.DeriveAsync(password, DummySalt, ct);
            return null;
        }

        if (await OpenPasswordSlotAsync(slot, password, ct) is not { } userKey || !user.Active) return null;

        var now = Now;
        user.PreviousSignInAt = user.LastSignInAt;
        user.LastSignInAt = now;
        await Users.UpdateOneAsync(
            u => u.Id == user.Id,
            Builders<UserDocument>.Update.Set(u => u.LastSignInAt, now).Set(u => u.PreviousSignInAt, user.PreviousSignInAt),
            cancellationToken: ct);
        return (user.ToUser(), userKey, readAt);
    }

    /// <summary>
    /// Forgot password: with the recovery key, sets a new password, ends every session of the user, revokes every
    /// API key and replaces the recovery key, so the used one opens nothing any more. Returns the new recovery key,
    /// formatted for humans; null if the user or key is unknown or wrong; <see cref="ArgumentException"/> if the
    /// new password is refused.
    /// </summary>
    public async Task<string?> RecoverAsync(string username, string recoveryKey, string newPassword, CancellationToken ct)
    {
        if (PasswordPolicy.Check(newPassword) is { } reason) throw new ArgumentException(reason, nameof(newPassword));
        await EnsureReadyAsync(ct);
        if (!RecoveryKey.TryParse(recoveryKey, out var key)) return null;
        var user = NormaliseUsername(username) is { } name ? await Users.Find(u => u.Username == name && u.Active).FirstOrDefaultAsync(ct) : null;
        if (user is null || await FindSlotAsync(user.Id, UserSlotDocument.Recovery, ct) is not { } slot) return null;

        byte[] userKey;
        try
        {
            userKey = VaultCrypto.Open(RecoveryKek(key, user.Id), slot.Wrapped, SlotContext(user.Id, UserSlotDocument.Recovery));
        }
        catch (CryptographicException)
        {
            return null;
        }

        await SetPasswordAsync(user.ToUser(), userKey, newPassword, revokeApiKeys: true, ct);
        // From here on the request finishes even if the client gives up. The recovery key goes last: a crash
        // before it leaves the old key working, so the user simply recovers again; a crash after it, before the
        // answer arrives, leaves the user the password they just chose, to sign in with and make a new recovery
        // key in the settings.
        var newKey = await ReplaceRecoveryKeyAsync(user.Id, userKey, CancellationToken.None);
        log.LogInformation("User {Username} set a new password with their recovery key; the API keys are revoked and the recovery key replaced", user.Username);
        return newKey;
    }

    /// <summary>
    /// Replaces the password; false if <paramref name="oldPassword"/> is wrong or doesn't open
    /// <paramref name="userKey"/>. Ends every session, as a changed password must, and with
    /// <paramref name="revokeApiKeys"/> revokes every API key of the user.
    /// </summary>
    public async Task<bool> ChangePasswordAsync(User user, byte[] userKey, string oldPassword, string newPassword, bool revokeApiKeys, CancellationToken ct)
    {
        if (PasswordPolicy.Check(newPassword) is { } reason) throw new ArgumentException(reason, nameof(newPassword));
        await EnsureReadyAsync(ct);
        if (await FindSlotAsync(user.Id, UserSlotDocument.Password, ct) is not { Salt: not null } slot
            || await OpenPasswordSlotAsync(slot, oldPassword, ct) is not { } opened
            || !CryptographicOperations.FixedTimeEquals(opened, userKey))
        {
            return false;
        }

        await SetPasswordAsync(user, userKey, newPassword, revokeApiKeys, ct);
        return true;
    }

    /// <summary>
    /// A new recovery key for the user, the old one stops working at once; null if <paramref name="currentPassword"/>
    /// is wrong or doesn't open <paramref name="userKey"/>. The password is asked for because a stolen session
    /// alone must not be able to make a recovery key, set a new password with it and lock the owner out.
    /// </summary>
    public async Task<string?> RegenerateRecoveryKeyAsync(string userId, byte[] userKey, string currentPassword, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var user = await Users.Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        // The password must open this very user key: a wrong key would leave a recovery slot that opens to
        // nothing, and the user would lose recovery unnoticed.
        if (user is null
            || await FindSlotAsync(userId, UserSlotDocument.Password, ct) is not { Salt: not null } passwordSlot
            || await OpenPasswordSlotAsync(passwordSlot, currentPassword, ct) is not { } opened
            || !CryptographicOperations.FixedTimeEquals(opened, userKey))
        {
            return null;
        }

        var recoveryKey = await ReplaceRecoveryKeyAsync(userId, userKey, ct);
        log.LogInformation("User {Username} has a new recovery key", user.Username);
        return recoveryKey;
    }

    /// <summary>
    /// A new recovery key for <paramref name="userKey"/>, which the caller has checked is the user's. The slot is
    /// replaced in one write, so there is always exactly one recovery slot: the old one until the write, the new
    /// one after it.
    /// </summary>
    private async Task<string> ReplaceRecoveryKeyAsync(string userId, byte[] userKey, CancellationToken ct)
    {
        var recoveryKey = RecoveryKey.New();
        var slot = RecoverySlot(userId, userKey, recoveryKey);
        await Slots.UpdateOneAsync(
            s => s.User == userId && s.Kind == UserSlotDocument.Recovery,
            Builders<UserSlotDocument>.Update.Set(s => s.Wrapped, slot.Wrapped).Set(s => s.CreatedAt, slot.CreatedAt),
            new UpdateOptions { IsUpsert = true },
            ct);
        return RecoveryKey.Format(recoveryKey);
    }

    /// <summary>
    /// Lower case, or null if it isn't a username: 1 to 64 ASCII letters, digits, '.', '-' or '_'. ASCII only,
    /// so no two usernames look alike (a Cyrillic "а" in "аdmin") and no Unicode normalisation is needed.
    /// </summary>
    private static string? NormaliseUsername(string username)
    {
        var name = username.ToLowerInvariant();
        return name.Length is > 0 and <= MaxUsernameLength && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-' or '_')
            ? name
            : null;
    }

    private async Task SetPasswordAsync(User user, byte[] userKey, string password, bool revokeApiKeys, CancellationToken ct)
    {
        var userId = user.Id;
        var slot = await PasswordSlotAsync(userId, userKey, password, ct);
        // Stamped before the new slot, so sessions of the old password are refused even if a crash leaves their
        // slots behind (a crash before the slot write only leaves the old password in place, signed out) ...
        await StampCredentialsChangedAsync(userId, ct);
        // The keys go after the stamp, so a key whose creation raced it is either found here or refused (see
        // ApiKeys.CreateAsync), and before the new password, so a crash in between leaves the keys revoked and the
        // old password in place: the safe way round, and the user simply tries again.
        if (revokeApiKeys) await keys.RevokeAllAsync(user, ct);
        await Slots.UpdateOneAsync(
            s => s.User == userId && s.Kind == UserSlotDocument.Password,
            Builders<UserSlotDocument>.Update.Set(s => s.Salt, slot.Salt).Set(s => s.Wrapped, slot.Wrapped).Set(s => s.CreatedAt, slot.CreatedAt),
            new UpdateOptions { IsUpsert = true },
            ct);
        // ... and again after it: a sign-in may have read the old slot between the first stamp and the write,
        // and its session's createdAt (the read time) is then before this one.
        await StampCredentialsChangedAsync(userId, ct);
        await Slots.DeleteManyAsync(s => s.User == userId && s.Kind == UserSlotDocument.Session, ct);
    }

    private Task StampCredentialsChangedAsync(string userId, CancellationToken ct) =>
        Users.UpdateOneAsync(u => u.Id == userId, Builders<UserDocument>.Update.Set(u => u.CredentialsChangedAt, Now), cancellationToken: ct);

    private Task<UserSlotDocument?> FindSlotAsync(string userId, string kind, CancellationToken ct) =>
        Slots.Find(s => s.User == userId && s.Kind == kind).FirstOrDefaultAsync(ct)!;

    private async Task<byte[]?> OpenPasswordSlotAsync(UserSlotDocument slot, string password, CancellationToken ct)
    {
        // Argon2 refuses an empty password, and no stored password is empty.
        if (password.Length == 0) return null;
        var kek = await kdf.DeriveAsync(password, slot.Salt!, ct);
        try
        {
            return VaultCrypto.Open(kek, slot.Wrapped, SlotContext(slot.User, UserSlotDocument.Password));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private async Task<UserSlotDocument> PasswordSlotAsync(string userId, byte[] userKey, string password, CancellationToken ct)
    {
        var salt = RandomNumberGenerator.GetBytes(Argon2Kdf.SaltBytes);
        return new UserSlotDocument
        {
            User = userId,
            Kind = UserSlotDocument.Password,
            Salt = salt,
            Wrapped = VaultCrypto.Seal(await kdf.DeriveAsync(password, salt, ct), userKey, SlotContext(userId, UserSlotDocument.Password)),
            CreatedAt = Now,
        };
    }

    private UserSlotDocument RecoverySlot(string userId, byte[] userKey, byte[] recoveryKey) => new()
    {
        User = userId,
        Kind = UserSlotDocument.Recovery,
        Wrapped = VaultCrypto.Seal(RecoveryKek(recoveryKey, userId), userKey, SlotContext(userId, UserSlotDocument.Recovery)),
        CreatedAt = Now,
    };

    private static byte[] RecoveryKek(byte[] recoveryKey, string userId) =>
        VaultCrypto.Derive(recoveryKey, RecoveryKekLabel, Encoding.UTF8.GetBytes(userId));

    private static byte[] KeyId(byte[] userKey, string userId) => VaultCrypto.Derive(userKey, KeyIdLabel, Encoding.UTF8.GetBytes(userId));

    /// <summary>The associated data of a slot: it opens only for this user and this kind of credential.</summary>
    private static string SlotContext(string userId, string kind) => $"user:{userId}:{kind}";

    /// <summary>Creates the indexes once per process; lazy, so the server starts before MongoDB is reachable.</summary>
    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        await Users.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<UserDocument>(Builders<UserDocument>.IndexKeys.Ascending(u => u.Username), new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<UserDocument>(
                    Builders<UserDocument>.IndexKeys.Ascending(u => u.Role),
                    new CreateIndexOptions<UserDocument>
                    {
                        Name = OnePrimaryAdminIndex,
                        Unique = true,
                        PartialFilterExpression = Builders<UserDocument>.Filter.Eq(u => u.Role, Roles.PrimaryAdmin),
                    }),
            ],
            ct);
        var slotKeys = Builders<UserSlotDocument>.IndexKeys;
        await Slots.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<UserSlotDocument>(slotKeys.Ascending(s => s.User)),

                // One password and one recovery slot per user; sessions are many.
                new CreateIndexModel<UserSlotDocument>(
                    slotKeys.Ascending(s => s.User).Ascending(s => s.Kind),
                    new CreateIndexOptions<UserSlotDocument>
                    {
                        Unique = true,
                        PartialFilterExpression = Builders<UserSlotDocument>.Filter.In(s => s.Kind, [UserSlotDocument.Password, UserSlotDocument.Recovery]),
                    }),
            ],
            ct);
        _ready = true;
    }
}

public sealed class UsernameTakenException(string username) : Exception($"The username '{username}' is taken.");

public sealed class PrimaryAdminExistsException() : Exception("There is a primary admin already; there can only be one.");
