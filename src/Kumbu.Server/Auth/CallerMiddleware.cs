// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.OAuth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Auth;

/// <summary>Marks an endpoint the web app calls in the background (the open note's version, every few seconds): with
/// a session, it doesn't count as the session's use, so the idle timeout still ends a session left alone.</summary>
public sealed class BackgroundMetadata
{
    public static readonly BackgroundMetadata Instance = new();
}

/// <summary>
/// Resolves every request that needs one to a <see cref="RequestCaller"/>: a bearer key (the dev token when
/// configured, or an API key) or, under <c>/api</c> only, the session cookie; on a connection's URL only that
/// connection's access tokens. Which requests need one
/// fails closed:
/// <list type="bullet">
/// <item>every matched endpoint, unless it is marked <c>AllowAnonymous()</c> (<c>/healthz</c>,
/// <c>/api/openapi.json</c>, <c>POST /api/session</c>, <c>POST /api/join</c>, <c>POST /api/invitations/peek</c>,
/// <c>POST /api/recover</c>),
/// so a path spelled differently but routed to <c>MapMcp</c> is still guarded;</item>
/// <item>every path under <c>/mcp</c> or <c>/api</c> (case-insensitively: endpoint routing matches literal
/// segments case-insensitively, so <c>/MCP</c> reaches <c>MapMcp("/mcp")</c>), also without an endpoint, unless an
/// anonymous endpoint is mapped there itself (an anonymous fallback doesn't count).</item>
/// </list>
/// Everything else (the web app's static files, later) is anonymous. A connection's URL, <c>/mcp/c/{id}</c>, is
/// answered before all that (<see cref="ConnectionUrlAsync"/>). Runs after routing and before the MCP SDK
/// sees the request; never logs a key or a cookie.
/// </summary>
public sealed class CallerMiddleware(
    RequestDelegate next, IOptions<KumbuOptions> options, Sessions sessions, ApiKeys apiKeys, VaultCatalog catalog, WrongKeyLimiter wrongKeys,
    Connections connections, OAuthTokens tokens)
{
    public const string SessionCookie = "kumbu_session";

    private const string Scheme = "Bearer ";

    private readonly string? _devToken = string.IsNullOrEmpty(options.Value.DevToken) ? null : options.Value.DevToken;

    private readonly byte[]? _devTokenHash = string.IsNullOrEmpty(options.Value.DevToken) ? null : Hash(options.Value.DevToken);

    public async Task InvokeAsync(HttpContext context)
    {
        if (ConnectionIdSegment(context) is { } segment)
        {
            await ConnectionUrlAsync(context, segment);
            return;
        }

        if (!NeedsCaller(context))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        RequestCaller? caller = null;
        if (header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            var ip = WrongKeyLimiter.Partition(context.Connection.RemoteIpAddress);
            if (wrongKeys.TryReserve(ip) is { } retryAfter)
            {
                await TooManyAsync(context, retryAfter);
                return;
            }

            try
            {
                caller = await ResolveBearerAsync(header[Scheme.Length..], context.RequestAborted);
            }
            catch (VaultBusyException e)
            {
                // A known key whose vault can't be opened just now: not a wrong key, and not a fault.
                wrongKeys.Release(ip);
                await BusyAsync(context, e);
                return;
            }
            catch
            {
                // MongoDB unreachable or the request aborted: not a wrong key.
                wrongKeys.Release(ip);
                throw;
            }

            if (caller is null) wrongKeys.Failed(ip);
            else wrongKeys.Release(ip);
        }
        else if (IsUnder(context, "/api"))
        {
            // Only the web app's API takes the cookie. MCP clients send bearer keys; a cookie on /mcp would let a
            // page on a sibling subdomain (same site, so SameSite=Strict doesn't stop it) post a blind no-cors
            // tools/call with the user's session.
            caller = context.Request.Cookies.TryGetValue(SessionCookie, out var secret)
                     && await sessions.ResolveAsync(secret, context.RequestAborted, touch: context.GetEndpoint()?.Metadata.GetMetadata<BackgroundMetadata>() is null)
                         is var (user, userKey, readAt)
                ? RequestCaller.ForSession(user, userKey, readAt, catalog)
                : null;
        }

        if (caller is null)
        {
            await UnauthorisedAsync(context);
            return;
        }

        context.Features.Set(caller);
        await next(context);
    }

    /// <summary>The third segment of <c>/mcp/c/{id}…</c> (the prefix case-insensitive, as routing is), else null.</summary>
    private static string? ConnectionIdSegment(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/mcp/c", StringComparison.OrdinalIgnoreCase, out var rest)) return null;
        var segments = rest.Value!.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? null : segments[0];
    }

    /// <summary>
    /// A connection's own URL takes only its access tokens (spec §4): API keys, the dev token and the cookie are not
    /// resolved here, so a credential for <c>/mcp</c> opens nothing on it, and an access token is resolved nowhere
    /// else. An id that is no connection's is a 404 (Ruling 18), like its metadata URL; a known one, in any state,
    /// answers a request without a valid token with a 401 and the challenge that leads an app to the metadata. An
    /// access token is looked up under the wrong-key limit; one that is known but no longer valid here (expired,
    /// another connection's, revoked meanwhile) isn't a guess and gives its permit back (Ruling 12), an unknown one
    /// uses it up. Any other bearer is refused without a lookup.
    /// </summary>
    private async Task ConnectionUrlAsync(HttpContext context, string id)
    {
        if (!OAuthUrls.IsConnectionId(id) || await connections.FindAsync(id, context.RequestAborted) is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "Not found." });
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        var bearer = header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) ? header[Scheme.Length..] : null;
        if (bearer is not null && bearer.StartsWith(OAuthTokens.AccessPrefix, StringComparison.Ordinal))
        {
            var ip = WrongKeyLimiter.Partition(context.Connection.RemoteIpAddress);
            if (wrongKeys.TryReserve(ip) is { } retryAfter)
            {
                await TooManyAsync(context, retryAfter);
                return;
            }

            RequestCaller? caller = null;
            AccessResolution resolution;
            try
            {
                var origin = OAuthUrls.Origin(context, options.Value.PublicUrl);
                resolution = await tokens.ResolveAccessAsync(bearer, id, OAuthUrls.Resource(origin, id), context.RequestAborted);
                if (resolution.Connection is { } connection) caller = await RequestCaller.ForConnectionAsync(connection, bearer, catalog, context.RequestAborted);
            }
            catch (VaultBusyException e)
            {
                wrongKeys.Release(ip);
                await BusyAsync(context, e);
                return;
            }
            catch
            {
                // MongoDB unreachable or the request aborted: not a wrong key.
                wrongKeys.Release(ip);
                throw;
            }

            if (resolution.Known) wrongKeys.Release(ip);
            else wrongKeys.Failed(ip);
            if (caller is not null)
            {
                context.Features.Set(caller);
                await next(context);
                return;
            }
        }

        var challenge = $"Bearer resource_metadata=\"{OAuthUrls.ResourceMetadata(OAuthUrls.Origin(context, options.Value.PublicUrl), id)}\", scope=\"{OAuthScopes.Challenge}\"";
        if (bearer is not null) challenge += ", error=\"invalid_token\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = challenge;
    }

    private static bool NeedsCaller(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var anonymous = endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        foreach (var prefix in GuardedPrefixes)
        {
            // Under /mcp and /api only an endpoint mapped there may be anonymous. An anonymous fallback (the web
            // app's MapFallbackToFile) matches unmapped /api and /mcp paths too, and must not answer them.
            if (IsUnder(context, "/" + prefix)) return !(anonymous && IsMappedUnder(endpoint!, prefix));
        }

        return endpoint is not null && !anonymous;
    }

    private static readonly string[] GuardedPrefixes = ["mcp", "api"];

    /// <summary>A route of its own under <paramref name="prefix"/>: its first segment is that literal, and it
    /// isn't a fallback (MapFallback gives its endpoints the last order, int.MaxValue).</summary>
    private static bool IsMappedUnder(Endpoint endpoint, string prefix) =>
        endpoint is RouteEndpoint { Order: < int.MaxValue } route
        && route.RoutePattern.PathSegments is [{ IsSimple: true, Parts: [Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart literal] }, ..]
        && literal.Content.Equals(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(HttpContext context, string prefix) =>
        context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase);

    private async Task<RequestCaller?> ResolveBearerAsync(string presented, CancellationToken ct)
    {
        // Hashes of equal length, so that a key of the wrong length takes as long to refuse as one of the right length.
        if (_devToken is not null && CryptographicOperations.FixedTimeEquals(Hash(presented), _devTokenHash))
        {
            return RequestCaller.ForDevToken(_devToken, catalog);
        }

        if (ApiKeys.LooksLikeKey(presented) && await apiKeys.ResolveAsync(presented, ct) is { } key)
        {
            return await RequestCaller.ForApiKeyAsync(key, presented, catalog, ct);
        }

        return null;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static Task UnauthorisedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        if (IsUnder(context, "/api")) return context.Response.WriteAsJsonAsync(new { error = "Sign in, or send an API key." });
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    /// <summary>A vault of the caller is being or was just re-encrypted (<see cref="VaultBusyException"/>): 503 with
    /// <c>Retry-After</c>, as <see cref="Api.ApiErrors"/> answers it behind the endpoints.</summary>
    private static Task BusyAsync(HttpContext context, VaultBusyException e)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = ((int)Api.ApiErrors.BusyRetryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return IsUnder(context, "/api") ? context.Response.WriteAsJsonAsync(new Api.ApiError(e.Message)) : Task.CompletedTask;
    }

    private static Task TooManyAsync(HttpContext context, TimeSpan retryAfter)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(Math.Max(1, retryAfter.TotalSeconds))).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return IsUnder(context, "/api")
            ? context.Response.WriteAsJsonAsync(new { error = "Too many wrong keys from this address; try again later." })
            : Task.CompletedTask;
    }
}
