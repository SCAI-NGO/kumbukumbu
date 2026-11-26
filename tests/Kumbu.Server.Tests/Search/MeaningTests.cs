// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Core.Search;
using Kumbu.Server.Search;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Search;

/// <summary><see cref="EncryptedNoteStore.MeaningAsync"/>: what a query with <c>meaning:</c> takes from the index (embeddable
/// properties design, decision 6 and §4): the text embedded once, each note's current property vector.</summary>
[Collection("mongo")]
public sealed class MeaningTests(MongoFixture fixture) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime T0 = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    private const string Sync = "---\nsummary: Offline sync conflicts\n---\n# Sync\nTwo copies meet.\n";

    private static StoredNote New(string path, string content) => new(P(path), content, 1, Writers.Dev, T0, [], Ref: WriterRef.Server);

    private sealed record Setup(VaultWorld World, VaultCatalog Catalog, SearchIndex Index, EncryptedNoteStore Store, FakeEmbedder Embedder);

    private readonly List<SearchIndex> _indexes = [];

    public void Dispose()
    {
        foreach (var index in _indexes) index.Dispose();
    }

    private async Task<Setup> StartAsync(VaultWorld? world = null, bool start = true)
    {
        world ??= new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        var embedder = new FakeEmbedder();
        var index = new SearchIndex(catalog, EmbeddingProviders.Of(embedder), NullLogger<SearchIndex>.Instance);
        _indexes.Add(index);
        if (start) await index.StartAsync(Ct);
        if ((await catalog.OpenAsync(Token, Ct)).Count == 0) await catalog.CreateAsync("Work", Token, null, Ct);
        return new Setup(world, catalog, index, new EncryptedNoteStore(catalog, new FixedCredential(Token), index), embedder);
    }

    private static async Task FilledAsync(Setup s)
    {
        await s.Store.TryCreateAsync(New("/Work/Sync.md", Sync), Ct);
        await s.Store.TryCreateAsync(New("/Work/Farm.md", "---\nsummary: Hens and ducks\ndescription: Animals\n---\n# Farm\n"), Ct);
        await s.Store.TryCreateAsync(New("/Work/Plain.md", "# Plain\nNo summary here.\n"), Ct);
        await s.Index.IdleAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Embeds_the_text_once_and_returns_current_property_vectors()
    {
        var s = await StartAsync();
        await FilledAsync(s);
        var before = s.Embedder.Seen.Count;

        var result = await s.Store.MeaningAsync(P("/Work"), "summary", "when copies of a note disagree", Ct);

        Assert.Equal(SemanticState.Ready, result.State);
        Assert.Equal([("when copies of a note disagree", true)], s.Embedder.Seen.Skip(before));
        Assert.Equal(["/Work/Farm.md", "/Work/Sync.md"], result.Vectors.Keys.Select(p => p.Value).Order(StringComparer.Ordinal));
        Assert.Equal(s.Embedder.Dimensions, result.Query!.Length);
        var description = await s.Store.MeaningAsync(P("/Work"), "description", "animals", Ct);
        Assert.Equal(P("/Work/Farm.md"), Assert.Single(description.Vectors.Keys));
    }

    [Fact]
    public async Task A_stale_property_vector_is_left_out()
    {
        var s = await StartAsync();
        await FilledAsync(s);

        // A server whose index doesn't run: the edited summary isn't embedded again, so its stored hash is the old text's.
        var later = await StartAsync(s.World, start: false);
        var current = (await later.Store.GetAsync(P("/Work/Sync.md"), Ct))!;
        Assert.True(await later.Store.TryReplaceAsync(current, NoteWriter.Next(current, Sync.Replace("Offline sync conflicts", "Hens at night"), Writers.Dev, WriterRef.Server, T0), Ct));

        var result = await later.Store.MeaningAsync(P("/Work"), "summary", "hens", Ct);

        Assert.Equal(SemanticState.Ready, result.State);
        Assert.Equal(P("/Work/Farm.md"), Assert.Single(result.Vectors.Keys));
    }

    [Fact]
    public async Task An_unconsented_shared_vault_sends_nothing()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        var embedder = new FakeEmbedder();
        using var index = new SearchIndex(catalog, EmbeddingProviders.Of(embedder), NullLogger<SearchIndex>.Instance);
        await index.StartAsync(Ct);
        (string Id, byte[] Key) alice = ("u1", VaultCrypto.NewKey()), bob = ("u2", VaultCrypto.NewKey());
        var vault = await catalog.CreateForUserAsync("Gartenarbeit", alice.Id, alice.Key, Ct);
        var hers = new EncryptedNoteStore(catalog, new UserCredential(alice.Id, alice.Key), index);
        await hers.TryCreateAsync(New("/Gartenarbeit/Hens.md", "---\nsummary: The hens laid eggs\n---\n# Hens\n"), Ct);
        await index.IdleAsync(TimeSpan.FromSeconds(10), Ct);
        var membership = await Share.GrantAsync(catalog, world.Memberships(catalog), alice, vault.Id, bob, "Garten", MemberRole.Write);
        // As if the owner had changed the provider since Bob last saw it.
        await world.Db.GetCollection<MembershipDocument>(Memberships.CollectionName).UpdateOneAsync(
            m => m.Id == membership, Builders<MembershipDocument>.Update.Set(m => m.ProviderSeen, "earlier"), cancellationToken: Ct);
        var before = embedder.Seen.Count;

        var his = new EncryptedNoteStore(catalog, new UserCredential(bob.Id, bob.Key), index);
        var result = await his.MeaningAsync(P("/Garten"), "summary", "eggs", Ct);

        Assert.Equal(SemanticState.Off, result.State);
        Assert.Null(result.Query);
        Assert.Empty(result.Vectors);
        Assert.Contains("your searches don't go there until you OK the change", result.Reason, StringComparison.Ordinal);
        Assert.Equal(before, embedder.Seen.Count);
    }

    [Fact]
    public async Task A_provider_that_times_out_twice_is_unavailable()
    {
        var s = await StartAsync();
        await FilledAsync(s);
        s.Embedder.Hang = true;
        var store = new EncryptedNoteStore(s.Catalog, new FixedCredential(Token), s.Index) { MeaningTimeout = TimeSpan.FromMilliseconds(50) };

        var result = await store.MeaningAsync(P("/Work"), "summary", "hens", Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(SemanticState.Off, result.State);
        Assert.Null(result.Query);
        Assert.Contains("didn't answer", result.Reason, StringComparison.Ordinal);
        Assert.Equal(2, s.Embedder.Seen.Count(t => t == ("hens", true)));
    }

    /// <summary>A store that counts the queries its embedder is asked, before the embedder fails them with <paramref name="failure"/>.</summary>
    private async Task<(Setup Setup, Func<int> Queries)> FailingAsync(Exception failure)
    {
        var s = await StartAsync();
        await FilledAsync(s);
        var queries = 0;
        s.Embedder.BeforeEmbed = query =>
        {
            if (query) Interlocked.Increment(ref queries);
            return Task.CompletedTask;
        };
        s.Embedder.Failure = failure;
        return (s, () => queries);
    }

    [Fact]
    public async Task A_provider_whose_own_timeout_fires_twice_is_unavailable()
    {
        // As the remote driver throws when a query's own timeout ran out.
        var (s, queries) = await FailingAsync(new EmbeddingUnavailableException("the embedding provider Remote didn't answer within 5 s", providerDown: true) { TimedOut = true });

        var result = await s.Store.MeaningAsync(P("/Work"), "summary", "hens", Ct);

        Assert.Equal((SemanticState.Off, 2), (result.State, queries()));
        Assert.Null(result.Query);
        Assert.Equal("the embedding provider Remote didn't answer within 5 s", result.Reason);
    }

    [Fact]
    public async Task A_provider_that_fails_otherwise_is_asked_once()
    {
        var (s, queries) = await FailingAsync(new EmbeddingUnavailableException("the embedding provider Remote answered 503 Service Unavailable", providerDown: true));

        var result = await s.Store.MeaningAsync(P("/Work"), "summary", "hens", Ct);

        Assert.Equal((SemanticState.Off, 1), (result.State, queries()));
        Assert.Equal("the embedding provider Remote answered 503 Service Unavailable", result.Reason);
    }

    [Fact]
    public async Task The_own_key_of_an_owner_who_is_away_is_not_used()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog(byok: true);
        var embedder = new FakeEmbedder();
        using var index = new SearchIndex(catalog, EmbeddingProviders.Of(embedder), NullLogger<SearchIndex>.Instance);
        (string Id, byte[] Key) alice = ("u1", VaultCrypto.NewKey()), bob = ("u2", VaultCrypto.NewKey());
        var users = world.Db.GetCollection<Kumbu.Server.Accounts.UserDocument>("users");
        await users.UpdateOneAsync(u => u.Id == alice.Id, Builders<Kumbu.Server.Accounts.UserDocument>.Update.Set(u => u.EmbeddingsKeyId, "key-id-u1"), cancellationToken: Ct);
        var created = await catalog.CreateForUserAsync("Gartenarbeit", alice.Id, alice.Key, Ct);
        var vault = Assert.Single(await catalog.OpenForUserAsync(alice.Id, alice.Key, Ct)).Vault;
        await catalog.SetEmbeddingProviderAsync(vault, Byok.Provider, "sk-key-of-u1-0001", "key-id-u1", Ct);
        // Bob has seen her own key as the provider: only her being away keeps his query from it.
        await Share.GrantAsync(catalog, world.Memberships(catalog), alice, created.Id, bob, "Garten", MemberRole.Write);
        await users.UpdateOneAsync(u => u.Id == alice.Id, Builders<Kumbu.Server.Accounts.UserDocument>.Update.Set(u => u.Active, false), cancellationToken: Ct);

        var result = await new EncryptedNoteStore(catalog, new UserCredential(bob.Id, bob.Key), index).MeaningAsync(P("/Garten"), "summary", "hens", Ct);

        Assert.Equal(SemanticState.Off, result.State);
        Assert.Equal("its owner's account is deactivated, and its owner's own key isn't used meanwhile", result.Reason);
        Assert.DoesNotContain(embedder.Seen, t => t.Query);
    }
}
