// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Publishing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Kumbu.Server.Skills;

/// <summary>A file of the shared skill: its index, its path relative to the skill's folder, its media type and size.</summary>
public sealed record SkillPageFileView(int Index, string Path, string MediaType, long Size);

/// <summary>The shared skill: its folder's name, the manifest's description and text, when it was taken, its files,
/// whether any of them is a script, and its copy id, which the page sends with the <c>.zip</c> (<see cref="ReaderApi.CopyHeader"/>).</summary>
public sealed record SkillPageView(string Name, string Description, string Manifest, DateTime SnapshotAt, SkillPageFileView[] Files, bool HasScripts, string Copy);

/// <summary>
/// The anonymous skill-link endpoint (skills design §3.12, Decision 6), as the reader's (<see cref="ReaderApi"/>) for a
/// note: the code opens the skill and sets a cookie of its own, so a reload and the <c>.zip</c> need no code. Every failure
/// answers the same 404, counts as a wrong code towards the reader's limits and clears the cookie; the answer names no
/// owner, vault or path. The code, its lookup id and the cookie never reach a log.
/// </summary>
public static class SkillPageApi
{
    public const string Cookie = "kumbu_skill";
    public const string CookiePath = "/api/k";
    public const string NotFoundSentence = "This link doesn't open a shared skill: the code is wrong, or the skill is no longer shared.";
    public const string ChangedSentence = "This skill was changed since it was shown; look at it again before adding.";
    public const string OffSentence = "Shared skills can't be opened on this server.";

    private static readonly string[] ScriptExtensions = [".py", ".sh", ".js", ".mjs", ".ts"];

