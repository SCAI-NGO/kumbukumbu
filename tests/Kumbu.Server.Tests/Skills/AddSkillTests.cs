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
    private async Task<(RestClient Alice, string Code)> AlicesLinkAsync()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await PutNoteAsync(alice, "/Skills/write-as-alice/SKILL.md", Manifest);
        await PutNoteAsync(alice, "/Skills/write-as-alice/examples/post.md", "# Post\n");
        using var shared = await alice.SendAsync(HttpMethod.Post, "/api/skills/shared", new { path = "/Skills/write-as-alice" });
        Assert.Equal(HttpStatusCode.Created, shared.StatusCode);
        var link = (await RestClient.JsonAsync(shared))["link"]!.GetValue<string>();
        return (alice, ShareCode.Parse(link[(link.IndexOf('#') + 1)..])!);
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
        Assert.DoesNotContain(code, answer);
        Assert.DoesNotContain(ShareCode.Format(code), answer);
        var content = await NoteAsync(dave, "/Writing/_skills/write-as-alice/SKILL.md");
        var manifest = SkillManifest.Read("write-as-alice", content);
        Assert.Equal("link:" + Convert.ToHexStringLower(ShareCode.Lookup(code)), manifest.Source);
        Assert.Equal(
            SkillHash.Of([("SKILL.md", Encoding.UTF8.GetBytes(Manifest)), ("examples/post.md", "# Post\n"u8.ToArray())]),
            manifest.Hash);
        Assert.DoesNotContain(code, content);
        Assert.DoesNotContain(ShareCode.Format(code), content);
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
    public async Task The_code_is_kept_sealed_in_the_target_vaults_record()
    {
        var (_, code) = await AlicesLinkAsync();
        var dave = await RestClient.NewAsync(app.PublicApi, vault: "Writing");
        using (var added = await AddAsync(dave, new { code, vault = "Writing" })) Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var vault = (await AccessAsync(dave, "Writing")).Vault;
        var raw = await app.Database.GetCollection<SealedDocument>(PublicationStore.RecordCollection).Find(d => d.Vault == vault.Id).SingleAsync(Ct);
        Assert.Equal(-1, raw.Blob.AsSpan().IndexOf(Encoding.UTF8.GetBytes(code)));
        var record = await RecordAsync(dave, "Writing");
        Assert.Equal(code, record.SkillCodes.Single(c => c.Path == "/Writing/_skills/write-as-alice").Code);
        var origin = Assert.Single(record.Origins);
        Assert.Equal("/Writing/_skills/write-as-alice", origin.Path);
        Assert.Equal("link:" + Convert.ToHexStringLower(ShareCode.Lookup(code)), origin.Source);
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
            Assert.DoesNotContain(code, body.ToJsonString());
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
        Assert.DoesNotContain(code, await response.Content.ReadAsStringAsync(Ct));
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

    /// <summary>As the session of <paramref name="user"/>, from <paramref name="ip"/>.</summary>
    private static async Task<HttpResponseMessage> SendFromAsync(WebApplicationFactory<Program> api, RestClient user, object body, string ip)
    {
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/skills/add") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={user.Session}");
        request.Headers.Add("Origin", RestClient.Origin);
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
}
