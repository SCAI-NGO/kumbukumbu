// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
}
