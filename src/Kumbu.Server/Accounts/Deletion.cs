// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>What a deletion removed, for the log and the caller: counts only, never a name.</summary>
public sealed record DeletionSummary(int Vaults, int ApiKeys, long Documents, int Connections);

/// <summary>
/// Deleting for good: one vault (its owner), one's own account, or a deactivated user's account (an admin). Each is
/// one MongoDB transaction, so a fault leaves everything or nothing. A typed confirmation is checked here, not only
/// in the web app, so no single careless request deletes anything; deleting one's own account also needs the
/// password, so a stolen session alone can't. The log names ids, usernames and counts, never a vault name or content.
/// <para>
/// A user's invitations stay: they are the instance's record of who invited whom (the accounts they made exist on
/// their own), and the remaining admins see and can revoke an open one, as after a deactivation.
/// </para>
/// </summary>
public sealed class Deletion(IMongoDatabase db, VaultCatalog catalog, UserCatalog users, ILogger<Deletion> log)
{
    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    private IMongoCollection<UserSlotDocument> UserSlots { get; } = db.GetCollection<UserSlotDocument>("user_slots");

    private IMongoCollection<ApiKeyDocument> Keys { get; } = db.GetCollection<ApiKeyDocument>("api_keys");

    /// <summary>
    /// Deletes the user's vault named <paramref name="name"/> (exactly, as the user sees it) with everything in it,
    /// once <paramref name="confirm"/> repeats the name and <paramref name="password"/> is the user's (so a stolen
    /// session alone can't); null if the user has no vault of that name. API keys and connections that had it keep their
    /// other vaults; one left with none is revoked for good in the same transaction.
    /// </summary>
    public async Task<DeletionSummary?> DeleteVaultAsync(User user, byte[] userKey, string name, string password, string confirm, CancellationToken ct)
    {
        if (!string.Equals(confirm, name, StringComparison.Ordinal)) throw new ArgumentException("Type the vault's name exactly to confirm.", nameof(confirm));
        // Before the lookup: without the password, nobody learns whether a vault of that name exists.
        if (!await users.CheckPasswordAsync(user.Id, userKey, password, ct)) throw new ArgumentException("The password is wrong.", nameof(password));
        // Only the owner's user key finds a vault through a user slot: nobody else finds it by name.
        if (await catalog.FindForUserAsync(user.Id, userKey, name, ct) is not { } vaultId) return null;

        var documents = await catalog.DeleteVaultsAsync([vaultId], (_, _) => Task.CompletedTask, ct);
        log.LogInformation("User {Username} deleted vault {VaultId} ({Documents} documents)", user.Username, vaultId, documents);
        return new DeletionSummary(1, 0, documents, 0);
    }

    /// <summary>
    /// Deletes the caller's own account; false if <paramref name="password"/> is wrong. <paramref name="confirm"/>
    /// must be the username (any case). The primary admin can't: there must always be one.
    /// </summary>
    public async Task<bool> DeleteOwnAccountAsync(User user, byte[] userKey, string password, string confirm, CancellationToken ct)
    {
        if (!ConfirmsUsername(confirm, user.Username)) throw new ArgumentException("Type your username to confirm.", nameof(confirm));
        if (user.Role == Roles.PrimaryAdmin) throw new NotPermittedException("The primary admin's account can't be deleted.");
        if (!await users.CheckPasswordAsync(user.Id, userKey, password, ct)) return false;

        var summary = await DeleteAccountAsync(user.Id, Builders<UserDocument>.Filter.Where(u => u.Id == user.Id && u.Role != Roles.PrimaryAdmin), ct)
            ?? throw new NotPermittedException("Your account changed meanwhile; nothing was deleted.");
        log.LogInformation(
            "User {Username} deleted their account: {Vaults} vaults, {Keys} API keys, {Connections} connections, {Documents} documents",
            user.Username, summary.Vaults, summary.ApiKeys, summary.Connections, summary.Documents);
        return true;
    }

