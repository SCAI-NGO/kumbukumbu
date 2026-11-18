// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Search;
using Kumbu.Server.Search;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Search;

/// <summary>Vaults on people's own keys in the index and the search (BYOK spec, "How it runs").</summary>
[Collection("mongo")]
public sealed class ByokIndexTests(MongoFixture fixture)
{
    private const string KeyA = "sk-key-of-anna-0001";
    private const string KeyB = "sk-key-of-bert-0002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime T0 = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    private static StoredNote New(string path, string content) => new(P(path), content, 1, Writers.Dev, T0, [], Ref: WriterRef.Server);

    private sealed record Setup(VaultWorld World, VaultCatalog Catalog, SearchIndex Index, EncryptedNoteStore Store, ByokDriversTests.TensorX TensorX);

    private async Task<Setup> StartAsync(VaultWorld? world = null, bool byok = true, ByokDriversTests.TensorX? tensorx = null)
    {
        world ??= new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog(byok: byok);
        tensorx ??= new ByokDriversTests.TensorX();
        var drivers = new ByokDrivers(new ByokDriversTests.Factory(tensorx), NullLoggerFactory.Instance, TimeProvider.System, (_, _) => Task.CompletedTask);
        var index = new SearchIndex(catalog, EmbeddingProviders.Of(new FakeEmbedder(), byok ? drivers : null), NullLogger<SearchIndex>.Instance);
        await index.StartAsync(Ct);
        if ((await catalog.OpenAsync(Token, Ct)).Count == 0)
        {
            await catalog.CreateAsync("Anna", Token, null, Ct);
            await catalog.CreateAsync("Bert", Token, null, Ct);
        }

        return new Setup(world, catalog, index, new EncryptedNoteStore(catalog, new FixedCredential(Token), index), tensorx);
    }

    private static async Task<UnlockedVault> VaultAsync(Setup s, string name) => (await s.Catalog.OpenAsync(Token, Ct)).Single(v => v.Name == name).Vault;

    private static async Task OnKeyAsync(Setup s, string vault, string? key)
    {
        var v = await VaultAsync(s, vault);
        await s.Catalog.SetEmbeddingProviderAsync(v, key is null ? EmbeddingProviders.Internal : Byok.Provider, key, Ct);
        s.Index.CatchUp(v);
    }

    private static Task IdleAsync(Setup s) => s.Index.IdleAsync(TimeSpan.FromSeconds(10), Ct);

    [Fact]
    public async Task A_byok_vault_is_embedded_and_searched_with_its_owners_key()
    {
        var s = await StartAsync();
        await OnKeyAsync(s, "Anna", KeyA);
        await s.Store.TryCreateAsync(New("/Anna/hens.md", "The hens laid eggs today.\n"), Ct);
        await IdleAsync(s);

        var result = await s.Store.SearchAsync(P("/Anna"), "eggs", 5, Ct);

        Assert.Equal(SemanticState.Ready, result.State);
        Assert.Equal(P("/Anna/hens.md"), result.Hits[0].Path);
        Assert.NotEmpty(s.TensorX.Keys);
        Assert.All(s.TensorX.Keys, k => Assert.Equal(KeyA, k));
        Assert.Equal("tensorx-byok:qwen/qwen3-embedding-8b@1024", (await VaultAsync(s, "Anna")).Embeddings[P("/Anna/hens.md")].Model);
    }

    [Fact]
    public async Task Two_keys_embed_their_own_vaults_and_a_refused_one_holds_up_only_its_own()
    {
        var s = await StartAsync();
        s.TensorX.Refused.Add(KeyA);
        await OnKeyAsync(s, "Anna", KeyA);
        await OnKeyAsync(s, "Bert", KeyB);
        await s.Store.TryCreateAsync(New("/Anna/hens.md", "The hens laid eggs today.\n"), Ct);
        await s.Store.TryCreateAsync(New("/Bert/ducks.md", "The ducks swam in the pond.\n"), Ct);
        await Task.Delay(500, Ct);

        Assert.Contains(KeyB, s.TensorX.Keys);
        Assert.Contains(P("/Bert/ducks.md"), (await VaultAsync(s, "Bert")).Embeddings.Keys);
        Assert.DoesNotContain(P("/Anna/hens.md"), (await VaultAsync(s, "Anna")).Embeddings.Keys);

        var both = await s.Store.SearchAsync(P("/"), "pond", 5, Ct);
        Assert.Equal(SemanticState.Ready, both.State);
        Assert.Equal(["Anna"], both.WordsOnlyVaults);
    }

    [Fact]
    public async Task With_byok_off_a_byok_vault_unlocks_as_internal_and_keeps_its_key()
    {
        var s = await StartAsync();
        await OnKeyAsync(s, "Anna", KeyA);
        await IdleAsync(s);

        var off = await StartAsync(s.World, byok: false);
        var anna = await VaultAsync(off, "Anna");
        Assert.Equal((EmbeddingProviders.Internal, (string?)null), (anna.EmbeddingProvider, anna.EmbeddingKey));

        var on = await StartAsync(s.World, byok: true);
        anna = await VaultAsync(on, "Anna");
        Assert.Equal((Byok.Provider, (string?)KeyA), (anna.EmbeddingProvider, anna.EmbeddingKey));
    }

    [Fact]
    public async Task Another_provider_clears_the_key_from_the_meta()
    {
        var s = await StartAsync();
        await OnKeyAsync(s, "Anna", KeyA);
        await OnKeyAsync(s, "Anna", null);

        var later = await StartAsync(s.World);
        var anna = await VaultAsync(later, "Anna");
        Assert.Equal((EmbeddingProviders.Internal, (string?)null), (anna.EmbeddingProvider, anna.EmbeddingKey));
    }
}
