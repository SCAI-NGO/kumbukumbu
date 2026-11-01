// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>The sharing's events (collab design §8.3): plain documents, their text built per viewer when read.</summary>
[Collection("app")]
public sealed class VaultEventTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Memberships Memberships => Api.Services.GetRequiredService<Memberships>();

    private Task SetDisplayAsync(RestClient user, string display) =>
        app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == user.User.Id, Builders<UserDocument>.Update.Set(u => u.Display, display), cancellationToken: Ct);

    private static async Task<string[]> ActivityTextsAsync(RestClient user, string vault)
    {
        using var response = await user.GetAsync($"/api/vaults/{Uri.EscapeDataString(vault)}/activity");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await RestClient.JsonAsync(response))["events"]!.AsArray().Select(e => e!["text"]!.GetValue<string>())];
    }

    private static async Task<JsonArray> FeedAsync(RestClient member)
    {
        using var response = await member.GetAsync("/api/vault-events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response)).AsArray();
    }

    private static string Url(RestClient owner, string membershipId, string action) =>
        $"/api/vaults/{Uri.EscapeDataString(owner.Vault)}/memberships/{membershipId}/{action}";

    [Fact]
    public async Task The_owner_sees_invited_accepted_and_revoked()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var vaultId = await alice.VaultIdAsync();
        var invite = await Memberships.InviteAsync(alice.User.Id, alice.UserKey, vaultId, "Bob", MemberRole.Write, TimeSpan.FromDays(7), false, Ct);
        await Memberships.AcceptAsync(bob.User.Id, bob.UserKey, invite.Secret, "Garten", Ct);
        var withdrawn = await Memberships.InviteAsync(alice.User.Id, alice.UserKey, vaultId, "Carol", MemberRole.Read, TimeSpan.FromDays(7), false, Ct);
        await Memberships.WithdrawAsync(vaultId, withdrawn.MembershipId, Ct);
        await Task.Delay(5, Ct);
        using (var revoked = await alice.SendAsync(HttpMethod.Post, Url(alice, invite.MembershipId, "revoke"))) Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        Assert.Equal(
            ["You removed Bob.", "You withdrew the invitation for Carol.", "You invited Carol to read.", "Bob accepted the invitation.", "You invited Bob to write."],
            await ActivityTextsAsync(alice, "Gartenarbeit"));
        // Bob no longer reaches the vault; his own feed still says what happened.
        var feed = await FeedAsync(bob);
        var removed = Assert.Single(feed)!;
        Assert.Equal(VaultEvents.MembershipRevoked, removed["kind"]!.GetValue<string>());
        Assert.Null(removed["vault"]);
        Assert.Contains("removed you from a vault shared with you", removed["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bob_sees_his_role_change_as_Alice_changed_your_role_to_write()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit", remember: true);
        await SetDisplayAsync(alice, "Alice");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var membershipId = await alice.ShareWithAsync(bob, "Garten", MemberRole.Read, label: "Bob");

        using (var raised = await alice.SendAsync(HttpMethod.Put, Url(alice, membershipId, "role"), new { role = "write" })) Assert.Equal(HttpStatusCode.OK, raised.StatusCode);

        Assert.Equal(["\"Alice\" changed your role to write."], await ActivityTextsAsync(bob, "Garten"));
        Assert.Equal(["You changed Bob's role to write."], await ActivityTextsAsync(alice, "Gartenarbeit"));
        var feed = Assert.Single(await FeedAsync(bob))!;
        Assert.Equal(("Garten", "write"), (feed["vault"]!.GetValue<string>(), feed["role"]!.GetValue<string>()));
        // Built when read: a new display name shows in the old event, and the document holds no text.
        await SetDisplayAsync(alice, "Alice\nExample");
        Assert.Equal(["\"AliceExample\" changed your role to write."], await ActivityTextsAsync(bob, "Garten"));
        var raw = await app.Database.GetCollection<BsonDocument>(VaultEvents.CollectionName).Find(new BsonDocument("membership", membershipId)).SingleAsync(Ct);
        Assert.Equal(["_id", "vault", "kind", "membership", "at", "role"], raw.Names);
    }

    [Fact]
    public async Task Carol_does_not_see_Bobs_events()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit", remember: true);
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        var bobs = await alice.ShareWithAsync(bob, "Garten", MemberRole.Read, label: "Bob");
        await alice.ShareWithAsync(carol, "Beete", MemberRole.Read, label: "Carol");
        using (var raised = await alice.SendAsync(HttpMethod.Put, Url(alice, bobs, "role"), new { role = "write" })) Assert.Equal(HttpStatusCode.OK, raised.StatusCode);
        using (var left = await bob.SendAsync(HttpMethod.Post, $"/api/memberships/{bobs}/leave")) Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);

        Assert.Empty(await ActivityTextsAsync(carol, "Beete"));
        Assert.Empty(await FeedAsync(carol));
        Assert.Equal(2, (await FeedAsync(bob)).Count);
        Assert.Equal(["Bob left Gartenarbeit.", "You changed Bob's role to write."], await ActivityTextsAsync(alice, "Gartenarbeit"));
    }

    [Fact]
    public async Task Events_go_with_the_vault()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit", remember: true);
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var vaultId = await alice.VaultIdAsync();
        var membershipId = await alice.ShareWithAsync(bob, "Garten", MemberRole.Read, label: "Bob");
        using (var raised = await alice.SendAsync(HttpMethod.Put, Url(alice, membershipId, "role"), new { role = "write" })) Assert.Equal(HttpStatusCode.OK, raised.StatusCode);
        var events = app.Database.GetCollection<VaultEventDocument>(VaultEvents.CollectionName);
        Assert.True(await events.Find(e => e.Vault == vaultId).AnyAsync(Ct));

        using var deleted = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/delete", new { password = RestClient.Password, confirm = "Gartenarbeit" });

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await events.Find(e => e.Vault == vaultId).AnyAsync(Ct));
    }
}
