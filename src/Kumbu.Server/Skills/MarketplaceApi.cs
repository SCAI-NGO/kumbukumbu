// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Skills;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Publishing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Skills;

/// <summary>A marketplace of hers: its repository, <c>owner/repo</c>, and when she added it.</summary>
public sealed record MarketplaceView(string Repo, DateTime AddedAt);

/// <summary>Her marketplaces, and whether the admin has marketplaces on at all.</summary>
public sealed record MarketplacesView(bool Enabled, MarketplaceView[] Marketplaces);

/// <summary>The marketplace to add, as <c>owner/repo</c>.</summary>
public sealed record MarketplaceAddRequest(string Repo);

/// <summary>What a marketplace offers at <c>Commit</c> (<see cref="MarketplaceListing"/>).</summary>
public sealed record MarketplaceListingView(string Repo, string Commit, MarketplaceSkill[] Skills, MarketplacePlugin[] Plugins, string? Note);

/// <summary>A file of a skill Kumbu would store, by its path in the skill's folder, and its size in bytes.</summary>
public sealed record MarketplaceFileView(string Path, long Size);

/// <summary>What the install dialog shows (skills spec §3.9): the skill's <c>SKILL.md</c>, the files that would be stored, those
/// that would be skipped, whether it holds scripts, and the commit it would be taken from.</summary>
public sealed record MarketplaceSkillView(string Repo, string Path, string Commit, string Manifest, MarketplaceFileView[] Files, string[] Skipped, bool HasScripts);

/// <summary>Install the skill at <c>Path</c> of <c>Repo</c> at <c>Commit</c> into the vault <c>Vault</c>, under <c>Name</c>, by
/// default the folder's name in the repository.</summary>
public sealed record MarketplaceInstallRequest(string Repo, string Commit, string Path, string Vault, string? Name = null);

/// <summary>
/// The Marketplaces tab (skills spec §3.9, Decisions 13, 30, 33, 34), for a session only. Her list of marketplaces is kept
/// sealed in her vault Skills' record; adding one asks GitHub nothing. Browsing, previewing and installing ask GitHub only
/// as her request, each counted by <see cref="AccountLimits.GitHubFetch"/>; installing takes the commit the client was
/// shown, never the branch as it is now. With marketplaces switched off every route but the list answers 404 and
/// nothing is fetched.
/// </summary>
public static class MarketplaceApi
{
    /// <summary>The <see cref="ApiError.Type"/> of every route while marketplaces are switched off.</summary>
    public const string Off = "marketplaces-off";

    /// <summary>The answer of every route that would ask GitHub while marketplaces are switched off.</summary>
    internal static ApiException SwitchedOff() => new(StatusCodes.Status404NotFound, "Marketplaces are switched off on this server.", type: Off);

    /// <summary>The <see cref="ApiError.Type"/> of a user over her own limit of requests that ask GitHub.</summary>
    public const string Busy = "github-busy";

    public const string RateLimited = "github-rate-limited";

    public const string RateLimitedSentence = "GitHub allows 60 requests an hour without a token; try again later, or ask your admin to set a GitHub token.";

    /// <summary>How many marketplaces one user keeps, which bounds her vault Skills' record.</summary>
    public const int MaxMarketplaces = 100;

