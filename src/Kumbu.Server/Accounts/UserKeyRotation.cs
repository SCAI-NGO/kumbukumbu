// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using Kumbu.Core.Crypto;
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>The current session's slot, sealed again under its cookie secret with the new user key.</summary>
public sealed record KeptSession(ObjectId SlotId, byte[] OldWrapped, byte[] NewWrapped);

/// <summary>A vault slot wrapped again under the new user key; <c>NewNameId</c> null: unset. A member slot also has its
/// alias key sealed under the new user key (<c>NewAliasKey</c>), and is written only if its alias is still
/// <c>OldAlias</c>, the one its new name hash was computed from: a rename in between makes the rotation conflict. A user
/// slot (<c>CheckNameId</c>) likewise only if its name hash is still <c>OldNameId</c>, the one read with the meta its new
/// hash was computed from, null for none: the owner's rename of her vault in between conflicts too.</summary>
public sealed record SlotRewrap(ObjectId Id, byte[] OldWrapped, byte[] NewWrapped, byte[]? NewNameId)
{
    public byte[]? OldAlias { get; init; }

    public byte[]? OldNameId { get; init; }

    public bool CheckNameId { get; init; }

    public byte[]? NewAliasKey { get; init; }
}

/// <summary>The user's new keypair (collab design §5.1), its private key sealed under the new user key, written only if
/// her private key is still <c>OldPrivateKey</c>; null <c>OldPrivateKey</c>: she had no keypair, and must still have none.</summary>
public sealed record KeypairRotation(byte[]? OldPrivateKey, byte[] NewPublicKey, byte[] NewPrivateKey);

/// <summary>A member slot to wrap to the new public key: the vault key her old private key opened from it, with that
/// key's public key, and the revision its membership was read at; the pin is sealed again only at that revision.</summary>
public sealed record MemberSlotRewrap(ObjectId SlotId, string MembershipId, int Revision, byte[] OldWrapped, byte[] OldPublicKey, byte[] VaultKey);

/// <summary>A member slot her old private key opens but that has no membership, and so no pin (collab design §5.4):
/// damaged, deleted in the rotation's transaction while it still holds <c>OldWrapped</c>.</summary>
public sealed record DamagedMemberSlot(ObjectId SlotId, string VaultId, byte[] OldWrapped);

/// <summary>An API key's or connection's meta sealed again under the new user key, with its name id. One that didn't
/// open has its old values as the new ones: it is left as it is, but still counted among the user's.</summary>
public sealed record MetaReseal(string Id, byte[] OldMeta, byte[]? OldNameId, byte[] NewMeta, byte[]? NewNameId);

/// <summary>The user's list of discreet vaults sealed again under the new user key, written only if it is still
/// <c>Old</c>; null <c>Old</c>: she had none, and must still have none. One that didn't open has its old value as the
/// new one: it is left as it is.</summary>
public sealed record ListReseal(byte[]? Old, byte[]? New);

