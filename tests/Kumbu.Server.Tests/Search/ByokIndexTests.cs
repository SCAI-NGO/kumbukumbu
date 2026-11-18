// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Search;
using Kumbu.Server.Accounts;
using Kumbu.Server.Search;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
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

    // Anna's vault is u1's, Bert's u2's: a key belongs to an account, and each owner has her own.
    private static readonly byte[] KeyOfU1 = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyOfU2 = Enumerable.Range(2, 32).Select(i => (byte)i).ToArray();

    private static (string User, byte[] UserKey, string KeyId) OwnerOf(string vault) => vault == "Anna" ? ("u1", KeyOfU1, "key-id-u1") : ("u2", KeyOfU2, "key-id-u2");

    private sealed record Setup(VaultWorld World, VaultCatalog Catalog, SearchIndex Index, ByokDriversTests.TensorX TensorX)
    {
        public EncryptedNoteStore Store(string vault) => new(Catalog, new UserCredential(OwnerOf(vault).User, OwnerOf(vault).UserKey), Index);
    }

    private async Task<Setup> StartAsync(VaultWorld? world = null, bool byok = true, ByokDriversTests.TensorX? tensorx = null)
    {
        var fresh = world is null;
        world ??= new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog(byok: byok);
        tensorx ??= new ByokDriversTests.TensorX();
        var drivers = new ByokDrivers(new ByokDriversTests.Factory(tensorx), NullLoggerFactory.Instance, TimeProvider.System, (_, _) => Task.CompletedTask);
        var index = new SearchIndex(catalog, EmbeddingProviders.Of(new FakeEmbedder(), byok ? drivers : null), NullLogger<SearchIndex>.Instance);
        await index.StartAsync(Ct);
        if (fresh)
        {
            foreach (var vault in new[] { "Anna", "Bert" })
            {
                var (user, userKey, keyId) = OwnerOf(vault);
                await world.Db.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == user, Builders<UserDocument>.Update.Set(u => u.EmbeddingsKeyId, keyId), cancellationToken: Ct);
                await catalog.CreateForUserAsync(vault, user, userKey, Ct);
            }
        }

        return new Setup(world, catalog, index, tensorx);
    }

    private static async Task<UnlockedVault> VaultAsync(Setup s, string name) =>
        (await s.Catalog.OpenForUserAsync(OwnerOf(name).User, OwnerOf(name).UserKey, Ct)).Single(v => v.Name == name).Vault;

    private static async Task OnKeyAsync(Setup s, string vault, string? key)
    {
        var v = await VaultAsync(s, vault);
        await s.Catalog.SetEmbeddingProviderAsync(v, key is null ? EmbeddingProviders.Internal : Byok.Provider, key, OwnerOf(vault).KeyId, Ct);
        s.Index.CatchUp(v);
    }

    private static Task IdleAsync(Setup s) => s.Index.IdleAsync(TimeSpan.FromSeconds(10), Ct);

    private static async Task<VaultMeta> StoredMetaAsync(Setup s, string vault)
    {
        var v = await VaultAsync(s, vault);
        return v.OpenMeta((await s.Catalog.Vaults.Find(d => d.Id == v.Id).FirstAsync(Ct)).Meta);
    }

    [Fact]
    public async Task A_byok_vault_is_embedded_and_searched_with_its_owners_key()
    {
        var s = await StartAsync();
        await OnKeyAsync(s, "Anna", KeyA);
        await s.Store("Anna").TryCreateAsync(New("/Anna/hens.md", "The hens laid eggs today.\n"), Ct);
        await IdleAsync(s);

        var result = await s.Store("Anna").SearchAsync(P("/Anna"), "eggs", 5, Ct);

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
        await s.Store("Anna").TryCreateAsync(New("/Anna/hens.md", "The hens laid eggs today.\n"), Ct);
        await s.Store("Bert").TryCreateAsync(New("/Bert/ducks.md", "The ducks swam in the pond.\n"), Ct);
        await Task.Delay(500, Ct);

        Assert.Contains(KeyB, s.TensorX.Keys);
        Assert.Contains(P("/Bert/ducks.md"), (await VaultAsync(s, "Bert")).Embeddings.Keys);
        Assert.DoesNotContain(P("/Anna/hens.md"), (await VaultAsync(s, "Anna")).Embeddings.Keys);

        Assert.NotEqual(SemanticState.Ready, (await s.Store("Anna").SearchAsync(P("/"), "pond", 5, Ct)).State);
        var bert = await s.Store("Bert").SearchAsync(P("/"), "pond", 5, Ct);
        Assert.Equal(SemanticState.Ready, bert.State);
        Assert.Empty(bert.WordsOnlyVaults);
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

    [Fact]
    public async Task A_copy_of_a_key_its_owner_no_longer_has_runs_on_internal_and_goes_at_the_unlock()
    {
        var s = await StartAsync();
        await OnKeyAsync(s, "Anna", KeyA);
        await IdleAsync(s);
        // She removed or replaced the key while the vault couldn't be written (in a re-encryption, say).
        await s.World.Db.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == "u1", Builders<UserDocument>.Update.Set(u => u.EmbeddingsKeyId, "key-id-later"), cancellationToken: Ct);

        var later = await StartAsync(s.World);
        var anna = await VaultAsync(later, "Anna");

        Assert.Equal((EmbeddingProviders.Internal, (string?)null), (anna.EmbeddingProvider, anna.EmbeddingKey));
        var meta = await StoredMetaAsync(later, "Anna");
        Assert.Equal((EmbeddingProviders.Internal, (string?)null, (string?)null), (meta.Embeddings, meta.EmbeddingsKey, meta.EmbeddingsKeyId));
    }

    [Fact]
    public async Task A_vault_known_to_be_discreet_never_runs_on_tensorx()
    {
        var s = await StartAsync();
        await OnKeyAsync(s, "Anna", KeyA);
        var anna = await VaultAsync(s, "Anna");

        anna.Discreet = true;

        Assert.Equal(EmbeddingProviders.Internal, s.Index.Providers.RouteOf(anna));
        Assert.Equal(EmbeddingProviders.Internal, s.Index.Providers.Resolve(anna)!.Name);
    }
}
