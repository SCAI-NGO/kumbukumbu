// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Skills;

/// <summary>The vault marked as the skills vault stays what it is: it isn't deleted, renamed, shared with members, nor
/// given "May publish" (a skill is shared as a whole, by link, from the Skills screen).</summary>
[Collection("app")]
public sealed class SkillsVaultFixedTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A user with a vault Work and the vault Skills, marked as such.</summary>
    private async Task<RestClient> AliceAsync()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var catalog = app.PublicApi.Services.GetRequiredService<VaultCatalog>();
        if (!(await catalog.OpenForUserAsync(alice.User.Id, alice.UserKey, Ct)).Any(v => v.Vault.Kind == VaultKinds.Skills))
        {
            await catalog.CreateForUserAsync("Skills", alice.User.Id, alice.UserKey, Ct, kind: VaultKinds.Skills);
        }

        return alice;
    }

    private static async Task<HashSet<string>> VaultNamesAsync(RestClient alice)
    {
        using var list = await alice.GetAsync("/api/vaults");
        return [.. (await RestClient.JsonAsync(list)).AsArray().Select(v => v!["name"]!.GetValue<string>())];
    }

    [Fact]
    public async Task The_vault_Skills_cant_be_deleted()
    {
        var alice = await AliceAsync();
        using var deleted = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Skills/delete", new { password = RestClient.Password, confirm = "Skills" });
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Equal("The vault Skills can't be deleted: it holds your skills. Remove single skills instead.", await RestClient.ErrorAsync(deleted));
        Assert.Contains("Skills", await VaultNamesAsync(alice));
    }

    [Fact]
    public async Task The_vault_Skills_cant_be_renamed()
    {
        var alice = await AliceAsync();
        using var renamed = await alice.SendAsync(HttpMethod.Put, "/api/vaults/Skills/name", new { name = "Mine" });
        Assert.Equal(HttpStatusCode.Conflict, renamed.StatusCode);
        Assert.Equal("The vault Skills keeps its name.", await RestClient.ErrorAsync(renamed));
        Assert.Contains("Skills", await VaultNamesAsync(alice));
    }

    [Fact]
    public async Task An_ordinary_vault_still_can_be_renamed()
    {
        var alice = await AliceAsync();
        using var renamed = await alice.SendAsync(HttpMethod.Put, "/api/vaults/Work/name", new { name = "Job" });
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Contains("Job", await VaultNamesAsync(alice));
    }

    [Fact]
    public async Task The_vault_Skills_cant_be_shared_with_members()
    {
        var alice = await AliceAsync();
        using var invited = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Skills/invitations", new { password = RestClient.Password, label = "Bob", role = "read" });
        Assert.Equal(HttpStatusCode.Conflict, invited.StatusCode);
        Assert.Equal("The vault Skills isn't shared with others. Share a single skill by link from the Skills screen.", await RestClient.ErrorAsync(invited));
        using var ordinary = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Work/invitations", new { password = RestClient.Password, label = "Bob", role = "read" });
        Assert.Equal(HttpStatusCode.Created, ordinary.StatusCode);
    }

    [Fact]
    public async Task May_publish_isnt_given_for_Skills()
    {
        const string message = "May publish isn't given for the vault Skills: a skill is shared from the Skills screen.";
        var alice = await AliceAsync();
        var vaults = new Dictionary<string, string> { ["Work"] = "write", ["Skills"] = "read" };

        using var key = await alice.SendAsync(HttpMethod.Post, "/api/keys", new { name = "agent", vaults, publish = new[] { "Skills" } });
        Assert.Equal(HttpStatusCode.BadRequest, key.StatusCode);
        Assert.Equal(message, await RestClient.ErrorAsync(key));

        using var connection = await alice.SendAsync(HttpMethod.Post, "/api/connections", new { name = "app", vaults, publish = new[] { "Skills" } });
        Assert.Equal(HttpStatusCode.BadRequest, connection.StatusCode);
        Assert.Equal(message, await RestClient.ErrorAsync(connection));

        using var created = await alice.SendAsync(HttpMethod.Post, "/api/connections", new { name = "app", vaults, publish = new[] { "Work" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
        using var set = await alice.SendAsync(HttpMethod.Put, $"/api/connections/{id}/vaults", new { vaults, publish = new[] { "Skills" } });
        Assert.Equal(HttpStatusCode.BadRequest, set.StatusCode);
        Assert.Equal(message, await RestClient.ErrorAsync(set));

        using var kept = await alice.SendAsync(HttpMethod.Put, $"/api/connections/{id}/vaults", new { vaults, publish = new[] { "Work" } });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
    }
}
