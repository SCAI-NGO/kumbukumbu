// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Api;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// Owner-only actions (collab design §3.3) answer a member with 404, as for a vault she doesn't have (§3.5), at every
/// role; the embedding provider too, which resolves the vault through the open store. Pairing a device to a shared
/// vault is refused for everyone until the plugin knows members (planner decision 7).
/// </summary>
[Collection("app")]
public sealed class OwnerOnlyTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    /// <summary>Alice's "Gartenarbeit", shared with Bob as "garten"; Bob owns "Work".</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId, string MembershipId)> SharedAsync(MemberRole role = MemberRole.Write, string bobsVault = "Work")
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "# Beete\n");
        var bob = await RestClient.NewAsync(Api, vault: bobsVault);
        var membership = await alice.ShareWithAsync(bob, "garten", role);
        return (alice, bob, await alice.VaultIdAsync(), membership);
    }

    private static Task<HttpResponseMessage> ChooseAsync(RestClient user, string vault) =>
        user.SendAsync(HttpMethod.Put, $"/api/vaults/{Uri.EscapeDataString(vault)}/embeddings", new { provider = "internal" });

    private static Task<HttpResponseMessage> PairAsync(RestClient user, string vault) =>
        user.SendAsync(HttpMethod.Post, $"/api/vaults/{Uri.EscapeDataString(vault)}/pairing", new { });

    private Task<bool> StartedAsync(string vaultId) =>
        app.Database.GetCollection<BsonDocument>("vaults").Find(new BsonDocument { ["_id"] = vaultId, ["rekeyStartedAt"] = new BsonDocument("$exists", true) }).AnyAsync(Ct);

    [Fact]
    public async Task A_member_may_not_change_the_embedding_provider()
    {
        var (alice, bob, _, membership) = await SharedAsync();

        foreach (var role in new[] { MemberRole.Read, MemberRole.Write, MemberRole.Maintain })
        {
            await Share.SetRoleAsync(app.Database, membership, role);
            using var refused = await ChooseAsync(bob, "garten");
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Equal("You have no vault named 'garten'.", await RestClient.ErrorAsync(refused));
        }

        // The owner's own vaults still work, Alice's and Bob's alike.
        using (var owners = await ChooseAsync(alice, "Gartenarbeit")) Assert.Equal(HttpStatusCode.OK, owners.StatusCode);
        using var bobs = await ChooseAsync(bob, "Work");
        Assert.Equal(HttpStatusCode.OK, bobs.StatusCode);
        Assert.Equal("Work", (await RestClient.JsonAsync(bobs))["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_member_may_not_rename_the_vault()
    {
        var (alice, bob, _, membership) = await SharedAsync();

        foreach (var role in new[] { MemberRole.Read, MemberRole.Write, MemberRole.Maintain })
        {
            await Share.SetRoleAsync(app.Database, membership, role);
            // Also to the name he has for it already, which the owner's own rename would answer with 204 and do nothing.
            foreach (var name in new[] { "Beet", "garten" })
            {
                using var refused = await bob.SendAsync(HttpMethod.Put, "/api/vaults/garten/name", new { name });
                Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
                Assert.Equal("You have no vault named 'garten'.", await RestClient.ErrorAsync(refused));
            }
        }

        using (var alices = await alice.GetAsync("/api/vaults"))
        {
            Assert.Equal(["Gartenarbeit", "Skills"], (await RestClient.JsonAsync(alices)).AsArray().Select(v => v!["name"]!.GetValue<string>()).ToArray());
        }

        using var bobs = await bob.GetAsync("/api/vaults");
        Assert.Equal(["Skills", "Work", "garten"], (await RestClient.JsonAsync(bobs)).AsArray().Select(v => v!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("rekey")]
    [InlineData("access")]
    [InlineData("embeddings")]
    public async Task Owner_only_endpoints_answer_a_member_with_404(string endpoint)
    {
        var (alice, bob, vaultId, _) = await SharedAsync(MemberRole.Maintain);

        using var response = endpoint switch
        {
            "delete" => await bob.SendAsync(HttpMethod.Post, "/api/vaults/garten/delete", new { password = RestClient.Password, confirm = "garten" }),
            "rekey" => await bob.SendAsync(HttpMethod.Post, "/api/vaults/garten/rekey", new { password = RestClient.Password }),
            "access" => await bob.GetAsync("/api/vaults/garten/access"),
            _ => await ChooseAsync(bob, "garten"),
        };

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // Nothing happened to the vault: there, under its key, and Bob still in.
        Assert.Contains(vaultId, (await Catalog.OpenForUserAsync(alice.User.Id, alice.UserKey, Ct)).Select(a => a.Id));
        Assert.False(await StartedAsync(vaultId));
        using var read = await bob.GetAsync("/api/notes/garten/plan.md");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    // Task 1.14 refused these codes until the plugin check at redemption (task 5.8, PairingApiTests) came.
    [Fact]
    public async Task The_member_and_the_owner_get_pairing_codes_for_the_shared_vault()
    {
        var (alice, bob, _, _) = await SharedAsync();

        using (var member = await PairAsync(bob, "garten")) Assert.Equal(HttpStatusCode.Created, member.StatusCode);
        using (var owner = await PairAsync(alice, "Gartenarbeit")) Assert.Equal(HttpStatusCode.Created, owner.StatusCode);
        using (var bobs = await PairAsync(bob, "Work")) Assert.Equal(HttpStatusCode.Created, bobs.StatusCode);
    }

    [Fact]
    public async Task A_members_own_vault_named_like_the_shared_one_stays_apart()
    {
        // Bob owns a "Gartenarbeit" of his own and has Alice's as "garten".
        var (alice, bob, vaultId, _) = await SharedAsync(MemberRole.Maintain, bobsVault: "Gartenarbeit");
        var bobsId = await bob.VaultIdAsync();
        Assert.NotEqual(vaultId, bobsId);

        await bob.CreateAsync("eigen.md", "# Bobs\n");
        using (var shared = await bob.SendAsync(HttpMethod.Put, "/api/notes/garten/geteilt.md", new { content = "# Geteilt\n" })) Assert.Equal(HttpStatusCode.Created, shared.StatusCode);

        // Each name reaches its own vault, and nothing crosses.
        Assert.Equal("# Bobs\n", (await bob.ReadAsync("eigen.md"))["content"]!.GetValue<string>());
        using (var mine = await bob.GetAsync("/api/notes/Gartenarbeit/plan.md")) Assert.Equal(HttpStatusCode.NotFound, mine.StatusCode);
        using (var theirs = await bob.GetAsync("/api/notes/garten/plan.md")) Assert.Equal(HttpStatusCode.OK, theirs.StatusCode);
        using (var notThere = await bob.GetAsync("/api/notes/garten/eigen.md")) Assert.Equal(HttpStatusCode.NotFound, notThere.StatusCode);
        using (var alices = await alice.GetAsync("/api/notes/Gartenarbeit/geteilt.md")) Assert.Equal(HttpStatusCode.OK, alices.StatusCode);
        using (var notAlices = await alice.GetAsync("/api/notes/Gartenarbeit/eigen.md")) Assert.Equal(HttpStatusCode.NotFound, notAlices.StatusCode);

        // His owner-only actions on "Gartenarbeit" are on his own vault; on "garten" they answer 404.
        using (var own = await ChooseAsync(bob, "Gartenarbeit")) Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using (var refused = await ChooseAsync(bob, "garten")) Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        using (var deleted = await bob.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/delete", new { password = RestClient.Password, confirm = "Gartenarbeit" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.DoesNotContain(bobsId, (await Catalog.OpenForUserAsync(bob.User.Id, bob.UserKey, Ct)).Select(a => a.Id));
        Assert.Equal("# Beete\n", (await alice.ReadAsync("plan.md"))["content"]!.GetValue<string>());
        using var still = await bob.GetAsync("/api/notes/garten/geteilt.md");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }
}
