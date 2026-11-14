// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;
using Kumbu.Server.Auth;

namespace Kumbu.Server.Accounts;

/// <summary>
/// The zone of the request's caller (calendar spec §3): the zone her user chose, for her sessions and her agents' keys and
/// connections alike, so "today" means the same for both; else, and for the dev token, the server's. Read once per request.
/// </summary>
public sealed class AccountCallerZone(RequestCaller caller, CalendarSettings settings, ServerZone server) : ICallerZone
{
    private Task<TimeZoneInfo>? _zone;

    public Task<TimeZoneInfo> ZoneAsync(CancellationToken ct) => _zone ??= ResolveAsync(ct);

    private async Task<TimeZoneInfo> ResolveAsync(CancellationToken ct) =>
        caller.User is { } user && Zones.Find(await settings.ZoneAsync(user.Id, ct)) is { } zone ? zone : await server.ZoneAsync(ct);
}
