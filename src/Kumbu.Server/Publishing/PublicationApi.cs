// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Publishing;

/// <summary><c>password</c>: the owner's, as for an invitation; without it the password window must be open.</summary>
public sealed record PublishRequest(string? Password = null);

/// <summary>The copy just made: <c>link</c> holds the code in its fragment (an update gives the same link);
/// <c>plain</c>, the embeds that became plain text.</summary>
public sealed record PublishedView(string Link, DateTime SnapshotAt, string[] Plain);

/// <summary>A published note. For a member only <c>path</c>, the rest null (publish spec §3.10); <c>link</c> only on
/// the owner's read of the one note.</summary>
public sealed record PublicationView(string Path, DateTime? SnapshotAt, bool? Older, DateTime? ChangedAt, string? PublishedBy, string? Link);

/// <summary>A vault's publications and, for its owner, its unpublishable notes and folders.</summary>
public sealed record VaultPublicationsView(PublicationView[] Publications, string[] Unpublishable);

/// <summary>A publication of one of the owner's own vaults, for the overview; never the link.</summary>
public sealed record PublicationOverviewView(string Path, DateTime SnapshotAt, bool Older, DateTime? ChangedAt, string PublishedBy);

/// <summary>
/// The owner's publications in the REST API (publish spec §3.5, §3.11): sessions only, over <see cref="Publications"/>,
/// which makes every check of owner, right, mark and switch. Publishing takes the password or the open window
/// (decision 9); updating the window; withdrawing and the marks neither, and they also work while publishing is
/// switched off, as they only reduce what is out (decision 20). An empty path, the vault itself, is no note: 404.
/// </summary>
public static class PublicationApi
{
    public static void MapPublicationApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        api.MapGet("/publications", OverviewAsync).Errors(401, 403, 503);
        api.MapGet("/vaults/{vault}/publications", VaultAsync).Errors(401, 403, 404, 503);
        api.MapGet("/publications/{vault}/{**path}", ReadAsync).Errors(400, 401, 403, 404, 503);
        api.MapPost("/publications/{vault}/{**path}", PublishAsync).Errors(400, 401, 403, 404, 409, 429, 503);
        api.MapPut("/publications/{vault}/{**path}", UpdateAsync).WithMetadata(PasswordWindowMetadata.Instance).Errors(400, 401, 403, 404, 409, 503);
        api.MapDelete("/publications/{vault}/{**path}", WithdrawAsync).Errors(400, 401, 403, 404, 409, 503);
        api.MapPut("/unpublishable/{vault}/{**path}", MarkAsync).Errors(400, 401, 403, 404, 409, 503);
        api.MapDelete("/unpublishable/{vault}/{**path}", UnmarkAsync).Errors(400, 401, 403, 404, 409, 503);
    }

    private static async Task<Ok<PublicationOverviewView[]>> OverviewAsync(Publications publications, EncryptedNoteStore store, CancellationToken ct)
    {
        var views = new List<PublicationOverviewView>();
        foreach (var access in (await store.UnlockedVaultsAsync(ct)).Where(Owned))
        {
            views.AddRange((await publications.ListAsync(access, store, VaultPath.Root.Child(access.Name), null, ct))
                .Select(s => new PublicationOverviewView(s.Note.Value, s.SnapshotAt!.Value, s.Older!.Value, s.ChangedAt, s.PublishedBy!)));
        }

        return TypedResults.Ok(views.OrderBy(v => v.Path, StringComparer.Ordinal).ToArray());
    }

    private static async Task<Ok<VaultPublicationsView>> VaultAsync(string vault, Publications publications, EncryptedNoteStore store, CancellationToken ct)
    {
        var access = await AccessAsync(store, vault, ct);
        var states = await publications.ListAsync(access, store, VaultPath.Root.Child(access.Name), null, ct);
        var marks = await publications.MarksAsync(access, ct);
        return TypedResults.Ok(new VaultPublicationsView([.. states.Select(View)], [.. marks.Select(m => m.Value)]));
    }

    /// <summary>The one read that carries the link; <see cref="Publications"/> gives it to the owner alone.</summary>
    private static async Task<Ok<PublicationView>> ReadAsync(
        string vault, string? path, HttpContext http, Publications publications, EncryptedNoteStore store, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var (access, note) = await NoteAsync(store, vault, path, ct);
        var state = await publications.StateAsync(access, store, note, Publications.BaseUrl(http, options.Value), ct) ?? throw NotPublished(note);
        return TypedResults.Ok(View(state));
    }

    /// <summary>The password is checked as an invitation's, before the note is read; given, it opens the window.</summary>
    private static async Task<Created<PublishedView>> PublishAsync(
        string vault, string? path, PublishRequest body, HttpContext http, RequestCaller caller, UserCatalog users, Sessions sessions, AccountLimits limits,
        Publications publications, EncryptedNoteStore store, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var (access, note) = await NoteAsync(store, vault, path, ct);
        if (body.Password is null) Security.RequirePasswordWindow(caller);
        else
        {
            if (limits.PasswordCheck(caller.User!.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
            if (!await users.CheckPasswordAsync(caller.User!.Id, caller.UserKey!, body.Password, ct)) throw new WrongPasswordException("The password is wrong.");
            await AccountApi.PasswordGivenAsync(http, sessions, ct);
        }

        var (by, byRef) = Writer(caller, access);
        var published = await publications.PublishAsync(access, store, note, caller.User!.Id, by, byRef, Publications.BaseUrl(http, options.Value), ct);
        return TypedResults.Created((string?)null, View(published));
    }

    private static async Task<Ok<PublishedView>> UpdateAsync(
        string vault, string? path, HttpContext http, RequestCaller caller, Publications publications, EncryptedNoteStore store, IOptions<KumbuOptions> options,
        CancellationToken ct)
    {
        var (access, note) = await NoteAsync(store, vault, path, ct);
        var (by, byRef) = Writer(caller, access);
        return TypedResults.Ok(View(await publications.UpdateAsync(access, store, note, by, byRef, Publications.BaseUrl(http, options.Value), ct)));
    }

    private static async Task<NoContent> WithdrawAsync(
        string vault, string? path, RequestCaller caller, Publications publications, EncryptedNoteStore store, CancellationToken ct)
    {
        var (access, note) = await NoteAsync(store, vault, path, ct);
        var (by, byRef) = Writer(caller, access);
        await publications.WithdrawAsync(access, store, note, by, byRef, ct);
        return TypedResults.NoContent();
    }

    private static Task<NoContent> MarkAsync(
        string vault, string? path, RequestCaller caller, Publications publications, EncryptedNoteStore store, CancellationToken ct) =>
        SetMarkAsync(vault, path, marked: true, caller, publications, store, ct);

    private static Task<NoContent> UnmarkAsync(
        string vault, string? path, RequestCaller caller, Publications publications, EncryptedNoteStore store, CancellationToken ct) =>
        SetMarkAsync(vault, path, marked: false, caller, publications, store, ct);

    /// <summary>The owner's alone: a member's alias answers 404, as on the other owner-only endpoints. A note's, a
    /// file's or a folder's path.</summary>
    private static async Task<NoContent> SetMarkAsync(
        string vault, string? path, bool marked, RequestCaller caller, Publications publications, EncryptedNoteStore store, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path)) throw NoPath(vault);
        var target = ApiPaths.Parse(vault, path, PathKind.Folder);
        var access = await AccessAsync(store, vault, ct);
        if (!Owned(access)) throw NoVault(vault);
        var (by, byRef) = Writer(caller, access);
        await publications.SetUnpublishableAsync(access, store, target, marked, by, byRef, ct);
        return TypedResults.NoContent();
    }

    private static bool Owned(VaultAccess access) => access.IsOwner && access.Membership is null;

    private static async Task<VaultAccess> AccessAsync(EncryptedNoteStore store, string vault, CancellationToken ct) =>
        (await store.UnlockedVaultsAsync(ct)).FirstOrDefault(a => a.Name == vault) ?? throw NoVault(vault);

    /// <summary>The note and the caller's access to its vault. An empty path is checked first: <see cref="ApiPaths"/>
    /// would read it as the vault's root.</summary>
    private static async Task<(VaultAccess Access, VaultPath Note)> NoteAsync(EncryptedNoteStore store, string vault, string? path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path)) throw NoPath(vault);
        var note = ApiPaths.Parse(vault, path, note: true);
        return (await AccessAsync(store, vault, ct), note);
    }

    /// <summary>As <see cref="ShellPublisher"/>: a member is refused before anything is written, so her writer id isn't needed.</summary>
    private static (string By, WriterRef ByRef) Writer(RequestCaller caller, VaultAccess access) =>
        access.Membership is null ? (caller.WriterIn(access.Name), caller.ReferenceIn(access.Name)) : ("", caller.Reference);

    private static PublicationView View(PublicationState s) =>
        new(s.Note.Value, s.SnapshotAt, s.Older, s.ChangedAt, s.PublishedBy, s.Link);

    private static PublishedView View(PublishedNote p) => new(p.Link, p.SnapshotAt, [.. p.Plain]);

    private static ApiException NoVault(string vault) => ApiErrors.NotFound($"You have no vault named '{vault}'.");

    private static ApiException NoPath(string vault) => ApiErrors.NotFound($"Name a note or folder in {vault}: the vault itself is neither.");

    private static ApiException NotPublished(VaultPath note) => ApiErrors.NotFound($"{note.Value} is not published.");
}
