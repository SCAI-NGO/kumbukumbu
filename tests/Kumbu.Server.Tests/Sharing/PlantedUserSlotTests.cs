// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Kumbu.Core.Crypto;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// What a known limit reaches (security architecture §4, "Database write access"): someone who kept a vault's key and can
/// write the database writes a <c>user</c> slot for his own account that wraps it. These tests pin down today's behaviour,
/// so that the document stays true and a fix shows up here. They don't say the behaviour is wanted.
/// </summary>
[Collection("app")]
public sealed class PlantedUserSlotTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    /// <summary>Alice's "Gartenarbeit", shared with Carol as "garten", and Bob, who has a slot of his own on it.</summary>
    private async Task<(RestClient Alice, RestClient Bob, RestClient Carol, string CarolsMembership)> PlantedAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "# Beete\n");
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        var carols = await alice.ShareWithAsync(carol, "garten");
        var bob = await RestClient.NewAsync(Api, vault: "Werkstatt");

        // The labels and contexts are spelt out: he needs only his own user key and the vault key, no secret of the server.
        var vaultId = await alice.VaultIdAsync();
        var kek = VaultCrypto.Derive(bob.UserKey, "kumbu/v1/user-slot-kek", Encoding.UTF8.GetBytes(vaultId));
        var keptKey = await alice.VaultKeyAsync();
        await Catalog.Slots.InsertOneAsync(
            new SlotDocument
            {
                Vault = vaultId,
                Kind = SlotDocument.User,
                Lookup = VaultCatalog.UserSlotLookup(bob.UserKey, bob.User.Id),
                Owner = bob.User.Id,
                NameId = VaultCatalog.AliasNameId(bob.UserKey, "Gartenarbeit"),
                Wrapped = VaultCrypto.Seal(kek, keptKey, $"slot:{vaultId}:{SlotDocument.User}"),
                CreatedAt = DateTime.UtcNow,
            },
            cancellationToken: Ct);
        return (alice, bob, carol, carols);
    }

    private Task<bool> StartedAsync(string vaultId) =>
        app.Database.GetCollection<BsonDocument>("vaults").Find(new BsonDocument { ["_id"] = vaultId, ["rekeyStartedAt"] = new BsonDocument("$exists", true) }).AnyAsync(Ct);

    [Fact]
    public async Task The_vault_is_listed_as_his_own_and_opens()
    {
        var (_, bob, _, _) = await PlantedAsync();

        using var vaults = await bob.GetAsync("/api/vaults");
        Assert.Equal(HttpStatusCode.OK, vaults.StatusCode);
        var planted = (await RestClient.JsonAsync(vaults)).AsArray().Single(v => v!["name"]!.GetValue<string>() == "Gartenarbeit")!;
        Assert.Equal("owner", planted["level"]!.GetValue<string>());

        using var read = await bob.GetAsync("/api/notes/Gartenarbeit/plan.md");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("# Beete\n", (await RestClient.JsonAsync(read))["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("access", HttpStatusCode.OK)]
    [InlineData("invite", HttpStatusCode.Created)]
    [InlineData("role", HttpStatusCode.OK)]
    [InlineData("revoke", HttpStatusCode.NoContent)]
    [InlineData("rekey", HttpStatusCode.OK)]
    public async Task The_owners_routes_answer_him(string route, HttpStatusCode expected)
    {
        var (_, bob, _, carols) = await PlantedAsync();

        using var response = route switch
        {
            "access" => await bob.GetAsync("/api/vaults/Gartenarbeit/access"),
            "invite" => await bob.SendAsync(
                HttpMethod.Post, "/api/vaults/Gartenarbeit/invitations", new { password = RestClient.Password, label = "Dave", role = "read", expiresInDays = 7, confirm = false }),
            "role" => await bob.SendAsync(HttpMethod.Put, $"/api/vaults/Gartenarbeit/memberships/{carols}/role", new { role = "read" }),
            "revoke" => await bob.SendAsync(HttpMethod.Post, $"/api/vaults/Gartenarbeit/memberships/{carols}/revoke"),
            _ => await bob.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/rekey", new { password = RestClient.Password }),
        };

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task His_re_encryption_locks_the_owner_out()
    {
        var (alice, bob, _, _) = await PlantedAsync();
        var f = Builders<SlotDocument>.Filter;
        var hersFilter = f.Eq(s => s.Kind, SlotDocument.User) & f.Eq(s => s.Lookup, VaultCatalog.UserSlotLookup(alice.UserKey, alice.User.Id));
        var before = await Catalog.Slots.Find(hersFilter).SingleAsync(Ct);

        using (var rekeyed = await bob.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/rekey", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
        }

        // The run moves only the slot of the user who started it, so hers still wraps the old key.
        var after = await Catalog.Slots.Find(hersFilter).SingleAsync(Ct);
        Assert.Equal(before.Wrapped, after.Wrapped);
        using var hers = await alice.GetAsync("/api/notes/Gartenarbeit/plan.md");
        Assert.NotEqual(HttpStatusCode.OK, hers.StatusCode);
        using var his = await bob.GetAsync("/api/notes/Gartenarbeit/plan.md");
        Assert.Equal(HttpStatusCode.OK, his.StatusCode);
    }

    [Fact]
    public async Task The_owners_re_encryption_ends_it()
    {
        var (alice, bob, _, _) = await PlantedAsync();
        var oldKey = await alice.VaultKeyAsync();

        using (var rekeyed = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/rekey", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
        }

        Assert.NotEqual(oldKey, await alice.VaultKeyAsync());
        Assert.False(await StartedAsync(await alice.VaultIdAsync()));
        Assert.Equal("# Beete\n", (await alice.ReadAsync("plan.md"))["content"]!.GetValue<string>());
        using var his = await bob.GetAsync("/api/notes/Gartenarbeit/plan.md");
        Assert.NotEqual(HttpStatusCode.OK, his.StatusCode);
    }
}
