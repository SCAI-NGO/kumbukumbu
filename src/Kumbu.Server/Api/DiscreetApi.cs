// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Sync;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary>Whether the session shows the discreet vaults now, and until when: null for a reveal until the session ends
/// or hides them, and whenever they aren't shown. The same for a user with discreet vaults or a PIN and one without.</summary>
public sealed record DiscreetView(bool Revealed, DateTime? Until)
{
    public static readonly DiscreetView Hidden = new(false, null);

    /// <summary>The session's reveal as resolved for this request; never an end that has passed.</summary>
    internal static DiscreetView Of(SessionDiscreet? discreet) => discreet is { Revealed: true } ? new(true, discreet.ShownUntil) : Hidden;
}

/// <summary>The current password and the new PIN, of 4 to 6 digits; it replaces any earlier one.</summary>
public sealed record SetPinRequest(string? Password = null, string? Pin = null);

/// <summary><c>minutes</c>: 5, 10, 30 or 60; null or left out, until the session ends or hides them.</summary>
public sealed record RevealRequest(string? Pin = null, int? Minutes = null);

public sealed record MarkDiscreetRequest(bool Discreet);

/// <summary>
/// Discreet vaults in the web app (discreet vaults design): the PIN, showing and hiding them in this session, and marking
/// a vault. For a session only. Every answer around the PIN is the same whether or not the user has a PIN or discreet
/// vaults; nothing here logs a PIN or a vault's name.
/// </summary>
public static class DiscreetApi
{
    private const string MinutesSentence = "Choose 5, 10, 30 or 60 minutes, or never.";

    private static readonly int[] Minutes = [5, 10, 30, 60];

