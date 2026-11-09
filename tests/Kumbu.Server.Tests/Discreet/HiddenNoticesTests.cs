// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Crypto;
using Kumbu.Core.Skills;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>
/// What a session says of hidden vaults outside its credential (Decisions 11 and 13): the member notices and the events
/// of a hidden vault, also of one that is gone, aren't listed, and dismissing such a notice answers as an unknown id;
/// the notice of the vault Skills is left out while the vault it renamed is hidden, and dismissing it then leaves it.
/// Revealed, all are back.
/// </summary>
[Collection("app")]
public sealed class HiddenNoticesTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private IMongoCollection<MemberNoticeDocument> Notices => app.Database.GetCollection<MemberNoticeDocument>(MemberNotices.CollectionName);

    /// <summary>
    /// Alice's world, where Carol also shares <c>Garten</c>, <c>Laube</c> and <c>Beet</c>, Alice with a key on each. Bob
    /// deletes his account and <c>Familie</c> with it at once; Carol removes Alice from <c>Laube</c> and re-encrypts
    /// <c>Garten</c> and <c>Beet</c>. Hidden: <c>Familie</c> (gone), <c>Laube</c> (no longer hers) and <c>Garten</c>;
    /// <c>Beet</c> stays in sight.
    /// </summary>
    private async Task<HiddenWorld> NoticeWorldAsync()
    {
        var world = await HiddenWorld.NewAsync(Api);
        var (alice, bob) = (world.Alice, world.Bob);
        var carol = await RestClient.NewAsync(Api, vault: "Beet");
        await carol.ShareWithAsync(alice, "Beet");
        var garten = await CarolsAsync(carol, "Garten");
        await garten.ShareWithAsync(alice, "Garten");
        var laube = await CarolsAsync(carol, "Laube");
        var laubeMembership = await laube.ShareWithAsync(alice, "Laube");
        var (gartenId, laubeId) = (await garten.VaultIdAsync(), await laube.VaultIdAsync());
        await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            alice.User, alice.UserKey, DateTime.UtcNow, "Claude",
            new Dictionary<string, KeyRight> { [gartenId] = KeyRight.Read, [await carol.VaultIdAsync()] = KeyRight.Read }, Ct);

        using (var deleted = await bob.SendAsync(
            HttpMethod.Post, "/api/account/delete", new { password = RestClient.Password, confirm = bob.Username, sharedVaults = SharedVaultChoices.DeleteNow }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var revoked = await carol.SendAsync(HttpMethod.Post, $"/api/vaults/Laube/memberships/{laubeMembership}/revoke"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }

        foreach (var vault in new[] { "Garten", "Beet" })
        {
            using var rekeyed = await carol.SendAsync(HttpMethod.Post, $"/api/vaults/{vault}/rekey", new { password = RestClient.Password });
            Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
        }

        await world.HideAsync(gartenId, laubeId);
        return world;
    }

    private async Task<RestClient> CarolsAsync(RestClient carol, string vault)
    {
        using (var created = await carol.SendAsync(HttpMethod.Post, "/api/vaults", new { name = vault }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        return RestClient.Existing(Api, carol.User, carol.UserKey, carol.Session, vault);
    }

    private static async Task<(JsonArray Items, string Body)> ListAsync(RestClient user, string path)
    {
        using var response = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        return (JsonNode.Parse(body)!.AsArray(), body);
    }

    private static async Task<(HttpStatusCode, string)> DismissAsync(RestClient user, string id)
    {
        using var response = await user.SendAsync(HttpMethod.Delete, $"/api/member-notices/{id}");
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_notices_and_events_of_hidden_vaults_are_left_out_until_revealed()
    {
        var world = await NoticeWorldAsync();
        var (hidden, body) = await ListAsync(world.Alice, "/api/member-notices");
        Assert.Equal("Beet", hidden.Single()!["vault"]!.GetValue<string>());
        Assert.DoesNotContain("Familie", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Garten", body, StringComparison.Ordinal);

        // Dismissing one answers exactly as an id she has no notice of, and leaves it.
        var familie = (await Notices.Find(n => n.User == world.Alice.User.Id && n.Vault == world.FamilieId).SingleAsync(Ct)).Id.ToString();
        Assert.Equal(await DismissAsync(world.Alice, ObjectId.GenerateNewId().ToString()), await DismissAsync(world.Alice, familie));

        var (hiddenEvents, eventsBody) = await ListAsync(world.Alice, "/api/vault-events");
        Assert.NotEmpty(hiddenEvents);
        Assert.All(hiddenEvents, e => Assert.Equal("Beet", e!["vault"]?.GetValue<string>()));
        Assert.DoesNotContain("Garten", eventsBody, StringComparison.Ordinal);

        await world.RevealAsync();
        var (shown, _) = await ListAsync(world.Alice, "/api/member-notices");
        Assert.Equal(["Beet", "Familie", "Garten"], shown.Select(n => n!["vault"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal(HttpStatusCode.NoContent, (await DismissAsync(world.Alice, familie)).Item1);

        var (shownEvents, _) = await ListAsync(world.Alice, "/api/vault-events");
        // Laube's removal names it no more: she no longer reaches it.
        Assert.Contains(shownEvents, e => e!["vault"] is null && e["kind"]!.GetValue<string>() == VaultEvents.MembershipRevoked);
        Assert.Contains(shownEvents, e => e!["vault"]?.GetValue<string>() == "Garten");
        Assert.Equal(shownEvents.Count(e => e!["vault"]?.GetValue<string>() == "Beet"), hiddenEvents.Count);
    }

    /// <summary>A user whose own vault <c>Skills</c> made room for the vault Skills, signed in: its notice names
    /// <c>Skills (renamed)</c>. <paramref name="change"/> (if given) then acts in that session on the vault, now
    /// <c>Skills (renamed)</c>; the vault is marked discreet, and she signs in again.</summary>
    private async Task<(RestClient User, JsonNode SignIn)> RenamedSkillsAsync(Func<RestClient, Task>? change = null)
    {
        var users = Api.Services.GetRequiredService<UserCatalog>();
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, RestClient.Password, Roles.User, Ct);
        var (user, key, readAt) = (await users.TrySignInAsync(username, RestClient.Password, Ct))!.Value;
        var vault = await Api.Services.GetRequiredService<VaultCatalog>().CreateForUserAsync("Skills", user.Id, key, Ct, readAt);
        var (first, firstSession) = await SignInAsync(username);
        Assert.Equal("Skills (renamed)", first["skillsNotice"]!["renamed"]!.GetValue<string>());
        if (change is not null) await change(RestClient.Existing(Api, user, key, firstSession, "Skills (renamed)"));

        await MarkAsync(user.Id, key, vault.Id);
        var (view, session) = await SignInAsync(username);
        return (RestClient.Existing(Api, user, key, session, "Skills"), view);
    }

    private Task MarkAsync(string userId, byte[] userKey, params string[] ids) =>
        app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == userId, Builders<UserDocument>.Update.Set(u => u.DiscreetVaults, DiscreetVaults.SealList(userId, userKey, ids)), cancellationToken: Ct);

    private static Func<RestClient, Task> Rename(string name) => async client =>
    {
        using var renamed = await client.SendAsync(HttpMethod.Put, "/api/vaults/Skills (renamed)/name", new { name });
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
    };

    private async Task RevealAsync(RestClient user) =>
        Assert.True((await Api.Services.GetRequiredService<Sessions>().RevealAsync(user.Session, null, Ct)).Revealed);

    /// <summary>Hidden at the sign-in and in the session, then shown once revealed.</summary>
    private async Task HiddenUntilRevealedAsync(RestClient user, JsonNode signIn, bool alias = false)
    {
        Assert.Null(signIn["skillsNotice"]);
        Assert.Null(await SkillsNoticeAsync(user));

        await RevealAsync(user);
        var notice = (await SkillsNoticeAsync(user))!;
        Assert.Equal("Skills (renamed)", notice["renamed"]!.GetValue<string>());
        Assert.Equal(alias, notice["alias"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_Skills_notice_of_a_hidden_vault_is_left_out_until_revealed()
    {
        var (user, signIn) = await RenamedSkillsAsync();
        await HiddenUntilRevealedAsync(user, signIn);
    }

    /// <summary>The notice is tied to the vault, not to the name it gives: renamed since, in any spelling, it is still
    /// the hidden vault's.</summary>
    [Theory]
    [InlineData("skills (renamed)")]
    [InlineData("Werkstatt")]
    public async Task The_Skills_notice_of_a_hidden_vault_renamed_since_is_left_out_until_revealed(string rename)
    {
        var (user, signIn) = await RenamedSkillsAsync(Rename(rename));
        await HiddenUntilRevealedAsync(user, signIn);
    }

    [Fact]
    public async Task The_Skills_notice_of_a_hidden_vault_that_is_gone_is_left_out_until_revealed()
    {
        var (user, signIn) = await RenamedSkillsAsync(async client =>
        {
            using var deleted = await client.SendAsync(
                HttpMethod.Post, "/api/vaults/Skills (renamed)/delete", new { password = RestClient.Password, confirm = "Skills (renamed)" });
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        });
        await HiddenUntilRevealedAsync(user, signIn);
    }

    /// <summary>A notice kept before it named the vault's id is judged by the name it gives.</summary>
    [Fact]
    public async Task A_Skills_notice_without_its_vaults_id_is_left_out_by_its_name()
    {
        var (user, _) = await RenamedSkillsAsync();
        var legacy = VaultCrypto.Seal(VaultCrypto.Derive(user.UserKey, "kumbu/v1/skills-notice", Encoding.UTF8.GetBytes(user.User.Id)),
            "{\"NewName\":\"Skills (renamed)\",\"Alias\":false}"u8, $"skills-notice:{user.User.Id}");
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == user.User.Id, Builders<UserDocument>.Update.Set(u => u.SkillsNotice, legacy), cancellationToken: Ct);
        var (signIn, session) = await SignInAsync(user.Username);
        await HiddenUntilRevealedAsync(RestClient.Existing(Api, user.User, user.UserKey, session, "Skills"), signIn);
    }

    [Fact]
    public async Task The_Skills_notice_of_a_hidden_shared_vault_is_left_out_until_revealed()
    {
        // Bob's Work, shared with Alice, who named it Skills before the upgrade, so she has no vault Skills yet.
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var alice = await RestClient.NewAsync(Api, vault: "Privat");
        var membership = await bob.ShareWithAsync(alice, alias: "Garden");
        var catalog = Api.Services.GetRequiredService<VaultCatalog>();
        var alicesSkills = (await catalog.OpenForUserAsync(alice.User.Id, alice.UserKey, Ct)).Single(a => a.Vault.Kind == VaultKinds.Skills);
        await catalog.DeleteVaultsAsync([alicesSkills.Id], (_, _) => Task.CompletedTask, Ct);
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == alice.User.Id, Builders<UserDocument>.Update.Unset(u => u.SkillsAt), cancellationToken: Ct);
        Api.Services.GetRequiredService<Kumbu.Server.Skills.SkillsVaults>().Forget(alice.User.Id);
        await catalog.RenameAliasAsync(alice.User.Id, alice.UserKey, membership, "Skills", Ct);

        await MarkAsync(alice.User.Id, alice.UserKey, await bob.VaultIdAsync());
        var (signIn, session) = await SignInAsync(alice.Username);
        await HiddenUntilRevealedAsync(RestClient.Existing(Api, alice.User, alice.UserKey, session, "Skills"), signIn, alias: true);
    }

    /// <summary>Dismissing it while it is hidden answers as dismissing no notice, and leaves it.</summary>
    [Fact]
    public async Task Dismissing_a_hidden_Skills_notice_leaves_it()
    {
        var (user, _) = await RenamedSkillsAsync();
        using (var dismissed = await user.SendAsync(HttpMethod.Delete, "/api/account/skills-notice"))
        {
            Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        }

        await RevealAsync(user);
        Assert.Equal("Skills (renamed)", (await SkillsNoticeAsync(user))!["renamed"]!.GetValue<string>());
    }

    private async Task<(JsonNode View, string Session)> SignInAsync(string username)
    {
        using var answer = await RestClient.SendAsync(Api, HttpMethod.Post, "/api/session", new { username, password = RestClient.Password }, session: null, bearer: null);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var cookie = answer.Headers.GetValues("Set-Cookie").First(v => v.StartsWith(CallerMiddleware.SessionCookie + "=", StringComparison.Ordinal));
        return (await RestClient.JsonAsync(answer), cookie[(CallerMiddleware.SessionCookie.Length + 1)..].Split(';')[0]);
    }

    private static async Task<JsonNode?> SkillsNoticeAsync(RestClient user)
    {
        using var response = await user.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response))["skillsNotice"];
    }

    [Fact]
    public async Task The_Skills_notice_of_a_vault_in_sight_stays()
    {
        var users = Api.Services.GetRequiredService<UserCatalog>();
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, RestClient.Password, Roles.User, Ct);
        var (owner, ownerKey, readAt) = (await users.TrySignInAsync(username, RestClient.Password, Ct))!.Value;
        var catalog = Api.Services.GetRequiredService<VaultCatalog>();
        await catalog.CreateForUserAsync("Skills", owner.Id, ownerKey, Ct, readAt);
        var tagebuch = await catalog.CreateForUserAsync("Tagebuch", owner.Id, ownerKey, Ct, readAt);
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == owner.Id, Builders<UserDocument>.Update.Set(u => u.DiscreetVaults, DiscreetVaults.SealList(owner.Id, ownerKey, [tagebuch.Id])), cancellationToken: Ct);
        var (view, _) = await SignInAsync(username);
        Assert.Equal("Skills (renamed)", view["skillsNotice"]!["renamed"]!.GetValue<string>());
    }
}
