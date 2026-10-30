// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.OAuth;
using Kumbu.Server.Vaults;
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

/// <summary>
/// What the consent page shows for a pending authorisation. <c>client</c>: the app asking; <c>redirectHost</c>: where
/// the browser returns. <c>connection</c>: the connection its URL names, with the rights this grant gets (already
/// capped to read if the app asked for read only); null when there is a <c>problem</c> that hides it.
/// <c>renewal</c>: the connection was authorised before. <c>problem</c>: null, <c>not_a_connection</c> (the app was
/// given an address that is no connection's URL), <c>other_user</c> (the connection is another account's),
/// <c>revoked</c> or <c>expired</c>; with a problem only Deny is possible.
/// </summary>
public sealed record ConsentView(string RequestId, ClientView Client, string RedirectHost, ConsentConnectionView? Connection, bool Renewal, string? Problem);

/// <summary>A connection on the consent page: its name, vault name → <c>read</c> or <c>write</c>, its expiry, and the app
/// it is authorised for now, if any.</summary>
public sealed record ConsentConnectionView(string Name, IReadOnlyDictionary<string, string> Vaults, DateTime? ExpiresAt, ClientView? CurrentClient);

/// <summary><c>allow</c>: the user's Allow (true) or Deny (false).</summary>
public sealed record ConsentDecisionRequest(bool Allow);

/// <summary>Where the consent page sends the browser: back to the app, with a code or an error.</summary>
public sealed record ConsentDecisionView(string Redirect);

/// <summary>The connections endpoints under <c>/api</c>, for a signed-in session only (<see cref="SessionOnlyMetadata"/>):
/// an API key or a connection's token must not make or change credentials.</summary>
public static class ConnectionApi
{
    private const string NoSuchConnection = "You have no such connection that is neither revoked nor expired.";

    public static void MapConnectionApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/connections").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        group.MapGet("", ListAsync).Errors(401, 403, 503);
        group.MapPost("", CreateAsync).WithMetadata(PasswordWindowMetadata.Instance).Errors(400, 401, 403, 409, 503);
        group.MapPut("/{id}/vaults", SetVaultsAsync).WithMetadata(PasswordWindowMetadata.Instance).Errors(400, 401, 403, 404, 503);
        group.MapDelete("/{id}", RevokeAsync).Errors(401, 403, 404);

