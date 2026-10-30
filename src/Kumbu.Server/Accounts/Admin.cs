// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>A user as the admin's list shows them: metadata only, never a vault's name or anything in it.
/// <c>LastActiveAt</c> is the latest sign-in or visit of a remembered session (<see cref="Sessions.ResolveAsync"/>).</summary>
public sealed record UserSummary(
    string Id, string Username, string Display, string Role, bool Active, DateTime CreatedAt, DateTime? LastActiveAt, int Vaults, long BytesStored);

/// <summary>
/// The admin's tools (accounts design, "Admin"): the user list, deactivating and reactivating, and roles. Every
/// method takes the acting user and enforces the rules itself: only an active admin acts, nobody acts on the
/// primary admin, and nobody is made primary admin. Admins never see content: the figures here come from what
/// MongoDB holds in the clear (slot owners, document sizes), and no vault is opened.
/// </summary>
public sealed class Admin(IMongoDatabase db, VaultCatalog catalog, Sessions sessions, ApiKeys keys, Connections connections, TimeProvider time, ILogger<Admin> log)
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
        return [.. users.Select(u =>
        {
            var vaults = vaultsByOwner.GetValueOrDefault(u.Id) ?? [];
            return new UserSummary(
                u.Id, u.Username, u.Display, u.Role, u.Active, u.CreatedAt, u.LastSignInAt, vaults.Count, vaults.Sum(v => bytesByVault.GetValueOrDefault(v)));
        })];
    }

    /// <summary>
    /// Deactivates or reactivates a user; false if there is no such user, true (and nothing done) if they already
    /// are so, so a repeated request costs nobody their keys. A real change in either direction ends every session
    /// and revokes every API key of the user and the tokens of every connection (<see cref="ConnectionReasons.Deactivated"/>),
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

        (int Keys, int Connections, long Invitations)? revoked;
        using (var session = await catalog.StartSessionAsync(ct))
        {
            // Retried on a write conflict: a key or a connection in use writes its lastUsedAt outside any transaction.
            revoked = await session.WithTransactionAsync<(int, int, long)?>(
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
                    var connectionCount = await connections.RevokeAllTokensAsync(s, userId, ConnectionReasons.Deactivated, token);
                    var invitationCount = active ? 0 : await Invitations.RevokeOpenOfAsync(db, s, userId, Now, token);
                    return (keyCount, connectionCount, invitationCount);
                },
                cancellationToken: ct);
        }

        if (revoked is not { } count) return true;
        if (active) log.LogInformation("{Admin} reactivated user {Username}", acting.Username, target.Username);
        else
        {
            log.LogInformation(
                "{Admin} deactivated user {Username}; {Count} API keys, the tokens of {Connections} connections and {Invitations} open invitations revoked",
                acting.Username, target.Username, count.Keys, count.Connections, count.Invitations);
        }

        return true;
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

    /// <summary>The BSON size of every document of these vaults, summed per vault: what they take in the database.</summary>
    private async Task<Dictionary<string, long>> BytesByVaultAsync(IReadOnlyList<string> vaultIds, CancellationToken ct)
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
