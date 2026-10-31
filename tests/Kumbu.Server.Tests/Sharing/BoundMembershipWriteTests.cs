// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// HARD RULE 2: every route that changes a membership is bound to the vault resolved from the owner's own slot, or to
/// the caller as the member, and to the status it may move from (collab design §6.0, §13 H2). An id of someone else's
/// membership answers 404 and changes nothing, byte for byte. Never weakened for another test.
/// </summary>
[Collection("app")]
public sealed class BoundMembershipWriteTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Memberships Memberships => Api.Services.GetRequiredService<Memberships>();

    /// <summary>The membership and every slot that belongs to it, as MongoDB holds them.</summary>
    private async Task<string> SnapshotAsync(string membershipId)
    {
        var doc = await app.Database.GetCollection<BsonDocument>(Memberships.CollectionName).Find(new BsonDocument("_id", membershipId)).SingleAsync(Ct);
        var slots = await app.Database.GetCollection<BsonDocument>("slots")
            .Find(new BsonDocument("$or", new BsonArray { new BsonDocument("membership", membershipId), new BsonDocument("grantedVia", membershipId) }))
            .Sort(new BsonDocument("_id", 1)).ToListAsync(Ct);
        return doc.ToJson() + string.Concat(slots.Select(s => s.ToJson()));
    }

    /// <summary>A membership of <paramref name="owner"/>'s vault with <paramref name="member"/> in the state
    /// <paramref name="route"/> moves from: redeemed and waiting for confirmation for confirm and decline, an open
    /// invitation for withdraw, active for revoke and role.</summary>
    private async Task<string> MembershipForAsync(string route, RestClient owner, RestClient member)
    {
        var vaultId = await owner.VaultIdAsync();
        switch (route)
        {
            case "confirm" or "decline":
                var invite = await Memberships.InviteAsync(owner.User.Id, owner.UserKey, vaultId, "Bob", MemberRole.Read, TimeSpan.FromDays(7), confirm: true, Ct);
                await Memberships.AcceptAsync(member.User.Id, member.UserKey, invite.Secret, owner.Vault, Ct);
                return invite.MembershipId;
            case "withdraw":
                return (await Memberships.InviteAsync(owner.User.Id, owner.UserKey, vaultId, "Bob", MemberRole.Read, TimeSpan.FromDays(7), confirm: false, Ct)).MembershipId;
            default:
                return await owner.ShareWithAsync(member, owner.Vault, MemberRole.Read);
        }
    }

    /// <summary><paramref name="route"/> as <paramref name="caller"/>'s session, on her vault <paramref name="vault"/>'s URL.</summary>
    private static Task<HttpResponseMessage> CallAsync(RestClient caller, string route, string vault, string membershipId)
    {
        var url = $"/api/vaults/{Uri.EscapeDataString(vault)}/memberships/{membershipId}";
        return route switch
        {
            "withdraw" => caller.SendAsync(HttpMethod.Delete, url),
            "role" => caller.SendAsync(HttpMethod.Put, url + "/role", new { role = "maintain" }),
            "leave" => caller.SendAsync(HttpMethod.Post, $"/api/memberships/{membershipId}/leave"),
            _ => caller.SendAsync(HttpMethod.Post, url + "/" + route),
        };
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("decline")]
    [InlineData("withdraw")]
    [InlineData("revoke")]
    [InlineData("role")]
    public async Task An_owner_cannot_touch_a_membership_of_another_vault(string route)
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var carol = await RestClient.NewAsync(Api, vault: "Work");
        var bob = await RestClient.NewAsync(Api, vault: "Privat");
        var carols = await MembershipForAsync(route, carol, bob);
        var before = await SnapshotAsync(carols);

        using var response = await CallAsync(alice, route, "Gartenarbeit", carols);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(carols));
        // The membership was one the route moves: on its own vault's URL, its owner's call succeeds.
        using var own = await CallAsync(carol, route, "Work", carols);
        Assert.True(own.IsSuccessStatusCode, $"{route}: {own.StatusCode}");
        Assert.NotEqual(before, await SnapshotAsync(carols));
    }

    [Fact]
    public async Task An_owner_cannot_raise_her_own_role_in_someone_elses_vault()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var carol = await RestClient.NewAsync(Api, vault: "Work");
        var hers = await carol.ShareWithAsync(alice, "Arbeit", MemberRole.Read);
        var before = await SnapshotAsync(hers);

        // On her own vault's URL, and on her alias's: neither is the vault of the membership as its owner.
        foreach (var vault in new[] { "Gartenarbeit", "Arbeit" })
        {
            using var response = await CallAsync(alice, "role", vault, hers);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(before, await SnapshotAsync(hers));
        Assert.Equal("read", (await Share.MembershipAsync(app.Database, hers)).Role);
    }

    [Fact]
    public async Task A_member_cannot_leave_for_someone_else()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        await alice.ShareWithAsync(bob, "Garten");
        var carols = await alice.ShareWithAsync(carol, "Garten");
        var before = await SnapshotAsync(carols);

        // Neither another member nor the owner leaves in Carol's place.
        foreach (var caller in new[] { bob, alice })
        {
            using var response = await CallAsync(caller, "leave", "", carols);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(before, await SnapshotAsync(carols));
        using var hers = await CallAsync(carol, "leave", "", carols);
        Assert.Equal(HttpStatusCode.NoContent, hers.StatusCode);
    }

    [Theory]
    [InlineData("confirm", MembershipStatus.Active)]
    [InlineData("decline", MembershipStatus.Active)]
    [InlineData("withdraw", MembershipStatus.Active)]
    [InlineData("revoke", MembershipStatus.Revoked)]
    [InlineData("role", MembershipStatus.Revoked)]
    [InlineData("leave", MembershipStatus.Left)]
    public async Task A_route_may_only_move_from_its_statuses(string route, string status)
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var membership = await alice.ShareWithAsync(bob, "Garten", MemberRole.Read);
        if (status != MembershipStatus.Active)
        {
            using var ended = await CallAsync(status == MembershipStatus.Left ? bob : alice, status == MembershipStatus.Left ? "leave" : "revoke", "Gartenarbeit", membership);
            Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        }

        var before = await SnapshotAsync(membership);

        using var response = await CallAsync(route == "leave" ? bob : alice, route, "Gartenarbeit", membership);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(membership));
        Assert.Equal(status, (await Share.MembershipAsync(app.Database, membership)).Status);
    }
}
