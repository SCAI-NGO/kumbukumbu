// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Sync;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary><c>label</c>: what the device list calls the browser ("Firefox on Linux"), one line of at most
/// <see cref="MirrorSlots.MaxLabelLength"/> characters.</summary>
public sealed record CreateMirrorSlotRequest(string? Label);

/// <summary><c>label</c>: the device list's new name for the slot, the same one line as <see cref="CreateMirrorSlotRequest"/>'s.</summary>
public sealed record RenameMirrorSlotRequest(string? Label);

/// <summary><c>token</c>: the one the slot was made with. <c>waiting</c>: the changes the browser has waiting to upload,
/// for the device list's warning before a removal.</summary>
public sealed record MirrorContactRequest(string? Token, int Waiting = 0);

/// <summary>A vault the browser may mirror now. <c>tag</c> changes when the vault is re-keyed: the browser purges a
/// vault it mirrored under another tag. <c>canWrite</c>: false for a read-only member, whose mirror queues no write.
/// <c>pending</c>: being re-keyed now; nothing syncs until it ends.</summary>
public sealed record MirrorVaultView(string Id, string Tag, bool CanWrite, bool Pending);

/// <summary>The slot's mirror key (Base64Url, AES-256) and the vaults the browser may mirror: every one of its session
/// but the discreet ones. A vault it mirrors that isn't listed is purged. <c>token</c>, only when the slot is made: kept
/// by the browser in the clear and handed in at every contact, never shown again.</summary>
public sealed record MirrorContactView(
    string Id, string Key, IReadOnlyList<MirrorVaultView> Vaults,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Token = null);

/// <summary>A slot in the device list: never its key or token. <c>waiting</c>: the changes the browser had waiting at its
/// last contact, lost if the slot is removed before they upload. <c>current</c>: this browser's.</summary>
public sealed record MirrorSlotView(string Id, string Label, DateTime CreatedAt, DateTime? ContactAt, int Waiting, bool Current);

/// <summary>
/// The offline mirror's key slots (offline mode design §4.2, §4.4), for a session only: an API key or a connected app
/// gets the 403 of <see cref="SessionOnlyMetadata"/>, and the Origin check guards every POST and DELETE. The wipe
/// signals: a contact answers 404 with code <see cref="ApiErrors.MirrorWipe"/> when the slot is gone (purge the whole
/// mirror), and the vaults it lists say which mirrors to keep; the sync socket's <c>{"type":"mirror"}</c> asks a tab to
/// make contact now.
/// </summary>
public static class MirrorSlotsApi
{
    public static void MapMirrorSlotsApi(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/api/account/mirror-slots").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        account.MapGet("", ListAsync).Errors(401, 403);
        account.MapPost("", CreateAsync).Errors(400, 401, 403, 409);
        // Made on every reconnect, so like the web app's other background calls it keeps no idle session alive.
        account.MapPost("/contact", ContactAsync).WithMetadata(BackgroundMetadata.Instance).Errors(400, 401, 403, 404);
        account.MapPut("/{id}/label", RenameAsync).Errors(400, 401, 403, 404);
        account.MapDelete("/{id}", DeleteAsync).Errors(401, 403, 404);
    }

    private static async Task<Ok<MirrorSlotView[]>> ListAsync(HttpContext http, RequestCaller caller, MirrorSlots slots, CancellationToken ct) =>
        TypedResults.Ok((await slots.ListAsync(caller.User!.Id, Subject(http), ct))
            .Select(s => new MirrorSlotView(s.Id, s.Label, s.CreatedAt, s.ContactAt, s.Waiting, s.Current)).ToArray());

