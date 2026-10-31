// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Sync;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.OAuth;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// HARD RULE 1: a member reaches a shared vault only while the membership is active, checked on every request before
/// the vault is loaded or taken from memory (collab design §3.1): (a) her sessions, (b) her API keys, connections and
/// sync tickets, whose role also caps them. Never weakened for another test. In the app's collection, for the API
/// tests of (b); the catalog-level tests of (a) use a database of their own beside the app's.
/// </summary>
[Collection("app")]
public sealed class MembershipCheckTests(KumbuAppFactory app)
{
    private const string Callback = "https://claude.test/cb";

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly (string Id, byte[] Key) Alice = ("u1", VaultCrypto.NewKey());
    private static readonly (string Id, byte[] Key) Bob = ("u2", VaultCrypto.NewKey());
    private static readonly (string Id, byte[] Key) Carol = ("user-lea", VaultCrypto.NewKey());

    private readonly VaultWorld _world = new(app.NewDatabase());

    /// <summary>Alice's "Gartenarbeit" in <paramref name="catalog"/>, shared with Bob; its id and the membership's.</summary>
    private async Task<(string VaultId, string MembershipId)> SharedAsync(VaultCatalog catalog)
    {
        var vault = await catalog.CreateForUserAsync("Gartenarbeit", Alice.Id, Alice.Key, Ct);
        var membership = await Share.GrantAsync(catalog, _world.Memberships(catalog), Alice, vault.Id, Bob, "Garten", MemberRole.Write);
        return (vault.Id, membership);
    }

    private static async Task<IReadOnlyList<string>> OpenedAsync(VaultCatalog catalog, (string Id, byte[] Key) user) =>
        [.. (await catalog.OpenForUserAsync(user.Id, user.Key, Ct)).Select(v => v.Id)];

    [Fact]
    public async Task A_member_session_opens_the_shared_vault()
    {
        var (vaultId, _) = await SharedAsync(_world.Catalog());
        var own = await _world.Catalog().CreateForUserAsync("Work", Bob.Id, Bob.Key, Ct);

        // A second process, with nothing in memory.
        Assert.Equal(new[] { own.Id, vaultId }.Order(), (await OpenedAsync(_world.Catalog(), Bob)).Order());
    }

    [Fact]
    public async Task A_revoked_membership_refuses_an_already_unlocked_vault_on_the_next_request()
    {
        var catalog = _world.Catalog();
        var (vaultId, membershipId) = await SharedAsync(catalog);
        Assert.Equal([vaultId], await OpenedAsync(catalog, Bob));

        await Share.SetStatusAsync(_world.Db, membershipId, MembershipStatus.Revoked);

        // The vault is still unlocked in this process: only the membership check keeps Bob out.
        Assert.Equal(1, catalog.OpenCount);
        Assert.Empty(await OpenedAsync(catalog, Bob));
        Assert.Equal([vaultId], await OpenedAsync(catalog, Alice));
        Assert.Empty(await OpenedAsync(catalog, Bob));
    }

    [Theory]
    [InlineData(MembershipStatus.Left)]
    [InlineData(MembershipStatus.Pending)]
    [InlineData(MembershipStatus.Revoked)]
    public async Task A_left_or_pending_membership_opens_nothing(string status)
    {
        var catalog = _world.Catalog();
        var (vaultId, membershipId) = await SharedAsync(catalog);
        await Share.SetStatusAsync(_world.Db, membershipId, status);

        Assert.Empty(await OpenedAsync(_world.Catalog(), Bob));
        // Nor with the vault in memory, opened by its owner.
        Assert.Equal([vaultId], await OpenedAsync(catalog, Alice));
        Assert.Empty(await OpenedAsync(catalog, Bob));
    }