    public static void MapDiscreetApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/account/discreet").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        api.MapPost("/pin", SetPinAsync).Errors(400, 401, 403, 429);
        api.MapPost("/reveal", RevealAsync).Errors(400, 401, 403, 429);
        api.MapPost("/hide", HideAsync).Errors(401, 403);
        api.MapPut("/vaults/{vault}", MarkAsync).Errors(400, 401, 403, 404, 409);
    }

    /// <summary>The password, checked and limited as every password check behind a session, then the PIN's form.</summary>
    private static async Task<NoContent> SetPinAsync(
        SetPinRequest body, HttpContext http, RequestCaller caller, UserCatalog users, Sessions sessions, AccountLimits limits, DiscreetVaults discreet,
        CancellationToken ct)
    {
        var user = caller.User!;
        if (limits.PasswordCheck(user.Username) is { } retryAfter) throw ApiErrors.TooManyPasswordChecks(retryAfter);
        if (!await users.CheckPasswordAsync(user.Id, caller.UserKey!, body.Password ?? "", ct)) throw new WrongPasswordException("The password is wrong.");
        await AccountApi.PasswordGivenAsync(http, sessions, ct);
        if (!DiscreetVaults.IsPin(body.Pin)) throw ApiErrors.BadRequest(DiscreetVaults.PinSentence);
        await discreet.SetPinAsync(user.Id, body.Pin!, ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The PIN shows the discreet vaults (Decisions 4–6): the minutes are checked first, then the user's limit, then the
    /// entry is counted, then the PIN derived, once in every case. Every PIN that doesn't reveal gets one answer. The
    /// tenth entry of a session, right or wrong, ends it, and it answers as any request of an ended session.
    /// </summary>
    private static async Task<Results<Ok<DiscreetView>, JsonHttpResult<ApiError>>> RevealAsync(
        RevealRequest body, HttpContext http, RequestCaller caller, Sessions sessions, DiscreetVaults discreet, AccountLimits limits, Pairings pairings,
        SyncHub hub, ILoggerFactory logs, CancellationToken ct)
    {
        if (body.Minutes is { } chosen && !Minutes.Contains(chosen)) throw ApiErrors.BadRequest(MinutesSentence);
        var user = caller.User!;
        if (limits.PinCheck(user.Id) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var secret = http.Request.Cookies[CallerMiddleware.SessionCookie]!;
        var entered = await sessions.PinEnteredAsync(secret, ct);
        if (entered.Ended)
        {
            // Only the entry that ended it says so; a session gone meanwhile was ended by someone else.
            if (entered.UserId is { } ended)
            {
                pairings.DropAll(ended);
                Log(logs).LogInformation("Ended a session of user {UserId} after too many PIN entries", ended);
            }

            return await EndedAsync(http, user.Id, hub);
        }

        if (await discreet.CheckPinAsync(user.Id, body.Pin ?? "", ct) != PinMatch.Reveal) throw ApiErrors.BadRequest(DiscreetVaults.NoneSentence);
        var (revealed, until) = await sessions.RevealAsync(secret, body.Minutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null, ct);
        // Ended meanwhile (a concurrent tenth entry, a sign-out elsewhere): as the request would have been answered later.
        if (!revealed) return await EndedAsync(http, user.Id, hub);
        if (Sessions.SubjectOf(secret) is { } subject) hub.DiscreetRevealed(subject);
        return TypedResults.Ok(new DiscreetView(true, until));
    }

    /// <summary>Hides the discreet vaults in this session again, revealed or not: always 204.</summary>
    private static async Task<NoContent> HideAsync(HttpContext http, Sessions sessions, SyncHub hub, CancellationToken ct)
    {
        var secret = http.Request.Cookies[CallerMiddleware.SessionCookie]!;
        await sessions.HideAsync(secret, ct);
        if (Sessions.SubjectOf(secret) is { } subject) await hub.DiscreetHiddenAsync(subject, CancellationToken.None);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Marks one of her vaults, her own or one shared with her, discreet or not; only while the session shows the
    /// discreet vaults. Otherwise, and for a name she doesn't have, the 404 of a vault she doesn't have, whether or not
    /// the vault exists or is discreet. The vault Skills never is.
    /// </summary>
    private static async Task<Results<NoContent, JsonHttpResult<ApiError>>> MarkAsync(
        string vault, MarkDiscreetRequest body, RequestCaller caller, DiscreetVaults discreet, SyncHub hub, CancellationToken ct)
    {
        var user = caller.User!;
        var visible = await caller.OpenVaultsAsync(ct);
        var session = caller.Session!;
        var access = (session.Revealed ? visible.FirstOrDefault(v => v.Name == vault) : null)
            ?? throw ApiErrors.NotFound($"You have no vault named '{vault}'.");
        if (access.IsSkillsVault) throw ApiErrors.BadRequest("The vault Skills can't be discreet.");

        // Every vault the session opens but Skills, should the list not open (Decision 2).
        var all = visible.Concat(session.Hidden).Where(v => !v.IsSkillsVault).Select(v => v.Id).Distinct(StringComparer.Ordinal).ToList();
        try
        {
            await discreet.SetAsync(user.Id, caller.UserKey!, access.Id, body.Discreet, all, ct);
        }
        catch (InvalidOperationException e) when (e.GetType() == typeof(InvalidOperationException))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "Something changed meanwhile; try again.");
        }
        // The user key changed (a rotation): this session's request is answered as one of an ended session.
        catch (CredentialsChangedException)
        {
            return Ended();
        }

        hub.DiscreetChanged(user.Id, access.Id);
        return TypedResults.NoContent();
    }

    /// <summary>What a request of an ended session gets from <see cref="CallerMiddleware"/>; the cookie goes and the
    /// session's sockets close.</summary>
    private static async Task<JsonHttpResult<ApiError>> EndedAsync(HttpContext http, string userId, SyncHub hub)
    {
        await hub.SubjectsEndedAsync(userId, CancellationToken.None);
        AccountApi.ClearSessionCookie(http);
        return Ended();
    }

    private static JsonHttpResult<ApiError> Ended() => TypedResults.Json(new ApiError(CallerMiddleware.SignInSentence), statusCode: StatusCodes.Status401Unauthorized);

    private static ILogger Log(ILoggerFactory logs) => logs.CreateLogger(typeof(DiscreetApi).FullName!);
}
