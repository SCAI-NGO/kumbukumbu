// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Kumbu.Server.Accounts;
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
/// A member's account deletion (collab design §7.2, §12 phase 1): his memberships go, tombstones included, with the
/// slots he reached the vaults by; the owners' vaults stay as they are, his writes in them included, and resolve to
/// "a former member" for the owner and "another user" for the other members.
/// </summary>
[Collection("app")]
public sealed class MemberDeletionTests(KumbuAppFactory app)
{
    private static readonly byte[] Picture = FileApiTests.Png(4, 4);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private IMongoCollection<BsonDocument> Raw(string collection) => app.Database.GetCollection<BsonDocument>(collection);

    private IMongoCollection<SlotDocument> Slots => app.Database.GetCollection<SlotDocument>("slots");

    private IMongoCollection<MembershipDocument> Memberships => app.Database.GetCollection<MembershipDocument>(Kumbu.Server.Sharing.Memberships.CollectionName);

    private Task<long> CountAsync(string collection, string vaultId) =>
        Raw(collection).CountDocumentsAsync(new BsonDocument("vault", vaultId), cancellationToken: Ct);

    private async Task<string> KeyAsync(RestClient user, string name, params string[] vaultIds)
    {
        var (key, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, name, vaultIds.ToDictionary(v => v, _ => KeyRight.Write), Ct);
        return key;
    }

