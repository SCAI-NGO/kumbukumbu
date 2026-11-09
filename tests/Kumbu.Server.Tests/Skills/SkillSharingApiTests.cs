// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Publishing;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kumbu.Server.Tests.Skills;

/// <summary>Sharing a skill by link from the Skills screen: <c>/api/skills/shared</c>, for the vault's owner in her session
/// alone, the link only in the answer that makes it (Decision 15).</summary>
[Collection("app")]
public sealed class SkillSharingApiTests(KumbuAppFactory app)
{
    private const string Manifest = "---\nname: x\ndescription: Does x.\n---\n# X\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A user with a vault Work holding /Work/_skills/x.</summary>
    private async Task<RestClient> AliceAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? api = null)
    {
        var alice = await RestClient.NewAsync(api ?? app.PublicApi, vault: "Work");
        await alice.CreateAsync("_skills/x/SKILL.md", Manifest);
        return alice;
    }

    private static Task<HttpResponseMessage> ShareAsync(RestClient user, string path, string? bearer = null) =>
        user.SendAsync(HttpMethod.Post, "/api/skills/shared", new { path }, bearer);

    /// <summary>The link of a new share, from its 201.</summary>
    private static async Task<string> LinkAsync(RestClient user, string path)
    {
        using var response = await ShareAsync(user, path);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await RestClient.JsonAsync(response))["link"]!.GetValue<string>();
    }

    private static string CodeOf(string link) => ShareCode.Parse(link[(link.IndexOf('#') + 1)..])!;

    private static async Task<JsonArray> SharedAsync(RestClient user)
    {
        using var response = await user.GetAsync("/api/skills/shared");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response)).AsArray();
    }

    /// <summary>The skill at <paramref name="path"/> as <c>GET /api/skills</c> lists it.</summary>
    private static async Task<JsonNode> SkillAsync(RestClient user, string path)
    {
        using var response = await user.GetAsync("/api/skills");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response))["skills"]!.AsArray().Single(s => s!["path"]?.GetValue<string>() == path)!;
    }

    private static async Task<string> IdAsync(RestClient user) => Assert.Single(await SharedAsync(user))!["id"]!.GetValue<string>();

    /// <summary>That <paramref name="text"/> holds neither a '#' nor any eight characters of the code in a row.</summary>
    private static void AssertNoCode(string text, string code)
    {
        Assert.DoesNotContain("#", text, StringComparison.Ordinal);
        for (var i = 0; i + 8 <= code.Length; i++)
        {
            Assert.False(text.Contains(code.Substring(i, 8), StringComparison.OrdinalIgnoreCase), "a part of the code is in the text");
        }
    }

    [Fact]
    public async Task Sharing_answers_the_link_once()
    {
        var log = new CapturedLog();
        await using var logged = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var alice = await AliceAsync(logged);

        using var created = await ShareAsync(alice, "/Work/_skills/x");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await RestClient.JsonAsync(created);
        var link = body["link"]!.GetValue<string>();
        Assert.Matches(@"^https://kumbu\.test/k#[0-9A-Z]{4}(-[0-9A-Z]{4}){7}$", link);
        Assert.NotNull(body["snapshotAt"]);
        var code = CodeOf(link);

        using var list = await alice.GetAsync("/api/skills/shared");
        var listed = await list.Content.ReadAsStringAsync(Ct);
        AssertNoCode(listed, code);
        var entry = Assert.Single((await SharedAsync(alice)))!;
        Assert.Equal("/Work/_skills/x", entry["path"]!.GetValue<string>());
        Assert.False(entry["changed"]!.GetValue<bool>());
        Assert.Equal(body["snapshotAt"]!.GetValue<DateTime>(), entry["snapshotAt"]!.GetValue<DateTime>());

        using var skills = await alice.GetAsync("/api/skills");
        var skillsText = await skills.Content.ReadAsStringAsync(Ct);
        AssertNoCode(skillsText, code);
        var view = (await RestClient.JsonAsync(skills))["skills"]!.AsArray().Single(s => s!["path"]?.GetValue<string>() == "/Work/_skills/x")!;
        Assert.True(view["shared"]!.GetValue<bool>());

        var id = entry["id"]!.GetValue<string>();
        using var updated = await alice.SendAsync(HttpMethod.Post, $"/api/skills/shared/{id}/update");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        AssertNoCode(await updated.Content.ReadAsStringAsync(Ct), code);
        using var withdrawn = await alice.SendAsync(HttpMethod.Delete, $"/api/skills/shared/{id}");
        Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        AssertNoCode(await withdrawn.Content.ReadAsStringAsync(Ct), code);

        Assert.All(log.Lines, l => AssertNoCode(l.Replace("#", "", StringComparison.Ordinal), code));
    }

    [Fact]
    public async Task A_second_share_is_409()
    {
        var alice = await AliceAsync();
        await LinkAsync(alice, "/Work/_skills/x");

        using var again = await ShareAsync(alice, "/Work/_skills/x");

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var refused = await RestClient.JsonAsync(again);
        Assert.Equal("/Work/_skills/x is shared already: withdraw its link first to make a new one.", refused["error"]!.GetValue<string>());
        Assert.Equal("skill-shared", refused["code"]!.GetValue<string>());
        Assert.Single(await SharedAsync(alice));
    }

    [Fact]
    public async Task Update_and_withdraw_by_id()
    {
        var alice = await AliceAsync();
        await LinkAsync(alice, "/Work/_skills/x");
        var id = await IdAsync(alice);
        var before = Assert.Single(await SharedAsync(alice))!["snapshotAt"]!.GetValue<DateTime>();

        await alice.CreateAsync("_skills/x/ref/guide.md", "# Guide\n");
        var changed = Assert.Single(await SharedAsync(alice))!;
        Assert.True(changed["changed"]!.GetValue<bool>());

        using var updated = await alice.SendAsync(HttpMethod.Post, $"/api/skills/shared/{id}/update");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var at = (await RestClient.JsonAsync(updated))["snapshotAt"]!.GetValue<DateTime>();
        Assert.True(at >= before);
        var after = Assert.Single(await SharedAsync(alice))!;
        Assert.Equal(id, after["id"]!.GetValue<string>());
        Assert.False(after["changed"]!.GetValue<bool>());
        Assert.Equal(at, after["snapshotAt"]!.GetValue<DateTime>());

        using var withdrawn = await alice.SendAsync(HttpMethod.Delete, $"/api/skills/shared/{id}");
        Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        Assert.Empty(await SharedAsync(alice));

        using var gone = await alice.SendAsync(HttpMethod.Delete, $"/api/skills/shared/{id}");
        using var goneUpdate = await alice.SendAsync(HttpMethod.Post, $"/api/skills/shared/{id}/update");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, goneUpdate.StatusCode);
    }

    [Fact]
    public async Task Sharing_again_after_withdrawing_makes_a_new_link()
    {
        var alice = await AliceAsync();
        var first = await LinkAsync(alice, "/Work/_skills/x");
        using (var withdrawn = await alice.SendAsync(HttpMethod.Delete, $"/api/skills/shared/{await IdAsync(alice)}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        }

        var second = await LinkAsync(alice, "/Work/_skills/x");

        Assert.NotEqual(CodeOf(first), CodeOf(second));
        Assert.Single(await SharedAsync(alice));
    }

    [Fact]
    public async Task A_shared_skill_cant_be_moved()
    {
        var alice = await AliceAsync();
        await LinkAsync(alice, "/Work/_skills/x");

        using var moved = await alice.SendAsync(HttpMethod.Post, "/api/skills/move", new { from = "/Work/_skills/x", to = "Skills" });

        Assert.Equal(HttpStatusCode.Conflict, moved.StatusCode);
        var body = await RestClient.JsonAsync(moved);
        Assert.Equal("shared", body["type"]!.GetValue<string>());
        Assert.Equal("Withdraw the link first: a shared skill stays where its link reads it.", body["error"]!.GetValue<string>());
        Assert.Equal(Manifest, (await alice.ReadAsync("_skills/x/SKILL.md"))["content"]!.GetValue<string>());
        using var target = await alice.GetAsync("/api/notes/Skills/x/SKILL.md");
        Assert.Equal(HttpStatusCode.NotFound, target.StatusCode);
        Assert.Single(await SharedAsync(alice));

        // A copy is no move: the link reads the source, which stays.
        using var copied = await alice.SendAsync(HttpMethod.Post, "/api/skills/copy", new { from = "/Work/_skills/x", to = "Skills" });
        Assert.Equal(HttpStatusCode.OK, copied.StatusCode);
    }

    [Fact]
    public async Task Only_a_session_shares()
    {
        var alice = await AliceAsync();
        await alice.CreateAsync("_skills/y/SKILL.md", Manifest.Replace("name: x", "name: y", StringComparison.Ordinal));
        var key = await alice.KeyAsync("agent", KeyRight.Write, mayPublish: true);
        await LinkAsync(alice, "/Work/_skills/x");
        var id = await IdAsync(alice);

        // y isn't shared yet: without the session's gate, the key's share would make a link.
        using var share = await ShareAsync(alice, "/Work/_skills/y", bearer: key);
        using var list = await alice.GetAsync("/api/skills/shared", bearer: key);
        using var update = await alice.SendAsync(HttpMethod.Post, $"/api/skills/shared/{id}/update", bearer: key);
        using var withdraw = await alice.SendAsync(HttpMethod.Delete, $"/api/skills/shared/{id}", bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, share.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withdraw.StatusCode);
        Assert.Equal(id, await IdAsync(alice));
        Assert.False((await SkillAsync(alice, "/Work/_skills/y"))["shared"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_withdrawn_skill_says_so_until_it_is_shared_anew()
    {
        var alice = await AliceAsync();
        Assert.False((await SkillAsync(alice, "/Work/_skills/x"))["withdrawnBefore"]!.GetValue<bool>());
        await LinkAsync(alice, "/Work/_skills/x");
        Assert.False((await SkillAsync(alice, "/Work/_skills/x"))["withdrawnBefore"]!.GetValue<bool>());

        using (var withdrawn = await alice.SendAsync(HttpMethod.Delete, $"/api/skills/shared/{await IdAsync(alice)}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        }

        var after = await SkillAsync(alice, "/Work/_skills/x");
        Assert.False(after["shared"]!.GetValue<bool>());
        Assert.True(after["withdrawnBefore"]!.GetValue<bool>());

        await LinkAsync(alice, "/Work/_skills/x");
        Assert.False((await SkillAsync(alice, "/Work/_skills/x"))["withdrawnBefore"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_link_withdrawn_by_a_change_or_a_mark_counts_too()
    {
        var alice = await AliceAsync();
        await alice.CreateAsync("_skills/x/ref.md", "# Ref\n");
        await LinkAsync(alice, "/Work/_skills/x");

        // SKILL.md to the trash withdraws the link (Decision 31); written again, the skill remembers.
        using (var deleted = await alice.SendAsync(HttpMethod.Delete, alice.Url("_skills/x/SKILL.md")))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await alice.CreateAsync("_skills/x/SKILL.md", Manifest);
        Assert.Empty(await SharedAsync(alice));
        Assert.True((await SkillAsync(alice, "/Work/_skills/x"))["withdrawnBefore"]!.GetValue<bool>());

        // Shared anew, then a mark on one of its files withdraws it.
        await LinkAsync(alice, "/Work/_skills/x");
        Assert.False((await SkillAsync(alice, "/Work/_skills/x"))["withdrawnBefore"]!.GetValue<bool>());
        using (var marked = await alice.SendAsync(HttpMethod.Put, "/api/unpublishable/Work/_skills/x/ref.md"))
        {
            Assert.Equal(HttpStatusCode.NoContent, marked.StatusCode);
        }

        Assert.Empty(await SharedAsync(alice));
        Assert.True((await SkillAsync(alice, "/Work/_skills/x"))["withdrawnBefore"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_member_cant_share_a_skill_of_the_owner()
    {
        var alice = await AliceAsync();
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Private");
        await alice.ShareWithAsync(bob, "Team", MemberRole.Write);

        using var share = await ShareAsync(bob, "/Team/_skills/x");

        Assert.Equal(HttpStatusCode.Forbidden, share.StatusCode);
        Assert.Equal("Only the owner of Team can share its skills.", await RestClient.ErrorAsync(share));
        Assert.Empty(await SharedAsync(alice));

        // Alice's link is none of Bob's: he neither sees nor touches it.
        await LinkAsync(alice, "/Work/_skills/x");
        var id = await IdAsync(alice);
        Assert.Empty(await SharedAsync(bob));
        using var update = await bob.SendAsync(HttpMethod.Post, $"/api/skills/shared/{id}/update");
        using var withdraw = await bob.SendAsync(HttpMethod.Delete, $"/api/skills/shared/{id}");
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withdraw.StatusCode);
        Assert.Equal(id, await IdAsync(alice));
    }

    [Fact]
    public async Task An_unknown_skill_is_404()
    {
        var alice = await AliceAsync();

        using var nothing = await ShareAsync(alice, "/Work/_skills/nothing");
        using var notSkill = await ShareAsync(alice, "/Work/notes");
        using var noVault = await ShareAsync(alice, "/Nowhere/_skills/x");

        Assert.Equal(HttpStatusCode.NotFound, nothing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notSkill.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noVault.StatusCode);
        Assert.Empty(await SharedAsync(alice));
    }
}
