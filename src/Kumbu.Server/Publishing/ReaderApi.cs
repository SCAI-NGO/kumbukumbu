// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Attachments;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Kumbu.Server.Publishing;

/// <summary><c>code</c> as the reader typed or pasted it (case, spaces and hyphens don't matter).</summary>
public sealed record ReaderCodeRequest(string Code);

/// <summary>The published copy: its title, its filtered Markdown, when the snapshot was taken, its files, each read
/// through <c>GET /api/s/files/{index}</c>, and its copy id, which the page sends with each (<see cref="ReaderApi.CopyHeader"/>).</summary>
public sealed record ReaderView(string Title, string Markdown, DateTime SnapshotAt, ReaderFileView[] Files, string Copy);

public sealed record ReaderFileView(int Index, string Name, string MediaType);

/// <summary>The paths strangers open (publish spec §3.12): the reader page and its endpoint. They get the reader's
/// headers and are left out of the HTTP metrics.</summary>
public static class ReaderPaths
{
    public static bool Is(PathString path) =>
        (path.StartsWithSegments("/s", StringComparison.OrdinalIgnoreCase, out var rest) && rest.Value is null or "" or "/")
        || path.Equals("/s.html", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(ReaderApi.CookiePath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The anonymous reader endpoint (publish spec §3.12, §3.13). The code opens the copy, and the answer sets a reader
/// cookie of its own (decision 11), so a reload needs no code. A malformed, wrong or withdrawn code, a dead cookie and a
/// deactivated owner all answer the same 404 (decision 13), count as a wrong code towards the limits, as pairing's do,
/// and clear the cookie. No caller is asked for: a session beside it neither helps nor hinders. Nothing about a reader
/// is stored, counted or logged; the code, its lookup id and the cookie never reach a log.
/// </summary>
public static class ReaderApi
{
    public const string Cookie = "kumbu_reader";
    public const string CookiePath = "/api/s";

    /// <summary>The copy id (<see cref="ShareCode.CopyId"/>) the page sends with each file: the file opens only if it is
    /// the reader cookie's copy's. Never logged, as the cookie isn't.</summary>
    public const string CopyHeader = "X-Kumbu-Copy";
    public const string NotFoundSentence = "This link doesn't open a published note: the code is wrong, or the note is no longer published.";
    public const string OffSentence = "Published notes can't be read on this server.";

    /// <summary>The <see cref="ApiError.Type"/> of the server's switch: a fact about the server, not about the link.</summary>
    public const string OffType = "publishing-off";

    public static void MapReaderApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(CookiePath).AddEndpointFilter(ApiErrors.Filter).AllowAnonymous();
        // A code and a little JSON around it: a larger body is refused before it is bound.
        api.MapPost("", ReadWithCodeAsync).WithMetadata(new RequestSizeLimitAttribute(1024)).Errors(400, 403, 404, 413, 429);
        api.MapGet("", ReadAgainAsync).Errors(404, 429);
        // Only images are published (decision 1); a file opened in a tab of its own runs nothing.
        api.MapGet("/files/{index}", FileAsync)
            .Produces(StatusCodes.Status200OK, typeof(byte[]), ImageTypes[0], ImageTypes[1..])
            .WithMetadata(FileContentMetadata.Instance)
            .Errors(404, 429);
    }

    private static readonly string[] ImageTypes = [.. AttachmentFormat.All.Where(f => f.Kind == AttachmentKind.Image).Select(f => f.MediaType).Distinct()];

    private static async Task<Ok<ReaderView>> ReadWithCodeAsync(
        ReaderCodeRequest body, HttpContext http, PublicationStore store, UserCatalog users, AccountLimits limits, IOptions<KumbuOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        var (code, opened) = await OpenAsync(body.Code, limits.ReaderRead(http), http, store, users, limits, options, logs, ct);
        http.Response.Cookies.Append(Cookie, code, CookieOptions());
        return TypedResults.Ok(View(code, opened.Copy));
    }

    private static async Task<Ok<ReaderView>> ReadAgainAsync(
        HttpContext http, PublicationStore store, UserCatalog users, AccountLimits limits, IOptions<KumbuOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        var (code, opened) = await OpenAsync(http.Request.Cookies[Cookie], limits.ReaderRead(http), http, store, users, limits, options, logs, ct);
        return TypedResults.Ok(View(code, opened.Copy));
    }

    /// <summary>A file of the reader cookie's copy, if <see cref="CopyHeader"/> names that copy: one cookie serves every
    /// tab of a browser, and the copy read last set it.</summary>
    private static async Task<FileContentHttpResult> FileAsync(
        string index, [FromHeader(Name = CopyHeader)] string? copy, HttpContext http, PublicationStore store, UserCatalog users, AccountLimits limits,
        IOptions<KumbuOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        var (code, opened) = await OpenAsync(http.Request.Cookies[Cookie], limits.ReaderFileRead(http), http, store, users, limits, options, logs, ct);
        // Another copy's id or a bad index with a live cookie is no guess: the same answer, but the cookie stays and
        // nothing is counted.
        if (!string.Equals(copy, ShareCode.CopyId(code), StringComparison.Ordinal)
            || !int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            || await store.OpenFileAsync(opened, n, ct) is not { } file)
        {
            throw ApiErrors.NotFound(NotFoundSentence);
        }

        return TypedResults.File(file.Bytes, file.MediaType);
    }

    /// <summary>
    /// The limits first, as pairing's redeem: the read's own (<paramref name="allowance"/>: a copy's per address,
    /// network and server, or a file's per address), then the wrong codes per network and server-wide; then the switch.
    /// The copy opens only if its owner's account is active (spec §3.7): deactivation hides it.
    /// </summary>
    private static async Task<(string Code, OpenedPublication Opened)> OpenAsync(
        string? typed, TimeSpan? allowance, HttpContext http, PublicationStore store, UserCatalog users, AccountLimits limits, IOptions<KumbuOptions> options,
        ILoggerFactory logs, CancellationToken ct)
    {
        if (allowance is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        if (limits.ReadersBlocked(http)) throw ApiErrors.TooMany(AccountLimits.RedeemRetryAfter);
        if (!options.Value.Publish.Enabled) throw new ApiException(StatusCodes.Status404NotFound, OffSentence, type: OffType);

        var code = typed is null ? null : ShareCode.Parse(typed);
        if (code is not null && await store.OpenPublicationAsync(code, ct) is { } opened
            && await users.Users.Find(u => u.Id == opened.Owner).FirstOrDefaultAsync(ct) is { Active: true })
        {
            return (code, opened);
        }

        // No cookie at all is no guess; it answers alike all the same.
        if (typed is not null) Failed(http, limits, logs);
        http.Response.Cookies.Delete(Cookie, CookieOptions());
        throw ApiErrors.NotFound(NotFoundSentence);
    }

    private static void Failed(HttpContext http, AccountLimits limits, ILoggerFactory logs)
    {
        // The network goes unnamed, as every address does in the log, and so does the code.
        switch (limits.ReaderFailed(http))
        {
            case RedeemBlock.Network:
                logs.CreateLogger(typeof(ReaderApi).FullName!).LogWarning(
                    "{Count} wrong share codes within a minute from one network: its reads of published notes are refused until it eases", AccountLimits.ReaderFailuresPerNetwork);
                break;
            case RedeemBlock.Server:
                logs.CreateLogger(typeof(ReaderApi).FullName!).LogWarning(
                    "{Count} wrong share codes within a minute: every read of a published note is refused until it eases", AccountLimits.ReaderFailuresPerServer);
                break;
        }
    }

    /// <summary>Decision 11: a session cookie (no Max-Age), sent only to the reader endpoint, which
    /// <c>/api/session</c> doesn't path-match.</summary>
    private static CookieOptions CookieOptions() => new() { Path = CookiePath, Secure = true, SameSite = SameSiteMode.Strict, HttpOnly = true };

    private static ReaderView View(string code, PublishedCopy copy) =>
        new(copy.Title, copy.Markdown, copy.SnapshotAt, [.. copy.Files.Select(f => new ReaderFileView(f.Index, f.Name, f.MediaType))], ShareCode.CopyId(code));
}
