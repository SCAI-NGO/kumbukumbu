// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;
using Kumbu.Core.Skills;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Publishing;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Skills;

/// <summary>Copy to… and Move to… of the Skills screen: <c>POST /api/skills/copy</c> and <c>/move</c>, across vaults, for a
/// session only, with a link copy's sealed state going along (Decisions 21, 33, 34).</summary>
[Collection("app")]
public sealed class SkillCopyTests(KumbuAppFactory app)
{
    private const string Manifest = "---\nname: x\ndescription: Does x.\n---\n# X\n";
    private const string Guide = "# Guide\n\nStep one.\n";
    private static readonly byte[] Script = "print(1)\n"u8.ToArray();
    private const string Code = "test-share-code-not-real";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    /// <summary>A user with a vault Work and /Skills/x: SKILL.md, ref/guide.md and scripts/run.py.</summary>
    private async Task<RestClient> UserWithSkillAsync(string vault = "Work")
    {
        var user = await RestClient.NewAsync(app.PublicApi, vault: vault);
        await PutNoteAsync(user, "/Skills/x/SKILL.md", Manifest);
        await PutNoteAsync(user, "/Skills/x/ref/guide.md", Guide);
        using var put = await user.SendContentAsync(
            HttpMethod.Put, "/api/files/Skills/x/scripts/run.py", new ByteArrayContent(Script) { Headers = { ContentType = new("application/octet-stream") } });
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        return user;
    }

    private static async Task PutNoteAsync(RestClient user, string path, string content)
    {
        using var put = await user.SendAsync(HttpMethod.Put, "/api/notes" + path, new { content });
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
    }

    private static Task<HttpResponseMessage> CopyAsync(RestClient user, object body, string? bearer = null) => user.SendAsync(HttpMethod.Post, "/api/skills/copy", body, bearer);

    private static Task<HttpResponseMessage> MoveAsync(RestClient user, object body) => user.SendAsync(HttpMethod.Post, "/api/skills/move", body);

