// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;
using Kumbu.Core.Sh;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Publishing;

/// <summary><c>password</c>: the owner's, as for an invitation; without it the password window must be open.
/// <c>depth</c> (0 to 5), <c>without</c> (notes of the walk left out, paths in the vault) and <c>expected</c> (the notes
/// the preview showed, paths in the vault): Publish 2.0. Past depth 0 <c>expected</c> is required (400 without it), and a
/// walk that takes other notes publishes nothing (409).</summary>
public sealed record PublishRequest(string? Password = null, int Depth = 0, string[]? Without = null, string[]? Expected = null);

/// <summary>An update's walk: a depth or notes left out other than the publication's own, and the notes the preview
/// showed, required when the publication or the new depth goes past depth 0.</summary>
public sealed record UpdateRequest(int? Depth = null, string[]? Without = null, string[]? Expected = null);

/// <summary>A note of a publication's preview, its path in the vault and the depth it was first reached at.</summary>
public sealed record PreviewNoteView(string Path, string Title, int Depth);

/// <summary>A note the walk left behind: <c>reason</c> is <c>unpublishable</c>, <c>behind</c> (reached only through a
/// note that stays out) or <c>without</c> (left out by the owner).</summary>
public sealed record PreviewLeftOutView(string Path, string Reason);

/// <summary>What a publication would take (Publish 2.0): the notes, the start first; those left behind; the embeds
/// that become plain text; the images' size; past the note limit or the size limit.</summary>
public sealed record PublicationPreviewView(PreviewNoteView[] Notes, PreviewLeftOutView[] LeftOut, string[] Plain, long Bytes, bool TooMany, bool TooLarge);

/// <summary>The 409 of a publication whose notes changed since the preview: nothing went out; the walk as it is now.</summary>
public sealed record BundleChangedView(string Message, PublicationPreviewView Preview);

/// <summary>A note published inside a bundle, and the bundle's start note; for a member, only the path.</summary>
public sealed record BundledView(string Path, string? Start);

/// <summary>The copy just made: <c>link</c> holds the code in its fragment (an update gives the same link);
/// <c>plain</c>, the embeds that became plain text.</summary>
public sealed record PublishedView(string Link, DateTime SnapshotAt, string[] Plain, string[]? Notes = null, string[]? Added = null, string[]? Removed = null);

/// <summary>A note of a publication's copy and the version of it the copy was made from.</summary>
public sealed record CopiedNoteView(string Path, string Version);

/// <summary>A published note. For a member only <c>path</c>, the rest null (publish spec §3.10); <c>link</c> only on
/// the owner's read of the one note.</summary>
public sealed record PublicationView(
    string Path, DateTime? SnapshotAt, bool? Older, DateTime? ChangedAt, string? PublishedBy, string? Link, int? Notes = null, int? Depth = null, string[]? Without = null,
    CopiedNoteView[]? Copied = null);

/// <summary>A vault's publications and, for its owner, its unpublishable notes and folders.</summary>
public sealed record VaultPublicationsView(PublicationView[] Publications, string[] Unpublishable, BundledView[]? Bundled = null);

