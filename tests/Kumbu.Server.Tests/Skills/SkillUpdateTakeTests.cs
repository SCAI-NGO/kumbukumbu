// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Skills;
using Kumbu.Server.Accounts;
using Kumbu.Server.Publishing;
using Kumbu.Server.Skills;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Skills;

/// <summary>Review, Take update and Skip (skills spec §3.10): the session's own, never an agent's. Review diffs her copy as
/// it is now against the source's new version; Take writes exactly the version reviewed through her writers and rewrites
/// the origin, in the frontmatter and sealed; Skip hides the notice until the source changes again.</summary>
[Collection("app")]
public sealed class SkillUpdateTakeTests(KumbuAppFactory app)
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Newer = "fedcba9876543210fedcba9876543210fedcba98";
    private const string Newest = "aaaaaaaaaabbbbbbbbbbccccccccccdddddddddd";
    private const string Repo = "alice-example/skills";
    private const string Folder = "/Work/_skills/pdf";
    private const string Manifest = "---\nname: pdf\ndescription: Reads PDFs.\n---\n# PDF\n\nRead the file.\n";
    private const string NewManifest = "---\nname: pdf\ndescription: Reads PDFs.\n---\n# PDF\n\nRead the whole file.\n";
    private static readonly byte[] Run = "print('hi')\n"u8.ToArray();
    private static readonly byte[] NewScript = "print('new')\n"u8.ToArray();
    private static readonly byte[] Logo = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 0x1F, 0x15, 0xC4, 0x89];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeGitHub _github = Fake();

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private static FakeGitHub Fake()
    {
        var fake = new FakeGitHub { Commit = Sha };
        fake.Archives[Repo] = new Dictionary<string, byte[]>
        {
            ["skills/pdf/SKILL.md"] = Encoding.UTF8.GetBytes(Manifest),
            ["skills/pdf/scripts/run.py"] = Run,
            ["skills/pdf/logo.png"] = Logo,
        };
        return fake;
    }

    private WebApplicationFactory<Program> Api() => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.AddHttpClient(GitHubServices.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _github);
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    /// <summary>Alice, with skills/pdf installed at <see cref="Sha"/> into Work.</summary>
    private static async Task<RestClient> InstalledAsync(WebApplicationFactory<Program> api)
    {
        var alice = await RestClient.NewAsync(api, vault: "Work", remember: true);
        MarketplaceApiTests.Shown(api, Repo, Sha);
        using var installed = await alice.SendAsync(HttpMethod.Post, "/api/skills/install", new { repo = Repo, commit = Sha, path = "skills/pdf", vault = "Work" });
        Assert.Equal(HttpStatusCode.Created, installed.StatusCode);
        return alice;
    }

    /// <summary>The source at <paramref name="commit"/> (a commit the archive cache hasn't seen) with its new files.</summary>
    private void Publish(string commit, Dictionary<string, byte[]> files)
    {
        _github.Commit = commit;
        _github.Archives[Repo] = files.ToDictionary(f => "skills/pdf/" + f.Key, f => f.Value);
        _time.Advance(GitHubClient.CommitAge);
    }

    /// <summary>The next version: <c>SKILL.md</c> changed, <c>scripts/run.py</c> gone, <c>scripts/new.py</c> new, the logo as it was.</summary>
    private static Dictionary<string, byte[]> NextVersion() => new()
    {
        ["SKILL.md"] = Encoding.UTF8.GetBytes(NewManifest),
        ["scripts/new.py"] = NewScript,
        ["logo.png"] = Logo,
    };

    private static string HashOf(Dictionary<string, byte[]> files) => SkillHash.Of(files.Select(f => (f.Key, (ReadOnlyMemory<byte>)f.Value)));

    private static async Task<JsonNode> ReviewAsync(RestClient user, string folder = Folder)
    {
        using var review = await user.GetAsync("/api/skills/updates/review?path=" + Uri.EscapeDataString(folder));
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        return await RestClient.JsonAsync(review);
    }

    private static Task<HttpResponseMessage> TakeAsync(RestClient user, string hash, string folder = Folder, string? bearer = null) =>
        user.SendAsync(HttpMethod.Post, "/api/skills/updates/take", new { path = folder, hash }, bearer: bearer);

    private static async Task<JsonArray> CheckAsync(RestClient user)
    {
        using var check = await user.SendAsync(HttpMethod.Post, "/api/skills/updates/check");
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        return (await RestClient.JsonAsync(check)).AsArray();
    }

    private static string StateOf(JsonArray states, string path) =>
        Assert.Single(states, s => s!["path"]!.GetValue<string>() == path)!["state"]!.GetValue<string>();

    private static JsonNode FileOf(JsonNode review, string path) =>
        Assert.Single(review["files"]!.AsArray(), f => f!["path"]!.GetValue<string>() == path)!;

    private static string[] Hunks(JsonNode file) => [.. file["hunks"]!.AsArray().Select(h => h!.GetValue<string>())];

    private static async Task<PublishingRecord> RecordAsync(RestClient user, string vault)
    {
        var access = (await user.Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == vault);
        return (await user.Api.Services.GetRequiredService<PublicationStore>().ReadAsync(null, access.Vault, Ct)).Record;
    }

    private static async Task PutNoteAsync(RestClient user, string path, string content, string? version = null)
    {
        using var put = await user.SendAsync(HttpMethod.Put, "/api/notes" + path, new { content, version });
        Assert.True(put.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"{path}: {put.StatusCode}");
    }

    private static async Task<string> ManifestAsync(RestClient user) => (await user.ReadAsync("_skills/pdf/SKILL.md"))["content"]!.GetValue<string>();

    [Fact]
    public async Task Review_names_repo_path_and_commit()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        var next = NextVersion();
        Publish(Newer, next);

        var review = await ReviewAsync(alice);

        Assert.Equal($"github:{Repo}/skills/pdf@{Newer}", review["source"]!.GetValue<string>());
        Assert.Equal(Repo, review["repo"]!.GetValue<string>());
        Assert.Equal("skills/pdf", review["path"]!.GetValue<string>());
        Assert.Equal(Newer, review["commit"]!.GetValue<string>());
        Assert.Equal(HashOf(next), review["hash"]!.GetValue<string>());
        Assert.Equal(["SKILL.md", "logo.png", "scripts/new.py", "scripts/run.py"], review["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal("changed", FileOf(review, "SKILL.md")["change"]!.GetValue<string>());
        Assert.Equal("added", FileOf(review, "scripts/new.py")["change"]!.GetValue<string>());
        Assert.Contains("+print('new')", Hunks(FileOf(review, "scripts/new.py")));
        Assert.Equal("removed", FileOf(review, "scripts/run.py")["change"]!.GetValue<string>());
        Assert.Contains("-print('hi')", Hunks(FileOf(review, "scripts/run.py")));
        Assert.Equal("same", FileOf(review, "logo.png")["change"]!.GetValue<string>());
        Assert.Empty(Hunks(FileOf(review, "logo.png")));
        // The frontmatter as Take would write it: the new commit, the new hash.
        var hunks = Hunks(FileOf(review, "SKILL.md"));
        Assert.Contains(hunks, h => h.StartsWith("+  kumbu-source: ", StringComparison.Ordinal) && h.Contains($"github:{Repo}/skills/pdf@{Newer}", StringComparison.Ordinal));
        Assert.Contains(hunks, h => h.StartsWith("-  kumbu-source: ", StringComparison.Ordinal) && h.Contains($"github:{Repo}/skills/pdf@{Sha}", StringComparison.Ordinal));
        Assert.Contains("+Read the whole file.", hunks);
    }

    [Fact]
    public async Task Review_shows_the_users_own_edits_too()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        var read = await alice.ReadAsync("_skills/pdf/SKILL.md");
        await PutNoteAsync(alice, "/Work/_skills/pdf/SKILL.md", read["content"]!.GetValue<string>().Replace("Read the file.", "Read the file, Alice's way."), read["version"]!.GetValue<string>());
        Publish(Newer, NextVersion());

        var hunks = Hunks(FileOf(await ReviewAsync(alice), "SKILL.md"));

        Assert.Contains("-Read the file, Alice's way.", hunks);
        Assert.Contains("+Read the whole file.", hunks);
        Assert.DoesNotContain("-Read the file.", hunks);
    }

    [Fact]
    public async Task Take_replaces_the_folder_and_keeps_history()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        var before = await ManifestAsync(alice);
        var next = NextVersion();
        Publish(Newer, next);
        var review = await ReviewAsync(alice);

        using var take = await TakeAsync(alice, review["hash"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, take.StatusCode);
        Assert.Equal(SkillManifest.Body(NewManifest), SkillManifest.Body(await ManifestAsync(alice)));
        using var script = await alice.GetAsync(alice.Url("_skills/pdf/scripts/new.py", "files"));
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal(NewScript, await script.Content.ReadAsByteArrayAsync(Ct));
        using var gone = await alice.GetAsync(alice.Url("_skills/pdf/scripts/run.py", "files"));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        using var trash = await alice.GetAsync("/api/vaults/Work/trash");
        Assert.Contains((await RestClient.JsonAsync(trash))["notes"]!.AsArray(), e => e!["path"]!.GetValue<string>() == "/Work/_skills/pdf/scripts/run.py");
        using var history = await alice.GetAsync("/api/history/Work/_skills/pdf/SKILL.md");
        var versions = (await RestClient.JsonAsync(history)).AsArray();
        Assert.Equal(2, versions.Count);
        var previous = versions.Single(v => !v!["current"]!.GetValue<bool>())!["version"]!.GetValue<string>();
        using var old = await alice.GetAsync(alice.Url("_skills/pdf/SKILL.md") + "?version=" + Uri.EscapeDataString(previous));
        Assert.Equal(before, (await RestClient.JsonAsync(old))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Take_rewrites_the_origin_fields_and_the_sealed_origin()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        var next = NextVersion();
        Publish(Newer, next);
        Assert.Equal("available", StateOf(await CheckAsync(alice), Folder));
        var access = (await api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(alice.User.Id, alice.UserKey, Ct)).Single(v => v.Name == "Work");
        await api.Services.GetRequiredService<PublicationStore>().ChangeAsync(access, r => r with { Skipped = [new SkippedUpdate(Folder, "sha256:" + new string('0', 64))] }, Ct);
        var hash = (await ReviewAsync(alice))["hash"]!.GetValue<string>();

        using var take = await TakeAsync(alice, hash);

        Assert.Equal(HttpStatusCode.OK, take.StatusCode);
        var source = $"github:{Repo}/skills/pdf@{Newer}";
        var manifest = SkillManifest.Read("pdf", await ManifestAsync(alice));
        Assert.Equal(source, manifest.Source);
        Assert.Equal(HashOf(next), manifest.Hash);
        var record = await RecordAsync(alice, "Work");
        Assert.Equal(new SkillOrigin(Folder, source, HashOf(next)), Assert.Single(record.Origins));
        Assert.Empty(record.Updates);
        Assert.Empty(record.Skipped);
        Assert.Equal("current", StateOf(await CheckAsync(alice), Folder));
    }

    [Fact]
    public async Task Take_refuses_a_source_that_moved_on()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        Publish(Newer, NextVersion());
        var hash = (await ReviewAsync(alice))["hash"]!.GetValue<string>();
        var before = await ManifestAsync(alice);
        var newest = NextVersion();
        newest["SKILL.md"] = Encoding.UTF8.GetBytes(NewManifest + "And more.\n");
        Publish(Newest, newest);

        using var take = await TakeAsync(alice, hash);

        Assert.Equal(HttpStatusCode.Conflict, take.StatusCode);
        Assert.Equal("moved-on", (await RestClient.JsonAsync(take))["type"]!.GetValue<string>());
        Assert.Equal(before, await ManifestAsync(alice));
        Assert.Equal($"github:{Repo}/skills/pdf@{Sha}", Assert.Single((await RecordAsync(alice, "Work")).Origins).Source);
    }

    private async Task<string[]> FilesAsync(RestClient user)
    {
        var store = new EncryptedNoteStore(user.Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey));
        var folder = Kumbu.Core.Paths.VaultPath.TryParse(Folder, out var p) ? p : throw new InvalidOperationException();
        var notes = await store.ListNotesUnderAsync(folder, Ct);
        var files = await Kumbu.Core.Attachments.StoreAttachments.UnderAsync(store, folder, Ct);
        return [.. notes.Concat(files.Select(f => f.Path)).Select(f => f.Value[(Folder.Length + 1)..]).Order(StringComparer.Ordinal)];
    }

    /// <summary>An upstream that outgrew a skill's limits is refused as an install would refuse it, before anything is written.</summary>
    [Theory]
    [InlineData("files")]
    [InlineData("bytes")]
    public async Task Take_refuses_an_upstream_over_the_install_limits(string limit)
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        var next = NextVersion();
        if (limit == "files")
        {
            for (var i = 0; i < MarketplaceIndex.MaxSkillFiles; i++) next[$"references/r{i:000}.md"] = "x\n"u8.ToArray();
        }
        else
        {
            foreach (var name in new[] { "a.pdf", "b.pdf" })
            {
                var pdf = new byte[6 * 1024 * 1024];
                "%PDF-1.4\n"u8.CopyTo(pdf);
                next[$"assets/{name}"] = pdf;
            }
        }

        Publish(Newer, next);
        var (manifest, files) = (await ManifestAsync(alice), await FilesAsync(alice));

        using var review = await alice.GetAsync("/api/skills/updates/review?path=" + Uri.EscapeDataString(Folder));
        using var take = await TakeAsync(alice, HashOf(next));

        foreach (var refused in new[] { review, take })
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = await RestClient.JsonAsync(refused);
            Assert.Equal(SkillUpdateTaker.NotInstallable, body["type"]!.GetValue<string>());
            Assert.Contains(limit == "files" ? $"at most {MarketplaceIndex.MaxSkillFiles}" : "at most 10 MB", body["error"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        Assert.Equal(manifest, await ManifestAsync(alice));
        Assert.Equal(files, await FilesAsync(alice));
        Assert.Equal($"github:{Repo}/skills/pdf@{Sha}", Assert.Single((await RecordAsync(alice, "Work")).Origins).Source);
    }

    [Fact]
    public async Task Take_refuses_an_upstream_folder_whose_name_cant_be_kept()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        // A copy whose sealed source names a folder with a zero-width space, as install would never have taken it.
        const string hidden = "skills/bad\u200Bname";
        var source = $"github:{Repo}/{hidden}@{Sha}";
        var content = await ManifestAsync(alice);
        var hash = SkillManifest.Read("pdf", content).Hash!;
        await PutNoteAsync(alice, "/Work/_skills/pdf/SKILL.md", SkillManifest.WithOrigin(content, source, hash), (await alice.ReadAsync("_skills/pdf/SKILL.md"))["version"]!.GetValue<string>());
        var work = (await alice.Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(alice.User.Id, alice.UserKey, Ct)).Single(v => v.Name == "Work");
        await alice.Api.Services.GetRequiredService<PublicationStore>().ChangeAsync(work, r => r with { Origins = [new(Folder, source, hash)] }, Ct);
        var next = NextVersion();
        _github.Commit = Newer;
        _github.Archives[Repo] = next.ToDictionary(f => hidden + "/" + f.Key, f => f.Value);
        _time.Advance(GitHubClient.CommitAge);
        var before = await FilesAsync(alice);

        using var take = await TakeAsync(alice, HashOf(next));

        Assert.Equal(HttpStatusCode.Conflict, take.StatusCode);
        var body = await RestClient.JsonAsync(take);
        Assert.Equal(SkillUpdateTaker.NotInstallable, body["type"]!.GetValue<string>());
        Assert.Contains("control or format character", body["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(before, await FilesAsync(alice));
    }

    [Fact]
    public async Task Take_refuses_an_edited_origin()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        var next = NextVersion();
        Publish(Newer, next);
        var key = await alice.KeyAsync("agent", KeyRight.Write);
        var read = await alice.ReadAsync("_skills/pdf/SKILL.md");
        var content = read["content"]!.GetValue<string>();
        var evil = SkillManifest.WithOrigin(content, $"github:bob-example/evil/x@{Newer}", SkillManifest.Read("pdf", content).Hash!);
        var written = await alice.ToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Work/_skills/pdf/SKILL.md", ["content"] = evil, ["version"] = read["version"]!.GetValue<string>() }, key);
        Assert.False(written.IsError ?? false);
        _github.Requests.Clear();

        using var take = await TakeAsync(alice, HashOf(next));

        Assert.Equal(HttpStatusCode.Conflict, take.StatusCode);
        Assert.Equal("origin-edited", (await RestClient.JsonAsync(take))["type"]!.GetValue<string>());
        Assert.Empty(_github.Requests);
        Assert.Equal(evil, await ManifestAsync(alice));
    }

    [Fact]
    public async Task Skip_hides_it_until_the_source_changes_again()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        Publish(Newer, NextVersion());
        Assert.Equal("available", StateOf(await CheckAsync(alice), Folder));
        var hash = (await ReviewAsync(alice))["hash"]!.GetValue<string>();

        using var skip = await alice.SendAsync(HttpMethod.Post, "/api/skills/updates/skip", new { path = Folder, hash });

        Assert.Equal(HttpStatusCode.NoContent, skip.StatusCode);
        var record = await RecordAsync(alice, "Work");
        Assert.Equal(new SkippedUpdate(Folder, hash), Assert.Single(record.Skipped));
        Assert.Empty(record.Updates);
        Assert.Equal("skipped", StateOf(await CheckAsync(alice), Folder));

        var newest = NextVersion();
        newest["SKILL.md"] = Encoding.UTF8.GetBytes(NewManifest + "And more.\n");
        Publish(Newest, newest);
        Assert.Equal("available", StateOf(await CheckAsync(alice), Folder));
    }

    [Fact]
    public async Task A_link_copy_takes_the_updated_publication()
    {
        await using var api = Api();
        var alice = await RestClient.NewAsync(api, vault: "Work");
        await PutNoteAsync(alice, "/Skills/write-as-alice/SKILL.md", "---\nname: write-as-alice\ndescription: Writes posts.\n---\n# Write\n");
        using var shared = await alice.SendAsync(HttpMethod.Post, "/api/skills/shared", new { path = "/Skills/write-as-alice" });
        var link = (await RestClient.JsonAsync(shared))["link"]!.GetValue<string>();
        var code = link[(link.IndexOf('#') + 1)..];
        var dave = await RestClient.NewAsync(api, vault: "Writing");
        using var added = await dave.SendAsync(HttpMethod.Post, "/api/skills/add", new { code, vault = "Writing" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        await PutNoteAsync(alice, "/Skills/write-as-alice/examples/post.md", "# Post\n");
        using var list = await alice.GetAsync("/api/skills/shared");
        var id = (await RestClient.JsonAsync(list)).AsArray().Single()!["id"]!.GetValue<string>();
        using var updated = await alice.SendAsync(HttpMethod.Post, $"/api/skills/shared/{id}/update");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        const string folder = "/Writing/_skills/write-as-alice";

        using var reviewed = await dave.GetAsync("/api/skills/updates/review?path=" + Uri.EscapeDataString(folder));
        var text = await reviewed.Content.ReadAsStringAsync(Ct);
        var review = JsonNode.Parse(text)!;
        var sealedCode = Assert.Single((await RecordAsync(dave, "Writing")).SkillCodes).Code;
        Assert.StartsWith("link:", review["source"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(review["repo"]);
        Assert.Null(review["commit"]);
        Assert.Equal("added", FileOf(review, "examples/post.md")["change"]!.GetValue<string>());
        Assert.DoesNotContain(code, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sealedCode, text, StringComparison.OrdinalIgnoreCase);

        using var take = await TakeAsync(dave, review["hash"]!.GetValue<string>(), folder);

        Assert.Equal(HttpStatusCode.OK, take.StatusCode);
        var taken = await take.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(code, taken, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sealedCode, taken, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("# Post\n", (await dave.ReadAsync("_skills/write-as-alice/examples/post.md"))["content"]!.GetValue<string>());
        var record = await RecordAsync(dave, "Writing");
        Assert.Equal(review["hash"]!.GetValue<string>(), Assert.Single(record.Origins).Hash);
        Assert.Equal(sealedCode, Assert.Single(record.SkillCodes).Code);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task An_agent_cant_take_an_update()
    {
        await using var api = Api();
        var alice = await InstalledAsync(api);
        var next = NextVersion();
        Publish(Newer, next);
        var key = await alice.KeyAsync("agent", KeyRight.Write);
        var before = await ManifestAsync(alice);
        _github.Requests.Clear();

        using var take = await TakeAsync(alice, HashOf(next), bearer: key);
        using var review = await alice.GetAsync("/api/skills/updates/review?path=" + Uri.EscapeDataString(Folder), bearer: key);
        using var skip = await alice.SendAsync(HttpMethod.Post, "/api/skills/updates/skip", new { path = Folder, hash = HashOf(next) }, bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, take.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, review.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, skip.StatusCode);
        Assert.Empty(_github.Requests);
        Assert.Equal(before, await ManifestAsync(alice));
        Assert.Empty((await RecordAsync(alice, "Work")).Skipped);
    }
}
