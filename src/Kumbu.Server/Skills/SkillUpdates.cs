// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Skills;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Publishing;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Skills;

/// <summary>A copy's state after a check: its folder in the caller's view, one of <see cref="SkillUpdates"/>' states, its
/// sealed source, and for a GitHub copy the commit its repository's default branch points to now.</summary>
public sealed record SkillUpdateState(VaultPath Folder, string State, string? Source, string? Commit);

/// <summary>
/// The update check (skills spec §3.10, Decisions 22, 33), run only by the session's Check for updates: per copy in a vault
/// she may write, the source sealed in the vault's record is asked, never one the frontmatter names. A frontmatter whose
/// <c>kumbu-source</c> or <c>kumbu-hash</c> disagrees with the sealed origin is <see cref="OriginEdited"/> and fetched
/// for not at all; a skill whose frontmatter names a source and that has no sealed origin is local and not checked. A
/// GitHub copy is asked at the commit the default branch points to now, a link copy with its sealed code; their files hash
/// as at install (<see cref="SkillHash"/>). The folders found <see cref="Available"/> are kept in the vault's record, so
/// <c>skills ls</c> and the list tell of them without fetching. The share code reaches no log and no answer.
/// </summary>
internal sealed class SkillUpdates(EncryptedNoteStore store, MarketplaceFetcher github, PublicationStore publishing, AccountLimits limits, RequestCaller caller)
{
    public const string Current = "current", Available = "available", Gone = "gone", Skipped = "skipped", OriginEdited = "origin-edited";

    private const string GitHubPrefix = "github:", LinkPrefix = "link:";

    /// <summary>Each repository whose commit isn't in the cache counts once against her GitHub limit, as it is reached; a
    /// check that asks GitHub nothing counts not at all. Her limit running out (429 <see cref="MarketplaceApi.Busy"/>) or
    /// GitHub's rate limit stops the whole check with nothing recorded: what is written comes only after every copy.</summary>
    public async Task<IReadOnlyList<SkillUpdateState>> CheckAsync(CancellationToken ct)
    {
        var counts = store.CountsUse;
        store.CountsUse = false;
        try
        {
            var states = new List<SkillUpdateState>();
            var results = new List<(VaultAccess Access, List<(SkillOrigin Origin, SkillUpdateState? State)> Found)>();
            foreach (var access in await store.UnlockedVaultsAsync(ct))
            {
                if (await store.VaultInfoAsync(access.Name, ct) is not { Level: >= AccessLevel.Write }) continue;
                var record = (await publishing.ReadAsync(null, access.Vault, ct)).Record;
                if (record.Origins.Count == 0) continue;

                var found = new List<(SkillOrigin Origin, SkillUpdateState? State)>();
                foreach (var origin in record.Origins.OrderBy(o => o.Path, StringComparer.Ordinal))
                {
                    if (!VaultPath.TryParse(origin.Path, out var stored)) continue;
                    var folder = access.ToCaller(stored);
                    var state = await StateAsync(record, origin, folder, ct);
                    found.Add((origin, state));
                    if (state is not null) states.Add(state);
                }

                results.Add((access, found));
            }

            foreach (var (access, found) in results) await RecordAsync(access, found, ct);

            return states;
        }
        finally
        {
            store.CountsUse = counts;
        }
    }

