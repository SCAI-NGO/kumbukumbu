// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>
/// The endpoints that find a vault by name or a membership by id without the credential (discreet vaults design,
/// Decision 11): a vault the session hides, or a membership of one, answers exactly as a name or id she never had, also
/// with the right password, and nothing happens to it.
/// </summary>
[Collection("app")]
public sealed class HiddenByNameTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private IMongoDatabase Db => Api.Services.GetRequiredService<IMongoDatabase>();

    [Fact]
    public async Task The_access_list_of_a_hidden_vault_answers_as_none()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.Alice.KeyAsync("Agent", Kumbu.Server.Accounts.KeyRight.Write);
        await w.HideAsync();

        await w.AsNowhereAsync("Tagebuch", HttpMethod.Get, "/api/vaults/{v}/access");
        await w.AsNowhereAsync("Familie", HttpMethod.Get, "/api/vaults/{v}/access");
        using var work = await w.Alice.GetAsync("/api/vaults/Work/access");
        Assert.Equal(HttpStatusCode.OK, work.StatusCode);
    }

    [Fact]
    public async Task The_owner_membership_endpoints_answer_as_for_no_vault()
    {
        var w = await HiddenWorld.NewAsync(Api);
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        var carols = await w.In("Tagebuch").ShareWithAsync(carol, "Tagebuch", MemberRole.Read, label: "Carol");
        await w.HideAsync();

        foreach (var id in new[] { carols, "m" + Guid.NewGuid().ToString("N") })
        {
            await w.AsNowhereAsync("Tagebuch", HttpMethod.Get, $"/api/vaults/{{v}}/memberships/{id}");
            await w.AsNowhereAsync("Tagebuch", HttpMethod.Post, $"/api/vaults/{{v}}/memberships/{id}/confirm", _ => new { });
            await w.AsNowhereAsync("Tagebuch", HttpMethod.Post, $"/api/vaults/{{v}}/memberships/{id}/decline", _ => new { });
            await w.AsNowhereAsync("Tagebuch", HttpMethod.Put, $"/api/vaults/{{v}}/memberships/{id}/role", _ => new { role = "write" });
            await w.AsNowhereAsync("Tagebuch", HttpMethod.Post, $"/api/vaults/{{v}}/memberships/{id}/revoke", _ => new { });
            await w.AsNowhereAsync("Tagebuch", HttpMethod.Delete, $"/api/vaults/{{v}}/memberships/{id}");
        }

        var membership = await Share.MembershipAsync(Db, carols);
        Assert.Equal((MembershipStatus.Active, "read"), (membership.Status, membership.Role));
    }

    [Fact]
    public async Task Deleting_a_hidden_vault_with_the_right_password_answers_as_for_no_vault()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.HideAsync();

        await w.AsNowhereAsync("Tagebuch", HttpMethod.Post, "/api/vaults/{v}/delete", v => new { password = RestClient.Password, confirm = v });

        await w.RevealAsync();
        Assert.Contains(("Tagebuch", true), await w.VaultsAsync());
    }

    [Fact]
    public async Task Deleting_a_hidden_shared_vault_answers_as_for_no_vault()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.HideAsync();

        await w.AsNowhereAsync("Familie", HttpMethod.Post, "/api/vaults/{v}/delete", v => new { password = RestClient.Password, confirm = v });
    }

    [Fact]
    public async Task Re_encrypting_a_hidden_vault_with_the_right_password_answers_as_for_no_vault()
    {
        var w = await HiddenWorld.NewAsync(Api);
        var key = await w.In("Tagebuch").KeyAsync("Agent", Kumbu.Server.Accounts.KeyRight.Write);
        await w.HideAsync();

        await w.AsNowhereAsync("Tagebuch", HttpMethod.Post, "/api/vaults/{v}/rekey", _ => new { password = RestClient.Password });

        // Not re-encrypted: the key with the vault still reads it.
        Assert.False((await w.Alice.ShAsync("cat /Tagebuch/geheim.md", key)).IsError);
    }

    [Fact]
    public async Task Inviting_to_a_hidden_vault_with_the_right_password_answers_as_for_no_vault()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.HideAsync();

        await w.AsNowhereAsync("Tagebuch", HttpMethod.Post, "/api/vaults/{v}/invitations", _ => new { password = RestClient.Password, label = "Carol", role = "read" });

        Assert.Equal(0, await Db.GetCollection<MembershipDocument>(Memberships.CollectionName).CountDocumentsAsync(m => m.Vault == w.TagebuchId, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_hidden_vault_s_membership_answers_as_one_she_does_not_have()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.HideAsync();
        var unknown = "m" + Guid.NewGuid().ToString("N");

        await AsUnknownAsync(w, HttpMethod.Put, "alias", w.FamilieMembership, unknown, new { alias = "Umbenannt" });
        await AsUnknownAsync(w, HttpMethod.Post, "provider-seen", w.FamilieMembership, unknown, new { provider = "internal" });
        await AsUnknownAsync(w, HttpMethod.Post, "leave", w.FamilieMembership, unknown, new { });

        Assert.Equal(MembershipStatus.Active, (await Share.MembershipAsync(Db, w.FamilieMembership)).Status);
        await w.RevealAsync();
        Assert.Contains(("Familie", true), await w.VaultsAsync());
    }

    /// <summary>The request on <paramref name="id"/> answers as on <paramref name="unknown"/>, once that id is replaced
    /// by the other in the answer.</summary>
    private static async Task AsUnknownAsync(HiddenWorld w, HttpMethod method, string action, string id, string unknown, object body)
    {
        using var hidden = await w.Alice.SendAsync(method, $"/api/memberships/{id}/{action}", body);
        using var none = await w.Alice.SendAsync(method, $"/api/memberships/{unknown}/{action}", body);
        Assert.Equal(
            (none.StatusCode, (await none.Content.ReadAsStringAsync(Ct)).Replace(unknown, id, StringComparison.Ordinal)),
            (hidden.StatusCode, await hidden.Content.ReadAsStringAsync(Ct)));
    }
}
