// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>The vault marked as the vault Skills: its kind is stored in the vault's document, survives loading, and
/// is named to its owner only (the skills design, the vault kind).</summary>
[Collection("app")]
public sealed class SkillsVaultKindTests(KumbuAppFactory app)
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    private async Task<(User User, byte[] Key, DateTime ReadAt)> NewUserAsync()
    {
        var users = Api.Services.GetRequiredService<UserCatalog>();
        var username = "s" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, Password, Roles.User, Ct);
        return (await users.TrySignInAsync(username, Password, Ct))!.Value;
    }

    [Fact]
    public async Task A_vault_created_as_skills_carries_the_kind_in_its_document()
    {
        var (user, key, _) = await NewUserAsync();
        var skills = await Catalog.CreateForUserAsync("Skills", user.Id, key, Ct, kind: VaultKinds.Skills);
        var other = await Catalog.CreateForUserAsync("Private", user.Id, key, Ct);

        var vaults = app.Database.GetCollection<BsonDocument>("vaults");
        var skillsDoc = await vaults.Find(new BsonDocument("_id", skills.Id)).SingleAsync(Ct);
        var otherDoc = await vaults.Find(new BsonDocument("_id", other.Id)).SingleAsync(Ct);

        Assert.Equal("skills", skillsDoc["kind"].AsString);
        Assert.False(otherDoc.Contains("kind"));
    }

    [Fact]
    public async Task The_kind_survives_loading_the_vault_again()
    {
        var world = new VaultWorld(app.NewDatabase());
        var key = VaultCrypto.NewKey();
        await world.Catalog().CreateForUserAsync("Skills", "u1", key, Ct, kind: VaultKinds.Skills);
        await world.Catalog().CreateForUserAsync("Private", "u1", key, Ct);

        var opened = await world.Catalog().OpenForUserAsync("u1", key, Ct);

        Assert.Equal(VaultKinds.Skills, opened.Single(a => a.Name == "Skills").Vault.Kind);
        Assert.Null(opened.Single(a => a.Name == "Private").Vault.Kind);
    }

    [Fact]
    public async Task SkillsVaultAsync_names_the_marked_vault_only()
    {
        var (user, key, _) = await NewUserAsync();
        await Catalog.CreateForUserAsync("Skills", user.Id, key, Ct, kind: VaultKinds.Skills);
        var (plainUser, plainKey, _) = await NewUserAsync();
        // Named like the marked vault, but not marked: the name alone does not make it the vault Skills.
        await Catalog.CreateForUserAsync("Skills", plainUser.Id, plainKey, Ct);
        var work = await Catalog.CreateForUserAsync("Work", plainUser.Id, plainKey, Ct);
        var (apiKey, info) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            plainUser, plainKey, DateTime.UtcNow, "agent", new Dictionary<string, KeyRight> { [work.Id] = KeyRight.Write }, Ct);

        var owner = new EncryptedNoteStore(Catalog, new UserCredential(user.Id, key));
        var unmarked = new EncryptedNoteStore(Catalog, new UserCredential(plainUser.Id, plainKey));
        var keyWithoutSkills = new EncryptedNoteStore(Catalog, new ApiKeyCredential(info.Id, apiKey));

        Assert.Equal("Skills", await owner.SkillsVaultAsync(Ct));
        Assert.Null(await unmarked.SkillsVaultAsync(Ct));
        Assert.Null(await keyWithoutSkills.SkillsVaultAsync(Ct));
    }

    [Fact]
    public async Task GET_api_vaults_shows_the_kind_to_its_owner()
    {
        var (user, key, readAt) = await NewUserAsync();
        await Catalog.CreateForUserAsync("Skills", user.Id, key, Ct, kind: VaultKinds.Skills);
        await Catalog.CreateForUserAsync("Private", user.Id, key, Ct);
        var session = await Api.Services.GetRequiredService<Sessions>().StartAsync(user, key, readAt, Ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/vaults");
        request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");
        request.Headers.Add("Origin", KumbuAppFactory.PublicUrl);
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var vaults = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsArray();
        Assert.Equal("skills", vaults.Single(v => v!["name"]!.GetValue<string>() == "Skills")!["kind"]!.GetValue<string>());
        Assert.Null(vaults.Single(v => v!["name"]!.GetValue<string>() == "Private")!["kind"]);
    }
}