        // The consent page's calls (Ruling 8): session-only too, so the decision, a POST, also passes the Origin check
        // (Security.UseCallerRules), which is its CSRF guard.
        var consent = app.MapGroup("/api/oauth/requests").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        consent.MapGet("/{id}", ConsentAsync).Errors(401, 403, 404, 503);
        consent.MapPost("/{id}/decision", DecideAsync).Errors(400, 401, 403, 404, 409, 503);
    }

    private const string RequestExpired = "This request has expired; start the connection again in the app.";
    private const string ClientGone = "The app could not be identified any more; start the connection again in the app.";

    private static async Task<Ok<ConsentView>> ConsentAsync(
        string id, RequestCaller caller, AuthorizationRequests requests, OAuthClients clients, Connections connections, TimeProvider time, CancellationToken ct)
    {
        var request = await requests.GetAsync(id, ct) ?? throw ApiErrors.NotFound(RequestExpired);
        var client = await clients.ResolveAsync(request.ClientId, ct) ?? throw ApiErrors.NotFound(ClientGone);
        var (problem, connection, renewal) = await ConsentConnectionAsync(request, caller, connections, time, ct);
        return TypedResults.Ok(new ConsentView(request.Id, new ClientView(client.Name, client.Host, client.Verified), new Uri(request.RedirectUri).Host, connection, renewal, problem));
    }

    /// <summary>
    /// The decision, which takes the request (single use): Deny always redirects with <c>access_denied</c>
    /// (<c>invalid_target</c> for an address that is no connection's, Ruling 7); Allow with a problem is a 409 and
    /// leaves the request, so the page can still offer the way back; Allow issues the code for the signed-in user.
    /// </summary>
    private static async Task<Ok<ConsentDecisionView>> DecideAsync(
        string id, ConsentDecisionRequest body, HttpContext http, RequestCaller caller, AuthorizationRequests requests, OAuthClients clients, OAuthTokens tokens,
        Connections connections, VaultCatalog catalog, IOptions<KumbuOptions> options, TimeProvider time, ILoggerFactory logs, CancellationToken ct)
    {
        // Allow authorises an app, so it needs the password window; Deny is cleaning up and is never asked.
        if (body.Allow) Security.RequirePasswordWindow(caller);
        var log = logs.CreateLogger(OAuthEndpoints.LogCategory);
        var pending = await requests.GetAsync(id, ct) ?? throw ApiErrors.NotFound(RequestExpired);
        var clientHost = OAuthEndpoints.ClientHost(pending.ClientId, pending.RedirectUri);
        if (body.Allow)
        {
            var (problem, _, _) = await ConsentConnectionAsync(pending, caller, connections, time, ct);
            if (problem is not null)
            {
                OAuthEndpoints.LogStep(log, "consent", "conflict", pending.ConnectionId, clientHost, pending.Id);
                throw new ApiException(StatusCodes.Status409Conflict, ProblemText(problem));
            }

            // A vault being re-encrypted: 503 while the request stays, so that Allow works again once it is done.
            if (await connections.FindAsync(pending.ConnectionId!, ct) is { } doc) await catalog.RequireNoneRekeyingAsync(doc.Vaults, ct);
        }

        // Taken only now: of two decisions at once, one gets it and the other a 404.
        var request = await requests.TakeAsync(id, ct) ?? throw ApiErrors.NotFound(RequestExpired);
        var issuer = OAuthUrls.Origin(http, options.Value.PublicUrl);
        if (!body.Allow)
        {
            OAuthEndpoints.LogStep(log, "consent", "denied", request.ConnectionId, clientHost, request.Id);
            var error = request.Problem == AuthorizationRequests.NotAConnection ? "invalid_target" : "access_denied";
            var description = error == "invalid_target" ? "The address given is no connection's URL." : "The user denied the request.";
            return TypedResults.Ok(new ConsentDecisionView(OAuthEndpoints.Redirect(request.RedirectUri, issuer, request.State, ("error", error), ("error_description", description))));
        }

        string code;
        try
        {
            code = await tokens.IssueCodeAsync(caller.User!, caller.UserKey!, caller.CredentialReadAt!.Value, request, ct);
        }
        catch (ConnectionUnavailableException e)
        {
            // Revoked, expired or changed between the check above and the transaction: the request is spent.
            OAuthEndpoints.LogStep(log, "consent", "conflict", request.ConnectionId, clientHost, request.Id);
            throw new ApiException(StatusCodes.Status409Conflict, e.Message);
        }
        catch (VaultBusyException)
        {
            // A re-keying started or finished between the check above and the transaction, which issued nothing:
            // the request goes back, so "try again" means this request.
            await requests.PutBackAsync(request, CancellationToken.None);
            OAuthEndpoints.LogStep(log, "consent", "busy", request.ConnectionId, clientHost, request.Id);
            throw;
        }

        // After the commit: should this fail, the code is never seen and expires harmlessly within 60 s.
        await clients.MarkAuthorisedAsync(request.ClientId, ct);
        OAuthEndpoints.LogStep(log, "consent", "allowed", request.ConnectionId, clientHost, request.Id);
        return TypedResults.Ok(new ConsentDecisionView(OAuthEndpoints.Redirect(request.RedirectUri, issuer, request.State, ("code", code))));
    }

    /// <summary>
    /// The connection a request names, as the signed-in user may see it: its problem if any (then nothing of it is
    /// shown when it isn't theirs), else its name, its vaults by name with the rights of this grant, its expiry and
    /// client, and whether it was authorised before.
    /// </summary>
    private static async Task<(string? Problem, ConsentConnectionView? Connection, bool Renewal)> ConsentConnectionAsync(
        PendingAuthorisation request, RequestCaller caller, Connections connections, TimeProvider time, CancellationToken ct)
    {
        if (request.Problem is not null || request.ConnectionId is null) return (request.Problem ?? AuthorizationRequests.NotAConnection, null, false);
        var doc = await connections.FindAsync(request.ConnectionId, ct);
        if (doc is null) return (AuthorizationRequests.NotAConnection, null, false);
        if (doc.User != caller.User!.Id) return ("other_user", null, false);
        // A meta that doesn't open with the user key was changed in the database: nothing to show or to authorise.
        if (connections.OpenMeta(caller.User, caller.UserKey!, doc) is not { } meta) return ("revoked", null, false);

        var names = await AccountApi.VaultNamesAsync(caller, ct);
        var view = new ConsentConnectionView(
            meta.Name,
            OAuthTokens.Rights(doc, meta, request.ReadOnly).Where(v => names.ContainsKey(v.Key)).ToDictionary(v => names[v.Key], v => KeyRights.ToText(v.Value)),
            doc.ExpiresAt,
            meta.Client is { } c ? new ClientView(c.Name, c.Host, c.Verified) : null);
        var renewal = doc.AuthorisedAt is not null || meta.Client is not null;
        if (doc.RevokedAt is not null) return ("revoked", view, renewal);
        if (time.GetUtcNow().UtcDateTime >= doc.ExpiresAt) return ("expired", view, renewal);
        return (null, view, renewal);
    }

    private static string ProblemText(string problem) => problem switch
    {
        AuthorizationRequests.NotAConnection => "This address isn't a connection; it can't be allowed.",
        "other_user" => "This connection belongs to another account.",
        "expired" => "This connection has expired.",
        _ => "This connection was revoked.",
    };

    private static async Task<Ok<ConnectionView[]>> ListAsync(HttpContext http, RequestCaller caller, Connections connections, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var names = await AccountApi.VaultNamesAsync(caller, ct);
        var list = await connections.ListAsync(caller.User!, caller.UserKey!, ct);
        var origin = OAuthUrls.Origin(http, options.Value.PublicUrl);
        return TypedResults.Ok(list.Select(c => View(c, names, origin)).ToArray());
    }

    private static async Task<Created<ConnectionView>> CreateAsync(
        CreateConnectionRequest body, HttpContext http, RequestCaller caller, Connections connections, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var names = await AccountApi.VaultNamesAsync(caller, ct);
        var vaults = AccountApi.RightsByVaultId(body.Vaults, names);
        var info = await connections.CreateAsync(caller.User!, caller.UserKey!, caller.CredentialReadAt!.Value, body.Name, vaults, ct, body.ExpiresInDays);
        return TypedResults.Created($"/api/connections/{info.Id}", View(info, names, OAuthUrls.Origin(http, options.Value.PublicUrl)));
    }

    private static async Task<Ok<ConnectionView>> SetVaultsAsync(
        string id, SetConnectionVaultsRequest body, HttpContext http, RequestCaller caller, Connections connections, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var names = await AccountApi.VaultNamesAsync(caller, ct);
        var vaults = AccountApi.RightsByVaultId(body.Vaults, names);
        var info = await connections.SetVaultsAsync(caller.User!, caller.UserKey!, caller.CredentialReadAt!.Value, id, vaults, ct) ?? throw ApiErrors.NotFound(NoSuchConnection);
        return TypedResults.Ok(View(info, names, OAuthUrls.Origin(http, options.Value.PublicUrl)));
    }

    private static async Task<NoContent> RevokeAsync(string id, RequestCaller caller, Connections connections, CancellationToken ct) =>
        await connections.RevokeAsync(caller.User!, id, ct) ? TypedResults.NoContent() : throw ApiErrors.NotFound("You have no such connection that isn't revoked.");

    /// <summary>A vault the connection was given but that no longer opens for the user isn't listed: it has no name to show.</summary>
    private static ConnectionView View(ConnectionInfo info, Dictionary<string, string> names, string origin) => new(
        info.Id,
        info.Name,
        OAuthUrls.Resource(origin, info.Id),
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
