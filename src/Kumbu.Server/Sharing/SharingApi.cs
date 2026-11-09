// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Sync;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Sharing;

/// <summary>An invitation to a vault: the owner's <c>password</c>; <c>label</c>, her name for the person (1 to 64
/// characters); <c>role</c> <c>read</c>, <c>write</c> or <c>maintain</c>; <c>expiresInDays</c> 1 to 30 (default 7);
/// with <c>confirm</c> the owner confirms whoever redeems it.</summary>
public sealed record CreateVaultInvitationRequest(string Password, string Label, string Role, int? ExpiresInDays = null, bool Confirm = false);

/// <summary><c>link</c> holds the secret in its fragment and is shown this once; the server keeps neither.</summary>
public sealed record CreatedVaultInvitationView(string Id, string Link, string Role, bool Confirm, DateTime ExpiresAt);

/// <summary><c>secret</c>: what follows <c>#</c> in the link; <c>alias</c>, the name the caller would give the vault
/// (default: the vault's own name), to check whether it is free.</summary>
public sealed record VaultInvitationPeekRequest(string Secret, string? Alias = null);

/// <summary>An invitation before it is accepted: <c>owner</c> is the owner's display name, sanitised and quoted, since
/// it is free text one user typed and another reads; <c>ownerUsername</c> is hers, beside it.</summary>
public sealed record VaultInvitationPeekView(
    string Vault, string Owner, string OwnerUsername, string Role, string EmbeddingProvider, string Alias, bool AliasFree, bool Confirm, DateTime ExpiresAt);

/// <summary><c>secret</c>: what follows <c>#</c> in the link; <c>alias</c>: the caller's name for the vault, a vault
/// name of hers that she doesn't have yet.</summary>
public sealed record AcceptInvitationRequest(string Secret, string Alias);

/// <summary>The membership, the vault's name for the caller (the alias), the role, and <c>status</c>: <c>active</c>, or
/// <c>pending</c> while the owner hasn't confirmed.</summary>
public sealed record AcceptedInvitationView(string Id, string Vault, string Role, string Status);

/// <summary>Who redeemed an invitation, from their user document: <c>name</c>, their display name sanitised and quoted,
/// and <c>username</c> beside it.</summary>
public sealed record RedeemerView(string Name, string Username);

/// <summary>A membership of the owner's vault as she sees it: her <c>label</c> for the person, and with
/// <c>redeemer</c> who redeemed it, for "Bob Example (username bob) accepted Bob's invitation — grant access?".</summary>
public sealed record MembershipView(
    string Id, string Label, string Role, string Status, bool Confirm, DateTime? ExpiresAt, DateTime? RedeemedAt, RedeemerView? Redeemer);

/// <summary>A member's new role: <c>read</c>, <c>write</c> or <c>maintain</c>; raising it to <c>maintain</c> needs the
/// owner's <c>password</c>.</summary>
public sealed record ChangeRoleRequest(string Role, string? Password = null);

/// <summary><c>provider</c>: the embedding provider the member OKs, as the vault's <c>embeddings</c> named it.</summary>
public sealed record ProviderSeenRequest(string Provider);

/// <summary><c>alias</c>: the member's new name for a vault shared with her, a vault name she doesn't have yet.</summary>
public sealed record RenameAliasRequest(string Alias);

/// <summary>A notice to the caller of a vault shared with her that is gone (collab design §7.3): <c>vault</c>, her name
/// for it, or null where it can't be read any more; <c>text</c>, "The owner has deleted gartenarbeit.".</summary>
public sealed record MemberNoticeView(string Id, string Kind, DateTime At, string? Vault, string Text);

