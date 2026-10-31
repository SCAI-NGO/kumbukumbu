// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
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
public sealed record PeekInvitationRequest(string Secret, string? Alias = null);

/// <summary>An invitation before it is accepted: <c>owner</c> is the owner's display name, sanitised and quoted, since
/// it is free text one user typed and another reads; <c>ownerUsername</c> is hers, beside it.</summary>
public sealed record InvitationPeekView(
    string Vault, string Owner, string OwnerUsername, string Role, string EmbeddingProvider, string Alias, bool AliasFree, bool Confirm, DateTime ExpiresAt);

/// <summary>Sharing a vault (collab design §4): invitations and memberships.</summary>
public static class SharingApi
{
    public static void MapSharingApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter);
        // Not /api/invitations/…, as the design writes it: that is the account invitations' (the join page's).
        api.MapPost("/vault-invitations/peek", PeekAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 403, 404, 409, 429);
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
        if (limits.PasswordCheck(caller.User!.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        if (string.IsNullOrEmpty(body.Password) || !await users.CheckPasswordAsync(caller.User!.Id, caller.UserKey!, body.Password, ct))
        {
            throw new WrongPasswordException("The password is wrong.");
        }

        await AccountApi.PasswordGivenAsync(http, sessions, ct);
        var vaultId = await catalog.FindForUserAsync(caller.User!.Id, caller.UserKey!, vault, ct) ?? throw ApiErrors.NotFound($"You have no vault named '{vault}'.");
        var invite = await memberships.InviteAsync(caller.User!.Id, caller.UserKey!, vaultId, body.Label, role, TimeSpan.FromDays(days), body.Confirm, ct);
        return TypedResults.Created(
            (string?)null,
            new CreatedVaultInvitationView(invite.MembershipId, Memberships.ShareLink(options.Value.PublicUrl, invite.Secret), MemberRoles.ToText(role), body.Confirm, invite.ExpiresAt));
    }

    /// <summary>
    /// What the share page shows before accepting. Every answer for a link that isn't valid is the same 404; the
    /// requests are limited per account and per network only (<see cref="AccountLimits.InvitePeek"/>).
    /// </summary>
    private static async Task<Ok<InvitationPeekView>> PeekAsync(
        PeekInvitationRequest body, HttpContext http, RequestCaller caller, AccountLimits limits, Memberships memberships, CancellationToken ct)
    {
        if (limits.InvitePeek(http, caller.User!.Id) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var peek = await memberships.PeekAsync(caller.User!.Id, caller.UserKey!, body.Secret, body.Alias, ct);
        return TypedResults.Ok(new InvitationPeekView(
            peek.Vault, WriterLabels.Quoted(peek.OwnerDisplay), peek.OwnerUsername, MemberRoles.ToText(peek.Role), peek.EmbeddingProvider, peek.Alias, peek.AliasFree,
            peek.Confirm, peek.ExpiresAt));
    }
}
