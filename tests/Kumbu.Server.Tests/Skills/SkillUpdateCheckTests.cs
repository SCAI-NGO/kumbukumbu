// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Skills;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Publishing;
using Kumbu.Server.Skills;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Skills;

/// <summary>The update check (skills spec §3.10, Decisions 22, 33): only the session's <c>POST /api/skills/updates/check</c>
/// asks a copy's source, and only the source sealed in the vault's record; the folders with an update are remembered, so
/// <c>skills ls</c> and <c>GET /api/skills</c> tell of them without fetching.</summary>
[Collection("app")]
public sealed class SkillUpdateCheckTests(KumbuAppFactory app)
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Newer = "fedcba9876543210fedcba9876543210fedcba98";
    private const string Repo = "alice-example/skills";
    private const string Source = $"github:{Repo}/skills/pdf@{Sha}";
    private const string Manifest = "---\nname: pdf\ndescription: Reads PDFs.\n---\n# PDF\n";
    private const string LinkManifest = "---\nname: write-as-alice\ndescription: Writes posts.\n---\n# Write\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeGitHub _github = Fake();

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private static FakeGitHub Fake()
    {
        var fake = new FakeGitHub { Commit = Sha };
        fake.Archives[Repo] = new Dictionary<string, byte[]>
        {
            ["README.md"] = "# Skills\n"u8.ToArray(),
            ["skills/pdf/SKILL.md"] = Encoding.UTF8.GetBytes(Manifest),
            ["skills/pdf/scripts/run.py"] = "print('hi')\n"u8.ToArray(),
        };
        return fake;
    }

    private WebApplicationFactory<Program> Api() => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.AddHttpClient(GitHubServices.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _github);
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    /// <summary>Alice, with skills/pdf of the marketplace installed at <see cref="Sha"/> into Work.</summary>
    private static async Task<RestClient> InstalledAsync(WebApplicationFactory<Program> api)
    {
        var alice = await RestClient.NewAsync(api, vault: "Work", remember: true);
        using var installed = await alice.SendAsync(HttpMethod.Post, "/api/skills/install", new { repo = Repo, commit = Sha, path = "skills/pdf", vault = "Work" });
        Assert.Equal(HttpStatusCode.Created, installed.StatusCode);
        return alice;
    }

    private static async Task<JsonArray> CheckAsync(RestClient user)
    {
        using var check = await user.SendAsync(HttpMethod.Post, "/api/skills/updates/check");
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        return (await RestClient.JsonAsync(check)).AsArray();
    }

    private static JsonNode StateOf(JsonArray states, string path) => Assert.Single(states, s => s!["path"]!.GetValue<string>() == path)!;

    private static async Task<PublishingRecord> RecordAsync(RestClient user, string vault)
    {
        var access = (await user.Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == vault);
        return (await user.Api.Services.GetRequiredService<PublicationStore>().ReadAsync(null, access.Vault, Ct)).Record;
    }

    /// <summary>An update recorded for <paramref name="folder"/> as by an earlier check.</summary>
    private static async Task SeedUpdateAsync(WebApplicationFactory<Program> api, RestClient user, string folder)
    {
        var access = (await api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == "Work");
        await api.Services.GetRequiredService<PublicationStore>().ChangeAsync(access, r => r with { Updates = [folder] }, Ct);
        Assert.Equal([folder], (await RecordAsync(user, "Work")).Updates);
    }

    private static async Task PutNoteAsync(RestClient user, string path, string content)
    {
        using var put = await user.SendAsync(HttpMethod.Put, "/api/notes" + path, new { content });
        Assert.True(put.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"{path}: {put.StatusCode}");
    }

    private static async Task<JsonNode> ViewAsync(RestClient user, string path)
    {
        using var list = await user.GetAsync("/api/skills");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        return (await RestClient.JsonAsync(list))["skills"]!.AsArray().Single(s => s!["path"]?.GetValue<string>() == path)!;
    }

    /// <summary>Alice's write-as-alice in her vault Skills, shared by link; Dave's copy of it in Writing; the link's id.</summary>
    private static async Task<(RestClient Alice, RestClient Dave, string Id)> LinkCopyAsync(WebApplicationFactory<Program> api)
    {
        var alice = await RestClient.NewAsync(api, vault: "Work");
        await PutNoteAsync(alice, "/Skills/write-as-alice/SKILL.md", LinkManifest);
        await PutNoteAsync(alice, "/Skills/write-as-alice/examples/post.md", "# Post\n");
        using var shared = await alice.SendAsync(HttpMethod.Post, "/api/skills/shared", new { path = "/Skills/write-as-alice" });
        Assert.Equal(HttpStatusCode.Created, shared.StatusCode);
        var link = (await RestClient.JsonAsync(shared))["link"]!.GetValue<string>();
        var dave = await RestClient.NewAsync(api, vault: "Writing");
        using var added = await dave.SendAsync(HttpMethod.Post, "/api/skills/add", new { code = link[(link.IndexOf('#') + 1)..], vault = "Writing" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        using var list = await alice.GetAsync("/api/skills/shared");
        return (alice, dave, (await RestClient.JsonAsync(list)).AsArray().Single()!["id"]!.GetValue<string>());
    }

    private static async Task UpdateLinkAsync(RestClient alice, string id)
    {
        await PutNoteAsync(alice, "/Skills/write-as-alice/examples/second.md", "# Second\n");
        using var updated = await alice.SendAsync(HttpMethod.Post, $"/api/skills/shared/{id}/update");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
    }

    [Fact]
    public async Task A_newer_commit_with_other_files_is_an_update()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        _github.Commit = Newer;
        _github.Archives[Repo]["skills/pdf/SKILL.md"] = Encoding.UTF8.GetBytes(Manifest + "More.\n");

        var state = StateOf(await CheckAsync(alice), "/Work/_skills/pdf");

        Assert.Equal("available", state["state"]!.GetValue<string>());
        Assert.Equal(Source, state["source"]!.GetValue<string>());
        Assert.Equal(Newer, state["commit"]!.GetValue<string>());
        Assert.Equal(["/Work/_skills/pdf"], (await RecordAsync(alice, "Work")).Updates);
        Assert.Equal("available", (await ViewAsync(alice, "/Work/_skills/pdf"))["update"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_newer_commit_with_the_same_files_is_current()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        Assert.Equal("current", StateOf(await CheckAsync(alice), "/Work/_skills/pdf")["state"]!.GetValue<string>());

        _github.Commit = Newer;
        _time.Advance(GitHubClient.CommitAge);
        _github.Archives[Repo]["README.md"] = "# Skills, now more\n"u8.ToArray();

        var state = StateOf(await CheckAsync(alice), "/Work/_skills/pdf");

        Assert.Equal("current", state["state"]!.GetValue<string>());
        Assert.Contains(_github.Requests, r => r.Uri.Host == GitHubClient.ArchiveHost && r.Uri.AbsolutePath.EndsWith(Newer, StringComparison.Ordinal));
        Assert.Empty((await RecordAsync(alice, "Work")).Updates);
        Assert.Null((await ViewAsync(alice, "/Work/_skills/pdf"))["update"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_skipped_update_is_shown_as_skipped()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        _github.Commit = Newer;
        var changed = Encoding.UTF8.GetBytes(Manifest + "More.\n");
        _github.Archives[Repo]["skills/pdf/SKILL.md"] = changed;
        var hash = SkillHash.Of([("SKILL.md", changed), ("scripts/run.py", "print('hi')\n"u8.ToArray())]);
        var access = (await api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(alice.User.Id, alice.UserKey, Ct)).Single(v => v.Name == "Work");
        await api.Services.GetRequiredService<PublicationStore>().ChangeAsync(access, r => r with { Skipped = [new SkippedUpdate("/Work/_skills/pdf", hash)] }, Ct);

        Assert.Equal("skipped", StateOf(await CheckAsync(alice), "/Work/_skills/pdf")["state"]!.GetValue<string>());
        Assert.Empty((await RecordAsync(alice, "Work")).Updates);
    }

    [Fact]
    public async Task A_link_whose_publication_changed_is_an_update()
    {
        await using var api = Api();
        var (alice, dave, id) = await LinkCopyAsync(api);
        Assert.Equal("current", StateOf(await CheckAsync(dave), "/Writing/_skills/write-as-alice")["state"]!.GetValue<string>());

        await UpdateLinkAsync(alice, id);

        using var check = await dave.SendAsync(HttpMethod.Post, "/api/skills/updates/check");
        var text = await check.Content.ReadAsStringAsync(Ct);
        var state = StateOf((await RestClient.JsonAsync(check)).AsArray(), "/Writing/_skills/write-as-alice");
        Assert.Equal("available", state["state"]!.GetValue<string>());
        Assert.StartsWith("link:", state["source"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(state["commit"]?.GetValue<string>());
        var code = Assert.Single((await RecordAsync(dave, "Writing")).SkillCodes).Code;
        Assert.DoesNotContain(code, text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task A_withdrawn_link_is_gone()
    {
        await using var api = Api();
        var (alice, dave, id) = await LinkCopyAsync(api);
        using var withdrawn = await alice.SendAsync(HttpMethod.Delete, $"/api/skills/shared/{id}");
        Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);

        Assert.Equal("gone", StateOf(await CheckAsync(dave), "/Writing/_skills/write-as-alice")["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_vanished_repository_is_gone()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        await SeedUpdateAsync(api, alice, "/Work/_skills/pdf");
        _github.NotFound = true;

        Assert.Equal("gone", StateOf(await CheckAsync(alice), "/Work/_skills/pdf")["state"]!.GetValue<string>());
        Assert.Empty((await RecordAsync(alice, "Work")).Updates);
    }

    [Fact]
    public async Task A_vanished_skill_folder_is_gone()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        _github.Commit = Newer;
        _github.Archives[Repo].Remove("skills/pdf/SKILL.md");

        Assert.Equal("gone", StateOf(await CheckAsync(alice), "/Work/_skills/pdf")["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_edited_origin_is_shown_and_never_fetched()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        var key = await alice.KeyAsync("agent", KeyRight.Write);
        var read = await alice.ReadAsync("_skills/pdf/SKILL.md");
        var content = read["content"]!.GetValue<string>();
        var hash = SkillManifest.Read("pdf", content).Hash!;
        var evil = SkillManifest.WithOrigin(content, $"github:bob-example/evil/x@{Newer}", hash);
        var written = await alice.ToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Work/_skills/pdf/SKILL.md", ["content"] = evil, ["version"] = read["version"]!.GetValue<string>() }, key);
        Assert.False(written.IsError ?? false, Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(written.Content[0]).Text);
        _github.Requests.Clear();

        var state = StateOf(await CheckAsync(alice), "/Work/_skills/pdf");

        Assert.Equal("origin-edited", state["state"]!.GetValue<string>());
        Assert.DoesNotContain(_github.Requests, r => r.Uri.AbsolutePath.Contains("bob-example", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_github.Requests);
        Assert.True((await ViewAsync(alice, "/Work/_skills/pdf"))["originEdited"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_source_only_the_frontmatter_names_is_never_fetched()
    {
        await using var api = Api();
        var alice = await RestClient.NewAsync(api, vault: "Work", remember: true);
        await PutNoteAsync(alice, "/Work/_skills/x/SKILL.md",
            $"---\nname: x\ndescription: Does x.\nmetadata:\n  kumbu-source: github:bob-example/evil/x@{Newer}\n  kumbu-hash: sha256:00\n---\n# X\n");

        var states = await CheckAsync(alice);

        Assert.Empty(states);
        Assert.Empty(_github.Requests);
        Assert.False((await ViewAsync(alice, "/Work/_skills/x"))["originEdited"]?.GetValue<bool>() ?? false);
    }

    [Fact]
    public async Task Skills_ls_says_update_available_after_a_check()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        var key = await alice.KeyAsync("agent", KeyRight.Read);
        _github.Commit = Newer;
        _github.Archives[Repo]["skills/pdf/SKILL.md"] = Encoding.UTF8.GetBytes(Manifest + "More.\n");

        var (_, before) = await alice.ShAsync("skills ls", key);
        Assert.DoesNotContain("update available", before, StringComparison.Ordinal);

        await CheckAsync(alice);
        var requests = _github.Requests.Count;

        var (isError, after) = await alice.ShAsync("skills ls", key);
        Assert.False(isError, after);
        var line = Assert.Single(after.Split('\n'), l => l.StartsWith("pdf (/Work/_skills/pdf", StringComparison.Ordinal));
        Assert.EndsWith("[update available: Skills screen]", line, StringComparison.Ordinal);
        Assert.Equal(requests, _github.Requests.Count);
    }

    [Fact]
    public async Task A_renamed_link_copy_is_still_checked()
    {
        await using var api = Api();
        var (alice, dave, id) = await LinkCopyAsync(api);
        using var move = await dave.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Writing/_skills/write-as-alice/", to = "/Writing/_skills/alice-voice/" });
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        await UpdateLinkAsync(alice, id);

        var states = await CheckAsync(dave);

        Assert.Equal("available", StateOf(states, "/Writing/_skills/alice-voice")["state"]!.GetValue<string>());
        Assert.DoesNotContain(states, s => s!["path"]!.GetValue<string>() == "/Writing/_skills/write-as-alice");
        Assert.Equal(["/Writing/_skills/alice-voice"], (await RecordAsync(dave, "Writing")).Updates);
    }

    [Fact]
    public async Task Nothing_is_checked_without_the_user()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        var key = await alice.KeyAsync("agent", KeyRight.Read);
        _github.Commit = Newer;
        var requests = _github.Requests.Count;

        using var list = await alice.GetAsync("/api/skills");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var (isError, ls) = await alice.ShAsync("skills ls", key);
        Assert.False(isError, ls);

        Assert.Equal(requests, _github.Requests.Count);
        Assert.Empty((await RecordAsync(alice, "Work")).Updates);
    }

    [Fact]
    public async Task An_agent_cant_check()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        var key = await alice.KeyAsync("agent", KeyRight.Write);
        var requests = _github.Requests.Count;

        using var check = await alice.SendAsync(HttpMethod.Post, "/api/skills/updates/check", bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, check.StatusCode);
        Assert.Equal(requests, _github.Requests.Count);
    }

    [Fact]
    public async Task A_check_counts_against_the_users_GitHub_limit()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        var limits = api.Services.GetRequiredService<AccountLimits>();
        while (limits.GitHubFetch(alice.User.Id) is null) { }

        using var check = await alice.SendAsync(HttpMethod.Post, "/api/skills/updates/check");

        Assert.Equal(HttpStatusCode.TooManyRequests, check.StatusCode);
        Assert.Equal(MarketplaceApi.Busy, (await RestClient.JsonAsync(check))["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_check_takes_a_token_per_repository_it_asks_and_records_nothing_when_they_run_out()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        _github.Archives["alice-example/more"] = new Dictionary<string, byte[]> { ["skills/zz/SKILL.md"] = "---\nname: zz\ndescription: Does zz.\n---\n"u8.ToArray() };
        using (var installed = await alice.SendAsync(HttpMethod.Post, "/api/skills/install", new { repo = "alice-example/more", commit = Sha, path = "skills/zz", vault = "Work" }))
        {
            Assert.Equal(HttpStatusCode.Created, installed.StatusCode);
        }

        _github.Commit = Newer;
        _github.Archives[Repo]["skills/pdf/SKILL.md"] = Encoding.UTF8.GetBytes(Manifest + "More.\n");
        var limits = api.Services.GetRequiredService<AccountLimits>();
        // Two installs took two; one is left for the check, which needs one for each of the two repositories.
        for (var i = 2; i < AccountLimits.GitHubFetchesPerUser - 1; i++) Assert.Null(limits.GitHubFetch(alice.User.Id));

        using var check = await alice.SendAsync(HttpMethod.Post, "/api/skills/updates/check");

        Assert.Equal(HttpStatusCode.TooManyRequests, check.StatusCode);
        Assert.Equal(MarketplaceApi.Busy, (await RestClient.JsonAsync(check))["type"]!.GetValue<string>());
        Assert.Contains(_github.Requests, r => r.Uri.AbsolutePath.Contains("/alice-example/skills/", StringComparison.Ordinal) && r.Uri.Host == GitHubClient.ApiHost);
        Assert.DoesNotContain(_github.Requests, r => r.Uri.AbsolutePath.Contains("/alice-example/more/", StringComparison.Ordinal) && r.Uri.Host == GitHubClient.ApiHost);
        Assert.Empty((await RecordAsync(alice, "Work")).Updates);
    }

    [Fact]
    public async Task A_check_of_cached_commits_or_links_alone_takes_no_token()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        Assert.Equal("current", StateOf(await CheckAsync(alice), "/Work/_skills/pdf")["state"]!.GetValue<string>());
        var limits = api.Services.GetRequiredService<AccountLimits>();
        while (limits.GitHubFetch(alice.User.Id) is null) { }

        // The commit is in the cache for ten minutes: no token needed.
        Assert.Equal("current", StateOf(await CheckAsync(alice), "/Work/_skills/pdf")["state"]!.GetValue<string>());

        var (_, dave, _) = await LinkCopyAsync(api);
        while (limits.GitHubFetch(dave.User.Id) is null) { }
        Assert.Equal("current", StateOf(await CheckAsync(dave), "/Writing/_skills/write-as-alice")["state"]!.GetValue<string>());

        _time.Advance(GitHubClient.CommitAge);
        using var check = await alice.SendAsync(HttpMethod.Post, "/api/skills/updates/check");
        Assert.Equal(HttpStatusCode.TooManyRequests, check.StatusCode);
    }
}
