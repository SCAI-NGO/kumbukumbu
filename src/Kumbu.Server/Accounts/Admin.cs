// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>A user as the admin's list shows them: metadata only, never a vault's name or anything in it.
/// <c>LastActiveAt</c> is the latest sign-in or visit of a remembered session (<see cref="Sessions.ResolveAsync"/>).
/// <c>SharedVaults</c>: how many of their vaults have an active member, for the deletion dialog's choice; a number only.
/// <c>Quota</c>: their storage override as stored (<see cref="UserDocument.Quota"/>).</summary>
public sealed record UserSummary(
    string Id, string Username, string Display, string Role, bool Active, DateTime CreatedAt, DateTime? LastActiveAt, int Vaults, long BytesStored, int SharedVaults,
    long? Quota = null);

/// <summary>
/// The admin's tools (accounts design, "Admin"): the user list, deactivating and reactivating, and roles. Every
/// method takes the acting user and enforces the rules itself: only an active admin acts, nobody acts on the
/// primary admin, and nobody is made primary admin. Admins never see content: the figures here come from what
/// MongoDB holds in the clear (slot owners, document sizes), and no vault is opened.
/// </summary>
public sealed class Admin(
    IMongoDatabase db, VaultCatalog catalog, Sessions sessions, ApiKeys keys, Connections connections, DavShares shares, Sync.SyncHub hub, TimeProvider time,
    ILogger<Admin> log)
{
    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Every user, oldest first, with the number of their vaults and the bytes those take in the database.</summary>
    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(User actor, CancellationToken ct)
    {
        await AdminRules.RequireAdminAsync(Users, actor, ct);
        var users = await Users.Find(FilterDefinition<UserDocument>.Empty).SortBy(u => u.CreatedAt).ToListAsync(ct);

        var slotFilter = Builders<SlotDocument>.Filter;
        var owned = await catalog.Slots.Find(slotFilter.Eq(s => s.Kind, SlotDocument.User) & slotFilter.Exists(s => s.Owner)).ToListAsync(ct);
        var vaultIds = owned.Select(s => s.Vault).Distinct().ToList();
        // A slot whose vault document is missing is an orphan of a create that failed half way: no vault.
        var existing = (await catalog.Vaults.Find(Builders<VaultDocument>.Filter.In(v => v.Id, vaultIds)).Project(v => v.Id).ToListAsync(ct)).ToHashSet();
        var vaultsByOwner = owned.Where(s => existing.Contains(s.Vault)).GroupBy(s => s.Owner!).ToDictionary(g => g.Key, g => g.Select(s => s.Vault).Distinct().ToList());

        var bytesByVault = await BytesByVaultAsync([.. existing], ct);
        var memberships = db.GetCollection<Sharing.MembershipDocument>(Sharing.Memberships.CollectionName);
        var mf = Builders<Sharing.MembershipDocument>.Filter;
        var withMembers = (await memberships.Distinct(m => m.Vault, mf.In(m => m.Vault, existing) & mf.Eq(m => m.Status, Sharing.MembershipStatus.Active), cancellationToken: ct).ToListAsync(ct)).ToHashSet();
        return [.. users.Select(u =>
        {
            var vaults = vaultsByOwner.GetValueOrDefault(u.Id) ?? [];
            return new UserSummary(
                u.Id, u.Username, u.Display, u.Role, u.Active, u.CreatedAt, u.LastSignInAt, vaults.Count, vaults.Sum(v => bytesByVault.GetValueOrDefault(v)),
                vaults.Count(withMembers.Contains), u.Quota);
        })];
    }

    /// <summary>
    /// Deactivates or reactivates a user; false if there is no such user, true (and nothing done) if they already
    /// are so, so a repeated request costs nobody their keys. A real change in either direction ends every session
    /// and revokes every API key and WebDAV share of the user and the tokens of every connection (<see cref="ConnectionReasons.Deactivated"/>),
    /// in one transaction with the flag, and stamps <c>credentialsChangedAt</c>; deactivating also revokes the user's
    /// open invitations there. Revoking again on reactivation catches a key or a code whose creation raced the
    /// deactivation (its request's session resolved just before it): reactivating revives no key, no app, no session
    /// and no invitation.
    /// </summary>
    public async Task<bool> SetActiveAsync(User actor, string userId, bool active, CancellationToken ct)
    {
        var acting = await AdminRules.RequireAdminAsync(Users, actor, ct);
        var target = await Users.Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        if (target is null) return false;
        if (target.Role == Roles.PrimaryAdmin) throw new NotPermittedException("The primary admin can't be deactivated.");
        if (!active && target.Id == acting.Id) throw new NotPermittedException("You can't deactivate yourself.");

        (int Keys, int Connections, long Invitations, int Shares)? revoked;
        using (var session = await catalog.StartSessionAsync(ct))
        {
            // Retried on a write conflict: a key or a connection in use writes its lastUsedAt outside any transaction.
            revoked = await session.WithTransactionAsync<(int, int, long, int)?>(
                async (s, token) =>
                {
                    // The role is checked again in the filter, in case it changed since the read above; the state, so
                    // that only a real change revokes anything.
                    var updated = await Users.UpdateOneAsync(
                        s,
                        u => u.Id == userId && u.Role != Roles.PrimaryAdmin && u.Active != active,
                        Builders<UserDocument>.Update.Set(u => u.Active, active).Set(u => u.CredentialsChangedAt, Now),
                        cancellationToken: token);
                    // The user exists (read above) and can't have become primary admin, so they already are as asked.
                    if (updated.MatchedCount == 0) return null;

                    await sessions.EndAllAsync(s, userId, token);
                    var keyCount = await keys.RevokeAllAsync(s, userId, token);
                    var shareCount = await shares.RevokeAllAsync(s, userId, token);
                    var connectionCount = await connections.RevokeAllTokensAsync(s, userId, ConnectionReasons.Deactivated, token);
                    var invitationCount = active ? 0 : await Invitations.RevokeOpenOfAsync(db, s, userId, Now, token);
                    // Her shared vaults become read-only for their members (collab design §7.4): an event for each, which
                    // their activity shows and their agents are told once. Nothing of the memberships changes.
                    if (!active)
                    {
                        foreach (var vaultId in await SharedVaultsAsync(s, userId, token))
                        {
                            await Sharing.VaultEvents.RecordAsync(db, s, vaultId, Sharing.VaultEvents.OwnerInactive, null, Now, null, token);
                        }
                    }

                    return (keyCount, connectionCount, invitationCount, shareCount);
                },
                cancellationToken: ct);
        }

        if (revoked is not { } count) return true;
        // At once in this process: indexing of her shared vaults stops, or catches up again. The members' requests read
        // her flag themselves, from the next one on, in every process. Reactivated, every vault of hers is cleared, not
        // only those shared now: one whose members all left meanwhile would otherwise stay unindexed here.
        foreach (var vaultId in active ? await catalog.OwnedVaultIdsAsync(userId, ct) : await SharedVaultsAsync(null, userId, ct)) catalog.OwnerActive(vaultId, active);
        // Before the answer (collab design §9.1): her sockets close, and her members' hold her vaults read-only (or
        // again at their roles), for hints and presence alike. Past the commit: an aborted request must not skip it.
        try
        {
            await hub.UserChangedAsync(userId, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Telling the sync hub that user {UserId} changed failed; the sockets learn it at the next check", userId);
        }

        if (active) log.LogInformation("{Admin} reactivated user {Username}", acting.Username, target.Username);
        else
        {
            log.LogInformation(
                "{Admin} deactivated user {Username}; {Count} API keys, {Shares} WebDAV shares, the tokens of {Connections} connections and {Invitations} open invitations revoked",
                acting.Username, target.Username, count.Keys, count.Shares, count.Connections, count.Invitations);
        }

        return true;
    }

    /// <summary>The vaults <paramref name="ownerId"/> shares with anyone now (an active membership), inside
    /// <paramref name="session"/>'s transaction if given.</summary>
    private async Task<IReadOnlyList<string>> SharedVaultsAsync(IClientSessionHandle? session, string ownerId, CancellationToken ct)
    {
        var docs = db.GetCollection<Sharing.MembershipDocument>(Sharing.Memberships.CollectionName);
        var filter = Builders<Sharing.MembershipDocument>.Filter.Eq(m => m.Owner, ownerId) & Builders<Sharing.MembershipDocument>.Filter.Eq(m => m.Status, Sharing.MembershipStatus.Active);
        var field = new StringFieldDefinition<Sharing.MembershipDocument, string>("vault");
        return session is null ? await (await docs.DistinctAsync(field, filter, cancellationToken: ct)).ToListAsync(ct)
            : await (await docs.DistinctAsync(session, field, filter, cancellationToken: ct)).ToListAsync(ct);
    }

        /// <summary>Makes a user a <c>user</c> or an <c>admin</c>; false if there is no such user. The primary admin's
    /// role can't be changed, and nobody can be made primary admin. Made a user, an admin's open invitations are revoked.</summary>
    public async Task<bool> SetRoleAsync(User actor, string userId, string role, CancellationToken ct)
    {
        if (!Roles.IsKnown(role)) throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        if (role == Roles.PrimaryAdmin) throw new NotPermittedException("Nobody can be made primary admin: there is exactly one.");
        var acting = await AdminRules.RequireAdminAsync(Users, actor, ct);
        var target = await Users.Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        if (target is null) return false;
        if (target.Role == Roles.PrimaryAdmin) throw new NotPermittedException("The primary admin's role can't be changed.");

        var updated = await Users.UpdateOneAsync(
            u => u.Id == userId && u.Role != Roles.PrimaryAdmin,
            Builders<UserDocument>.Update.Set(u => u.Role, role),
            cancellationToken: ct);
        if (updated.MatchedCount == 0) return false;
        log.LogInformation("{Admin} made {Username} {Role}", acting.Username, target.Username, role);
        if (role == Roles.User && await Invitations.RevokeOpenOfAsync(db, null, userId, Now, ct) is > 0 and var revoked)
        {
            log.LogInformation("{Count} open invitations of {Username} revoked", revoked, target.Username);
        }

        return true;
    }

    /// <summary>
    /// Sets a user's storage override (WebDAV design §3.10): null for the default, <see cref="StorageQuota.Unlimited"/>, or
    /// a number of bytes (0: no write that adds data); false if there is no such user. Nobody acts on the primary admin
    /// but the primary admin herself. The caller tells <see cref="StorageQuota"/>, which depends on this class.
    /// </summary>
    public async Task<bool> SetQuotaAsync(User actor, string userId, long? quota, CancellationToken ct)
    {
        if (quota is < StorageQuota.Unlimited) throw new ArgumentException("A quota is a number of bytes, 0 or more.", nameof(quota));
        var acting = await AdminRules.RequireAdminAsync(Users, actor, ct);
        var target = await Users.Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        if (target is null) return false;
        if (target.Role == Roles.PrimaryAdmin && target.Id != acting.Id) throw new NotPermittedException("Only the primary admin sets the primary admin's storage.");

        var update = quota is { } bytes ? Builders<UserDocument>.Update.Set(u => u.Quota, bytes) : Builders<UserDocument>.Update.Unset(u => u.Quota);
        // The role again in the filter, in case it changed since the read above.
        var updated = await Users.UpdateOneAsync(u => u.Id == userId && (u.Role != Roles.PrimaryAdmin || u.Id == acting.Id), update, cancellationToken: ct);
        if (updated.MatchedCount == 0) return false;
        log.LogInformation(
            "{Admin} set the storage of {Username} to {Quota}", acting.Username, target.Username,
            quota switch { null => "the default", < 0 => "unlimited", _ => $"{quota} bytes" });
        return true;
    }

    /// <summary>The BSON size of every document of these vaults, summed per vault: what they take in the database. The
    /// user list's figure, and the storage quota's (<see cref="StorageQuota"/>) for one owner's vaults.</summary>
    internal async Task<Dictionary<string, long>> BytesByVaultAsync(IReadOnlyList<string> vaultIds, CancellationToken ct)
    {
        var bytes = new Dictionary<string, long>();
        if (vaultIds.Count == 0) return bytes;
        var pipeline = PipelineDefinition<SealedDocument, BsonDocument>.Create(
            new BsonDocument("$match", new BsonDocument("vault", new BsonDocument("$in", new BsonArray(vaultIds)))),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", "$vault" },
                { "bytes", new BsonDocument("$sum", new BsonDocument("$bsonSize", "$$ROOT")) },
            }));
        foreach (var collection in catalog.ContentCollections)
        {
            foreach (var row in await collection.Aggregate(pipeline, cancellationToken: ct).ToListAsync(ct))
            {
                var vault = row["_id"].AsString;
                bytes[vault] = bytes.GetValueOrDefault(vault) + row["bytes"].ToInt64();
            }
        }

        return bytes;
    }
}