/// <summary>
/// Everything a rotation writes, computed by <see cref="UserKeyRotation.PrepareAsync"/>; <see cref="UserKeyRotation.ApplyAsync"/>
/// writes it only where each document still holds the value it was prepared from. <c>RecoveryUsed</c>: in a recovery,
/// the recovery slot that was opened; <c>PasswordUsed</c>: the password slot that was read (null: there was none).
/// <c>SkippedSlotIds</c> are slots that matched the old lookup but didn't open; <c>VaultIds</c> go to <c>rekeyPending</c>.
/// </summary>
public sealed record PreparedRotation(
    string UserId, byte[] NewUserKey, string RecoveryKey,
    UserSlotDocument PasswordSlot, UserSlotDocument RecoverySlot, byte[]? RecoveryUsed,
    KeptSession? KeptSession,
    byte[] OldLookup, byte[] NewLookup, byte[] UserKeyId,
    IReadOnlyList<SlotRewrap> VaultSlots, IReadOnlyList<ObjectId> SkippedSlotIds, IReadOnlyList<ObjectId> OrphanSlotIds,
    IReadOnlyList<MetaReseal> ApiKeys, IReadOnlyList<MetaReseal> Connections,
    IReadOnlyList<string> VaultIds,
    byte[]? PasswordUsed)
{
    /// <summary>The new keypair, made on every rotation, so an old private key that leaks opens no member slot.</summary>
    public required KeypairRotation Keypair { get; init; }

    /// <summary>The member slots wrapped again to the new public key, in the rotation's transaction.</summary>
    public IReadOnlyList<MemberSlotRewrap> MemberSlots { get; init; } = [];

    /// <summary>The member slots without a membership, deleted in the rotation's transaction.</summary>
    public IReadOnlyList<DamagedMemberSlot> DamagedMemberSlots { get; init; } = [];

    /// <summary>Her notices of vaults deleted for everyone, their alias keys sealed again under the new user key (collab
    /// design §14).</summary>
    internal IReadOnlyList<Sharing.NoticeReseal> Notices { get; init; } = [];

    /// <summary>Her WebDAV shares' metas, sealed again under the new user key, as <see cref="ApiKeys"/>' are.</summary>
    public IReadOnlyList<MetaReseal> DavShareMetas { get; init; } = [];

    /// <summary>Her list of discreet vaults, sealed again under the new user key.</summary>
    public ListReseal DiscreetList { get; init; } = new(null, null);
}

/// <summary>Something the rotation was prepared from changed before it was applied; nothing was written.</summary>
public sealed class RotationConflictException() : Exception("Something changed while re-encrypting; try again.");

/// <summary>A recovery's rotation kept meeting concurrent changes; nothing was written but the stamp and the
/// revocations, so the recovery key still works.</summary>
public sealed class RecoveryConflictException() : Exception("Something changed during the recovery; try again.");

/// <summary>
/// Stage 1 of re-keying (rekeying design §2): a new random user key replaces the old one, with every slot and keyed
/// hash that hangs on it, in one transaction: the password slot (a new salt), a new recovery key, the current session
/// if one is kept (every other session ends), the vault slots with their lookup and name hash, the metas and name ids
/// of the API keys and connections, the key id, the credentials stamp and the vaults pending re-keying. Whatever a
/// crash leaves, the password and the vault slots open with the same key: the old one before the commit, the new
/// one after it. Works on the collections directly, as <see cref="ConnectionRevocation"/> does: the classes that own
/// them depend on <see cref="VaultCatalog"/>, and <see cref="UserCatalog"/> depends on this one.
/// </summary>
public sealed class UserKeyRotation(IMongoDatabase db, Argon2Kdf kdf, TimeProvider time, ILogger<UserKeyRotation> log)
{
    /// <summary>How many times a conflict is prepared again before the caller is told to try again.</summary>
    internal const int Attempts = 3;

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    private IMongoCollection<UserSlotDocument> UserSlots { get; } = db.GetCollection<UserSlotDocument>("user_slots");

    private IMongoCollection<SlotDocument> Slots { get; } = db.GetCollection<SlotDocument>("slots");

    private IMongoCollection<VaultDocument> Vaults { get; } = db.GetCollection<VaultDocument>("vaults");

    private IMongoCollection<ApiKeyDocument> Keys { get; } = db.GetCollection<ApiKeyDocument>("api_keys");

    private IMongoCollection<ConnectionDocument> ConnectionDocs { get; } = db.GetCollection<ConnectionDocument>(Accounts.Connections.CollectionName);

    private IMongoCollection<DavShareDocument> ShareDocs { get; } = db.GetCollection<DavShareDocument>(DavShares.CollectionName);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>For tests: called with <c>"before-apply"</c> (prepared, no transaction yet) and <c>"inside-apply"</c>
    /// (everything written, not committed); an exception thrown there is a crash at that point.</summary>
    internal Func<string, Task>? FaultAt { get; set; }

