// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Api;
using Kumbu.Server.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Skills;

/// <summary>A marketplace's archive: where it comes from, the limits it's read within, the names it may hold and the
/// cache it's kept in (spec §3.9; Decisions 23, 30).</summary>
public sealed class GitHubArchiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly RepoName Repo = RepoName.Parse("alice-example/skills")!;

    /// <summary>Real time, so that a request that is never given up fails its test rather than hanging it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static (GitHubClient Client, FakeGitHub Fake, FakeTimeProvider Time, CapturedLog Log) Make(string? token = null)
    {
        var fake = new FakeGitHub { Commit = Sha };
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-08T12:00:00Z"));
        var log = new CapturedLog();
        var options = Options.Create(new KumbuOptions { Skills = new SkillsOptions { GitHubToken = token } });
        return (new GitHubClient(new StubFactory(fake), options, time, log.CreateLogger("github").ToGeneric<GitHubClient>()), fake, time, log);
    }

    [Fact]
    public async Task The_archive_comes_from_codeload_at_the_commit_without_the_token()
    {
        var (client, fake, _, _) = Make(token: "ghp_secret0000");
        fake.Archive = FakeGitHub.Tar(("skills-0123456/skills/pdf/SKILL.md", "---\nname: pdf\ndescription: x\n---\n"u8.ToArray()));
        var archive = await client.ArchiveAsync(Repo, Sha, Ct);
        var request = fake.Requests.Single();
        Assert.Equal($"https://codeload.github.com/alice-example/skills/tar.gz/{Sha}", request.Uri.ToString());
        Assert.Null(request.Authorization);
        Assert.Equal("skills/pdf/SKILL.md", Assert.Single(archive.Files).Path);   // the top folder stripped
        Assert.Equal(Sha, archive.Commit);
        Assert.Equal(archive.Files.Sum(f => f.Bytes.Length), archive.Bytes);
    }

    [Fact]
    public async Task An_archive_over_25_MB_is_refused_while_reading()
    {
        var (client, fake, _, _) = Make();
        // Incompressible, so the compressed bytes pass 25 MB while the tar stays valid and every file under 15 MB.
        fake.RawArchive = FakeGitHub.Tar(("r/a.bin", Noise(13 * 1024 * 1024, 1)), ("r/b.bin", Noise(13 * 1024 * 1024, 2)));   // sent without Content-Length
        Assert.True(fake.RawArchive.Length > GitHubClient.MaxArchiveBytes);
        Assert.Equal("too-large", (await Assert.ThrowsAsync<GitHubException>(() => client.ArchiveAsync(Repo, Sha, Ct))).Kind);
    }

    [Fact]
    public async Task An_archive_announced_over_25_MB_is_refused_before_reading()
    {
        var (client, fake, _, _) = Make();
        fake.Archive = new byte[26 * 1024 * 1024];   // with Content-Length; not even a gzip
        Assert.Equal("too-large", (await Assert.ThrowsAsync<GitHubException>(() => client.ArchiveAsync(Repo, Sha, Ct))).Kind);
    }

    [Fact]
    public async Task More_than_100_MB_decompressed_is_refused()
    {
        var (client, fake, _, _) = Make();
        fake.Archive = FakeGitHub.Tar(Enumerable.Range(0, 8).Select(i => ($"r/big{i}.txt", new byte[14 * 1024 * 1024])).ToArray());   // zeros: tiny compressed
        Assert.Equal("too-large", (await Assert.ThrowsAsync<GitHubException>(() => client.ArchiveAsync(Repo, Sha, Ct))).Kind);
    }

    [Fact]
    public async Task A_2_GB_entry_header_is_refused_before_allocating()
    {
        var (client, fake, _, _) = Make();
        fake.Archive = FakeGitHub.TarWithDeclaredSize("r/huge.bin", 2L * 1024 * 1024 * 1024, actual: new byte[16]);   // a header claiming 2 GB
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal("too-large", (await Assert.ThrowsAsync<GitHubException>(() => client.ArchiveAsync(Repo, Sha, Ct))).Kind);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64L * 1024 * 1024);
    }

    [Fact]
    public async Task An_entry_shorter_than_its_header_is_a_bad_answer()
    {
        var (client, fake, _, _) = Make();
        fake.Archive = FakeGitHub.TarWithDeclaredSize("r/short.bin", 1024 * 1024, actual: new byte[16]);
        Assert.Equal("bad-answer", (await Assert.ThrowsAsync<GitHubException>(() => client.ArchiveAsync(Repo, Sha, Ct))).Kind);
    }

    [Fact]
    public async Task More_than_5000_entries_is_refused_counting_every_kind()
    {
        var (client, fake, _, _) = Make();
        fake.Archive = FakeGitHub.TarOfDirectories(5001);   // directory entries only
        Assert.Equal("too-large", (await Assert.ThrowsAsync<GitHubException>(() => client.ArchiveAsync(Repo, Sha, Ct))).Kind);
    }

    [Fact]
    public async Task Links_and_escaping_names_are_skipped()
    {
        var (client, fake, _, _) = Make();
        fake.Archive = FakeGitHub.TarWith(symlink: "r/link", hardlink: "r/hard", files: [("r/ok.md", "x"u8.ToArray()), ("/etc/x", "x"u8.ToArray()), ("r/a/../../b", "x"u8.ToArray()), ("r/a\\b", "x"u8.ToArray())]);
        var archive = await client.ArchiveAsync(Repo, Sha, Ct);
        Assert.Equal(["ok.md"], archive.Files.Select(f => f.Path));
        Assert.Equal(["/etc/x", "a/../../b", "a\\b", "hard", "link"], archive.Skipped.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_long_name_and_a_leading_dot_slash_are_read()
    {
        var (client, fake, _, _) = Make();
        var deep = string.Join('/', Enumerable.Repeat("folder-with-a-long-name", 12)) + "/SKILL.md";   // past ustar's 100 + 155
        fake.Archive = FakeGitHub.TarWith(files: [("./r/" + deep, "x"u8.ToArray())], gnu: true);
        Assert.Equal([deep], (await client.ArchiveAsync(Repo, Sha, Ct)).Files.Select(f => f.Path));
        fake.Archive = FakeGitHub.Tar(("r/" + deep, "x"u8.ToArray()));   // PAX
        Assert.Equal([deep], (await client.ArchiveAsync(Repo, Sha[..39] + "8", Ct)).Files.Select(f => f.Path));
    }

    [Fact]
    public async Task The_cache_holds_at_most_four_archives_and_200_MB()
    {
        var (client, fake, _, _) = Make();
        foreach (var i in Enumerable.Range(0, 5)) { fake.Archive = FakeGitHub.Tar(($"r/{i}.txt", "x"u8.ToArray())); await client.ArchiveAsync(Repo, Sha[..39] + i, Ct); }
        await client.ArchiveAsync(Repo, Sha[..39] + 0, Ct);   // evicted: fetched again
        await client.ArchiveAsync(Repo, Sha[..39] + 4, Ct);   // still cached
        Assert.Equal(6, fake.Requests.Count(r => r.Uri.Host == GitHubClient.ArchiveHost));
        fake.Requests.Clear();
        fake.Archive = FakeGitHub.Tar(Enumerable.Range(0, 4).Select(j => ($"r/m{j}.txt", new byte[14 * 1024 * 1024])).ToArray());   // 56 MB each
        foreach (var i in Enumerable.Range(5, 4)) await client.ArchiveAsync(Repo, Sha[..39] + i, Ct);   // 4 × 56 MB > 200 MB, 4 archives
        await client.ArchiveAsync(Repo, Sha[..39] + 5, Ct);   // the oldest was evicted by bytes
        await client.ArchiveAsync(Repo, Sha[..39] + 5, Ct);   // and is cached again
        Assert.Equal(5, fake.Requests.Count);
    }

    [Fact]
    public async Task A_used_archive_is_kept_over_an_older_one()
    {
        var (client, fake, _, _) = Make();
        fake.Archive = FakeGitHub.Tar(("r/x.txt", "x"u8.ToArray()));
        foreach (var i in Enumerable.Range(0, 4)) await client.ArchiveAsync(Repo, Sha[..39] + i, Ct);
        await client.ArchiveAsync(Repo, Sha[..39] + 0, Ct);   // 0 used: 1 is now the least recently used
        await client.ArchiveAsync(Repo, Sha[..39] + 4, Ct);
        await client.ArchiveAsync(Repo, Sha[..39] + 0, Ct);
        Assert.Equal(5, fake.Requests.Count);
        await client.ArchiveAsync(Repo, Sha[..39] + 1, Ct);
        Assert.Equal(6, fake.Requests.Count);
    }

    [Fact]
    public async Task Names_are_nfc_and_case_twins_skipped()
    {
        var (client, fake, _, _) = Make();
        fake.Archive = FakeGitHub.Tar(("r/Cafe\u0301.md", "x"u8.ToArray()), ("r/A.md", "1"u8.ToArray()), ("r/a.md", "2"u8.ToArray()), ("r/Caf\u00e9.md", "y"u8.ToArray()));
        var archive = await client.ArchiveAsync(Repo, Sha, Ct);
        Assert.Equal(["A.md", "Caf\u00e9.md"], archive.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Equal("x"u8.ToArray(), archive.Files.Single(f => f.Path == "Caf\u00e9.md").Bytes);
        Assert.Equal(["Caf\u00e9.md", "a.md"], archive.Skipped.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_download_that_hangs_is_given_up_as_unavailable()
    {
        var (client, fake, time, _) = Make();
        fake.HangBody = true;
        var fetch = client.ArchiveAsync(Repo, Sha, Ct);
        await fake.Hanging.Task.WaitAsync(Ct);
        time.Advance(GitHubClient.RequestTimeout + TimeSpan.FromSeconds(1));
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<GitHubException>(() => fetch.WaitAsync(Bound, Ct))).Kind);
    }

    [Fact]
    public async Task An_api_answer_that_hangs_is_given_up_as_unavailable()
    {
        var (client, fake, time, _) = Make();
        fake.HangBody = true;
        var resolve = client.ResolveCommitAsync(Repo, Ct);
        await fake.Hanging.Task.WaitAsync(Ct);
        time.Advance(GitHubClient.RequestTimeout + TimeSpan.FromSeconds(1));
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<GitHubException>(() => resolve.WaitAsync(Bound, Ct))).Kind);
    }

    [Fact]
    public async Task The_callers_cancellation_stays_a_cancellation()
    {
        var (client, fake, _, _) = Make();
        fake.HangBody = true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var fetch = client.ArchiveAsync(Repo, Sha, cancel.Token);
        await fake.Hanging.Task.WaitAsync(Ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(Bound, Ct));
    }

    [Fact]
    public void GitHub_fetches_are_limited_per_user()
    {
        using var limits = new AccountLimits();
        for (var i = 0; i < AccountLimits.GitHubFetchesPerUser; i++) Assert.Null(limits.GitHubFetch("alice"));
        Assert.Equal(30, AccountLimits.GitHubFetchesPerUser);
        Assert.NotNull(limits.GitHubFetch("alice"));
        Assert.Null(limits.GitHubFetch("bob"));
    }

    private static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(GitHubServices.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
