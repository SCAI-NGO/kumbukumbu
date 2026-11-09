// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Kumbu.Server.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>Vault invitations over REST (collab design §4): creating, peeking and accepting.</summary>
[Collection("app")]
public sealed class InvitationApiTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Memberships Memberships => Api.Services.GetRequiredService<Memberships>();

    private IMongoCollection<MembershipDocument> Docs => app.Database.GetCollection<MembershipDocument>(Memberships.CollectionName);

    private static Task<HttpResponseMessage> InviteAsync(RestClient owner, string? vault = null, object? body = null) =>
        owner.SendAsync(
            HttpMethod.Post, $"/api/vaults/{Uri.EscapeDataString(vault ?? owner.Vault)}/invitations",
            body ?? new { password = RestClient.Password, label = "Bob", role = "write" });

    /// <summary>An invitation made through the service, without the route's password check (whose budget is 5 a minute).</summary>
    private async Task<CreatedInvite> InviteDirectAsync(RestClient owner, bool confirm = false, MemberRole role = MemberRole.Write) =>
        await Memberships.InviteAsync(owner.User.Id, owner.UserKey, await owner.VaultIdAsync(), "Bob", role, TimeSpan.FromDays(7), confirm, Ct);

    private static Task<HttpResponseMessage> PeekAsync(RestClient user, string secret, string? alias = null) =>
        user.SendAsync(HttpMethod.Post, "/api/vault-invitations/peek", alias is null ? new { secret } : new { secret, alias });

    /// <summary>A request as <paramref name="user"/>'s session from <paramref name="address"/>, on <paramref name="api"/>.</summary>
    private static async Task<HttpResponseMessage> FromAsync(WebApplicationFactory<Program> api, RestClient user, string path, object body, string address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Cookie", $"{Kumbu.Server.Auth.CallerMiddleware.SessionCookie}={user.Session}");
        request.Headers.Add("Origin", RestClient.Origin);
        request.Headers.Add("X-Forwarded-For", address);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> AcceptAsync(RestClient user, string secret, string alias) =>
        user.SendAsync(HttpMethod.Post, "/api/vault-invitations/accept", new { secret, alias });

    /// <summary>The names of the vaults the user's session sees.</summary>
    private static async Task<string[]> VaultNamesAsync(RestClient user)
    {
        using var response = await user.GetAsync("/api/vaults");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await RestClient.JsonAsync(response)).AsArray().Select(v => v!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal)];
    }

    /// <summary>Whether the invitation is open as it was made: pending, nobody's, its lookup and its invite slot in place.</summary>
    private async Task AssertStillOpenAsync(CreatedInvite invite)
    {
        var doc = await Share.MembershipAsync(app.Database, invite.MembershipId);
        Assert.Equal(MembershipStatus.Pending, doc.Status);
        Assert.Null(doc.Member);
        Assert.Null(doc.RedeemedAt);
        Assert.NotNull(doc.InviteLookup);
        Assert.Equal(invite.ExpiresAt, doc.ExpiresAt);
        Assert.Equal(0, doc.Revision);
        Assert.Equal(1, await app.Database.GetCollection<SlotDocument>("slots").CountDocumentsAsync(s => s.Kind == SlotDocument.Invite && s.Membership == invite.MembershipId, cancellationToken: Ct));
        Assert.Equal(0, await app.Database.GetCollection<SlotDocument>("slots").CountDocumentsAsync(s => s.Kind == SlotDocument.Member && s.Membership == invite.MembershipId, cancellationToken: Ct));
    }

    /// <summary>A secret of the right shape that is nobody's invitation.</summary>
    private static string WrongSecret() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private Task<long> PendingAsync(string vaultId) => Docs.CountDocumentsAsync(m => m.Vault == vaultId && m.Status == MembershipStatus.Pending, cancellationToken: Ct);

    [Fact]
    public async Task Creating_an_invitation_returns_the_link_once_and_stores_no_secret()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");

        using var created = await InviteAsync(alice, body: new { password = RestClient.Password, label = "Bob", role = "maintain", expiresInDays = 3, confirm = true });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await RestClient.JsonAsync(created);
        var link = new Uri(json["link"]!.GetValue<string>());
        Assert.Equal(KumbuAppFactory.PublicUrl + "/share", link.GetLeftPart(UriPartial.Path));
        // The secret is in the fragment alone, which no browser sends.
        Assert.Empty(link.Query);
        var secret = link.Fragment[1..];
        Assert.Equal(43, secret.Length);
        Assert.Equal("maintain", json["role"]!.GetValue<string>());
        Assert.True(json["confirm"]!.GetValue<bool>());

        var vaultId = await alice.VaultIdAsync();
        var doc = await Docs.Find(m => m.Id == json["id"]!.GetValue<string>()).SingleAsync(Ct);
        Assert.Equal((MembershipStatus.Pending, vaultId, alice.User.Id, "maintain", true), (doc.Status, doc.Vault, doc.Owner, doc.Role, doc.Confirm));
        Assert.Null(doc.Member);
        Assert.Null(doc.Pin);
        Assert.Equal(doc.CreatedAt.AddDays(3), doc.ExpiresAt);
        // A keyed hash under the server secret: without it, the database alone checks no guess.
        var key = await Api.Services.GetRequiredService<ServerSecret>().KeyAsync("kumbu/v1/vault-invite", Ct);
        Assert.Equal(VaultCrypto.Mac(key, secret), doc.InviteLookup);
        Assert.NotEqual(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), doc.InviteLookup);

        var slot = await app.Database.GetCollection<SlotDocument>("slots").Find(s => s.Kind == SlotDocument.Invite && s.Membership == doc.Id).SingleAsync(Ct);
        Assert.Equal((vaultId, doc.ExpiresAt), (slot.Vault, slot.ExpiresAt));
        Assert.Null(slot.Lookup);
        Assert.Equal(await alice.VaultKeyAsync(), VaultCatalog.OpenInviteSlot(System.Buffers.Text.Base64Url.DecodeFromChars(secret), slot));

        // Nowhere in the database: not the secret, not its bytes.
        var raw = System.Buffers.Text.Base64Url.DecodeFromChars(secret);
        foreach (var name in await (await app.Database.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct))
        {
            foreach (var bson in await app.Database.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
            {
                var bytes = bson.ToBson();
                Assert.False(bytes.AsSpan().IndexOf(raw) >= 0, $"the secret's bytes are in {name}");
                Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0, $"the secret is in {name}");
            }
        }
    }

    [Fact]
    public async Task The_11th_pending_invitation_is_refused()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        for (var i = 0; i < Memberships.PendingLimit; i++) await InviteDirectAsync(alice);

        using var refused = await InviteAsync(alice);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("A vault can have at most 10 open invitations; withdraw one first.", await RestClient.ErrorAsync(refused));
        Assert.Equal(10, await PendingAsync(await alice.VaultIdAsync()));
    }

    [Fact]
    public async Task Concurrent_invitations_at_the_limit_admit_one()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        for (var i = 0; i < Memberships.PendingLimit - 1; i++) await InviteDirectAsync(alice);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try
            {
                await InviteDirectAsync(alice);
                return true;
            }
            catch (PendingLimitException)
            {
                return false;
            }
        }));

        Assert.Single(results, r => r);
        Assert.Equal(10, await PendingAsync(await alice.VaultIdAsync()));
    }

    [Fact]
    public async Task A_member_cannot_invite()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "garten", MemberRole.Maintain);

        using var refused = await InviteAsync(bob, "garten");

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal("You have no vault named 'garten'.", await RestClient.ErrorAsync(refused));
        Assert.Equal(0, await PendingAsync(await alice.VaultIdAsync()));
    }

    [Fact]
    public async Task An_invitation_needs_the_password()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");

        using var wrong = await InviteAsync(alice, body: new { password = "not the password", label = "Bob", role = "write" });
        using var unknown = await InviteAsync(alice, "Fremd", new { password = "not the password", label = "Bob", role = "write" });

        // As a vault's deletion: the wrong password, before anyone learns whether the vault exists.
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("The password is wrong.", await RestClient.ErrorAsync(wrong));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("The password is wrong.", await RestClient.ErrorAsync(unknown));
        Assert.Equal(0, await Docs.CountDocumentsAsync(m => m.Owner == alice.User.Id, cancellationToken: Ct));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(31, false)]
    [InlineData(30, true)]
    public async Task Expiry_beyond_30_days_is_refused(int days, bool allowed)
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");

        using var response = await InviteAsync(alice, body: new { password = RestClient.Password, label = "Bob", role = "read", expiresInDays = days });

        Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.BadRequest, response.StatusCode);
        if (!allowed) Assert.Equal("An invitation is valid for 1 to 30 days.", await RestClient.ErrorAsync(response));
        Assert.Equal(allowed ? 1 : 0, await PendingAsync(await alice.VaultIdAsync()));
    }

    [Fact]
    public async Task Peek_shows_vault_owner_role_and_provider_without_loading_the_vault()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == alice.User.Id, Builders<UserDocument>.Update.Set(u => u.Display, "Alice\u202e\nExample"), cancellationToken: Ct);
        var invite = await InviteDirectAsync(alice, confirm: true, role: MemberRole.Read);
        // Another process, which has never unlocked Alice's vault.
        await using var fresh = Api.WithWebHostBuilder(_ => { });
        var bob = await RestClient.NewAsync(fresh, vault: "Work");
        var catalog = fresh.Services.GetRequiredService<VaultCatalog>();
        var before = catalog.UnlockedCount;

        using var peek = await PeekAsync(bob, invite.Secret);

        Assert.Equal(HttpStatusCode.OK, peek.StatusCode);
        var json = await RestClient.JsonAsync(peek);
        Assert.Equal("Gartenarbeit", json["vault"]!.GetValue<string>());
        // Sanitised and quoted; the username beside it.
        Assert.Equal("\"AliceExample\"", json["owner"]!.GetValue<string>());
        Assert.Equal(alice.Username, json["ownerUsername"]!.GetValue<string>());
        Assert.Equal("read", json["role"]!.GetValue<string>());
        Assert.Equal(catalog.DefaultEmbeddingProvider, json["embeddingProvider"]!.GetValue<string>());
        Assert.True(json["confirm"]!.GetValue<bool>());
        Assert.Equal(before, catalog.UnlockedCount);
        // Nothing changed: the invitation is still open.
        Assert.Equal(MembershipStatus.Pending, (await Share.MembershipAsync(app.Database, invite.MembershipId)).Status);
    }

    [Fact]
    public async Task A_wrong_and_a_used_secret_answer_the_same_404()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var used = await InviteDirectAsync(alice);
        var expired = await InviteDirectAsync(alice);
        var withdrawn = await InviteDirectAsync(alice);
        // As a redemption leaves it, an expiry the TTL monitor hasn't seen yet, and a withdrawal.
        await Docs.UpdateOneAsync(
            m => m.Id == used.MembershipId,
            Builders<MembershipDocument>.Update.Set(m => m.Member, bob.User.Id).Set(m => m.Status, MembershipStatus.Active).Unset(m => m.InviteLookup).Unset(m => m.ExpiresAt),
            cancellationToken: Ct);
        await Docs.UpdateOneAsync(m => m.Id == expired.MembershipId, Builders<MembershipDocument>.Update.Set(m => m.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)), cancellationToken: Ct);
        await Docs.UpdateOneAsync(m => m.Id == withdrawn.MembershipId, Builders<MembershipDocument>.Update.Set(m => m.Status, MembershipStatus.Revoked), cancellationToken: Ct);

        var answers = new List<(HttpStatusCode, string)>();
        foreach (var secret in new[] { WrongSecret(), "not-a-secret", used.Secret, expired.Secret, withdrawn.Secret })
        {
            using var response = await PeekAsync(bob, secret);
            answers.Add((response.StatusCode, await response.Content.ReadAsStringAsync(Ct)));
        }

        Assert.All(answers, a => Assert.Equal((HttpStatusCode.NotFound, """{"error":"This invitation link is not valid (any more)."}"""), a));
    }

    [Fact]
    public async Task Peek_is_rate_limited_per_account_and_network_not_server_wide()
    {
        await using var api = Api.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<AccountLimits>();
            s.AddSingleton(new AccountLimits(AccountLimits.RedeemFailuresPerNetwork, AccountLimits.RedeemFailuresPerServer, invitePeeksPerAccount: 3, invitePeeksPerNetwork: 5));
        }));
        var users = new List<RestClient>();
        for (var i = 0; i < 4; i++) users.Add(await RestClient.NewAsync(api, vault: "Work"));
        var network = $"198.40.{Random.Shared.Next(256)}";
        var body = new { secret = WrongSecret() };

        // One account: 3 a minute, then refused from any network.
        for (var i = 0; i < 3; i++)
        {
            using var wrong = await FromAsync(api, users[0], "/api/vault-invitations/peek", body, $"{network}.{i + 1}");
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        }

        using (var account = await FromAsync(api, users[0], "/api/vault-invitations/peek", body, $"198.41.{Random.Shared.Next(256)}.1")) Assert.Equal(HttpStatusCode.TooManyRequests, account.StatusCode);

        // One network: 5 a minute, whoever asks.
        for (var i = 0; i < 2; i++)
        {
            using var wrong = await FromAsync(api, users[1], "/api/vault-invitations/peek", body, $"{network}.{i + 10}");
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        }

        using (var same = await FromAsync(api, users[2], "/api/vault-invitations/peek", body, $"{network}.20")) Assert.Equal(HttpStatusCode.TooManyRequests, same.StatusCode);
        // Nobody else is held off.
        using (var other = await FromAsync(api, users[3], "/api/vault-invitations/peek", body, $"198.42.{Random.Shared.Next(256)}.1")) Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        using (var otherAgain = await FromAsync(api, users[2], "/api/vault-invitations/peek", body, $"198.43.{Random.Shared.Next(256)}.1")) Assert.Equal(HttpStatusCode.NotFound, otherAgain.StatusCode);
    }

    [Fact]
    public async Task Peek_says_whether_the_alias_is_free()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        await carol.ShareWithAsync(bob, "Garten");
        var invite = await InviteDirectAsync(alice);

        async Task<(string Alias, bool Free)> AskAsync(string? alias)
        {
            using var response = await PeekAsync(bob, invite.Secret, alias);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await RestClient.JsonAsync(response);
            return (json["alias"]!.GetValue<string>(), json["aliasFree"]!.GetValue<bool>());
        }

        Assert.Equal(("Gartenarbeit", true), await AskAsync(null));
        Assert.Equal(("work", false), await AskAsync("work"));
        Assert.Equal(("garten", false), await AskAsync(" garten "));
        Assert.Equal(("Beete", true), await AskAsync("Beete"));
    }

    [Fact]
    public async Task Peek_is_refused_while_the_owner_is_inactive_or_closing()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var invite = await InviteDirectAsync(alice);
        var users = app.Database.GetCollection<UserDocument>("users");
        var vaults = app.Database.GetCollection<VaultDocument>("vaults");
        var vaultId = await alice.VaultIdAsync();

        await users.UpdateOneAsync(u => u.Id == alice.User.Id, Builders<UserDocument>.Update.Set(u => u.Active, false), cancellationToken: Ct);
        using (var inactive = await PeekAsync(bob, invite.Secret))
        {
            Assert.Equal(HttpStatusCode.Conflict, inactive.StatusCode);
            Assert.Equal("owner-inactive", (await RestClient.JsonAsync(inactive))["code"]!.GetValue<string>());
        }

        await users.UpdateOneAsync(u => u.Id == alice.User.Id, Builders<UserDocument>.Update.Set(u => u.Active, true), cancellationToken: Ct);
        await vaults.UpdateOneAsync(v => v.Id == vaultId, Builders<VaultDocument>.Update.Set(v => v.ClosingAt, DateTime.UtcNow.AddDays(7)), cancellationToken: Ct);
        using (var closing = await PeekAsync(bob, invite.Secret))
        {
            Assert.Equal(HttpStatusCode.Conflict, closing.StatusCode);
            Assert.Equal("closing", (await RestClient.JsonAsync(closing))["code"]!.GetValue<string>());
        }

        await vaults.UpdateOneAsync(v => v.Id == vaultId, Builders<VaultDocument>.Update.Unset(v => v.ClosingAt), cancellationToken: Ct);
        using var open = await PeekAsync(bob, invite.Secret);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
    }

    [Fact]
    public async Task Accepting_makes_Bob_a_member_under_his_alias()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "# Beete\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var invite = await InviteDirectAsync(alice);
        var vaultId = await alice.VaultIdAsync();
        var vaults = app.Database.GetCollection<VaultDocument>("vaults");
        var changed = (await vaults.Find(v => v.Id == vaultId).SingleAsync(Ct)).MembershipsChanged;

        using var accepted = await AcceptAsync(bob, invite.Secret, "Garten");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var json = await RestClient.JsonAsync(accepted);
        Assert.Equal((invite.MembershipId, "Garten", "write", "active"), (json["id"]!.GetValue<string>(), json["vault"]!.GetValue<string>(), json["role"]!.GetValue<string>(), json["status"]!.GetValue<string>()));
        Assert.Equal(["Garten", "Skills", "Work"], await VaultNamesAsync(bob));
        using (var note = await bob.GetAsync("/api/notes/Garten/plan.md")) Assert.Equal(HttpStatusCode.OK, note.StatusCode);

        var doc = await Share.MembershipAsync(app.Database, invite.MembershipId);
        Assert.Equal((MembershipStatus.Active, bob.User.Id), (doc.Status, doc.Member));
        Assert.NotNull(doc.AcceptedAt);
        Assert.NotNull(doc.RedeemedAt);
        Assert.Null(doc.InviteLookup);
        Assert.Null(doc.ExpiresAt);
        Assert.Equal(Api.Services.GetRequiredService<VaultCatalog>().DefaultEmbeddingProvider, doc.ProviderSeen);
        // The pin names the key his private key is the key of.
        var user = await app.Database.GetCollection<UserDocument>("users").Find(u => u.Id == bob.User.Id).SingleAsync(Ct);
        Assert.Equal(user.PublicKey, Memberships.PinnedKey(doc, await alice.VaultKeyAsync()));
        Assert.Equal(1, user.MembershipsAccepted);
        Assert.Equal(changed + 1, (await vaults.Find(v => v.Id == vaultId).SingleAsync(Ct)).MembershipsChanged);
        var slots = app.Database.GetCollection<SlotDocument>("slots");
        Assert.Equal(0, await slots.CountDocumentsAsync(s => s.Kind == SlotDocument.Invite && s.Membership == invite.MembershipId, cancellationToken: Ct));
        Assert.Equal(1, await slots.CountDocumentsAsync(s => s.Kind == SlotDocument.Member && s.Membership == invite.MembershipId, cancellationToken: Ct));

        // Used up: a second acceptance finds nothing.
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        using var again = await AcceptAsync(carol, invite.Secret, "Garten");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Peek_at_a_renamed_vault_shows_its_shown_name()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "# Beete\n");
        // Taken before the rename: RestClient.Vault stays Gartenarbeit.
        var vaultId = await alice.VaultIdAsync();
        var before = await InviteDirectAsync(alice);
        using (var renamed = await alice.SendAsync(HttpMethod.Put, "/api/vaults/Gartenarbeit/name", new { name = "Garten" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        var after = await Memberships.InviteAsync(alice.User.Id, alice.UserKey, vaultId, "Carol", MemberRole.Write, TimeSpan.FromDays(7), false, Ct);
        var bob = await RestClient.NewAsync(Api, vault: "Work");

        string? proposed = null;
        foreach (var invite in new[] { before, after })
        {
            using var peek = await PeekAsync(bob, invite.Secret);
            Assert.Equal(HttpStatusCode.OK, peek.StatusCode);
            var json = await RestClient.JsonAsync(peek);
            Assert.Equal(("Garten", "Garten", true), (json["vault"]!.GetValue<string>(), json["alias"]!.GetValue<string>(), json["aliasFree"]!.GetValue<bool>()));
            proposed = json["alias"]!.GetValue<string>();
        }

        using var accepted = await AcceptAsync(bob, before.Secret, proposed!);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Garten", (await RestClient.JsonAsync(accepted))["vault"]!.GetValue<string>());
        Assert.Equal(["Garten", "Skills", "Work"], await VaultNamesAsync(bob));
        using var note = await bob.GetAsync("/api/notes/Garten/plan.md");
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        Assert.Equal("/Garten/plan.md", (await RestClient.JsonAsync(note))["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_taken_alias_keeps_the_link_usable()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var invite = await InviteDirectAsync(alice);

        using var taken = await AcceptAsync(bob, invite.Secret, "work");

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        await AssertStillOpenAsync(invite);
        Assert.Equal(0, (await app.Database.GetCollection<UserDocument>("users").Find(u => u.Id == bob.User.Id).SingleAsync(Ct)).MembershipsAccepted);
        using var other = await AcceptAsync(bob, invite.Secret, "Garten");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(["Garten", "Skills", "Work"], await VaultNamesAsync(bob));
    }

    [Fact]
    public async Task Two_concurrent_accepts_admit_one()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var takers = new List<RestClient>();
        for (var i = 0; i < 4; i++) takers.Add(await RestClient.NewAsync(Api, vault: "Work"));
        var invite = await InviteDirectAsync(alice);

        var results = await Task.WhenAll(takers.Select(async t =>
        {
            try
            {
                await Memberships.AcceptAsync(t.User.Id, t.UserKey, invite.Secret, "Garten", Ct);
                return true;
            }
            catch (InvitationNotFoundException)
            {
                return false;
            }
        }));

        Assert.Single(results, r => r);
        var doc = await Share.MembershipAsync(app.Database, invite.MembershipId);
        Assert.Equal(takers[Array.IndexOf(results, true)].User.Id, doc.Member);
        Assert.Equal(1, await Docs.CountDocumentsAsync(m => m.Vault == doc.Vault && m.Member != null, cancellationToken: Ct));
        Assert.Equal(1, await app.Database.GetCollection<SlotDocument>("slots").CountDocumentsAsync(s => s.Kind == SlotDocument.Member && s.Membership == invite.MembershipId, cancellationToken: Ct));
    }

    [Fact]
    public async Task Alice_cannot_accept_her_own_invitation()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var invite = await InviteDirectAsync(alice);

        using var own = await AcceptAsync(alice, invite.Secret, "Garten");

        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        await AssertStillOpenAsync(invite);
    }

    [Fact]
    public async Task Bob_cannot_hold_two_memberships_of_one_vault()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "Garten");
        var invite = await InviteDirectAsync(alice);

        using var second = await AcceptAsync(bob, invite.Secret, "Beete");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("This person is already a member of the vault.", await RestClient.ErrorAsync(second));
        await AssertStillOpenAsync(invite);
        Assert.Equal(["Garten", "Skills", "Work"], await VaultNamesAsync(bob));
    }

    [Fact]
    public async Task An_expired_invitation_is_refused_even_before_the_TTL_monitor()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var api = Api.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(time);
        }));
        var alice = await RestClient.NewAsync(api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(api, vault: "Work");
        var invite = await api.Services.GetRequiredService<Memberships>().InviteAsync(
            alice.User.Id, alice.UserKey, await alice.VaultIdAsync(), "Bob", MemberRole.Write, TimeSpan.FromDays(1), confirm: false, Ct);

        time.Advance(TimeSpan.FromDays(1));
        bob = await bob.SignInAgainAsync();
        using var expired = await AcceptAsync(bob, invite.Secret, "Garten");

        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(["Skills", "Work"], await VaultNamesAsync(bob));
        Assert.Null((await Share.MembershipAsync(app.Database, invite.MembershipId)).Member);

        // The membership's own expiry decides, whatever its invite slot says.
        var later = await api.Services.GetRequiredService<Memberships>().InviteAsync(
            alice.User.Id, alice.UserKey, await alice.VaultIdAsync(), "Bob", MemberRole.Write, TimeSpan.FromDays(1), confirm: false, Ct);
        await Docs.UpdateOneAsync(m => m.Id == later.MembershipId, Builders<MembershipDocument>.Update.Set(m => m.ExpiresAt, time.GetUtcNow().UtcDateTime), cancellationToken: Ct);
        using var lapsed = await AcceptAsync(bob, later.Secret, "Garten");
        Assert.Equal(HttpStatusCode.NotFound, lapsed.StatusCode);
        Assert.Null((await Share.MembershipAsync(app.Database, later.MembershipId)).Member);
    }

    [Fact]
    public async Task With_confirm_the_membership_waits_for_the_owner()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var invite = await InviteDirectAsync(alice, confirm: true);

        using var accepted = await AcceptAsync(bob, invite.Secret, "Garten");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("pending", (await RestClient.JsonAsync(accepted))["status"]!.GetValue<string>());
        var doc = await Share.MembershipAsync(app.Database, invite.MembershipId);
        Assert.Equal((MembershipStatus.Pending, bob.User.Id), (doc.Status, doc.Member));
        Assert.NotNull(doc.RedeemedAt);
        Assert.Null(doc.AcceptedAt);
        Assert.Null(doc.InviteLookup);
        Assert.NotNull(doc.Pin);
        // Nothing opens until the owner confirms.
        Assert.Equal(["Skills", "Work"], await VaultNamesAsync(bob));
        // It counts towards the ten pending invitations.
        for (var i = 0; i < Memberships.PendingLimit - 1; i++) await InviteDirectAsync(alice);
        await Assert.ThrowsAsync<PendingLimitException>(() => InviteDirectAsync(alice));
    }

    [Fact]
    public async Task Accept_racing_Bobs_account_deletion_conflicts()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var invite = await InviteDirectAsync(alice);
        var memberships = Memberships;
        var deleted = false;
        memberships.FaultAt = async point =>
        {
            if (point != "accept-before-user" || deleted) return;
            deleted = true;
            // Bob's account goes while the acceptance is under way, in a transaction of its own.
            Assert.True(await Api.Services.GetRequiredService<Deletion>().DeleteOwnAccountAsync(bob.User, bob.UserKey, RestClient.Password, bob.Username, Ct));
        };
        try
        {
            await Assert.ThrowsAsync<CredentialsChangedException>(() => memberships.AcceptAsync(bob.User.Id, bob.UserKey, invite.Secret, "Garten", Ct));
        }
        finally
        {
            memberships.FaultAt = null;
        }

        Assert.True(deleted);
        Assert.Equal(0, await Docs.CountDocumentsAsync(m => m.Member == bob.User.Id, cancellationToken: Ct));
        await AssertStillOpenAsync(invite);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("x12345678901234567890123456789012345678901234567890123456789012345")]
    public async Task An_alias_follows_the_rule_of_a_vault_name(string name)
    {
        var bob = await RestClient.NewAsync(Api, vault: "Work");

        using var vault = await bob.SendAsync(HttpMethod.Post, "/api/vaults", new { name });
        using var alias = await AcceptAsync(bob, Base64UrlSecret(), name);

        // One name index for both (collab design §2.3): one rule, one answer.
        Assert.Equal(HttpStatusCode.BadRequest, vault.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, alias.StatusCode);
        Assert.Equal(await RestClient.ErrorAsync(vault), await RestClient.ErrorAsync(alias));
    }

    private static string Base64UrlSecret() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