    /// <summary>
    /// Reads and computes everything and writes nothing, Argon2 for <paramref name="password"/> included: the new
    /// password slot wraps the new key under it. <paramref name="oldUserKey"/> must be the user's, checked by the caller.
    /// <see cref="RotationConflictException"/> if a creation of a vault may be under way (a young orphan slot).
    /// </summary>
    public async Task<PreparedRotation> PrepareAsync(
        User user, byte[] oldUserKey, string password, string? keepSessionSecret, byte[]? recoveryUsed, CancellationToken ct)
    {
        var userId = user.Id;
        var newUserKey = VaultCrypto.NewKey();
        var now = Now;
        // The cheap reads first, so a young orphan is met before Argon2.
        var vaults = await VaultCatalog.PrepareUserSlotsAsync(db, userId, oldUserKey, newUserKey, now, log, ct);
        var keys = await ApiKeys.PrepareResealAsync(db, userId, oldUserKey, newUserKey, log, ct);
        var connections = await Accounts.Connections.PrepareResealAsync(db, userId, oldUserKey, newUserKey, log, ct);
        var shares = await DavShares.PrepareResealAsync(db, userId, oldUserKey, newUserKey, log, ct);
        var kept = keepSessionSecret is null ? null : await Sessions.PrepareKeepAsync(db, userId, keepSessionSecret, newUserKey, now, ct);
        var passwordUsed = (await UserSlots.Find(s => s.User == userId && s.Kind == UserSlotDocument.Password).FirstOrDefaultAsync(ct))?.Wrapped;
        var (keypair, memberSlots, damaged) = await PrepareKeypairAsync(userId, oldUserKey, newUserKey, ct);

        var recoveryKey = RecoveryKey.New();
        return new PreparedRotation(
            userId,
            newUserKey,
            RecoveryKey.Format(recoveryKey),
            await UserCatalog.PasswordSlotAsync(kdf, userId, newUserKey, password, now, ct),
            UserCatalog.RecoverySlot(userId, newUserKey, recoveryKey, now),
            recoveryUsed,
            kept,
            VaultCatalog.UserSlotLookup(oldUserKey, userId),
            VaultCatalog.UserSlotLookup(newUserKey, userId),
            UserCatalog.KeyId(newUserKey, userId),
            vaults.Rewraps,
            vaults.Skipped,
            vaults.Orphans,
            keys,
            connections,
            vaults.VaultIds,
            passwordUsed)
        {
            Keypair = keypair,
            MemberSlots = memberSlots,
            DamagedMemberSlots = damaged,
            Notices = await Sharing.MemberNotices.PrepareResealAsync(db, userId, oldUserKey, newUserKey, ct),
            DavShareMetas = shares,
            DiscreetList = await PrepareDiscreetListAsync(userId, oldUserKey, newUserKey, ct),
        };
    }

    /// <summary>The list of discreet vaults sealed again under the new key; one that doesn't open with the old key is
    /// left as it is (it counts as unreadable before and after).</summary>
    private async Task<ListReseal> PrepareDiscreetListAsync(string userId, byte[] oldUserKey, byte[] newUserKey, CancellationToken ct)
    {
        var old = await Users.Find(u => u.Id == userId).Project(u => u.DiscreetVaults).FirstOrDefaultAsync(ct);
        if (old is null) return new ListReseal(null, null);
        if (DiscreetVaults.OpenList(userId, oldUserKey, old, out var failure) is not { } ids)
        {
            log.LogWarning("The discreet vaults of user {UserId} do not open: {Reason}; they are left as they are", userId, failure);
            return new ListReseal(old, old);
        }

        return new ListReseal(old, DiscreetVaults.SealList(userId, newUserKey, ids));
    }

