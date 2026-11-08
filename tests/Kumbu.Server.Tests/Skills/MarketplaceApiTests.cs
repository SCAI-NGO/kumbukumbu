// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;
using Kumbu.Core.Skills;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Publishing;
using Kumbu.Server.Skills;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Skills;

/// <summary>The Marketplaces tab's API (skills spec §3.9, Decisions 13, 30, 33, 34): a user's list of marketplaces kept sealed
/// in her vault Skills' record, browsing and previewing at a commit, and installing into any vault she may write.</summary>
[Collection("app")]
public sealed class MarketplaceApiTests(KumbuAppFactory app)
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Other = "fedcba9876543210fedcba9876543210fedcba98";
    private const string Repo = "alice-example/skills";
    private const string Manifest = "---\nname: pdf\ndescription: Reads PDFs.\n---\n# PDF\n";
    private static readonly byte[] Script = "print('hi')\n"u8.ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeGitHub _github = Fake();

    /// <summary>The time of the host, starting at the real time in whole seconds (MongoDB keeps milliseconds).</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private static FakeGitHub Fake()
    {
        var fake = new FakeGitHub { Commit = Sha };
        fake.Archives[Repo] = new Dictionary<string, byte[]>
        {
            ["README.md"] = "# Skills\n"u8.ToArray(),
            ["skills/pdf/SKILL.md"] = Encoding.UTF8.GetBytes(Manifest),
            ["skills/pdf/scripts/run.py"] = Script,
            ["skills/pdf/tool.exe"] = [0x4D, 0x5A, 0x90, 0x00],
        };
        return fake;
    }

    /// <summary>The app with <paramref name="github"/> in place of the factory's NoGitHub and the fake clock.</summary>
    private WebApplicationFactory<Program> Api(FakeGitHub? github = null, bool enabled = true) => app.PublicApi.WithWebHostBuilder(b =>
    {
        if (!enabled) b.UseSetting("Kumbu:Skills:Marketplaces:Enabled", "false");
        b.ConfigureServices(s =>
        {
            s.AddHttpClient(GitHubServices.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => github ?? _github);
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(_time);
        });
    });

    private static Task<RestClient> UserAsync(WebApplicationFactory<Program> api) => RestClient.NewAsync(api, vault: "Work", remember: true);

    private static Task<HttpResponseMessage> InstallAsync(RestClient user, object body, string? bearer = null) =>
        user.SendAsync(HttpMethod.Post, "/api/skills/install", body, bearer);

    private static object Install(string vault = "Skills", string? name = null, string commit = Sha, string path = "skills/pdf", string repo = Repo) =>
        new { repo, commit, path, vault, name };

    private int ArchiveRequests => _github.Requests.Count(r => r.Uri.Host == GitHubClient.ArchiveHost);

    private int CommitRequests => _github.Requests.Count(r => r.Uri.Host == GitHubClient.ApiHost);

    private static async Task<string?> TypeAsync(HttpResponseMessage response) => (await RestClient.JsonAsync(response))["type"]?.GetValue<string>();

    private static async Task<VaultAccess> AccessAsync(RestClient user, string name) =>
        (await user.Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == name);

    private static async Task<PublishingRecord> RecordAsync(RestClient user, string vault) =>
        (await user.Api.Services.GetRequiredService<PublicationStore>().ReadAsync(null, (await AccessAsync(user, vault)).Vault, Ct)).Record;

    private static async Task<string> NoteAsync(RestClient user, string path)
    {
        using var get = await user.GetAsync("/api/notes" + path);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        return (await RestClient.JsonAsync(get))["content"]!.GetValue<string>();
    }

    private static async Task<string[]> FilesAsync(RestClient user, string folder)
    {
        var store = new EncryptedNoteStore(user.Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey));
        var path = VaultPath.TryParse(folder, out var p) ? p : throw new ArgumentException(folder);
        var notes = await store.ListNotesUnderAsync(path, Ct);
        var files = await StoreAttachments.UnderAsync(store, path, Ct);
        return [.. notes.Concat(files.Select(f => f.Path)).Select(f => f.Value[(folder.Length + 1)..]).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Adding_a_marketplace_fetches_nothing()
    {
        await using var api = Api();
        var alice = await UserAsync(api);

        using var added = await alice.SendAsync(HttpMethod.Post, "/api/skills/marketplaces", new { repo = Repo });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        using var list = await alice.GetAsync("/api/skills/marketplaces");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var body = await RestClient.JsonAsync(list);
        Assert.True(body["enabled"]!.GetValue<bool>());
        var entry = Assert.Single(body["marketplaces"]!.AsArray())!;
        Assert.Equal(Repo, entry["repo"]!.GetValue<string>());
        Assert.Equal(_time.GetUtcNow().UtcDateTime, entry["addedAt"]!.GetValue<DateTime>().ToUniversalTime());
        Assert.Empty(_github.Requests);

        using var again = await alice.SendAsync(HttpMethod.Post, "/api/skills/marketplaces", new { repo = "Alice-Example/Skills" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        using var removed = await alice.SendAsync(HttpMethod.Delete, "/api/skills/marketplaces/alice-example/skills");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var gone = await alice.SendAsync(HttpMethod.Delete, "/api/skills/marketplaces/alice-example/skills");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Empty((await RecordAsync(alice, "Skills")).Marketplaces);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task Nothing_is_fetched_at_startup()
    {
        RestClient alice;
        await using (var first = Api())
        {
            alice = await UserAsync(first);
            using var added = await alice.SendAsync(HttpMethod.Post, "/api/skills/marketplaces", new { repo = Repo });
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }

        var github = Fake();
        await using var api = Api(github);
        using var list = await alice.On(api).GetAsync("/api/skills/marketplaces");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var session = await alice.On(api).GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        Assert.Empty(github.Requests);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task The_list_is_kept_sealed_in_Skills_record()
    {
        await using var api = Api();
        var alice = await UserAsync(api);

        using var added = await alice.SendAsync(HttpMethod.Post, "/api/skills/marketplaces", new { repo = Repo });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var skills = await AccessAsync(alice, "Skills");
        Assert.Equal(Repo, Assert.Single((await RecordAsync(alice, "Skills")).Marketplaces).Repo);
        Assert.Empty((await RecordAsync(alice, "Work")).Marketplaces);
        var raw = await app.Database.GetCollection<BsonDocument>(PublicationStore.RecordCollection).Find(new BsonDocument("vault", skills.Vault.Id)).SingleAsync(Ct);
        var bytes = raw.ToBson();
        Assert.False(bytes.AsSpan().IndexOf("alice-example"u8) >= 0, "the repository's name is in the record's bytes");
    }

    [Fact]
    public async Task Browse_lists_the_skills_at_a_commit()
    {
        await using var api = Api();
        var alice = await UserAsync(api);

        using var browse = await alice.GetAsync("/api/skills/marketplaces/alice-example/skills");

        Assert.Equal(HttpStatusCode.OK, browse.StatusCode);
        var body = await RestClient.JsonAsync(browse);
        Assert.Equal(Repo, body["repo"]!.GetValue<string>());
        Assert.Equal(Sha, body["commit"]!.GetValue<string>());
        var skill = Assert.Single(body["skills"]!.AsArray())!;
        Assert.Equal("skills/pdf", skill["path"]!.GetValue<string>());
        Assert.Equal("pdf", skill["name"]!.GetValue<string>());
        Assert.Equal("Reads PDFs.", skill["description"]!.GetValue<string>());
        Assert.True(skill["installable"]!.GetValue<bool>());
        Assert.True(skill["hasScripts"]!.GetValue<bool>());
        // The fake answered, not the factory's NoGitHub: both requests reached it.
        Assert.Equal(
            ["https://api.github.com/repos/alice-example/skills/commits/HEAD", $"https://codeload.github.com/alice-example/skills/tar.gz/{Sha}"],
            _github.Requests.Select(r => r.Uri.ToString()));
    }

    [Fact]
    public async Task The_listing_is_cached_an_hour()
    {
        await using var api = Api();
        var alice = await UserAsync(api);
        using (var first = await alice.GetAsync("/api/skills/marketplaces/alice-example/skills")) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, ArchiveRequests);

        // Four other marketplaces push the archive out of the client's cache; the listing stays.
        for (var i = 0; i < GitHubClient.MaxCachedArchives; i++)
        {
            _github.Archives[$"alice-example/other{i}"] = new Dictionary<string, byte[]> { ["x/SKILL.md"] = "---\nname: x\ndescription: X.\n---\n"u8.ToArray() };
            using var other = await alice.GetAsync($"/api/skills/marketplaces/alice-example/other{i}");
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }

        _time.Advance(TimeSpan.FromMinutes(30));
        var before = ArchiveRequests;
        using (var second = await alice.GetAsync("/api/skills/marketplaces/alice-example/skills")) Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(before, ArchiveRequests);

        _time.Advance(TimeSpan.FromMinutes(31));
        using (var third = await alice.GetAsync("/api/skills/marketplaces/alice-example/skills")) Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(before + 1, ArchiveRequests);
    }

    [Fact]
    public async Task Preview_names_the_repo_path_commit_skipped_files_and_scripts()
    {
        await using var api = Api();
        var alice = await UserAsync(api);

        using var preview = await alice.GetAsync($"/api/skills/marketplaces/alice-example/skills/skill?commit={Sha}&path=skills/pdf");

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var body = await RestClient.JsonAsync(preview);
        Assert.Equal(Repo, body["repo"]!.GetValue<string>());
        Assert.Equal("skills/pdf", body["path"]!.GetValue<string>());
        Assert.Equal(Sha, body["commit"]!.GetValue<string>());
        Assert.Equal(Manifest, body["manifest"]!.GetValue<string>());
        Assert.Equal(
            [("SKILL.md", (long)Encoding.UTF8.GetByteCount(Manifest)), ("scripts/run.py", Script.Length)],
            body["files"]!.AsArray().Select(f => (f!["path"]!.GetValue<string>(), f["size"]!.GetValue<long>())));
        Assert.Equal(["tool.exe"], body["skipped"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.True(body["hasScripts"]!.GetValue<bool>());
        Assert.Equal(0, CommitRequests);
    }

    [Fact]
    public async Task Install_writes_the_copy_with_its_origin_and_sealed_origin()
    {
        await using var api = Api();
        var alice = await UserAsync(api);

        using var installed = await InstallAsync(alice, Install());

        Assert.Equal(HttpStatusCode.Created, installed.StatusCode);
        Assert.Equal("/Skills/pdf", (await RestClient.JsonAsync(installed))["path"]!.GetValue<string>());
        Assert.Equal(["SKILL.md", "scripts/run.py"], await FilesAsync(alice, "/Skills/pdf"));
        var hash = SkillHash.Of([("SKILL.md", Encoding.UTF8.GetBytes(Manifest)), ("scripts/run.py", Script)]);
        var source = $"github:{Repo}/skills/pdf@{Sha}";
        var manifest = SkillManifest.Read("pdf", await NoteAsync(alice, "/Skills/pdf/SKILL.md"));
        Assert.Equal(source, manifest.Source);
        Assert.Equal(hash, manifest.Hash);
        Assert.Equal("Reads PDFs.", manifest.Description);
        var skills = await AccessAsync(alice, "Skills");
        Assert.Equal(new SkillOrigin(skills.ToVault(VaultPath.Root.Child("Skills").Child("pdf")).Value, source, hash), Assert.Single((await RecordAsync(alice, "Skills")).Origins));

        // Any vault she may write: its _skills/, the sealed origin in that vault's record.
        using var work = await InstallAsync(alice, Install(vault: "Work"));
        Assert.Equal(HttpStatusCode.Created, work.StatusCode);
        Assert.Equal("/Work/_skills/pdf", (await RestClient.JsonAsync(work))["path"]!.GetValue<string>());
        Assert.Equal(["SKILL.md", "scripts/run.py"], await FilesAsync(alice, "/Work/_skills/pdf"));
        Assert.Equal("/Work/_skills/pdf", Assert.Single((await RecordAsync(alice, "Work")).Origins).Path);
    }

    [Fact]
    public async Task Install_takes_the_commit_it_was_given()
    {
        await using var api = Api();
        var alice = await UserAsync(api);
        using (var browse = await alice.GetAsync("/api/skills/marketplaces/alice-example/skills")) Assert.Equal(HttpStatusCode.OK, browse.StatusCode);

        // The branch moves on and the client's commit cache has forgotten it; the install keeps to what was shown.
        _github.Commit = Other;
        _time.Advance(TimeSpan.FromMinutes(11));
        var commits = CommitRequests;
        using var installed = await InstallAsync(alice, Install());

        Assert.Equal(HttpStatusCode.Created, installed.StatusCode);
        Assert.Equal($"github:{Repo}/skills/pdf@{Sha}", SkillManifest.Read("pdf", await NoteAsync(alice, "/Skills/pdf/SKILL.md")).Source);
        Assert.Equal(commits, CommitRequests);
        Assert.DoesNotContain(_github.Requests, r => r.Uri.ToString().Contains(Other, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Repo, "main", "skills/pdf")]
    [InlineData(Repo, "0123", "skills/pdf")]
    [InlineData(Repo, "0123456789ABCDEF0123456789ABCDEF01234567", "skills/pdf")]
    [InlineData(Repo, Sha, "../x")]
    [InlineData(Repo, Sha, ".github/x")]
    [InlineData("a/..", Sha, "skills/pdf")]
    public async Task Bad_commits_paths_and_repos_are_400(string repo, string commit, string path)
    {
        await using var api = Api();
        var alice = await UserAsync(api);

        // Refused by the route's own checks, before the request reaches the fetcher or the GitHub client.
        var sentence = repo != Repo ? "Give a GitHub repository as owner/repo."
            : !Commits.IsValid(commit) ? "Give the commit as its 40 lower-case hex digits."
            : "Give the skill's folder in the repository, as path.";
        using var installed = await InstallAsync(alice, Install(commit: commit, path: path, repo: repo));
        Assert.Equal(HttpStatusCode.BadRequest, installed.StatusCode);
        Assert.Equal(sentence, await RestClient.ErrorAsync(installed));
        if (repo == Repo)
        {
            using var preview = await alice.GetAsync($"/api/skills/marketplaces/{repo}/skill?commit={Uri.EscapeDataString(commit)}&path={Uri.EscapeDataString(path)}");
            Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
            Assert.Equal(sentence, await RestClient.ErrorAsync(preview));
        }
        else
        {
            using var added = await alice.SendAsync(HttpMethod.Post, "/api/skills/marketplaces", new { repo });
            Assert.Equal(HttpStatusCode.BadRequest, added.StatusCode);
            Assert.Equal(sentence, await RestClient.ErrorAsync(added));
        }

        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task A_name_that_escapes_is_400()
    {
        await using var api = Api();
        var alice = await UserAsync(api);

        foreach (var name in new[] { "../evil", "a/b", ".hidden", "" })
        {
            using var installed = await InstallAsync(alice, Install(name: name));
            Assert.Equal(HttpStatusCode.BadRequest, installed.StatusCode);
        }

        Assert.Empty(await FilesAsync(alice, "/Skills"));
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task A_taken_name_is_409()
    {
        await using var api = Api();
        var alice = await UserAsync(api);
        using (var first = await InstallAsync(alice, Install())) Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var again = await InstallAsync(alice, Install());

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(SkillCopier.NameTaken, await TypeAsync(again));
        using var renamed = await InstallAsync(alice, Install(name: "pdf-2"));
        Assert.Equal(HttpStatusCode.Created, renamed.StatusCode);
        Assert.Equal("/Skills/pdf-2", (await RestClient.JsonAsync(renamed))["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_skill_that_isnt_installable_is_400_with_its_reason()
    {
        _github.Archives[Repo]["skills/bad​name/SKILL.md"] = "---\nname: badname\ndescription: Hides.\n---\n"u8.ToArray();
        _github.Archives[Repo]["skills/broken/SKILL.md"] = [0xFF, 0xFE, 0x00];
        await using var api = Api();
        var alice = await UserAsync(api);

        using var hidden = await InstallAsync(alice, Install(path: "skills/bad​name", name: "badname"));
        Assert.Equal(HttpStatusCode.BadRequest, hidden.StatusCode);
        Assert.Contains("control or format character", await RestClient.ErrorAsync(hidden));

        using var broken = await InstallAsync(alice, Install(path: "skills/broken"));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Contains("SKILL.md", await RestClient.ErrorAsync(broken));

        using var none = await InstallAsync(alice, Install(path: "skills"));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Empty(await FilesAsync(alice, "/Skills"));
    }

    [Fact]
    public async Task Switched_off_hides_and_fetches_nothing()
    {
        await using var api = Api(enabled: false);
        var alice = await UserAsync(api);

        using var list = await alice.GetAsync("/api/skills/marketplaces");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.False((await RestClient.JsonAsync(list))["enabled"]!.GetValue<bool>());
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Post, "/api/skills/marketplaces", new { repo = Repo }),
                     (HttpMethod.Delete, "/api/skills/marketplaces/alice-example/skills", null),
                     (HttpMethod.Get, "/api/skills/marketplaces/alice-example/skills", null),
                     (HttpMethod.Get, $"/api/skills/marketplaces/alice-example/skills/skill?commit={Sha}&path=skills/pdf", null),
                     (HttpMethod.Post, "/api/skills/install", Install()),
                 })
        {
            using var response = await alice.SendAsync(method, path, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path}: {response.StatusCode}");
            Assert.Equal(MarketplaceApi.Off, await TypeAsync(response));
        }

        Assert.Empty(_github.Requests);
        Assert.Empty(await FilesAsync(alice, "/Skills"));
    }

    [Fact]
    public async Task The_rate_limit_answers_429_with_its_type()
    {
        _github.RateLimited = true;
        await using var api = Api();
        var alice = await UserAsync(api);

        using var browse = await alice.GetAsync("/api/skills/marketplaces/alice-example/skills");

        Assert.Equal(HttpStatusCode.TooManyRequests, browse.StatusCode);
        var body = await RestClient.JsonAsync(browse);
        Assert.Equal("github-rate-limited", body["type"]!.GetValue<string>());
        Assert.Equal("GitHub allows 60 requests an hour without a token; try again later, or ask your admin to set a GitHub token.", body["error"]!.GetValue<string>());

        _github.RateLimited = false;
        _github.NotFound = true;
        using var missing = await alice.GetAsync("/api/skills/marketplaces/alice-example/nothing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not-found", await TypeAsync(missing));

        _github.NotFound = false;
        _github.Redirect = "https://evil.example/x";
        using var redirected = await alice.GetAsync("/api/skills/marketplaces/alice-example/moved");
        Assert.Equal(HttpStatusCode.BadGateway, redirected.StatusCode);
        Assert.Equal("github-redirected", await TypeAsync(redirected));
    }

    [Fact]
    public async Task Too_many_fetches_by_one_user_answer_429_github_busy()
    {
        await using var api = Api();
        var alice = await UserAsync(api);
        for (var i = 0; i < AccountLimits.GitHubFetchesPerUser; i++)
        {
            using var browse = await alice.GetAsync("/api/skills/marketplaces/alice-example/skills");
            Assert.Equal(HttpStatusCode.OK, browse.StatusCode);
        }

        var before = _github.Requests.Count;
        using var busy = await alice.GetAsync("/api/skills/marketplaces/alice-example/skills");
        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        Assert.Equal(MarketplaceApi.Busy, await TypeAsync(busy));
        Assert.True(busy.Headers.RetryAfter is not null, "no Retry-After");
        using var install = await InstallAsync(alice, Install());
        Assert.Equal(HttpStatusCode.TooManyRequests, install.StatusCode);
        Assert.Equal(before, _github.Requests.Count);

        // Per user: Bob's requests go ahead.
        var bob = await UserAsync(api);
        using var his = await bob.GetAsync("/api/skills/marketplaces/alice-example/skills");
        Assert.Equal(HttpStatusCode.OK, his.StatusCode);
    }

    [Fact]
    public async Task An_api_key_cant_install()
    {
        await using var api = Api();
        var alice = await UserAsync(api);
        var key = await alice.KeyAsync("agent", KeyRight.Write);

        using var installed = await InstallAsync(alice, Install(vault: "Work"), key);
        Assert.Equal(HttpStatusCode.Forbidden, installed.StatusCode);
        using var browse = await alice.GetAsync("/api/skills/marketplaces/alice-example/skills", key);
        Assert.Equal(HttpStatusCode.Forbidden, browse.StatusCode);
        using var added = await alice.SendAsync(HttpMethod.Post, "/api/skills/marketplaces", new { repo = Repo }, key);
        Assert.Equal(HttpStatusCode.Forbidden, added.StatusCode);

        Assert.Empty(_github.Requests);
        Assert.Empty(await FilesAsync(alice, "/Work"));
    }

    [Fact]
    public async Task Parallel_fetches_of_one_commit_share_one_download()
    {
        var gate = new GatedHandler(_github);
        var options = Options.Create(new KumbuOptions());
        var client = new GitHubClient(new OneClient(gate), options, _time, NullLogger<GitHubClient>.Instance);
        var fetcher = new MarketplaceFetcher(client, _time);
        var repo = RepoName.Parse(Repo)!;

        var fetches = Enumerable.Range(0, 5).Select(_ => fetcher.ArchiveAsync(repo, Sha, Ct)).ToList();
        gate.Open();
        var archives = await Task.WhenAll(fetches);

        Assert.Equal(1, ArchiveRequests);
        Assert.All(archives, a => Assert.Same(archives[0], a));

        // Once it's done, the next one is the client's to answer from its cache.
        await fetcher.ArchiveAsync(repo, Sha, Ct);
        Assert.Equal(1, ArchiveRequests);
    }

    /// <summary>Holds every request until <see cref="Open"/>.</summary>
    private sealed class GatedHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _open.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _open.Task.WaitAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
