// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>What a member sees of a vault shared with her (collab design §8.6, §10.3): its owner, her effective level,
/// and which notes another user wrote last.</summary>
[Collection("app")]
public sealed class MemberViewTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>Alice ("Alice\nExample" as display name) shares "Gartenarbeit" with Bob as "garten", to write.</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId)> SharedAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "Beete planen\n");
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == alice.User.Id, Builders<UserDocument>.Update.Set(u => u.Display, "Alice\nExample"), cancellationToken: Ct);
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "garten", MemberRole.Write);
        return (alice, bob, await alice.VaultIdAsync());
    }

    private async Task<string> KeyAsync(RestClient user, KeyRight right, params string[] vaultIds) =>
        (await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, "Claude " + right, vaultIds.ToDictionary(v => v, _ => right), Ct)).Key;

    private static async Task<System.Text.Json.Nodes.JsonArray> VaultsAsync(RestClient user, string? bearer = null)
    {
        using var response = await user.GetAsync("/api/vaults", bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response)).AsArray();
    }

    [Fact]
    public async Task VaultView_carries_owner_level_and_shared()
    {
        var (alice, bob, vaultId) = await SharedAsync();

        var bobs = await VaultsAsync(bob);
        var garten = bobs.Single(v => v!["name"]!.GetValue<string>() == "garten")!;
        Assert.Equal(("\"AliceExample\"", "write", true, true), (
            garten["owner"]!.GetValue<string>(), garten["level"]!.GetValue<string>(), garten["shared"]!.GetValue<bool>(), garten["canWrite"]!.GetValue<bool>()));
        var work = bobs.Single(v => v!["name"]!.GetValue<string>() == "Work")!;
        Assert.Equal(("owner", false), (work["level"]!.GetValue<string>(), work["shared"]!.GetValue<bool>()));
        Assert.Null(work["owner"]);

        // Alice's own vault is shared; she is its owner, and nobody's name is on it.
        var hers = (await VaultsAsync(alice)).Single()!;
        Assert.Equal(("owner", true), (hers["level"]!.GetValue<string>(), hers["shared"]!.GetValue<bool>()));
        Assert.Null(hers["owner"]);

        // Through Bob's read key the level is the key's, canWrite derived from it.
        var read = (await VaultsAsync(bob, await KeyAsync(bob, KeyRight.Read, vaultId))).Single()!;
        Assert.Equal(("read", false), (read["level"]!.GetValue<string>(), read["canWrite"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Ls_shows_the_owner_and_the_effective_level_and_cat_marks_another_users_note()
    {
        var (alice, bob, vaultId) = await SharedAsync();
        // A key sealed maintain acts at Bob's role, write; one sealed read at read.
        var maintain = await KeyAsync(bob, KeyRight.Maintain, vaultId, await bob.VaultIdAsync());
        var read = await KeyAsync(bob, KeyRight.Read, vaultId);

        var (failed, listed) = await bob.ShAsync("ls /", maintain);
        Assert.False(failed, listed);
        Assert.Equal("garten/ (shared by \"AliceExample\", write)\nWork/", listed);
        Assert.Equal("garten/ (shared by \"AliceExample\", read)", (await bob.ShAsync("ls /", read)).Text);

        // Alice wrote plan.md: another user's to Bob. His agent's own note isn't marked, for him.
        Assert.Equal("[/garten/plan.md: last written by another user]\nBeete planen\n", (await bob.ShAsync("cat /garten/plan.md", maintain)).Text);
        using (var written = await bob.SendAsync(HttpMethod.Put, "/api/notes/garten/kohl.md", new { content = "Kohl\n" }, bearer: maintain)) Assert.Equal(HttpStatusCode.Created, written.StatusCode);
        Assert.Equal("Kohl\n", (await bob.ShAsync("cat /garten/kohl.md", maintain)).Text);
        // To Alice it is the other way round, in her own, shared vault.
        var hers = await KeyAsync(alice, KeyRight.Write, vaultId);
        Assert.Equal("[/Gartenarbeit/kohl.md: last written by another user]\nKohl\n", (await alice.ShAsync("cat /Gartenarbeit/kohl.md", hers)).Text);
        Assert.Equal("Beete planen\n", (await alice.ShAsync("cat /Gartenarbeit/plan.md", hers)).Text);
        Assert.Equal("[/garten/plan.md: last written by another user]\n/garten/plan.md:1:Beete planen", (await bob.ShAsync("rg Beete /garten", maintain)).Text);
    }
}
