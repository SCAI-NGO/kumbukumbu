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
    public async Task Bob_sees_no_vault_event_from_before_he_joined()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit", remember: true);
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var vaultId = await alice.VaultIdAsync();
        var events = app.Database.GetCollection<VaultEventDocument>(VaultEvents.CollectionName);
        // While the vault was shared with someone else, before Bob.
        await events.InsertOneAsync(new VaultEventDocument { Vault = vaultId, Kind = VaultEvents.ProviderChanged, At = DateTime.UtcNow.AddHours(-1) }, cancellationToken: Ct);
        await alice.ShareWithAsync(bob, "Garten", MemberRole.Read, label: "Bob");
        await events.InsertOneAsync(new VaultEventDocument { Vault = vaultId, Kind = VaultEvents.ProviderChanged, At = DateTime.UtcNow.AddSeconds(1) }, cancellationToken: Ct);

        Assert.Single(await ActivityTextsAsync(bob, "Garten"));
        Assert.Single(await FeedAsync(bob));
        Assert.Equal(2, (await ActivityTextsAsync(alice, "Gartenarbeit")).Count(t => t == "You changed the embedding provider."));
    }

    [Fact]
    public async Task The_owner_sees_it_in_her_activity()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var vaultId = await alice.VaultIdAsync();

        using (var renamed = await alice.SendAsync(HttpMethod.Put, "/api/vaults/Gartenarbeit/name", new { name = "Garten" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        Assert.Equal(["You renamed this vault."], await ActivityTextsAsync(alice, "Garten"));
        // A plain document: neither the old name nor the new.
        var stored = Assert.Single(await app.Database.GetCollection<BsonDocument>(VaultEvents.CollectionName).Find(new BsonDocument("vault", vaultId)).ToListAsync(Ct));
        Assert.Equal(VaultEvents.VaultRenamed, stored["kind"].AsString);
        Assert.DoesNotContain("Garten", stored.ToJson(), StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task The_owner_is_told_how_many_open_invitations_a_re_encryption_withdrew()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit", remember: true);
        await alice.CreateAsync("plan.md", "# Beete\n");
        using (var invited = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/invitations", new { password = RestClient.Password, label = "Carol", role = "read", expiresInDays = 7, confirm = false }))
        {
            Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        }

        using var rekeyed = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/rekey", new { password = RestClient.Password });

        Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
        Assert.Equal(1, (await RestClient.JsonAsync(rekeyed))["withdrawnInvitations"]!.GetValue<int>());
    }

    [Fact]
    public async Task Members_are_told_to_give_their_agents_the_vault_again()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit", remember: true);
        await SetDisplayAsync(alice, "Alice");
        await alice.CreateAsync("plan.md", "# Beete\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var vaultId = await alice.VaultIdAsync();
        await alice.ShareWithAsync(bob, "Garten", MemberRole.Write, label: "Bob");
        var (key, info) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write, [await bob.VaultIdAsync()] = KeyRight.Write }, Ct);

        using (var rekeyed = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/rekey", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
        }

        Assert.Equal(["\"Alice\" re-encrypted Garten; your keys and apps for it were revoked. Give your agents Garten again."], await ActivityTextsAsync(bob, "Garten"));
        var feed = Assert.Single(await FeedAsync(bob))!;
        Assert.Equal(VaultEvents.RekeyFinished, feed["kind"]!.GetValue<string>());
        Assert.Equal(["Gartenarbeit was re-encrypted; members' keys and apps for it were revoked."], await ActivityTextsAsync(alice, "Gartenarbeit"));
        // One vault-wide event, naming his key that lost the vault by id only.
        var raw = await app.Database.GetCollection<VaultEventDocument>(VaultEvents.CollectionName).Find(e => e.Vault == vaultId).SingleAsync(Ct);
        Assert.Null(raw.Membership);
        Assert.Equal([info.Id], raw.Credentials);
        Assert.False(await app.Database.GetCollection<Kumbu.Server.Vaults.VaultDocument>("vaults").Find(v => v.Id == vaultId && v.RekeyEnded != null).AnyAsync(Ct));

        // His key kept his own vault, and its next call says why it lost the shared one.
        var blocks = await bob.ShBlocksAsync("ls /", key);
        Assert.Equal("\nNotice: The owner (\"Alice\") re-encrypted a vault shared with you; you no longer reach it until you are given it again.", blocks[^1]);
        Assert.StartsWith("Work/", blocks[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_Bob_leaves_the_owner_is_offered_a_re_encryption_until_she_re_keys()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit", remember: true);
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        var vaultId = await alice.VaultIdAsync();
        var bobs = await alice.ShareWithAsync(bob, "Garten", MemberRole.Read, label: "Bob");
        await alice.ShareWithAsync(carol, "Beete", MemberRole.Read, label: "Carol");
        using (var left = await bob.SendAsync(HttpMethod.Post, $"/api/memberships/{bobs}/leave")) Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);

        async Task<string[]> LeftAsync()
        {
            using var access = await alice.GetAsync("/api/vaults/Gartenarbeit/access");
            Assert.Equal(HttpStatusCode.OK, access.StatusCode);
            return [.. (await RestClient.JsonAsync(access))["left"]!.AsArray().Select(d => d!["label"]!.GetValue<string>())];
        }

        Assert.Equal(["Bob"], await LeftAsync());
        using (var rekeyed = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/rekey", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
        }

        // Re-encrypted after he left: nothing more to offer.
        Assert.Empty(await LeftAsync());
        Assert.Equal("Gartenarbeit was re-encrypted; members' keys and apps for it were revoked.", (await ActivityTextsAsync(alice, "Gartenarbeit"))[0]);

        // A vault never shared records nothing when re-keyed.
        var diaryId = await carol.VaultIdAsync();
        using (var rekeyed = await carol.SendAsync(HttpMethod.Post, "/api/vaults/Notizen/rekey", new { password = RestClient.Password })) Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
        Assert.False(await app.Database.GetCollection<VaultEventDocument>(VaultEvents.CollectionName).Find(e => e.Vault == diaryId).AnyAsync(Ct));
    }
}
