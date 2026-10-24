// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Search;
using Kumbu.Server.Search;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Search;

[Collection("mongo")]
public sealed class SearchIndexTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime T0 = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private static StoredNote New(string path, string content) => new(P(path), content, 1, Writers.Dev, T0, []);

    private sealed record Setup(VaultWorld World, VaultCatalog Catalog, SearchIndex Index, EncryptedNoteStore Store, FakeEmbedder Embedder);

    private async Task<Setup> StartAsync(VaultWorld? world = null, FakeEmbedder? embedder = null)
    {
        world ??= new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        embedder ??= new FakeEmbedder();
        var index = new SearchIndex(catalog, embedder, NullLogger<SearchIndex>.Instance);
        await index.StartAsync(Ct);
        if ((await catalog.OpenAsync(Token, Ct)).Count == 0) await catalog.CreateAsync("Work", Token, null, Ct);
        return new Setup(world, catalog, index, new EncryptedNoteStore(catalog, new FixedCredential(Token), index), embedder);
    }

    private static async Task IdleAsync(Setup s) => await s.Index.IdleAsync(TimeSpan.FromSeconds(10), Ct);

    [Fact]
    public async Task Written_notes_are_found_by_meaning_section_by_section()
    {
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/gate.md", "# Gate\nThe service checks rules at the entrance.\n# Team\nAnna and Ben.\n"), Ct);
        await s.Store.TryCreateAsync(New("/Work/farm.md", "Cats, dogs, hens and ducks.\n"), Ct);
        await IdleAsync(s);

        var result = await s.Store.SearchAsync(P("/Work"), "which rules does the entrance check", 10, Ct);

        Assert.Equal(SemanticState.Ready, result.State);
        Assert.Equal(0, result.Pending);
        Assert.Equal((P("/Work/gate.md"), 0), (result.Hits[0].Path, result.Hits[0].StartLine));
        Assert.Equal(3, result.Hits.Count);
    }

    [Fact]
    public async Task Embeddings_are_sealed_and_survive_a_restart_without_embedding_again()
    {
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/Geheim.md", "# Übernahme\nWir kaufen die Firma Acme.\n"), Ct);
        await IdleAsync(s);

        var dump = new StringBuilder();
        foreach (var doc in await s.World.Db.GetCollection<BsonDocument>("embeddings").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
        {
            dump.Append(doc.ToJson()).Append(Encoding.UTF8.GetString(doc["blob"].AsByteArray));
        }

        Assert.NotEmpty(dump.ToString());
        foreach (var secret in new[] { "Acme", "Geheim", "Übernahme", "kaufen" }) Assert.DoesNotContain(secret, dump.ToString(), StringComparison.OrdinalIgnoreCase);

        var later = await StartAsync(s.World);
        await IdleAsync(later);
        Assert.Equal(0, later.Embedder.Embedded);
        Assert.Equal(P("/Work/Geheim.md"), Assert.Single((await later.Store.SearchAsync(P("/"), "Firma Acme", 1, Ct)).Hits).Path);
    }

    [Fact]
    public async Task Only_changed_sections_are_embedded_again_and_removed_notes_leave()
    {
        var s = await StartAsync();
        var note = New("/Work/plan.md", "# A\nalpha\n# B\nbeta\n# C\ngamma\n");
        await s.Store.TryCreateAsync(note, Ct);
        await IdleAsync(s);
        Assert.Equal(3, s.Embedder.Embedded);

        var current = (await s.Store.GetAsync(note.Path, Ct))!;
        await s.Store.TryReplaceAsync(current, NoteWriter.Next(current, "# A\nalpha\n# B\nbeta changed\n# C\ngamma\n", Writers.Dev, T0), Ct);
        await IdleAsync(s);
        Assert.Equal(4, s.Embedder.Embedded);

        await s.Store.TryTrashAsync((await s.Store.GetAsync(note.Path, Ct))!, Writers.Dev, T0, Ct);
        await IdleAsync(s);
        Assert.Empty((await s.Store.SearchAsync(P("/Work"), "beta", 10, Ct)).Hits);
        Assert.Equal(0, await s.World.Db.GetCollection<BsonDocument>("embeddings").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
    }

    [Fact]
    public async Task While_the_model_loads_search_says_so_and_the_queue_waits()
    {
        var embedder = new FakeEmbedder(SemanticState.Preparing);
        var s = await StartAsync(embedder: embedder);
        await s.Store.TryCreateAsync(New("/Work/a.md", "alpha\n"), Ct);

        var waiting = await s.Store.SearchAsync(P("/Work"), "alpha", 10, Ct);
        Assert.Equal((SemanticState.Preparing, "the embedding model is loading"), (waiting.State, waiting.Reason));

        embedder.BecomeReady();
        await IdleAsync(s);
        var ready = await s.Store.SearchAsync(P("/Work"), "alpha", 10, Ct);
        Assert.Equal((0, P("/Work/a.md")), (ready.Pending, ready.Hits[0].Path));
    }

    [Fact]
    public async Task Another_token_finds_nothing_of_a_vault_it_cannot_open()
    {
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/a.md", "alpha\n"), Ct);
        await IdleAsync(s);

        var stranger = new EncryptedNoteStore(s.Catalog, new FixedCredential("another-token-0123456789abcdef"), s.Index);
        Assert.Empty((await stranger.SearchAsync(P("/"), "alpha", 10, Ct)).Hits);
    }

    /// <summary>The real model, where it has been downloaded (KUMBU_MODEL_DIR): meaning across languages.</summary>
    [Fact]
    public async Task The_real_model_ranks_by_meaning()
    {
        var dir = Environment.GetEnvironmentVariable("KUMBU_MODEL_DIR");
        Assert.SkipUnless(dir is not null && File.Exists(Path.Combine(dir, ModelFiles.Model)), "KUMBU_MODEL_DIR with the model not set");

        var options = Microsoft.Extensions.Options.Options.Create(new KumbuOptions { Search = new SearchOptions { ModelPath = dir! } });
        using var embedder = new OnnxEmbedder(options, new NoHttp(), NullLogger<OnnxEmbedder>.Instance);
        await embedder.StartAsync(Ct);
        await embedder.WhenReadyAsync(Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct);

        var docs = await embedder.EmbedAsync(["Katzen, Hunde, Hühner und Enten leben auf dem Hof.", "The gate service checks the rules at the entrance."], query: false, Ct);
        var query = (await embedder.EmbedAsync(["animals on a farm"], query: true, Ct))[0];

        Assert.True(Vectors.Dot(query, docs[0]) > Vectors.Dot(query, docs[1]) + 0.1);
        Assert.Equal(1.0, Vectors.Dot(docs[0], docs[0]), 3);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("The test must not download");
    }
}
