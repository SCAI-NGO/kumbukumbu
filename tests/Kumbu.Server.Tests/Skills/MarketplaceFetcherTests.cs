// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Server.Api;
using Kumbu.Server.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Skills;

/// <summary>The server-wide bounds of archive downloads: at most <see cref="MarketplaceFetcher.MaxDownloads"/> in flight, one
/// per user, shared among the requests that wait for it and cancelled once none does; and only commits the server itself
/// resolved for the repository lately are taken from a client.</summary>
public sealed class MarketplaceFetcherTests
{
    private const string Repo = "alice-example/skills";
    private const string A = "0123456789abcdef0123456789abcdef01234567", B = "1123456789abcdef0123456789abcdef01234567", C = "2123456789abcdef0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly FakeGitHub _github = new() { Commit = A };
    private readonly HeldHandler _held;
    private readonly MarketplaceFetcher _fetcher;
    private static readonly RepoName Name = RepoName.Parse(Repo)!;

    public MarketplaceFetcherTests()
    {
        _github.Archives[Repo] = new Dictionary<string, byte[]> { ["skills/pdf/SKILL.md"] = Encoding.UTF8.GetBytes("---\nname: pdf\ndescription: Reads PDFs.\n---\n") };
        _held = new HeldHandler(_github);
        _fetcher = new MarketplaceFetcher(new GitHubClient(new OneClient(_held), Options.Create(new KumbuOptions()), _time, NullLogger<GitHubClient>.Instance), _time);
    }

    private int ArchiveRequests => _github.Requests.Count(r => r.Uri.Host == GitHubClient.ArchiveHost);

    private static async Task AssertBusyAsync(Task download)
    {
        var refused = await Assert.ThrowsAsync<ApiException>(() => download);
        Assert.Equal(429, refused.Status);
        Assert.Equal(MarketplaceApi.Busy, refused.Type);
    }

    [Fact]
    public async Task A_third_download_at_once_is_refused_before_anything_is_fetched()
    {
        Assert.Equal(2, MarketplaceFetcher.MaxDownloads);
        Assert.Equal(200L * 1024 * 1024, MarketplaceFetcher.MaxInFlightBytes);
        var first = _fetcher.ArchiveAsync(Name, A, "u1", Ct);
        var second = _fetcher.ArchiveAsync(Name, B, "u2", Ct);
        await _held.ArrivedAsync(2);

        await AssertBusyAsync(_fetcher.ArchiveAsync(Name, C, "u3", Ct));
        Assert.Equal(2, _held.Arrived);

        _held.Open();
        await Task.WhenAll(first, second);
        // Both done: there is room again.
        Assert.Equal(C, (await _fetcher.ArchiveAsync(Name, C, "u3", Ct)).Commit);
    }

    [Fact]
    public async Task One_user_has_one_download_in_flight_and_joins_others_freely()
    {
        var hers = _fetcher.ArchiveAsync(Name, A, "u1", Ct);
        await _held.ArrivedAsync(1);

        await AssertBusyAsync(_fetcher.ArchiveAsync(Name, B, "u1", Ct));
        // Another user waiting for the same commit shares her download.
        var his = _fetcher.ArchiveAsync(Name, A, "u2", Ct);
        _held.Open();

        Assert.Same(await hers, await his);
        Assert.Equal(1, ArchiveRequests);
    }

    [Fact]
    public async Task A_download_nobody_waits_for_any_more_is_cancelled()
    {
        using var gone = new CancellationTokenSource();
        var download = _fetcher.ArchiveAsync(Name, A, "u1", gone.Token);
        await _held.ArrivedAsync(1);

        gone.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        // The request to GitHub is given up, and the slot and her turn are free again.
        await _held.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        while (_fetcher.InFlight > 0) await Task.Delay(10, Ct);
        _held.Open();
        Assert.Equal(B, (await _fetcher.ArchiveAsync(Name, B, "u1", Ct)).Commit);
    }

    [Fact]
    public async Task A_download_someone_still_waits_for_goes_on()
    {
        using var gone = new CancellationTokenSource();
        var leaving = _fetcher.ArchiveAsync(Name, A, "u1", gone.Token);
        var staying = _fetcher.ArchiveAsync(Name, A, "u2", Ct);
        await _held.ArrivedAsync(1);

        gone.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        _held.Open();

        Assert.Equal(A, (await staying).Commit);
        Assert.False(_held.Cancelled.Task.IsCompleted);
        Assert.Equal(1, ArchiveRequests);
    }

    [Fact]
    public async Task Only_a_commit_resolved_lately_is_known()
    {
        Assert.False(_fetcher.IsKnown(Name, A));
        _held.Open();

        Assert.Equal(A, await _fetcher.ResolveCommitAsync(Name, Ct));

        Assert.True(_fetcher.IsKnown(Name, A));
        Assert.True(_fetcher.IsKnown(RepoName.Parse("Alice-Example/Skills")!, A));
        Assert.False(_fetcher.IsKnown(Name, B));
        Assert.False(_fetcher.IsKnown(RepoName.Parse("bob-example/skills")!, A));
        _time.Advance(MarketplaceFetcher.ListingAge + TimeSpan.FromSeconds(1));
        Assert.False(_fetcher.IsKnown(Name, A));
    }

    /// <summary>Holds every request until <see cref="Open"/>, counting those that arrived; a request given up while held
    /// completes <see cref="Cancelled"/>.</summary>
    private sealed class HeldHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Arrived => Volatile.Read(ref _arrived);

        public void Open() => _open.TrySetResult();

        public async Task ArrivedAsync(int count)
        {
            while (Arrived < count) await Task.Delay(10, Ct);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _arrived);
            try
            {
                await _open.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