/// <summary>A publication of one of the owner's own vaults, for the overview; never the link. Its walk (<c>notes</c>,
/// <c>depth</c>, <c>without</c>) as on the note's own read, so that Update copy can show a bundle's list from there.</summary>
public sealed record PublicationOverviewView(
    string Path, DateTime SnapshotAt, bool Older, DateTime? ChangedAt, string PublishedBy, int? Notes = null, int? Depth = null, string[]? Without = null,
    CopiedNoteView[]? Copied = null);

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
        api.MapGet("/publication-previews/{vault}/{**path}", PreviewAsync).Errors(400, 401, 403, 404, 503);
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
                .Select(s => new PublicationOverviewView(
                    s.Note.Value, s.SnapshotAt!.Value, s.Older!.Value, s.ChangedAt, s.PublishedBy!, s.Notes, s.Depth, s.Without is null ? null : [.. s.Without.Select(w => w.Value)], Copied(s))));
        }

        return TypedResults.Ok(views.OrderBy(v => v.Path, StringComparer.Ordinal).ToArray());
    }

    private static async Task<Ok<VaultPublicationsView>> VaultAsync(string vault, Publications publications, EncryptedNoteStore store, CancellationToken ct)
    {
        var access = await AccessAsync(store, vault, ct);
        var states = await publications.ListAsync(access, store, VaultPath.Root.Child(access.Name), null, ct);
        var marks = await publications.MarksAsync(access, ct);
        var bundled = await publications.BundledAsync(access, VaultPath.Root.Child(access.Name), ct);
        return TypedResults.Ok(new VaultPublicationsView([.. states.Select(View)], [.. marks.Select(m => m.Value)], [.. bundled.Select(b => new BundledView(b.Note.Value, b.Start?.Value))]));
    }

    /// <summary>The one read that carries the link; <see cref="Publications"/> gives it to the owner alone.</summary>
    private static async Task<Ok<PublicationView>> ReadAsync(
        string vault, string? path, HttpContext http, Publications publications, EncryptedNoteStore store, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var (access, note) = await NoteAsync(store, vault, path, ct);
        var state = await publications.StateAsync(access, store, note, Publications.BaseUrl(http, options.Value), ct) ?? throw NotPublished(note);
        return TypedResults.Ok(View(state));
    }

    /// <summary>What publishing the note to <paramref name="depth"/> without <paramref name="without"/> would take; nothing
    /// is written (Publish 2.0 decision 2).</summary>
    private static async Task<Ok<PublicationPreviewView>> PreviewAsync(
        string vault, string? path, Publications publications, EncryptedNoteStore store, HeavyCallGate gate, RequestCaller caller, CancellationToken ct,
        int depth = 0, string[]? without = null)
    {
        var (access, note) = await NoteAsync(store, vault, path, ct);
        var leftOut = Paths(vault, without);
        await publications.RequireInWalkAsync(access, store, note, depth, leftOut, ct);
        using var place = await gate.StartAsync(caller.User?.Id ?? "", ct);
        return TypedResults.Ok(View(await publications.PreviewAsync(access, store, note, depth, leftOut, ct)));
    }

    /// <summary>The password is checked as an invitation's, before the note is read; given, it opens the window. The
    /// switch and a member are refused first, as <see cref="Publications"/> would: no prompt, no password check spent.</summary>
    private static async Task<Results<Created<PublishedView>, Conflict<BundleChangedView>>> PublishAsync(
        string vault, string? path, PublishRequest body, HttpContext http, RequestCaller caller, UserCatalog users, Sessions sessions, AccountLimits limits,
        Publications publications, EncryptedNoteStore store, IOptions<KumbuOptions> options, HeavyCallGate gate, CancellationToken ct)
    {
        var (access, note) = await NoteAsync(store, vault, path, ct);
        if (!publications.Enabled) throw new PublishRefusedException("publishing is switched off on this server", ShellErrorKind.Refused, Refusals.PublishingOff);
        if (!Owned(access)) throw new PublishRefusedException($"only the owner of {access.Name} can publish its notes", ShellErrorKind.Refused, Refusals.NotOwner);
        if (body.Depth > 0 && body.Expected is null) throw Unseen();
        if (body.Password is null) Security.RequirePasswordWindow(caller);
        else
        {
            if (limits.PasswordCheck(caller.User!.Username) is { } retryAfter) throw ApiErrors.TooManyPasswordChecks(retryAfter);
            if (!await users.CheckPasswordAsync(caller.User!.Id, caller.UserKey!, body.Password, ct)) throw new WrongPasswordException("The password is wrong.");
            await AccountApi.PasswordGivenAsync(http, sessions, ct);
        }

        var (by, byRef) = Writer(caller, access);
        var without = Paths(vault, body.Without);
        await publications.RequireInWalkAsync(access, store, note, body.Depth, without, ct);
        using var place = await gate.StartAsync(caller.User!.Id, ct);
        PublishedNote published;
        try
        {
            published = await publications.PublishAsync(
                access, store, note, caller.User!.Id, by, byRef, Publications.BaseUrl(http, options.Value), ct, body.Depth, without, Expected(vault, body.Expected));
        }
        catch (BundleChangedException e)
        {
            return TypedResults.Conflict(Changed(e));
        }
        // The shell's sentence names `publish update`; the web app's button is Update copy.
        catch (PublishRefusedException e) when (e.Kind == ShellErrorKind.Conflict && e.Message.StartsWith($"{note} is already published", StringComparison.Ordinal))
        {
            throw new ApiException(StatusCodes.Status409Conflict, $"{note.Value} is already published; Update copy makes a new copy under the same link.");
        }

        return TypedResults.Created((string?)null, View(published));
    }

    private static async Task<Results<Ok<PublishedView>, Conflict<BundleChangedView>>> UpdateAsync(
        string vault, string? path, HttpContext http, RequestCaller caller, Publications publications, EncryptedNoteStore store,
        IOptions<KumbuOptions> options, HeavyCallGate gate, CancellationToken ct)
    {
        var (access, note) = await NoteAsync(store, vault, path, ct);
        var (by, byRef) = Writer(caller, access);
        var body = await UpdateBodyAsync(http, ct);
        var without = body?.Without is null ? null : Paths(vault, body.Without);
        // A member's state has no depth, and none is there when the note isn't published: Publications refuses those as before.
        var state = await publications.StateAsync(access, store, note, null, ct);
        var depth = body?.Depth ?? state?.Depth ?? 0;
        if (body?.Expected is null && (depth > 0 || state?.Depth > 0)) throw Unseen();
        if (without is not null) await publications.RequireInWalkAsync(access, store, note, depth, without, ct);
        using var place = await gate.StartAsync(caller.User?.Id ?? "", ct);
        try
        {
            return TypedResults.Ok(View(await publications.UpdateAsync(
                access, store, note, by, byRef, Publications.BaseUrl(http, options.Value), ct,
                body?.Depth, without, Expected(vault, body?.Expected))));
        }
        catch (BundleChangedException e)
        {
            return TypedResults.Conflict(Changed(e));
        }
    }

    /// <summary>The optional body of an update: none, as before Publish 2.0, keeps the publication's walk. Read by hand,
    /// as a body parameter would make routing refuse a <c>PUT</c> without one.</summary>
    private static async Task<UpdateRequest?> UpdateBodyAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength is null or 0 && !http.Request.HasJsonContentType()) return null;
        if (!http.Request.HasJsonContentType()) throw new ApiException(StatusCodes.Status415UnsupportedMediaType, "Send the body as JSON (Content-Type: application/json).");
        if (http.Request.ContentLength > 64 * 1024) throw new ApiException(StatusCodes.Status413PayloadTooLarge, "The request body is too large.");
        try
        {
            return await http.Request.ReadFromJsonAsync<UpdateRequest>(ct);
        }
        catch (System.Text.Json.JsonException)
        {
            throw ApiErrors.BadRequest("The request body is missing, not JSON, or lacks a required field.");
        }
    }

    /// <summary>Paths in the vault (<c>Plan.md</c>, as the API's other paths) as note paths; at most as many as a
    /// publication may take.</summary>
    private static List<VaultPath> Paths(string vault, string[]? paths)
    {
        if (paths is null) return [];
        if (paths.Length > MaxListed) throw ApiErrors.BadRequest($"At most {MaxListed} notes can be named.");
        return [.. paths.Select(p => ApiPaths.Parse(vault, p, note: true))];
    }

    private static List<string>? Expected(string vault, string[]? paths) => paths is null ? null : [.. Paths(vault, paths).Select(p => p.Value)];

    private const int MaxListed = 1000;

    /// <summary>Nothing unseen goes out (decision 2): over HTTP a walk past the note alone needs the list it was checked
    /// against. <c>sh publish</c> keeps its own rule, as its agent sees the list in the answer.</summary>
    private static ApiException Unseen() =>
        ApiErrors.BadRequest("Send expected, the notes the list showed: a note goes out with its linked notes only as they were seen.");

    private static BundleChangedView Changed(BundleChangedException e) =>
        new("Something changed in the vault since the list was shown: check the list again.", View(e.Now));

    private static PublicationPreviewView View(BundlePreview p) => new(
        [.. p.Notes.Select(n => new PreviewNoteView(n.Path.Value, n.Title, n.Depth))],
        [.. p.LeftOut.Select(l => new PreviewLeftOutView(l.Path.Value, l.Reason))],
        [.. p.Plain], p.Bytes, p.TooMany, p.TooLarge);

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
        new(s.Note.Value, s.SnapshotAt, s.Older, s.ChangedAt, s.PublishedBy, s.Link, s.Notes, s.Depth, s.Without is null ? null : [.. s.Without.Select(w => w.Value)], Copied(s));

    private static CopiedNoteView[]? Copied(PublicationState s) => s.Copied is null ? null : [.. s.Copied.Select(c => new CopiedNoteView(c.Note.Value, c.Version))];

    private static PublishedView View(PublishedNote p) => new(
        p.Link, p.SnapshotAt, [.. p.Plain], p.Notes is null ? null : [.. p.Notes.Select(n => n.Path.Value)],
        p.Added is null ? null : [.. p.Added.Select(a => a.Value)], p.Removed is null ? null : [.. p.Removed.Select(r => r.Value)]);

    private static ApiException NoVault(string vault) => ApiErrors.NotFound($"You have no vault named '{vault}'.");

    private static ApiException NoPath(string vault) => ApiErrors.NotFound($"Name a note or folder in {vault}: the vault itself is neither.");

    private static ApiException NotPublished(VaultPath note) => ApiErrors.NotFound($"{note.Value} is not published.");
}
