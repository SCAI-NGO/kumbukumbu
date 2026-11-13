// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary>The person's calendar settings: <c>zone</c>, the IANA zone she chose (null: none, so the server's applies);
/// <c>serverZone</c>, the server's.</summary>
public sealed record CalendarPreferencesView(string? Zone, string ServerZone);

/// <summary>A change of the zone: an IANA id such as <c>Europe/Vienna</c>, or null to clear it.</summary>
public sealed record CalendarZoneRequest(string? Zone);

/// <summary><c>view</c>: <c>week</c> or <c>month</c>; <c>grid</c>: the month grid shown.</summary>
public sealed record CalendarViewRequest(string View, bool Grid);

/// <summary>The calendar for the web app (calendar spec §7): the person's time zone and her last view per calendar.</summary>
public static class CalendarApi
{
    private const string NoViewSentence = "Only a signed-in person keeps a calendar view.";

    public static void MapCalendarApi(this IEndpointRouteBuilder app)
    {
        // As /account/preferences: a person's setting, for her session only.
        var account = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        account.MapGet("/account/calendar", PreferencesAsync).Errors(401, 403);
        account.MapPut("/account/calendar", SetZoneAsync).Errors(400, 401, 403);

        var vault = app.MapGroup("/api/vaults/{vault}/calendar").AddEndpointFilter(ApiErrors.Filter);
        vault.MapGet("view", ViewAsync).Errors(400, 401, 404, 503);
        vault.MapPut("view", SetViewAsync).Errors(400, 401, 403, 404, 409, 503);
    }

    private static async Task<Ok<CalendarPreferencesView>> PreferencesAsync(
        RequestCaller caller, CalendarSettings settings, ServerZone server, CancellationToken ct) =>
        TypedResults.Ok(new CalendarPreferencesView(await settings.ZoneAsync(caller.User!.Id, ct), (await server.ZoneAsync(ct)).Id));

    private static async Task<Ok<CalendarPreferencesView>> SetZoneAsync(
        CalendarZoneRequest body, RequestCaller caller, CalendarSettings settings, ServerZone server, CancellationToken ct)
    {
        string? zone = null;
        if (body.Zone is not null)
        {
            zone = (Zones.Find(body.Zone) ?? throw ApiErrors.BadRequest(
                $"{body.Zone} is not a time zone; choose one from the list (an IANA name such as Europe/Vienna).")).Id;
        }

        await settings.SetZoneAsync(caller.User!.Id, zone, ct);
        return TypedResults.Ok(new CalendarPreferencesView(zone, (await server.ZoneAsync(ct)).Id));
    }

    /// <summary>Asked when the calendar opens; a credential without a person's user key (a key, a connection) reads the
    /// default.</summary>
    private static async Task<Ok<CalendarViewState>> ViewAsync(
        string vault, INoteStore store, RequestCaller caller, CalendarSettings settings, CancellationToken ct)
    {
        var id = await VaultIdAsync(vault, store, caller, ct);
        if (caller.User is not { } user || caller.UserKey is not { } userKey) return TypedResults.Ok(CalendarViewState.Default);
        return TypedResults.Ok(await settings.ViewAsync(user.Id, userKey, id, ct));
    }

    private static async Task<Ok<CalendarViewState>> SetViewAsync(
        string vault, CalendarViewRequest body, INoteStore store, RequestCaller caller, CalendarSettings settings, CancellationToken ct)
    {
        var id = await VaultIdAsync(vault, store, caller, ct);
        if (caller.User is not { } user || caller.UserKey is not { } userKey) throw new ApiException(StatusCodes.Status403Forbidden, NoViewSentence);
        if (!CalendarViewState.IsView(body.View)) throw ApiErrors.BadRequest($"A calendar's view is {CalendarViewState.Week} or {CalendarViewState.Month}.");
        var view = new CalendarViewState(body.View, body.Grid);
        try
        {
            await settings.SetViewAsync(user.Id, userKey, id, view, ct);
        }
        catch (CalendarViewConflictException e)
        {
            throw new ApiException(StatusCodes.Status409Conflict, e.Message);
        }

        return TypedResults.Ok(view);
    }

    /// <summary>The id of the caller's vault <paramref name="vault"/>; a vault she doesn't have, or that her session
    /// hides, is the same 404.</summary>
    private static async Task<string> VaultIdAsync(string vault, INoteStore store, RequestCaller caller, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        await NoteApi.RequireVaultAsync(store, root, ct);
        return (await caller.OpenVaultsAsync(ct)).FirstOrDefault(v => v.Name == root.Segments[0])?.Id
            ?? throw ApiErrors.NotFound($"There is no vault '{root.Segments[0]}'.");
    }
}
