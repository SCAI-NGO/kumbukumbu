// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Threading.RateLimiting;
using Kumbu.Server.Auth;

namespace Kumbu.Server.Api;

/// <summary>Marks the endpoints only a signed-in session may call: account, key management and admin.</summary>
public sealed class SessionOnlyMetadata
{
    public static readonly SessionOnlyMetadata Instance = new();
}

/// <summary>Marks an anonymous endpoint that carries its own secret and no ambient credential (pairing's redeem):
/// the Origin check, which guards cookies against forged requests, doesn't apply to it.</summary>
public sealed class OriginExemptMetadata
{
    public static readonly OriginExemptMetadata Instance = new();
}

/// <summary>Marks an endpoint whose answers the app's own pages may load into a frame (the gallery's ZIP download,
/// posted into a hidden frame so that an error can be read there instead of replacing the app): its responses get
/// <c>frame-ancestors 'self'</c> instead of <c>'none'</c>. Only the same origin can frame them, so no other site can
/// lay them under its own page.</summary>
public sealed class SameOriginFramesMetadata
{
    public static readonly SameOriginFramesMetadata Instance = new();
}

/// <summary>Marks an endpoint that serves an attachment's bytes: its responses get <see cref="Security.FileContentPolicy"/>
/// instead of the app's policy, so a file opened in a tab of its own runs nothing at the app's origin.</summary>
public sealed class FileContentMetadata
{
    public static readonly FileContentMetadata Instance = new();
}

/// <summary>The HTTP-level protections of the server: security headers, the Origin check, session-only endpoints.</summary>
public static class Security
{
    /// <summary>
    /// Only the app's own scripts, styles, images and connections. <c>base-uri</c> and <c>form-action</c> don't fall
    /// back to <c>default-src</c>: no <c>&lt;base&gt;</c> may redirect relative URLs, and forms may only post to the app
    /// (the web app's submit through fetch, but for the gallery's ZIP, a same-origin form post). <c>object-src 'none'</c>
    /// is stricter than the <c>'self'</c> it would inherit: no plug-in content at all. <c>blob:</c> images: the web app shows an attachment
    /// it fetched (with its session) through an object URL. <c>worker-src 'self'</c> is what it would inherit, said
    /// explicitly: the only worker is pdf.js's, a file of the app; no <c>blob:</c> worker, and no WebAssembly (pdf.js
    /// runs without it, so no <c>'wasm-unsafe-eval'</c>). Scripts never get <c>'unsafe-eval'</c>: nothing in a PDF is
    /// compiled into code.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: blob:; worker-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; " +
        "base-uri 'none'; form-action 'self'; object-src 'none'";

    /// <summary>The same, for an endpoint marked <see cref="SameOriginFramesMetadata"/>: only its own origin may frame it.</summary>
    public static readonly string SameOriginFramesPolicy = ContentSecurityPolicy.Replace("frame-ancestors 'none'", "frame-ancestors 'self'", StringComparison.Ordinal);

    /// <summary>
    /// For an attachment's bytes (<see cref="FileContentMetadata"/>): a document of its own origin that may load nothing
    /// and run nothing, and that no page may frame. It applies only when the file is opened as a page; the web app
    /// fetches files and shows them through object URLs, which this policy doesn't reach.
    /// </summary>
    public const string FileContentPolicy = "sandbox; default-src 'none'; frame-ancestors 'none'";

    /// <summary>One year, and for the subdomains too; no <c>preload</c>, which can't be undone quickly.</summary>
    public const string StrictTransportSecurity = "max-age=31536000; includeSubDomains";

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "[::1]";