    /// <summary>The copy's state; null when its source couldn't be asked for now (GitHub unavailable), which leaves what the
    /// last check found.</summary>
    private async Task<SkillUpdateState?> StateAsync(PublishingRecord record, SkillOrigin origin, VaultPath folder, CancellationToken ct)
    {
        if (IsOriginEdited(folder, await store.ReadAsync(folder.Child(SkillPaths.Manifest), ct), origin))
        {
            return new SkillUpdateState(folder, OriginEdited, origin.Source, null);
        }

        var stored = origin.Path;
        string StateOf(string hash) =>
            string.Equals(hash, origin.Hash, StringComparison.Ordinal) ? Current
            : record.Skipped.Any(s => string.Equals(s.Path, stored, StringComparison.Ordinal) && string.Equals(s.Hash, hash, StringComparison.Ordinal)) ? Skipped
            : Available;

        if (origin.Source.StartsWith(LinkPrefix, StringComparison.Ordinal))
        {
            var hash = await LinkHashAsync(record.SkillCodes.FirstOrDefault(c => string.Equals(c.Path, stored, StringComparison.Ordinal))?.Code, ct);
            return new SkillUpdateState(folder, hash is null ? Gone : StateOf(hash), origin.Source, null);
        }

        if (GitHubSource(origin.Source) is not var (repo, path, commit)) return new SkillUpdateState(folder, Gone, origin.Source, null);
        try
        {
            if (!github.HasCachedCommit(repo) && limits.GitHubFetch(caller.User!.Id) is { } retryAfter)
                throw new ApiException(StatusCodes.Status429TooManyRequests, "Too many requests to GitHub; try again in a minute.", retryAfter, type: MarketplaceApi.Busy);
            var now = await github.ResolveCommitAsync(repo, ct);
            if (string.Equals(now, commit, StringComparison.Ordinal)) return new SkillUpdateState(folder, Current, origin.Source, now);
            if (MarketplaceIndex.FilesOf(await github.ArchiveAsync(repo, now, ct), path) is not { } files) return new SkillUpdateState(folder, Gone, origin.Source, now);
            return new SkillUpdateState(folder, StateOf(SkillHash.Of(files.Files.Select(f => (f.Path, (ReadOnlyMemory<byte>)f.Bytes)))), origin.Source, now);
        }
        catch (GitHubException e) when (e.Kind == "not-found")
        {
            return new SkillUpdateState(folder, Gone, origin.Source, null);
        }
        catch (GitHubException e) when (e.Kind == "rate-limited")
        {
            throw new ApiException(StatusCodes.Status429TooManyRequests, MarketplaceApi.RateLimitedSentence, type: MarketplaceApi.RateLimited);
        }
        catch (GitHubException)
        {
            return null;
        }
    }

    /// <summary>Whether the <c>kumbu-source</c> or <c>kumbu-hash</c> of <paramref name="manifest"/>, the text of the
    /// folder's <c>SKILL.md</c> (null if it has none), disagrees with the sealed origin (Decision 33).</summary>
    internal static bool IsOriginEdited(VaultPath folder, string? manifest, SkillOrigin origin)
    {
        var read = SkillManifest.Read(folder.Name, manifest);
        return !string.Equals(read.Source, origin.Source, StringComparison.Ordinal) || !string.Equals(read.Hash, origin.Hash, StringComparison.Ordinal);
    }

    /// <summary>The hash of the files behind the link the sealed <paramref name="code"/> opens; null when there is no code,
    /// the link was withdrawn, or a file of it doesn't open.</summary>
    private async Task<string?> LinkHashAsync(string? code, CancellationToken ct)
    {
        if (code is null || await publishing.OpenSkillAsync(code, ct) is not { } opened) return null;
        var files = new List<(string, ReadOnlyMemory<byte>)>(opened.Copy.Files.Count);
        foreach (var file in opened.Copy.Files)
        {
            if (await publishing.OpenSkillFileAsync(opened, file.Index, ct) is not { } bytes) return null;
            files.Add((file.Path, bytes.Bytes));
        }

        return SkillHash.Of(files);
    }

    /// <summary>A sealed <c>github:owner/repo/path@commit</c> as its parts; null for one that doesn't parse.</summary>
    internal static (RepoName Repo, string Path, string Commit)? GitHubSource(string source)
    {
        if (!source.StartsWith(GitHubPrefix, StringComparison.Ordinal)) return null;
        var text = source[GitHubPrefix.Length..];
        var at = text.LastIndexOf('@');
        if (at < 0) return null;
        var (location, commit) = (text[..at], text[(at + 1)..]);
        var parts = location.Split('/', 3);
        if (parts.Length != 3 || RepoName.Parse($"{parts[0]}/{parts[1]}") is not { } repo || !MarketplaceIndex.IsSkillPath(parts[2]) || !Commits.IsValid(commit)) return null;
        return (repo, parts[2], commit);
    }

    /// <summary>The folders found <see cref="Available"/> into the vault's record in place of those the last check found;
    /// a copy whose source couldn't be asked keeps its entry, a folder without a sealed origin any more loses it.</summary>
    private Task RecordAsync(VaultAccess access, IReadOnlyList<(SkillOrigin Origin, SkillUpdateState? State)> found, CancellationToken ct)
    {
        var checkedPaths = found.Where(f => f.State is not null).Select(f => f.Origin.Path).ToHashSet(StringComparer.Ordinal);
        var available = found.Where(f => f.State?.State == Available).Select(f => f.Origin.Path).ToList();
        return publishing.ChangeAsync(access, r =>
        {
            List<string> next = [.. r.Updates.Where(u => !checkedPaths.Contains(u) && r.Origins.Any(o => string.Equals(o.Path, u, StringComparison.Ordinal))), .. available];
            next = [.. next.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            return next.SequenceEqual(r.Updates) ? null : r with { Updates = next };
        }, ct);
    }
}