    private static EncryptedNoteStore Store(RestClient user) => new(user.Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey));

    /// <summary>Every note and file below the folder, relative to it.</summary>
    private static async Task<string[]> FilesAsync(RestClient user, string folder)
    {
        var store = Store(user);
        var path = P(folder);
        var notes = await store.ListNotesUnderAsync(path, Ct);
        var files = await StoreAttachments.UnderAsync(store, path, Ct);
        return [.. notes.Concat(files.Select(f => f.Path)).Select(p => p.Value[(folder.Length + 1)..]).Order(StringComparer.Ordinal)];
    }

    private static async Task<VaultAccess> AccessAsync(RestClient user, string name) =>
        (await user.Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == name);

    private static async Task<PublishingRecord> RecordAsync(RestClient user, string vault) =>
        (await user.Api.Services.GetRequiredService<PublicationStore>().ReadAsync(null, (await AccessAsync(user, vault)).Vault, Ct)).Record;

    /// <summary>The sealed state of a link copy at /Skills/x, as adding it from a link will leave it.</summary>
    private static async Task PlantLinkCopyAsync(RestClient user)
    {
        await user.Api.Services.GetRequiredService<PublicationStore>().ChangeAsync(
            await AccessAsync(user, "Skills"),
            r => r with { SkillCodes = [new SkillCode("/Skills/x", Code)], Origins = [new SkillOrigin("/Skills/x", "link:00ff", "sha256:abc")] },
            Ct);
    }

    private static async Task<string> PathOfAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await RestClient.JsonAsync(response))["path"]!.GetValue<string>();
    }

    [Fact]
    public async Task Copy_from_Skills_to_Work_writes_every_file()
    {
        var alice = await UserWithSkillAsync();

        using var response = await CopyAsync(alice, new { from = "/Skills/x", to = "Work" });

        Assert.Equal("/Work/_skills/x", await PathOfAsync(response));
        Assert.Equal(["SKILL.md", "ref/guide.md", "scripts/run.py"], await FilesAsync(alice, "/Work/_skills/x"));
        Assert.Equal(Manifest, await Store(alice).ReadAsync(P("/Work/_skills/x/SKILL.md"), Ct));
        Assert.Equal(Guide, await Store(alice).ReadAsync(P("/Work/_skills/x/ref/guide.md"), Ct));
        using var script = await alice.GetAsync("/api/files/Work/_skills/x/scripts/run.py");
        Assert.Equal(Script, await script.Content.ReadAsByteArrayAsync(Ct));
        // A copy: the source stays.
        Assert.Equal(["SKILL.md", "ref/guide.md", "scripts/run.py"], await FilesAsync(alice, "/Skills/x"));
    }

    [Fact]
    public async Task Copy_of_a_system_skill_into_Skills()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var system = SystemSkills.Find("create-skill")!;

        using var response = await CopyAsync(alice, new { from = "system:create-skill", to = "Skills" });

        Assert.Equal("/Skills/create-skill", await PathOfAsync(response));
        string[] expected = [.. system.Files.Select(f => f.Path).Order(StringComparer.Ordinal)];
        Assert.Equal(expected, await FilesAsync(alice, "/Skills/create-skill"));
        Assert.Equal(system.Files.Single(f => f.Path == SkillPaths.Manifest).Content, await Store(alice).ReadAsync(P("/Skills/create-skill/SKILL.md"), Ct));
    }

    [Fact]
    public async Task Move_leaves_the_source_in_its_trash()
    {
        var alice = await UserWithSkillAsync();

        using var response = await MoveAsync(alice, new { from = "/Skills/x", to = "Work" });

        Assert.Equal("/Work/_skills/x", await PathOfAsync(response));
        Assert.Equal(["SKILL.md", "ref/guide.md", "scripts/run.py"], await FilesAsync(alice, "/Work/_skills/x"));
        Assert.Empty(await FilesAsync(alice, "/Skills/x"));
        var store = Store(alice);
        Assert.NotEmpty(await store.ListTrashAsync(P("/Skills/x/SKILL.md"), Ct));
        Assert.NotEmpty(await store.ListTrashAsync(P("/Skills/x/ref/guide.md"), Ct));
        Assert.Contains(await store.ListAttachmentTrashAsync(P("/Skills/x/scripts/run.py"), Ct), t => t.Attachment.Path == P("/Skills/x/scripts/run.py"));
    }

    [Fact]
    public async Task A_taken_name_is_409_with_its_type()
    {
        var alice = await UserWithSkillAsync();
        await alice.CreateAsync("_skills/x/SKILL.md", Manifest);

        using var response = await CopyAsync(alice, new { from = "/Skills/x", to = "Work" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await RestClient.JsonAsync(response);
        Assert.Equal("/Work/_skills/x is taken; choose another name.", body["error"]!.GetValue<string>());
        Assert.Equal("name-taken", body["type"]!.GetValue<string>());
        Assert.Equal(["SKILL.md"], await FilesAsync(alice, "/Work/_skills/x"));
    }

    [Fact]
    public async Task Another_name_resolves_it()
    {
        var alice = await UserWithSkillAsync();
        await alice.CreateAsync("_skills/x/SKILL.md", Manifest);

        using var response = await CopyAsync(alice, new { from = "/Skills/x", to = "Work", name = "x-2" });

        Assert.Equal("/Work/_skills/x-2", await PathOfAsync(response));
        Assert.Equal(["SKILL.md", "ref/guide.md", "scripts/run.py"], await FilesAsync(alice, "/Work/_skills/x-2"));
    }

    [Theory, InlineData("../evil"), InlineData("a/b")]
    public async Task A_name_that_escapes_is_400(string name)
    {
        var alice = await UserWithSkillAsync();

        using var response = await CopyAsync(alice, new { from = "/Skills/x", to = "Work", name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("a skill's name has no '/'", await RestClient.ErrorAsync(response));
        Assert.Empty(await FilesAsync(alice, "/Work"));
    }

    [Fact]
    public async Task A_name_that_breaks_the_note_names_is_400()
    {
        var alice = await UserWithSkillAsync();

        using var response = await CopyAsync(alice, new { from = "/Skills/x", to = "Work", name = ".." });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("starts with '.'", await RestClient.ErrorAsync(response));
    }

    [Fact]
    public async Task A_read_only_target_is_403()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Private");
        await alice.ShareWithAsync(bob, "Team", MemberRole.Read);

        using var response = await CopyAsync(bob, new { from = "system:create-skill", to = "Team" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You can only read vault Team.", await RestClient.ErrorAsync(response));
        Assert.Empty(await FilesAsync(alice, "/Work"));
    }

    [Fact]
    public async Task A_members_writable_vault_is_a_target_and_gets_the_sealed_state()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var bob = await UserWithSkillAsync("Private");
        await PlantLinkCopyAsync(bob);
        await alice.ShareWithAsync(bob, "Team", MemberRole.Write);

        using var response = await CopyAsync(bob, new { from = "/Skills/x", to = "Team" });

        Assert.Equal("/Team/_skills/x", await PathOfAsync(response));
        Assert.Equal(["SKILL.md", "ref/guide.md", "scripts/run.py"], await FilesAsync(alice, "/Work/_skills/x"));
        var work = await RecordAsync(alice, "Work");
        Assert.Equal([new SkillCode("/Work/_skills/x", Code)], work.SkillCodes);
        Assert.Equal([new SkillOrigin("/Work/_skills/x", "link:00ff", "sha256:abc")], work.Origins);
        // A copy: Bob's own stays as it was.
        Assert.Equal([new SkillCode("/Skills/x", Code)], (await RecordAsync(bob, "Skills")).SkillCodes);
    }

    [Fact]
    public async Task Copy_needs_a_session()
    {
        var alice = await UserWithSkillAsync();
        var key = await alice.KeyAsync("agent", KeyRight.Write);

        using var response = await CopyAsync(alice, new { from = "/Skills/x", to = "Work" }, bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await FilesAsync(alice, "/Work/_skills"));
    }

    [Fact]
    public async Task The_quota_applies()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await PutNoteAsync(alice, "/Skills/x/SKILL.md", Manifest);
        using (var big = await alice.SendContentAsync(
            HttpMethod.Put, "/api/files/Skills/x/big.txt", new ByteArrayContent(Encoding.UTF8.GetBytes(new string('a', 50_000))) { Headers = { ContentType = new("application/octet-stream") } }))
        {
            Assert.Equal(HttpStatusCode.Created, big.StatusCode);
        }

        // Room for SKILL.md, not for big.txt: the first file is written, then the copy fails.
        var quota = alice.Api.Services.GetRequiredService<StorageQuota>();
        var used = (await quota.FigureAsync(alice.User.Id, Ct)).Used;
        var users = alice.Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<UserDocument>("users");
        await users.UpdateOneAsync(u => u.Id == alice.User.Id, Builders<UserDocument>.Update.Set(u => u.Quota, used + 10_000), cancellationToken: Ct);
        await quota.FigureAsync(alice.User.Id, Ct);

        using var response = await CopyAsync(alice, new { from = "/Skills/x", to = "Work" });

        Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
        Assert.Empty(await FilesAsync(alice, "/Work/_skills/x"));
        Assert.Equal(["SKILL.md", "big.txt"], await FilesAsync(alice, "/Skills/x"));
    }

    [Fact]
    public async Task A_link_copys_code_goes_along()
    {
        var alice = await UserWithSkillAsync();
        await PlantLinkCopyAsync(alice);

        using var response = await MoveAsync(alice, new { from = "/Skills/x", to = "Work" });

        Assert.Equal("/Work/_skills/x", await PathOfAsync(response));
        var work = await RecordAsync(alice, "Work");
        Assert.Equal([new SkillCode("/Work/_skills/x", Code)], work.SkillCodes);
        Assert.Equal([new SkillOrigin("/Work/_skills/x", "link:00ff", "sha256:abc")], work.Origins);
        var skills = await RecordAsync(alice, "Skills");
        Assert.Empty(skills.SkillCodes);
        Assert.Empty(skills.Origins);
    }

    [Fact]
    public async Task An_unknown_source_is_404_and_the_same_place_400()
    {
        var alice = await UserWithSkillAsync();

        using var unknown = await CopyAsync(alice, new { from = "/Skills/nothing", to = "Work" });
        using var system = await CopyAsync(alice, new { from = "system:nothing", to = "Work" });
        using var vault = await CopyAsync(alice, new { from = "/Skills/x", to = "Nowhere" });
        using var same = await MoveAsync(alice, new { from = "/Skills/x", to = "Skills" });

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, system.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, vault.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        Assert.Equal(["SKILL.md", "ref/guide.md", "scripts/run.py"], await FilesAsync(alice, "/Skills/x"));
    }

    [Fact]
    public async Task Move_needs_write_on_the_source()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await alice.CreateAsync("_skills/x/SKILL.md", Manifest);
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Private");
        await alice.ShareWithAsync(bob, "Team", MemberRole.Read);

        using var response = await MoveAsync(bob, new { from = "/Team/_skills/x", to = "Skills" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You can only read vault Team.", await RestClient.ErrorAsync(response));
        Assert.Equal(["SKILL.md"], await FilesAsync(alice, "/Work/_skills/x"));
        Assert.Empty(await FilesAsync(bob, "/Skills/x"));
    }

    [Fact]
    public async Task A_system_skill_is_copied_not_moved()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");

        using var response = await MoveAsync(alice, new { from = "system:create-skill", to = "Work" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await FilesAsync(alice, "/Work/_skills"));
    }

    private static PublicationStore Publishing(RestClient user) => user.Api.Services.GetRequiredService<PublicationStore>();

    private static readonly SkillOrigin Origin = new("/Work/_skills/x", "link:00ff", "sha256:abc");

    /// <summary>As every write of the vault: a re-keying run, which holds the gate, never seals the record between a check and the write.</summary>
    [Fact]
    public async Task A_record_change_runs_under_the_vaults_gate()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var access = await AccessAsync(alice, "Work");
        var held = false;

        await Publishing(alice).ChangeAsync(access, r =>
        {
            held = access.Vault.Gate.CurrentCount == 0;
            return r with { Origins = [Origin] };
        }, Ct);

        Assert.True(held);
        Assert.Equal([Origin], (await RecordAsync(alice, "Work")).Origins);
    }

    [Fact]
    public async Task A_record_change_during_a_re_keying_is_refused()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var access = await AccessAsync(alice, "Work");

        await using (await alice.HoldRekeyAsync())
        {
            var busy = await Assert.ThrowsAsync<VaultBusyException>(() => Publishing(alice).ChangeAsync(access, r => r with { Origins = [Origin] }, Ct));
            Assert.Equal(VaultBusyException.BeingReencrypted, busy.Message);
        }
    }

    [Fact]
    public async Task A_record_change_that_keeps_conflicting_is_409()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var access = await AccessAsync(alice, "Work");
        var records = alice.Api.Services.GetRequiredService<VaultCatalog>().Publishing;
        await Publishing(alice).ChangeAsync(access, r => r with { Origins = [Origin] }, Ct);
        var calls = 0;

        // Each time, another writer gets there first: the record is a revision further, still sealed as it should be.
        await Assert.ThrowsAsync<PublishingConflictException>(() => Publishing(alice).ChangeAsync(access, r =>
        {
            calls++;
            var doc = records.Find(d => d.Vault == access.Id).Single();
            records.ReplaceOne(d => d.Id == doc.Id, new SealedDocument
            {
                Id = doc.Id, Vault = doc.Vault, PathId = doc.PathId, Revision = doc.Revision + 1, Blob = access.Vault.SealPublishing(doc.Id, doc.Revision + 1, r),
            });
            return r with { Origins = [] };
        }, Ct));

        Assert.Equal(3, calls);
        Assert.Equal([Origin], (await RecordAsync(alice, "Work")).Origins);
        var answer = await ApiErrors.Filter(new DefaultEndpointFilterInvocationContext(new DefaultHttpContext()), _ => throw new PublishingConflictException());
        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<JsonHttpResult<ApiError>>(answer).StatusCode);
    }

    [Fact]
    public void A_skill_code_is_not_in_its_text()
    {
        Assert.DoesNotContain(Code, new SkillCode("/Skills/x", Code).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Code, (PublishingRecord.Empty with { SkillCodes = [new SkillCode("/Skills/x", Code)] }).ToString(), StringComparison.Ordinal);
    }
}