    /// <summary>
    /// The security headers, on every response (API, MCP, static files and errors alike), and
    /// <c>Cache-Control: no-store</c> and <c>X-Kumbu-Version</c> on the API's. Added when the response
    /// starts, so that a handler clearing the response doesn't drop them. HSTS when the public URL is https or the
    /// request came over https as the app sees it: behind a trusted proxy that is <c>X-Forwarded-Proto</c>
    /// (see Program.cs), otherwise the connection itself, except to a loopback host (as ASP.NET's own UseHsts):
    /// pinned there, the browser would force https on every local port for a year. Never on plain http: a browser
    /// ignores it there, and on a plain-http localhost it would pin https for every local port.
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
                headers.TryAdd("Referrer-Policy", "same-origin"); // unless stricter already, as on /oauth/authorize
                var metadata = context.GetEndpoint()?.Metadata;
                headers.ContentSecurityPolicy = metadata?.GetMetadata<FileContentMetadata>() is not null ? FileContentPolicy
                    : metadata?.GetMetadata<SameOriginFramesMetadata>() is not null ? SameOriginFramesPolicy
                    : ContentSecurityPolicy;
                if (httpsPublicUrl || (context.Request.IsHttps && !IsLoopbackHost(context.Request.Host.Host))) headers.StrictTransportSecurity = StrictTransportSecurity;
                // API answers are per user and some carry a secret shown once (keys, codes, recovery keys):
                // no browser or proxy cache may keep them.
                // The build version on every API answer (errors and 401s too): the web app reloads into a newer one.
                if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    headers.CacheControl = "no-store";
                    headers["X-Kumbu-Version"] = BuildVersion.Full;
                }
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
                await RefuseAsync(context, "This needs a signed-in session: API keys, connected apps and the dev token can't do it.");
                return;
            }

            var exempt = caller is null && context.GetEndpoint()?.Metadata.GetMetadata<OriginExemptMetadata>() is not null;
            if (!IsSafe(context.Request.Method) && (caller?.Kind is null or CallerKind.Session) && !exempt)
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
/// the server spend. Searching earlier versions and reading a vault's activity, 60 per minute per user (together).
/// Sliding windows, as in <see cref="WrongKeyLimiter"/>.
/// </summary>
public sealed class AccountLimits : IDisposable
{
    public const int SignInsPerUsername = 5;
    public const int SignInsPerAddress = 20;
    public const int JoinsPerAddress = 5;
    public const int RecoveriesPerAddress = 5;
    public const int RedeemsPerAddress = 10;
    public const int RedeemFailuresPerServer = 100;

    /// <summary>
    /// Each of these requests reads every note in its scope with its history. The web app asks once per search that
    /// rested for 200 ms (with the switch for earlier versions on) and once per page of 50 activity rows, and asks
    /// again for each page shown when the window regains focus; a person stays well under 60 a minute, while a stolen
    /// session gets one whole-vault read a second rather than as many as it can open connections for.
    /// </summary>
    public const int HistoryScansPerUser = 60;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>No username is longer; a longer one only needs a partition, not a distinct one.</summary>
    private const int MaxUsernameKey = 64;

    private readonly PartitionedRateLimiter<string> _signInByUsername = Limiter(SignInsPerUsername);
    private readonly PartitionedRateLimiter<string> _signInByAddress = Limiter(SignInsPerAddress);
    private readonly PartitionedRateLimiter<string> _passwordCheckByUsername = Limiter(SignInsPerUsername);
    private readonly PartitionedRateLimiter<string> _joinByAddress = Limiter(JoinsPerAddress);
    private readonly PartitionedRateLimiter<string> _recoveryByAddress = Limiter(RecoveriesPerAddress);
    private readonly PartitionedRateLimiter<string> _redeemByAddress = Limiter(RedeemsPerAddress);
    private readonly PartitionedRateLimiter<string> _redeemFailures = Limiter(RedeemFailuresPerServer);
    private readonly PartitionedRateLimiter<string> _historyScanByUser = Limiter(HistoryScansPerUser);

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

    /// <summary>Redeeming a pairing code: 10 attempts a minute per address.</summary>
    public TimeSpan? Redeem(HttpContext context) => Take(_redeemByAddress, Address(context));

    /// <summary>Searching earlier versions or reading a vault's activity, counted per user across their sessions.</summary>
    public TimeSpan? HistoryScan(string userId) => Take(_historyScanByUser, userId);

    public const string HistoryScansRefused = "Too many searches of earlier versions or reads of activity; try again in a minute.";

    /// <summary>
    /// Whether the server has seen 100 wrong codes in the last minute. A wrong code can't tell which code it was aimed
    /// at, so there is no count per code; this bounds a guess spread over many addresses. While it holds every redeem
    /// is refused, a right code too: the spec's trade-off, an attack delays pairing rather than speeding up guessing.
    /// </summary>
    public bool RedeemsBlocked()
    {
        using var probe = _redeemFailures.AttemptAcquire(ServerWide, 0);
        return !probe.IsAcquired;
    }

    /// <summary>A wrong or expired code, counted towards <see cref="RedeemsBlocked"/>; true for the one that used up the minute's allowance.</summary>
    public bool RedeemFailed()
    {
        using var lease = _redeemFailures.AttemptAcquire(ServerWide);
        return lease.IsAcquired && RedeemsBlocked();
    }

    public static readonly TimeSpan RedeemRetryAfter = Window;

    private const string ServerWide = "*";

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
        _redeemByAddress.Dispose();
        _redeemFailures.Dispose();
        _historyScanByUser.Dispose();
    }
}
