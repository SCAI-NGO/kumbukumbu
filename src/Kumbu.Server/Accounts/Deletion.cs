// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>What a deletion removed, for the log and the caller: counts only, never a name. <c>Notes</c> counts the
/// documents in the <c>notes</c> collection of the deleted vaults (by vault id, so no key is needed), <c>Attachments</c>
/// those in <c>attachments</c> (one per file, its versions and trash not counted); <c>Invitations</c> the invitations
/// the user joined with whose note and link to them were erased.</summary>
public sealed record DeletionSummary(int Vaults, int ApiKeys, long Documents, int Connections, long Notes = 0, int Invitations = 0, long Attachments = 0);

/// <summary>A user an admin deleted, for the receipt: ids, the usernames, the time and the counts; no content and no
/// vault or note name. <c>Listed</c>: the id was added to the <see cref="DeletionList"/>.</summary>
public sealed record DeletedUser(string Id, string Username, string Display, string DeletedBy, DateTime DeletedAt, DeletionSummary Summary, bool Listed);

/// <summary>
/// Deleting for good: one vault (its owner), one's own account, or any other user's account (an admin, for a privacy
/// request, say). Each is
/// one MongoDB transaction, so a fault leaves everything or nothing. A typed confirmation is checked here, not only
/// in the web app, so no single careless request deletes anything; deleting one's own account also needs the
/// password, so a stolen session alone can't. The log names ids, usernames and counts, never a vault name or content.
/// <para>
/// The invitations a user created stay: they are the instance's record of who invited whom (the accounts they made
/// exist on their own), and the remaining admins see and can revoke an open one, as after a deactivation. The one the
/// user joined with stays as a used invitation, but without its note (which often names the invitee) and without
/// the link to the user, so nothing in the database still names or points to the person.
/// </para>
/// </summary>
public sealed class Deletion(
    IMongoDatabase db, VaultCatalog catalog, UserCatalog users, DeletionList list, RestoredDeletions restored, TimeProvider time, ILogger<Deletion> log)
{
    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    private IMongoCollection<UserSlotDocument> UserSlots { get; } = db.GetCollection<UserSlotDocument>("user_slots");

    private IMongoCollection<ApiKeyDocument> Keys { get; } = db.GetCollection<ApiKeyDocument>("api_keys");

    private IMongoCollection<BsonDocument> Notes { get; } = db.GetCollection<BsonDocument>("notes");

    private IMongoCollection<BsonDocument> Attachments { get; } = db.GetCollection<BsonDocument>("attachments");

    private IMongoCollection<InvitationDocument> InvitationDocs { get; } = db.GetCollection<InvitationDocument>("invitations");

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
        // After the commit, and whatever happens to the request now: the deletion is done either way.
        await list.AddAsync(user.Id, CancellationToken.None);
        log.LogInformation(
            "User {UserId} deleted their account: {Vaults} vaults, {Notes} notes, {Attachments} attachments, {Keys} API keys, {Connections} connections, {Invitations} invitations erased, {Documents} documents",
            user.Id, summary.Vaults, summary.Notes, summary.Attachments, summary.ApiKeys, summary.Connections, summary.Invitations, summary.Documents);
        return true;
    }

    /// <summary>
    /// An admin deletes another user, active or not, in one step; null if there is no such user. <paramref name="confirm"/>
    /// must be their username (any case), and <paramref name="password"/> the admin's own (it must open
    /// <paramref name="actorKey"/>), so a stolen admin session alone deletes nobody. The user's sessions, API keys and
    /// connections go in the same transaction as everything else, so an active user's next request is refused. Never the
    /// primary admin, and never the admin themselves: they delete their own account in Settings.
    /// </summary>
    public async Task<DeletedUser?> DeleteUserAsync(User actor, byte[] actorKey, string password, string userId, string confirm, CancellationToken ct)
    {
        var acting = await AdminRules.RequireAdminAsync(Users, actor, ct);
        // Before anything about the target: without the password, nobody learns whether the user exists.
        if (!await users.CheckPasswordAsync(acting.Id, actorKey, password, ct)) throw new ArgumentException("Your password is wrong.", nameof(password));
        if (userId == acting.Id) throw new NotPermittedException("Delete your own account in Settings.");
        var target = await Users.Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        if (target is null) return null;
        if (target.Role == Roles.PrimaryAdmin) throw new NotPermittedException("The primary admin can't be deleted.");
        if (!ConfirmsUsername(confirm, target.Username)) throw new ArgumentException("Type the username to confirm.", nameof(confirm));

        // Checked again in the transaction: the target can't have become primary admin meanwhile.
        var summary = await DeleteAccountAsync(userId, Builders<UserDocument>.Filter.Where(u => u.Id == userId && u.Role != Roles.PrimaryAdmin), ct)
            ?? throw new NotPermittedException("The user changed meanwhile; nothing was deleted.");
        var listed = await list.AddAsync(userId, CancellationToken.None);
        log.LogInformation(
            "{Admin} deleted user {UserId}: {Vaults} vaults, {Notes} notes, {Attachments} attachments, {Keys} API keys, {Connections} connections, {Invitations} invitations erased, {Documents} documents",
            acting.Username, target.Id, summary.Vaults, summary.Notes, summary.Attachments, summary.ApiKeys, summary.Connections, summary.Invitations, summary.Documents);
        return new DeletedUser(target.Id, target.Username, target.Display, acting.Username, time.GetUtcNow().UtcDateTime, summary, listed);
    }

    /// <summary>
    /// After a backup restore: an admin deletes again, in one step, every user on the <see cref="DeletionList"/> that
    /// the restore brought back, with their own password (no username to type: the list names them). Each user goes
    /// in a transaction of their own, so one that fails doesn't stop the others (it is logged and stays on the
    /// users page). Never the primary admin and never the admin themselves. Nothing is added to the list: the ids
    /// are on it already.
    /// </summary>
    public async Task<IReadOnlyList<DeletedUser>> RepeatAsync(User actor, byte[] actorKey, string password, CancellationToken ct)
    {
        var acting = await AdminRules.RequireAdminAsync(Users, actor, ct);
        if (!await users.CheckPasswordAsync(acting.Id, actorKey, password, ct)) throw new ArgumentException("Your password is wrong.", nameof(password));

        var done = new List<DeletedUser>();
        foreach (var target in await restored.FindAsync(ct))
        {
            if (target.Id == acting.Id) continue;
            DeletionSummary? summary;
            try
            {
                summary = await DeleteAccountAsync(target.Id, Builders<UserDocument>.Filter.Where(u => u.Id == target.Id && u.Role != Roles.PrimaryAdmin), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One user that fails doesn't cost the others their deletion, or the admin the receipts of those done.
                log.LogError("{Admin} could not delete user {UserId} again after a restore ({Error})", acting.Username, target.Id, e.Message);
                continue;
            }

            if (summary is null) continue;
            log.LogInformation(
                "{Admin} deleted user {UserId} again after a restore: {Vaults} vaults, {Notes} notes, {Attachments} attachments, {Keys} API keys, {Connections} connections, {Invitations} invitations erased, {Documents} documents",
                acting.Username, target.Id, summary.Vaults, summary.Notes, summary.Attachments, summary.ApiKeys, summary.Connections, summary.Invitations, summary.Documents);
            done.Add(new DeletedUser(target.Id, target.Username, target.Display, acting.Username, time.GetUtcNow().UtcDateTime, summary, Listed: true));
        }

        return done;
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
        var notes = await CountAsync(Notes, vaults, ct);
        var attachments = await CountAsync(Attachments, vaults, ct);
        var keyCount = 0;
        var connectionCount = 0;
        var invitationCount = 0;
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
                // The invitation they joined with stays a used one, but its note (often the invitee's name) and its
                // link to them go: nothing left names or points to the person.
                invitationCount = (int)(await InvitationDocs.UpdateManyAsync(
                    s,
                    i => i.UsedBy == userId,
                    Builders<InvitationDocument>.Update.Unset(i => i.Note).Unset(i => i.UsedBy),
                    cancellationToken: token)).ModifiedCount;
            }, ct);
        }
        catch (UserChangedException)
        {
            return null;
        }

        // A vault the user made between the read above and the transaction (from another tab, say) has an owner
        // that no longer exists: it goes too.
        var late = await OwnedVaultsAsync(userId, ct);
        if (late.Count > 0)
        {
            notes += await CountAsync(Notes, late, ct);
            attachments += await CountAsync(Attachments, late, ct);
            documents += await catalog.DeleteVaultsAsync(late, (_, _) => Task.CompletedTask, ct);
        }

        return new DeletionSummary(vaults.Count + late.Count, keyCount, documents, connectionCount, notes, invitationCount, attachments);
    }

    /// <summary>The notes or attachments in these vaults, counted by vault id: the receipt's counts, read without any key.</summary>
    private static async Task<long> CountAsync(IMongoCollection<BsonDocument> collection, IReadOnlyList<string> vaults, CancellationToken ct) =>
        vaults.Count == 0 ? 0 : await collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.In("vault", vaults), cancellationToken: ct);

    /// <summary>The vaults whose user slot names this owner, orphans of a failed create included.</summary>
    private async Task<IReadOnlyList<string>> OwnedVaultsAsync(string userId, CancellationToken ct) =>
        await catalog.Slots.Distinct(
            s => s.Vault, Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.User) & Builders<SlotDocument>.Filter.Eq(s => s.Owner, userId), cancellationToken: ct)
            .ToListAsync(ct);

    private sealed class UserChangedException() : Exception("The user changed during the deletion.");
}
