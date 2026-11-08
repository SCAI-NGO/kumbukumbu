// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Server.Net;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Skills;

/// <summary>A repository on GitHub, as <c>owner/repo</c> (Decision 23). Only names GitHub itself allows get through, and
/// never <c>.</c> or <c>..</c>, so each part is safe as one URL segment.</summary>
public sealed partial record RepoName(string Owner, string Repo)
{
    public static RepoName? Parse(string text)
    {
        var slash = text.IndexOf('/');
        if (slash < 0) return null;
        var owner = text[..slash];
        var repo = text[(slash + 1)..];
        if (!OwnerPattern().IsMatch(owner) || !RepoPattern().IsMatch(repo) || repo is "." or "..") return null;
        return new RepoName(owner, repo);
    }

    public override string ToString() => $"{Owner}/{Repo}";

    // \z, not $: $ would let a line break at the end through.
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})\z")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}\z")]
    private static partial Regex RepoPattern();
}

/// <summary>A full commit id: 40 lower-case hex digits, whoever names it.</summary>
public static class Commits
{
    public static bool IsValid(string? text) => text is { Length: 40 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>A request to GitHub that gave no usable answer. <see cref="Kind"/> is one of <c>not-found</c>, <c>rate-limited</c>,
/// <c>redirected</c>, <c>too-large</c>, <c>bad-answer</c> or <c>unavailable</c>; the message can be shown as it is.</summary>
public sealed class GitHubException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}

public static class GitHubServices
{
    public const string HttpClientName = "github";

    /// <summary>The client and the one HTTP client it uses, which refuses redirects, cookies and proxies and connects only to
    /// a public address it resolved itself (as <see cref="OAuth.ClientMetadataServices"/>). Nothing here asks GitHub
    /// anything; only a signed-in user's request does.</summary>
    public static IServiceCollection AddGitHubClient(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = (context, ct) => PublicAddresses.ConnectAsync(context, false, ct),
        });
        services.AddSingleton<GitHubClient>();
        return services;
    }
}

/// <summary>The server's requests to GitHub: to <see cref="ApiHost"/> and <see cref="ArchiveHost"/> only, over https, with URLs
/// built from a checked <see cref="RepoName"/> and commit. The token goes to <see cref="ApiHost"/> only and is never logged.</summary>
public sealed partial class GitHubClient(IHttpClientFactory http, IOptions<KumbuOptions> options, TimeProvider time, ILogger<GitHubClient> log)
{
    public const string ApiHost = "api.github.com", ArchiveHost = "codeload.github.com";
    public const int MaxJsonBytes = 1024 * 1024;
    public static readonly TimeSpan CommitAge = TimeSpan.FromMinutes(10);

    private const int MaxCachedCommits = 1000;

    private readonly ConcurrentDictionary<string, CachedCommit> _commits = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedCommit(string Commit, DateTimeOffset Stored);

    /// <summary>The commit the default branch points to, from the cache for <see cref="CommitAge"/>.</summary>
    public async Task<string> ResolveCommitAsync(RepoName repo, CancellationToken ct)
    {
        var key = repo.ToString();
        if (_commits.TryGetValue(key, out var cached) && time.GetUtcNow() < cached.Stored + CommitAge) return cached.Commit;

        var body = await ReadApiAsync(repo, ApiUri(repo, "commits", "HEAD"), "application/vnd.github.sha", ct);
        var commit = Encoding.UTF8.GetString(body);
        if (!Commits.IsValid(commit)) throw Fail(repo, "bad-answer", "GitHub's answer for the latest commit isn't a commit id.");

        _commits[key] = new CachedCommit(commit, time.GetUtcNow());
        while (_commits.Count > MaxCachedCommits)
            _commits.TryRemove(_commits.MinBy(e => e.Value.Stored).Key, out _);
        log.LogInformation("GitHub commit of {Repo} resolved: {Commit}", key, commit);
        return commit;
    }