    public static void MapMarketplaceApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/skills").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        group.MapGet("/marketplaces", ListAsync).Errors(401, 403, 503);
        group.MapPost("/marketplaces", AddAsync).Errors(400, 401, 403, 404, 409, 503);
        group.MapDelete("/marketplaces/{owner}/{repo}", RemoveAsync).Errors(400, 401, 403, 404, 503);
        group.MapGet("/marketplaces/{owner}/{repo}", BrowseAsync).Errors(400, 401, 403, 404, 429, 502, 503);
        group.MapGet("/marketplaces/{owner}/{repo}/skill", PreviewAsync).Errors(400, 401, 403, 404, 429, 502, 503);
        group.MapPost("/install", InstallAsync).Errors(400, 401, 403, 404, 409, 429, 502, 503, 507);
    }

    private static async Task<Ok<MarketplacesView>> ListAsync(RequestCaller caller, PublicationStore publishing, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var record = await SkillsVaultAsync(caller, ct) is { } skills ? (await publishing.ReadAsync(null, skills.Vault, ct)).Record : PublishingRecord.Empty;
        return TypedResults.Ok(new MarketplacesView(options.Value.Skills.Marketplaces.Enabled, [.. record.Marketplaces.Select(m => new MarketplaceView(m.Repo, m.AddedAt))]));
    }

    private static async Task<Created<MarketplaceView>> AddAsync(
        MarketplaceAddRequest body, RequestCaller caller, PublicationStore publishing, IOptions<KumbuOptions> options, TimeProvider time, CancellationToken ct)
    {
        RequireOn(options);
        var repo = Repo(body.Repo);
        var skills = await SkillsVaultAsync(caller, ct) ?? throw NoSkillsVault();
        var entry = new MarketplaceEntry(repo.ToString(), time.GetUtcNow().UtcDateTime);
        await publishing.ChangeAsync(skills, r =>
        {
            if (r.Marketplaces.Any(m => Same(m.Repo, repo))) throw new ApiException(StatusCodes.Status409Conflict, $"{repo} is among your marketplaces already.");
            if (r.Marketplaces.Count >= MaxMarketplaces) throw new ApiException(StatusCodes.Status409Conflict, $"You have {MaxMarketplaces} marketplaces; remove one first.");
            return r with { Marketplaces = [.. r.Marketplaces, entry] };
        }, ct);
        return TypedResults.Created((string?)null, new MarketplaceView(entry.Repo, entry.AddedAt));
    }

    private static async Task<NoContent> RemoveAsync(string owner, string repo, RequestCaller caller, PublicationStore publishing, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        RequireOn(options);
        var name = Repo($"{owner}/{repo}");
        var skills = await SkillsVaultAsync(caller, ct) ?? throw NoSkillsVault();
        await publishing.ChangeAsync(skills, r => r.Marketplaces.Any(m => Same(m.Repo, name))
            ? r with { Marketplaces = [.. r.Marketplaces.Where(m => !Same(m.Repo, name))] }
            : throw ApiErrors.NotFound($"{name} isn't among your marketplaces."), ct);
        return TypedResults.NoContent();
    }

    /// <summary>The skills of the marketplace at the commit its default branch points to now.</summary>
    private static async Task<Ok<MarketplaceListingView>> BrowseAsync(
        string owner, string repo, RequestCaller caller, MarketplaceFetcher fetcher, AccountLimits limits, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        RequireOn(options);
        var name = Repo($"{owner}/{repo}");
        RequireFetch(caller, limits);
        var listing = await GitHubAsync(async () => await fetcher.ListingAsync(name, await fetcher.ResolveCommitAsync(name, ct), ct));
        return TypedResults.Ok(new MarketplaceListingView(name.ToString(), listing.Commit, [.. listing.Skills], [.. listing.Plugins], listing.Note));
    }

    private static async Task<Ok<MarketplaceSkillView>> PreviewAsync(
        string owner, string repo, [FromQuery] string? commit, [FromQuery] string? path, RequestCaller caller, MarketplaceFetcher fetcher, AccountLimits limits,
        IOptions<KumbuOptions> options, CancellationToken ct)
    {
        RequireOn(options);
        var (name, at, folder) = (Repo($"{owner}/{repo}"), Commit(commit), SkillPath(path));
        RequireFetch(caller, limits);
        var (_, files) = await SkillAsync(fetcher, name, at, folder, ct);
        var manifest = files.Files.FirstOrDefault(f => f.Path == SkillPaths.Manifest).Bytes;
        return TypedResults.Ok(new MarketplaceSkillView(
            name.ToString(), folder, at, manifest is null ? "" : Encoding.UTF8.GetString(manifest),
            [.. files.Files.Select(f => new MarketplaceFileView(f.Path, f.Bytes.Length))], [.. files.Skipped], SkillPageApi.HasScripts(files.Files.Select(f => f.Path))));
    }

    /// <summary>
    /// The skill at the commit the client names (the one it was shown), its stored files only, into her vault through her
    /// writers (rights, quota, history) under the listing's folder name or one she chose, never the manifest's. A skill
    /// the listing doesn't call installable is refused with its reason.
    /// </summary>
    private static async Task<Created<SkillCopiedView>> InstallAsync(
        MarketplaceInstallRequest body, RequestCaller caller, MarketplaceFetcher fetcher, AccountLimits limits, EncryptedNoteStore store, NoteWriter writer,
        PublicationStore publishing, IOptions<KumbuOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        RequireOn(options);
        var (name, at, folder) = (Repo(body.Repo), Commit(body.Commit), SkillPath(body.Path));
        if (body.Name is { } chosen && SkillCopier.NameProblem(chosen) is { } problem) throw ApiErrors.BadRequest($"Choose another name: {problem}.");
        RequireFetch(caller, limits);
        var (skill, files) = await SkillAsync(fetcher, name, at, folder, ct);
        if (!skill.Installable) throw ApiErrors.BadRequest($"{folder} can't be installed: {skill.Reason}.");
        var copied = await new SkillCopier(store, writer, publishing, caller, logs.CreateLogger<SkillCopier>())
            .InstallAsync(body.Vault ?? "", body.Name ?? skill.Name, files.Files, $"github:{name}/{folder}@{at}", ct);
        return TypedResults.Created((string?)null, new SkillCopiedView(copied.Value));
    }

    /// <summary>The listing's entry for the skill at <paramref name="folder"/> and its files, at <paramref name="commit"/>.</summary>
    private static async Task<(MarketplaceSkill Skill, SkillFiles Files)> SkillAsync(MarketplaceFetcher fetcher, RepoName repo, string commit, string folder, CancellationToken ct)
    {
        var (listing, archive) = await GitHubAsync(async () => (await fetcher.ListingAsync(repo, commit, ct), await fetcher.ArchiveAsync(repo, commit, ct)));
        var skill = listing.Skills.FirstOrDefault(s => string.Equals(s.Path, folder, StringComparison.Ordinal));
        if (skill is null || MarketplaceIndex.FilesOf(archive, folder) is not { } files) throw ApiErrors.NotFound($"{repo} has no skill at {folder}.");
        return (skill, files);
    }

    private static void RequireOn(IOptions<KumbuOptions> options)
    {
        if (!options.Value.Skills.Marketplaces.Enabled) throw SwitchedOff();
    }

    private static void RequireFetch(RequestCaller caller, AccountLimits limits)
    {
        if (limits.GitHubFetch(caller.User!.Id) is { } retryAfter)
            throw new ApiException(StatusCodes.Status429TooManyRequests, "Too many requests to GitHub; try again in a minute.", retryAfter, type: Busy);
    }

    private static RepoName Repo(string? text) =>
        RepoName.Parse(text ?? "") ?? throw ApiErrors.BadRequest("Give a GitHub repository as owner/repo.");

    private static string Commit(string? text) =>
        Commits.IsValid(text) ? text! : throw ApiErrors.BadRequest("Give the commit as its 40 lower-case hex digits.");

    private static string SkillPath(string? text) =>
        text is not null && MarketplaceIndex.IsSkillPath(text) ? text : throw ApiErrors.BadRequest("Give the skill's folder in the repository, as path.");

    private static bool Same(string a, RepoName b) => string.Equals(a, b.ToString(), StringComparison.OrdinalIgnoreCase);

    private static async Task<VaultAccess?> SkillsVaultAsync(RequestCaller caller, CancellationToken ct) =>
        (await caller.OpenVaultsAsync(ct)).FirstOrDefault(v => v.IsSkillsVault);

    private static ApiException NoSkillsVault() => new(StatusCodes.Status409Conflict, "You have no vault Skills yet; sign in again to have it made.");

    /// <summary>GitHub's refusals as answers: <c>not-found</c> 404, the rate limit 429, the rest 502, each with its type.</summary>
    internal static async Task<T> GitHubAsync<T>(Func<Task<T>> fetch)
    {
        try
        {
            return await fetch();
        }
        catch (GitHubException e)
        {
            throw e.Kind switch
            {
                "not-found" => new ApiException(StatusCodes.Status404NotFound, e.Message, type: "not-found"),
                "rate-limited" => new ApiException(StatusCodes.Status429TooManyRequests, RateLimitedSentence, type: RateLimited),
                _ => new ApiException(StatusCodes.Status502BadGateway, e.Message, type: "github-" + e.Kind),
            };
        }
    }
}

