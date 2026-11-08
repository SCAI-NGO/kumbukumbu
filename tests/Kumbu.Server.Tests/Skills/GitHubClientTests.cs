// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Net;
using Kumbu.Server.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Skills;

/// <summary>The requests to GitHub and the rules they keep (spec §3.9; Decision 23).</summary>
public sealed class GitHubClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly RepoName Repo = RepoName.Parse("alice-example/skills")!;

    private static (GitHubClient Client, FakeGitHub Fake, FakeTimeProvider Time, CapturedLog Log) Make(string? token = null)
    {
        var fake = new FakeGitHub { Commit = Sha };
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-08T12:00:00Z"));
        var log = new CapturedLog();
        var options = Options.Create(new KumbuOptions { Skills = new SkillsOptions { GitHubToken = token } });
        return (new GitHubClient(new StubFactory(fake), options, time, log.CreateLogger("github").ToGeneric<GitHubClient>()), fake, time, log);
    }

    [Fact]
    public async Task One_request_resolves_the_default_branch()
    {
        var (client, fake, _, _) = Make();
        Assert.Equal(Sha, await client.ResolveCommitAsync(Repo, Ct));
        var request = Assert.Single(fake.Requests);
        Assert.Equal("https://api.github.com/repos/alice-example/skills/commits/HEAD", request.Uri.ToString());
        Assert.Equal("application/vnd.github.sha", request.Accept);
        Assert.StartsWith("Kumbu/", request.UserAgent);
        Assert.Null(request.Authorization);
    }

    [Theory, InlineData("0123"), InlineData("0123456789ABCDEF0123456789ABCDEF01234567"), InlineData("../../x"), InlineData("0123456789abcdef0123456789abcdef01234567\n")]
    public async Task A_commit_that_isnt_40_lower_hex_is_refused(string answer)
    {
        var (client, fake, _, _) = Make();
        fake.Commit = answer;
        Assert.Equal("bad-answer", (await Assert.ThrowsAsync<GitHubException>(() => client.ResolveCommitAsync(Repo, Ct))).Kind);
    }

    [Theory, InlineData("abc"), InlineData("../x"), InlineData("0123456789abcdef0123456789abcdef0123456/")]
    public void An_archive_uri_needs_a_valid_commit(string commit)
    {
        var (client, _, _, _) = Make();
        Assert.Throws<ArgumentException>(() => client.ArchiveUri(Repo, commit));
    }

    [Fact]
    public void An_archive_uri_is_built_from_the_escaped_segments()
    {
        var (client, _, _, _) = Make();
        Assert.Equal("https://codeload.github.com/alice-example/skills/tar.gz/" + Sha, client.ArchiveUri(Repo, Sha).ToString());
        Assert.Equal("https://api.github.com/repos/alice-example/skills/commits/HEAD", client.ApiUri(Repo, "commits", "HEAD").ToString());
    }

    [Fact]
    public async Task The_commit_is_cached_ten_minutes()
    {
        var (client, fake, time, _) = Make();
        await client.ResolveCommitAsync(Repo, Ct);
        time.Advance(TimeSpan.FromMinutes(9));
        await client.ResolveCommitAsync(Repo, Ct);
        Assert.Single(fake.Requests);
        time.Advance(TimeSpan.FromMinutes(2));
        await client.ResolveCommitAsync(Repo, Ct);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task A_redirect_isnt_followed()
    {
        var (client, fake, _, _) = Make();
        fake.Redirect = "https://evil.example/x";
        Assert.Equal("redirected", (await Assert.ThrowsAsync<GitHubException>(() => client.ResolveCommitAsync(Repo, Ct))).Kind);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Only_the_two_hosts_are_asked()   // after resolving and (Task 36) fetching the archive
    {
        var (client, fake, _, _) = Make();
        await client.ResolveCommitAsync(Repo, Ct);
        Assert.All(fake.Requests, r => { Assert.Equal("https", r.Uri.Scheme); Assert.Contains(r.Uri.Host, new[] { GitHubClient.ApiHost, GitHubClient.ArchiveHost }); });
    }

    [Fact]
    public async Task An_api_answer_over_1_MB_is_refused()
    {
        var (client, fake, _, _) = Make();
        fake.Commit = new string('a', GitHubClient.MaxJsonBytes + 1);
        Assert.Equal("too-large", (await Assert.ThrowsAsync<GitHubException>(() => client.ResolveCommitAsync(Repo, Ct))).Kind);

        // Without a Content-Length, the bytes read are counted.
        fake.UnsizedBody = GitHubClient.MaxJsonBytes + 1;
        Assert.Equal("too-large", (await Assert.ThrowsAsync<GitHubException>(() => client.ResolveCommitAsync(Repo, Ct))).Kind);
    }

    [Fact]
    public async Task The_rate_limit_is_named()
    {
        var (client, fake, _, _) = Make();
        fake.RateLimited = true;
        Assert.Equal("rate-limited", (await Assert.ThrowsAsync<GitHubException>(() => client.ResolveCommitAsync(Repo, Ct))).Kind);
        fake.RateLimited = false;
        fake.TooManyRequests = true;
        Assert.Equal("rate-limited", (await Assert.ThrowsAsync<GitHubException>(() => client.ResolveCommitAsync(Repo, Ct))).Kind);
    }

    [Fact]
    public async Task A_missing_repository_is_not_found()
    {
        var (client, fake, _, _) = Make();
        fake.NotFound = true;
        Assert.Equal("not-found", (await Assert.ThrowsAsync<GitHubException>(() => client.ResolveCommitAsync(Repo, Ct))).Kind);
    }

    [Fact]
    public async Task The_token_goes_to_api_github_com_only_and_never_to_the_log()
    {
        var (client, fake, _, log) = Make(token: "ghp_secret0000");
        await client.ResolveCommitAsync(Repo, Ct);
        Assert.Equal("Bearer ghp_secret0000", fake.Requests.Single().Authorization);
        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains("ghp_secret0000"));
        // the archive request of Task 36 carries no Authorization (asserted there)
    }

    [Fact]
    public async Task The_real_handler_refuses_loopback()
    {
        using var services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System)
            .AddSingleton(Options.Create(new KumbuOptions())).AddGitHubClient().BuildServiceProvider();
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubServices.HttpClientName);
        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://localhost:1/", Ct));
        Assert.IsType<NotPublicAddressException>(failed.InnerException);
    }

    [Fact]
    public void The_real_handler_follows_no_redirects_and_takes_no_proxy_or_cookies()
    {
        using var services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System)
            .AddSingleton(Options.Create(new KumbuOptions())).AddGitHubClient().BuildServiceProvider();
        HttpMessageHandler handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(GitHubServices.HttpClientName);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.AllowAutoRedirect); Assert.False(sockets.UseProxy); Assert.False(sockets.UseCookies);
        Assert.NotNull(sockets.ConnectCallback);
    }

    [Theory, InlineData("alice-example/skills", true), InlineData("alice/../x", false), InlineData("-bad/x", false), InlineData("a/.", false),
        InlineData("a/..", false), InlineData("a/b/c", false), InlineData("https://github.com/a/b", false), InlineData("a/b%2F..", false), InlineData("a/b c", false),
        InlineData("a/b\n", false), InlineData("a/.github", true)]
    public void Repo_names_are_checked(string text, bool ok) => Assert.Equal(ok, RepoName.Parse(text) is not null);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(GitHubServices.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}

internal static class LoggerAdapter
{
    /// <summary>A plain logger as the typed one a constructor asks for.</summary>
    public static ILogger<T> ToGeneric<T>(this ILogger logger) => new Typed<T>(logger);

    private sealed class Typed<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
