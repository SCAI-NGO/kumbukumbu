// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Threading.RateLimiting;
using Kumbu.Server.Auth;

namespace Kumbu.Server.Api;

/// <summary>Marks the endpoints only a signed-in session may call: account, key management and admin.</summary>
public sealed class SessionOnlyMetadata
{
    public static readonly SessionOnlyMetadata Instance = new();
}

/// <summary>The HTTP-level protections of the server: security headers, the Origin check, session-only endpoints.</summary>
public static class Security
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";

    /// <summary>One year, and for the subdomains too; no <c>preload</c>, which can't be undone quickly.</summary>
    public const string StrictTransportSecurity = "max-age=31536000; includeSubDomains";

    /// <summary>
    /// The security headers, on every response (API, MCP, static files and errors alike), and
    /// <c>Cache-Control: no-store</c> on the API's. Added when the response
    /// starts, so that a handler clearing the response doesn't drop them. HSTS when the public URL is https or the
    /// request came over https as the app sees it: behind a trusted proxy that is <c>X-Forwarded-Proto</c>
    /// (see Program.cs), otherwise the connection itself. Never on plain http: a browser ignores it there, and on
    /// a plain-http localhost it would pin https for every local port.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, string? publicUrl)
    {
        var httpsPublicUrl = publicUrl is not null && publicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        return app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "same-origin";
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                if (httpsPublicUrl || context.Request.IsHttps) headers.StrictTransportSecurity = StrictTransportSecurity;
                // API answers are per user and some carry a secret shown once (keys, codes, recovery keys):
                // no browser or proxy cache may keep them.
                if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
            return next(context);
        });
    }

    /// <summary>
    /// After <see cref="CallerMiddleware"/>, for <c>/api</c>:
    /// <list type="bullet">
    /// <item>an endpoint marked <see cref="SessionOnlyMetadata"/> refuses every caller but a session (403): an
    /// API key carries its owner's user and role, but must not manage keys, the account or other users;</item>
    /// <item>a state-changing request (not GET, HEAD, OPTIONS) without a bearer caller, i.e. with a session or to
    /// an anonymous endpoint such as sign-in, needs an <c>Origin</c> equal to <c>Kumbu:PublicUrl</c>, else 403;
    /// a missing <c>Origin</c> is refused too (browsers send it on every such request). This is the CSRF check
    /// behind <c>SameSite=Strict</c>. Bearer callers need none: a page in a browser can't make up their header.
    /// Without a <c>PublicUrl</c> (development, tests) the request's own scheme and host stand in for it, so
    /// only same-origin pages pass; behind a proxy that terminates TLS that would refuse everything, which is
    /// one more reason a public instance sets <c>KUMBU_PUBLIC_URL</c>.</item>
    /// </list>
    /// </summary>
    public static IApplicationBuilder UseCallerRules(this IApplicationBuilder app, string? publicUrl)
    {
        var expected = publicUrl is null ? null : new Uri(publicUrl).GetLeftPart(UriPartial.Authority);
        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            var caller = context.Features.Get<RequestCaller>();
            if (context.GetEndpoint()?.Metadata.GetMetadata<SessionOnlyMetadata>() is not null && caller?.Kind != CallerKind.Session)
            {
                await RefuseAsync(context, "This needs a signed-in session; API keys can't manage accounts, keys or users.");
                return;
            }

            if (!IsSafe(context.Request.Method) && caller?.Kind is null or CallerKind.Session)
            {
                var origin = context.Request.Headers.Origin.ToString();
                var own = expected ?? $"{context.Request.Scheme}://{context.Request.Host}";
                if (!string.Equals(origin, own, StringComparison.OrdinalIgnoreCase))
                {
                    await RefuseAsync(context, "This request didn't come from Kumbu's own pages.");
                    return;
                }
            }

            await next(context);
        });
    }

    private static bool IsSafe(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static Task RefuseAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new ApiError(message));
    }
}

/// <summary>
/// The rate limits of the account endpoints, in memory (one instance): sign-in 5 per minute per username and
/// 20 per minute per address, join and recovery 5 per minute per address; checking the current password of a
/// password change or of a new recovery key 5 per minute per username (together). Every attempt counts,
/// successful or not: each one costs an Argon2 run, so the limits also bound the memory and CPU a client can make
/// the server spend. Sliding windows, as in <see cref="WrongKeyLimiter"/>.
/// </summary>
public sealed class AccountLimits : IDisposable
{
    public const int SignInsPerUsername = 5;
    public const int SignInsPerAddress = 20;
    public const int JoinsPerAddress = 5;
    public const int RecoveriesPerAddress = 5;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>No username is longer; a longer one only needs a partition, not a distinct one.</summary>
    private const int MaxUsernameKey = 64;

    private readonly PartitionedRateLimiter<string> _signInByUsername = Limiter(SignInsPerUsername);
    private readonly PartitionedRateLimiter<string> _signInByAddress = Limiter(SignInsPerAddress);
    private readonly PartitionedRateLimiter<string> _passwordCheckByUsername = Limiter(SignInsPerUsername);
    private readonly PartitionedRateLimiter<string> _joinByAddress = Limiter(JoinsPerAddress);
    private readonly PartitionedRateLimiter<string> _recoveryByAddress = Limiter(RecoveriesPerAddress);

    /// <summary>Null if the sign-in may go ahead, else how long to wait. The address is asked first, so a client
    /// over its own limit uses up nothing of the username's.</summary>
    public TimeSpan? SignIn(HttpContext context, string username) =>
        Take(_signInByAddress, Address(context)) ?? Take(_signInByUsername, UsernameKey(username));

    /// <summary>A password change or a new recovery key checks the current password: limited like the username's
    /// sign-ins (but apart from them, so changing the password doesn't use up the sign-in that follows), so that a
    /// stolen session can't be used to guess the password faster than signing in could.</summary>
    public TimeSpan? PasswordCheck(string username) => Take(_passwordCheckByUsername, UsernameKey(username));

    public TimeSpan? Join(HttpContext context) => Take(_joinByAddress, Address(context));

    public TimeSpan? Recovery(HttpContext context) => Take(_recoveryByAddress, Address(context));

    private static string Address(HttpContext context) => WrongKeyLimiter.Partition(context.Connection.RemoteIpAddress);

    private static string UsernameKey(string username)
    {
        var key = username.ToLowerInvariant();
        return key.Length > MaxUsernameKey ? key[..MaxUsernameKey] : key;
    }

    private static TimeSpan? Take(PartitionedRateLimiter<string> limiter, string key)
    {
        using var lease = limiter.AttemptAcquire(key);
        if (lease.IsAcquired) return null;
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : Window;
    }

    private static PartitionedRateLimiter<string> Limiter(int permits) => PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = Window,
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    public void Dispose()
    {
        _signInByUsername.Dispose();
        _signInByAddress.Dispose();
        _passwordCheckByUsername.Dispose();
        _joinByAddress.Dispose();
        _recoveryByAddress.Dispose();
    }
}