/// <summary>
/// The marketplaces' side of <see cref="GitHubClient"/>: listings per repository and commit for <see cref="ListingAge"/>
/// (Decision 30), in memory only, through the injected clock; and one download at a time of an archive not in the
/// client's cache, which every request that wants it meanwhile waits for, so that parallel requests for one commit don't
/// each hold its unpacked bytes. The download is never cancelled by one of them going away: the client's own timeout
/// bounds it.
/// </summary>
public sealed class MarketplaceFetcher(GitHubClient github, TimeProvider time)
{
    public static readonly TimeSpan ListingAge = TimeSpan.FromHours(1);

    private const int MaxCachedListings = 200;

    private readonly ConcurrentDictionary<string, CachedListing> _listings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<RepoArchive>>> _downloads = new(StringComparer.Ordinal);

    private sealed record CachedListing(MarketplaceListing Listing, DateTimeOffset Stored);

    public Task<string> ResolveCommitAsync(RepoName repo, CancellationToken ct) => github.ResolveCommitAsync(repo, ct);

    public bool HasCachedCommit(RepoName repo) => github.HasCachedCommit(repo);

    public async Task<MarketplaceListing> ListingAsync(RepoName repo, string commit, CancellationToken ct)
    {
        var key = Key(repo, commit);
        var now = time.GetUtcNow();
        if (_listings.TryGetValue(key, out var cached) && now < cached.Stored + ListingAge) return cached.Listing;

        var listing = MarketplaceIndex.Of(await ArchiveAsync(repo, commit, ct));
        _listings[key] = new CachedListing(listing, time.GetUtcNow());
        foreach (var old in _listings.Where(e => time.GetUtcNow() >= e.Value.Stored + ListingAge)) _listings.TryRemove(old);
        while (_listings.Count > MaxCachedListings) _listings.TryRemove(_listings.MinBy(e => e.Value.Stored).Key, out _);
        return listing;
    }

    public Task<RepoArchive> ArchiveAsync(RepoName repo, string commit, CancellationToken ct)
    {
        var key = Key(repo, commit);
        var download = _downloads.GetOrAdd(key, _ => new Lazy<Task<RepoArchive>>(() => DownloadAsync(repo, commit, key)));
        return download.Value.WaitAsync(ct);
    }

    private async Task<RepoArchive> DownloadAsync(RepoName repo, string commit, string key)
    {
        try
        {
            return await github.ArchiveAsync(repo, commit, CancellationToken.None);
        }
        finally
        {
            _downloads.TryRemove(key, out _);
        }
    }

    private static string Key(RepoName repo, string commit) => $"{repo.ToString().ToLowerInvariant()}@{commit}";
}
