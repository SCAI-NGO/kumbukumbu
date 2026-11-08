// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Metrics;
using Kumbu.Server.Search;
using Kumbu.Server.Sync;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Api;

public sealed record UserView(string Id, string Username, string Display, string Role)
{
    public static UserView From(User user) => new(user.Id, user.Username, user.Display, user.Role);
}

/// <summary>The signed-in user. <c>previousSignInAt</c> is the previous visit, where the agent banner starts counting
/// changes: a sign-in or, for a remembered session, the first request after 30 minutes without one.
/// <c>passwordWindowOpen</c>: whether the actions that need the password lately given go ahead without asking for it
/// (a remembered session's normal window; always true for a normal session, and in the sign-in's answer).
/// <c>publishEnabled</c>: whether this server publishes notes (<c>Kumbu:Publish:Enabled</c>); off, the web app hides it.
/// <c>storageLimited</c>: whether a storage limit applies to the user, read from her limit without counting her storage;
/// the shell's notice asks for the storage only then.</summary>
public sealed record SessionView(UserView User, DateTime? PreviousSignInAt, bool PasswordWindowOpen, bool PublishEnabled, bool StorageLimited);

/// <summary><c>remember</c>: "Keep me signed in on this device", a session of 30 days (7 idle) with the normal limits
/// for the actions that need the password; false when left out.</summary>
public sealed record SignInRequest(string Username, string Password, bool Remember = false);

/// <summary>The current password, to open a remembered session's normal window again.</summary>
public sealed record ConfirmPasswordRequest(string Password);