    private static async Task<Created<MirrorContactView>> CreateAsync(
        CreateMirrorSlotRequest body, HttpContext http, RequestCaller caller, MirrorSlots slots, CancellationToken ct)
    {
        var label = Label(body.Label);
        try
        {
            var (id, token, key) = await slots.CreateAsync(caller.User!.Id, caller.UserKey!, label, Subject(http), ct);
            var decoded = MirrorSlots.DecodeToken(token)!;
            return TypedResults.Created((string?)null, new MirrorContactView(id, Encode(key), await VaultsAsync(caller, decoded, ct), token));
        }
        catch (MirrorSlotLimitException e)
        {
            throw new ApiException(StatusCodes.Status409Conflict, e.Message);
        }
    }

    private static async Task<Ok<MirrorContactView>> ContactAsync(
        MirrorContactRequest body, HttpContext http, RequestCaller caller, MirrorSlots slots, CancellationToken ct)
    {
        if (MirrorSlots.DecodeToken(body.Token) is not { } token) throw ApiErrors.BadRequest("This is not a mirror slot's token.");
        if (body.Waiting is < 0 or > MirrorSlots.MaxWaiting) throw ApiErrors.BadRequest($"The changes waiting are 0 to {MirrorSlots.MaxWaiting}.");
        var contact = await slots.ContactAsync(caller.User!.Id, caller.UserKey!, token, body.Waiting, Subject(http), ct)
            ?? throw ApiErrors.MirrorGone(StatusCodes.Status404NotFound);
        return TypedResults.Ok(new MirrorContactView(contact.Id, Encode(contact.Key), await VaultsAsync(caller, token, ct)));
    }

    /// <summary>Only the name changes: the slot keeps its key, token and vaults, so the device's copy stays as it is.</summary>
    private static async Task<NoContent> RenameAsync(string id, RenameMirrorSlotRequest body, RequestCaller caller, MirrorSlots slots, CancellationToken ct) =>
        await slots.RenameAsync(caller.User!.Id, id, Label(body.Label), ct) ? TypedResults.NoContent() : throw ApiErrors.NotFound("There is no such offline copy.");

    private static async Task<Results<NoContent, NotFound<ApiError>>> DeleteAsync(
        string id, RequestCaller caller, MirrorSlots slots, SyncHub hub, CancellationToken ct)
    {
        if (!await slots.DeleteAsync(caller.User!.Id, id, ct)) return TypedResults.NotFound(new ApiError("There is no such offline copy."));
        await hub.MirrorChangedAsync(caller.User!.Id, CancellationToken.None);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The vaults the browser may mirror: those the session opens, but never a discreet one (§4.3), shown or not, nor any
    /// while the discreet list doesn't open (every vault but Skills counts as discreet then). A vault the session hides
    /// isn't opened, so it isn't among them either.
    /// </summary>
    private static async Task<IReadOnlyList<MirrorVaultView>> VaultsAsync(RequestCaller caller, byte[] token, CancellationToken ct)
    {
        var open = await caller.OpenVaultsAsync(ct);
        var session = caller.Session!;
        return [.. open
            .Where(v => !session.IsDiscreet(v.Id) && !v.Vault.Discreet)
            .Select(v => new MirrorVaultView(v.Id, MirrorSlots.VaultTag(token, v.Id, v.Vault.KeyId), v.Level >= AccessLevel.Write, v.Vault.Pending))];
    }

    /// <summary>A slot's label, trimmed: one line of 1 to <see cref="MirrorSlots.MaxLabelLength"/> characters, else a 400.</summary>
    private static string Label(string? label)
    {
        label = label?.Trim();
        if (string.IsNullOrEmpty(label) || label.Length > MirrorSlots.MaxLabelLength || label.Any(char.IsControl))
        {
            throw ApiErrors.BadRequest($"A label is one line of 1 to {MirrorSlots.MaxLabelLength} characters.");
        }

        return label;
    }

    /// <summary>The asking session, as <see cref="Sessions.SubjectOf"/> names it.</summary>
    private static string? Subject(HttpContext http) =>
        http.Request.Cookies.TryGetValue(CallerMiddleware.SessionCookie, out var secret) ? Sessions.SubjectOf(secret) : null;

    private static string Encode(byte[] key) => System.Buffers.Text.Base64Url.EncodeToString(key);
}
