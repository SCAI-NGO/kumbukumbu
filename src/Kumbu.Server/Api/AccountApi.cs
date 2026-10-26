// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Search;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Api;

public sealed record UserView(string Id, string Username, string Display, string Role)
{
    public static UserView From(User user) => new(user.Id, user.Username, user.Display, user.Role);
}

/// <summary>The signed-in user; <c>previousSignInAt</c> is where the agent banner starts counting changes.</summary>
public sealed record SessionView(UserView User, DateTime? PreviousSignInAt);

public sealed record SignInRequest(string Username, string Password);

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
/// <c>expiresAt</c> is null for a key that doesn't expire; once past, the key is refused like a revoked one.</summary>
public sealed record ApiKeyView(
    string Id, string Name, string Prefix, IReadOnlyDictionary<string, string> Vaults, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt,
    DateTime? ExpiresAt);

/// <summary><c>vaults</c>: vault name → <c>read</c> or <c>write</c>. <c>expiresInDays</c>: 30, 90 or 365; left
/// out or null, the key doesn't expire.</summary>
public sealed record CreateApiKeyRequest(string Name, IReadOnlyDictionary<string, string> Vaults, int? ExpiresInDays = null);

/// <summary><c>key</c> is shown here once and never again.</summary>
public sealed record CreatedApiKeyView(string Key, ApiKeyView ApiKey);

/// <summary>A user in the admin's list: metadata only. <c>lastSignInAt</c> is the last sign-in, not the last request.</summary>
public sealed record AdminUserView(
    string Id, string Username, string Display, string Role, bool Active, DateTime CreatedAt, DateTime? LastSignInAt, int Vaults, long BytesStored);

/// <summary>Either or both; the role is <c>user</c> or <c>admin</c>.</summary>
public sealed record UpdateUserRequest(bool? Active = null, string? Role = null);

/// <summary>Deleting one's own account: the current password, and <c>confirm</c>, the username typed out.</summary>
public sealed record DeleteAccountRequest(string Password, string Confirm);

/// <summary>Deleting a user (active or not): the admin's own current password, and <c>confirm</c>, the user's username typed out.</summary>
public sealed record DeleteUserRequest(string Password, string Confirm);

/// <summary>
/// What an admin keeps as a record of a deletion for a privacy request: ids, the usernames, the time and counts, never
/// content or a vault or note name. <c>externalProviders</c>: the labels of every external embedding provider this
/// instance has configured (a vault's choice is sealed with it, so which ones it used can't be read); empty if none.
/// <c>privacyContact</c>: <c>Kumbu:Privacy:Contact</c>, or null. <c>listed</c>: the id was added to the list of deleted
/// users kept outside the database.
/// </summary>
public sealed record DeletionReceiptView(
    string UserId, string Username, string DeletedBy, DateTime DeletedAt, int Vaults, long Notes, int ApiKeys, int Connections,
    IReadOnlyList<string> ExternalProviders, string? PrivacyContact, bool Listed);

/// <summary><c>createdBy</c> and <c>usedBy</c> are user ids (<c>bootstrap-admin</c> for the primary admin's link);
/// <c>state</c> is <c>open</c>, <c>used</c>, <c>expired</c> or <c>revoked</c>.</summary>
public sealed record AdminInvitationView(
    string Id, string Role, string? Note, string CreatedBy, DateTime CreatedAt, DateTime ExpiresAt, DateTime? UsedAt, string? UsedBy, DateTime? RevokedAt, string State);

