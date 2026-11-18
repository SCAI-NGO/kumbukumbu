// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Search;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Discreet;
using Kumbu.Server.Tests.Search;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kumbu.Server.Tests.Api;

/// <summary>People's own TensorX keys over REST (BYOK spec, "API"): the key, the vaults' choice, new vaults, discreet
/// vaults, members, and that the key never shows.</summary>
[Collection("app")]
public sealed class ByokApiTests(KumbuAppFactory app) : IAsyncDisposable
{
    private const string Key = "sk-tensorx-own-key-0001";
    private const string NewKey = "sk-tensorx-new-key-0002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ByokDriversTests.TensorX _tensorx = new();
    private readonly CapturedLog _log = new();
    private readonly List<WebApplicationFactory<Program>> _apis = [];

    /// <summary>The public instance with BYOK offered (or, with <paramref name="byok"/> false, not), TensorX faked.</summary>
    private WebApplicationFactory<Program> Api(bool byok = true, string? @default = null)
    {
        var api = app.PublicApi.WithWebHostBuilder(b =>
        {
            b.UseSetting("Kumbu:Embeddings:Byok", byok ? "true" : "false");
            if (@default is not null)
            {
                b.UseSetting("Kumbu:Embeddings:Providers:wire:Driver", "openai-compatible");
                b.UseSetting("Kumbu:Embeddings:Providers:wire:BaseUrl", "http://127.0.0.1:9/v1");
                b.UseSetting("Kumbu:Embeddings:Providers:wire:Model", "test-model");
                b.UseSetting("Kumbu:Embeddings:Default", @default);
            }

            b.ConfigureLogging(l => l.AddProvider(_log));
            b.ConfigureServices(services => services.AddHttpClient(EmbeddingDrivers.HttpClientName(Byok.Provider)).ConfigurePrimaryHttpMessageHandler(() => _tensorx));
        });
        _apis.Add(api);
        return api;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var api in _apis) await api.DisposeAsync();
    }

    private static Task IndexedAsync(RestClient user) => user.Api.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

    private static async Task<HttpResponseMessage> SaveKeyAsync(RestClient user, string key, string? bearer = null) =>
        await user.SendAsync(HttpMethod.Put, "/api/account/embeddings-key", new { key }, bearer);

    private static async Task<JsonNode> KeyViewAsync(RestClient user)
    {
        using var response = await user.GetAsync("/api/account/embeddings-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RestClient.JsonAsync(response);
    }

    private static async Task<HttpResponseMessage> ChooseAsync(RestClient user, string vault, string provider) =>
        await user.SendAsync(HttpMethod.Put, $"/api/vaults/{Uri.EscapeDataString(vault)}/embeddings", new { provider });

    private static async Task<string> ProviderOfAsync(RestClient user, string vault)
    {
        using var list = await user.GetAsync("/api/vaults");
        return (await RestClient.JsonAsync(list)).AsArray().Single(v => v!["name"]!.GetValue<string>() == vault)!["embeddings"]!.GetValue<string>();
    }

    private static async Task<JsonNode?> ByokEntryAsync(RestClient user)
    {
        using var list = await user.GetAsync("/api/embeddings");
        return (await RestClient.JsonAsync(list)).AsArray().SingleOrDefault(p => p!["name"]!.GetValue<string>() == Byok.Provider);
    }

    private async Task<RestClient> WithKeyAsync(string vault = "Garten")
    {
        var user = await RestClient.NewAsync(Api(), vault);
        using var saved = await SaveKeyAsync(user, Key);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return user;
    }

    [Fact]
    public async Task The_list_offers_tensorx_and_asks_for_a_key_until_one_is_saved()
    {
        var user = await RestClient.NewAsync(Api());

        var before = await ByokEntryAsync(user);
        Assert.NotNull(before);
        Assert.Equal(("TensorX (your own key)", true, true, false), (before["label"]!.GetValue<string>(), before["external"]!.GetValue<bool>(), before["needsKey"]!.GetValue<bool>(), before["available"]!.GetValue<bool>()));
        var view = await KeyViewAsync(user);
        Assert.Equal((true, false), (view["offered"]!.GetValue<bool>(), view["keySaved"]!.GetValue<bool>()));

        using var saved = await SaveKeyAsync(user, Key);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal((true, "0001"), ((await RestClient.JsonAsync(saved))["keySaved"]!.GetValue<bool>(), (await RestClient.JsonAsync(saved))["last4"]!.GetValue<string>()));
        var after = await ByokEntryAsync(user);
        Assert.Equal((false, true), (after!["needsKey"]!.GetValue<bool>(), after["available"]!.GetValue<bool>()));
        Assert.Equal("0001", (await KeyViewAsync(user))["last4"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_malformed_refused_or_unchecked_key_is_not_saved()
    {
        var user = await RestClient.NewAsync(Api());
        _tensorx.Refused.Add("sk-refused-key-0003");

        using var malformed = await SaveKeyAsync(user, "short");
        using var refused = await SaveKeyAsync(user, "sk-refused-key-0003");
        _tensorx.Down = true;
        using var unreachable = await SaveKeyAsync(user, Key);
        _tensorx.Down = false;

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("TensorX refused this key.", await RestClient.ErrorAsync(refused));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unreachable.StatusCode);
        Assert.False((await KeyViewAsync(user))["keySaved"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Without_the_switch_or_with_an_external_default_nothing_is_offered()
    {
        foreach (var api in new[] { Api(byok: false), Api(byok: true, @default: "wire") })
        {
            var user = await RestClient.NewAsync(api);

            Assert.Null(await ByokEntryAsync(user));
            Assert.False((await KeyViewAsync(user))["offered"]!.GetValue<bool>());
            using var saved = await SaveKeyAsync(user, Key);
            Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
            using var chosen = await ChooseAsync(user, user.Vault, Byok.Provider);
            Assert.Equal(HttpStatusCode.BadRequest, chosen.StatusCode);
        }
    }

    [Fact]
    public async Task Only_a_session_sets_the_key()
    {
        var user = await RestClient.NewAsync(Api());
        var bearer = await user.KeyAsync("agent", KeyRight.Write);

        using var byAgent = await SaveKeyAsync(user, Key, bearer);

        Assert.Equal(HttpStatusCode.Forbidden, byAgent.StatusCode);
        Assert.False((await KeyViewAsync(user))["keySaved"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_vault_needs_the_key_and_is_then_embedded_and_searched_with_it()
    {
        var user = await RestClient.NewAsync(Api(), "Garten");
        await user.CreateAsync("hens.md", "# Hens\n\nThe hens laid eggs today.\n");
        using (var early = await ChooseAsync(user, "Garten", Byok.Provider))
        {
            Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
            Assert.Equal("Save your TensorX key in Settings, Vaults first.", await RestClient.ErrorAsync(early));
        }

        using (await SaveKeyAsync(user, Key))
        {
        }

        using var chosen = await ChooseAsync(user, "Garten", Byok.Provider);
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        await IndexedAsync(user);
        using var search = await user.GetAsync("/api/search?q=eggs&mode=meaning&path=/Garten");

        Assert.Equal("/Garten/hens.md", (await RestClient.JsonAsync(search))["results"]![0]!["path"]!.GetValue<string>());
        Assert.Equal(Byok.Provider, await ProviderOfAsync(user, "Garten"));
        Assert.Contains(Key, _tensorx.Keys);
    }

    [Fact]
    public async Task With_a_key_a_new_vault_starts_on_tensorx()
    {
        var user = await WithKeyAsync();

        using var created = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Ideen" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(Byok.Provider, (await RestClient.JsonAsync(created))["embeddings"]!.GetValue<string>());
        Assert.Equal(Byok.Provider, await ProviderOfAsync(user, "Ideen"));
        Assert.Equal("internal", await ProviderOfAsync(user, "Garten"));
    }

    [Fact]
    public async Task Replacing_the_key_reaches_the_vaults_and_removing_it_brings_them_back()
    {
        var user = await WithKeyAsync();
        using (await ChooseAsync(user, "Garten", Byok.Provider))
        {
        }

        using (var replaced = await SaveKeyAsync(user, NewKey))
        {
            Assert.Equal("0002", (await RestClient.JsonAsync(replaced))["last4"]!.GetValue<string>());
        }

        _tensorx.Keys.Clear();
        await user.CreateAsync("ducks.md", "# Ducks\n\nThe ducks swam in the pond.\n");
        await IndexedAsync(user);
        Assert.NotEmpty(_tensorx.Keys);
        Assert.All(_tensorx.Keys, k => Assert.Equal(NewKey, k));

        using var removed = await user.SendAsync(HttpMethod.Delete, "/api/account/embeddings-key");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal("internal", await ProviderOfAsync(user, "Garten"));
        Assert.False((await KeyViewAsync(user))["keySaved"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_key_survives_a_rename_and_a_new_user_key()
    {
        var user = await WithKeyAsync();
        using (await ChooseAsync(user, "Garten", Byok.Provider))
        {
        }

        using (var renamed = await user.SendAsync(HttpMethod.Put, "/api/vaults/Garten/name", new { name = "Beete" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        using (var reencrypted = await user.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, reencrypted.StatusCode);
        }

        Assert.Equal("0001", (await KeyViewAsync(user))["last4"]!.GetValue<string>());
        Assert.Equal(Byok.Provider, await ProviderOfAsync(user, "Beete"));
    }

    [Fact]
    public async Task A_discreet_vault_never_uses_tensorx()
    {
        var w = await HiddenWorld.NewAsync(Api());
        using (await SaveKeyAsync(w.Alice, Key))
        {
        }

        using (var chosen = await ChooseAsync(w.Alice, "Tagebuch", Byok.Provider))
        {
            Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        }

        await w.RevealAsync();
        using (var marked = await w.Alice.SendAsync(HttpMethod.Put, "/api/account/discreet/vaults/Tagebuch", new { discreet = true }))
        {
            Assert.Equal(HttpStatusCode.NoContent, marked.StatusCode);
        }

        Assert.Equal("internal", await ProviderOfAsync(w.Alice, "Tagebuch"));
        using var again = await ChooseAsync(w.Alice, "Tagebuch", Byok.Provider);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task A_member_searches_a_tensorx_vault_after_consent_with_the_owners_key()
    {
        var alice = await WithKeyAsync("Gartenarbeit");
        await alice.CreateAsync("hens.md", "# Hens\n\nThe hens laid eggs today.\n");
        var bob = await RestClient.NewAsync(alice.Api, "Work");
        var membership = await alice.ShareWithAsync(bob, "garten");
        using (await ChooseAsync(alice, "Gartenarbeit", Byok.Provider))
        {
        }

        await IndexedAsync(alice);
        using (var ok = await bob.SendAsync(HttpMethod.Post, $"/api/memberships/{membership}/provider-seen", new { provider = Byok.Provider }))
        {
            Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        }

        _tensorx.Keys.Clear();
        using var search = await bob.GetAsync("/api/search?q=eggs&mode=meaning&path=/garten");

        Assert.Equal("/garten/hens.md", (await RestClient.JsonAsync(search))["results"]![0]!["path"]!.GetValue<string>());
        Assert.Equal([Key], _tensorx.Keys);
    }

    [Fact]
    public async Task The_key_never_appears_in_answers_or_the_log()
    {
        var user = await WithKeyAsync();
        using (await ChooseAsync(user, "Garten", Byok.Provider))
        {
        }

        await user.CreateAsync("hens.md", "# Hens\n\nThe hens laid eggs today.\n");
        await IndexedAsync(user);

        foreach (var path in new[] { "/api/account/embeddings-key", "/api/embeddings", "/api/vaults", "/api/search?q=eggs&mode=meaning" })
        {
            using var response = await user.GetAsync(path);
            Assert.DoesNotContain(Key, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(_log.Lines, l => l.Contains(Key, StringComparison.Ordinal));
    }
}