/// <summary>The rule every admin action starts with.</summary>
internal static class AdminRules
{
    /// <summary>
    /// The acting user as the database has them now, if they are an active admin or the primary admin; else
    /// <see cref="NotPermittedException"/>. Read again rather than trusted from <paramref name="actor"/>, which
    /// may be stale: an admin demoted or deactivated a moment ago acts no more.
    /// </summary>
    public static async Task<UserDocument> RequireAdminAsync(IMongoCollection<UserDocument> users, User actor, CancellationToken ct)
    {
        var current = await users.Find(u => u.Id == actor.Id).FirstOrDefaultAsync(ct);
        if (current is not { Active: true, Role: Roles.Admin or Roles.PrimaryAdmin }) throw new NotPermittedException("Only an admin can do this.");
        return current;
    }
}

/// <summary>An action the acting user may not take: they aren't an admin, or it would touch the primary admin.</summary>
public sealed class NotPermittedException(string message) : Exception(message);

/// <summary>
/// A password checked behind a session was wrong: a 400 with the message, and one of the wrong passwords that
/// end a remembered session (<see cref="Sessions.PasswordWrongAsync"/>, counted where <c>ApiErrors.Filter</c> answers
/// it). Every such check throws this when the password is wrong, as it calls <c>AccountApi.PasswordGivenAsync</c> when
/// it is right.
/// </summary>
public sealed class WrongPasswordException(string message) : Exception(message);