/// <summary>For <c>user</c> or <c>admin</c>, valid for <c>expiresInDays</c> (1 to 30, default 7).</summary>
public sealed record CreateInvitationRequest(string Role, string? Note = null, int? ExpiresInDays = null);

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
        open.MapPost("/invitations/peek", PeekInvitationAsync).Errors(400, 403, 404);
        open.MapPost("/join", JoinAsync).Errors(400, 403, 409, 429);
        open.MapPost("/recover", RecoverAsync).Errors(400, 401, 403, 409, 429);

        var account = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        account.MapGet("/session", Session).Errors(401, 403);
        account.MapDelete("/session", SignOutAsync).Errors(401, 403);
        account.MapPost("/password", ChangePasswordAsync).Errors(400, 401, 403, 409, 429);
        account.MapPost("/recovery-key", RegenerateRecoveryKeyAsync).Errors(400, 401, 403, 409, 429);
        account.MapPost("/sessions/end-others", EndOtherSessionsAsync).Errors(401, 403);
        account.MapPost("/account/delete", DeleteAccountAsync).Errors(400, 401, 403, 429);
        account.MapPost("/account/reencrypt", ReencryptAsync).Errors(400, 401, 403, 409, 429);

        account.MapGet("/keys", ListKeysAsync).Errors(401, 403, 503);
        account.MapPost("/keys", CreateKeyAsync).Errors(400, 401, 403, 409, 503);
        account.MapDelete("/keys/{id}", RevokeKeyAsync).Errors(401, 403, 404);

        account.MapGet("/admin/users", ListUsersAsync).Errors(401, 403);
        account.MapPatch("/admin/users/{id}", UpdateUserAsync).Errors(400, 401, 403, 404);
        account.MapPost("/admin/users/{id}/delete", DeleteUserAsync).Errors(400, 401, 403, 404, 429);
        account.MapGet("/admin/invitations", ListInvitationsAsync).Errors(401, 403);
        account.MapPost("/admin/invitations", CreateInvitationAsync).Errors(400, 401, 403);
        account.MapDelete("/admin/invitations/{id}", RevokeInvitationAsync).Errors(401, 403, 404);
    }

    private static async Task<Ok<SessionView>> SignInAsync(
        SignInRequest body, HttpContext http, UserCatalog users, Sessions sessions, Rekeying rekeying, AccountLimits limits, CancellationToken ct)
    {
        if (limits.SignIn(http, body.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        // Unknown user, wrong password and inactive user all answer the same; Argon2 runs for each of them.
        var (user, userKey, readAt) = await users.TrySignInAsync(body.Username, body.Password, ct)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, SignInFailed);

        // Signing in again in the same browser replaces its session rather than leaving the old one behind.
        if (http.Request.Cookies.TryGetValue(CallerMiddleware.SessionCookie, out var old)) await sessions.EndAsync(old, ct);
        SetSessionCookie(http, await sessions.StartAsync(user, userKey, readAt, ct));
        // Vaults a recovery or "Re-encrypt my vaults" left pending (a restart lost the runner): after the session
        // started, and never waited for, so that sign-in is never held up by a re-keying.
        rekeying.ResumePending(user, userKey);
        return TypedResults.Ok(new SessionView(UserView.From(user), user.PreviousSignInAt));
    }

    private static async Task<Ok<InvitationPeekView>> PeekInvitationAsync(PeekInvitationRequest body, Invitations invitations, CancellationToken ct)
    {
        var info = await invitations.PeekAsync(body.Code, ct) ?? throw ApiErrors.NotFound("This is not a valid invitation.");
        return TypedResults.Ok(new InvitationPeekView(info.Role, info.ExpiresAt, info.Valid, info.Valid ? null : StateText(info.State)));
    }

    private static async Task<Ok<JoinedView>> JoinAsync(
        JoinRequest body, HttpContext http, Invitations invitations, UserCatalog users, Sessions sessions, AccountLimits limits, CancellationToken ct)
    {
        if (limits.Join(http) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var (user, recoveryKey) = await invitations.RedeemAsync(body.Code, body.Username, body.Password, ct);

        // The account exists now, whatever happens next: the recovery key must reach the user even if signing in
        // fails (deactivated in the meantime), so that case answers without a cookie rather than with an error.
        // Signing in costs a second Argon2 run, for a user key the service doesn't hand out at creation.
        if (await users.TrySignInAsync(body.Username, body.Password, CancellationToken.None) is var (signedIn, userKey, readAt)
            && await TryStartSessionAsync(sessions, signedIn, userKey, readAt) is { } secret)
        {
            SetSessionCookie(http, secret);
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
        RecoverRequest body, HttpContext http, UserCatalog users, Rekeying rekeying, AccountLimits limits, CancellationToken ct)
    {
        if (limits.Recovery(http) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var (recoveryKey, user, userKey) = await users.RecoverWithKeyAsync(body.Username, body.RecoveryKey, body.NewPassword, ct)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, "The username or recovery key is wrong.");
        rekeying.ResumePending(user, userKey);
        return TypedResults.Ok(new RecoveryKeyView(recoveryKey));
    }

    private static Ok<SessionView> Session(RequestCaller caller) =>
        TypedResults.Ok(new SessionView(UserView.From(caller.User!), caller.User!.PreviousSignInAt));

    private static async Task<NoContent> SignOutAsync(HttpContext http, Sessions sessions, CancellationToken ct)
    {
        await sessions.EndAsync(http.Request.Cookies[CallerMiddleware.SessionCookie]!, ct);
        ClearSessionCookie(http);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<PasswordChangedView>> ChangePasswordAsync(
        ChangePasswordRequest body, HttpContext http, RequestCaller caller, UserCatalog users, AccountLimits limits, CancellationToken ct)
    {
        var user = caller.User!;
        if (limits.PasswordCheck(user.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        if (!await users.ChangePasswordAsync(user, caller.UserKey!, body.OldPassword, body.NewPassword, body.RevokeApiKeys, ct))
        {
            throw ApiErrors.BadRequest("The current password is wrong.");
        }

        ClearSessionCookie(http);
        return TypedResults.Ok(new PasswordChangedView(SignedOut: true));
    }

    private static async Task<Ok<RecoveryKeyView>> RegenerateRecoveryKeyAsync(
        RegenerateRecoveryKeyRequest body, RequestCaller caller, UserCatalog users, AccountLimits limits, CancellationToken ct)
    {
        var user = caller.User!;
        // The same limit as the password change: both are a password check behind a session.
        if (limits.PasswordCheck(user.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var recoveryKey = await users.RegenerateRecoveryKeyAsync(user.Id, caller.UserKey!, body.CurrentPassword, ct)
            ?? throw ApiErrors.BadRequest("The current password is wrong.");
        return TypedResults.Ok(new RecoveryKeyView(recoveryKey));
    }

    /// <summary>
    /// "Re-encrypt my vaults": a new user key, every API key and app revoked, the vaults re-keyed in the background, and
    /// a new recovery key, shown here once. This session stays signed in; every other one ends.
    /// </summary>
    private static async Task<Ok<RecoveryKeyView>> ReencryptAsync(
        ReencryptRequest body, HttpContext http, RequestCaller caller, Rekeying rekeying, AccountLimits limits, CancellationToken ct)
    {
        var user = caller.User!;
        if (limits.PasswordCheck(user.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var recoveryKey = await rekeying.ReencryptAccountAsync(user, caller.UserKey!, body.Password, http.Request.Cookies[CallerMiddleware.SessionCookie]!, ct)
            ?? throw ApiErrors.BadRequest("The password is wrong.");
        return TypedResults.Ok(new RecoveryKeyView(recoveryKey));
    }

    /// <summary>Everything of the account goes, this session too: the cookie is cleared with it.</summary>
    private static async Task<NoContent> DeleteAccountAsync(
        DeleteAccountRequest body, HttpContext http, RequestCaller caller, Deletion deletion, AccountLimits limits, CancellationToken ct)
    {
        var user = caller.User!;
        if (limits.PasswordCheck(user.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        if (!await deletion.DeleteOwnAccountAsync(user, caller.UserKey!, body.Password, body.Confirm, ct)) throw ApiErrors.BadRequest("The password is wrong.");
        ClearSessionCookie(http);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> EndOtherSessionsAsync(HttpContext http, RequestCaller caller, Sessions sessions, CancellationToken ct)
    {
        await sessions.EndAllAsync(caller.User!.Id, http.Request.Cookies[CallerMiddleware.SessionCookie], ct);
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
        var (key, info) = await keys.CreateAsync(caller.User!, caller.UserKey!, caller.CredentialReadAt!.Value, body.Name, vaults, ct, body.ExpiresInDays);
        return TypedResults.Created($"/api/keys/{info.Id}", new CreatedApiKeyView(key, KeyView(info, names)));
    }

    private static async Task<NoContent> RevokeKeyAsync(string id, RequestCaller caller, ApiKeys keys, CancellationToken ct) =>
        await keys.RevokeAsync(caller.User!, id, ct) ? TypedResults.NoContent() : throw ApiErrors.NotFound("You have no such API key that isn't revoked.");

    private static async Task<Ok<AdminUserView[]>> ListUsersAsync(RequestCaller caller, Admin admin, CancellationToken ct) =>
        TypedResults.Ok((await admin.ListUsersAsync(caller.User!, ct))
            .Select(u => new AdminUserView(u.Id, u.Username, u.Display, u.Role, u.Active, u.CreatedAt, u.LastSignInAt, u.Vaults, u.BytesStored))
            .ToArray());

    private static async Task<NoContent> UpdateUserAsync(string id, UpdateUserRequest body, RequestCaller caller, Admin admin, CancellationToken ct)
    {
        if (body is { Active: null, Role: null }) throw ApiErrors.BadRequest("Give 'active', 'role' or both.");
        // A refused request changes nothing. The role goes first, and SetRoleAsync refuses before it writes (an
        // unusable role, the primary admin, no such user, an actor who isn't an admin). The one refusal left to
        // SetActiveAsync after that is deactivating oneself, so it is checked here, before the role is set.
        if (body.Active is false && id == caller.User!.Id) throw new NotPermittedException("You can't deactivate yourself.");
        if (body.Role is { } role && !await admin.SetRoleAsync(caller.User!, id, role, ct)) throw NoSuchUser();
        if (body.Active is { } active && !await admin.SetActiveAsync(caller.User!, id, active, ct)) throw NoSuchUser();
        return TypedResults.NoContent();

        static ApiException NoSuchUser() => ApiErrors.NotFound("There is no such user.");
    }

    /// <summary>An admin deletes another user, active or not, in one step, and gets the receipt for their records.</summary>
    private static async Task<Ok<DeletionReceiptView>> DeleteUserAsync(
        string id, DeleteUserRequest body, RequestCaller caller, Deletion deletion, AccountLimits limits, SearchIndex index,
        IOptions<KumbuOptions> options, CancellationToken ct)
    {
        if (limits.PasswordCheck(caller.User!.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var deleted = await deletion.DeleteUserAsync(caller.User!, caller.UserKey!, body.Password, id, body.Confirm, ct)
            ?? throw ApiErrors.NotFound("There is no such user.");
        return TypedResults.Ok(ReceiptView(deleted, index, options.Value));
    }

    internal static DeletionReceiptView ReceiptView(DeletedUser deleted, SearchIndex index, KumbuOptions options) => new(
        deleted.Id, deleted.Username, deleted.DeletedBy, deleted.DeletedAt, deleted.Summary.Vaults, deleted.Summary.Notes, deleted.Summary.ApiKeys,
        deleted.Summary.Connections, index.Providers.All.Where(p => p.External).Select(p => p.Label).ToArray(),
        string.IsNullOrWhiteSpace(options.Privacy.Contact) ? null : options.Privacy.Contact.Trim(), deleted.Listed);

    private static async Task<Ok<AdminInvitationView[]>> ListInvitationsAsync(RequestCaller caller, Invitations invitations, CancellationToken ct) =>
        TypedResults.Ok((await invitations.ListAsync(caller.User!, ct)).Select(InvitationView).ToArray());

    private static async Task<Created<CreatedInvitationView>> CreateInvitationAsync(
        CreateInvitationRequest body, RequestCaller caller, Invitations invitations, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var expiry = body.ExpiresInDays is { } days
            ? days > 0 && days <= Invitations.MaxExpiry.TotalDays ? TimeSpan.FromDays(days) : throw ApiErrors.BadRequest($"An invitation is valid for 1 to {Invitations.MaxExpiry.TotalDays} days.")
            : (TimeSpan?)null;
        var (code, info) = await invitations.CreateAsync(caller.User!, body.Role, body.Note, expiry, ct);
        var link = Invitations.Link(options.Value.PublicUrl, code);
        return TypedResults.Created($"/api/admin/invitations/{info.Id}", new CreatedInvitationView(code, link, InvitationView(info)));
    }

    private static async Task<NoContent> RevokeInvitationAsync(string id, RequestCaller caller, Invitations invitations, CancellationToken ct) =>
        await invitations.RevokeAsync(caller.User!, id, ct) ? TypedResults.NoContent() : throw ApiErrors.NotFound("There is no such open invitation.");

    /// <summary>A request's rights (vault name → <c>read</c>/<c>write</c>) by vault id; 400 for a vault the caller
    /// has no such name for or another right.</summary>
    internal static Dictionary<string, KeyRight> RightsByVaultId(IReadOnlyDictionary<string, string> requested, Dictionary<string, string> names)
    {
        var idsByName = names.ToDictionary(n => n.Value, n => n.Key, StringComparer.Ordinal);
        var vaults = new Dictionary<string, KeyRight>();
        foreach (var (name, rightText) in requested)
        {
            var id = idsByName.GetValueOrDefault(name) ?? throw ApiErrors.BadRequest($"You have no vault named '{name}'.");
            vaults[id] = KeyRights.Parse(rightText) ?? throw ApiErrors.BadRequest($"A right is 'read' or 'write', not '{rightText}'.");
        }

        return vaults;
    }

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
        info.ExpiresAt);

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

    private static void SetSessionCookie(HttpContext http, string secret)
    {
        var options = SessionCookieOptions();
        // The browser drops it when the session would end anyway; the server checks both limits regardless.
        options.MaxAge = Sessions.AbsoluteTimeout;
        http.Response.Cookies.Append(CallerMiddleware.SessionCookie, secret, options);
    }

    private static void ClearSessionCookie(HttpContext http) => http.Response.Cookies.Delete(CallerMiddleware.SessionCookie, SessionCookieOptions());
}
