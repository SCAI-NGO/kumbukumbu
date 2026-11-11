// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;
using Kumbu.Core.Skills;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Publishing;
using Kumbu.Server.Sharing;
using Kumbu.Server.Skills;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Publishing;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Skills;

/// <summary>Add to my skills: <c>POST /api/skills/add</c> writes the copy behind a link into a vault the caller may write,
/// with its origin in <c>SKILL.md</c> and the code only sealed in that vault's record (skills design §3.12, Decisions 6, 16, 33, 34).</summary>
[Collection("app")]
public sealed class AddSkillTests(KumbuAppFactory app)
{
    private const string Manifest = "---\nname: write-as-alice\ndescription: Writes posts.\n---\n# Write\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private static async Task PutNoteAsync(RestClient user, string path, string content)
    {
        using var put = await user.SendAsync(HttpMethod.Put, "/api/notes" + path, new { content });
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
    }

    private static async Task<string> NoteAsync(RestClient user, string path)
    {
        using var get = await user.GetAsync("/api/notes" + path);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        return (await RestClient.JsonAsync(get))["content"]!.GetValue<string>();
    }

    /// <summary>Alice's skill write-as-alice in her vault Skills, shared by link; the code, normalised.</summary>
    private async Task<(RestClient Alice, string Code)> AlicesLinkAsync(string manifest = Manifest, string post = "# Post\n")
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await PutNoteAsync(alice, "/Skills/write-as-alice/SKILL.md", manifest);
        await PutNoteAsync(alice, "/Skills/write-as-alice/examples/post.md", post);
        using var shared = await alice.SendAsync(HttpMethod.Post, "/api/skills/shared", new { path = "/Skills/write-as-alice" });
        Assert.Equal(HttpStatusCode.Created, shared.StatusCode);
        var link = (await RestClient.JsonAsync(shared))["link"]!.GetValue<string>();
        return (alice, ShareCode.Parse(link[(link.IndexOf('#') + 1)..])!);
    }

    /// <summary>No part of the code, in either spelling, without the text or the code in the message should it fail.</summary>
    private static void AssertNoCode(string text, string code)
    {
        var plain = text.Replace("-", "", StringComparison.Ordinal);
        for (var i = 0; i + 8 <= code.Length; i++)
        {
            Assert.False(plain.Contains(code.Substring(i, 8), StringComparison.OrdinalIgnoreCase), "a part of the code is in the text");
        }
    }

    private static Task<HttpResponseMessage> AddAsync(RestClient user, object body, string? bearer = null) => user.SendAsync(HttpMethod.Post, "/api/skills/add", body, bearer);

    private static async Task<VaultAccess> AccessAsync(RestClient user, string name) =>
        (await user.Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == name);

    private static async Task<PublishingRecord> RecordAsync(RestClient user, string vault) =>
        (await user.Api.Services.GetRequiredService<PublicationStore>().ReadAsync(null, (await AccessAsync(user, vault)).Vault, Ct)).Record;

    private static async Task<string[]> FilesAsync(RestClient user, string folder)
    {
        var store = new EncryptedNoteStore(user.Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey));
        var notes = await store.ListNotesUnderAsync(P(folder), Ct);
        var files = await StoreAttachments.UnderAsync(store, P(folder), Ct);
        return [.. notes.Concat(files.Select(f => f.Path)).Select(p => p.Value[(folder.Length + 1)..]).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Dave_adds_Alices_skill_with_its_origin_and_without_the_code()
    {
        var (_, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");
        using var added = await AddAsync(dave, new { code = ShareCode.Format(code), vault = "Writing" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var answer = await added.Content.ReadAsStringAsync(Ct);
        Assert.Contains("/Writing/_skills/write-as-alice", answer);
        AssertNoCode(answer, code);
        var content = await NoteAsync(dave, "/Writing/_skills/write-as-alice/SKILL.md");
        var manifest = SkillManifest.Read("write-as-alice", content);
        Assert.True(manifest.Source == "link:" + Convert.ToHexStringLower(ShareCode.Lookup(code)), "kumbu-source isn't the link's lookup id");
        Assert.Equal(
            SkillHash.Of([("SKILL.md", Encoding.UTF8.GetBytes(Manifest)), ("examples/post.md", "# Post\n"u8.ToArray())]),
            manifest.Hash);
        AssertNoCode(content, code);
        Assert.Equal("# Post\n", await NoteAsync(dave, "/Writing/_skills/write-as-alice/examples/post.md"));
    }

    [Fact]
    public async Task Into_Skills_by_default_name_it_lands_in_the_marked_vault()
    {
        var (_, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");
        using var added = await AddAsync(dave, new { code, vault = "Skills" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal("/Skills/write-as-alice", (await RestClient.JsonAsync(added))["path"]!.GetValue<string>());
        Assert.Equal(["SKILL.md", "examples/post.md"], await FilesAsync(dave, "/Skills/write-as-alice"));
    }

    [Fact]
    public async Task The_default_name_is_the_folders_never_the_manifests()
    {
        var (_, code) = await AlicesLinkAsync("---\nname: ../evil\ndescription: Writes posts.\n---\n# Write\n");
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");

        using var added = await AddAsync(dave, new { code, vault = "Writing" });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal("/Writing/_skills/write-as-alice", (await RestClient.JsonAsync(added))["path"]!.GetValue<string>());
        Assert.Equal(["_skills/write-as-alice/SKILL.md", "_skills/write-as-alice/examples/post.md"], await FilesAsync(dave, "/Writing"));
        Assert.Empty(await FilesAsync(dave, "/Skills"));
        Assert.Equal("/Writing/_skills/write-as-alice", Assert.Single((await RecordAsync(dave, "Writing")).Origins).Path);
    }

    [Fact]
    public async Task A_received_note_keeps_its_bytes_and_its_hash()
    {
        // Every write path stores a note's \r\n as \n, so a shared note holds none; a byte order mark is content. The copy
        // stores the bytes the link gave, which are the bytes its hash covers.
        const string post = "\uFEFF# Post\r\n\r\nA line.\r\n";
        var (alice, code) = await AlicesLinkAsync(post: post);
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");

        using (var added = await AddAsync(dave, new { code, vault = "Writing" })) Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        var shared = await NoteAsync(alice, "/Skills/write-as-alice/examples/post.md");
        var copied = await NoteAsync(dave, "/Writing/_skills/write-as-alice/examples/post.md");
        Assert.Equal(shared, copied);
        Assert.Equal("\uFEFF# Post\n\nA line.\n", copied);
        var hash = SkillHash.Of([
            ("SKILL.md", Encoding.UTF8.GetBytes(await NoteAsync(alice, "/Skills/write-as-alice/SKILL.md"))),
            ("examples/post.md", Encoding.UTF8.GetBytes(copied)),
        ]);
        Assert.Equal(hash, SkillManifest.Read("write-as-alice", await NoteAsync(dave, "/Writing/_skills/write-as-alice/SKILL.md")).Hash);
        Assert.Equal(hash, Assert.Single((await RecordAsync(dave, "Writing")).Origins).Hash);
    }

    [Fact]
    public async Task The_code_is_kept_sealed_in_the_target_vaults_record()
    {
        var (_, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");
        using (var added = await AddAsync(dave, new { code, vault = "Writing" })) Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var vault = (await AccessAsync(dave, "Writing")).Vault;
        var raw = await app.Database.GetCollection<SealedDocument>(PublicationStore.RecordCollection).Find(d => d.Vault == vault.Id).SingleAsync(Ct);
        Assert.Equal(-1, raw.Blob.AsSpan().IndexOf(Encoding.UTF8.GetBytes(code)));
        var record = await RecordAsync(dave, "Writing");
        Assert.True(record.SkillCodes.Single(c => c.Path == "/Writing/_skills/write-as-alice").Code == code, "the sealed code isn't the link's");
        var origin = Assert.Single(record.Origins);
        Assert.Equal("/Writing/_skills/write-as-alice", origin.Path);
        Assert.True(origin.Source == "link:" + Convert.ToHexStringLower(ShareCode.Lookup(code)), "the sealed origin isn't the link's lookup id");
        Assert.Equal(SkillManifest.Read("write-as-alice", await NoteAsync(dave, "/Writing/_skills/write-as-alice/SKILL.md")).Hash, origin.Hash);
    }

    [Fact]
    public async Task A_taken_name_is_409()
    {
        var (_, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");
        await PutNoteAsync(dave, "/Writing/_skills/write-as-alice/SKILL.md", "---\nname: write-as-alice\ndescription: Mine.\n---\n");

        using (var taken = await AddAsync(dave, new { code, vault = "Writing" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            var body = await RestClient.JsonAsync(taken);
            Assert.Equal(SkillCopier.NameTaken, body["type"]!.GetValue<string>());
            AssertNoCode(body.ToJsonString(), code);
        }

        Assert.Contains("description: Mine.", await NoteAsync(dave, "/Writing/_skills/write-as-alice/SKILL.md"));
        Assert.Empty((await RecordAsync(dave, "Writing")).SkillCodes);

        // Another name resolves it; the folder's name is the one given, the manifest's stays.
        using var other = await AddAsync(dave, new { code, vault = "Writing", name = "alices-posts" });
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.Equal("/Writing/_skills/alices-posts", (await RestClient.JsonAsync(other))["path"]!.GetValue<string>());
        Assert.Equal(["SKILL.md", "examples/post.md"], await FilesAsync(dave, "/Writing/_skills/alices-posts"));
        Assert.Equal("/Writing/_skills/alices-posts", Assert.Single((await RecordAsync(dave, "Writing")).SkillCodes).Path);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    public async Task A_name_that_escapes_is_400(string name)
    {
        var (_, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");

        using var response = await AddAsync(dave, new { code, vault = "Writing", name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNoCode(await response.Content.ReadAsStringAsync(Ct), code);
        Assert.Empty(await FilesAsync(dave, "/Writing"));
        Assert.Empty((await RecordAsync(dave, "Writing")).SkillCodes);
    }

    [Fact]
    public async Task A_wrong_code_is_404_and_counts()
    {
        var (_, code) = await AlicesLinkAsync();
        await using var api = ReaderApiTests.Limited(app.PublicApi);
        var dave = await RestClient.NewAsync(api, vault: "Writing");
        for (var i = 0; i < ReaderApiTests.FailuresPerNetwork; i++)
        {
            using var wrong = await SendFromAsync(api, dave, new { code = ShareCode.New(), vault = "Writing" }, $"203.0.113.{40 + i}");
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
            Assert.Equal(SkillPageApi.NotFoundSentence, await RestClient.ErrorAsync(wrong));
        }

        using (var held = await SendFromAsync(api, dave, new { code, vault = "Writing" }, "203.0.113.50"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, held.StatusCode);
            Assert.NotNull(held.Headers.RetryAfter);
        }

        Assert.Empty(await FilesAsync(dave, "/Writing"));
        using var other = await SendFromAsync(api, dave, new { code, vault = "Writing" }, "198.51.100.50");
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    /// <summary>As the session of <paramref name="user"/> (none without one), from <paramref name="ip"/>, with <paramref name="origin"/>.</summary>
    private static async Task<HttpResponseMessage> SendFromAsync(
        WebApplicationFactory<Program> api, RestClient? user, object body, string ip, string path = "/api/skills/add", string origin = RestClient.Origin)
    {
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (user is not null) request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={user.Session}");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Forwarded-For", ip);
        var response = await client.SendAsync(request, Ct);
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }

    [Fact]
    public async Task It_needs_a_session()
    {
        var (_, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");
        var key = await dave.KeyAsync("agent", KeyRight.Write);

        using var response = await AddAsync(dave, new { code, vault = "Writing" }, bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await FilesAsync(dave, "/Writing"));
    }

    [Fact]
    public async Task A_read_only_target_is_403()
    {
        var (alice, code) = await AlicesLinkAsync();
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Private");
        await alice.ShareWithAsync(bob, "Garden", MemberRole.Read);

        using var response = await AddAsync(bob, new { code, vault = "Garden" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You can only read vault Garden.", await RestClient.ErrorAsync(response));
        Assert.Empty(await FilesAsync(alice, "/Work"));
    }

    [Fact]
    public async Task A_members_writable_vault_takes_the_copy_and_its_sealed_state()
    {
        var (alice, code) = await AlicesLinkAsync();
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Private");
        await alice.ShareWithAsync(bob, "Garden", MemberRole.Write);

        using var response = await AddAsync(bob, new { code, vault = "Garden" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/Garden/_skills/write-as-alice", (await RestClient.JsonAsync(response))["path"]!.GetValue<string>());
        Assert.Equal(["SKILL.md", "examples/post.md"], await FilesAsync(alice, "/Work/_skills/write-as-alice"));
        var work = await RecordAsync(alice, "Work");
        Assert.Equal([new SkillCode("/Work/_skills/write-as-alice", code)], work.SkillCodes);
        Assert.Equal("/Work/_skills/write-as-alice", Assert.Single(work.Origins).Path);
        Assert.Empty((await RecordAsync(bob, "Skills")).SkillCodes);
    }

    [Fact]
    public async Task The_quota_applies()
    {
        var (_, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");
        // No room for the copy: the add fails and leaves nothing, nor its sealed state.
        var quota = dave.Api.Services.GetRequiredService<StorageQuota>();
        var used = (await quota.FigureAsync(dave.User.Id, Ct)).Used;
        var users = dave.Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<UserDocument>("users");
        await users.UpdateOneAsync(u => u.Id == dave.User.Id, Builders<UserDocument>.Update.Set(u => u.Quota, used + 1), cancellationToken: Ct);
        await quota.FigureAsync(dave.User.Id, Ct);

        using var response = await AddAsync(dave, new { code, vault = "Writing" });

        Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
        Assert.Empty(await FilesAsync(dave, "/Writing/_skills"));
        Assert.Empty((await RecordAsync(dave, "Writing")).SkillCodes);
    }

    private static Task<HttpResponseMessage> PreviewAsync(RestClient user, object body, string? bearer = null) =>
        user.SendAsync(HttpMethod.Post, "/api/skills/add/preview", body, bearer);

    /// <summary>The link's snapshot time as the preview gives it, which the add sends back.</summary>
    private static async Task<string> ShownAsync(RestClient user, string code)
    {
        using var preview = await PreviewAsync(user, new { code });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        return (await RestClient.JsonAsync(preview))["snapshotAt"]!.GetValue<string>();
    }

    [Fact]
    public async Task The_add_preview_shows_the_skill_of_a_code()
    {
        var (_, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");

        using var preview = await PreviewAsync(dave, new { code = ShareCode.Format(code) });

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("no-store", preview.Headers.CacheControl?.ToString());
        var body = await RestClient.JsonAsync(preview);
        Assert.Equal("write-as-alice", body["name"]!.GetValue<string>());
        Assert.Equal("Writes posts.", body["description"]!.GetValue<string>());
        Assert.Equal(Manifest, body["manifest"]!.GetValue<string>());
        Assert.Equal(["SKILL.md", "examples/post.md"], body["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.False(body["hasScripts"]!.GetValue<bool>());
        Assert.False(preview.Headers.Contains("Set-Cookie"), "the preview sets a cookie");
        // Nothing is added by looking.
        Assert.Empty(await FilesAsync(dave, "/Writing"));
        Assert.Empty(await FilesAsync(dave, "/Skills"));
    }

    [Fact]
    public async Task The_add_preview_says_when_a_skill_holds_scripts()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await PutNoteAsync(alice, "/Skills/runner/SKILL.md", "---\nname: runner\ndescription: Runs.\n---\n# Run\n");
        using (var put = await alice.SendContentAsync(HttpMethod.Put, "/api/files/Skills/runner/scripts/run.py", new ByteArrayContent("print(1)\n"u8.ToArray())))
        {
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        }

        using var shared = await alice.SendAsync(HttpMethod.Post, "/api/skills/shared", new { path = "/Skills/runner" });
        Assert.Equal(HttpStatusCode.Created, shared.StatusCode);
        var link = (await RestClient.JsonAsync(shared))["link"]!.GetValue<string>();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");

        using var preview = await PreviewAsync(dave, new { code = link[(link.IndexOf('#') + 1)..] });

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var body = await RestClient.JsonAsync(preview);
        Assert.True(body["hasScripts"]!.GetValue<bool>());
        Assert.Contains("scripts/run.py", body["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_add_preview_of_an_unknown_code_answers_as_the_skill_page_does()
    {
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");
        var unknown = ShareCode.Format(ShareCode.New());

        using var preview = await PreviewAsync(dave, new { code = unknown });
        using var page = await SendFromAsync(app.PublicApi, null, new { code = unknown }, $"198.51.{Random.Shared.Next(256)}.7", "/api/k");

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Equal(page.StatusCode, preview.StatusCode);
        Assert.Equal(await page.Content.ReadAsStringAsync(Ct), await preview.Content.ReadAsStringAsync(Ct));
        Assert.Equal(SkillPageApi.NotFoundSentence, await RestClient.ErrorAsync(preview));
    }

    [Fact]
    public async Task The_add_preview_counts_a_wrong_code_as_the_skill_page_does()
    {
        var (_, code) = await AlicesLinkAsync();
        await using var api = ReaderApiTests.Limited(app.PublicApi);
        var dave = await RestClient.NewAsync(api, vault: "Writing");
        for (var i = 0; i < ReaderApiTests.FailuresPerNetwork; i++)
        {
            using var wrong = await SendFromAsync(api, dave, new { code = ShareCode.New() }, $"203.0.113.{40 + i}", "/api/skills/add/preview");
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        }

        using var held = await SendFromAsync(api, dave, new { code }, "203.0.113.50", "/api/skills/add/preview");
        Assert.Equal(HttpStatusCode.TooManyRequests, held.StatusCode);
    }

    [Fact]
    public async Task The_add_preview_needs_a_session()
    {
        var (_, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");
        var key = await dave.KeyAsync("agent", KeyRight.Write);

        using var withKey = await PreviewAsync(dave, new { code }, bearer: key);
        using var anonymous = await SendFromAsync(app.PublicApi, null, new { code }, $"198.51.{Random.Shared.Next(256)}.8", "/api/skills/add/preview");

        Assert.Equal(HttpStatusCode.Forbidden, withKey.StatusCode);
        Assert.DoesNotContain("write-as-alice", await withKey.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.DoesNotContain("write-as-alice", await anonymous.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_add_preview_checks_the_origin()
    {
        var (_, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");

        using var forged = await SendFromAsync(app.PublicApi, dave, new { code }, $"198.51.{Random.Shared.Next(256)}.9", "/api/skills/add/preview", "https://evil.test");
        using var own = await SendFromAsync(app.PublicApi, dave, new { code }, $"198.51.{Random.Shared.Next(256)}.9", "/api/skills/add/preview");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.DoesNotContain("write-as-alice", await forged.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_a_skill_changed_since_it_was_shown_is_refused()
    {
        var (alice, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");
        var shown = await ShownAsync(dave, code);
        // Alice changes the skill and updates its link: the same code now opens other files.
        await PutNoteAsync(alice, "/Skills/write-as-alice/examples/other.md", "# Something else\n");
        using (var list = await alice.GetAsync("/api/skills/shared"))
        {
            var id = Assert.Single((await RestClient.JsonAsync(list)).AsArray())!["id"]!.GetValue<string>();
            using var update = await alice.SendAsync(HttpMethod.Post, $"/api/skills/shared/{id}/update");
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }
        Assert.NotEqual(shown, await ShownAsync(dave, code));

        using var added = await AddAsync(dave, new { code, vault = "Writing", shown });

        Assert.Equal(HttpStatusCode.Conflict, added.StatusCode);
        var body = await RestClient.JsonAsync(added);
        Assert.Equal(SkillsApi.SkillChanged, body["type"]!.GetValue<string>());
        AssertNoCode(body.ToJsonString(), code);
        Assert.Empty(await FilesAsync(dave, "/Writing"));
        Assert.Empty((await RecordAsync(dave, "Writing")).SkillCodes);
    }

    [Fact]
    public async Task Adding_the_skill_as_shown_adds_it()
    {
        var (_, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");

        using var added = await AddAsync(dave, new { code, vault = "Writing", shown = await ShownAsync(dave, code) });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal("/Writing/_skills/write-as-alice", (await RestClient.JsonAsync(added))["path"]!.GetValue<string>());
        Assert.Equal(["SKILL.md", "examples/post.md"], await FilesAsync(dave, "/Writing/_skills/write-as-alice"));
    }

    [Fact]
    public async Task Adding_a_skill_whose_files_changed_while_it_was_read_is_refused()
    {
        var (alice, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");
        var shown = await ShownAsync(dave, code);
        // The link's copy as it was, then its files updated, then the old copy put back: the copy opened lists other files
        // than those read after it, as when an update lands between the two.
        var publications = app.Database.GetCollection<PublicationDocument>("publications");
        var lookup = ShareCode.Lookup(code);
        var old = (await publications.Find(d => d.Lookup == lookup).SingleAsync(Ct)).Blob;
        using (var get = await alice.GetAsync("/api/notes/Skills/write-as-alice/examples/post.md"))
        {
            var version = (await RestClient.JsonAsync(get))["version"]!.GetValue<string>();
            using var put = await alice.SendAsync(HttpMethod.Put, "/api/notes/Skills/write-as-alice/examples/post.md", new { content = "# Post\n\nKeep every post long.\n", version });
            Assert.True(put.IsSuccessStatusCode, $"{put.StatusCode}");
        }
        using (var list = await alice.GetAsync("/api/skills/shared"))
        {
            var id = Assert.Single((await RestClient.JsonAsync(list)).AsArray())!["id"]!.GetValue<string>();
            using var update = await alice.SendAsync(HttpMethod.Post, $"/api/skills/shared/{id}/update");
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }
        await publications.UpdateOneAsync(d => d.Lookup == lookup, Builders<PublicationDocument>.Update.Set(d => d.Blob, old), cancellationToken: Ct);
        Assert.Equal(shown, await ShownAsync(dave, code));

        using var added = await AddAsync(dave, new { code, vault = "Writing", shown });

        Assert.Equal(HttpStatusCode.Conflict, added.StatusCode);
        Assert.Equal(SkillsApi.SkillChanged, (await RestClient.JsonAsync(added))["type"]!.GetValue<string>());
        Assert.Equal(SkillPageApi.ChangedSentence, await RestClient.ErrorAsync(added));
        Assert.Empty(await FilesAsync(dave, "/Writing"));
        Assert.Empty((await RecordAsync(dave, "Writing")).SkillCodes);
    }
}