/// <summary>Sharing a vault (collab design §4): invitations and memberships.</summary>
public static class SharingApi
{
    public static void MapSharingApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter);
        // Not /api/invitations/…, as the design writes it: that is the account invitations' (the join page's).
        api.MapPost("/vault-invitations/peek", PeekAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 403, 404, 409, 429);
        api.MapPost("/vault-invitations/accept", AcceptAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 404, 409, 429);
        api.MapGet("/vaults/{vault}/memberships/{id}", MembershipAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(401, 403, 404, 503);
        api.MapPost("/vaults/{vault}/memberships/{id}/confirm", ConfirmAsync)
            .WithMetadata(SessionOnlyMetadata.Instance, PasswordWindowMetadata.Instance).Errors(401, 403, 404, 409, 503);
        api.MapPost("/vaults/{vault}/memberships/{id}/decline", DeclineAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(401, 403, 404);
        api.MapDelete("/vaults/{vault}/memberships/{id}", WithdrawAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(401, 403, 404);
        api.MapPost("/vaults/{vault}/memberships/{id}/revoke", RevokeAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(401, 403, 404);
        api.MapPut("/vaults/{vault}/memberships/{id}/role", ChangeRoleAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 404, 429, 503);
        api.MapGet("/vault-events", EventsAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(401, 403);
        api.MapPost("/memberships/{id}/leave", LeaveAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(401, 403, 404);
        api.MapPut("/memberships/{id}/alias", RenameAliasAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 404, 409, 429, 503);
        api.MapPost("/memberships/{id}/provider-seen", ProviderSeenAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 404, 503);
        api.MapGet("/member-notices", NoticesAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(401, 403);
        api.MapDelete("/member-notices/{id}", DismissNoticeAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(401, 403, 404);
        api.MapPost("/vaults/{vault}/invitations", CreateAsync).WithMetadata(SessionOnlyMetadata.Instance, PasswordWindowMetadata.Instance).Errors(400, 401, 403, 404, 409, 429, 503);
    }

    /// <summary>
    /// The owner invites someone to her vault: her password in the body, checked before the vault is looked up (as a
    /// vault's deletion), and the password window, since an invitation is a new way into the vault. Only the vault's
    /// owner finds it by name: a member's alias answers 404.
    /// </summary>
    private static async Task<Created<CreatedVaultInvitationView>> CreateAsync(
        string vault, CreateVaultInvitationRequest body, HttpContext http, RequestCaller caller, UserCatalog users, Sessions sessions, AccountLimits limits,
        VaultCatalog catalog, Memberships memberships, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var role = MemberRoles.Parse(body.Role) ?? throw ApiErrors.BadRequest($"A role is 'read', 'write' or 'maintain', not '{body.Role}'.");
        var days = body.ExpiresInDays ?? (int)Memberships.DefaultInviteExpiry.TotalDays;
        if (days < 1 || days > Memberships.MaxInviteExpiry.TotalDays) throw ApiErrors.BadRequest($"An invitation is valid for 1 to {Memberships.MaxInviteExpiry.TotalDays} days.");
        // The same budget as the other password checks behind a session.
        if (limits.PasswordCheck(caller.User!.Username) is { } retryAfter) throw ApiErrors.TooManyPasswordChecks(retryAfter);
        if (string.IsNullOrEmpty(body.Password) || !await users.CheckPasswordAsync(caller.User!.Id, caller.UserKey!, body.Password, ct))
        {
            throw new WrongPasswordException("The password is wrong.");
        }

        await AccountApi.PasswordGivenAsync(http, sessions, ct);
        var vaultId = await OwnVaultIdAsync(vault, caller, catalog, ct);
        if ((await caller.OpenVaultsAsync(ct)).Any(v => v.Id == vaultId && v.IsSkillsVault)) throw new ApiException(StatusCodes.Status409Conflict, SharingRefusedException.SkillsVault().Message);
        var invite = await memberships.InviteAsync(caller.User!.Id, caller.UserKey!, vaultId, body.Label, role, TimeSpan.FromDays(days), body.Confirm, ct);
        return TypedResults.Created(
            (string?)null,
            new CreatedVaultInvitationView(invite.MembershipId, Memberships.ShareLink(options.Value.PublicUrl, invite.Secret), MemberRoles.ToText(role), body.Confirm, invite.ExpiresAt));
    }

    /// <summary>
    /// What the share page shows before accepting. Every answer for a link that isn't valid is the same 404; the
    /// requests are limited per account and per network only (<see cref="AccountLimits.InvitePeek"/>).
    /// </summary>
    private static async Task<Ok<VaultInvitationPeekView>> PeekAsync(
        VaultInvitationPeekRequest body, HttpContext http, RequestCaller caller, AccountLimits limits, Memberships memberships, CancellationToken ct)
    {
        if (limits.InvitePeek(http, caller.User!.Id) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var peek = await memberships.PeekAsync(caller.User!.Id, caller.UserKey!, body.Secret, body.Alias, ct);
        // A name the session hides is free to the peek: only accepting meets the clash (Decision 14).
        var aliasFree = peek.AliasFree || await HiddenNames.ClashesWithHiddenAsync(caller, peek.Alias, ct);
        return TypedResults.Ok(new VaultInvitationPeekView(
            peek.Vault, WriterLabels.Quoted(peek.OwnerDisplay), peek.OwnerUsername, MemberRoles.ToText(peek.Role), peek.EmbeddingProvider, peek.Alias, aliasFree,
            peek.Confirm, peek.ExpiresAt));
    }

    /// <summary>
    /// The caller accepts an invitation under her alias for the vault. A refusal leaves the link as it was, so she can
    /// try another alias; limited as peek is.
    /// </summary>
    private static async Task<Ok<AcceptedInvitationView>> AcceptAsync(
        AcceptInvitationRequest body, HttpContext http, RequestCaller caller, AccountLimits limits, Memberships memberships, CancellationToken ct)
    {
        if (limits.InvitePeek(http, caller.User!.Id) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        // An alias follows the rules of a vault name: it is one, to the member.
        var alias = VaultNames.Check(body.Alias);
        var hidden = await caller.HiddenIdsAsync(ct);
        AcceptedInvite accepted;
        try
        {
            accepted = await memberships.AcceptAsync(caller.User!.Id, caller.UserKey!, body.Secret, alias, ct, hidden);
        }
        catch (VaultNameTakenException)
        {
            if (await HiddenNames.ClashesWithHiddenAsync(caller, alias, ct)) throw HiddenNames.Refusal();
            throw;
        }

        return TypedResults.Ok(new AcceptedInvitationView(accepted.MembershipId, accepted.Alias, MemberRoles.ToText(accepted.Role), accepted.Status));
    }

    /// <summary>The id of the caller's own vault named <paramref name="vault"/>, from her user slot: a member's alias, a
    /// vault of someone else's, or one her session hides answers 404 (collab design §6.0 binds every write to this id).</summary>
    private static async Task<string> OwnVaultIdAsync(string vault, RequestCaller caller, VaultCatalog catalog, CancellationToken ct) =>
        await catalog.FindForUserAsync(caller.User!.Id, caller.UserKey!, vault, ct) is { } vaultId && !await caller.HidesAsync(vaultId, ct)
            ? vaultId
            : throw ApiErrors.NotFound($"You have no vault named '{vault}'.");

    private static MembershipView View(OwnersMembership m) => new(
        m.Id, m.Label, MemberRoles.ToText(m.Role), m.Status, m.Confirm, m.ExpiresAt, m.RedeemedAt,
        m.RedeemerUsername is { } username ? new RedeemerView(WriterLabels.Quoted(m.RedeemerDisplay ?? ""), username) : null);

    private static async Task<Ok<MembershipView>> MembershipAsync(string vault, string id, RequestCaller caller, VaultCatalog catalog, Memberships memberships, CancellationToken ct) =>
        TypedResults.Ok(View(await memberships.OwnersViewAsync(caller.User!.Id, caller.UserKey!, await OwnVaultIdAsync(vault, caller, catalog, ct), id, ct)));

    /// <summary>The owner grants a redeemer access: behind the password window, as it gives someone the vault (collab
    /// design §4.2, §13 M7).</summary>
    private static async Task<Ok<MembershipView>> ConfirmAsync(string vault, string id, RequestCaller caller, VaultCatalog catalog, Memberships memberships, CancellationToken ct) =>
        TypedResults.Ok(View(await memberships.ConfirmAsync(caller.User!.Id, caller.UserKey!, await OwnVaultIdAsync(vault, caller, catalog, ct), id, ct)));

    /// <summary>Declining needs no password window: the owner can always reduce access at once.</summary>
    private static async Task<NoContent> DeclineAsync(string vault, string id, RequestCaller caller, VaultCatalog catalog, Memberships memberships, CancellationToken ct)
    {
        await memberships.DeclineAsync(await OwnVaultIdAsync(vault, caller, catalog, ct), id, ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The owner revokes a member (collab design §6.1, §6.2), without the password window: reducing access is always
    /// open. The atomic step ends every access of the member's at once; the sync hub drops the vault from her sockets
    /// before the answer; then the cleanup, which the sweep finishes if it fails here.
    /// </summary>
    private static async Task<NoContent> RevokeAsync(
        string vault, string id, RequestCaller caller, VaultCatalog catalog, Memberships memberships, SyncHub hub, CancellationToken ct)
    {
        await EndedAsync(await memberships.RevokeAsync(await OwnVaultIdAsync(vault, caller, catalog, ct), id, ct), memberships, hub);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The owner changes a member's role (collab design §6.3). Guarded inline: raising it to <c>write</c> needs the
    /// password window, since it widens what the member's credentials reach; raising it to <c>maintain</c> needs the
    /// owner's password in the body each time, window open or not, checked before the vault is looked up as an
    /// invitation's is, and opening the window; lowering never needs either. The hub is told as for a revocation.
    /// </summary>
    private static async Task<Ok<MembershipView>> ChangeRoleAsync(
        string vault, string id, ChangeRoleRequest body, HttpContext http, RequestCaller caller, UserCatalog users, Sessions sessions, AccountLimits limits,
        VaultCatalog catalog, Memberships memberships, SyncHub hub, CancellationToken ct)
    {
        var role = MemberRoles.Parse(body.Role) ?? throw ApiErrors.BadRequest($"A role is 'read', 'write' or 'maintain', not '{body.Role}'.");
        var passwordGiven = false;
        if (role == MemberRole.Maintain && !string.IsNullOrEmpty(body.Password))
        {
            // The same budget as the other password checks behind a session.
            if (limits.PasswordCheck(caller.User!.Username) is { } retryAfter) throw ApiErrors.TooManyPasswordChecks(retryAfter);
            if (!await users.CheckPasswordAsync(caller.User!.Id, caller.UserKey!, body.Password, ct)) throw new WrongPasswordException("The password is wrong.");
            await AccountApi.PasswordGivenAsync(http, sessions, ct);
            passwordGiven = true;
        }

        var vaultId = await OwnVaultIdAsync(vault, caller, catalog, ct);
        // Decided on the role the membership has in the transaction, so a change in between can't skip the password.
        var changed = await memberships.ChangeRoleAsync(
            caller.User!.Id, caller.UserKey!, vaultId, id, role,
            () =>
            {
                if (role != MemberRole.Maintain) Security.RequirePasswordWindow(caller);
                else if (!passwordGiven) throw ApiErrors.BadRequest("Raising a member to maintain needs your password.");
            },
            ct);
        try
        {
            // Past the commit: an aborted request must not skip it.
            await hub.MembershipChangedAsync(vaultId, changed.MemberId!, id, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            memberships.Log.LogWarning(e, "Telling the sync hub of the role change of membership {MembershipId} failed", id);
        }

        return TypedResults.Ok(View(changed));
    }

    /// <summary>
    /// The caller's own events as a member, across the vaults shared with her (collab design §8.3): her role changes,
    /// her removal and her leaving, also of a vault she no longer reaches, and the vaults' own events while she is a
    /// member. Nothing of another member's.
    /// </summary>
    private static async Task<Ok<VaultEventView[]>> EventsAsync(
        RequestCaller caller, Vaults.EncryptedNoteStore store, Memberships memberships, MongoDB.Driver.IMongoDatabase db, CancellationToken ct)
    {
        var aliases = (await store.UnlockedVaultsAsync(ct)).Where(a => a.Membership is not null).ToDictionary(a => a.Id, a => a.Name, StringComparer.Ordinal);
        var events = await VaultEvents.OfMemberEverywhereAsync(db, caller.User!.Id, aliases, await caller.HiddenIdsAsync(ct), memberships.DisplayNameAsync, ct);
        return TypedResults.Ok(events.Select(e => new VaultEventView(e.Event.Kind, e.Event.At, e.Text, e.Event.Role, e.Vault)).ToArray());
    }

    /// <summary>The member leaves a vault shared with her: as a revocation, bound to her as the member.</summary>
    private static async Task<NoContent> LeaveAsync(string id, RequestCaller caller, Memberships memberships, SyncHub hub, CancellationToken ct)
    {
        // A vault her session hides answers as a membership that isn't hers.
        if (await caller.HidesMembershipAsync(id, ct)) throw new MembershipNotFoundException();
        await EndedAsync(await memberships.LeaveAsync(caller.User!.Id, id, ct), memberships, hub);
        return TypedResults.NoContent();
    }

    /// <summary>The member renames a vault shared with her (collab design §2.3, §10.1): her name for it, in her own
    /// namespace, so it widens nothing and needs no password window. Bound to her as the member; a name she has for an
    /// own vault or another alias answers 409 and the old alias stays. Limited per user
    /// (<see cref="AccountLimits.AliasRenamesPerUser"/>), and 503 while the vault is re-keyed.</summary>
    private static async Task<NoContent> RenameAliasAsync(
        string id, RenameAliasRequest body, RequestCaller caller, AccountLimits limits, VaultCatalog catalog, SyncHub hub, CancellationToken ct)
    {
        if (limits.AliasRename(caller.User!.Id) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var alias = VaultNames.Check(body.Alias);
        if (await caller.HidesMembershipAsync(id, ct)) throw new MembershipNotFoundException();
        try
        {
            alias = await catalog.RenameAliasAsync(caller.User!.Id, caller.UserKey!, id, alias, ct);
        }
        catch (ArgumentException)
        {
            throw new MembershipNotFoundException();
        }
        catch (VaultNameTakenException)
        {
            if (await HiddenNames.ClashesWithHiddenAsync(caller, alias, ct)) throw HiddenNames.Refusal();
            throw;
        }

        // Her sockets' hints and presence name the vault as she does now.
        hub.AliasRenamed(caller.User.Id, id, alias);

        return TypedResults.NoContent();
    }

    /// <summary>The member's "OK" on the banner of a changed embedding provider (collab design §8.5): bound to her as the
    /// member, as leaving is. Her own consent, for her own searches: nothing of the vault changes.</summary>
    private static async Task<NoContent> ProviderSeenAsync(
        string id, ProviderSeenRequest body, RequestCaller caller, Memberships memberships, Search.SearchIndex index, CancellationToken ct)
    {
        if (index.Providers.Get(body.Provider) is null) throw ApiErrors.BadRequest($"This server has no embedding provider '{body.Provider}'.");
        if (await caller.HidesMembershipAsync(id, ct)) throw new MembershipNotFoundException();
        await memberships.ProviderSeenAsync(caller.User!.Id, id, body.Provider, ct);
        return TypedResults.NoContent();
    }

    /// <summary>After the atomic step, which has committed: the hub, then the cleanup. Neither undoes the step, so the
    /// request answers success either way; what fails is logged, and the sweep retries the cleanup and tells the hub
    /// again. Both run whatever happens to the request: a client gone after the commit must not keep them from running.</summary>
    private static async Task EndedAsync(EndedMembership ended, Memberships memberships, SyncHub hub)
    {
        try
        {
            await hub.MembershipChangedAsync(ended.Vault, ended.Member, ended.Id, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            memberships.Log.LogWarning(e, "Telling the sync hub that membership {MembershipId} ended failed; her sockets get hints for the vault until they reconnect", ended.Id);
        }

        try
        {
            await memberships.CleanupAsync(ended.Id, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            memberships.Log.LogWarning(e, "The cleanup of membership {MembershipId} failed; the sweep finishes it", ended.Id);
        }
    }

    /// <summary>The caller's notices of shared vaults, newest first, each naming the vault by her alias, opened with her
    /// user key. One of a vault deleted for everyone never names the owner; one of a re-encryption is listed only while
    /// her membership is active, and names the owner as she is now, read through it: the notice itself names nobody.
    /// None of a vault her session hides, also once it is gone.</summary>
    private static async Task<Ok<MemberNoticeView[]>> NoticesAsync(
        RequestCaller caller, Memberships memberships, MongoDB.Driver.IMongoDatabase db, TimeProvider time, CancellationToken ct)
    {
        await memberships.EnsureReadyAsync(ct);
        var hidden = await caller.HiddenIdsAsync(ct);
        var notices = await MemberNotices.ListAsync(db, caller.User!.Id, caller.UserKey!, time.GetUtcNow().UtcDateTime, hidden, ct);
        var active = await MemberNotices.ActiveOwnersAsync(
            db, caller.User.Id, [.. notices.Where(n => n.Notice.Kind == MemberNoticeDocument.VaultRekeyed).Select(n => n.Notice.Membership)], ct);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<MemberNoticeView>();
        foreach (var (notice, alias) in notices)
        {
            var owner = "";
            if (notice.Kind == MemberNoticeDocument.VaultRekeyed)
            {
                // Once she has left, was removed, or the vault is gone, "give your agents it again" is wrong.
                if (!active.TryGetValue(notice.Membership, out var ownerId)) continue;
                if (!owners.TryGetValue(ownerId, out owner)) owners[ownerId] = owner = WriterLabels.Owner(await memberships.DisplayNameAsync(ownerId, ct));
            }

            result.Add(new MemberNoticeView(notice.Id.ToString(), notice.Kind, notice.At, alias, MemberNotices.TextFor(notice.Kind, alias, owner)));
        }

        return TypedResults.Ok(result.ToArray());
    }

    /// <summary>Dismissing one of her own notices; any other id answers 404, as does one of a vault her session hides.</summary>
    private static async Task<NoContent> DismissNoticeAsync(string id, RequestCaller caller, MongoDB.Driver.IMongoDatabase db, CancellationToken ct) =>
        await MemberNotices.DismissAsync(db, caller.User!.Id, id, await caller.HiddenIdsAsync(ct), ct)
            ? TypedResults.NoContent() : throw ApiErrors.NotFound("You have no such notice.");

    /// <summary>Withdrawing an open invitation needs no password window either.</summary>
    private static async Task<NoContent> WithdrawAsync(string vault, string id, RequestCaller caller, VaultCatalog catalog, Memberships memberships, CancellationToken ct)
    {
        await memberships.WithdrawAsync(await OwnVaultIdAsync(vault, caller, catalog, ct), id, ct);
        return TypedResults.NoContent();
    }
}
