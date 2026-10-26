// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Sync;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary>A note's path from the root of the vault tree and its version, as <c>GET /api/notes</c> gives it.</summary>
public sealed record VersionEntry(string Path, string Version);

/// <summary>
/// A vault's versions (sync spec §5). <c>full</c>: every note of the vault is in <c>notes</c>; false only for an
/// answer to <c>since</c>, which holds the paths touched since <c>since</c> minus five minutes, with their current
/// version in <c>notes</c> or, when there is no note at the path now, in <c>deleted</c>. <c>cursor</c>: the server's
/// time before the answer was read, to send back as <c>since</c>. <c>state</c>: a hash over the whole vault's paths,
/// versions and damaged count, also the <c>ETag</c>. <c>damaged</c>: stored notes that don't open (their paths are
/// sealed with them, so they can't be listed; a touched path whose note doesn't open is in neither list). Both lists in path order.
/// </summary>
public sealed record VersionsView(bool Full, string Cursor, string State, int Damaged, IReadOnlyList<VersionEntry> Notes, IReadOnlyList<string> Deleted);

/// <summary>A ticket for one connection to <c>GET /api/ws</c>, until <c>expiresAt</c> (30 seconds).</summary>
public sealed record WsTicketView(string Ticket, DateTime ExpiresAt);

/// <summary>What the Obsidian plugin syncs against: the version list, and (with the hints) asking only for what changed.</summary>
public static class SyncApi
{
    /// <summary>A <c>since</c> answer reaches back this much further, for slack.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    /// <summary>An older cursor gets the full list: the trash keeps a deletion for 30 days.</summary>
    public static readonly TimeSpan MaxCursorAge = TimeSpan.FromDays(25);

    public static void MapSyncApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter);
        api.MapGet("/vaults/{vault}/versions", VersionsAsync).Produces(StatusCodes.Status304NotModified).Errors(400, 401, 404, 503);
        api.MapPost("/ws-ticket", TicketAsync).Errors(401, 403, 503);
        // The ticket is the credential: no cookie or header is involved, so neither the caller nor the Origin check applies.
        app.MapGet("/api/ws", SocketAsync).AllowAnonymous().Produces(StatusCodes.Status101SwitchingProtocols).Errors(400, 401);
    }

    /// <summary>API keys only: a session has the web app, which polls; the dev token and connections don't sync.</summary>
    private static async Task<Ok<WsTicketView>> TicketAsync(RequestCaller caller, SyncTickets tickets, CancellationToken ct)
    {
        if (caller.Kind != CallerKind.ApiKey || caller.Credential is not ApiKeyCredential key)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "Only an API key gets a WebSocket ticket.");
        }

        var vaults = await caller.OpenVaultsAsync(ct);
        var (ticket, expiresAt) = tickets.Issue(key.KeyId, [.. vaults.Select(v => (v.Id, v.Name))]);
        return TypedResults.Ok(new WsTicketView(ticket, expiresAt));
    }

    /// <summary>The upgrade; 401 for an unknown, used or expired ticket. Hints go out until the socket closes.</summary>
    private static async Task<IResult> SocketAsync(HttpContext http, string? ticket, SyncTickets tickets, SyncHub hub, ApiKeys keys)
    {
        if (!http.WebSockets.IsWebSocketRequest) return ApiErrors.Result(StatusCodes.Status400BadRequest, "This is a WebSocket; connect with an upgrade and a ticket from POST /api/ws-ticket.");
        // The key is asked again: revoked or expired in the seconds since the ticket was made, it gets no socket.
        if (ticket is null || tickets.Take(ticket) is not { } grant || !await keys.IsActiveAsync(grant.KeyId, http.RequestAborted))
        {
            return ApiErrors.Result(StatusCodes.Status401Unauthorized, "This ticket is not valid (any more); ask for a new one.");
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        await hub.RunAsync(socket, grant, http.RequestAborted);
        return Results.Empty;
    }

    /// <summary><c>since</c>: a <c>cursor</c> of an earlier answer. Without it, <c>If-None-Match</c> with the last
    /// <c>state</c> answers 304 while nothing changed.</summary>
    private static async Task<Results<Ok<VersionsView>, StatusCodeHttpResult>> VersionsAsync(
        string vault, string? since, HttpContext http, EncryptedNoteStore store, VaultCatalog catalog, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        var unlocked = (await store.UnlockedVaultsAsync(ct)).FirstOrDefault(v => v.Name == root.Segments[0])
            ?? throw ApiErrors.NotFound($"There is no vault '{root.Segments[0]}'.");
        if (unlocked.Pending) throw VaultBusyException.Being();
        var now = catalog.Now;
        unlocked.Touch(now);

        // Taken before the snapshot: whatever commits after it is recorded at or after the cursor.
        var cursor = now.ToString("O", CultureInfo.InvariantCulture);
        var changedAt = unlocked.ChangedAt;
        var notes = unlocked.Notes;
        var damaged = unlocked.DamagedPathIds.Count;
        var state = State(notes.Values.Select(n => n.Note), damaged);
        http.Response.Headers.ETag = $"\"{state}\"";

        var from = Since(since, now, unlocked.Horizon);
        if (from is null)
        {
            if (since is null && http.Request.Headers.IfNoneMatch.Contains($"\"{state}\"")) return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            var all = notes.Values.Select(n => n.Note).OrderBy(n => n.Path.Value, TextOrder.Default).Select(n => new VersionEntry(n.Path.Value, n.Version)).ToList();
            return TypedResults.Ok(new VersionsView(true, cursor, state, damaged, all, []));
        }

        var touched = changedAt.Where(c => c.Value >= from).Select(c => c.Key).OrderBy(p => p.Value, TextOrder.Default).ToList();
        var listed = new List<VersionEntry>();
        var deleted = new List<string>();
        foreach (var path in touched)
        {
            if (notes.TryGetValue(path, out var entry)) listed.Add(new VersionEntry(path.Value, entry.Note.Version));
            // A note that doesn't open isn't gone: the client mustn't trash its copy. It is counted in damaged.
            else if (!unlocked.IsDamaged(path)) deleted.Add(path.Value);
        }

        return TypedResults.Ok(new VersionsView(false, cursor, state, damaged, listed, deleted));
    }

    /// <summary>
    /// Where a <c>since</c> answer starts, or null for the full list: no cursor, one that doesn't parse, one in the
    /// future or older than <see cref="MaxCursorAge"/>, or one from before <paramref name="horizon"/>, which this
    /// process can't answer for.
    /// </summary>
    private static DateTime? Since(string? since, DateTime now, DateTime horizon)
    {
        if (string.IsNullOrEmpty(since) || !DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cursor)) return null;
        if (cursor.Kind != DateTimeKind.Utc) return null;
        if (cursor > now || now - cursor > MaxCursorAge || cursor < horizon) return null;
        return cursor - Overlap;
    }

    /// <summary>The first 16 hex characters of SHA-256 over "path\tversion\n" per note in ordinal order, then "damaged\tn\n".</summary>
    private static string State(IEnumerable<Kumbu.Core.Notes.StoredNote> notes, int damaged)
    {
        var text = new StringBuilder();
        foreach (var n in notes.OrderBy(n => n.Path.Value, StringComparer.Ordinal)) text.Append(n.Path.Value).Append('\t').Append(n.Version).Append('\n');
        text.Append("damaged\t").Append(damaged.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }
}
