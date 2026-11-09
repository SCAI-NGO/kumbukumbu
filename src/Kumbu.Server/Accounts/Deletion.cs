// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>What a deletion removed, for the log and the caller: counts only, never a name. <c>Notes</c> counts the
/// documents in the <c>notes</c> collection of the deleted vaults (by vault id, so no key is needed), <c>Attachments</c>
/// those in <c>attachments</c> (one per file, its versions and trash not counted); <c>Invitations</c> the invitations
/// the user joined with whose note and link to them were erased. <c>Memberships</c>: the user's memberships of other
/// people's vaults that were live (active, or pending) and ended with the account; <c>SharedVaults</c>: the user's own
/// vaults among <c>Vaults</c> that were shared with anyone, closed for their members with them (collab design §7.2).
/// <c>Closing</c>: the user's shared vaults given a closing period instead (collab design §7.3), by vault id, with the
/// time each goes; they aren't among <c>Vaults</c>.</summary>
public sealed record DeletionSummary(
    int Vaults, int ApiKeys, long Documents, int Connections, long Notes = 0, int Invitations = 0, long Attachments = 0, int Memberships = 0, int SharedVaults = 0,
    IReadOnlyDictionary<string, DateTime>? Closing = null);

/// <summary>What becomes of the user's vaults that are shared with others when her account goes (collab design §7.3,
/// concept §9): <see cref="GiveMembersTime"/> (the default) keeps each read-only for its members for
/// <see cref="Deletion.ClosingPeriod"/>, then deletes it; <see cref="DeleteNow"/> deletes it at once, and each member is
/// left a notice.</summary>
public enum SharedVaultChoice
{
    GiveMembersTime,
    DeleteNow,
}

public static class SharedVaultChoices
{
    public const string GiveMembersTime = "give-members-7-days";
    public const string DeleteNow = "delete-now";

