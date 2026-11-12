// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Publishing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Skills;

/// <summary>A file of the copy against the new version: <c>added</c>, <c>removed</c>, <c>changed</c> or <c>same</c>; the hunks
/// of a unified diff for a text file, none for a binary one.</summary>
public sealed record SkillReviewFileView(string Path, string Change, string[] Hunks);

/// <summary>What Review shows: the <c>kumbu-source</c> Take would write, for a GitHub copy its repository, folder and
/// commit, the hash of the new version (what Take and Skip name), and every file of her copy against it.</summary>
public sealed record SkillReviewView(string Source, string? Repo, string? Path, string? Commit, string Hash, SkillReviewFileView[] Files);

/// <summary>Take or skip the version of the copy at <c>Path</c> whose hash Review named.</summary>
public sealed record SkillUpdateRequest(string Path, string Hash);

/// <summary>The copy after Take: its folder, and the origin it has now.</summary>
public sealed record SkillTakenView(string Path, string Source, string Hash);

/// <summary>
/// Review, Take update and Skip (skills spec §3.10, <see cref="SkillUpdateTaker"/>), for a session only: agents never take
/// updates, and no <c>sh</c> command does.
/// </summary>
public static class SkillUpdatesApi
{
    public static void MapSkillUpdatesApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/skills/updates").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        group.MapGet("/review", ReviewAsync).Errors(401, 403, 404, 409, 429, 502, 503);
        group.MapPost("/take", TakeAsync).Errors(401, 403, 404, 409, 429, 502, 503, 507);
        group.MapPost("/skip", SkipAsync).Errors(400, 401, 403, 404, 409, 503);
    }

    private static async Task<Ok<SkillReviewView>> ReviewAsync(
        [FromQuery] string? path, RequestCaller caller, EncryptedNoteStore store, NoteWriter writer, MarketplaceFetcher github, PublicationStore publishing,
        UserCatalog users, AccountLimits limits, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var (upstream, files) = await new SkillUpdateTaker(store, writer, github, publishing, users, limits, caller, options.Value.Skills.Marketplaces.Enabled, options.Value.Publish.Enabled).ReviewAsync(path, ct);
        return TypedResults.Ok(new SkillReviewView(
            upstream.Source, upstream.Repo?.ToString(), upstream.RepoPath, upstream.Commit, upstream.Hash,
            [.. files.Select(f => new SkillReviewFileView(f.Path, f.Change, [.. f.Hunks]))]));
    }

    private static async Task<Ok<SkillTakenView>> TakeAsync(
        SkillUpdateRequest body, RequestCaller caller, EncryptedNoteStore store, NoteWriter writer, MarketplaceFetcher github, PublicationStore publishing,
        UserCatalog users, AccountLimits limits, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var taken = await new SkillUpdateTaker(store, writer, github, publishing, users, limits, caller, options.Value.Skills.Marketplaces.Enabled, options.Value.Publish.Enabled).TakeAsync(body.Path, body.Hash, ct);
        return TypedResults.Ok(new SkillTakenView(body.Path.TrimEnd('/'), taken.Source, taken.Hash));
    }

    private static async Task<NoContent> SkipAsync(
        SkillUpdateRequest body, RequestCaller caller, EncryptedNoteStore store, NoteWriter writer, MarketplaceFetcher github, PublicationStore publishing,
        UserCatalog users, AccountLimits limits, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        await new SkillUpdateTaker(store, writer, github, publishing, users, limits, caller, options.Value.Skills.Marketplaces.Enabled, options.Value.Publish.Enabled).SkipAsync(body.Path, body.Hash, ct);
        return TypedResults.NoContent();
    }
}