    public static void MapSkillPageApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(CookiePath).AddEndpointFilter(ApiErrors.Filter).AllowAnonymous();
        api.MapPost("", OpenWithCodeAsync).WithMetadata(new RequestSizeLimitAttribute(1024)).Errors(400, 403, 404, 413, 429);
        api.MapGet("", OpenAgainAsync).Errors(404, 429);
        api.MapGet("/zip", ZipAsync)
            .Produces(StatusCodes.Status200OK, typeof(byte[]), "application/zip")
            .WithMetadata(FileContentMetadata.Instance)
            .Errors(404, 429);
    }

    /// <summary>A file under <c>scripts/</c> or with a script's extension.</summary>
    internal static bool HasScripts(IEnumerable<string> paths) =>
        paths.Any(p => p.StartsWith("scripts/", StringComparison.Ordinal) || ScriptExtensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)));

    private static async Task<Ok<SkillPageView>> OpenWithCodeAsync(
        ReaderCodeRequest body, HttpContext http, PublicationStore store, UserCatalog users, AccountLimits limits, IOptions<KumbuOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        var (code, opened) = await OpenAsync(body.Code, http, store, users, limits, options, logs, ct);
        http.Response.Cookies.Append(Cookie, code, CookieOptions());
        return TypedResults.Ok(View(code, opened.Copy));
    }

    private static async Task<Ok<SkillPageView>> OpenAgainAsync(
        HttpContext http, PublicationStore store, UserCatalog users, AccountLimits limits, IOptions<KumbuOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        var (code, opened) = await OpenAsync(http.Request.Cookies[Cookie], http, store, users, limits, options, logs, ct);
        return TypedResults.Ok(View(code, opened.Copy));
    }

    /// <summary>Every file of the cookie's skill under its folder's name, if <see cref="ReaderApi.CopyHeader"/> names that
    /// copy: one cookie serves every tab of a browser, and the skill opened last set it.</summary>
    private static async Task<FileContentHttpResult> ZipAsync(
        [FromHeader(Name = ReaderApi.CopyHeader)] string? copy, HttpContext http, PublicationStore store, UserCatalog users, AccountLimits limits,
        IOptions<KumbuOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        var (code, opened) = await OpenAsync(http.Request.Cookies[Cookie], http, store, users, limits, options, logs, ct);
        // Another copy's id with a live cookie is no guess: the same answer, but the cookie stays and nothing is counted.
        if (!string.Equals(copy, ShareCode.CopyId(code), StringComparison.Ordinal)) throw ApiErrors.NotFound(NotFoundSentence);
        var files = new List<(string Path, byte[] Bytes)>(opened.Copy.Files.Count);
        foreach (var file in opened.Copy.Files.OrderBy(f => f.Index))
        {
            files.Add((file.Path, (await store.OpenSkillFileAsync(opened, file.Index, ct))?.Bytes ?? throw ApiErrors.NotFound(NotFoundSentence)));
        }

        http.Response.Headers.ContentDisposition = SkillZip.Disposition(opened.Copy.Name);
        return TypedResults.File(SkillZip.Of(opened.Copy.Name, files, opened.Copy.SnapshotAt), "application/zip");
    }

    private static async Task<(string Code, OpenedSkill Opened)> OpenAsync(
        string? typed, HttpContext http, PublicationStore store, UserCatalog users, AccountLimits limits, IOptions<KumbuOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        if (await OpenCodeAsync(typed, http, store, users, limits, options, logs, ct) is { } opened) return opened;
        http.Response.Cookies.Delete(Cookie, CookieOptions());
        throw ApiErrors.NotFound(NotFoundSentence);
    }

    /// <summary>As the reader's: the limits first, then the switch; no code at all takes no allowance. The skill opens
    /// only if its owner's account is active; null for a wrong code, which counts. Also Add to my skills' way in
    /// (<see cref="SkillsApi"/>), so a session guesses no faster than a stranger.</summary>
    internal static async Task<(string Code, OpenedSkill Opened)?> OpenCodeAsync(
        string? typed, HttpContext http, PublicationStore store, UserCatalog users, AccountLimits limits, IOptions<KumbuOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        if (typed is not null)
        {
            if (limits.ReaderRead(http) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
            if (limits.ReadersBlocked(http)) throw ApiErrors.TooMany(AccountLimits.RedeemRetryAfter);
        }

        if (!options.Value.Publish.Enabled) throw new ApiException(StatusCodes.Status404NotFound, OffSentence, type: ReaderApi.OffType);

        if (typed is not null)
        {
            if (ShareCode.Parse(typed) is { } code && await store.OpenSkillAsync(code, ct) is { } opened
                && await users.Users.Find(u => u.Id == opened.Owner).FirstOrDefaultAsync(ct) is { Active: true })
            {
                return (code, opened);
            }

            Failed(http, limits, logs);
        }

        return null;
    }

    private static void Failed(HttpContext http, AccountLimits limits, ILoggerFactory logs)
    {
        // The network goes unnamed, as every address does in the log, and so does the code.
        switch (limits.ReaderFailed(http))
        {
            case RedeemBlock.Network:
                logs.CreateLogger(typeof(SkillPageApi).FullName!).LogWarning(
                    "{Count} wrong share codes within a minute from one network: its reads of shared skills and published notes are refused until it eases", AccountLimits.ReaderFailuresPerNetwork);
                break;
            case RedeemBlock.Server:
                logs.CreateLogger(typeof(SkillPageApi).FullName!).LogWarning(
                    "{Count} wrong share codes within a minute: every read of a shared skill or a published note is refused until it eases", AccountLimits.ReaderFailuresPerServer);
                break;
        }
    }

    /// <summary>A session cookie (no Max-Age), sent only to this endpoint, apart from the reader's.</summary>
    private static CookieOptions CookieOptions() => new() { Path = CookiePath, Secure = true, SameSite = SameSiteMode.Strict, HttpOnly = true };

    /// <summary>The skill as the page and Add to my skills' preview (<see cref="SkillsApi"/>) show it.</summary>
    internal static SkillPageView View(string code, PublishedSkillCopy copy) =>
        new(copy.Name, copy.Description, copy.Manifest, copy.SnapshotAt,
            [.. copy.Files.Select(f => new SkillPageFileView(f.Index, f.Path, f.MediaType, f.Size))], HasScripts(copy.Files.Select(f => f.Path)), ShareCode.CopyId(code));
}