    [Fact]
    public async Task A_member_slot_without_a_membership_opens_nothing()
    {
        var catalog = _world.Catalog();
        var garden = await catalog.CreateForUserAsync("Gartenarbeit", Alice.Id, Alice.Key, Ct);
        var work = await catalog.CreateForUserAsync("Work", Alice.Id, Alice.Key, Ct);
        var carols = await Share.GrantAsync(catalog, _world.Memberships(catalog), Alice, garden.Id, Carol, "Garten", MemberRole.Write);
        var vaultKey = await catalog.OwnerVaultKeyAsync(Alice.Id, Alice.Key, garden.Id, Ct);
        var workKey = await catalog.OwnerVaultKeyAsync(Alice.Id, Alice.Key, work.Id, Ct);

        // Inserted by someone with access to the database, wrapped properly for Bob: one without any membership, one
        // pointing at Carol's active membership, one at Carol's membership but on another vault of Alice's.
        await catalog.Slots.InsertManyAsync(
            [
                catalog.MemberSlot(garden.Id, "no-such-membership", Bob.Id, Bob.Key, vaultKey, "Garten"),
                catalog.MemberSlot(garden.Id, carols, Bob.Id, Bob.Key, vaultKey, "Garten 2"),
                catalog.MemberSlot(work.Id, carols, Bob.Id, Bob.Key, workKey, "Work"),
            ],
            cancellationToken: Ct);

        Assert.Empty(await OpenedAsync(_world.Catalog(), Bob));
        Assert.Equal(new[] { garden.Id, work.Id }.Order(), (await OpenedAsync(catalog, Alice)).Order());
        Assert.Empty(await OpenedAsync(catalog, Bob));
        Assert.Equal([garden.Id], await OpenedAsync(catalog, Carol));

        // Bob's own membership opens the vault it is of, and no other.
        var bobs = await Share.GrantAsync(catalog, _world.Memberships(catalog), Alice, garden.Id, Bob, "Mein Garten", MemberRole.Write);
        await catalog.Slots.InsertOneAsync(catalog.MemberSlot(work.Id, bobs, Bob.Id, Bob.Key, workKey, "Work 2"), cancellationToken: Ct);
        Assert.Equal([garden.Id], await OpenedAsync(catalog, Bob));
        Assert.Equal([garden.Id], await OpenedAsync(_world.Catalog(), Bob));
    }