    /// <summary>The choice a request names; absent, <see cref="SharedVaultChoice.GiveMembersTime"/>; anything else, null.</summary>
    public static SharedVaultChoice? Parse(string? text) => text switch
    {
        null or GiveMembersTime => SharedVaultChoice.GiveMembersTime,
        DeleteNow => SharedVaultChoice.DeleteNow,
        _ => null,
    };
}

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
/// exist on their own); an open one is revoked in the same transaction, as after a deactivation. The one the
/// user joined with stays as a used invitation, but without its note (which often names the invitee) and without
/// the link to the user, so nothing in the database still names or points to the person.
/// </para>
/// </summary>
public sealed class Deletion(
    IMongoDatabase db, VaultCatalog catalog, UserCatalog users, DeletionList list, RestoredDeletions restored, Sync.SyncHub hub, TimeProvider time,
    ILogger<Deletion> log)
{
    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    private IMongoCollection<UserSlotDocument> UserSlots { get; } = db.GetCollection<UserSlotDocument>("user_slots");

    private IMongoCollection<ApiKeyDocument> Keys { get; } = db.GetCollection<ApiKeyDocument>("api_keys");

    private IMongoCollection<DavShareDocument> Shares { get; } = db.GetCollection<DavShareDocument>(DavShares.CollectionName);

    private IMongoCollection<BsonDocument> Notes { get; } = db.GetCollection<BsonDocument>("notes");

    private IMongoCollection<BsonDocument> Attachments { get; } = db.GetCollection<BsonDocument>("attachments");

    private IMongoCollection<InvitationDocument> InvitationDocs { get; } = db.GetCollection<InvitationDocument>("invitations");

    // Made here, not injected: PublicationStore is internal and holds nothing but the catalog.
    private readonly Publishing.PublicationStore _publications = new(catalog);

    /// <summary>
    /// Deletes the user's vault named <paramref name="name"/> (exactly, as the user sees it) with everything in it,
    /// once <paramref name="confirm"/> repeats the name and <paramref name="password"/> is the user's (so a stolen
    /// session alone can't); null if the user has no vault of that name. API keys and connections that had it keep their
    /// other vaults; one left with none is revoked for good in the same transaction. A vault among <paramref name="hidden"/>
    /// (those a web session doesn't show) counts as none of that name.
    /// </summary>
    public async Task<DeletionSummary?> DeleteVaultAsync(
        User user, byte[] userKey, string name, string password, string confirm, IReadOnlySet<string>? hidden, CancellationToken ct)
    {
        if (!string.Equals(confirm, name, StringComparison.Ordinal)) throw new ArgumentException("Type the vault's name exactly to confirm.", nameof(confirm));
        // Before the lookup: without the password, nobody learns whether a vault of that name exists.
        if (!await users.CheckPasswordAsync(user.Id, userKey, password, ct)) throw new WrongPasswordException("The password is wrong.");
        // Only the owner's user key finds a vault through a user slot: nobody else finds it by name.
        if (await catalog.FindForUserAsync(user.Id, userKey, name, ct) is not { } vaultId || hidden?.Contains(vaultId) == true) return null;

        await RequireNoSharedRunAsync(null, [vaultId], ct);
        var documents = await catalog.DeleteVaultsAsync([vaultId], (_, _) => Task.CompletedTask, ct, first: (s, token) => RequireNoSharedRunAsync(s, [vaultId], token));
        log.LogInformation("User {Username} deleted vault {VaultId} ({Documents} documents)", user.Username, vaultId, documents);
        await TellHubAsync(null, [vaultId]);
        return new DeletionSummary(1, 0, documents, 0);
    }

    /// <summary>
    /// Deletes the caller's own account; false if <paramref name="password"/> is wrong. <paramref name="confirm"/>
    /// must be the username (any case). The primary admin can't: there must always be one.
    /// </summary>
    public async Task<bool> DeleteOwnAccountAsync(
        User user, byte[] userKey, string password, string confirm, CancellationToken ct, SharedVaultChoice shared = SharedVaultChoice.GiveMembersTime)
    {
        if (!ConfirmsUsername(confirm, user.Username)) throw new ArgumentException("Type your username to confirm.", nameof(confirm));
        if (user.Role == Roles.PrimaryAdmin) throw new NotPermittedException("The primary admin's account can't be deleted.");
        if (!await users.CheckPasswordAsync(user.Id, userKey, password, ct)) return false;

        var summary = await DeleteAccountAsync(user.Id, Builders<UserDocument>.Filter.Where(u => u.Id == user.Id && u.Role != Roles.PrimaryAdmin), shared, null, ct, ownRuns: true)
            ?? throw new NotPermittedException("Your account changed meanwhile; nothing was deleted.");
        // After the commit, and whatever happens to the request now: the deletion is done either way.
        await list.AddAsync(user.Id, CancellationToken.None, summary.Closing);
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
    public async Task<DeletedUser?> DeleteUserAsync(
        User actor, byte[] actorKey, string password, string userId, string confirm, CancellationToken ct, SharedVaultChoice shared = SharedVaultChoice.GiveMembersTime)
    {
        var acting = await AdminRules.RequireAdminAsync(Users, actor, ct);
        // Before anything about the target: without the password, nobody learns whether the user exists.
        if (!await users.CheckPasswordAsync(acting.Id, actorKey, password, ct)) throw new WrongPasswordException("Your password is wrong.");
        if (userId == acting.Id) throw new NotPermittedException("Delete your own account in Settings.");
        var target = await Users.Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        if (target is null) return null;
        if (target.Role == Roles.PrimaryAdmin) throw new NotPermittedException("The primary admin can't be deleted.");
        if (!ConfirmsUsername(confirm, target.Username)) throw new ArgumentException("Type the username to confirm.", nameof(confirm));

        // Checked again in the transaction: the target can't have become primary admin meanwhile.
        var summary = await DeleteAccountAsync(userId, Builders<UserDocument>.Filter.Where(u => u.Id == userId && u.Role != Roles.PrimaryAdmin), shared, null, ct)
            ?? throw new NotPermittedException("The user changed meanwhile; nothing was deleted.");
        // The choice goes on the list with the id: a repeat after a restore applies it again.
        var listed = await list.AddAsync(userId, CancellationToken.None, summary.Closing);
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
        if (!await users.CheckPasswordAsync(acting.Id, actorKey, password, ct)) throw new WrongPasswordException("Your password is wrong.");

        var done = new List<DeletedUser>();
        var closing = await list.ReadClosingAsync(ct);
        foreach (var target in await restored.FindAsync(ct))
        {
            if (target.Id == acting.Id) continue;
            DeletionSummary? summary;
            try
            {
                // The choice made at the deletion, as recorded: a vault whose closing period has passed meanwhile goes now,
                // one whose hasn't closes again until its original time; any other goes now.
                summary = await DeleteAccountAsync(
                    target.Id, Builders<UserDocument>.Filter.Where(u => u.Id == target.Id && u.Role != Roles.PrimaryAdmin), SharedVaultChoice.DeleteNow,
                    closing.GetValueOrDefault(target.Id) ?? new Dictionary<string, DateTime>(), ct);
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

    /// <summary>How long members keep a closing vault, read-only, after its owner's account went (collab design §7.3).</summary>
    public static readonly TimeSpan ClosingPeriod = TimeSpan.FromDays(7);

    /// <summary>For tests: called with <c>"after-commit"</c> once the deletion's transaction has committed; an exception
    /// thrown there is a crash at that point.</summary>
    internal Func<string, Task>? FaultAt { get; set; }

    /// <summary>
    /// In one transaction: the user's vaults with everything in them, the user, their password, recovery and
    /// session slots, their API keys with the keys' slots, their connections with their codes and tokens (tombstones
    /// included), the tokens' slots and the pending authorisation requests, and their memberships of shared vaults with
    /// their member slots (the vaults stay their owners'). Null, and nothing deleted, if the user no
    /// longer matches <paramref name="stillDeletable"/>.
    /// <para>
    /// Her vaults with an active member (collab design §7.3): with <see cref="SharedVaultChoice.GiveMembersTime"/> each
    /// gets <c>closingAt</c> in the same transaction, and only her ways in go (<see cref="CloseInAsync"/>); the vault
    /// stays, through its member slots, and is left out of the vaults deleted here. With
    /// <see cref="SharedVaultChoice.DeleteNow"/> it goes with the others, and each member is left a notice
    /// (<see cref="Sharing.MemberNotices"/>). <paramref name="recorded"/>, for a repeat after a restore: the closing periods
    /// recorded at the deletion, by vault id, which replace the choice: a vault whose time hasn't come closes again until
    /// then, any other goes now. An admin's deletion (not <paramref name="ownRuns"/>) gives no closing period to a vault
    /// in a re-keying run: it goes now (collab design §7.1).
    /// </para>
    /// </summary>
    private async Task<DeletionSummary?> DeleteAccountAsync(
        string userId, FilterDefinition<UserDocument> stillDeletable, SharedVaultChoice choice, IReadOnlyDictionary<string, DateTime>? recorded, CancellationToken ct,
        bool ownRuns = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await DeleteAccountOnceAsync(userId, stillDeletable, choice, recorded, ownRuns, ct);
            }
            catch (RunStartedException) when (attempt < 3)
            {
                // A run of a vault given a closing period started meanwhile: decided again, it goes now.
            }
        }
    }

    private async Task<DeletionSummary?> DeleteAccountOnceAsync(
        string userId, FilterDefinition<UserDocument> stillDeletable, SharedVaultChoice choice, IReadOnlyDictionary<string, DateTime>? recorded, bool ownRuns,
        CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var owned = await OwnedVaultsAsync(userId, ct);
        if (ownRuns) await RequireNoSharedRunAsync(null, owned, ct);
        var withMembers = await WithActiveMembersAsync(owned, ct);
        // An admin's deletion isn't refused during a run (a privacy request can't wait for an owner who never comes
        // back), but a vault in its run can't be given a closing period: the new key's only copy is the owner's slot,
        // which goes, so the run could never finish and the members couldn't even export. It goes now, with their notices.
        HashSet<string> running = ownRuns ? new(StringComparer.Ordinal) : (await SharedRunsAsync(null, withMembers, ct)).ToHashSet(StringComparer.Ordinal);
        var closing = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var vaultId in withMembers)
        {
            if (running.Contains(vaultId)) continue;
            if (recorded is not null)
            {
                if (recorded.TryGetValue(vaultId, out var at) && at > now) closing[vaultId] = at;
            }
            else if (choice == SharedVaultChoice.GiveMembersTime) closing[vaultId] = now + ClosingPeriod;
        }

        var vaults = owned.Where(v => !closing.ContainsKey(v)).ToList();
        var deletedShared = withMembers.Where(v => !closing.ContainsKey(v)).ToList();
        var shared = await SharedCountAsync(vaults, ct);
        var notes = await CountAsync(Notes, vaults, ct);
        var attachments = await CountAsync(Attachments, vaults, ct);
        var keyCount = 0;
        var connectionCount = 0;
        var invitationCount = 0;
        long revokedCount = 0;
        var membershipCount = 0;
        var withdrawn = 0;
        var noticeCount = 0;
        long documents;
        try
        {
            documents = await catalog.DeleteVaultsAsync(
                vaults,
                async (s, token) =>
                {
                    if ((await Users.DeleteOneAsync(s, stillDeletable, cancellationToken: token)).DeletedCount == 0) throw new UserChangedException();
                    await UserSlots.DeleteManyAsync(s, u => u.User == userId, cancellationToken: token);
                    // The keys' slots are all on the user's own vaults, deleted above; any left over go by key id.
                    var keyIds = await Keys.Find(s, k => k.User == userId).Project(k => k.Id).ToListAsync(token);
                    foreach (var id in keyIds) await catalog.DeleteApiKeySlotsAsync(s, id, token);
                    await Keys.DeleteManyAsync(s, k => k.User == userId, cancellationToken: token);
                    keyCount = keyIds.Count;
                    // Her WebDAV shares the same way: those on others' vaults go with her memberships below, any left by share id.
                    var shareIds = await Shares.Find(s, d => d.User == userId).Project(d => d.Id).ToListAsync(token);
                    foreach (var id in shareIds) await catalog.DeleteDavSlotsAsync(s, id, token);
                    await Shares.DeleteManyAsync(s, d => d.User == userId, cancellationToken: token);
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
                    // The ones they created stay as the record of who invited whom, but none that is open stays usable.
                    revokedCount = await Invitations.RevokeOpenOfAsync(db, s, userId, now, token);
                    // Their memberships of other people's vaults, with the slots they reach them by; those vaults stay.
                    membershipCount = await Sharing.Memberships.DeleteOfMemberAsync(db, s, userId, token);
                    withdrawn = await CloseInAsync(s, closing, now, token);
                },
                ct,
                // Before the vaults' slots go: the members' notices copy their aliases from them.
                first: async (s, token) =>
                {
                    if (ownRuns) await RequireNoSharedRunAsync(s, owned, token);
                    else if ((await SharedRunsAsync(s, [.. closing.Keys], token)).Count > 0) throw new RunStartedException();
                    noticeCount = await Sharing.MemberNotices.LeaveForDeletedAsync(db, s, deletedShared, now, token);
                });
        }
        catch (UserChangedException)
        {
            return null;
        }

        if (FaultAt is { } fault) await fault("after-commit");
        // Nothing more of a closing vault is indexed in this process; others learn it from the members' next request.
        foreach (var vaultId in closing.Keys) catalog.OwnerActive(vaultId, false);
        // Her sockets close, and the members' drop a vault gone or hold a closing one read-only, before the answer.
        await TellHubAsync(userId, owned);
        if (revokedCount > 0) log.LogInformation("{Count} open invitations of deleted user {UserId} revoked", revokedCount, userId);
        if (membershipCount > 0) log.LogInformation("{Count} memberships of deleted user {UserId} ended", membershipCount, userId);
        foreach (var (vaultId, at) in closing) log.LogInformation("Vault {VaultId} of deleted user {UserId} is closing until {ClosingAt:O}", vaultId, userId, at);
        if (withdrawn > 0) log.LogInformation("{Count} vault invitations of deleted user {UserId} withdrawn", withdrawn, userId);
        for (var i = 0; i < withdrawn; i++) Metrics.KumbuMetrics.Invitation(Metrics.KumbuMetrics.InvitationOutcome.Withdrawn);
        if (noticeCount > 0) log.LogInformation("{Count} members of the shared vaults of deleted user {UserId} were left a notice", noticeCount, userId);

        // A vault the user made between the read above and the transaction (from another tab, say) has an owner
        // that no longer exists: it goes too.
        var late = await OwnedVaultsAsync(userId, ct);
        if (late.Count > 0)
        {
            shared += await SharedCountAsync(late, ct);
            notes += await CountAsync(Notes, late, ct);
            attachments += await CountAsync(Attachments, late, ct);
            documents += await catalog.DeleteVaultsAsync(late, (_, _) => Task.CompletedTask, ct);
        }

        return new DeletionSummary(vaults.Count + late.Count, keyCount, documents, connectionCount, notes, invitationCount, attachments, membershipCount, shared, closing);
    }

    /// <summary>
    /// Inside the deletion's transaction, for each vault given a closing period (collab design §7.3): <c>closingAt</c> on
    /// its document; every slot that isn't a member's goes (her user slot, her keys' and connections' slots, the dev
    /// token's, open invitations' slots), so the vault survives through its member slots and the slots derived from
    /// them alone; her open invitations to it are withdrawn, with the member slots of redemptions she never confirmed;
    /// and the members' event, with the date. Returns how many invitations were withdrawn.
    /// </summary>
    private async Task<int> CloseInAsync(IClientSessionHandle s, IReadOnlyDictionary<string, DateTime> closing, DateTime now, CancellationToken ct)
    {
        if (closing.Count == 0) return 0;
        var ids = closing.Keys.ToList();
        foreach (var (vaultId, at) in closing)
        {
            await catalog.Vaults.UpdateOneAsync(s, v => v.Id == vaultId, Builders<VaultDocument>.Update.Set(v => v.ClosingAt, at), cancellationToken: ct);
            await Sharing.VaultEvents.RecordAsync(db, s, vaultId, Sharing.VaultEvents.ClosingStarted, null, now, null, ct, until: at);
        }

        // Her publications go at once, not with the vault (publish spec §3.7); nobody publishes in a closing vault, so
        // its marks go with its record.
        await _publications.DeleteCopiesOfVaultsAsync(s, ids, ct);
        await _publications.DeleteRecordsOfVaultsAsync(s, ids, ct);

        var memberships = db.GetCollection<Sharing.MembershipDocument>(Sharing.Memberships.CollectionName);
        var m = Builders<Sharing.MembershipDocument>.Filter;
        var pending = m.In(x => x.Vault, ids) & m.Eq(x => x.Status, Sharing.MembershipStatus.Pending);
        var open = await memberships.Find(s, pending).Project(x => x.Id).ToListAsync(ct);
        var sl = Builders<SlotDocument>.Filter;
        await catalog.Slots.DeleteManyAsync(
            s,
            sl.In(x => x.Vault, ids) & ((sl.Ne(x => x.Kind, SlotDocument.Member) & sl.Exists(x => x.GrantedVia, false)) | (sl.Eq(x => x.Kind, SlotDocument.Member) & sl.In(x => x.Membership, open))),
            cancellationToken: ct);
        if (open.Count == 0) return 0;
        // Withdrawn as the owner withdraws one (VaultInvites.WithdrawAsync): their only ways in went above.
        await memberships.UpdateManyAsync(
            s,
            pending & m.In(x => x.Id, open),
            Builders<Sharing.MembershipDocument>.Update
                .Set(x => x.Status, Sharing.MembershipStatus.Revoked).Set(x => x.RevokedAt, now)
                .Unset(x => x.InviteLookup).Unset(x => x.ExpiresAt).Unset(x => x.Pin)
                .Set(x => x.CleanedAt, now).Inc(x => x.Revision, 1),
            cancellationToken: ct);
        return open.Count;
    }

    /// <summary>
    /// <see cref="RekeyRunningException"/> if one of these vaults has an active member and a re-keying run holds it or
    /// it is in its two-key period (collab design §7.1): only its owner's key finishes the run, and she is signed in, so
    /// it resumes and ends; deleted now, her account or the vault would leave the members with one nobody can finish.
    /// Before the deletion, and again inside its transaction (<paramref name="session"/>), where a start that commits
    /// meanwhile conflicts with the deletion of the vault document and the retry sees it.
    /// </summary>
    private async Task RequireNoSharedRunAsync(IClientSessionHandle? session, IReadOnlyList<string> vaults, CancellationToken ct)
    {
        if ((await SharedRunsAsync(session, vaults, ct)).Count > 0) throw new RekeyRunningException();
    }

    /// <summary>Of these vaults, those with an active member that a re-keying run holds or that are in their two-key
    /// period; inside <paramref name="session"/>'s transaction if given.</summary>
    private async Task<IReadOnlyList<string>> SharedRunsAsync(IClientSessionHandle? session, IReadOnlyList<string> vaults, CancellationToken ct)
    {
        if (vaults.Count == 0) return [];
        var v = Builders<VaultDocument>.Filter;
        var started = v.In(x => x.Id, vaults) & v.Ne(x => x.RekeyStartedAt, null);
        var running = vaults.Where(catalog.IsRekeying).Concat(
            session is null
                ? await catalog.Vaults.Find(started).Project(x => x.Id).ToListAsync(ct)
                : await catalog.Vaults.Find(session, started).Project(x => x.Id).ToListAsync(ct)).Distinct().ToList();
        if (running.Count == 0) return [];
        var f = Builders<Sharing.MembershipDocument>.Filter;
        var docs = db.GetCollection<Sharing.MembershipDocument>(Sharing.Memberships.CollectionName);
        var shared = f.In(m => m.Vault, running) & f.Eq(m => m.Status, Sharing.MembershipStatus.Active);
        return session is null
            ? await docs.Distinct(m => m.Vault, shared, cancellationToken: ct).ToListAsync(ct)
            : await docs.Distinct(session, m => m.Vault, shared, cancellationToken: ct).ToListAsync(ct);
    }

    /// <summary>Which of these vaults have an active member now: those a closing period applies to.</summary>
    private async Task<IReadOnlyList<string>> WithActiveMembersAsync(IReadOnlyList<string> vaults, CancellationToken ct)
    {
        if (vaults.Count == 0) return [];
        var f = Builders<Sharing.MembershipDocument>.Filter;
        var docs = db.GetCollection<Sharing.MembershipDocument>(Sharing.Memberships.CollectionName);
        return await docs.Distinct(m => m.Vault, f.In(m => m.Vault, vaults) & f.Eq(m => m.Status, Sharing.MembershipStatus.Active), cancellationToken: ct).ToListAsync(ct);
    }

    /// <summary>How many of these vaults are shared with anyone now (a membership or an invitation neither revoked nor
    /// left), read by vault id before they go.</summary>
    private async Task<int> SharedCountAsync(IReadOnlyList<string> vaults, CancellationToken ct)
    {
        if (vaults.Count == 0) return 0;
        var f = Builders<Sharing.MembershipDocument>.Filter;
        var docs = db.GetCollection<Sharing.MembershipDocument>(Sharing.Memberships.CollectionName);
        return (await docs.Distinct(m => m.Vault, f.In(m => m.Vault, vaults) & f.In(m => m.Status, [Sharing.MembershipStatus.Pending, Sharing.MembershipStatus.Active]), cancellationToken: ct)
            .ToListAsync(ct)).Count;
    }

    /// <summary>The notes or attachments in these vaults, counted by vault id: the receipt's counts, read without any key.</summary>
    private static async Task<long> CountAsync(IMongoCollection<BsonDocument> collection, IReadOnlyList<string> vaults, CancellationToken ct) =>
        vaults.Count == 0 ? 0 : await collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.In("vault", vaults), cancellationToken: ct);

    /// <summary>The vaults whose user slot names this owner, orphans of a failed create included.</summary>
    private async Task<IReadOnlyList<string>> OwnedVaultsAsync(string userId, CancellationToken ct) =>
        await catalog.Slots.Distinct(
            s => s.Vault, Builders<SlotDocument>.Filter.Eq(s => s.Kind, SlotDocument.User) & Builders<SlotDocument>.Filter.Eq(s => s.Owner, userId), cancellationToken: ct)
            .ToListAsync(ct);

    /// <summary>
    /// After a deletion committed (collab design §9.1): the hub checks at once the sockets of the user (if
    /// <paramref name="userId"/>) and those with one of <paramref name="vaultIds"/>. Whatever happens to the request
    /// meanwhile; a failure is only logged, and the 30-second check catches up.
    /// </summary>
    private async Task TellHubAsync(string? userId, IReadOnlyCollection<string> vaultIds)
    {
        try
        {
            if (userId is not null) await hub.UserChangedAsync(userId, CancellationToken.None);
            await hub.VaultsChangedAsync(vaultIds, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Telling the sync hub of a deletion failed; its sockets learn it at the next check");
        }
    }

    private sealed class UserChangedException() : Exception("The user changed during the deletion.");

    private sealed class RunStartedException() : Exception("A re-keying run of a vault to be closed started during the deletion.");
}

/// <summary>A shared vault, or the account of its owner, can't be deleted while a re-keying of the vault is under way
/// (collab design §7.1); nothing was deleted. Answered with 409.</summary>
public sealed class RekeyRunningException() : Exception("A re-encryption is still running; try again in a few minutes.");