    /// <summary>
    /// A new keypair for the user (collab design §5.1), and her member slots, each opened with her old private key (opened
    /// with the old user key) for the vault key, to be wrapped to the new public key. A private key that doesn't open,
    /// or a member slot that doesn't, is logged: it opened nothing before either, and is left as it is. A member slot
    /// without a membership has no pin to wrap to: it is damaged, and deleted (§5.4). The member slots are all those
    /// under the old lookup, those left as they are for their alias too: none may stay open to the old key.
    /// </summary>
    private async Task<(KeypairRotation Keypair, IReadOnlyList<MemberSlotRewrap> MemberSlots, IReadOnlyList<DamagedMemberSlot> Damaged)> PrepareKeypairAsync(
        string userId, byte[] oldUserKey, byte[] newUserKey, CancellationToken ct)
    {
        var doc = await Users.Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        byte[] newPublic, newPrivate;
        using (var made = MemberSlotCrypto.NewKeyPair())
        {
            newPublic = Sharing.MemberKeys.PublicFromPrivate(made);
            newPrivate = Sharing.MemberKeys.SealPrivate(userId, newUserKey, made);
        }

        var keypair = new KeypairRotation(doc?.PrivateKey, newPublic, newPrivate);
        if (doc?.PrivateKey is null) return (keypair, [], []);
        var f = Builders<SlotDocument>.Filter;
        var slots = await Slots.Find(f.Eq(s => s.Kind, SlotDocument.Member) & f.Eq(s => s.Lookup, VaultCatalog.UserSlotLookup(oldUserKey, userId))).ToListAsync(ct);
        if (slots.Count == 0) return (keypair, [], []);

        ECDiffieHellman oldKey;
        try
        {
            oldKey = Sharing.MemberKeys.OpenPrivate(doc, oldUserKey);
        }
        catch (CryptographicException)
        {
            log.LogWarning("The private key of user {UserId} does not open; their {Count} member slots open nothing and are left as they are", userId, slots.Count);
            return (keypair, [], []);
        }

        using (oldKey)
        {
            var oldPublic = Sharing.MemberKeys.PublicFromPrivate(oldKey);
            var memberships = (await db.GetCollection<Sharing.MembershipDocument>(Sharing.Memberships.CollectionName)
                .Find(Builders<Sharing.MembershipDocument>.Filter.In(m => m.Id, slots.Select(s => s.Membership ?? "")))
                .ToListAsync(ct)).ToDictionary(m => m.Id, StringComparer.Ordinal);
            var rewraps = new List<MemberSlotRewrap>();
            var damaged = new List<DamagedMemberSlot>();
            foreach (var slot in slots)
            {
                if (slot.Membership is not { } id || !memberships.TryGetValue(id, out var membership))
                {
                    // Read after the slots: a grant's membership commits with its slot, so this is no grant under way.
                    damaged.Add(new DamagedMemberSlot(slot.Id, slot.Vault, slot.Wrapped));
                    continue;
                }

                try
                {
                    rewraps.Add(new MemberSlotRewrap(slot.Id, id, membership.Revision, slot.Wrapped, oldPublic, VaultCatalog.OpenMemberSlot(oldKey, slot)));
                }
                catch (CryptographicException)
                {
                    log.LogWarning("A member slot of vault {VaultId} of user {UserId} does not open with their private key; it is left as it is", slot.Vault, userId);
                }
            }

            return (keypair, rewraps, damaged);
        }
    }

