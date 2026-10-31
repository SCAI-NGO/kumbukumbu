// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// Until re-keying wraps the new key to members (collab design §12, phase 4), every route to it refuses a vault with a
/// membership or an invitation and changes nothing: "Re-encrypt vault", and "Re-encrypt my vaults" and recovery, which
/// re-key the other vaults and leave the shared one pending. Otherwise the start would delete the member slots.
/// </summary>
[Collection("app")]
public sealed class SharedRekeyRefusalTests(KumbuAppFactory app)
{
    private const string NewPassword = "another horse, another staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    private IMongoCollection<BsonDocument> Raw(string collection) => app.Database.GetCollection<BsonDocument>(collection);

    /// <summary>Alice's "Gartenarbeit" (and her vault "Diary"), shared with Bob as "garten", and a key of Bob's on it.</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId, string DiaryId, string BobsKey)> SharedAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "# Beete\n");
        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Diary" })) Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using (var note = await alice.SendAsync(HttpMethod.Put, "/api/notes/Diary/plan.md", new { content = "# Tag\n" })) Assert.Equal(HttpStatusCode.Created, note.StatusCode);
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "garten");
        var vaultId = await alice.VaultIdAsync();
        var diaryId = (await Catalog.FindForUserAsync(alice.User.Id, alice.UserKey, "Diary", Ct))!;
        var (key, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct);
        return (alice, bob, vaultId, diaryId, key);
    }

    /// <summary>Every slot of the vault, as MongoDB holds it.</summary>
    private async Task<string> SlotsAsync(string vaultId) =>
        string.Join("\n", (await Raw("slots").Find(new BsonDocument("vault", vaultId)).Sort(new BsonDocument("_id", 1)).ToListAsync(Ct)).Select(d => d.ToJson()));

    private async Task<string> MembershipsAsync(string vaultId) =>
        string.Join("\n", (await Raw(Memberships.CollectionName).Find(new BsonDocument("vault", vaultId)).Sort(new BsonDocument("_id", 1)).ToListAsync(Ct)).Select(d => d.ToJson()));

    private async Task<string[]> RekeyPendingAsync(string userId)
    {
        var user = await Raw("users").Find(new BsonDocument("_id", userId)).SingleAsync(Ct);
        return [.. user.GetValue("rekeyPending", new BsonArray()).AsBsonArray.Select(v => v.AsString).Order(StringComparer.Ordinal)];
    }

    private Task<bool> StartedAsync(string vaultId) =>
        Raw("vaults").Find(new BsonDocument { ["_id"] = vaultId, ["rekeyStartedAt"] = new BsonDocument("$exists", true) }).AnyAsync(Ct);

    private async Task<byte[]> OwnerVaultKeyAsync(string userId, byte[] userKey, string vaultId) =>
        await Catalog.OwnerVaultKeyAsync(userId, userKey, vaultId, Ct);

    /// <summary>Bob still reads the shared vault, with his session and with his key.</summary>
    private static async Task AssertBobReadsAsync(RestClient bob, string key)
    {
        using (var session = await bob.GetAsync("/api/notes/garten/plan.md")) Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using var byKey = await bob.GetAsync("/api/notes/garten/plan.md", bearer: key);
        Assert.Equal(HttpStatusCode.OK, byKey.StatusCode);
        Assert.Equal("# Beete\n", (await RestClient.JsonAsync(byKey))["content"]!.GetValue<string>());
    }

    /// <summary>Waits until "Diary" is re-keyed, then has the runner try the shared vault and sees it refused.</summary>
    private async Task AssertOthersRekeyedAndSharedLeftAsync(RestClient alice, string vaultId, string diaryId, byte[] newKey)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while ((await RekeyPendingAsync(alice.User.Id)).Contains(diaryId) || await StartedAsync(diaryId))
        {
            Assert.True(DateTime.UtcNow < until, "Diary was not re-keyed in time.");
            await Task.Delay(50, Ct);
        }

        var refused = await Assert.ThrowsAsync<VaultSharedException>(() => Api.Services.GetRequiredService<RekeyRunner>().RunAsync(vaultId, alice.User.Id, newKey).WaitAsync(Ct));
        Assert.Equal(VaultSharedException.Sentence, refused.Message);
        Assert.Equal([vaultId], await RekeyPendingAsync(alice.User.Id));
        Assert.False(await StartedAsync(vaultId));
    }

    private async Task<HttpResponseMessage> PostAnonymousAsync(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", KumbuAppFactory.PublicUrl);
        request.Headers.Add("X-Forwarded-For", $"198.19.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Re_encrypting_a_shared_vault_is_refused_and_changes_nothing()
    {
        var (alice, bob, vaultId, _, key) = await SharedAsync();
        var (alicesKey, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            alice.User, alice.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct);
        var (slots, memberships) = (await SlotsAsync(vaultId), await MembershipsAsync(vaultId));
        Assert.Contains("\"kind\" : \"member\"", slots, StringComparison.Ordinal);
        Assert.Contains("\"grantedVia\"", slots, StringComparison.Ordinal);
        var vaultKey = await OwnerVaultKeyAsync(alice.User.Id, alice.UserKey, vaultId);

        using var response = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/rekey", new { password = RestClient.Password });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Re-encrypting a shared vault comes with a later step; nothing was changed.", await RestClient.ErrorAsync(response));
        // Not one slot or membership changed: the member slot and Bob's GrantedVia slot are there, the vault key is the same.
        Assert.Equal(slots, await SlotsAsync(vaultId));
        Assert.Equal(memberships, await MembershipsAsync(vaultId));
        Assert.Equal(vaultKey, await OwnerVaultKeyAsync(alice.User.Id, alice.UserKey, vaultId));
        Assert.False(await StartedAsync(vaultId));
        Assert.Empty(await RekeyPendingAsync(alice.User.Id));
        // Alice's own key, which a run would have revoked, still works.
        Assert.NotNull(await Api.Services.GetRequiredService<ApiKeys>().ResolveAsync(alicesKey, Ct));
        await AssertBobReadsAsync(bob, key);
        Assert.Equal("# Beete\n", (await alice.ReadAsync("plan.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Re_encrypt_my_vaults_re_keys_the_others_and_leaves_the_shared_one_pending()
    {
        var (alice, bob, vaultId, diaryId, key) = await SharedAsync();
        var oldDiary = await OwnerVaultKeyAsync(alice.User.Id, alice.UserKey, diaryId);
        var vaultKey = await OwnerVaultKeyAsync(alice.User.Id, alice.UserKey, vaultId);
        var memberships = await MembershipsAsync(vaultId);

        using (var response = await alice.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var (_, newKey, _) = (await Api.Services.GetRequiredService<UserCatalog>().TrySignInAsync(alice.Username, RestClient.Password, Ct))!.Value;
        await AssertOthersRekeyedAndSharedLeftAsync(alice, vaultId, diaryId, newKey);

        Assert.NotEqual(oldDiary, await OwnerVaultKeyAsync(alice.User.Id, newKey, diaryId));
        // The shared vault keeps its key, its members and their slots.
        Assert.Equal(vaultKey, await OwnerVaultKeyAsync(alice.User.Id, newKey, vaultId));
        Assert.Equal(memberships, await MembershipsAsync(vaultId));
        var slots = await SlotsAsync(vaultId);
        Assert.Contains("\"kind\" : \"member\"", slots, StringComparison.Ordinal);
        Assert.Contains("\"grantedVia\"", slots, StringComparison.Ordinal);
        await AssertBobReadsAsync(bob, key);
        Assert.Equal("# Beete\n", (await alice.ReadAsync("plan.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Recovery_leaves_the_shared_vault_pending_and_its_members_in()
    {
        var (alice, bob, vaultId, diaryId, key) = await SharedAsync();
        var users = Api.Services.GetRequiredService<UserCatalog>();
        var recoveryKey = (await users.RegenerateRecoveryKeyAsync(alice.User.Id, alice.UserKey, RestClient.Password, Ct))!;
        var oldDiary = await OwnerVaultKeyAsync(alice.User.Id, alice.UserKey, diaryId);
        var vaultKey = await OwnerVaultKeyAsync(alice.User.Id, alice.UserKey, vaultId);
        var memberships = await MembershipsAsync(vaultId);

        using (var recovered = await PostAnonymousAsync("/api/recover", new { username = alice.Username, recoveryKey, newPassword = NewPassword }))
        {
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        }

        var (_, newKey, _) = (await users.TrySignInAsync(alice.Username, NewPassword, Ct))!.Value;
        await AssertOthersRekeyedAndSharedLeftAsync(alice, vaultId, diaryId, newKey);

        Assert.NotEqual(oldDiary, await OwnerVaultKeyAsync(alice.User.Id, newKey, diaryId));
        Assert.Equal(vaultKey, await OwnerVaultKeyAsync(alice.User.Id, newKey, vaultId));
        Assert.Equal(memberships, await MembershipsAsync(vaultId));
        await AssertBobReadsAsync(bob, key);
        // Tried again, as the next sign-in does, it is refused again: still nothing changed.
        var refused = await Assert.ThrowsAsync<VaultSharedException>(() => Catalog.RekeyAsync(vaultId, alice.User.Id, newKey, Ct));
        Assert.Equal(VaultSharedException.Sentence, refused.Message);
        Assert.Equal([vaultId], await RekeyPendingAsync(alice.User.Id));
        await AssertBobReadsAsync(bob, key);
    }

    [Fact]
    public async Task A_pending_invitation_counts_as_shared()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "# Beete\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var vaultId = await alice.VaultIdAsync();
        // Bob left: a tombstone doesn't make the vault shared.
        await Share.SetStatusAsync(app.Database, await alice.ShareWithAsync(bob, "garten"), MembershipStatus.Left);
        // An invitation nobody redeemed yet, as the invitation route will write it.
        var invitation = new MembershipDocument
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            Vault = vaultId,
            Owner = alice.User.Id,
            Role = MemberRoles.ToText(MemberRole.Read),
            Status = MembershipStatus.Pending,
            InviteLookup = RandomNumberGenerator.GetBytes(32),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
        };
        await app.Database.GetCollection<MembershipDocument>(Memberships.CollectionName).InsertOneAsync(invitation, cancellationToken: Ct);
        var slots = await SlotsAsync(vaultId);

        using (var refused = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/rekey", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal(VaultSharedException.Sentence, await RestClient.ErrorAsync(refused));
        }

        Assert.Equal(slots, await SlotsAsync(vaultId));

        // Withdrawn, the invitation is a tombstone too, and the vault is re-keyed.
        await Share.SetStatusAsync(app.Database, invitation.Id, MembershipStatus.Revoked);
        using var rekeyed = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/rekey", new { password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
        Assert.Equal("# Beete\n", (await alice.ReadAsync("plan.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_grant_racing_the_start_is_seen_by_it()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "# Beete\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var vaultId = await alice.VaultIdAsync();
        var granted = 0;
        // Inside the start's transaction, after its check: the grant commits, the start's write of the vault document
        // conflicts with it, and its retry finds the membership.
        Catalog.RekeyFaultAt = async point =>
        {
            if (point != "inside-start" || !Catalog.IsRekeying(vaultId) || Interlocked.Exchange(ref granted, 1) == 1) return;
            await alice.ShareWithAsync(bob, "garten");
        };
        try
        {
            using var response = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/rekey", new { password = RestClient.Password });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            Catalog.RekeyFaultAt = null;
        }

        Assert.Equal(1, granted);
        Assert.Contains("\"kind\" : \"member\"", await SlotsAsync(vaultId), StringComparison.Ordinal);
        using var read = await bob.GetAsync("/api/notes/garten/plan.md");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }
}