    private static async Task PutAsync(RestClient user, string path, string content, string? bearer = null)
    {
        using var response = await user.SendAsync(HttpMethod.Put, path, new { content }, bearer);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task DeleteAccountAsync(RestClient user)
    {
        using var response = await user.SendAsync(HttpMethod.Post, "/api/account/delete", new { password = RestClient.Password, confirm = user.Username });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<(string Writer, string? Kind)> WriterAsync(RestClient viewer, string path)
    {
        using var response = await viewer.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var note = await RestClient.JsonAsync(response);
        return (note["lastWriter"]!.GetValue<string>(), note["lastWriterKind"]?.GetValue<string>());
    }

    [Fact]
    public async Task The_admins_user_list_counts_the_shared_vaults_of_each_user_and_names_none()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("plan.md", "# Beete\n");
        await alice.ShareWithAsync(bob, "garten", MemberRole.Read);
        var admin = await RestClient.NewAsync(Api, vault: "Admin", role: Roles.Admin);

        using var response = await admin.GetAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var rows = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsArray();
        // One vault, shared with Bob: Alice has it counted, and Bob's own unshared vault is no shared vault of his.
        Assert.Equal(1, rows.Single(u => u!["username"]!.GetValue<string>() == alice.Username)!["sharedVaults"]!.GetValue<int>());
        Assert.Equal(0, rows.Single(u => u!["username"]!.GetValue<string>() == bob.Username)!["sharedVaults"]!.GetValue<int>());
        Assert.Equal(0, rows.Single(u => u!["username"]!.GetValue<string>() == carol.Username)!["sharedVaults"]!.GetValue<int>());
        Assert.DoesNotContain("Gartenarbeit", body);
    }

    [Fact]
    public async Task A_members_account_deletion_never_deletes_the_owners_vault()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "# Beete\n");
        await alice.CreateFileAsync("beet.png", Picture);
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await bob.CreateAsync("eigen.md", "# Bobs\n");
        await alice.ShareWithAsync(bob, "garten", MemberRole.Maintain);
        var (vaultId, bobsId) = (await alice.VaultIdAsync(), await bob.VaultIdAsync());
        await PutAsync(bob, "/api/notes/garten/salat.md", "Salat\n");
        await PutAsync(bob, "/api/notes/garten/kohl.md", "Kohl\n", await KeyAsync(bob, "Claude", vaultId, bobsId));
        using (var upload = await bob.SendContentAsync(
            HttpMethod.Put, "/api/files/garten/bild.png", new ByteArrayContent(Picture) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }))
        {
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        }

        var ownerSlots = (await Slots.Find(s => s.Vault == vaultId && s.Kind != SlotDocument.Member && s.GrantedVia == null).SortBy(s => s.Id).ToListAsync(Ct))
            .Select(s => s.ToBsonDocument().ToJson()).ToList();
        var counts = new Dictionary<string, long>();
        foreach (var collection in new[] { "notes", "attachments", "blobs", "moves" }) counts[collection] = await CountAsync(collection, vaultId);
        Assert.Equal(3, counts["notes"]);

        await DeleteAccountAsync(bob);

        // Alice's vault: there, its own slots byte for byte, every note and attachment, Bob's writes included.
        Assert.True(await Raw("vaults").Find(new BsonDocument("_id", vaultId)).AnyAsync(Ct));
        Assert.Equal(ownerSlots, (await Slots.Find(s => s.Vault == vaultId).SortBy(s => s.Id).ToListAsync(Ct)).Select(s => s.ToBsonDocument().ToJson()).ToList());
        foreach (var (collection, count) in counts) Assert.Equal(count, await CountAsync(collection, vaultId));
        Assert.Equal("Salat\n", (await alice.ReadAsync("salat.md"))["content"]!.GetValue<string>());
        Assert.Equal("Kohl\n", (await alice.ReadAsync("kohl.md"))["content"]!.GetValue<string>());
        foreach (var file in new[] { "beet.png", "bild.png" })
        {
            using var read = await alice.GetAsync(alice.Url(file, "files"));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(Picture, await read.Content.ReadAsByteArrayAsync(Ct));
        }

        // Bob's own vault is gone, with everything in it.
        Assert.False(await Raw("vaults").Find(new BsonDocument("_id", bobsId)).AnyAsync(Ct));
        Assert.Equal(0, await CountAsync("slots", bobsId));
        Assert.Equal(0, await CountAsync("notes", bobsId));
    }

    [Fact]
    public async Task A_members_account_deletion_removes_his_memberships_and_slots_on_shared_vaults()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "# Beete\n");
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        await carol.CreateAsync("liste.md", "# Liste\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var (garten, privat) = (await alice.VaultIdAsync(), await carol.VaultIdAsync());
        var bobs = new[] { await alice.ShareWithAsync(bob, "garten"), await carol.ShareWithAsync(bob, "privat") };
        var carols = await alice.ShareWithAsync(carol, "Beete");
        // Bob's left one goes too: a tombstone names him as well.
        await Share.SetStatusAsync(app.Database, bobs[1], MembershipStatus.Left);
        await KeyAsync(bob, "Claude", garten, await bob.VaultIdAsync());
        var mine = Builders<SlotDocument>.Filter.In(s => s.Membership, bobs) | Builders<SlotDocument>.Filter.In(s => s.GrantedVia, bobs);
        Assert.Equal(3, await Slots.CountDocumentsAsync(mine, cancellationToken: Ct));

        await DeleteAccountAsync(bob);

        Assert.Equal(0, await Memberships.CountDocumentsAsync(m => m.Member == bob.User.Id, cancellationToken: Ct));
        Assert.Equal(0, await Slots.CountDocumentsAsync(mine, cancellationToken: Ct));
        // Carol's membership of Alice's vault, and the owners' slots, stay as they were.
        Assert.Equal(MembershipStatus.Active, (await Share.MembershipAsync(app.Database, carols)).Status);
        Assert.Equal(1, await Slots.CountDocumentsAsync(s => s.Membership == carols && s.Kind == SlotDocument.Member, cancellationToken: Ct));
        Assert.Equal(1, await Slots.CountDocumentsAsync(s => s.Vault == garten && s.Kind == SlotDocument.User, cancellationToken: Ct));
        Assert.Equal(1, await Slots.CountDocumentsAsync(s => s.Vault == privat && s.Kind == SlotDocument.User, cancellationToken: Ct));
        using (var carolReads = await carol.GetAsync("/api/notes/Beete/plan.md")) Assert.Equal(HttpStatusCode.OK, carolReads.StatusCode);
        Assert.Equal("# Liste\n", (await carol.ReadAsync("liste.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task After_a_members_deletion_his_writes_show_as_a_former_member_to_the_owner_and_another_user_to_members()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        await alice.ShareWithAsync(bob, "garten", label: "Bob");
        await alice.ShareWithAsync(carol, "Beete", label: "Carol");
        var vaultId = await alice.VaultIdAsync();
        await PutAsync(bob, "/api/notes/garten/s.md", "Salat\n");
        await PutAsync(bob, "/api/notes/garten/k.md", "Kohl\n", await KeyAsync(bob, "Claude", vaultId));
        Assert.Equal(("Bob · Claude", WriterLabels.Agent), await WriterAsync(alice, "/api/notes/Gartenarbeit/k.md"));
        Assert.Equal(("Bob", WriterLabels.User), await WriterAsync(alice, "/api/notes/Gartenarbeit/s.md"));

        await DeleteAccountAsync(bob);

        Assert.Equal((WriterLabels.FormerMember, WriterLabels.Agent), await WriterAsync(alice, "/api/notes/Gartenarbeit/k.md"));
        Assert.Equal((WriterLabels.FormerMember, WriterLabels.User), await WriterAsync(alice, "/api/notes/Gartenarbeit/s.md"));
        Assert.Equal((WriterLabels.AnotherUser, WriterLabels.Agent), await WriterAsync(carol, "/api/notes/Beete/k.md"));
        Assert.Equal((WriterLabels.AnotherUser, WriterLabels.User), await WriterAsync(carol, "/api/notes/Beete/s.md"));
        // Nothing in Alice's history of the note names him any more.
        using var history = await alice.GetAsync("/api/history/Gartenarbeit/k.md");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var text = await history.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("Bob", text, StringComparison.Ordinal);
        Assert.DoesNotContain(bob.Username, text, StringComparison.Ordinal);
    }
}