    /// <summary>
    /// Writes the prepared rotation inside the caller's transaction, each write only where the document still holds the
    /// value it was prepared from; then checks that no slot under the old lookup is left (but the skipped ones) and
    /// that the user has exactly the prepared API keys, connections and WebDAV shares, and still the list of discreet
    /// vaults it was prepared from. <see cref="RotationConflictException"/> on any mismatch: the caller aborts, prepares
    /// again and retries. <see cref="CredentialsChangedException"/> if the user
    /// is gone or inactive.
    /// </summary>
    public async Task ApplyAsync(IClientSessionHandle session, PreparedRotation rotation, CancellationToken ct)
    {
        var userId = rotation.UserId;
        // Whole milliseconds, as MongoDB keeps them: the kept session must read back as created after the stamp.
        var stamp = Milliseconds(Now);

        // The user document first: a vault, key, connection or session being created writes it too, so the two conflict
        // and whichever comes second either sees the other here or is refused by the stamp.
        var user = await Users.UpdateOneAsync(
            session,
            u => u.Id == userId && u.Active,
            Builders<UserDocument>.Update.Set(u => u.KeyId, rotation.UserKeyId).Set(u => u.CredentialsChangedAt, stamp).AddToSetEach(u => u.RekeyPending, rotation.VaultIds),
            cancellationToken: ct);
        if (user.MatchedCount == 0) throw new CredentialsChangedException();
        // The new keypair, only over the one prepared from: a keypair created meanwhile, under the old key, would be lost.
        var keypair = rotation.Keypair;
        var users = Builders<UserDocument>.Filter;
        Require(await Users.UpdateOneAsync(
            session,
            users.Eq(u => u.Id, userId) & (keypair.OldPrivateKey is { } oldPrivate ? users.Eq(u => u.PrivateKey, oldPrivate) : users.Exists(u => u.PrivateKey, false)),
            Builders<UserDocument>.Update.Set(u => u.PublicKey, keypair.NewPublicKey).Set(u => u.PrivateKey, keypair.NewPrivateKey),
            cancellationToken: ct));
        // The list of discreet vaults, only over the one prepared from: a mark meanwhile, under the old key, would be lost.
        var list = rotation.DiscreetList;
        var unchanged = users.Eq(u => u.Id, userId) & (list.Old is { } old ? users.Eq(u => u.DiscreetVaults, old) : users.Exists(u => u.DiscreetVaults, false));
        if (list.New is { } resealed)
        {
            Require(await Users.UpdateOneAsync(
                session,
                unchanged,
                Builders<UserDocument>.Update.Set(u => u.DiscreetVaults, resealed),
                cancellationToken: ct));
        }
        else if (!await Users.Find(session, unchanged).AnyAsync(ct))
        {
            // None prepared: nothing to write, but there must still be none.
            throw new RotationConflictException();
        }

        await ApplyUserSlotsAsync(session, rotation, stamp, ct);
        // The lookups first, then the member slots' wrapping: the lookup's update is conditional on the old wrapping.
        await ApplyVaultSlotsAsync(session, rotation, ct);
        await ApplyMemberSlotsAsync(session, rotation, ct);
        await Sharing.MemberNotices.ApplyResealAsync(db, session, userId, rotation.Notices, ct);
        await ApplyMetasAsync(session, Keys, rotation.ApiKeys, userId, ct);
        await ApplyMetasAsync(session, ConnectionDocs, rotation.Connections, userId, ct);
        await ApplyMetasAsync(session, ShareDocs, rotation.DavShareMetas, userId, ct);

        if (FaultAt is { } fault) await fault("inside-apply");
    }