    internal Uri ApiUri(RepoName repo, params string[] segments) =>
        new($"https://{ApiHost}/repos/{Segment(repo.Owner)}/{Segment(repo.Repo)}" + string.Concat(segments.Select(s => "/" + Segment(s))));

    internal Uri ArchiveUri(RepoName repo, string commit) =>
        Commits.IsValid(commit)
            ? new Uri($"https://{ArchiveHost}/{Segment(repo.Owner)}/{Segment(repo.Repo)}/tar.gz/{commit}")
            : throw new ArgumentException("Not a commit id.", nameof(commit));

    private static string Segment(string text) => Uri.EscapeDataString(text);

    /// <summary>An API answer's body, at most <see cref="MaxJsonBytes"/>.</summary>
    private async Task<byte[]> ReadApiAsync(RepoName repo, Uri uri, string accept, CancellationToken ct)
    {
        using var response = await SendAsync(repo, uri, accept, ct);
        if (response.Content.Headers.ContentLength > MaxJsonBytes) throw TooLarge(repo);
        try
        {
            await using var stream = new CappedStream(await response.Content.ReadAsStreamAsync(ct), MaxJsonBytes);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (CappedStream.TooLongException)
        {
            throw TooLarge(repo);
        }
        catch (IOException e)
        {
            throw Unavailable(repo, e);
        }
    }

    /// <summary>The one way out to GitHub: only <c>https://api.github.com/…</c> and <c>https://codeload.github.com/…</c>, no
    /// answer but a 2xx accepted. The response's headers are read; its body is the caller's to read and bound.</summary>
    private async Task<HttpResponseMessage> SendAsync(RepoName repo, Uri uri, string accept, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host is not (ApiHost or ArchiveHost) || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("Only api.github.com and codeload.github.com are asked.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Kumbu", BuildVersion.Semver));
        if (uri.Host == ApiHost)
        {
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (options.Value.Skills.ReadGitHubToken() is { } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response;
        try
        {
            using var client = http.CreateClient(GitHubServices.HttpClientName);
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            throw Unavailable(repo, e);
        }

        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300) return response;
        response.Dispose();
        throw status switch
        {
            >= 300 and < 400 => Fail(repo, "redirected", "GitHub answered with a redirect, which isn't followed."),
            429 => RateLimited(repo),
            403 when Header(response, "x-ratelimit-remaining") == "0" => RateLimited(repo),
            404 => Fail(repo, "not-found", $"GitHub has no repository {repo}, or it isn't public."),
            _ => Fail(repo, "unavailable", $"GitHub answered {status}."),
        };
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private GitHubException RateLimited(RepoName repo) =>
        Fail(repo, "rate-limited", "GitHub's rate limit is reached; try again later. A GitHub token (KUMBU_GITHUB_TOKEN) allows more requests.");

    private GitHubException TooLarge(RepoName repo) => Fail(repo, "too-large", "GitHub's answer is too large.");

    private GitHubException Unavailable(RepoName repo, Exception e)
    {
        // The type only: a message could carry an address or part of an answer.
        log.LogInformation("GitHub unreachable for {Repo} ({Type})", repo.ToString(), e.GetType().Name);
        return new GitHubException("unavailable", "GitHub can't be reached right now.");
    }

    private GitHubException Fail(RepoName repo, string kind, string message)
    {
        log.LogInformation("GitHub request for {Repo} refused: {Kind}", repo.ToString(), kind);
        return new GitHubException(kind, message);
    }

    /// <summary>Reads at most <c>limit</c> bytes of the inner stream and throws once it holds more, whatever it said its
    /// length was.</summary>
    internal sealed class CappedStream(Stream inner, long limit) : Stream
    {
        public sealed class TooLongException() : IOException("The stream is longer than its limit.");

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            BytesRead += read;
            if (BytesRead > limit) throw new TooLongException();
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
