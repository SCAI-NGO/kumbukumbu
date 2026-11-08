// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kumbu.Server.Api;
using Kumbu.Server.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Skills;

/// <summary>A marketplace's archive: where it comes from, the limits it's read within, the names it may hold and the
/// cache it's kept in (spec §3.9; Decisions 23, 30). Alone, so that the allocation measurements see only their own test.</summary>
[Collection(nameof(GitHubArchiveTests))]
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

    // 1 GiB stays below Array.MaxLength, so an allocation of the declared size would really happen and be measured.
    // The last three: a PAX size TarReader doesn't apply (after a GNU L or K, or before one) would make the guard skip
    // a header TarReader reads, here an x declaring 1 GiB inside what the guard took for the file's 1024 bytes.
    [Theory, InlineData("0", "too-large"), InlineData("x", "too-large"), InlineData("g", "too-large"), InlineData("L", "too-large"), InlineData("K", "too-large"),
        InlineData("L x file", "bad-answer"), InlineData("K x file", "bad-answer"), InlineData("x L file", "bad-answer")]
    public async Task A_1_GB_header_is_refused_before_allocating(string shape, string kind)
    {
        var (client, fake, _, _) = Make();
        var dir = FakeGitHub.UstarHeader("r/", '5', 0);
        var hidden = FakeGitHub.UstarHeader("PaxHeaders/y", 'x', 1L << 30).Concat(new byte[512]).ToArray();
        var size = Pax("size", "1024");
        var longName = "r/long"u8.ToArray();
        fake.Archive = shape switch
        {
            "0" => FakeGitHub.TarWithDeclaredSize("r/huge.bin", 1L << 30, actual: new byte[16]),
            "x" => FakeGitHub.GzipTar(dir, FakeGitHub.UstarHeader("PaxHeaders/x", 'x', 1L << 30), new byte[16]),
            "g" => FakeGitHub.GzipTar(FakeGitHub.UstarHeader("pax_global_header", 'g', 1L << 30), new byte[16]),   // first, where it may be
            "L" or "K" => FakeGitHub.GzipTar(dir, FakeGitHub.UstarHeader("././@LongLink", shape[0], 1L << 30, gnu: true), new byte[16]),
            "L x file" => FakeGitHub.GzipTar(dir, FakeGitHub.UstarHeader("././@LongLink", 'L', longName.Length, gnu: true), longName,
                FakeGitHub.UstarHeader("PaxHeaders/x", 'x', size.Length), size, FakeGitHub.UstarHeader("r/f.bin", '0', 0), hidden),
            "K x file" => FakeGitHub.GzipTar(dir, FakeGitHub.UstarHeader("././@LongLink", 'K', longName.Length, gnu: true), longName,
                FakeGitHub.UstarHeader("PaxHeaders/x", 'x', size.Length), size, FakeGitHub.UstarHeader("r/f.bin", '0', 0), hidden),
            _ => FakeGitHub.GzipTar(dir, FakeGitHub.UstarHeader("PaxHeaders/x", 'x', size.Length), size,
                FakeGitHub.UstarHeader("././@LongLink", 'L', longName.Length, gnu: true), longName, FakeGitHub.UstarHeader("r/f.bin", '0', 0, gnu: true), hidden),
        };
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var refused = await Assert.ThrowsAsync<GitHubException>(() => client.ArchiveAsync(Repo, Sha, Ct));
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(allocated < 256L * 1024 * 1024, $"{allocated} bytes allocated");
        Assert.Equal(kind, refused.Kind);
    }

    [Fact]
    public async Task The_shapes_real_archives_have_are_read()
    {
        var (client, fake, _, _) = Make();
        var deep = string.Join('/', Enumerable.Repeat("folder-with-a-long-name", 12)) + "/SKILL.md";
        var comment = Pax("comment", Sha);
        var path = Pax("path", "r/" + deep);
        // git archive: a global header with the commit, then ustar entries, an x before a long name.
        fake.Archive = FakeGitHub.GzipTar(FakeGitHub.UstarHeader("pax_global_header", 'g', comment.Length), comment, FakeGitHub.UstarHeader("r/", '5', 0),
            FakeGitHub.UstarHeader("PaxHeaders/x", 'x', path.Length), path, FakeGitHub.UstarHeader("r/x", '0', 1), "a"u8.ToArray(),
            FakeGitHub.UstarHeader("r/b.md", '0', 1), "b"u8.ToArray());
        Assert.Equal([deep, "b.md"], (await client.ArchiveAsync(Repo, Sha, Ct)).Files.Select(f => f.Path));

        // tar --format=gnu: an L before a long name, an L and a K before a link with a long name and target.
        var name = Encoding.ASCII.GetBytes("r/" + deep + "\0");
        var link = Encoding.ASCII.GetBytes("r/" + deep + "-link\0");
        fake.Archive = FakeGitHub.GzipTar(FakeGitHub.UstarHeader("r/", '5', 0, gnu: true),
            FakeGitHub.UstarHeader("././@LongLink", 'L', name.Length, gnu: true), name, FakeGitHub.UstarHeader("r/x", '0', 1, gnu: true), "a"u8.ToArray(),
            FakeGitHub.UstarHeader("././@LongLink", 'L', link.Length, gnu: true), link, FakeGitHub.UstarHeader("././@LongLink", 'K', name.Length, gnu: true), name,
            FakeGitHub.UstarHeader("r/l", '2', 0, gnu: true), FakeGitHub.UstarHeader("r/b.md", '0', 1, gnu: true), "b"u8.ToArray());
        var gnu = await client.ArchiveAsync(Repo, Sha[..39] + "9", Ct);
        Assert.Equal([deep, "b.md"], gnu.Files.Select(f => f.Path));
        Assert.Equal([deep + "-link"], gnu.Skipped);
    }

    /// <summary>One PAX record, its length counting itself.</summary>
    private static byte[] Pax(string key, string value)
    {
        var body = $" {key}={value}\n";
        var length = body.Length + 1;
        while ((length.ToString(CultureInfo.InvariantCulture) + body).Length != length) length++;
        return Encoding.UTF8.GetBytes(length.ToString(CultureInfo.InvariantCulture) + body);
    }

    [Fact]
    public async Task A_pax_size_is_followed_past_the_entry()
    {
        var (client, fake, _, _) = Make();
        fake.Archive = FakeGitHub.GzipTar(FakeGitHub.UstarHeader("r/", '5', 0), FakeGitHub.UstarHeader("PaxHeaders/f", 'x', 11), "11 size=16\n"u8.ToArray(),
            FakeGitHub.UstarHeader("r/f.bin", '0', 0), "aaaaaaaaaaaaaaaa"u8.ToArray(), FakeGitHub.UstarHeader("r/g.txt", '0', 1), "g"u8.ToArray());
        var archive = await client.ArchiveAsync(Repo, Sha, Ct);
        Assert.Equal(["f.bin", "g.txt"], archive.Files.Select(f => f.Path));
        Assert.Equal(16, archive.Files[0].Bytes.Length);
    }

    public static TheoryData<string> Malformed =>
        ["size", "checksum", "mode", "sparse", "pax-length", "pax-then-pax", "g-not-first", "two-l", "k-then-l", "x-then-fifo", "x-then-gnu", "l-then-posix", "x-then-end"];

    [Theory, MemberData(nameof(Malformed))]
    public async Task A_malformed_header_is_a_bad_answer(string kind)
    {
        var (client, fake, _, _) = Make();
        byte[][] blocks = kind switch
        {
            "size" => [FakeGitHub.UstarHeader("r/a", '0', 0, beforeSum: h => "zzzzzzzzzzz\0"u8.CopyTo(h.AsSpan(124)))],
            "checksum" => [FakeGitHub.UstarHeader("r/a", '0', 0, edit: h => h[148] = (byte)'7')],
            "mode" => [FakeGitHub.UstarHeader("r/a", '0', 0, beforeSum: h => "zzzzzzz\0"u8.CopyTo(h.AsSpan(100)))],
            "sparse" => [FakeGitHub.UstarHeader("r/a", 'S', 0, gnu: true)],   // TarReader: NotSupportedException
            "pax-length" => [FakeGitHub.UstarHeader("PaxHeaders/a", 'x', 11), "99 size=16\n"u8.ToArray(), FakeGitHub.UstarHeader("r/a", '0', 0)],
            "pax-then-pax" => [FakeGitHub.UstarHeader("PaxHeaders/a", 'x', 0), FakeGitHub.UstarHeader("PaxHeaders/b", 'x', 0), FakeGitHub.UstarHeader("r/a", '0', 0)],
            "g-not-first" => [FakeGitHub.UstarHeader("pax_global_header", 'g', 0), FakeGitHub.UstarHeader("r/a", '0', 0)],
            "two-l" => [FakeGitHub.UstarHeader("././@LongLink", 'L', 0, gnu: true), FakeGitHub.UstarHeader("././@LongLink", 'L', 0, gnu: true), FakeGitHub.UstarHeader("r/a", '0', 0, gnu: true)],
            "k-then-l" => [FakeGitHub.UstarHeader("././@LongLink", 'K', 0, gnu: true), FakeGitHub.UstarHeader("././@LongLink", 'L', 0, gnu: true), FakeGitHub.UstarHeader("r/a", '2', 0, gnu: true)],
            "x-then-fifo" => [FakeGitHub.UstarHeader("PaxHeaders/a", 'x', 0), FakeGitHub.UstarHeader("r/a", '6', 0)],
            "x-then-gnu" => [FakeGitHub.UstarHeader("PaxHeaders/a", 'x', 0), FakeGitHub.UstarHeader("r/a", '0', 0, gnu: true)],
            "l-then-posix" => [FakeGitHub.UstarHeader("././@LongLink", 'L', 0, gnu: true), FakeGitHub.UstarHeader("r/a", '0', 0)],
            _ => [FakeGitHub.UstarHeader("PaxHeaders/a", 'x', 0)],
        };
        fake.Archive = FakeGitHub.GzipTar([FakeGitHub.UstarHeader("r/", '5', 0), .. blocks]);
        Assert.Equal("bad-answer", (await Assert.ThrowsAsync<GitHubException>(() => client.ArchiveAsync(Repo, Sha, Ct))).Kind);
    }

    [Fact]
    public async Task Metadata_headers_dont_count_toward_5000()
    {
        var (client, fake, _, _) = Make();
        // 5,000 entries, each after its own PAX header (as TarWriter writes them): 10,000 headers. The shapes allowed
        // bound the metadata headers to two per entry and one global header.
        fake.Archive = FakeGitHub.Tar(Enumerable.Range(0, 5000).Select(i => ($"r/{i}.md", Array.Empty<byte>())).ToArray());
        Assert.Equal(5000, (await client.ArchiveAsync(Repo, Sha, Ct)).Files.Count);
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
        fake.Archive = FakeGitHub.TarWith(symlink: "r/link", hardlink: "r/hard", files: [("r/ok.md", "x"u8.ToArray()), ("../x", "x"u8.ToArray()), ("q/y", "x"u8.ToArray()), ("/etc/x", "x"u8.ToArray()), ("r/a/../../b", "x"u8.ToArray()), ("r/a\\b", "x"u8.ToArray())]);
        var archive = await client.ArchiveAsync(Repo, Sha, Ct);
        Assert.Equal(["ok.md"], archive.Files.Select(f => f.Path));
        Assert.Equal(["../x", "/etc/x", "a/../../b", "a\\b", "hard", "link", "q/y"], archive.Skipped.Order(StringComparer.Ordinal));
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

[CollectionDefinition(nameof(GitHubArchiveTests), DisableParallelization = true)]
public sealed class GitHubArchiveCollection;