    /// <summary>
    /// For Settings ("Re-encrypt my vaults"): checks that <paramref name="password"/> opens <paramref name="oldUserKey"/>,
    /// then prepares and applies in a transaction of its own, prepared again on a conflict up to <see cref="Attempts"/>
    /// times, then <see cref="RotationConflictException"/>. Null if the password is wrong. Not stamped before: the stamp in
    /// Apply is enough, and one before it would sign the user out if Apply then failed.
    /// </summary>
    public async Task<PreparedRotation?> RotateAsync(User user, byte[] oldUserKey, string password, string? keepSessionSecret, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Checked on every attempt, against the very slot the rotation then replaces: a password changed meanwhile
            // must not be overwritten with the one given here.
            if (await UserSlots.Find(s => s.User == user.Id && s.Kind == UserSlotDocument.Password).FirstOrDefaultAsync(ct) is not { Salt: not null } slot
                || await UserCatalog.OpenPasswordSlotAsync(kdf, slot, password, ct) is not { } opened
                || !CryptographicOperations.FixedTimeEquals(opened, oldUserKey))
            {
                return null;
            }

            try
            {
                var prepared = await PrepareAsync(user, oldUserKey, password, keepSessionSecret, null, ct);
                if (prepared.PasswordUsed is null || !prepared.PasswordUsed.AsSpan().SequenceEqual(slot.Wrapped)) throw new RotationConflictException();
                await CommitAsync(prepared, ct);
                log.LogInformation("User {Username} has a new user key; {Vaults} vaults are to be re-keyed", user.Username, prepared.VaultIds.Count);
                return prepared;
            }
            catch (RotationConflictException)
            {
                log.LogInformation("Rotating the user key of {Username} met a concurrent change (attempt {Attempt} of {Attempts})", user.Username, attempt, Attempts);
                if (attempt == Attempts) throw;
            }
        }
    }

    /// <summary>Applies a prepared rotation in a transaction of its own. Retried on a transient error (a write conflict,
    /// with <c>lastUsedAt</c> written on a key or session in use, or with a refused session's slot deleted), when the
    /// conditions then tell whether what conflicted changed anything the rotation was prepared from.</summary>
    internal async Task CommitAsync(PreparedRotation rotation, CancellationToken ct)
    {
        if (FaultAt is { } fault) await fault("before-apply");
        using var session = await db.Client.StartSessionAsync(cancellationToken: ct);
        await session.WithTransactionAsync(
            async (s, token) =>
            {
                await ApplyAsync(s, rotation, token);
                return true;
            },
            cancellationToken: ct);
    }

    private async Task ApplyUserSlotsAsync(IClientSessionHandle session, PreparedRotation rotation, DateTime stamp, CancellationToken ct)
    {
        var userId = rotation.UserId;
        var f = Builders<UserSlotDocument>.Filter;
        var u = Builders<UserSlotDocument>.Update;
        var mine = f.Eq(s => s.User, userId);

        var password = rotation.PasswordSlot;
        var passwordUpdate = u.Set(s => s.Salt, password.Salt).Set(s => s.Wrapped, password.Wrapped).Set(s => s.CreatedAt, password.CreatedAt);
        if (rotation.PasswordUsed is { } used)
        {
            Require(await UserSlots.UpdateOneAsync(session, mine & f.Eq(s => s.Kind, UserSlotDocument.Password) & f.Eq(s => s.Wrapped, used), passwordUpdate, cancellationToken: ct));
        }
        else
        {
            // There was none: one written since makes the unique index refuse this one.
            await InsertAsync(session, UserSlots, password, ct);
        }

        var recovery = rotation.RecoverySlot;
        var recoveryUpdate = u.Set(s => s.Wrapped, recovery.Wrapped).Set(s => s.CreatedAt, recovery.CreatedAt);
        var recoveryFilter = mine & f.Eq(s => s.Kind, UserSlotDocument.Recovery);
        if (rotation.RecoveryUsed is { } opened)
        {
            // A recovery: only the slot that was opened. Of two recoveries racing with one key, one gets through.
            Require(await UserSlots.UpdateOneAsync(session, recoveryFilter & f.Eq(s => s.Wrapped, opened), recoveryUpdate, cancellationToken: ct));
        }
        else
        {
            // Outside a recovery the old one can't be sealed again (its key isn't at hand) and would open a dead key.
            await UserSlots.UpdateOneAsync(session, recoveryFilter, recoveryUpdate, new UpdateOptions { IsUpsert = true }, ct);
        }

        var sessions = mine & f.Eq(s => s.Kind, UserSlotDocument.Session);
        if (rotation.KeptSession is { } kept)
        {
            // Created just after the stamp, or Sessions.ResolveAsync would refuse it with every other session.
            Require(await UserSlots.UpdateOneAsync(
                session,
                sessions & f.Eq(s => s.Id, kept.SlotId) & f.Eq(s => s.Wrapped, kept.OldWrapped),
                u.Set(s => s.Wrapped, kept.NewWrapped).Set(s => s.CreatedAt, stamp.AddMilliseconds(1)),
                cancellationToken: ct));
            sessions &= f.Ne(s => s.Id, kept.SlotId);
        }

        await UserSlots.DeleteManyAsync(session, sessions, cancellationToken: ct);
    }

    private async Task ApplyVaultSlotsAsync(IClientSessionHandle session, PreparedRotation rotation, CancellationToken ct)
    {
        var f = Builders<SlotDocument>.Filter;
        var u = Builders<SlotDocument>.Update;
        foreach (var slot in rotation.VaultSlots)
        {
            var update = u.Set(s => s.Lookup, rotation.NewLookup).Set(s => s.Wrapped, slot.NewWrapped);
            update = slot.NewNameId is { } nameId ? update.Set(s => s.NameId, nameId) : update.Unset(s => s.NameId);
            if (slot.NewAliasKey is { } aliasKey) update = update.Set(s => s.AliasKey, aliasKey);
            var filter = f.Eq(s => s.Id, slot.Id) & f.Eq(s => s.Lookup, rotation.OldLookup) & f.Eq(s => s.Wrapped, slot.OldWrapped);
            if (slot.OldAlias is { } alias) filter &= f.Eq(s => s.Alias, alias);
            // Null matches a slot without a name hash too.
            if (slot.CheckNameId) filter &= f.Eq(s => s.NameId, slot.OldNameId);
            Require(await Slots.UpdateOneAsync(session, filter, update, cancellationToken: ct));
        }

        foreach (var orphan in rotation.OrphanSlotIds)
        {
            var doc = await Slots.Find(session, f.Eq(s => s.Id, orphan) & f.Eq(s => s.Lookup, rotation.OldLookup)).FirstOrDefaultAsync(ct) ?? throw new RotationConflictException();
            if (await Vaults.Find(session, v => v.Id == doc.Vault).AnyAsync(ct)) throw new RotationConflictException();
            await Slots.DeleteOneAsync(session, f.Eq(s => s.Id, orphan), cancellationToken: ct);
            log.LogInformation("Deleted orphan slot {SlotId} of vault {VaultId}, left by a creation that failed half way", orphan, doc.Vault);
        }

        // A slot added since the preparation (a vault being created with the old key) would be lost under the new key.
        var left = f.Eq(s => s.Lookup, rotation.OldLookup) & f.Nin(s => s.Id, rotation.SkippedSlotIds);
        if (await Slots.Find(session, left).AnyAsync(ct)) throw new RotationConflictException();
    }

    /// <summary>
    /// Each member slot wrapped to the new public key through its membership's pin (HARD RULE 3: the pin must open under
    /// the vault key, name this membership and member and the old public key), and the pin sealed again for the new key,
    /// with <c>revision</c> raised: a re-key's finish, a role change or a revocation since the preparation conflicts.
    /// A pin that isn't valid had its member slots deleted, logged as damaged; the rotation goes on without them.
    /// </summary>
    private async Task ApplyMemberSlotsAsync(IClientSessionHandle session, PreparedRotation rotation, CancellationToken ct)
    {
        foreach (var slot in rotation.DamagedMemberSlots)
        {
            // Whatever its lookup is now (ApplyVaultSlotsAsync moved it): only while it still wraps what was opened.
            var deleted = await Slots.DeleteOneAsync(
                session, Builders<SlotDocument>.Filter.Eq(s => s.Id, slot.SlotId) & Builders<SlotDocument>.Filter.Eq(s => s.Wrapped, slot.OldWrapped), cancellationToken: ct);
            if (deleted.DeletedCount == 0) throw new RotationConflictException();
            log.LogWarning("A member slot of vault {VaultId} of user {UserId} has no membership: damaged; it was deleted", slot.VaultId, rotation.UserId);
        }

        var docs = db.GetCollection<Sharing.MembershipDocument>(Sharing.Memberships.CollectionName);
        var f = Builders<Sharing.MembershipDocument>.Filter;
        // By membership: a second member slot of one (written to the database) must not make the pin conflict with itself.
        foreach (var group in rotation.MemberSlots.GroupBy(r => r.MembershipId, StringComparer.Ordinal))
        {
            var first = group.First();
            var membership = await docs.Find(session, f.Eq(m => m.Id, first.MembershipId) & f.Eq(m => m.Revision, first.Revision)).FirstOrDefaultAsync(ct)
                ?? throw new RotationConflictException();
            var rewrapped = false;
            foreach (var rewrap in group)
            {
                var pinned = await Sharing.Memberships.WrapToPinAsync(
                    db, log, session, membership, rewrap.VaultKey, null, new Sharing.PinRotation(rewrap.OldPublicKey, rotation.Keypair.NewPublicKey, rewrap.OldWrapped), ct);
                if (pinned is not null)
                {
                    rewrapped = true;
                }
                else if (Sharing.Memberships.PinnedKey(membership, rewrap.VaultKey) is { } key && CryptographicOperations.FixedTimeEquals(key, rewrap.OldPublicKey))
                {
                    // A valid pin, but the slot isn't as it was opened: it changed since.
                    throw new RotationConflictException();
                }
            }

            if (!rewrapped) continue;
            Require(await docs.UpdateOneAsync(
                session,
                f.Eq(m => m.Id, membership.Id) & f.Eq(m => m.Revision, first.Revision),
                Builders<Sharing.MembershipDocument>.Update
                    .Set(m => m.Pin, Sharing.Memberships.SealPin(first.VaultKey, membership.Vault, membership.Id, membership.Member!, rotation.Keypair.NewPublicKey))
                    .Inc(m => m.Revision, 1),
                cancellationToken: ct));
        }
    }

    private static async Task ApplyMetasAsync<T>(
        IClientSessionHandle session, IMongoCollection<T> collection, IReadOnlyList<MetaReseal> reseals, string userId, CancellationToken ct)
    {
        var f = Builders<T>.Filter;
        var u = Builders<T>.Update;
        foreach (var reseal in reseals)
        {
            // Conditional on the name id too: a revocation unsets it, and must not come back.
            var filter = f.Eq("_id", reseal.Id) & f.Eq("user", userId) & f.Eq("meta", reseal.OldMeta)
                & (reseal.OldNameId is { } oldNameId ? f.Eq("nameId", oldNameId) : f.Exists("nameId", false));
            var update = u.Set("meta", reseal.NewMeta);
            update = reseal.NewNameId is { } nameId ? update.Set("nameId", nameId) : update.Unset("nameId");
            Require(await collection.UpdateOneAsync(session, filter, update, cancellationToken: ct));
        }

        // One made since the preparation would keep a meta and name id under the old key.
        var ids = await collection.Find(session, f.Eq("user", userId)).Project(Builders<T>.Projection.Include("_id")).ToListAsync(ct);
        if (!ids.Select(d => d["_id"].AsString).ToHashSet(StringComparer.Ordinal).SetEquals(reseals.Select(r => r.Id))) throw new RotationConflictException();
    }

    private static async Task InsertAsync<T>(IClientSessionHandle session, IMongoCollection<T> collection, T doc, CancellationToken ct)
    {
        try
        {
            await collection.InsertOneAsync(session, doc, cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new RotationConflictException();
        }
    }

    private static void Require(UpdateResult result)
    {
        if (result.MatchedCount != 1) throw new RotationConflictException();
    }

    private static DateTime Milliseconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
}
