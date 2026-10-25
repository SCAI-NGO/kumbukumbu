// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Search;
using Kumbu.Server.Tests.Search;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Kumbu.Server.Tests.Api;

/// <summary>The embedding providers over REST: the list, a vault's choice (owner only), and search with mixed providers.</summary>
[Collection("app")]
public sealed class EmbeddingApiTests(KumbuAppFactory app) : IAsyncDisposable
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

    private static Task<JsonNode> JsonAsync(HttpResponseMessage response) => RestClient.JsonAsync(response);

    private static async Task<HttpResponseMessage> ChooseAsync(RestClient user, string provider, string? bearer = null) =>
        await user.SendAsync(HttpMethod.Put, $"/api/vaults/{Uri.EscapeDataString(user.Vault)}/embeddings", new { provider }, bearer);

    /// <summary>Answers every embeddings request with unit vectors of 8 values, and counts the requests.</summary>
    private sealed class EightValues : HttpMessageHandler
    {
        public int Requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var count = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["input"]!.AsArray().Count;
            var data = new JsonArray([.. Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject
            {
                ["index"] = i,
                ["embedding"] = new JsonArray([.. Enumerable.Range(0, 8).Select(d => (JsonNode)(d == 0 ? 1.0 : 0.0))]),
            })]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["data"] = data }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task A_search_through_a_real_driver_leaves_no_http_client_lines_in_the_log()
    {
        var keyFile = Path.Combine(Directory.CreateTempSubdirectory("kumbu-key-").FullName, "key");
        await File.WriteAllTextAsync(keyFile, "sk-test-not-a-real-key", Ct);
        var handler = new EightValues();
        var log = new CapturedLog();
        await using var api = app.PublicApi.WithWebHostBuilder(b =>
        {
            b.UseSetting("Kumbu:Embeddings:Providers:wire:Driver", "openai-compatible");
            b.UseSetting("Kumbu:Embeddings:Providers:wire:BaseUrl", "https://embeddings.example.org/v1");
            b.UseSetting("Kumbu:Embeddings:Providers:wire:Model", "test-model");
            b.UseSetting("Kumbu:Embeddings:Providers:wire:ApiKeyFile", keyFile);
            b.ConfigureLogging(l => l.AddProvider(log));
            b.ConfigureServices(services => services.AddHttpClient(EmbeddingDrivers.HttpClientName("wire")).ConfigurePrimaryHttpMessageHandler(() => handler));
        });
        var user = await RestClient.NewAsync(api, "Wire");
        await user.CreateAsync("diary.md", "# Garden\n\nThe hens laid eggs today.\n");
        using (var chosen = await user.SendAsync(HttpMethod.Put, "/api/vaults/Wire/embeddings", new { provider = "wire" }))
        {
            Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        }

        await api.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);
        using var search = await user.GetAsync("/api/search?q=hens&mode=meaning&path=/Wire");

        Assert.Equal("/Wire/diary.md", (await JsonAsync(search))["results"]![0]!["path"]!.GetValue<string>());
        Assert.True(handler.Requests >= 2);
        Assert.DoesNotContain(log.Lines, l => l.Contains("System.Net.Http", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("embeddings.example.org", StringComparison.Ordinal) || l.Contains("hens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_providers_are_listed_internal_first_with_what_an_owner_should_know()
    {
        var user = await RestClient.NewAsync(Api);

        using var response = await user.GetAsync("/api/embeddings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = (await JsonAsync(response)).AsArray();
        Assert.Equal(["internal", "remote"], list.Select(p => p!["name"]!.GetValue<string>()));
        Assert.Equal((false, true), (list[0]!["external"]!.GetValue<bool>(), list[1]!["external"]!.GetValue<bool>()));
        Assert.Equal(("Remote AI", "EU, zero data retention", true), (list[1]!["label"]!.GetValue<string>(), list[1]!["description"]!.GetValue<string>(), list[1]!["available"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task A_new_vault_is_internal_and_its_owner_opts_in_to_an_external_provider()
    {
        var user = await RestClient.NewAsync(Api, "Privat");
        await user.CreateAsync("diary.md", "# Garden\n\nThe hens laid eggs today.\n");
        await IndexedAsync();
        Assert.DoesNotContain(_remote.Seen, t => t.Text.Contains("eggs"));

        using (var list = await user.GetAsync("/api/vaults"))
        {
            Assert.Equal("internal", (await JsonAsync(list)).AsArray().Single()!["embeddings"]!.GetValue<string>());
        }

        using var chosen = await ChooseAsync(user, "remote");
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Equal(("Privat", "remote"), ((await JsonAsync(chosen))["name"]!.GetValue<string>(), (await JsonAsync(chosen))["embeddings"]!.GetValue<string>()));
        await IndexedAsync();

        // Re-embedded by the provider, and searched with it.
        Assert.Contains(_remote.Seen, t => t.Text.Contains("eggs") && !t.Query);
        using var search = await user.GetAsync("/api/search?q=hens&mode=meaning&path=/Privat");
        Assert.Equal("/Privat/diary.md", (await JsonAsync(search))["results"]![0]!["path"]!.GetValue<string>());
        Assert.Contains(("hens", true), _remote.Seen);

        using (var list = await user.GetAsync("/api/vaults"))
        {
            Assert.Equal("remote", (await JsonAsync(list)).AsArray().Single()!["embeddings"]!.GetValue<string>());
        }

        // And back: the notes are embedded on the server again.
        using var back = await ChooseAsync(user, "internal");
        Assert.Equal("internal", (await JsonAsync(back))["embeddings"]!.GetValue<string>());
        await IndexedAsync();
        Assert.Contains(app.Embedder.Seen, t => t.Text.Contains("eggs") && !t.Query);
    }

    [Fact]
    public async Task Only_the_owners_session_chooses_and_only_a_configured_provider()
    {
        var user = await RestClient.NewAsync(Api);
        var key = await user.KeyAsync("agent", KeyRight.Write);

        using var byKey = await ChooseAsync(user, "remote", bearer: key);
        using var unknown = await ChooseAsync(user, "openai");
        using var noVault = await user.SendAsync(HttpMethod.Put, "/api/vaults/Fremd/embeddings", new { provider = "remote" });
        var stranger = await RestClient.NewAsync(Api, "Fremd");
        using var others = await stranger.SendAsync(HttpMethod.Put, $"/api/vaults/{user.Vault}/embeddings", new { provider = "remote" });

        Assert.Equal(HttpStatusCode.Forbidden, byKey.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("no embedding provider 'openai'", await RestClient.ErrorAsync(unknown), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, noVault.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, others.StatusCode);

        using var list = await user.GetAsync("/api/vaults");
        Assert.Equal("internal", (await JsonAsync(list)).AsArray().Single()!["embeddings"]!.GetValue<string>());
    }

    [Fact]
    public async Task Across_vaults_the_notes_of_a_vault_whose_provider_is_down_are_found_by_words()
    {
        var user = await RestClient.NewAsync(Api, "Arbeit");
        await user.CreateAsync("gate.md", "# Gate\n\nThe gate service checks the rules at the entrance.\n");
        using (await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Garten" }))
        {
        }

        using (await user.SendAsync(HttpMethod.Put, "/api/notes/Garten/hens.md", new { content = "# Hens\n\nThe hens laid eggs today.\n" }))
        {
        }

        using (await user.SendAsync(HttpMethod.Put, "/api/vaults/Garten/embeddings", new { provider = "remote" }))
        {
        }

        await IndexedAsync();
        _remote.Failure = new EmbeddingUnavailableException("the embedding provider Remote AI answered 503 Service Unavailable", providerDown: true);
        try
        {
            using var hybrid = await user.GetAsync("/api/search?q=hens%20eggs");
            using var meaning = await user.GetAsync("/api/search?q=hens%20eggs&mode=meaning");

            var found = await JsonAsync(hybrid);
            var paths = found["results"]!.AsArray().Select(r => r!["path"]!.GetValue<string>()).ToList();
            Assert.Contains("/Garten/hens.md", paths);
            Assert.Contains("/Arbeit/gate.md", paths);
            Assert.Null(found["results"]!.AsArray().First(r => r!["path"]!.GetValue<string>() == "/Garten/hens.md")!["score"]);
            Assert.Equal(
                "Search by meaning isn't available for some vaults, so their notes are ranked by words (Garten: the embedding provider Remote AI answered 503 Service Unavailable).",
                found["notice"]!.GetValue<string>());

            var byMeaning = await JsonAsync(meaning);
            Assert.DoesNotContain(byMeaning["results"]!.AsArray(), r => r!["path"]!.GetValue<string>() == "/Garten/hens.md");
            Assert.StartsWith("Search by meaning isn't available for some vaults, so their notes aren't in these results", byMeaning["notice"]!.GetValue<string>());
        }
        finally
        {
            _remote.Failure = null;
        }
    }

    [Fact]
    public async Task A_provider_that_is_down_leaves_its_vault_to_the_words_and_says_so()
    {
        var user = await RestClient.NewAsync(Api, "Remote");
        await user.CreateAsync("diary.md", "# Garden\n\nThe hens laid eggs today.\n");
        using (await ChooseAsync(user, "remote"))
        {
        }

        await IndexedAsync();
        _remote.Failure = new EmbeddingUnavailableException("the embedding provider Remote AI answered 503 Service Unavailable", providerDown: true);
        try
        {
            using var hybrid = await user.GetAsync("/api/search?q=hens%20eggs&path=/Remote");
            using var meaning = await user.GetAsync("/api/search?q=hens&mode=meaning&path=/Remote");

            var words = await JsonAsync(hybrid);
            Assert.Equal("/Remote/diary.md", words["results"]![0]!["path"]!.GetValue<string>());
            Assert.Equal("Ranked by words only: the embedding provider Remote AI answered 503 Service Unavailable.", words["notice"]!.GetValue<string>());
            Assert.Equal("Search by meaning isn't available: the embedding provider Remote AI answered 503 Service Unavailable.", (await JsonAsync(meaning))["notice"]!.GetValue<string>());
        }
        finally
        {
            _remote.Failure = null;
        }
    }
}