    [Fact]
    public async Task One_membership_query_per_request()
    {
        var catalog = _world.Catalog();
        var memberships = _world.Memberships(catalog);
        foreach (var name in new[] { "Gartenarbeit", "Work" })
        {
            var vault = await catalog.CreateForUserAsync(name, Alice.Id, Alice.Key, Ct);
            await Share.GrantAsync(catalog, memberships, Alice, vault.Id, Bob, name, MemberRole.Read);
        }

        await _world.Db.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 2), cancellationToken: Ct);
        try
        {
            Assert.Equal(2, (await OpenedAsync(catalog, Bob)).Count);
            Assert.Equal(1, await FindsOnMembershipsAsync());

            // Requests of an owner without shared vaults (Alice is one: her slots are user slots) pay nothing.
            Assert.Equal(2, (await OpenedAsync(catalog, Alice)).Count);
            Assert.Equal(1, await FindsOnMembershipsAsync());
        }
        finally
        {
            await _world.Db.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0), cancellationToken: Ct);
        }
    }

    private Task<long> FindsOnMembershipsAsync() =>
        _world.Db.GetCollection<BsonDocument>("system.profile").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("ns", $"{_world.Db.DatabaseNamespace.DatabaseName}.{Memberships.CollectionName}")
            & Builders<BsonDocument>.Filter.Eq("op", "query"),
            cancellationToken: Ct);

    // HARD RULE 1 (b): keys, connections and sync tickets.

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>
    /// Alice's "Gartenarbeit" with a note, shared with Bob, who has his own "Work" with a note. Bob's alias is the
    /// vault's own name, so these tests read the same paths whatever the per-caller view does (AliasTests pins that).
    /// </summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId, string MembershipId)> SharedOverApiAsync(MemberRole role = MemberRole.Write)
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("a.md", "Kompost");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await bob.CreateAsync("b.md", "Bobs");
        var membership = await alice.ShareWithAsync(bob, "Gartenarbeit", role);
        return (alice, bob, await alice.VaultIdAsync(), membership);
    }

    /// <summary>Bob's key on his own vault first and the shared one second, so that a check of only the first slot misses.</summary>
    private async Task<string> MemberKeyAsync(RestClient bob, string sharedId, KeyRight right = KeyRight.Write)
    {
        var (key, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [await bob.VaultIdAsync()] = right, [sharedId] = right }, Ct);
        return key;
    }

    [Fact]
    public async Task A_revoked_membership_refuses_a_live_API_key_on_the_next_request()
    {
        var (_, bob, vaultId, membershipId) = await SharedOverApiAsync();
        var key = await MemberKeyAsync(bob, vaultId);
        var (failed, text) = await bob.ShAsync("cat /Gartenarbeit/a.md", key);
        Assert.False(failed, text);
        Assert.Contains("Kompost", text, StringComparison.Ordinal);

        await Share.SetStatusAsync(app.Database, membershipId, MembershipStatus.Revoked);

        // The vault is still unlocked in the server: only the membership check keeps the key out.
        Assert.Contains(Api.Services.GetRequiredService<VaultCatalog>().UnlockedVaults, v => v.Id == vaultId);
        (failed, text) = await bob.ShAsync("cat /Gartenarbeit/a.md", key);
        Assert.True(failed);
        Assert.DoesNotContain("Kompost", text, StringComparison.Ordinal);
        var (_, listing) = await bob.ShAsync("ls /", key);
        Assert.DoesNotContain("Gartenarbeit", listing, StringComparison.Ordinal);
        Assert.Contains("Work", listing, StringComparison.Ordinal);
        (failed, text) = await bob.ShAsync("cat /Work/b.md", key);
        Assert.False(failed, text);
        Assert.Contains("Bobs", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revoked_membership_refuses_a_live_OAuth_token()
    {
        var (_, bob, vaultId, membershipId) = await SharedOverApiAsync();
        var connection = await Api.Services.GetRequiredService<Connections>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct);
        var flow = await OAuthFlow.StartAsync(Api, bob, connection.Id, await OAuthFlow.RegisterAsync(Api, Callback), Callback);
        await flow.AllowAsync();
        using var exchanged = await flow.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        var access = (await RestClient.JsonAsync(exchanged))["access_token"]!.GetValue<string>();
        using (var before = await InitializeAsync($"/mcp/c/{connection.Id}", access)) Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await Share.SetStatusAsync(app.Database, membershipId, MembershipStatus.Revoked);

        // The token's only vault is still in memory; the token opens nothing, so it is refused.
        Assert.Contains(Api.Services.GetRequiredService<VaultCatalog>().UnlockedVaults, v => v.Id == vaultId);
        using var after = await InitializeAsync($"/mcp/c/{connection.Id}", access);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task A_lowered_role_caps_a_live_key_on_the_next_request()
    {
        var (_, bob, vaultId, membershipId) = await SharedOverApiAsync(MemberRole.Write);
        var key = await MemberKeyAsync(bob, vaultId, KeyRight.Maintain);
        using (var written = await bob.SendAsync(HttpMethod.Put, "/api/notes/Gartenarbeit/c.md", new { content = "Beet" }, bearer: key))
        {
            Assert.Equal(HttpStatusCode.Created, written.StatusCode);
        }

        await Share.SetRoleAsync(app.Database, membershipId, MemberRole.Read);

        using var refused = await bob.SendAsync(HttpMethod.Put, "/api/notes/Gartenarbeit/d.md", new { content = "Beet" }, bearer: key);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("read-only", (await RestClient.JsonAsync(refused))["code"]!.GetValue<string>());
        // His own vault is his key's to write, whatever his role elsewhere.
        using var own = await bob.SendAsync(HttpMethod.Put, "/api/notes/Work/c.md", new { content = "Beet" }, bearer: key);
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
    }

    [Fact]
    public async Task A_ws_ticket_for_a_revoked_membership_lists_no_shared_vault()
    {
        var (_, bob, vaultId, membershipId) = await SharedOverApiAsync();
        var key = await MemberKeyAsync(bob, vaultId);
        var own = await bob.VaultIdAsync();
        Assert.Equal(new[] { own, vaultId }.Order(), (await TicketVaultsAsync(bob, key)).Order());

        await Share.SetStatusAsync(app.Database, membershipId, MembershipStatus.Revoked);

        Assert.Equal([own], await TicketVaultsAsync(bob, key));
    }

    [Fact]
    public async Task A_derived_slot_whose_grant_was_changed_in_the_database_opens_nothing()
    {
        var (alice, bob, vaultId, membershipId) = await SharedOverApiAsync();
        var key = await MemberKeyAsync(bob, vaultId);
        // Carol's active membership of the same vault, to borrow.
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        var carols = await alice.ShareWithAsync(carol, "Gartenarbeit");
        await Share.SetStatusAsync(app.Database, membershipId, MembershipStatus.Revoked);
        var slots = app.Database.GetCollection<SlotDocument>("slots");

        // Without its grant, the slot would look like an owner's; pointed at Carol's, it would borrow hers.
        foreach (var update in new[] { Builders<SlotDocument>.Update.Unset(s => s.GrantedVia), Builders<SlotDocument>.Update.Set(s => s.GrantedVia, carols) })
        {
            await slots.UpdateOneAsync(s => s.Vault == vaultId && s.Kind == SlotDocument.ApiKey, update, cancellationToken: Ct);
            var (_, listing) = await bob.ShAsync("ls /", key);
            Assert.DoesNotContain("Gartenarbeit", listing, StringComparison.Ordinal);
        }
    }

    private async Task<IReadOnlyList<string>> TicketVaultsAsync(RestClient bob, string key)
    {
        using var response = await bob.SendAsync(HttpMethod.Post, "/api/ws-ticket", bearer: key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = (await RestClient.JsonAsync(response))["ticket"]!.GetValue<string>();
        return [.. Api.Services.GetRequiredService<SyncTickets>().Take(ticket)!.Vaults.Select(v => v.VaultId)];
    }

    /// <summary>An MCP <c>initialize</c> on <paramref name="path"/> with <paramref name="bearer"/>, as a raw POST.</summary>
    private async Task<HttpResponseMessage> InitializeAsync(string path, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Forwarded-For", $"198.25.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        return await client.SendAsync(request, Ct);
    }
}
