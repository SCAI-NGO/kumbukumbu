// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using ConnectionInfo = Kumbu.Server.Accounts.ConnectionInfo;

namespace Kumbu.Server.Api;

/// <summary>The app a connection was authorised for; <c>verified</c> is false for a self-registered one.</summary>
public sealed record ClientView(string Name, string Host, bool Verified);

/// <summary>
/// A connection as its owner sees it: <c>url</c> is its connector URL, to paste into the app; vault names with
/// <c>read</c> or <c>write</c>. <c>state</c> is <c>waiting</c>, <c>connected</c>, <c>needs_reconnect</c>,
/// <c>revoked</c> or <c>expired</c>; <c>reason</c> (e.g. <c>rights_changed</c>) goes with the last three;
/// <c>client</c> is null until an app was authorised.
/// </summary>
public sealed record ConnectionView(
    string Id, string Name, string Url, IReadOnlyDictionary<string, string> Vaults, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? ExpiresAt, DateTime? RevokedAt,
    string State, string? Reason, ClientView? Client);

/// <summary><c>vaults</c>: vault name → <c>read</c> or <c>write</c>. <c>expiresInDays</c>: 30, 90 or 365; left
/// out or null, the connection doesn't expire.</summary>
public sealed record CreateConnectionRequest(string Name, IReadOnlyDictionary<string, string> Vaults, int? ExpiresInDays = null);

/// <summary><c>vaults</c>: vault name → <c>read</c> or <c>write</c>, replacing the connection's rights; the app
/// has to reconnect afterwards.</summary>
public sealed record SetConnectionVaultsRequest(IReadOnlyDictionary<string, string> Vaults);

/// <summary>The connections endpoints under <c>/api</c>, for a signed-in session only (<see cref="SessionOnlyMetadata"/>):
/// an API key or a connection's token must not make or change credentials.</summary>
public static class ConnectionApi
{
    private const string NoSuchConnection = "You have no such connection that is neither revoked nor expired.";

    public static void MapConnectionApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/connections").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        group.MapGet("", ListAsync).Errors(401, 403);
        group.MapPost("", CreateAsync).Errors(400, 401, 403, 409);
        group.MapPut("/{id}/vaults", SetVaultsAsync).Errors(400, 401, 403, 404);
        group.MapDelete("/{id}", RevokeAsync).Errors(401, 403, 404);
    }

    private static async Task<Ok<ConnectionView[]>> ListAsync(HttpContext http, RequestCaller caller, Connections connections, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var names = await AccountApi.VaultNamesAsync(caller, ct);
        var list = await connections.ListAsync(caller.User!, caller.UserKey!, ct);
        var origin = Origin(http, options.Value);
        return TypedResults.Ok(list.Select(c => View(c, names, origin)).ToArray());
    }

    private static async Task<Created<ConnectionView>> CreateAsync(
        CreateConnectionRequest body, HttpContext http, RequestCaller caller, Connections connections, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var names = await AccountApi.VaultNamesAsync(caller, ct);
        var vaults = AccountApi.RightsByVaultId(body.Vaults, names);
        var info = await connections.CreateAsync(caller.User!, caller.UserKey!, caller.CredentialReadAt!.Value, body.Name, vaults, ct, body.ExpiresInDays);
        return TypedResults.Created($"/api/connections/{info.Id}", View(info, names, Origin(http, options.Value)));
    }

    private static async Task<Ok<ConnectionView>> SetVaultsAsync(
        string id, SetConnectionVaultsRequest body, HttpContext http, RequestCaller caller, Connections connections, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var names = await AccountApi.VaultNamesAsync(caller, ct);
        var vaults = AccountApi.RightsByVaultId(body.Vaults, names);
        var info = await connections.SetVaultsAsync(caller.User!, caller.UserKey!, id, vaults, ct) ?? throw ApiErrors.NotFound(NoSuchConnection);
        return TypedResults.Ok(View(info, names, Origin(http, options.Value)));
    }

    private static async Task<NoContent> RevokeAsync(string id, RequestCaller caller, Connections connections, CancellationToken ct) =>
        await connections.RevokeAsync(caller.User!, id, ct) ? TypedResults.NoContent() : throw ApiErrors.NotFound("You have no such connection that isn't revoked.");

    /// <summary>The public URL if set (behind a proxy the request's own host may be an internal one), else the request's origin.</summary>
    private static string Origin(HttpContext http, KumbuOptions options) =>
        options.PublicUrl is { Length: > 0 } url ? url.TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}";

    /// <summary>A vault the connection was given but that no longer opens for the user isn't listed: it has no name to show.</summary>
    private static ConnectionView View(ConnectionInfo info, Dictionary<string, string> names, string origin) => new(
        info.Id,
        info.Name,
        $"{origin}/mcp/c/{info.Id}",
        info.Vaults.Where(v => names.ContainsKey(v.Key)).ToDictionary(v => names[v.Key], v => KeyRights.ToText(v.Value)),
        info.CreatedAt,
        info.LastUsedAt,
        info.ExpiresAt,
        info.RevokedAt,
        StateText(info.State),
        info.Reason,
        info.Client is { } client ? new ClientView(client.Name, client.Host, client.Verified) : null);

    private static string StateText(ConnectionState state) => state switch
    {
        ConnectionState.Waiting => "waiting",
        ConnectionState.Connected => "connected",
        ConnectionState.NeedsReconnect => "needs_reconnect",
        ConnectionState.Revoked => "revoked",
        ConnectionState.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
