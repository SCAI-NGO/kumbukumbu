// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Auth;

/// <summary>
/// Resolves every request that needs one to a <see cref="RequestCaller"/>: a bearer key (the dev token when
/// configured, or an API key) or, under <c>/api</c> only, the session cookie. Which requests need one
/// fails closed:
/// <list type="bullet">
/// <item>every matched endpoint, unless it is marked <c>AllowAnonymous()</c> (<c>/healthz</c>; later
/// <c>POST /api/session</c>, <c>POST /api/join</c>, <c>GET /api/invitations/{code}</c>, <c>POST /api/recover</c>),
/// so a path spelled differently but routed to <c>MapMcp</c> is still guarded;</item>
/// <item>every path under <c>/mcp</c> or <c>/api</c> (case-insensitively: endpoint routing matches literal
/// segments case-insensitively, so <c>/MCP</c> reaches <c>MapMcp("/mcp")</c>), also without an endpoint, unless an
/// anonymous endpoint is mapped there itself (an anonymous fallback doesn't count).</item>
/// </list>
/// Everything else (the web app's static files, later) is anonymous. Runs after routing and before the MCP SDK
/// sees the request; never logs a key or a cookie.
/// </summary>
public sealed class CallerMiddleware(
    RequestDelegate next, IOptions<KumbuOptions> options, Sessions sessions, ApiKeys apiKeys, VaultCatalog catalog, WrongKeyLimiter wrongKeys)
{
    public const string SessionCookie = "kumbu_session";

    private const string Scheme = "Bearer ";

    private readonly string? _devToken = string.IsNullOrEmpty(options.Value.DevToken) ? null : options.Value.DevToken;

    private readonly byte[]? _devTokenHash = string.IsNullOrEmpty(options.Value.DevToken) ? null : Hash(options.Value.DevToken);

    public async Task InvokeAsync(HttpContext context)
    {
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
                     && await sessions.ResolveAsync(secret, context.RequestAborted) is var (user, userKey)
                ? RequestCaller.ForSession(user, userKey, catalog)
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

    private static Task TooManyAsync(HttpContext context, TimeSpan retryAfter)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(Math.Max(1, retryAfter.TotalSeconds))).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return IsUnder(context, "/api")
            ? context.Response.WriteAsJsonAsync(new { error = "Too many wrong keys from this address; try again later." })
            : Task.CompletedTask;
    }
}
