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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// The owner deletes her account (collab design §7.3): "Give members 7 days" keeps a shared vault read-only and
/// exportable for its members, with <c>code: closing</c> on every write, until a sweep deletes it at <c>closingAt</c>;
/// the choice is recorded on the deletion list, so a repeat after a restore applies it again; "Delete it for everyone
/// now" leaves each member a notice under her alias; her open invitations are withdrawn either way.
/// </summary>
[Collection("app")]
public sealed class ClosingPeriodTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private IMongoCollection<VaultDocument> Vaults => app.Database.GetCollection<VaultDocument>("vaults");

    private IMongoCollection<SlotDocument> Slots => app.Database.GetCollection<SlotDocument>("slots");

    private IMongoCollection<MembershipDocument> MembershipDocs => app.Database.GetCollection<MembershipDocument>(Memberships.CollectionName);

    private sealed record World(RestClient Alice, RestClient Bob, string VaultId, string Key, string Plan);

    /// <summary>Alice shares "Gartenarbeit" (plan.md) with Bob as "garten", to write, Bob's key on it sealed write; she
    /// has a vault of her own beside it, "Privat".</summary>
    private async Task<World> SharedAsync(WebApplicationFactory<Program>? api = null)
    {
        api ??= Api;
        var alice = await RestClient.NewAsync(api, vault: "Gartenarbeit");
        var plan = await alice.CreateAsync("plan.md", "Beete planen\n");
        using (var privat = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Privat" })) Assert.Equal(HttpStatusCode.Created, privat.StatusCode);
        var bob = await RestClient.NewAsync(api, vault: "Work");
        await alice.ShareWithAsync(bob, "garten", MemberRole.Write);
        var vaultId = await alice.VaultIdAsync();
        var key = (await api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write, [await bob.VaultIdAsync()] = KeyRight.Write }, Ct)).Key;
        Assert.False((await bob.ShAsync("ls /garten", key)).IsError);
        return new World(alice, bob, vaultId, key, plan);
    }

    private static async Task DeleteOwnAsync(RestClient user, string? sharedVaults = null)
    {
        using var response = await user.SendAsync(
            HttpMethod.Post, "/api/account/delete", sharedVaults is null ? new { password = RestClient.Password, confirm = user.Username } : new { password = RestClient.Password, confirm = user.Username, sharedVaults });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<World> ClosingAsync()
    {
        var w = await SharedAsync();
        await DeleteOwnAsync(w.Alice);
        return w;
    }

    private static async Task<JsonNode?> GartenAsync(RestClient bob, string alias = "garten")
    {
        using var response = await bob.GetAsync("/api/vaults");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response)).AsArray().SingleOrDefault(v => v!["name"]!.GetValue<string>() == alias);
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await RestClient.JsonAsync(response);
        Assert.Equal(Refusals.Closing, body["code"]?.GetValue<string>());
        Assert.Contains("read-only: its owner deleted their account, and it is closing", body["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static async Task<string> PlanAsync(RestClient bob)
    {
        using var read = await bob.GetAsync("/api/notes/garten/plan.md");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        return (await RestClient.JsonAsync(read))["content"]!.GetValue<string>();
    }

    private static string Day(DateTime at) => at.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Give_members_7_days_keeps_the_vault_read_only_and_exportable()
    {
        var w = await SharedAsync();
        var privatId = await Api.Services.GetRequiredService<VaultCatalog>().FindForUserAsync(w.Alice.User.Id, w.Alice.UserKey, "Privat", Ct);
        var deletion = Api.Services.GetRequiredService<Deletion>();
        var before = DateTime.UtcNow;
        // A crash right after the commit: the closing period is set in the deletion's own transaction, or not at all.
        deletion.FaultAt = at => at == "after-commit" ? throw new InvalidOperationException("crash") : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.DeleteOwnAccountAsync(w.Alice.User, w.Alice.UserKey, RestClient.Password, w.Alice.Username, Ct));
        }
        finally
        {
            deletion.FaultAt = null;
        }

        // Her account, her own vault and her ways into the shared one are gone; the vault stays, closing in 7 days.
        Assert.False(await app.Database.GetCollection<UserDocument>("users").Find(u => u.Id == w.Alice.User.Id).AnyAsync(Ct));
        Assert.False(await Vaults.Find(v => v.Id == privatId).AnyAsync(Ct));
        var doc = await Vaults.Find(v => v.Id == w.VaultId).SingleAsync(Ct);
        Assert.InRange(doc.ClosingAt!.Value, before + Deletion.ClosingPeriod - TimeSpan.FromSeconds(1), DateTime.UtcNow + Deletion.ClosingPeriod);
        var slots = await Slots.Find(s => s.Vault == w.VaultId).ToListAsync(Ct);
        Assert.All(slots, s => Assert.True(s.Kind == SlotDocument.Member || s.GrantedVia is not null, s.Kind));
        Assert.Contains(slots, s => s.Kind == SlotDocument.Member);
        Assert.Empty(await Api.Services.GetRequiredService<VaultCatalog>().OwnedVaultIdsAsync(w.Alice.User.Id, Ct));

        // Bob reads it, read-only, with the date for the banner.
        var garten = (await GartenAsync(w.Bob))!;
        Assert.Equal(("read", false), (garten["level"]!.GetValue<string>(), garten["canWrite"]!.GetValue<bool>()));
        Assert.Equal(doc.ClosingAt.Value, garten["closingAt"]!.GetValue<DateTime>().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal("Beete planen\n", await PlanAsync(w.Bob));
        // His agent is told once, his activity says so without naming her.
        var blocks = await w.Bob.ShBlocksAsync("ls /", w.Key);
        Assert.Contains($"\nNotice: /garten will be deleted on {Day(doc.ClosingAt.Value)}; export a copy to keep its notes.", blocks);
        using (var events = await w.Bob.GetAsync("/api/vaults/garten/activity"))
        {
            var closing = (await RestClient.JsonAsync(events))["events"]!.AsArray().Single(e => e!["kind"]!.GetValue<string>() == VaultEvents.ClosingStarted)!;
            Assert.Equal($"The owner deleted their account; garten will be deleted on {Day(doc.ClosingAt.Value)}. Export a copy to keep its notes.", closing["text"]!.GetValue<string>());
        }

        // He may export it; nothing more of it is indexed.
        using var ticket = await w.Bob.SendAsync(HttpMethod.Post, "/api/export/ticket", new { password = RestClient.Password });
        var t = (await RestClient.JsonAsync(ticket))["ticket"]!.GetValue<string>();
        using var export = await w.Bob.GetAsync($"/api/vaults/garten/export?ticket={Uri.EscapeDataString(t)}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await export.Content.ReadAsByteArrayAsync(Ct)));
        Assert.Contains("garten/plan.md", zip.Entries.Select(e => e.FullName));
        var catalog = Api.Services.GetRequiredService<VaultCatalog>();
        Assert.False(catalog.MayIndex(catalog.Current(w.VaultId)!));
    }

    [Theory]
    [InlineData("PUT note")]
    [InlineData("DELETE note")]
    [InlineData("upload by key")]
    [InlineData("MCP write")]
    [InlineData("store")]
    public async Task Writes_are_refused_with_code_closing_on_every_route(string route)
    {
        var w = await ClosingAsync();
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
                Assert.Equal("mv: vault garten is read-only: its owner deleted their account, and it is closing", text);
                break;
            case "store":
                var store = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(bob.User.Id, bob.UserKey));
                var writer = new NoteWriter(store, TimeProvider.System, new FixedCaller("Bob"));
                var refused = await Assert.ThrowsAsync<VaultReadOnlyException>(() => writer.WriteAsync("/garten/neu.md", "neu\n", null, Ct));
                Assert.Equal(Refusals.Closing, refused.Code);
                break;
        }

        Assert.Equal("Beete planen\n", await PlanAsync(bob));
        using var gone = await bob.GetAsync("/api/notes/garten/neu.md");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var none = await bob.GetAsync("/api/notes/garten/In/neu.md");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        // His own vault is his to write, by the same key.
        using var own = await bob.SendAsync(HttpMethod.Put, "/api/notes/Work/eigen.md", new { content = "eigen\n" }, bearer: w.Key);
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
    }

    [Fact]
    public async Task The_sweep_deletes_it_at_closingAt()
    {
        var w = await ClosingAsync();
        var sweeper = Api.Services.GetRequiredService<MembershipSweeper>();

        await sweeper.SweepAsync(Ct);
        Assert.NotNull(await GartenAsync(w.Bob));

        // Its time comes.
        await Vaults.UpdateOneAsync(v => v.Id == w.VaultId, Builders<VaultDocument>.Update.Set(v => v.ClosingAt, DateTime.UtcNow.AddSeconds(-1)), cancellationToken: Ct);
        await sweeper.SweepAsync(Ct);

        Assert.False(await Vaults.Find(v => v.Id == w.VaultId).AnyAsync(Ct));
        Assert.Equal(0, await Slots.CountDocumentsAsync(s => s.Vault == w.VaultId, cancellationToken: Ct));
        Assert.Equal(0, await MembershipDocs.CountDocumentsAsync(m => m.Vault == w.VaultId, cancellationToken: Ct));
        Assert.Equal(0, await app.Database.GetCollection<BsonDocument>("notes").CountDocumentsAsync(new BsonDocument("vault", w.VaultId), cancellationToken: Ct));
        Assert.Null(await GartenAsync(w.Bob));
        // His key keeps his own vault; a second sweep finds nothing to do.
        Assert.False((await w.Bob.ShAsync("ls /Work", w.Key)).IsError);
        await sweeper.SweepAsync(Ct);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SharedVaultChoices.DeleteNow)]
    public async Task The_admins_deletion_applies_its_choice_to_every_shared_vault(string? choice)
    {
        var w = await SharedAsync();
        var memberships = Api.Services.GetRequiredService<Memberships>();
        var privatId = (await Api.Services.GetRequiredService<VaultCatalog>().FindForUserAsync(w.Alice.User.Id, w.Alice.UserKey, "Privat", Ct))!;
        await memberships.GrantAsync(w.Alice.User.Id, w.Alice.UserKey, privatId, w.Bob.User.Id, w.Bob.UserKey, "alices", "Bob", MemberRole.Read, Ct);
        // A third vault of hers, not shared: it goes at once either way.
        using (var drittes = await w.Alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Drittes" })) Assert.Equal(HttpStatusCode.Created, drittes.StatusCode);
        var admin = await RestClient.NewAsync(Api, vault: "Admin", role: Roles.Admin);

        using var response = await admin.SendAsync(
            HttpMethod.Post, $"/api/admin/users/{w.Alice.User.Id}/delete",
            choice is null ? new { password = RestClient.Password, confirm = w.Alice.Username } : new { password = RestClient.Password, confirm = w.Alice.Username, sharedVaults = choice });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await RestClient.JsonAsync(response);
        var closing = await Vaults.Find(v => (v.Id == w.VaultId || v.Id == privatId) && v.ClosingAt != null).ToListAsync(Ct);
        if (choice is null)
        {
            Assert.Equal(2, closing.Count);
            Assert.Equal(1, receipt["vaults"]!.GetValue<int>());
            Assert.Equal(2, receipt["sharedVaultsScheduled"]!.AsArray().Count);
            Assert.Equal(("read", "read"), ((await GartenAsync(w.Bob))!["level"]!.GetValue<string>(), (await GartenAsync(w.Bob, "alices"))!["level"]!.GetValue<string>()));
        }
        else
        {
            Assert.Empty(closing);
            Assert.False(await Vaults.Find(v => v.Id == w.VaultId || v.Id == privatId).AnyAsync(Ct));
            Assert.Equal((3, 2), (receipt["vaults"]!.GetValue<int>(), receipt["sharedVaultsClosed"]!.GetValue<int>()));
            Assert.Empty(receipt["sharedVaultsScheduled"]!.AsArray());
            Assert.Null(await GartenAsync(w.Bob));
        }

        Assert.DoesNotContain("garten", receipt.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_for_everyone_now_leaves_a_notice_under_the_members_alias_naming_no_owner()
    {
        var w = await SharedAsync();
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        await w.Alice.ShareWithAsync(carol, "beete", MemberRole.Read);

        await DeleteOwnAsync(w.Alice, SharedVaultChoices.DeleteNow);

        Assert.False(await Vaults.Find(v => v.Id == w.VaultId).AnyAsync(Ct));
        Assert.Null(await GartenAsync(w.Bob));
        async Task<JsonArray> NoticesAsync(RestClient user)
        {
            using var r = await user.GetAsync("/api/member-notices");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var text = await r.Content.ReadAsStringAsync(Ct);
            foreach (var name in new[] { w.Alice.Username, "Gartenarbeit", w.Alice.User.Id, w.VaultId }) Assert.DoesNotContain(name, text, StringComparison.Ordinal);
            return JsonNode.Parse(text)!.AsArray();
        }

        var bobs = await NoticesAsync(w.Bob);
        Assert.Equal(("garten", "The owner has deleted garten."), (bobs.Single()!["vault"]!.GetValue<string>(), bobs.Single()!["text"]!.GetValue<string>()));
        var carols = await NoticesAsync(carol);
        Assert.Equal("The owner has deleted beete.", carols.Single()!["text"]!.GetValue<string>());

        // A rotation of Bob's user key before he reads it: the notice still names his alias.
        using (var rotated = await w.Bob.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        }

        var bobAgain = await w.Bob.SignInAgainAsync();
        Assert.Equal("The owner has deleted garten.", (await NoticesAsync(bobAgain)).Single()!["text"]!.GetValue<string>());

        // Dismissed: his own only.
        using (var other = await bobAgain.SendAsync(HttpMethod.Delete, $"/api/member-notices/{carols.Single()!["id"]!.GetValue<string>()}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        }

        using (var dismissed = await bobAgain.SendAsync(HttpMethod.Delete, $"/api/member-notices/{bobs.Single()!["id"]!.GetValue<string>()}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        }

        Assert.Empty(await NoticesAsync(bobAgain));
        Assert.Single(await NoticesAsync(carol));
    }

    [Fact]
    public async Task The_owners_open_invitations_are_withdrawn()
    {
        var w = await SharedAsync();
        var memberships = Api.Services.GetRequiredService<Memberships>();
        var open = await memberships.InviteAsync(w.Alice.User.Id, w.Alice.UserKey, w.VaultId, "Carol", MemberRole.Read, TimeSpan.FromDays(7), false, Ct);
        var confirm = await memberships.InviteAsync(w.Alice.User.Id, w.Alice.UserKey, w.VaultId, "Dora", MemberRole.Read, TimeSpan.FromDays(7), true, Ct);
        var dora = await RestClient.NewAsync(Api, vault: "Doras");
        await memberships.AcceptAsync(dora.User.Id, dora.UserKey, confirm.Secret, "garten", Ct);
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");

        await DeleteOwnAsync(w.Alice);

        foreach (var id in new[] { open.MembershipId, confirm.MembershipId })
        {
            var m = await Share.MembershipAsync(app.Database, id);
            Assert.Equal((MembershipStatus.Revoked, (byte[]?)null), (m.Status, m.InviteLookup));
            Assert.Equal(0, await Slots.CountDocumentsAsync(s => s.Membership == id, cancellationToken: Ct));
        }

        using var peek = await carol.SendAsync(HttpMethod.Post, "/api/vault-invitations/peek", new { secret = open.Secret });
        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        Assert.Null(await GartenAsync(dora));
        // Bob's membership is untouched.
        Assert.NotNull(await GartenAsync(w.Bob));
    }

    [Fact]
    public async Task Accepting_an_invitation_to_a_closing_vault_is_refused()
    {
        var w = await SharedAsync();
        var memberships = Api.Services.GetRequiredService<Memberships>();
        var withdrawn = await memberships.InviteAsync(w.Alice.User.Id, w.Alice.UserKey, w.VaultId, "Carol", MemberRole.Read, TimeSpan.FromDays(7), false, Ct);
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        // An invitation that is still open while the vault closes (written straight to the database): refused all the same.
        var other = await RestClient.NewAsync(Api, vault: "Beete");
        var stillOpen = await memberships.InviteAsync(other.User.Id, other.UserKey, await other.VaultIdAsync(), "Carol", MemberRole.Read, TimeSpan.FromDays(7), false, Ct);
        var otherId = await other.VaultIdAsync();
        await Vaults.UpdateOneAsync(v => v.Id == otherId, Builders<VaultDocument>.Update.Set(v => v.ClosingAt, DateTime.UtcNow.AddDays(7)), cancellationToken: Ct);

        await DeleteOwnAsync(w.Alice);

        using (var accept = await carol.SendAsync(HttpMethod.Post, "/api/vault-invitations/accept", new { secret = withdrawn.Secret, alias = "garten" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
        }

        using (var accept = await carol.SendAsync(HttpMethod.Post, "/api/vault-invitations/accept", new { secret = stillOpen.Secret, alias = "beete" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
            Assert.Equal(Refusals.Closing, (await RestClient.JsonAsync(accept))["code"]!.GetValue<string>());
        }

        Assert.Null(await GartenAsync(carol));
        Assert.Null(await GartenAsync(carol, "beete"));
        Assert.Equal(0, await MembershipDocs.CountDocumentsAsync(m => m.Member == carol.User.Id, cancellationToken: Ct));
        Assert.Equal(MembershipStatus.Pending, (await Share.MembershipAsync(app.Database, stillOpen.MembershipId)).Status);
    }

    /// <summary>The documents of these users and vaults, as a backup holds them.</summary>
    private async Task<List<(string Collection, BsonDocument Doc)>> BackupAsync(string[] users, string[] vaults)
    {
        var result = new List<(string, BsonDocument)>();
        async Task TakeAsync(string collection, FilterDefinition<BsonDocument> filter)
        {
            foreach (var d in await app.Database.GetCollection<BsonDocument>(collection).Find(filter).ToListAsync(Ct)) result.Add((collection, d));
        }

        var f = Builders<BsonDocument>.Filter;
        await TakeAsync("users", f.In("_id", users));
        await TakeAsync("user_slots", f.In("user", users));
        await TakeAsync("vaults", f.In("_id", vaults));
        foreach (var c in new[] { "slots", Memberships.CollectionName, "notes" }) await TakeAsync(c, f.In("vault", vaults));
        return result;
    }

    private async Task RestoreAsync(List<(string Collection, BsonDocument Doc)> backup)
    {
        foreach (var (collection, doc) in backup)
        {
            await app.Database.GetCollection<BsonDocument>(collection).ReplaceOneAsync(new BsonDocument("_id", doc["_id"]), doc, new ReplaceOptions { IsUpsert = true }, Ct);
        }
    }

    [Fact]
    public async Task A_restored_backup_deletes_a_vault_whose_closing_passed_and_closes_one_whose_didnt()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var dir = Path.Combine(Path.GetTempPath(), "kumbu-deletions-" + Guid.NewGuid().ToString("N"));
        var api = Api.WithWebHostBuilder(b =>
        {
            b.UseSetting("Kumbu:Deletions:Path", dir);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(time);
            });
        });
        try
        {
            var first = await SharedAsync(api);
            var dora = await RestClient.NewAsync(api, vault: "Doras");
            await dora.CreateAsync("liste.md", "Liste\n");
            await dora.ShareWithAsync(first.Bob, "doras", MemberRole.Write);
            var doraVault = await dora.VaultIdAsync();
            var admin = await RestClient.NewAsync(api, vault: "Admin", role: Roles.Admin);
            var deletion = api.Services.GetRequiredService<Deletion>();
            var backup = await BackupAsync([first.Alice.User.Id, dora.User.Id], [first.VaultId, doraVault]);

            // Alice goes now, Dora five days later, both giving their members 7 days.
            var aliceDeletedAt = time.GetUtcNow().UtcDateTime;
            await deletion.DeleteUserAsync(admin.User, admin.UserKey, RestClient.Password, first.Alice.User.Id, first.Alice.Username, Ct);
            time.Advance(TimeSpan.FromDays(5));
            var doraClosingAt = time.GetUtcNow().UtcDateTime + Deletion.ClosingPeriod;
            await deletion.DeleteUserAsync(admin.User, admin.UserKey, RestClient.Password, dora.User.Id, dora.Username, Ct);
            Assert.Equal(doraClosingAt, (await Vaults.Find(v => v.Id == doraVault).SingleAsync(Ct)).ClosingAt);
            // Alice's has passed, and the sweep took it.
            time.Advance(TimeSpan.FromDays(3));
            await api.Services.GetRequiredService<MembershipSweeper>().SweepAsync(Ct);
            Assert.False(await Vaults.Find(v => v.Id == first.VaultId).AnyAsync(Ct));

            // The backup from before both deletions comes back, and the admin repeats them.
            await RestoreAsync(backup);
            Assert.Null((await Vaults.Find(v => v.Id == doraVault).SingleAsync(Ct)).ClosingAt);
            var again = await deletion.RepeatAsync(admin.User, admin.UserKey, RestClient.Password, Ct);

            Assert.Equal(new[] { dora.User.Id, first.Alice.User.Id }.Order(), again.Select(d => d.Id).Order());
            Assert.False(await Vaults.Find(v => v.Id == first.VaultId).AnyAsync(Ct));
            Assert.True(aliceDeletedAt + Deletion.ClosingPeriod < time.GetUtcNow().UtcDateTime);
            Assert.Equal(doraClosingAt, (await Vaults.Find(v => v.Id == doraVault).SingleAsync(Ct)).ClosingAt);
            var slots = await Slots.Find(s => s.Vault == doraVault).ToListAsync(Ct);
            Assert.Equal([SlotDocument.Member], slots.Select(s => s.Kind).Distinct());
            Assert.Equal(MembershipStatus.Active, (await MembershipDocs.Find(m => m.Vault == doraVault).SingleAsync(Ct)).Status);
            Assert.Equal(doraClosingAt, Assert.Single(again.Single(d => d.Id == dora.User.Id).Summary.Closing!).Value);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
