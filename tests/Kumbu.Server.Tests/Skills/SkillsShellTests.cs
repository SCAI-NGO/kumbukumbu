// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Skills;

/// <summary><c>skills</c> over MCP against the real store: a key sees the skills of the vaults it reaches and no
/// others, and <c>skills copy</c> writes only where the key may write.</summary>
[Collection("app")]
public sealed class SkillsShellTests(KumbuAppFactory app)
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

    /// <summary>A key of <paramref name="user"/> with these rights, by vault id.</summary>
    private static async Task<string> KeyAsync(RestClient user, Dictionary<string, KeyRight> rights, ApiKeyKind kind = ApiKeyKind.Agent) =>
        (await user.Api.Services.GetRequiredService<ApiKeys>().CreateAsync(user.User, user.UserKey, DateTime.UtcNow, "Claude", rights, Ct, kind: kind)).Key;

    [Fact]
    public async Task A_key_without_Skills_doesnt_see_its_skills()
    {
        var alice = await AliceAsync();
        var workOnly = await alice.KeyAsync("work", KeyRight.Write);

        var (_, ls) = await alice.ShAsync("skills ls", workOnly);
        Assert.Contains("release-notes (/Work/_skills/release-notes", ls);
        Assert.DoesNotContain("write-as-alice", ls);
        var (_, lsSkills) = await alice.ShAsync("skills ls /Skills", workOnly);
        Assert.DoesNotContain("write-as-alice", lsSkills);
        var (_, found) = await alice.ShAsync("skills find \"write posts in Alice's voice\"", workOnly);
        Assert.DoesNotContain("write-as-alice", found);
        var (_, foundWork) = await alice.ShAsync("skills find \"draft release notes\"", workOnly);
        Assert.StartsWith("1. release-notes (/Work/_skills/release-notes", foundWork);
        foreach (var line in new[] { "skills cat write-as-alice", "skills cat /Skills/write-as-alice", "skills cat /Skills/write-as-alice/SKILL.md", "skills check /Skills" })
        {
            var (isError, text) = await alice.ShAsync(line, workOnly);
            Assert.True(isError, line);
            Assert.DoesNotContain("Alice's voice", text);
        }

        // And the other way round: a key on Skills alone sees nothing of Work's skills.
        var skillsOnly = await KeyAsync(alice, new() { [await SkillsIdAsync(alice)] = KeyRight.Read });
        var (_, other) = await alice.ShAsync("skills ls", skillsOnly);
        Assert.Contains("write-as-alice (/Skills/write-as-alice", other);
        Assert.DoesNotContain("release-notes", other);
        var (_, foundSkills) = await alice.ShAsync("skills find \"write posts in Alice's voice\"", skillsOnly);
        Assert.StartsWith("1. write-as-alice (/Skills/write-as-alice", foundSkills);
        foreach (var line in new[] { "skills cat release-notes", "skills cat /Work/_skills/release-notes", "skills ls /Work", "skills find \"draft release notes\"" })
        {
            var (_, text) = await alice.ShAsync(line, skillsOnly);
            Assert.DoesNotContain("Drafts release notes", text);
        }
    }

    [Fact]
    public async Task A_key_with_read_on_Skills_cant_copy_into_it()
    {
        var alice = await AliceAsync();
        var key = await KeyAsync(alice, new() { [await SkillsIdAsync(alice)] = KeyRight.Read, [await alice.VaultIdAsync()] = KeyRight.Write });

        var (isError, text) = await alice.ShAsync("skills copy create-skill", key);

        Assert.True(isError);
        Assert.Contains("this key can only read vault Skills", text);
        using var read = await alice.GetAsync("/api/notes/Skills/create-skill/SKILL.md");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        // Where it may write, the copy goes through.
        var (copyError, copied) = await alice.ShAsync("skills copy create-skill /Work", key);
        Assert.False(copyError, copied);
        Assert.StartsWith("copied create-skill to /Work/_skills/create-skill/ (1 file)", copied);
        Assert.Contains("name: create-skill", (await alice.ReadAsync("_skills/create-skill/SKILL.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_member_sees_the_shared_vaults_skills_marked()
    {
        var alice = await AliceAsync();
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Private");
        await alice.ShareWithAsync(bob, "Garten", MemberRole.Write);
        var key = await KeyAsync(bob, new() { [await bob.VaultIdOfSharedAsync("Garten")] = KeyRight.Read });

        var (isError, ls) = await bob.ShAsync("skills ls", key);

        Assert.False(isError, ls);
        var line = Assert.Single(ls.Split('\n'), l => l.StartsWith("release-notes", StringComparison.Ordinal));
        Assert.StartsWith("release-notes (/Garten/_skills/release-notes, shared by \"", line);
        Assert.Contains("another user", line);
        Assert.EndsWith(": Drafts release notes.", line);
        // cat's notice above the text: it is someone else's.
        Assert.Equal("[/Garten/_skills/release-notes/SKILL.md: last written by another user]\n" + ReleaseNotes, (await bob.ShAsync("skills cat release-notes", key)).Text);
    }

    [Fact]
    public async Task A_device_key_given_Skills_reaches_its_files()
    {
        var alice = await AliceAsync();
        var onSkills = await KeyAsync(alice, new() { [await SkillsIdAsync(alice)] = KeyRight.Write }, ApiKeyKind.Device);
        var onWork = await alice.KeyAsync("Work – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);

        using var versions = await alice.GetAsync("/api/vaults/Skills/versions?text=1", onSkills);
        Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
        var paths = (await RestClient.JsonAsync(versions))["notes"]!.AsArray().Select(n => n is JsonObject o ? o["path"]!.GetValue<string>() : n!.GetValue<string>());
        Assert.Contains("/Skills/write-as-alice/SKILL.md", paths);

        using var refused = await alice.GetAsync("/api/vaults/Skills/versions?text=1", onWork);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }
}