    /// <summary>
    /// An admin deletes a deactivated user; false if there is no such user. <paramref name="confirm"/> must be their
    /// username (any case), and <paramref name="password"/> the admin's own (it must open <paramref name="actorKey"/>),
    /// so a stolen admin session alone deletes nobody. Only a deactivated user: deactivating first ended their
    /// sessions and revoked their keys, and takes a deliberate step of its own. Never the primary admin.
    /// </summary>
    public async Task<bool> DeleteUserAsync(User actor, byte[] actorKey, string password, string userId, string confirm, CancellationToken ct)
    {
        var acting = await AdminRules.RequireAdminAsync(Users, actor, ct);
        // Before anything about the target: without the password, nobody learns whether the user exists.
        if (!await users.CheckPasswordAsync(acting.Id, actorKey, password, ct)) throw new ArgumentException("Your password is wrong.", nameof(password));
        var target = await Users.Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        if (target is null) return false;
        if (target.Role == Roles.PrimaryAdmin) throw new NotPermittedException("The primary admin can't be deleted.");
        if (target.Active) throw new NotPermittedException("Only a deactivated user can be deleted: deactivate them first.");
        if (!ConfirmsUsername(confirm, target.Username)) throw new ArgumentException("Type the username to confirm.", nameof(confirm));

        // Checked again in the transaction: a reactivation since the read above stops the deletion.
        var summary = await DeleteAccountAsync(
            userId, Builders<UserDocument>.Filter.Where(u => u.Id == userId && !u.Active && u.Role != Roles.PrimaryAdmin), ct)
            ?? throw new NotPermittedException("The user was reactivated or changed meanwhile; nothing was deleted.");
        log.LogInformation(
            "{Admin} deleted user {Username}: {Vaults} vaults, {Keys} API keys, {Connections} connections, {Documents} documents",
            acting.Username, target.Username, summary.Vaults, summary.ApiKeys, summary.Connections, summary.Documents);
        return true;
    }

    private static bool ConfirmsUsername(string typed, string username) => string.Equals(typed.Trim(), username, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// In one transaction: the user's vaults with everything in them, the user, their password, recovery and
    /// session slots, their API keys with the keys' slots, and their connections with their codes and tokens (tombstones
    /// included), the tokens' slots and the pending authorisation requests. Null, and nothing deleted, if the user no
    /// longer matches <paramref name="stillDeletable"/>.
    /// </summary>
    private async Task<DeletionSummary?> DeleteAccountAsync(string userId, FilterDefinition<UserDocument> stillDeletable, CancellationToken ct)
    {
        var vaults = await OwnedVaultsAsync(userId, ct);
        var keyCount = 0;
        var connectionCount = 0;
        long documents;
        try
        {
            documents = await catalog.DeleteVaultsAsync(vaults, async (s, token) =>
            {
                if ((await Users.DeleteOneAsync(s, stillDeletable, cancellationToken: token)).DeletedCount == 0) throw new UserChangedException();
                await UserSlots.DeleteManyAsync(s, u => u.User == userId, cancellationToken: token);
                // The keys' slots are all on the user's own vaults, deleted above; any left over go by key id.
                var keyIds = await Keys.Find(s, k => k.User == userId).Project(k => k.Id).ToListAsync(token);
                foreach (var id in keyIds) await catalog.DeleteApiKeySlotsAsync(s, id, token);
                await Keys.DeleteManyAsync(s, k => k.User == userId, cancellationToken: token);
                keyCount = keyIds.Count;
                // The user document is deleted above, in this transaction: a connection being created or a token being
                // issued writes it or the connection's document, and conflicts.
                connectionCount = await ConnectionRevocation.DeleteAllAsync(db, s, userId, token);
            }, ct);
        }
        catch (UserChangedException)
        {
            return null;
        }

        // A vault the user made between the read above and the transaction (from another tab, say) has an owner
        // that no longer exists: it goes too.
        var late = await OwnedVaultsAsync(userId, ct);
        if (late.Count > 0) documents += await catalog.DeleteVaultsAsync(late, (_, _) => Task.CompletedTask, ct);
        return new DeletionSummary(vaults.Count + late.Count, keyCount, documents, connectionCount);
    }

    /// <summary>The vaults whose user slot names this owner, orphans of a failed create included.</summary>
    private async Task<IReadOnlyList<string>> OwnedVaultsAsync(string userId, CancellationToken ct) =>
        await catalog.Slots.Distinct(
            s => s.Vault, Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.User) & Builders<SlotDocument>.Filter.Eq(s => s.Owner, userId), cancellationToken: ct)
            .ToListAsync(ct);

    private sealed class UserChangedException() : Exception("The user changed during the deletion.");
}
