// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Search;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Skills;

/// <summary>The Skills screen's list and find: <c>/api/skills</c>, for a session only, also as one credential sees them.</summary>
[Collection("app")]
public sealed class SkillsApiTests(KumbuAppFactory app)
{
    private const string WriteAsAlice = "---\nname: write-as-alice\ndescription: Writes posts in Alice's voice.\n---\n# Write\n";
    private const string ReleaseNotes = "---\nname: release-notes\ndescription: Drafts release notes.\n---\n# Notes\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Alice with /Skills/write-as-alice and /Work/_skills/release-notes.</summary>
    private async Task<RestClient> AliceAsync()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        using var put = await alice.SendAsync(HttpMethod.Put, "/api/notes/Skills/write-as-alice/SKILL.md", new { content = WriteAsAlice });
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        await alice.CreateAsync("_skills/release-notes/SKILL.md", ReleaseNotes);
        return alice;
    }

    private static async Task<string> SkillsIdAsync(RestClient user) =>
        (await user.Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Vault.Kind == VaultKinds.Skills).Id;

    private static async Task<ApiKeyInfo> KeyAsync(RestClient user, string name, Dictionary<string, KeyRight> rights, ApiKeyKind kind = ApiKeyKind.Agent) =>
        (await user.Api.Services.GetRequiredService<ApiKeys>().CreateAsync(user.User, user.UserKey, DateTime.UtcNow, name, rights, Ct, kind: kind)).Info;

    private static async Task<JsonNode> GetAsync(RestClient user, string path)
    {
        using var response = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RestClient.JsonAsync(response);
    }

    private static string[] Names(JsonNode skills) => [.. skills.AsArray().Select(s => s!["skill"]!["name"]!.GetValue<string>())];

    [Fact]
    public async Task Lists_system_Skills_and_vault_skills_grouped()
    {
        var alice = await AliceAsync();

        var skills = (await GetAsync(alice, "/api/skills"))["skills"]!.AsArray();

        var system = skills.Where(s => s!["location"]!.GetValue<string>() == "system").ToList();
        Assert.Contains(system, s => s!["name"]!.GetValue<string>() == "create-skill");
        Assert.All(system, s => Assert.Null(s!["path"]));
        var mine = skills.Single(s => s!["name"]!.GetValue<string>() == "write-as-alice")!;
        Assert.Equal("Skills", mine["location"]!.GetValue<string>());
        Assert.Equal("/Skills/write-as-alice", mine["path"]!.GetValue<string>());
        Assert.Equal("Writes posts in Alice's voice.", mine["description"]!.GetValue<string>());
        Assert.True(mine["canWrite"]!.GetValue<bool>());
        Assert.False(mine["byOther"]!.GetValue<bool>());
        var work = skills.Single(s => s!["name"]!.GetValue<string>() == "release-notes")!;
        Assert.Equal("Work", work["location"]!.GetValue<string>());
        Assert.Equal("/Work/_skills/release-notes", work["path"]!.GetValue<string>());
        // System skills first, then the vault Skills, then the other vaults.
        var order = skills.Select(s => s!["location"]!.GetValue<string>()).ToList();
        Assert.True(order.LastIndexOf("system") < order.IndexOf("Skills") && order.LastIndexOf("Skills") < order.IndexOf("Work"));
    }

    [Fact]
    public async Task Names_keys_and_connections_without_Skills()
    {
        var alice = await AliceAsync();
        var skillsId = await SkillsIdAsync(alice);
        var workId = await alice.VaultIdAsync();
        await KeyAsync(alice, "A", new() { [workId] = KeyRight.Write });
        await KeyAsync(alice, "B", new() { [skillsId] = KeyRight.Read, [workId] = KeyRight.Write });
        var revoked = await KeyAsync(alice, "C", new() { [workId] = KeyRight.Write });
        await alice.Api.Services.GetRequiredService<ApiKeys>().RevokeAsync(alice.User, revoked.Id, Ct);
        await KeyAsync(alice, "D", new() { [workId] = KeyRight.Write }, ApiKeyKind.Device);
        await alice.Api.Services.GetRequiredService<Connections>().CreateAsync(alice.User, alice.UserKey, DateTime.UtcNow, "E", new Dictionary<string, KeyRight> { [workId] = KeyRight.Read }, Ct);
        await alice.Api.Services.GetRequiredService<Connections>().CreateAsync(alice.User, alice.UserKey, DateTime.UtcNow, "F", new Dictionary<string, KeyRight> { [skillsId] = KeyRight.Read }, Ct);

        var without = (await GetAsync(alice, "/api/skills"))["withoutSkills"]!.AsArray().Select(n => n!.GetValue<string>()).Order().ToArray();

        Assert.Equal(["A", "E"], without);
    }

    [Fact]
    public async Task A_members_skill_is_marked()
    {
        var alice = await AliceAsync();
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Private");
        await alice.ShareWithAsync(bob, "Garten", MemberRole.Read);

        var skill = (await GetAsync(bob, "/api/skills"))["skills"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "release-notes")!;

        Assert.Equal("Garten", skill["location"]!.GetValue<string>());
        Assert.True(skill["byOther"]!.GetValue<bool>());
        Assert.False(skill["canWrite"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_source_is_shown_only_where_the_sealed_origin_agrees()
    {
        const string source = "github:anthropics/skills/skills/pdf@0123456789abcdef0123456789abcdef01234567";
        var alice = await AliceAsync();
        var claimed = Kumbu.Core.Skills.SkillManifest.WithOrigin("---\nname: pdf\ndescription: Fills PDFs.\n---\n", source, "sha256:00ab");
        await alice.CreateAsync("_skills/pdf/SKILL.md", claimed);
        await alice.CreateAsync("_skills/real/SKILL.md", claimed.Replace("name: pdf", "name: real"));
        var work = (await alice.Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(alice.User.Id, alice.UserKey, Ct)).Single(v => v.Name == "Work");
        await alice.Api.Services.GetRequiredService<Kumbu.Server.Publishing.PublicationStore>().ChangeAsync(
            work, r => r with { Origins = [new("/Work/_skills/real", source, "sha256:00ab")] }, Ct);

        var skills = (await GetAsync(alice, "/api/skills"))["skills"]!.AsArray();
        var hits = (await GetAsync(alice, "/api/skills/find?q=" + Uri.EscapeDataString("fill PDFs"))).AsArray().Select(h => h!["skill"]!);

        foreach (var pdf in new[] { skills.Single(s => s!["name"]!.GetValue<string>() == "pdf")!, hits.Single(s => s["name"]!.GetValue<string>() == "pdf") })
        {
            Assert.Null(pdf["source"]);
            Assert.True(pdf["sourceUnverified"]!.GetValue<bool>());
            Assert.False(pdf["originEdited"]!.GetValue<bool>());
        }

        var real = skills.Single(s => s!["name"]!.GetValue<string>() == "real")!;
        Assert.Equal(source, real["source"]!.GetValue<string>());
        Assert.False(real["sourceUnverified"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Find_returns_rank_and_score()
    {
        var alice = await AliceAsync();

        var hits = (await GetAsync(alice, "/api/skills/find?q=" + Uri.EscapeDataString("draft release notes"))).AsArray();

        Assert.NotEmpty(hits);
        Assert.Equal(1, hits[0]!["rank"]!.GetValue<int>());
        Assert.True(hits[0]!["score"]!.GetValue<double>() > 0);
        Assert.Equal("release-notes", hits[0]!["skill"]!["name"]!.GetValue<string>());
        Assert.Equal("/Work/_skills/release-notes", hits[0]!["skill"]!["path"]!.GetValue<string>());
    }

    /// <summary>The fixture's fake embedder stands in for the model, so the real store's index answers by meaning here:
    /// a request sharing no word with the name or description finds a skill by its body.</summary>
    [Fact]
    public async Task Find_by_meaning_goes_through_the_stores_index()
    {
        var alice = await AliceAsync();
        await alice.CreateAsync("_skills/gardening/SKILL.md", "---\nname: gardening\ndescription: Plants.\n---\n# Greenhouse\n\nTomatoes cucumbers trellis irrigation.\n");
        await alice.CreateAsync("_skills/taxes/SKILL.md", "---\nname: taxes\ndescription: Returns.\n---\n# Forms\n\nInvoices deductions receipts.\n");
        await alice.Api.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);
        var embedded = app.Embedder.Seen.Count;

        var hits = (await GetAsync(alice, "/api/skills/find?q=" + Uri.EscapeDataString("tomatoes cucumbers trellis"))).AsArray();

        Assert.Equal("gardening", hits[0]!["skill"]!["name"]!.GetValue<string>());
        Assert.Contains(app.Embedder.Seen.Skip(embedded), s => s.Query && s.Text.Contains("tomatoes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Find_as_a_key_sees_only_its_vaults()
    {
        var alice = await AliceAsync();
        var workOnly = await KeyAsync(alice, "Work agent", new() { [await alice.VaultIdAsync()] = KeyRight.Read });
        var skillsOnly = await KeyAsync(alice, "Skills agent", new() { [await SkillsIdAsync(alice)] = KeyRight.Read });
        var connection = await alice.Api.Services.GetRequiredService<Connections>().CreateAsync(
            alice.User, alice.UserKey, DateTime.UtcNow, "Chat", new Dictionary<string, KeyRight> { [await alice.VaultIdAsync()] = KeyRight.Read }, Ct);

        var all = Names(await GetAsync(alice, "/api/skills/find?q=" + Uri.EscapeDataString("write posts release notes voice")));
        var work = Names(await GetAsync(alice, $"/api/skills/find?q={Uri.EscapeDataString("write posts release notes voice")}&as={workOnly.Id}"));
        var skills = Names(await GetAsync(alice, $"/api/skills/find?q={Uri.EscapeDataString("write posts release notes voice")}&as={skillsOnly.Id}"));
        var viaConnection = Names(await GetAsync(alice, $"/api/skills/find?q={Uri.EscapeDataString("write posts release notes voice")}&as={connection.Id}"));

        Assert.Contains("write-as-alice", all);
        Assert.Contains("release-notes", all);
        Assert.Equal(["release-notes"], work.Where(n => n is "release-notes" or "write-as-alice"));
        Assert.Equal(["write-as-alice"], skills.Where(n => n is "release-notes" or "write-as-alice"));
        Assert.Equal(["release-notes"], viaConnection.Where(n => n is "release-notes" or "write-as-alice"));
    }

    [Fact]
    public async Task Find_as_someone_elses_key_is_404()
    {
        var alice = await AliceAsync();
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Private");
        var bobs = await KeyAsync(bob, "Bob's", new() { [await bob.VaultIdAsync()] = KeyRight.Read });
        var device = await KeyAsync(alice, "Laptop", new() { [await alice.VaultIdAsync()] = KeyRight.Read }, ApiKeyKind.Device);

        foreach (var id in new[] { bobs.Id, device.Id, "nobody" })
        {
            using var response = await alice.GetAsync($"/api/skills/find?q=notes&as={id}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Listing_as_a_credential_leaves_its_last_use_alone()
    {
        var alice = await AliceAsync();
        var key = await KeyAsync(alice, "Work agent", new() { [await alice.VaultIdAsync()] = KeyRight.Read });

        await GetAsync(alice, $"/api/skills/find?q=notes&as={key.Id}");
        await GetAsync(alice, "/api/skills");

        // The last use is written beside the request: give it the time a use takes to show.
        await Task.Delay(500, Ct);
        var slot = Builders<SlotDocument>.Filter.Eq(s => s.KeyId, key.Id);
        Assert.All(await app.Database.GetCollection<SlotDocument>("slots").Find(slot).ToListAsync(Ct), s => Assert.Null(s.UsedAt));
        var after = (await alice.Api.Services.GetRequiredService<ApiKeys>().ListAsync(alice.User, alice.UserKey, Ct)).Single(k => k.Id == key.Id);
        Assert.Null(after.LastUsedAt);
    }

    [Fact]
    public async Task An_api_key_cant_call_it()
    {
        var alice = await AliceAsync();
        var key = await alice.KeyAsync("work", KeyRight.Write);

        using var list = await alice.GetAsync("/api/skills", key);
        using var find = await alice.GetAsync("/api/skills/find?q=notes", key);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, find.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?q=")]
    [InlineData("?q=%20%20")]
    public async Task An_empty_query_is_400(string query)
    {
        var alice = await AliceAsync();

        using var response = await alice.GetAsync("/api/skills/find" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
