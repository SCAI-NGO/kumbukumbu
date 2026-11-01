// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// The owner's account deactivated (collab design §7.4): her shared vault is read-only for every member, on every route,
/// with <c>code: owner-inactive</c>; members may export it; nothing more of it is indexed; her reactivation lifts it on
/// the next request.
/// </summary>
[Collection("app")]
public sealed class OwnerInactiveTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>The public instance's index (not the factory's own, another host).</summary>
    private Task IndexedAsync() => Api.Services.GetRequiredService<Kumbu.Server.Search.SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

    private sealed record World(RestClient Alice, RestClient Bob, string VaultId, string Key, string Plan);

    /// <summary>Alice ("Alice\nExample") shares "Gartenarbeit" (plan.md) with Bob as "garten", to write; Bob's key on it
    /// sealed write; then an admin deactivates Alice.</summary>
    private async Task<World> DeactivatedAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var plan = await alice.CreateAsync("plan.md", "Beete planen\n");
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == alice.User.Id, Builders<UserDocument>.Update.Set(u => u.Display, "Alice\nExample"), cancellationToken: Ct);
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "garten", MemberRole.Write);
        var vaultId = await alice.VaultIdAsync();
        var key = (await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct)).Key;
        // Bob's key and session work on it before.
        Assert.False((await bob.ShAsync("ls /garten", key)).IsError);
        await SetActiveAsync(alice, false);
        return new World(alice, bob, vaultId, key, plan);
    }

    private async Task SetActiveAsync(RestClient user, bool active)
    {
        var admin = await RestClient.NewAsync(Api, vault: "Admin");
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == admin.User.Id, Builders<UserDocument>.Update.Set(u => u.Role, Roles.Admin), cancellationToken: Ct);
        Assert.True(await Api.Services.GetRequiredService<Admin>().SetActiveAsync(admin.User, user.User.Id, active, Ct));
    }

    private static async Task<JsonNode> GartenAsync(RestClient bob)
    {
        using var response = await bob.GetAsync("/api/vaults");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response)).AsArray().Single(v => v!["name"]!.GetValue<string>() == "garten")!;
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await RestClient.JsonAsync(response);
        Assert.Equal(Refusals.OwnerInactive, body["code"]?.GetValue<string>());
        Assert.Contains("read-only while its owner's account is deactivated", body["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PUT note")]
    [InlineData("DELETE note")]
    [InlineData("upload by key")]
    [InlineData("MCP write")]
    [InlineData("store")]
    public async Task A_deactivated_owners_shared_vault_is_read_only_for_members_on_every_route(string route)
    {
        var w = await DeactivatedAsync();
        var bob = w.Bob;

        switch (route)
        {
            case "PUT note":
                using (var r = await bob.SendAsync(HttpMethod.Put, "/api/notes/garten/plan.md", new { content = "neu\n", version = w.Plan })) await AssertRefusedAsync(r);
                break;
            case "DELETE note":
                using (var r = await bob.SendAsync(HttpMethod.Delete, "/api/notes/garten/plan.md")) await AssertRefusedAsync(r);
                break;
            case "upload by key":
                using (var r = await bob.SendAsync(HttpMethod.Post, "/api/upload/garten/In", new { onConflict = "replace", files = new[] { new { path = "neu.md", content = "neu\n" } } }, bearer: w.Key))
                {
                    await AssertRefusedAsync(r);
                }

                break;
            case "MCP write":
                var (failed, text) = await bob.ShAsync("mv /garten/plan.md /garten/neu.md", w.Key);
                Assert.True(failed);
                Assert.Equal("mv: vault garten is read-only while its owner's account is deactivated", text);
                break;
            case "store":
                // A write path that skips the caller's check still meets the store's (VaultCatalog.CheckWritable).
                var store = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(bob.User.Id, bob.UserKey));
                var writer = new NoteWriter(store, TimeProvider.System, new FixedCaller("Bob"));
                var refused = await Assert.ThrowsAsync<VaultReadOnlyException>(() => writer.WriteAsync("/garten/neu.md", "neu\n", null, Ct));
                Assert.Equal(Refusals.OwnerInactive, refused.Code);
                break;
        }

        // Nothing was written, and reading goes on.
        using var read = await bob.GetAsync("/api/notes/garten/plan.md");
        Assert.Equal("Beete planen\n", (await RestClient.JsonAsync(read))["content"]!.GetValue<string>());
        using var gone = await bob.GetAsync("/api/notes/garten/neu.md");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var none = await bob.GetAsync("/api/notes/garten/In/neu.md");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    [Fact]
    public async Task Members_may_export_it()
    {
        var w = await DeactivatedAsync();
        using var ticket = await w.Bob.SendAsync(HttpMethod.Post, "/api/export/ticket", new { password = RestClient.Password });
        var t = (await RestClient.JsonAsync(ticket))["ticket"]!.GetValue<string>();

        using var export = await w.Bob.GetAsync($"/api/vaults/garten/export?ticket={Uri.EscapeDataString(t)}");

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await export.Content.ReadAsByteArrayAsync(Ct)));
        Assert.Contains("garten/plan.md", zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task Indexing_stops()
    {
        var w = await DeactivatedAsync();
        await IndexedAsync();
        // Its embeddings gone and the vault read again on Bob's next request: its catch-up would embed plan.md anew.
        var embeddings = app.Database.GetCollection<BsonDocument>("embeddings");
        await embeddings.DeleteManyAsync(new BsonDocument("vault", w.VaultId), Ct);
        var catalog = Api.Services.GetRequiredService<VaultCatalog>();
        catalog.Forget(catalog.Current(w.VaultId)!, new InvalidOperationException("test"));
        await GartenAsync(w.Bob);
        await IndexedAsync();

        Assert.Equal(0, await embeddings.CountDocumentsAsync(new BsonDocument("vault", w.VaultId), cancellationToken: Ct));
        Assert.False(catalog.MayIndex(catalog.Current(w.VaultId)!));

        // Active again: it catches up.
        await SetActiveAsync(w.Alice, true);
        Assert.True(catalog.MayIndex(catalog.Current(w.VaultId)!));
        Assert.Empty(catalog.Current(w.VaultId)!.Embeddings);
        await IndexedAsync();
        Assert.Equal(1, await embeddings.CountDocumentsAsync(new BsonDocument("vault", w.VaultId), cancellationToken: Ct));
    }

    [Fact]
    public async Task Reactivation_lifts_it_on_the_next_request()
    {
        var w = await DeactivatedAsync();
        var bob = w.Bob;
        // What a member sees meanwhile: the banner's data, the level, and once, his agent's notice.
        var garten = await GartenAsync(bob);
        Assert.Equal((true, "read", false, "\"AliceExample\""), (
            garten["ownerInactive"]!.GetValue<bool>(), garten["level"]!.GetValue<string>(), garten["canWrite"]!.GetValue<bool>(), garten["owner"]!.GetValue<string>()));
        var blocks = await bob.ShBlocksAsync("ls /", w.Key);
        Assert.StartsWith("garten/ (shared by \"AliceExample\", read)", blocks[0], StringComparison.Ordinal);
        Assert.Contains("\nNotice: The owner of /garten (\"AliceExample\") is deactivated; /garten is read-only until the account is active again.", blocks);
        using (var events = await bob.GetAsync("/api/vaults/garten/activity"))
        {
            Assert.Contains((await RestClient.JsonAsync(events))["events"]!.AsArray(), e => e!["kind"]!.GetValue<string>() == "owner-inactive");
        }

        await SetActiveAsync(w.Alice, true);

        garten = await GartenAsync(bob);
        Assert.Equal((false, "write"), (garten["ownerInactive"]!.GetValue<bool>(), garten["level"]!.GetValue<string>()));
        using var written = await bob.SendAsync(HttpMethod.Put, "/api/notes/garten/plan.md", new { content = "neu\n", version = w.Plan });
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        Assert.False((await bob.ShAsync("mv /garten/plan.md /garten/neu.md", w.Key)).IsError);
        // Memberships were never touched.
        Assert.Equal(MembershipStatus.Active, (await app.Database.GetCollection<MembershipDocument>(Memberships.CollectionName).Find(m => m.Vault == w.VaultId).SingleAsync(Ct)).Status);
    }
}
