// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Search;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Search;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// A changed embedding provider and the member's consent (collab design §8.5, §13 M5): until Bob OKs the provider the
/// owner chose, the shared vault is left out of his searches by meaning, so none of his queries go there.
/// </summary>
[Collection("app")]
public sealed class ProviderConsentTests(KumbuAppFactory app) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeEmbedder _remote = new(model: "remote:qwen3@64");

    private WebApplicationFactory<Program>? _api;

    /// <summary>The public instance with one external provider, "remote", a fake that sees every text sent to it.</summary>
    private WebApplicationFactory<Program> Api => _api ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(services =>
    {
        services.RemoveAll<EmbeddingProviders>();
        services.AddSingleton(sp => EmbeddingProviders.Of(
            sp.GetRequiredService<IEmbeddingDriver>(), new EmbeddingProvider("remote", "Remote AI", "EU, zero data retention", _remote)));
    }));

    public async ValueTask DisposeAsync()
    {
        if (_api is not null) await _api.DisposeAsync();
    }

    private Task IndexedAsync() => Api.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

    /// <summary>Alice's "Gartenarbeit" (a note on hens) shared with Bob as "garten"; Bob's own "Work"; then Alice
    /// points her vault at the remote provider.</summary>
    private async Task<(RestClient Alice, RestClient Bob, string MembershipId, JsonNode Answer)> ChangedAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("diary.md", "# Garden\n\nThe hens laid eggs today.\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await bob.CreateAsync("gate.md", "# Gate\n\nThe gate service checks the rules at the entrance.\n");
        var membershipId = await alice.ShareWithAsync(bob, "garten", MemberRole.Write);
        using var chosen = await alice.SendAsync(HttpMethod.Put, "/api/vaults/Gartenarbeit/embeddings", new { provider = "remote" });
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        var answer = await RestClient.JsonAsync(chosen);
        await IndexedAsync();
        return (alice, bob, membershipId, answer);
    }

    private static async Task<JsonNode> SearchAsync(RestClient user, string query)
    {
        using var response = await user.GetAsync(query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RestClient.JsonAsync(response);
    }

    private static IEnumerable<string> Paths(JsonNode found) => found["results"]!.AsArray().Select(r => r!["path"]!.GetValue<string>());

    private static async Task<JsonNode> GartenAsync(RestClient bob)
    {
        using var response = await bob.GetAsync("/api/vaults");
        return (await RestClient.JsonAsync(response)).AsArray().Single(v => v!["name"]!.GetValue<string>() == "garten")!;
    }

    private bool QuerySent(string query) => _remote.Seen.Contains((query, true));

    [Fact]
    public async Task A_changed_provider_leaves_the_shared_vault_out_of_Bobs_searches()
    {
        var (alice, bob, _, _) = await ChangedAsync();
        var key = (await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [await alice.VaultIdAsync()] = KeyRight.Write, [await bob.VaultIdAsync()] = KeyRight.Write }, Ct)).Key;

        // Across his vaults, and of the shared vault alone; the web app's search and his agent's.
        var everywhere = await SearchAsync(bob, "/api/search?q=hens%20laying&mode=meaning");
        var vaultOnly = await SearchAsync(bob, "/api/search?q=hens%20nesting&mode=meaning&path=/garten");
        var (_, agentEverywhere) = await bob.ShAsync("semsearch hens brooding", key);
        var (agentFailed, agentVault) = await bob.ShAsync("semsearch --meaning hens roosting /garten", key);

        Assert.DoesNotContain("/garten/diary.md", Paths(everywhere));
        Assert.Contains("garten: its owner changed its embedding provider to 'remote', and your searches don't go there until you OK the change", everywhere["notice"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(Paths(vaultOnly));
        Assert.Contains("its owner changed its embedding provider", vaultOnly["notice"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("by words only in garten: its owner changed its embedding provider", agentEverywhere, StringComparison.Ordinal);
        Assert.True(agentFailed);
        Assert.Contains("its owner changed its embedding provider", agentVault, StringComparison.Ordinal);
        // Not one of his queries reached the owner's provider.
        Assert.DoesNotContain(_remote.Seen, s => s.Query);
    }

    [Fact]
    public async Task Bobs_OK_brings_it_back()
    {
        var (_, bob, membershipId, _) = await ChangedAsync();
        var garten = await GartenAsync(bob);
        Assert.Equal((true, membershipId, "remote"), (garten["providerChanged"]!.GetValue<bool>(), garten["membership"]!.GetValue<string>(), garten["embeddings"]!.GetValue<string>()));

        using var ok = await bob.SendAsync(HttpMethod.Post, $"/api/memberships/{membershipId}/provider-seen", new { provider = "remote" });

        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.False((await GartenAsync(bob))["providerChanged"]!.GetValue<bool>());
        var found = await SearchAsync(bob, "/api/search?q=hens%20laying&mode=meaning&path=/garten");
        Assert.Equal("/garten/diary.md", Paths(found).First());
        Assert.True(QuerySent("hens laying"));
    }

    [Fact]
    public async Task Reading_and_writing_go_on_meanwhile()
    {
        var (_, bob, _, _) = await ChangedAsync();

        using (var read = await bob.GetAsync("/api/notes/garten/diary.md"))
        {
            Assert.Contains("hens", (await RestClient.JsonAsync(read))["content"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        using var written = await bob.SendAsync(HttpMethod.Put, "/api/notes/garten/kohl.md", new { content = "Kohl\n" });
        Assert.Equal(HttpStatusCode.Created, written.StatusCode);
        // By words, nothing leaves the server: his notes are found.
        Assert.Contains("/garten/diary.md", Paths(await SearchAsync(bob, "/api/search?q=hens&mode=exact")));
        Assert.True((await GartenAsync(bob))["providerChanged"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_owners_answer_counts_her_members()
    {
        var (alice, bob, _, answer) = await ChangedAsync();
        Assert.Equal(1, answer["members"]!.GetValue<int>());
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        await alice.ShareWithAsync(carol, "Beete", MemberRole.Read, label: "Carol");
        // An open invitation is nobody yet.
        await Api.Services.GetRequiredService<Memberships>().InviteAsync(alice.User.Id, alice.UserKey, await alice.VaultIdAsync(), "Dan", MemberRole.Read, TimeSpan.FromDays(7), false, Ct);

        using var back = await alice.SendAsync(HttpMethod.Put, "/api/vaults/Gartenarbeit/embeddings", new { provider = "internal" });

        Assert.Equal(2, (await RestClient.JsonAsync(back))["members"]!.GetValue<int>());
        // A member sees no count, and her own vaults count nobody.
        Assert.Null((await GartenAsync(bob))["members"]);
        using var bobs = await bob.GetAsync("/api/vaults");
        Assert.Equal(0, (await RestClient.JsonAsync(bobs)).AsArray().Single(v => v!["name"]!.GetValue<string>() == "Work")!["members"]!.GetValue<int>());
    }

    [Fact]
    public async Task Provider_seen_is_bound_to_the_caller()
    {
        var (alice, bob, membershipId, _) = await ChangedAsync();
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        await alice.ShareWithAsync(carol, "Beete", MemberRole.Read, label: "Carol");

        using var byCarol = await carol.SendAsync(HttpMethod.Post, $"/api/memberships/{membershipId}/provider-seen", new { provider = "remote" });
        using var byAlice = await alice.SendAsync(HttpMethod.Post, $"/api/memberships/{membershipId}/provider-seen", new { provider = "remote" });
        using var unknown = await bob.SendAsync(HttpMethod.Post, $"/api/memberships/{membershipId}/provider-seen", new { provider = "openai" });

        Assert.Equal(HttpStatusCode.NotFound, byCarol.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byAlice.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var doc = await app.Database.GetCollection<MembershipDocument>(Memberships.CollectionName).Find(m => m.Id == membershipId).SingleAsync(Ct);
        Assert.Equal("internal", doc.ProviderSeen);
        Assert.True((await GartenAsync(bob))["providerChanged"]!.GetValue<bool>());
    }
}