/// <summary>What the join page may know of an invitation: nothing of who made it or for whom. <c>reason</c>
/// (<c>used</c>, <c>expired</c> or <c>revoked</c>) only when it isn't valid; optional, hence the default.</summary>
public sealed record InvitationPeekView(
    string Role, DateTime ExpiresAt, bool Valid, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

public sealed record PeekInvitationRequest(string Code);

public sealed record JoinRequest(string Code, string Username, string Password);

/// <summary>The new account; <c>recoveryKey</c> is shown here once and never again.</summary>
public sealed record JoinedView(UserView User, string RecoveryKey);

public sealed record RecoverRequest(string Username, string RecoveryKey, string NewPassword);

/// <summary><c>revokeApiKeys</c> also revokes every API key of the user and disconnects every app (the connections'
/// tokens are revoked, the apps reconnect); true when left out, so an older client gets the safe behaviour: a key or an
/// app authorised from a stolen session doesn't outlive the password change.</summary>
public sealed record ChangePasswordRequest(string OldPassword, string NewPassword, bool RevokeApiKeys = true);

/// <summary>Always true: a changed password ends every session, this one too, so the web app signs in again.</summary>
public sealed record PasswordChangedView(bool SignedOut);

/// <summary>The current password, so a stolen session alone can't make a new recovery key.</summary>
public sealed record RegenerateRecoveryKeyRequest(string CurrentPassword);

public sealed record RecoveryKeyView(string RecoveryKey);

/// <summary>"Re-encrypt my vaults": the current password, so a stolen session alone can't rotate the keys and take the
/// new recovery key.</summary>
public sealed record ReencryptRequest(string Password);

/// <summary>An API key as its owner sees it: vault names with <c>read</c> or <c>write</c>; never the key.
/// <c>expiresAt</c> is null for a key that doesn't expire; once past, the key is refused like a revoked one.
/// <c>kind</c>: <c>agent</c>, or <c>device</c> for a key paired from Obsidian. <c>publish</c>: the names of the vaults
/// it may publish on ("May publish"), all among <c>vaults</c>.</summary>
public sealed record ApiKeyView(
    string Id, string Name, string Prefix, IReadOnlyDictionary<string, string> Vaults, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt,
    DateTime? ExpiresAt, string Kind, string[] Publish);

/// <summary><c>vaults</c>: vault name → <c>read</c> or <c>write</c>. <c>expiresInDays</c>: 30, 90 or 365; left
/// out or null, the key doesn't expire. <c>publish</c>: the names of the vaults it may publish on, among <c>vaults</c>
/// and her own; left out or null, none.</summary>
public sealed record CreateApiKeyRequest(string Name, IReadOnlyDictionary<string, string> Vaults, int? ExpiresInDays = null, IReadOnlyList<string>? Publish = null);

/// <summary><c>key</c> is shown here once and never again.</summary>
public sealed record CreatedApiKeyView(string Key, ApiKeyView ApiKey);

/// <summary>A WebDAV share as its owner sees it: its username, label, vault names with their rights; never its password.
/// <c>expiresAt</c> is null for a share that doesn't expire. <c>lostVaults</c>: the ids of vaults it was given but no
/// longer opens (a re-keying took them).</summary>
public sealed record DavShareView(
    string Id, string Username, string Label, IReadOnlyDictionary<string, string> Vaults, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt,
    DateTime? ExpiresAt, string[] LostVaults);

/// <summary>The WebDAV tab: <c>enabled</c> is whether this server answers on <c>/dav/</c> (<c>Kumbu:Dav:Enabled</c>); the
/// shares are listed either way.</summary>
public sealed record DavSharesView(bool Enabled, DavShareView[] Shares);

/// <summary><c>vaults</c>: vault name → <c>read</c>, <c>write</c> or <c>maintain</c>. <c>expiresInDays</c>: 30, 90 or
/// 365; left out or null, the share doesn't expire. A share never publishes.</summary>
public sealed record CreateDavShareRequest(string Label, IReadOnlyDictionary<string, string> Vaults, int? ExpiresInDays = null);

/// <summary><c>password</c> is shown here once and never again; the username is the share's.</summary>
public sealed record CreatedDavShareView(string Password, DavShareView Share);

/// <summary>A user in the admin's list: metadata only. <c>lastActiveAt</c> is the latest sign-in or, for a remembered
/// session, the latest visit (its first request after 30 minutes without one); not the last request. <c>sharedVaults</c>:
/// how many of their vaults have an active member (a count, never a name). <c>quota</c>: <c>default</c>, <c>unlimited</c> or
/// <c>custom</c>, the override the admin set (WebDAV design §3.10); <c>limitBytes</c>: the limit that applies, the default's
/// or the override's, null for none.</summary>
public sealed record AdminUserView(
    string Id, string Username, string Display, string Role, bool Active, DateTime CreatedAt, DateTime? LastActiveAt, int Vaults, long BytesStored, int SharedVaults,
    string Quota, long? LimitBytes);

/// <summary>Any of them; the role is <c>user</c> or <c>admin</c>. Making someone an admin needs <c>password</c>, the
/// acting admin's own. <c>quota</c>: <c>"default"</c>, <c>"unlimited"</c> or a number of bytes, 0 or more (0: no write
/// that adds data).</summary>
public sealed record UpdateUserRequest(bool? Active = null, string? Role = null, string? Password = null, System.Text.Json.JsonElement? Quota = null);

/// <summary>What the session's own vaults take in the database, in bytes, and the limit that applies to them (null: none).</summary>
public sealed record AccountStorageView(long Used, long? Limit);

/// <summary>Deleting one's own account: the current password, and <c>confirm</c>, the username typed out.
/// <c>sharedVaults</c>: what becomes of her vaults shared with others (collab design §7.3): <c>give-members-7-days</c>
/// (the default) keeps each read-only and exportable for its members for 7 days, then deletes it; <c>delete-now</c>
/// deletes them at once, and each member is told.</summary>
public sealed record DeleteAccountRequest(string Password, string Confirm, string? SharedVaults = null);

/// <summary>Deleting a user (active or not): the admin's own current password, and <c>confirm</c>, the user's username
/// typed out. <c>sharedVaults</c>: as for one's own account, once for all of the user's shared vaults (default
/// <c>give-members-7-days</c>).</summary>
public sealed record DeleteUserRequest(string Password, string Confirm, string? SharedVaults = null);

/// <summary>A deleted user that a backup restore brought back.</summary>
public sealed record RestoredUserView(string Id, string Username, string Display);

/// <summary><c>configured</c>: the server keeps a list of deleted users (<c>Kumbu:Deletions:Path</c>); without it, a
/// restored user can't be detected. <c>restored</c>: the users on it that exist again.</summary>
public sealed record DeletionsView(bool Configured, IReadOnlyList<RestoredUserView> Restored);

/// <summary>Deleting again every user a restore brought back: the admin's own current password.</summary>
public sealed record RepeatDeletionsRequest(string Password);

/// <summary>
/// What an admin keeps as a record of a deletion for a privacy request: ids, the usernames, the time and counts, never
/// content or a vault or note name. <c>externalProviders</c>: the labels of every external embedding provider this
/// instance has configured (a vault's choice is sealed with it, so which ones it used can't be read); empty if none.
/// <c>privacyContact</c>: <c>Kumbu:Privacy:Contact</c>, or null. <c>listed</c>: the id was added to the list of deleted
/// users kept outside the database. <c>attachments</c>: files (PDFs, images) in the deleted vaults, as <c>notes</c>
/// counts notes. <c>invitations</c>: invitations the user joined with whose note and link to the
/// user were erased (the invitation itself stays as a used one). <c>apiKeys</c> and <c>connections</c> count every
/// record removed, revoked ones included. <c>memberships</c>: the user's memberships of vaults shared with them that
/// ended; <c>sharedVaultsClosed</c>: their own vaults that were shared with others and went for those members too
/// (collab design §7.2), counted, never named. <c>sharedNotes</c>: with memberships ended, what became of the user's
/// writes there, for the person: "Notes you wrote in vaults shared with you stay in those vaults; your name was removed
/// from their history."; else null. <c>sharedVaultsScheduled</c>: for each of their shared vaults given a closing period
/// instead, the time it will be deleted ("scheduled for deletion on …"); those aren't among <c>vaults</c>.
/// </summary>
public sealed record DeletionReceiptView(
    string UserId, string Username, string DeletedBy, DateTime DeletedAt, int Vaults, long Notes, long Attachments, int ApiKeys, int Connections, int Invitations,
    IReadOnlyList<string> ExternalProviders, string? PrivacyContact, bool Listed, int Memberships = 0, int SharedVaultsClosed = 0, string? SharedNotes = null,
    IReadOnlyList<DateTime>? SharedVaultsScheduled = null);

/// <summary><c>createdBy</c> and <c>usedBy</c> are user ids (<c>bootstrap-admin</c> for the primary admin's link);
/// <c>state</c> is <c>open</c>, <c>used</c>, <c>expired</c> or <c>revoked</c>.</summary>
public sealed record AdminInvitationView(
    string Id, string Role, string? Note, string CreatedBy, DateTime CreatedAt, DateTime ExpiresAt, DateTime? UsedAt, string? UsedBy, DateTime? RevokedAt, string State);

/// <summary>For <c>user</c> or <c>admin</c>, valid for <c>expiresInDays</c> (1 to 30, default 7). An invitation for
/// <c>admin</c> needs <c>password</c>, the acting admin's own.</summary>
public sealed record CreateInvitationRequest(string Role, string? Note = null, int? ExpiresInDays = null, string? Password = null);

/// <summary><c>code</c> and <c>link</c> are shown here once and never again.</summary>
public sealed record CreatedInvitationView(string Code, string Link, AdminInvitationView Invitation);

/// <summary>
/// The account endpoints under <c>/api</c> (accounts design §4). Four are anonymous (sign-in, the join page's
/// look at an invitation, join, recovery); every other one is for a signed-in session only
/// (<see cref="SessionOnlyMetadata"/>). The admin endpoints leave the role check to <see cref="Admin"/> and
/// <see cref="Invitations"/>, which make it on every call.
/// </summary>
public static class AccountApi
{
    private const string SignInFailed = "The username or password is wrong.";

    public static void MapAccountApi(this IEndpointRouteBuilder app)
    {
        var open = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter).AllowAnonymous();
        open.MapPost("/session", SignInAsync).Errors(400, 401, 403, 429);
        // POST with the code in the body, never in the path: request lines end up in logs.
        open.MapPost("/invitations/peek", PeekInvitationAsync).Errors(400, 403, 404, 429);
        open.MapPost("/join", JoinAsync).Errors(400, 403, 409, 429);
        open.MapPost("/recover", RecoverAsync).Errors(400, 401, 403, 409, 429);

        var account = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        account.MapGet("/session", Session).Errors(401, 403);
        account.MapDelete("/session", SignOutAsync).Errors(401, 403);
        account.MapPost("/session/confirm", ConfirmPasswordAsync).Errors(400, 401, 403, 429);
        account.MapPost("/password", ChangePasswordAsync).Errors(400, 401, 403, 409, 429);
        account.MapPost("/recovery-key", RegenerateRecoveryKeyAsync).Errors(400, 401, 403, 409, 429);
        account.MapPost("/sessions/end-others", EndOtherSessionsAsync).Errors(401, 403);
        account.MapPost("/account/delete", DeleteAccountAsync).Errors(400, 401, 403, 409, 429);
        account.MapPost("/account/reencrypt", ReencryptAsync).Errors(400, 401, 403, 409, 429);

        account.MapGet("/keys", ListKeysAsync).Errors(401, 403, 503);
        account.MapPost("/keys", CreateKeyAsync).WithMetadata(PasswordWindowMetadata.Instance).Errors(400, 401, 403, 409, 503);
        account.MapDelete("/keys/{id}", RevokeKeyAsync).Errors(401, 403, 404);

        // Made and revoked while WebDAV is switched off too: the shares wait for it to be on again.
        account.MapGet("/dav-shares", ListDavSharesAsync).Errors(401, 403, 503);
        account.MapPost("/dav-shares", CreateDavShareAsync).WithMetadata(PasswordWindowMetadata.Instance).Errors(400, 401, 403, 409, 503);
        account.MapDelete("/dav-shares/{id}", RevokeDavShareAsync).Errors(401, 403, 404);

        account.MapGet("/admin/users", ListUsersAsync).Errors(401, 403);
        account.MapPatch("/admin/users/{id}", UpdateUserAsync).Errors(400, 401, 403, 404, 429);
        account.MapPost("/admin/users/{id}/delete", DeleteUserAsync).Errors(400, 401, 403, 404, 429);
        account.MapGet("/admin/deletions", ListDeletionsAsync).Errors(401, 403);
        account.MapPost("/admin/deletions/repeat", RepeatDeletionsAsync).Errors(400, 401, 403, 429);
        account.MapGet("/admin/invitations", ListInvitationsAsync).Errors(401, 403);
        account.MapPost("/admin/invitations", CreateInvitationAsync).Errors(400, 401, 403, 429);
        account.MapDelete("/admin/invitations/{id}", RevokeInvitationAsync).Errors(401, 403, 404);
        account.MapGet("/account/storage", StorageAsync).Errors(401, 403);
        account.MapGet("/account/preferences", PreferencesAsync).Errors(401, 403);
        account.MapPut("/account/preferences", SetPreferencesAsync).Errors(400, 401, 403, 429);
    }

    private static async Task<Ok<SessionView>> SignInAsync(
        SignInRequest body, HttpContext http, UserCatalog users, Sessions sessions, Rekeying rekeying, AccountLimits limits, Pairings pairings,
        StorageQuota quota, IOptions<KumbuOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        if (limits.SignIn(http, body.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        // Unknown user, wrong password and inactive user all answer the same; Argon2 runs for each of them.
        if (await users.TrySignInAsync(body.Username, body.Password, ct) is not { } signedIn)
        {
            KumbuMetrics.SignedIn(KumbuMetrics.Outcome.Failure);
            throw new ApiException(StatusCodes.Status401Unauthorized, SignInFailed);
        }

        var (user, userKey, readAt) = signedIn;

        // Signing in again in the same browser replaces its session rather than leaving the old one behind.
        // Its codes go with it, whoever's session it was.
        if (http.Request.Cookies.TryGetValue(CallerMiddleware.SessionCookie, out var old) && await sessions.EndAsync(old, ct) is { } replaced)
        {
            pairings.DropAll(replaced);
        }

        SetSessionCookie(http, await sessions.StartAsync(user, userKey, readAt, ct, body.Remember), body.Remember);
        KumbuMetrics.SignedIn(KumbuMetrics.Outcome.Success);
        if (body.Remember) Log(logs).LogInformation("User {UserId} signed in and is kept signed in on this device", user.Id);
        // Vaults a recovery or "Re-encrypt my vaults" left pending (a restart lost the runner): after the session
        // started, and never waited for, so that sign-in is never held up by a re-keying.
        rekeying.ResumePending(user, userKey);
        return TypedResults.Ok(new SessionView(
            UserView.From(user), user.PreviousSignInAt, PasswordWindowOpen: true, options.Value.Publish.Enabled, await quota.LimitedAsync(user.Id, ct)));
    }

    private static async Task<Ok<InvitationPeekView>> PeekInvitationAsync(
        PeekInvitationRequest body, HttpContext http, Invitations invitations, AccountLimits limits, CancellationToken ct)
    {
        if (limits.AccountInvitationPeek(http) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var info = await invitations.PeekAsync(body.Code, ct) ?? throw ApiErrors.NotFound("This is not a valid invitation.");
        return TypedResults.Ok(new InvitationPeekView(info.Role, info.ExpiresAt, info.Valid, info.Valid ? null : StateText(info.State)));
    }

    private static async Task<Ok<JoinedView>> JoinAsync(
        JoinRequest body, HttpContext http, Invitations invitations, UserCatalog users, Sessions sessions, Welcome.WelcomeVault welcome, AccountLimits limits,
        CancellationToken ct)
    {
        if (limits.Join(http) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var (user, recoveryKey) = await invitations.RedeemAsync(body.Code, body.Username, body.Password, ct);

        // The account exists now, whatever happens next: the recovery key must reach the user even if signing in
        // fails (deactivated in the meantime), so that case answers without a cookie rather than with an error.
        // Signing in costs a second Argon2 run, for a user key the service doesn't hand out at creation.
        if (await users.TrySignInAsync(body.Username, body.Password, CancellationToken.None) is var (signedIn, userKey, readAt)
            && await TryStartSessionAsync(sessions, signedIn, userKey, readAt) is { } secret)
        {
            SetSessionCookie(http, secret, remember: false);
            // Never fails the join, and not cancellable, like the rest of the join after the claim.
            await welcome.CreateAsync(signedIn, userKey, readAt, CancellationToken.None);
        }

        return TypedResults.Ok(new JoinedView(UserView.From(user), recoveryKey));
    }

    /// <summary>A session for a user who just signed in, or null if they were deactivated or deleted in between:
    /// joining then still answers, without a cookie, so the recovery key reaches the user.</summary>
    internal static async Task<string?> TryStartSessionAsync(Sessions sessions, User user, byte[] userKey, DateTime readAt)
    {
        try
        {
            return await sessions.StartAsync(user, userKey, readAt, CancellationToken.None);
        }
        catch (CredentialsChangedException)
        {
            return null;
        }
    }

    /// <summary>The used recovery key stops working: the answer carries its replacement, shown here once. The user key
    /// is rotated in the recovery; the vaults are re-keyed in the background, after the answer, which never waits for them.</summary>
    private static async Task<Ok<RecoveryKeyView>> RecoverAsync(
        RecoverRequest body, HttpContext http, UserCatalog users, Rekeying rekeying, AccountLimits limits, Pairings pairings, SyncHub hub, CancellationToken ct)
    {
        if (limits.Recovery(http) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var (recoveryKey, user, userKey) = await users.RecoverWithKeyAsync(body.Username, body.RecoveryKey, body.NewPassword, ct)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, "The username or recovery key is wrong.");
        pairings.DropAll(user.Id);
        await hub.SubjectsEndedAsync(user.Id, CancellationToken.None);
        rekeying.ResumePending(user, userKey);
        return TypedResults.Ok(new RecoveryKeyView(recoveryKey));
    }

    private static async Task<Ok<SessionView>> Session(RequestCaller caller, StorageQuota quota, IOptions<KumbuOptions> options, CancellationToken ct) =>
        TypedResults.Ok(new SessionView(
            UserView.From(caller.User!), caller.User!.PreviousSignInAt, caller.PasswordWindowOpen, options.Value.Publish.Enabled,
            await quota.LimitedAsync(caller.User.Id, ct)));

    /// <summary>
    /// The password again, inside a session: a remembered session's normal window opens (keep-me-signed-in design). No
    /// new session and no new cookie. Checked and limited as every password check behind a session, against the user
    /// key in force; a wrong one is a 400, not a 401, which would sign the web app out.
    /// </summary>
    private static async Task<NoContent> ConfirmPasswordAsync(
        ConfirmPasswordRequest body, HttpContext http, RequestCaller caller, UserCatalog users, Sessions sessions, AccountLimits limits, ILoggerFactory logs,
        CancellationToken ct)
    {
        var user = caller.User!;
        if (limits.PasswordCheck(user.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        if (!await users.CheckPasswordAsync(user.Id, caller.UserKey!, body.Password, ct)) throw new WrongPasswordException("The password is wrong.");
        await PasswordGivenAsync(http, sessions, ct);
        Log(logs).LogInformation("User {UserId} confirmed their password", user.Id);
        return TypedResults.NoContent();
    }

    /// <summary>After a successful check of the session user's password: their session's normal window opens again, as
    /// the password was just given. For session-only endpoints.</summary>
    internal static async Task PasswordGivenAsync(HttpContext http, Sessions sessions, CancellationToken ct)
    {
        if (http.Request.Cookies.TryGetValue(CallerMiddleware.SessionCookie, out var secret)) await sessions.OpenWindowAsync(secret, ct);
    }

    private static ILogger Log(ILoggerFactory logs) => logs.CreateLogger(typeof(AccountApi).FullName!);

    private static async Task<NoContent> SignOutAsync(HttpContext http, RequestCaller caller, Sessions sessions, Pairings pairings, SyncHub hub, CancellationToken ct)
    {
        await sessions.EndAsync(http.Request.Cookies[CallerMiddleware.SessionCookie]!, ct);
        pairings.DropAll(caller.User!.Id);
        // Past the end: this tab's socket closes with it, rather than at the next check (collab design §9.1).
        await hub.SubjectsEndedAsync(caller.User!.Id, CancellationToken.None);
        ClearSessionCookie(http);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<PasswordChangedView>> ChangePasswordAsync(
        ChangePasswordRequest body, HttpContext http, RequestCaller caller, UserCatalog users, AccountLimits limits, Pairings pairings, SyncHub hub, CancellationToken ct)
    {
        var user = caller.User!;
        if (limits.PasswordCheck(user.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        if (!await users.ChangePasswordAsync(user, caller.UserKey!, body.OldPassword, body.NewPassword, body.RevokeApiKeys, ct))
        {
            throw new WrongPasswordException("The current password is wrong.");
        }

        pairings.DropAll(user.Id);
        await hub.SubjectsEndedAsync(user.Id, CancellationToken.None);
        ClearSessionCookie(http);
        return TypedResults.Ok(new PasswordChangedView(SignedOut: true));
    }

    private static async Task<Ok<RecoveryKeyView>> RegenerateRecoveryKeyAsync(
        RegenerateRecoveryKeyRequest body, HttpContext http, RequestCaller caller, UserCatalog users, Sessions sessions, AccountLimits limits, CancellationToken ct)
    {
        var user = caller.User!;
        // The same limit as the password change: both are a password check behind a session.
        if (limits.PasswordCheck(user.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var recoveryKey = await users.RegenerateRecoveryKeyAsync(user.Id, caller.UserKey!, body.CurrentPassword, ct)
            ?? throw new WrongPasswordException("The current password is wrong.");
        await PasswordGivenAsync(http, sessions, ct);
        return TypedResults.Ok(new RecoveryKeyView(recoveryKey));
    }

    /// <summary>
    /// "Re-encrypt my vaults": a new user key, every API key and app revoked, the vaults re-keyed in the background, and
    /// a new recovery key, shown here once. This session stays signed in; every other one ends.
    /// </summary>
    private static async Task<Ok<RecoveryKeyView>> ReencryptAsync(
        ReencryptRequest body, HttpContext http, RequestCaller caller, Rekeying rekeying, Sessions sessions, AccountLimits limits, Pairings pairings,
        SyncHub hub, CancellationToken ct)
    {
        var user = caller.User!;
        if (limits.PasswordCheck(user.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var recoveryKey = await rekeying.ReencryptAccountAsync(user, caller.UserKey!, body.Password, http.Request.Cookies[CallerMiddleware.SessionCookie]!, ct)
            ?? throw new WrongPasswordException("The password is wrong.");
        await PasswordGivenAsync(http, sessions, ct);
        pairings.DropAll(user.Id);
        await hub.SubjectsEndedAsync(user.Id, CancellationToken.None);
        return TypedResults.Ok(new RecoveryKeyView(recoveryKey));
    }

    /// <summary>Everything of the account goes, this session too: the cookie is cleared with it.</summary>
    private static async Task<NoContent> DeleteAccountAsync(
        DeleteAccountRequest body, HttpContext http, RequestCaller caller, Deletion deletion, AccountLimits limits, Pairings pairings, CancellationToken ct)
    {
        var user = caller.User!;
        if (limits.PasswordCheck(user.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var shared = SharedVaultChoice(body.SharedVaults);
        if (!await deletion.DeleteOwnAccountAsync(user, caller.UserKey!, body.Password, body.Confirm, ct, shared)) throw new WrongPasswordException("The password is wrong.");
        pairings.DropAll(user.Id);
        ClearSessionCookie(http);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> EndOtherSessionsAsync(HttpContext http, RequestCaller caller, Sessions sessions, Pairings pairings, SyncHub hub, CancellationToken ct)
    {
        await sessions.EndAllAsync(caller.User!.Id, http.Request.Cookies[CallerMiddleware.SessionCookie], ct);
        // A code made by one of the sessions ended (perhaps the one that prompted this) goes with them.
        pairings.DropAll(caller.User!.Id);
        await hub.SubjectsEndedAsync(caller.User!.Id, CancellationToken.None);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<ApiKeyView[]>> ListKeysAsync(RequestCaller caller, ApiKeys keys, CancellationToken ct)
    {
        var names = await VaultNamesAsync(caller, ct);
        var list = await keys.ListAsync(caller.User!, caller.UserKey!, ct);
        return TypedResults.Ok(list.Select(k => KeyView(k, names)).ToArray());
    }

    private static async Task<Created<CreatedApiKeyView>> CreateKeyAsync(CreateApiKeyRequest body, RequestCaller caller, ApiKeys keys, CancellationToken ct)
    {
        var names = await VaultNamesAsync(caller, ct);
        var vaults = RightsByVaultId(body.Vaults, names);
        var (key, info) = await keys.CreateAsync(
            caller.User!, caller.UserKey!, caller.CredentialReadAt!.Value, body.Name, vaults, ct, body.ExpiresInDays, publish: PublishByVaultId(body.Publish, names));
        return TypedResults.Created($"/api/keys/{info.Id}", new CreatedApiKeyView(key, KeyView(info, names)));
    }

    private static async Task<NoContent> RevokeKeyAsync(string id, RequestCaller caller, ApiKeys keys, CancellationToken ct) =>
        await keys.RevokeAsync(caller.User!, id, ct) ? TypedResults.NoContent() : throw ApiErrors.NotFound("You have no such API key that isn't revoked.");

    private static async Task<Ok<DavSharesView>> ListDavSharesAsync(RequestCaller caller, DavShares shares, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var names = await VaultNamesAsync(caller, ct);
        var list = await shares.ListAsync(caller.User!, caller.UserKey!, ct);
        return TypedResults.Ok(new DavSharesView(options.Value.Dav.Enabled, [.. list.Select(s => DavShareView(s, names))]));
    }

    private static async Task<Created<CreatedDavShareView>> CreateDavShareAsync(CreateDavShareRequest body, RequestCaller caller, DavShares shares, CancellationToken ct)
    {
        var names = await VaultNamesAsync(caller, ct);
        var vaults = RightsByVaultId(body.Vaults, names);
        var (_, password, info) = await shares.CreateAsync(caller.User!, caller.UserKey!, caller.CredentialReadAt!.Value, body.Label, vaults, ct, body.ExpiresInDays);
        return TypedResults.Created($"/api/dav-shares/{info.Id}", new CreatedDavShareView(password, DavShareView(info, names)));
    }

    private static async Task<NoContent> RevokeDavShareAsync(string id, RequestCaller caller, DavShares shares, CancellationToken ct) =>
        await shares.RevokeAsync(caller.User!, id, ct) ? TypedResults.NoContent() : throw ApiErrors.NotFound("You have no such WebDAV share that isn't revoked.");

    private static async Task<Ok<AdminUserView[]>> ListUsersAsync(RequestCaller caller, Admin admin, StorageQuota quota, CancellationToken ct) =>
        TypedResults.Ok((await admin.ListUsersAsync(caller.User!, ct))
            .Select(u => new AdminUserView(
                u.Id, u.Username, u.Display, u.Role, u.Active, u.CreatedAt, u.LastActiveAt, u.Vaults, u.BytesStored, u.SharedVaults,
                StorageQuota.Kind(u.Quota), quota.Effective(u.Quota)))
            .ToArray());

    /// <summary>The session's own storage (Settings shows it; the shell's notice asks once a minute, and only when the
    /// session says a limit applies, <see cref="SessionView"/>): the
    /// limit as it is now, one document read, and the usage as <c>df</c> has it, counted at most once per
    /// <see cref="StorageQuota.ShownFor"/>, since a count reads every document of her vaults. Settings shows the usage
    /// without a limit too, so it is counted then as well.</summary>
    private static async Task<Ok<AccountStorageView>> StorageAsync(RequestCaller caller, StorageQuota quota, CancellationToken ct)
    {
        var limit = await quota.LimitAsync(caller.User!.Id, ct);
        var figure = await quota.RecentFigureAsync(caller.User.Id, ct);
        return TypedResults.Ok(new AccountStorageView(figure.Used, limit));
    }

    /// <summary>An override as the request gives it: <c>"default"</c> (null), <c>"unlimited"</c>, or whole bytes.</summary>
    private static long? QuotaOf(System.Text.Json.JsonElement value) => value switch
    {
        { ValueKind: System.Text.Json.JsonValueKind.String } when value.GetString() == "default" => null,
        { ValueKind: System.Text.Json.JsonValueKind.String } when value.GetString() == "unlimited" => StorageQuota.Unlimited,
        { ValueKind: System.Text.Json.JsonValueKind.Number } when value.TryGetInt64(out var bytes) && bytes >= 0 => bytes,
        _ => throw ApiErrors.BadRequest("'quota' is \"default\", \"unlimited\" or a number of bytes, 0 or more."),
    };

    private static async Task<NoContent> UpdateUserAsync(
        string id, UpdateUserRequest body, HttpContext http, RequestCaller caller, Admin admin, StorageQuota quota, Pairings pairings, UserCatalog users,
        Sessions sessions, AccountLimits limits, CancellationToken ct)
    {
        if (body is { Active: null, Role: null, Quota: null }) throw ApiErrors.BadRequest("Give 'active', 'role', 'quota' or more of them.");
        // Before anything is written: a quota that doesn't read refuses the whole request.
        var newQuota = body.Quota is { } given ? QuotaOf(given) : null;
        // Every change to another account needs the window: deactivating or demoting destroys that account's
        // credentials, which a remembered session with its window closed must not do. Making an admin brings the
        // password itself, which opens it.
        if (body.Role == Roles.Admin) await RequirePasswordAsync(http, caller, body.Password, "Making someone an admin needs your password.", users, sessions, limits, ct);
        else Security.RequirePasswordWindow(caller);
        // A refused request changes nothing. The role goes first, and SetRoleAsync refuses before it writes (an
        // unusable role, the primary admin, no such user, an actor who isn't an admin). The one refusal left to
        // SetActiveAsync after that is deactivating oneself, so it is checked here, before the role is set; and the
        // quota's checks run here too, so the quota, written last, is refused by nothing (not even by an admin who
        // makes herself a user in the same request).
        if (body.Active is false && id == caller.User!.Id) throw new NotPermittedException("You can't deactivate yourself.");
        var quotaChange = body.Quota is null ? null : await admin.PrepareQuotaAsync(caller.User!, id, ct) ?? throw NoSuchUser();
        if (body.Role is { } role && !await admin.SetRoleAsync(caller.User!, id, role, ct)) throw NoSuchUser();
        if (body.Active is { } active && !await admin.SetActiveAsync(caller.User!, id, active, ct)) throw NoSuchUser();
        if (body.Active is false) pairings.DropAll(id);
        if (body.Quota is not null)
        {
            if (!await admin.SetQuotaAsync(quotaChange!, newQuota, ct)) throw NoSuchUser();
            // At once in this process: the next write of hers is checked against the new limit.
            await quota.RecountAsync(id);
        }

        return TypedResults.NoContent();

        static ApiException NoSuchUser() => ApiErrors.NotFound("There is no such user.");
    }

    /// <summary>An admin deletes another user, active or not, in one step, and gets the receipt for their records.</summary>
    private static async Task<Ok<DeletionReceiptView>> DeleteUserAsync(
        string id, DeleteUserRequest body, HttpContext http, RequestCaller caller, Deletion deletion, Sessions sessions, AccountLimits limits, SearchIndex index,
        IOptions<KumbuOptions> options, Pairings pairings, CancellationToken ct)
    {
        if (limits.PasswordCheck(caller.User!.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        // A wrong password throws; null is a right one and no such user.
        var shared = SharedVaultChoice(body.SharedVaults);
        var deleted = await deletion.DeleteUserAsync(caller.User!, caller.UserKey!, body.Password, id, body.Confirm, ct, shared);
        await PasswordGivenAsync(http, sessions, ct);
        if (deleted is null) throw ApiErrors.NotFound("There is no such user.");
        pairings.DropAll(deleted.Id);
        return TypedResults.Ok(ReceiptView(deleted, index, options.Value));
    }

    private static async Task<Ok<DeletionsView>> ListDeletionsAsync(RequestCaller caller, RestoredDeletions restored, CancellationToken ct) =>
        TypedResults.Ok(new DeletionsView(
            restored.Configured, (await restored.ListAsync(caller.User!, ct)).Select(u => new RestoredUserView(u.Id, u.Username, u.Display)).ToArray()));

    /// <summary>After a restore: every user it brought back is deleted again, one receipt each.</summary>
    private static async Task<Ok<DeletionReceiptView[]>> RepeatDeletionsAsync(
        RepeatDeletionsRequest body, HttpContext http, RequestCaller caller, Deletion deletion, Sessions sessions, AccountLimits limits, SearchIndex index,
        IOptions<KumbuOptions> options, Pairings pairings, CancellationToken ct)
    {
        if (limits.PasswordCheck(caller.User!.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var deleted = await deletion.RepeatAsync(caller.User!, caller.UserKey!, body.Password, ct);
        await PasswordGivenAsync(http, sessions, ct);
        foreach (var user in deleted) pairings.DropAll(user.Id);
        return TypedResults.Ok(deleted.Select(d => ReceiptView(d, index, options.Value)).ToArray());
    }

    internal static DeletionReceiptView ReceiptView(DeletedUser deleted, SearchIndex index, KumbuOptions options) => new(
        deleted.Id, deleted.Username, deleted.DeletedBy, deleted.DeletedAt, deleted.Summary.Vaults, deleted.Summary.Notes, deleted.Summary.Attachments, deleted.Summary.ApiKeys,
        deleted.Summary.Connections, deleted.Summary.Invitations, index.Providers.All.Where(p => p.External).Select(p => p.Label).ToArray(),
        string.IsNullOrWhiteSpace(options.Privacy.Contact) ? null : options.Privacy.Contact.Trim(), deleted.Listed,
        deleted.Summary.Memberships, deleted.Summary.SharedVaults, deleted.Summary.Memberships > 0 ? SharedNotesSentence : null,
        [.. (deleted.Summary.Closing?.Values ?? []).Order()]);

    private static SharedVaultChoice SharedVaultChoice(string? text) => SharedVaultChoices.Parse(text)
        ?? throw ApiErrors.BadRequest($"'sharedVaults' is '{SharedVaultChoices.GiveMembersTime}' or '{SharedVaultChoices.DeleteNow}'.");

    /// <summary>The concept's sentence (§9) on a member's writes after her account's deletion.</summary>
    internal const string SharedNotesSentence = "Notes you wrote in vaults shared with you stay in those vaults; your name was removed from their history.";

    private static async Task<Ok<AdminInvitationView[]>> ListInvitationsAsync(RequestCaller caller, Invitations invitations, CancellationToken ct) =>
        TypedResults.Ok((await invitations.ListAsync(caller.User!, ct)).Select(InvitationView).ToArray());

    private static async Task<Created<CreatedInvitationView>> CreateInvitationAsync(
        CreateInvitationRequest body, HttpContext http, RequestCaller caller, Invitations invitations, UserCatalog users, Sessions sessions, AccountLimits limits,
        IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var expiry = body.ExpiresInDays is { } days
            ? days > 0 && days <= Invitations.MaxExpiry.TotalDays ? TimeSpan.FromDays(days) : throw ApiErrors.BadRequest($"An invitation is valid for 1 to {Invitations.MaxExpiry.TotalDays} days.")
            : (TimeSpan?)null;
        // An admin invitation brings the password, which opens the window; any other needs the window open.
        if (body.Role == Roles.Admin) await RequirePasswordAsync(http, caller, body.Password, "Inviting an admin needs your password.", users, sessions, limits, ct);
        else Security.RequirePasswordWindow(caller);
        var (code, info) = await invitations.CreateAsync(caller.User!, body.Role, body.Note, expiry, ct);
        var link = Invitations.Link(options.Value.PublicUrl, code);
        return TypedResults.Created($"/api/admin/invitations/{info.Id}", new CreatedInvitationView(code, link, InvitationView(info)));
    }

    /// <summary>
    /// Making an admin, by invitation or by role, needs the acting admin's password, checked and limited as a password
    /// change checks it: a stolen admin session alone must not make an admin account that outlives it. Both ways,
    /// since an invitation for a user and a promotion would otherwise make one too. A user invitation needs none: the
    /// account it makes opens nothing but its own vaults. The right password opens the session's normal window.
    /// </summary>
    private static async Task RequirePasswordAsync(
        HttpContext http, RequestCaller caller, string? password, string missing, UserCatalog users, Sessions sessions, AccountLimits limits, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password)) throw ApiErrors.BadRequest(missing);
        if (limits.PasswordCheck(caller.User!.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        if (!await users.CheckPasswordAsync(caller.User!.Id, caller.UserKey!, password, ct)) throw new WrongPasswordException("Your password is wrong.");
        await PasswordGivenAsync(http, sessions, ct);
    }

    private static async Task<NoContent> RevokeInvitationAsync(string id, RequestCaller caller, Invitations invitations, CancellationToken ct) =>
        await invitations.RevokeAsync(caller.User!, id, ct) ? TypedResults.NoContent() : throw ApiErrors.NotFound("There is no such open invitation.");

    /// <summary>A request's rights (vault name → <c>read</c>/<c>write</c>/<c>maintain</c>) by vault id; 400 for a vault the caller
    /// has no such name for or another right.</summary>
    internal static Dictionary<string, KeyRight> RightsByVaultId(IReadOnlyDictionary<string, string> requested, Dictionary<string, string> names)
    {
        var idsByName = names.ToDictionary(n => n.Value, n => n.Key, StringComparer.Ordinal);
        var vaults = new Dictionary<string, KeyRight>();
        foreach (var (name, rightText) in requested)
        {
            var id = idsByName.GetValueOrDefault(name) ?? throw ApiErrors.BadRequest($"You have no vault named '{name}'.");
            vaults[id] = KeyRights.Parse(rightText) ?? throw ApiErrors.BadRequest($"A right is 'read', 'write' or 'maintain', not '{rightText}'.");
        }

        return vaults;
    }

    /// <summary>A request's "May publish" (vault names) by vault id; 400 for a vault the caller has no such name for.
    /// Whether it may be given there is for the service to say (an <see cref="ArgumentException"/>, a 400 too).</summary>
    internal static HashSet<string> PublishByVaultId(IReadOnlyList<string>? requested, Dictionary<string, string> names)
    {
        var idsByName = names.ToDictionary(n => n.Value, n => n.Key, StringComparer.Ordinal);
        var publish = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in requested ?? []) publish.Add((name is null ? null : idsByName.GetValueOrDefault(name)) ?? throw ApiErrors.BadRequest($"You have no vault named '{name}'."));
        return publish;
    }

    /// <summary>"May publish" by vault name, in order; as for the rights, a vault that no longer opens for the user isn't listed.</summary>
    internal static string[] PublishNames(IReadOnlySet<string> publish, Dictionary<string, string> names) =>
        [.. publish.Where(names.ContainsKey).Select(id => names[id]).Order(StringComparer.Ordinal)];

    /// <summary>The caller's vaults, id → name. Opening them is what a session does on every vault request anyway.</summary>
    internal static async Task<Dictionary<string, string>> VaultNamesAsync(RequestCaller caller, CancellationToken ct) =>
        (await caller.OpenVaultsAsync(ct)).ToDictionary(v => v.Id, v => v.Name, StringComparer.Ordinal);

    /// <summary>A vault the key was given but that no longer opens for the user isn't listed: it has no name to show.</summary>
    private static ApiKeyView KeyView(ApiKeyInfo info, Dictionary<string, string> names) => new(
        info.Id,
        info.Name,
        info.Prefix,
        info.Vaults.Where(v => names.ContainsKey(v.Key)).ToDictionary(v => names[v.Key], v => KeyRights.ToText(v.Value)),
        info.CreatedAt,
        info.LastUsedAt,
        info.RevokedAt,
        info.ExpiresAt,
        info.Kind == ApiKeyKind.Device ? "device" : "agent",
        PublishNames(info.Publish, names));

    /// <summary>As <see cref="KeyView"/>: a vault that no longer opens for the user isn't listed by name.</summary>
    private static DavShareView DavShareView(DavShareInfo info, Dictionary<string, string> names) => new(
        info.Id,
        info.Username,
        info.Label,
        info.Vaults.Where(v => names.ContainsKey(v.Key)).ToDictionary(v => names[v.Key], v => KeyRights.ToText(v.Value)),
        info.CreatedAt,
        info.LastUsedAt,
        info.RevokedAt,
        info.ExpiresAt,
        [.. info.LostVaults]);

    private static AdminInvitationView InvitationView(InvitationInfo i) =>
        new(i.Id, i.Role, i.Note, i.CreatedBy, i.CreatedAt, i.ExpiresAt, i.UsedAt, i.UsedBy, i.RevokedAt, StateText(i.State));

    private static string StateText(InvitationState state) => state.ToString().ToLowerInvariant();

    private static CookieOptions SessionCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };

    private static void SetSessionCookie(HttpContext http, string secret, bool remember)
    {
        var options = SessionCookieOptions();
        // The browser drops it when the session would end anyway; the server checks both limits regardless.
        options.MaxAge = remember ? Sessions.RememberedAbsoluteTimeout : Sessions.AbsoluteTimeout;
        http.Response.Cookies.Append(CallerMiddleware.SessionCookie, secret, options);
    }

    private static void ClearSessionCookie(HttpContext http) => http.Response.Cookies.Delete(CallerMiddleware.SessionCookie, SessionCookieOptions());

    private static async Task<Ok<PreferencesView>> PreferencesAsync(RequestCaller caller, UserCatalog users, CancellationToken ct) =>
        TypedResults.Ok(new PreferencesView(await users.AsksPasswordForDownloadsAsync(caller.User!.Id, ct)));

    private static async Task<Ok<PreferencesView>> SetPreferencesAsync(
        PreferencesRequest body, HttpContext http, RequestCaller caller, UserCatalog users, Sessions sessions, AccountLimits limits, ILoggerFactory logs,
        CancellationToken ct)
    {
        var user = caller.User!;
        if (!body.AskPasswordForDownloads && await users.AsksPasswordForDownloadsAsync(user.Id, ct))
        {
            if (string.IsNullOrEmpty(body.Password)) throw ApiErrors.BadRequest("Turning off the password for downloads of several files needs your password.");
            // The same limit as the export's ticket: both are a password check behind a session.
            if (limits.PasswordCheck(user.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
            if (!await users.CheckPasswordAsync(user.Id, caller.UserKey!, body.Password, ct)) throw new WrongPasswordException("The password is wrong.");
            await PasswordGivenAsync(http, sessions, ct);
            Log(logs).LogInformation("User {UserId} turned off the password for downloads of several files", user.Id);
        }

        await users.SetAsksPasswordForDownloadsAsync(user.Id, body.AskPasswordForDownloads, ct);
        return TypedResults.Ok(new PreferencesView(body.AskPasswordForDownloads));
    }

    /// <summary>The counterpart of <see cref="PasswordGivenAsync"/>: a wrong one counts against the session, which
    /// ends if it is remembered and this is the fifth in a row or the tenth in all; the user's open pairing codes go
    /// with it. Called by <see cref="ApiErrors.Filter"/> for every <see cref="WrongPasswordException"/>, so a check
    /// only has to throw that.</summary>
    internal static async Task PasswordWrongAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.Cookies.TryGetValue(CallerMiddleware.SessionCookie, out var secret)) return;
        if (await http.RequestServices.GetRequiredService<Sessions>().PasswordWrongAsync(secret, ct) is { } userId)
        {
            http.RequestServices.GetRequiredService<Pairings>().DropAll(userId);
            Log(http.RequestServices.GetRequiredService<ILoggerFactory>()).LogInformation("Ended a remembered session of user {UserId} after too many wrong passwords", userId);
        }
    }
}

/// <summary>The user's own settings. <c>askPasswordForDownloads</c>: "Ask for my password when downloading several
/// files" (default true), see <c>GET /api/files/{vault}/zip</c>.</summary>
public sealed record PreferencesView(bool AskPasswordForDownloads);

/// <summary>A change of the preferences. Turning <c>askPasswordForDownloads</c> off needs <c>password</c>: it is a
/// protection a stolen session alone must not be able to drop.</summary>
public sealed record PreferencesRequest(bool AskPasswordForDownloads, string? Password = null);
