// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
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
/// the notice of the vault Skills is left out while the name it gives is a hidden vault's. Revealed, all are back.
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
    public async Task The_notices_of_hidden_vaults_are_left_out_until_revealed()
    {
        var world = await NoticeWorldAsync();
        var (hidden, body) = await ListAsync(world.Alice, "/api/member-notices");
        Assert.Equal("Beet", hidden.Single()!["vault"]!.GetValue<string>());
        Assert.DoesNotContain("Familie", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Garten", body, StringComparison.Ordinal);

        // Dismissing one answers exactly as an id she has no notice of, and leaves it.
        var familie = (await Notices.Find(n => n.User == world.Alice.User.Id && n.Vault == world.FamilieId).SingleAsync(Ct)).Id.ToString();
        Assert.Equal(await DismissAsync(world.Alice, ObjectId.GenerateNewId().ToString()), await DismissAsync(world.Alice, familie));

        await world.RevealAsync();
        var (shown, _) = await ListAsync(world.Alice, "/api/member-notices");
        Assert.Equal(["Beet", "Familie", "Garten"], shown.Select(n => n!["vault"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal(HttpStatusCode.NoContent, (await DismissAsync(world.Alice, familie)).Item1);
    }

    [Fact]
    public async Task The_events_of_hidden_vaults_are_left_out_until_revealed()
    {
        var world = await NoticeWorldAsync();
        await world.RevealAsync();
        var (shown, _) = await ListAsync(world.Alice, "/api/vault-events");
        // Laube's removal names it no more: she no longer reaches it.
        Assert.Contains(shown, e => e!["vault"] is null && e["kind"]!.GetValue<string>() == VaultEvents.MembershipRevoked);
        Assert.Contains(shown, e => e!["vault"]?.GetValue<string>() == "Garten");
        Assert.Contains(shown, e => e!["vault"]?.GetValue<string>() == "Beet");

        var hiding = await world.Alice.SignInAgainAsync();
        var (hidden, body) = await ListAsync(hiding, "/api/vault-events");
        Assert.NotEmpty(hidden);
        Assert.All(hidden, e => Assert.Equal("Beet", e!["vault"]?.GetValue<string>()));
        Assert.Equal(shown.Count(e => e!["vault"]?.GetValue<string>() == "Beet"), hidden.Count);
        Assert.DoesNotContain("Garten", body, StringComparison.Ordinal);
    }

    /// <summary>A user whose own vault <c>Skills</c> made room for the vault Skills, signed in: its notice names
    /// <c>Skills (renamed)</c>. She then renames it to <paramref name="rename"/> (if given) and marks it discreet.</summary>
    private async Task<(RestClient User, JsonNode SignIn)> RenamedSkillsAsync(string? rename)
    {
        var users = Api.Services.GetRequiredService<UserCatalog>();
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, RestClient.Password, Roles.User, Ct);
        var (user, key, readAt) = (await users.TrySignInAsync(username, RestClient.Password, Ct))!.Value;
        var vault = await Api.Services.GetRequiredService<VaultCatalog>().CreateForUserAsync("Skills", user.Id, key, Ct, readAt);
        var (first, _) = await SignInAsync(username);
        Assert.Equal("Skills (renamed)", first["skillsNotice"]!["renamed"]!.GetValue<string>());
        if (rename is not null)
        {
            var client = RestClient.Existing(Api, user, key, (await SignInAsync(username)).Session, "Skills (renamed)");
            using var renamed = await client.SendAsync(HttpMethod.Put, "/api/vaults/Skills (renamed)/name", new { name = rename });
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == user.Id, Builders<UserDocument>.Update.Set(u => u.DiscreetVaults, DiscreetVaults.SealList(user.Id, key, [vault.Id])), cancellationToken: Ct);
        var (view, session) = await SignInAsync(username);
        return (RestClient.Existing(Api, user, key, session, "Skills"), view);
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

    [Theory]
    [InlineData(null)]
    [InlineData("skills (renamed)")]
    public async Task The_Skills_notice_naming_a_hidden_vault_is_left_out_until_revealed(string? rename)
    {
        var (user, signIn) = await RenamedSkillsAsync(rename);
        Assert.Null(signIn["skillsNotice"]);
        Assert.Null(await SkillsNoticeAsync(user));

        Assert.True((await Api.Services.GetRequiredService<Sessions>().RevealAsync(user.Session, null, Ct)).Revealed);
        Assert.Equal("Skills (renamed)", (await SkillsNoticeAsync(user))!["renamed"]!.GetValue<string>());
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
