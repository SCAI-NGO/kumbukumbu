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
/// configured; API keys from Task 6) or else the session cookie. Which requests need one fails closed:
/// <list type="bullet">
/// <item>every matched endpoint, unless it is marked <c>AllowAnonymous()</c> (<c>/healthz</c>; later
/// <c>POST /api/session</c>, <c>POST /api/join</c>, <c>GET /api/invitations/{code}</c>, <c>POST /api/recover</c>),
/// so a path spelled differently but routed to <c>MapMcp</c> is still guarded;</item>
/// <item>every path under <c>/mcp</c> or <c>/api</c> (case-insensitively: endpoint routing matches literal
/// segments case-insensitively, so <c>/MCP</c> reaches <c>MapMcp("/mcp")</c>), also without an endpoint.</item>
/// </list>
/// Everything else (the web app's static files, later) is anonymous. Runs after routing and before the MCP SDK
/// sees the request; never logs a key or a cookie.
/// </summary>
public sealed class CallerMiddleware(
    RequestDelegate next, IOptions<KumbuOptions> options, Sessions sessions, VaultCatalog catalog, WrongKeyLimiter wrongKeys)
{
    public const string SessionCookie = "kumbu_session";

    private const string Scheme = "Bearer ";

    private readonly string? _devToken = string.IsNullOrEmpty(options.Value.DevToken) ? null : options.Value.DevToken;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!NeedsCaller(context))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        RequestCaller? caller;
        if (header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            var ip = WrongKeyLimiter.Partition(context.Connection.RemoteIpAddress);
            if (wrongKeys.RetryAfter(ip) is { } retryAfter)
            {
                await TooManyAsync(context, retryAfter);
                return;
            }

            caller = ResolveBearer(header[Scheme.Length..]);
            if (caller is null) wrongKeys.Failed(ip);
        }
        else
        {
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

    private static bool NeedsCaller(HttpContext context) =>
        context.GetEndpoint() is { } endpoint
            ? endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
            : IsUnder(context, "/mcp") || IsUnder(context, "/api");

    private static bool IsUnder(HttpContext context, string prefix) =>
        context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase);

    private RequestCaller? ResolveBearer(string presented)
    {
        if (_devToken is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(_devToken)))
        {
            return RequestCaller.ForDevToken(_devToken, catalog);
        }

        // Task 6: API keys (kumbu_…) are looked up here; until then any other bearer is a wrong key.
        return null;
    }

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
