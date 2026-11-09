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
/// as her request, each counted by <see cref="AccountLimits.GitHubFetch"/>; previewing and installing take the commit the
/// client was shown, never the branch as it is now, and only a commit this server resolved for the repository lately. With marketplaces switched off every route but the list answers 404 and
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

    /// <summary>The <see cref="ApiError.Type"/> of a commit this server didn't resolve for the repository lately
    /// (<see cref="MarketplaceFetcher.IsKnown"/>): the client browses again.</summary>
    public const string UnknownCommit = "commit-unknown";

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
        group.MapGet("/marketplaces/{owner}/{repo}/skill", PreviewAsync).Errors(400, 401, 403, 404, 409, 429, 502, 503);
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
        var listing = await GitHubAsync(async () => await fetcher.ListingAsync(name, await fetcher.ResolveCommitAsync(name, ct), caller.User!.Id, ct));
        return TypedResults.Ok(new MarketplaceListingView(name.ToString(), listing.Commit, [.. listing.Skills], [.. listing.Plugins], listing.Note));
    }

    private static async Task<Ok<MarketplaceSkillView>> PreviewAsync(
        string owner, string repo, [FromQuery] string? commit, [FromQuery] string? path, RequestCaller caller, MarketplaceFetcher fetcher, AccountLimits limits,
        IOptions<KumbuOptions> options, CancellationToken ct)
    {
        RequireOn(options);
        var (name, at, folder) = (Repo($"{owner}/{repo}"), Commit(commit), SkillPath(path));
        RequireKnown(fetcher, name, at);
        RequireFetch(caller, limits);
        var (_, files) = await SkillAsync(fetcher, name, at, folder, caller.User!.Id, ct);
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
        RequireKnown(fetcher, name, at);
        RequireFetch(caller, limits);
        var (skill, files) = await SkillAsync(fetcher, name, at, folder, caller.User!.Id, ct);
        if (!skill.Installable) throw ApiErrors.BadRequest($"{folder} can't be installed: {skill.Reason}.");
        var copied = await new SkillCopier(store, writer, publishing, caller, logs.CreateLogger<SkillCopier>())
            .InstallAsync(body.Vault ?? "", body.Name ?? skill.Name, files.Files, $"github:{name}/{folder}@{at}", ct);
        return TypedResults.Created((string?)null, new SkillCopiedView(copied.Value));
    }

    /// <summary>The listing's entry for the skill at <paramref name="folder"/> and its files, at <paramref name="commit"/>.</summary>
    private static async Task<(MarketplaceSkill Skill, SkillFiles Files)> SkillAsync(
        MarketplaceFetcher fetcher, RepoName repo, string commit, string folder, string user, CancellationToken ct)
    {
        var (listing, archive) = await GitHubAsync(async () => (await fetcher.ListingAsync(repo, commit, user, ct), await fetcher.ArchiveAsync(repo, commit, user, ct)));
        var skill = listing.Skills.FirstOrDefault(s => string.Equals(s.Path, folder, StringComparison.Ordinal));
        if (skill is null || MarketplaceIndex.FilesOf(archive, folder) is not { } files) throw ApiErrors.NotFound($"{repo} has no skill at {folder}.");
        return (skill, files);
    }

    private static void RequireOn(IOptions<KumbuOptions> options)
    {
        if (!options.Value.Skills.Marketplaces.Enabled) throw SwitchedOff();
    }

    /// <summary>Only a commit this server showed: what Browse resolved for the repository within the listing's hour.</summary>
    private static void RequireKnown(MarketplaceFetcher fetcher, RepoName repo, string commit)
    {
        if (!fetcher.IsKnown(repo, commit))
            throw new ApiException(StatusCodes.Status409Conflict, $"Browse {repo} again: this commit isn't one Kumbu showed you lately.", type: UnknownCommit);
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
/// (Decision 30), in memory only, through the injected clock; the commits it resolved, the only ones a client may name
/// (<see cref="IsKnown"/>); and the archive downloads not in the client's cache, bounded server-wide. At most
/// <see cref="MaxDownloads"/> are in flight, each within <see cref="GitHubClient.MaxUnpackedBytes"/>, so at most
/// <see cref="MaxInFlightBytes"/> of unpacked files are held, and a user starts at most one at a time; a request beyond
/// either is refused, 429 <see cref="MarketplaceApi.Busy"/>, before anything is fetched. Every request that wants a
/// download in flight meanwhile waits for it, so that parallel requests for one commit don't each hold its unpacked
/// bytes; one going away leaves it to the others, and once none waits any more it is cancelled.
/// </summary>
public sealed class MarketplaceFetcher(GitHubClient github, TimeProvider time)
{
    public static readonly TimeSpan ListingAge = TimeSpan.FromHours(1);

    /// <summary>Archive downloads in flight, server-wide, and the unpacked bytes they hold at most together.</summary>
    public const int MaxDownloads = 2;

    public const long MaxInFlightBytes = MaxDownloads * GitHubClient.MaxUnpackedBytes;

    private const int MaxCachedListings = 200, MaxKnownCommits = 1000;

    private static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, CachedListing> _listings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _known = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Download> _downloads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _users = new(StringComparer.Ordinal);
    private int _inFlight;

    private sealed record CachedListing(MarketplaceListing Listing, DateTimeOffset Stored);

    /// <summary>One download: its own cancellation, and how many requests wait for it.</summary>
    private sealed class Download(string user)
    {
        public string User { get; } = user;

        public CancellationTokenSource Cancel { get; } = new();

        public Task<RepoArchive> Task { get; set; } = null!;

        public int Waiters { get; set; }
    }

    /// <summary>Downloads in flight, those given up included until they have ended.</summary>
    internal int InFlight
    {
        get
        {
            lock (_lock) return _inFlight;
        }
    }

    /// <summary>The commit the default branch points to now, remembered as one this server showed (<see cref="IsKnown"/>).</summary>
    public async Task<string> ResolveCommitAsync(RepoName repo, CancellationToken ct)
    {
        var commit = await github.ResolveCommitAsync(repo, ct);
        Remember(repo, commit);
        return commit;
    }

    public bool HasCachedCommit(RepoName repo) => github.HasCachedCommit(repo);

    /// <summary>Whether this server resolved <paramref name="commit"/> for <paramref name="repo"/> within <see cref="ListingAge"/>:
    /// a client's commit is taken only then, so a request can't make it download any commit of the repository's fork
    /// network, nor one after another to hold their bytes.</summary>
    public bool IsKnown(RepoName repo, string commit) =>
        _known.TryGetValue(Key(repo, commit), out var at) && time.GetUtcNow() < at + ListingAge;

    internal void Remember(RepoName repo, string commit)
    {
        var now = time.GetUtcNow();
        _known[Key(repo, commit)] = now;
        if (_known.Count <= MaxKnownCommits) return;
        foreach (var old in _known.Where(e => now >= e.Value + ListingAge)) _known.TryRemove(old);
        while (_known.Count > MaxKnownCommits) _known.TryRemove(_known.MinBy(e => e.Value).Key, out _);
    }

    public async Task<MarketplaceListing> ListingAsync(RepoName repo, string commit, string user, CancellationToken ct)
    {
        var key = Key(repo, commit);
        var now = time.GetUtcNow();
        if (_listings.TryGetValue(key, out var cached) && now < cached.Stored + ListingAge) return cached.Listing;

        var listing = MarketplaceIndex.Of(await ArchiveAsync(repo, commit, user, ct));
        _listings[key] = new CachedListing(listing, time.GetUtcNow());
        foreach (var old in _listings.Where(e => time.GetUtcNow() >= e.Value.Stored + ListingAge)) _listings.TryRemove(old);
        while (_listings.Count > MaxCachedListings) _listings.TryRemove(_listings.MinBy(e => e.Value.Stored).Key, out _);
        return listing;
    }

    /// <summary>The archive for <paramref name="user"/>'s request: from the client's cache, by joining the download in
    /// flight, or by a new one within the bounds.</summary>
    public async Task<RepoArchive> ArchiveAsync(RepoName repo, string commit, string user, CancellationToken ct)
    {
        if (github.CachedArchive(repo, commit) is { } cached) return cached;
        var key = Key(repo, commit);
        Download download;
        lock (_lock)
        {
            if (!_downloads.TryGetValue(key, out download!))
            {
                if (_users.Contains(user)) throw Busy("You have a download from GitHub under way; try again once it is done.");
                if (_inFlight >= MaxDownloads) throw Busy("Kumbu is downloading from GitHub as much as it may at once; try again in a minute.");
                download = new Download(user);
                _downloads[key] = download;
                _users.Add(user);
                _inFlight++;
                download.Task = DownloadAsync(repo, commit, key, download);
            }

            download.Waiters++;
        }

        try
        {
            return await download.Task.WaitAsync(ct);
        }
        finally
        {
            var abandoned = false;
            lock (_lock)
            {
                if (--download.Waiters == 0 && !download.Task.IsCompleted)
                {
                    // Nobody waits for it any more: no one joins it again, and it ends as soon as it notices.
                    abandoned = true;
                    if (_downloads.TryGetValue(key, out var current) && current == download) _downloads.Remove(key);
                }
            }

            if (abandoned)
            {
                download.Cancel.Cancel();
                // What it ends with is no one's answer; observed, so that it isn't reported as unobserved.
                _ = download.Task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async Task<RepoArchive> DownloadAsync(RepoName repo, string commit, string key, Download download)
    {
        try
        {
            // Off the caller's stack, so that the lock is let go before the first byte is asked.
            await Task.Yield();
            return await github.ArchiveAsync(repo, commit, download.Cancel.Token);
        }
        finally
        {
            lock (_lock)
            {
                if (_downloads.TryGetValue(key, out var current) && current == download) _downloads.Remove(key);
                _users.Remove(download.User);
                _inFlight--;
            }

        }
    }

    private static ApiException Busy(string message) => new(StatusCodes.Status429TooManyRequests, message, BusyRetry, type: MarketplaceApi.Busy);

    private static string Key(RepoName repo, string commit) => $"{repo.ToString().ToLowerInvariant()}@{commit}";
}
