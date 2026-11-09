// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Kumbu.Core.Crypto;
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

    private static StoredNote New(string path, string content) => new(P(path), content, 1, Writers.Dev, T0, [], Ref: WriterRef.Server);

    private sealed record Setup(VaultWorld World, VaultCatalog Catalog, SearchIndex Index, EncryptedNoteStore Store, FakeEmbedder Embedder);

    /// <summary>A server on <paramref name="world"/>'s database; with <paramref name="start"/> false the index
    /// never runs, so whatever the unlock queues stays queued.</summary>
    private async Task<Setup> StartAsync(VaultWorld? world = null, FakeEmbedder? embedder = null, bool start = true, EmbeddingProvider[]? external = null, ChunkTokenizer? tokenizer = null,
        Microsoft.Extensions.Logging.ILogger<SearchIndex>? log = null)
    {
        world ??= new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        embedder ??= new FakeEmbedder();
        var index = new SearchIndex(catalog, EmbeddingProviders.Of(embedder, external ?? []), log ?? NullLogger<SearchIndex>.Instance, tokenizer: tokenizer);
        if (start) await index.StartAsync(Ct);
        if ((await catalog.OpenAsync(Token, Ct)).Count == 0) await catalog.CreateAsync("Work", Token, null, Ct);
        return new Setup(world, catalog, index, new EncryptedNoteStore(catalog, new FixedCredential(Token), index), embedder);
    }

    private static async Task IdleAsync(Setup s) => await s.Index.IdleAsync(TimeSpan.FromSeconds(10), Ct);

    [Fact]
    public async Task Written_notes_are_found_by_meaning_section_by_section()
    {
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/gate.md", "# Gate\n## Rules\nThe service checks rules at the entrance.\n# Team\n## People\nAnna and Ben.\n"), Ct);
        await s.Store.TryCreateAsync(New("/Work/farm.md", "Cats, dogs, hens and ducks.\n"), Ct);
        await IdleAsync(s);

        var result = await s.Store.SearchAsync(P("/Work"), "which rules does the entrance check", 10, Ct);

        Assert.Equal(SemanticState.Ready, result.State);
        Assert.Equal(0, result.Pending);
        Assert.Equal((P("/Work/gate.md"), 0), (result.Hits[0].Path, result.Hits[0].StartLine));
        Assert.Equal(3, result.Hits.Count);
    }

    [Fact]
    public async Task The_notes_of_an_unlocked_vault_are_split_ahead_of_the_first_search()
    {
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/gate.md", "# Gate\n## Rules\nThe service checks rules at the entrance.\n"), Ct);
        await s.Store.TryCreateAsync(New("/Work/farm.md", "Cats, dogs, hens and ducks.\n"), Ct);
        await IdleAsync(s);

        // After a restart nothing is embedded again, so only the warm-up splits the notes.
        var later = await StartAsync(s.World);
        await IdleAsync(later);
        await later.Index.Warming.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(0, later.Embedder.Embedded);
        var notes = await later.Store.ReadNotesUnderAsync(P("/Work"), Ct);
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.True(Chunker.IsSplit(n, later.Index.Bound), n.Path.Value));
    }

    [Fact]
    public async Task Embeddings_are_sealed_and_survive_a_restart_without_embedding_again()
    {
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/Geheim.md", "# Geheimakte Übernahme\nWir kaufen die Firma Acme-Übernahme-Geheimprojekt.\n"), Ct);
        await IdleAsync(s);

        var dump = new StringBuilder();
        foreach (var doc in await s.World.Db.GetCollection<BsonDocument>("embeddings").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
        {
            dump.Append(doc.ToJson()).Append(Encoding.UTF8.GetString(doc["blob"].AsByteArray));
        }

        Assert.NotEmpty(dump.ToString());
        foreach (var secret in new[] { "Acme-Übernahme-Geheimprojekt", "Geheimakte Übernahme" }) Assert.DoesNotContain(secret, dump.ToString(), StringComparison.OrdinalIgnoreCase);

        var later = await StartAsync(s.World);
        await IdleAsync(later);
        Assert.Equal(0, later.Embedder.Embedded);
        Assert.Equal(P("/Work/Geheim.md"), Assert.Single((await later.Store.SearchAsync(P("/"), "Firma Acme-Übernahme-Geheimprojekt", 1, Ct)).Hits).Path);
    }

    [Fact]
    public async Task Only_changed_sections_are_embedded_again_and_removed_notes_leave()
    {
        var s = await StartAsync();
        var note = New("/Work/plan.md", "# A\n## A1\nalpha\n# B\n## B1\nbeta\n# C\n## C1\ngamma\n");
        await s.Store.TryCreateAsync(note, Ct);
        await IdleAsync(s);
        Assert.Equal(3, s.Embedder.Embedded);

        var current = (await s.Store.GetAsync(note.Path, Ct))!;
        await s.Store.TryReplaceAsync(current, NoteWriter.Next(current, "# A\n## A1\nalpha\n# B\n## B1\nbeta changed\n# C\n## C1\ngamma\n", Writers.Dev, WriterRef.Server, T0), Ct);
        await IdleAsync(s);
        Assert.Equal(4, s.Embedder.Embedded);

        await s.Store.TryTrashAsync((await s.Store.GetAsync(note.Path, Ct))!, Writers.Dev, WriterRef.Server, T0, Ct);
        await IdleAsync(s);
        Assert.Empty((await s.Store.SearchAsync(P("/Work"), "beta", 10, Ct)).Hits);
        Assert.Equal(0, await s.World.Db.GetCollection<BsonDocument>("embeddings").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
    }

    [Fact]
    public async Task Until_the_tokenizer_settles_search_is_getting_ready_and_nothing_is_embedded()
    {
        var models = Directory.CreateTempSubdirectory("kumbu-models-").FullName;
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new KumbuOptions { Search = new SearchOptions { Enabled = false, ModelPath = models } });
            var tokenizer = new ChunkTokenizer(options, new NoHttp(), NullLogger<ChunkTokenizer>.Instance);
            var s = await StartAsync(tokenizer: tokenizer);
            await s.Store.TryCreateAsync(New("/Work/a.md", "alpha\n"), Ct);
            await Task.Delay(200, Ct);

            var waiting = await s.Store.SearchAsync(P("/Work"), "alpha", 10, Ct);
            Assert.Equal((SemanticState.Preparing, "search by meaning is getting ready (the tokenizer is loading)"), (waiting.State, waiting.Reason));
            Assert.Equal(0, s.Embedder.Embedded);
            Assert.Same(ChunkBound.Fallback, s.Store.Bound);

            await tokenizer.StartAsync(Ct);
            await tokenizer.WhenSettledAsync(Ct);
            await IdleAsync(s);
            Assert.Same(ChunkBound.Unbounded, s.Store.Bound);
            Assert.Equal(P("/Work/a.md"), Assert.Single((await s.Store.SearchAsync(P("/Work"), "alpha", 10, Ct)).Hits).Path);
        }
        finally
        {
            Directory.Delete(models, recursive: true);
        }
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

    private static async Task<List<NoteEmbeddings>> StoredEmbeddingsAsync(Setup s, string name = "Work")
    {
        var vault = (await s.Catalog.OpenAsync(Token, Ct)).Single(v => v.Name == name).Vault;
        var docs = await s.World.Db.GetCollection<SealedDocument>("embeddings").Find(d => d.Vault == vault.Id).ToListAsync(Ct);
        return [.. docs.Select(d => vault.OpenEmbeddings(d).Embeddings)];
    }

    [Fact]
    public async Task SwitchingTheModelReEmbedsOnUnlock()
    {
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/plan.md", "# A\n## A1\nalpha\n# B\n## B1\nbeta\n"), Ct);
        await s.Store.TryCreateAsync(New("/Work/farm.md", "Cats and dogs.\n"), Ct);
        await IdleAsync(s);
        Assert.Equal(3, s.Embedder.Embedded);

        var later = await StartAsync(s.World, new FakeEmbedder(model: "arctic-l"));
        await IdleAsync(later);

        Assert.Equal(3, later.Embedder.Embedded);
        var stored = await StoredEmbeddingsAsync(later);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, e => Assert.Equal(("arctic-l", 1024), (e.Model, e.Chunks[0].Vector.Length)));
        var result = await later.Store.SearchAsync(P("/Work"), "cats and dogs", 10, Ct);
        Assert.Equal((0, P("/Work/farm.md")), (result.Pending, result.Hits[0].Path));
    }

    [Fact]
    public async Task SearchSkipsVectorsOfAnotherModelAndCountsThemPending()
    {
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/a.md", "alpha\n"), Ct);
        await s.Store.TryCreateAsync(New("/Work/b.md", "alpha beta\n"), Ct);
        await IdleAsync(s);

        // The index doesn't run: the catch-up stays queued, and only a.md is embedded with arctic-l. The two
        // fakes' vectors differ in length, so a dot product across models would throw.
        var later = await StartAsync(s.World, new FakeEmbedder(model: "arctic-l"), start: false);
        var vault = Assert.Single(await later.Catalog.OpenAsync(Token, Ct)).Vault;
        await later.Index.IndexAsync(vault, P("/Work/a.md"), Ct);

        var result = await later.Store.SearchAsync(P("/Work"), "alpha", 10, Ct);

        Assert.Equal(SemanticState.Ready, result.State);
        Assert.Equal(1, result.Pending);
        Assert.Equal(P("/Work/a.md"), Assert.Single(result.Hits).Path);
    }

    [Fact]
    public async Task SwitchingBackDoesNotReuseVectorsOfTheOtherModel()
    {
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/plan.md", "# A\n## A1\nalpha\n# B\n## B1\nbeta\n"), Ct);
        await IdleAsync(s);

        var large = await StartAsync(s.World, new FakeEmbedder(model: "arctic-l"));
        await IdleAsync(large);
        Assert.Equal(2, large.Embedder.Embedded);

        // Same texts, same hashes: only the model tells the arctic-l vectors apart from the ones arctic-m needs.
        var back = await StartAsync(s.World, new FakeEmbedder(model: "arctic-m"));
        await IdleAsync(back);
        Assert.Equal(2, back.Embedder.Embedded);
        Assert.All(await StoredEmbeddingsAsync(back), e => Assert.Equal(("arctic-m", 768), (e.Model, e.Chunks[0].Vector.Length)));
    }

    [Fact]
    public void EmbeddingsWithoutAModelAreArcticM()
    {
        var vaultKey = VaultCrypto.NewKey();
        var vault = new UnlockedVault("v1", "Work", vaultKey, T0, new VaultLocks());
        var path = P("/Work/a.md");
        var pathId = vault.PathId(path);

        // The payload as step 7 wrote it: no model.
        var step7 = new { Path = path.Value, Version = "v-1", Chunks = new[] { new { StartLine = 0, Hash = "h", Vector = Convert.ToBase64String(new byte[4]) } } };
        var blob = VaultCrypto.Seal(
            VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), JsonSerializer.SerializeToUtf8Bytes(step7), $"embeddings:v1:{Convert.ToBase64String(pathId)}");

        var (opened, embeddings) = vault.OpenEmbeddings(new SealedDocument { Vault = "v1", PathId = pathId, Blob = blob });

        Assert.Equal((path, "arctic-m", "v-1", 2), (opened, embeddings.Model, embeddings.Version, embeddings.Chunks[0].Vector.Length));
    }

    [Fact]
    public void Embeddings_keep_their_scheme_and_parts()
    {
        var vault = new UnlockedVault("v1", "Work", VaultCrypto.NewKey(), T0, new VaultLocks());
        var path = P("/Work/a.md");
        var sealedDoc = new SealedDocument
        {
            Vault = "v1", PathId = vault.PathId(path),
            Blob = vault.SealEmbeddings(path, new NoteEmbeddings("arctic-m", "v-1", [new(3, "h0", [1, 0]), new(3, "h2", [0, 1], Part: 2)], "2/tok-f1cc44ad7faa")),
        };

        var (_, opened) = vault.OpenEmbeddings(sealedDoc);

        Assert.Equal("2/tok-f1cc44ad7faa", opened.Scheme);
        Assert.Equal([(3, 0, "h0"), (3, 2, "h2")], opened.Chunks.Select(c => (c.StartLine, c.Part, c.Hash)));
    }

    [Fact]
    public void Embeddings_before_the_scheme_open_without_one()
    {
        var vaultKey = VaultCrypto.NewKey();
        var vault = new UnlockedVault("v1", "Work", vaultKey, T0, new VaultLocks());
        var path = P("/Work/a.md");
        var pathId = vault.PathId(path);

        // The payload as 0.3 wrote it: no scheme, no parts.
        var before = new { Path = path.Value, Version = "v-1", Model = "arctic-m", Chunks = new[] { new { StartLine = 4, Hash = "h", Vector = Convert.ToBase64String(new byte[4]) } } };
        var blob = VaultCrypto.Seal(
            VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), JsonSerializer.SerializeToUtf8Bytes(before), $"embeddings:v1:{Convert.ToBase64String(pathId)}");

        var (_, embeddings) = vault.OpenEmbeddings(new SealedDocument { Vault = "v1", PathId = pathId, Blob = blob });

        Assert.Null(embeddings.Scheme);
        Assert.Equal((4, 0), (embeddings.Chunks[0].StartLine, embeddings.Chunks[0].Part));
    }

    /// <summary>The vault of <paramref name="s"/>, with the embeddings of <paramref name="paths"/> in its working set
    /// marked as made under another scheme (as 0.3 stored them).</summary>
    private static async Task<UnlockedVault> OfAnOlderSchemeAsync(Setup s, params string[] paths)
    {
        var vault = Assert.Single(await s.Catalog.OpenAsync(Token, Ct)).Vault;
        foreach (var path in paths) vault.Embeddings = vault.Embeddings.SetItem(P(path), vault.Embeddings[P(path)] with { Scheme = null });
        return vault;
    }

    [Fact]
    public async Task A_note_of_an_older_scheme_is_chunked_anew()
    {
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/a.md", "# A\nalpha\n"), Ct);
        await IdleAsync(s);
        var vault = await OfAnOlderSchemeAsync(s, "/Work/a.md");

        s.Index.CatchUp(vault);
        await IdleAsync(s);

        var stored = Assert.Single(await StoredEmbeddingsAsync(s));
        Assert.Equal(s.Index.Bound.Scheme, stored.Scheme);
        Assert.Equal(s.Index.Bound.Scheme, vault.Embeddings[P("/Work/a.md")].Scheme);
        // The same text: its vector is kept.
        Assert.Equal(1, s.Embedder.Embedded);
    }

    [Fact]
    public async Task Unchanged_texts_keep_their_vectors_across_schemes()
    {
        // Stored under the character bound; the next start has no provider of a tokenizer and chunks by words alone,
        // so the long line becomes fewer, longer pieces, and the short note stays as it was.
        var line = string.Join(' ', Enumerable.Range(0, 300).Select(i => $"reading{i}"));
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/short.md", "# Short\nA few words only.\n"), Ct);
        await s.Store.TryCreateAsync(New("/Work/log.md", "# Log\n" + line + "\n"), Ct);
        await IdleAsync(s);
        var before = (await StoredEmbeddingsAsync(s)).SelectMany(e => e.Chunks).Select(c => c.Hash).ToHashSet();
        Assert.Equal("2/fallback", Assert.Single((await StoredEmbeddingsAsync(s)).Select(e => e.Scheme).Distinct()));

        var models = Directory.CreateTempSubdirectory("kumbu-models-").FullName;
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new KumbuOptions { Search = new SearchOptions { Enabled = false, ModelPath = models } });
            var tokenizer = new ChunkTokenizer(options, new NoHttp(), NullLogger<ChunkTokenizer>.Instance);
            await tokenizer.StartAsync(Ct);
            await tokenizer.WhenSettledAsync(Ct);
            var later = await StartAsync(s.World, tokenizer: tokenizer);
            await IdleAsync(later);

            var notes = (await later.Store.ReadNotesUnderAsync(P("/Work"), Ct)).ToDictionary(n => n.Path.Value);
            var changed = Chunker.Split(notes["/Work/log.md"], later.Index.Bound).Select(c => c.EmbeddingText).Where(t => !before.Contains(Hash(t))).Distinct().ToList();
            Assert.NotEmpty(changed);
            Assert.Equal(changed.Count, later.Embedder.Embedded);
            Assert.DoesNotContain(later.Embedder.Seen, t => t.Text.Contains("A few words only."));
            Assert.All(await StoredEmbeddingsAsync(later), e => Assert.Equal("2/words", e.Scheme));
        }
        finally
        {
            Directory.Delete(models, recursive: true);
        }
    }

    private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    [Fact]
    public async Task Older_scheme_notes_are_pending_until_re_indexed()
    {
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/a.md", "alpha\n"), Ct);
        await s.Store.TryCreateAsync(New("/Work/b.md", "alpha beta\n"), Ct);
        await s.Store.TryCreateAsync(New("/Work/c.md", "alpha gamma\n"), Ct);
        await IdleAsync(s);

        // The index doesn't run yet: the unlock found everything current, so only the search queues the two.
        var later = await StartAsync(s.World, start: false);
        await OfAnOlderSchemeAsync(later, "/Work/a.md", "/Work/b.md");

        var stale = await later.Store.SearchAsync(P("/Work"), "alpha", 10, Ct);

        Assert.Equal((SemanticState.Ready, 2), (stale.State, stale.Pending));
        Assert.Equal(P("/Work/c.md"), Assert.Single(stale.Hits).Path);
        Assert.Equal(["/Work/a.md", "/Work/b.md"], stale.StaleNotes.Select(p => p.Value).Order(StringComparer.Ordinal));
        Assert.Equal(2, later.Index.Queued);

        await later.Index.StartAsync(Ct);
        await IdleAsync(later);
        var current = await later.Store.SearchAsync(P("/Work"), "alpha", 10, Ct);
        Assert.Equal(0, current.Pending);
        Assert.Empty(current.StaleNotes);
        Assert.Equal(3, current.Hits.Count);
        Assert.DoesNotContain(later.Embedder.Seen, t => !t.Query);
    }

    [Fact]
    public async Task A_note_that_fails_is_not_sent_again_by_every_search()
    {
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/a.md", "alpha\n"), Ct);
        await s.Store.TryCreateAsync(New("/Work/bad.md", "alpha poison\n"), Ct);
        await IdleAsync(s);
        // Stored under another scheme, and chunked otherwise then: chunking anew has a text to embed.
        var vault = Assert.Single(await s.Catalog.OpenAsync(Token, Ct)).Vault;
        var old = vault.Embeddings[P("/Work/bad.md")];
        vault.Embeddings = vault.Embeddings.SetItem(P("/Work/bad.md"), old with { Scheme = null, Chunks = [.. old.Chunks.Select(c => c with { Hash = "older" })] });
        s.Embedder.Refuses = t => t.Contains("poison");

        Assert.Single((await s.Store.SearchAsync(P("/Work"), "alpha", 10, Ct)).StaleNotes);
        await IdleAsync(s);
        Assert.Equal(1, s.Embedder.Refused);
        Assert.True(s.Index.HasFailed(vault, P("/Work/bad.md")));

        for (var i = 0; i < 3; i++)
        {
            // Still stale, so still ranked by its words, but not queued again.
            Assert.Single((await s.Store.SearchAsync(P("/Work"), "alpha", 10, Ct)).StaleNotes);
            Assert.Equal(0, s.Index.Queued);
            await IdleAsync(s);
        }

        Assert.Equal(1, s.Embedder.Refused);

        // A new version is tried again, by its write; it fails as well, and searches leave it again.
        var current = (await s.Store.GetAsync(P("/Work/bad.md"), Ct))!;
        await s.Store.TryReplaceAsync(current, NoteWriter.Next(current, "alpha poison again\n", Writers.Dev, WriterRef.Server, T0), Ct);
        await IdleAsync(s);
        Assert.Equal(2, s.Embedder.Refused);
        await s.Store.SearchAsync(P("/Work"), "alpha", 10, Ct);
        Assert.Equal(0, s.Index.Queued);
    }

    [Fact]
    public async Task Failures_are_forgotten_on_an_unlock_and_with_another_version()
    {
        // The index doesn't run: only what is noted here counts.
        var s = await StartAsync(start: false);
        await s.Store.TryCreateAsync(New("/Work/a.md", "alpha\n"), Ct);
        var vault = Assert.Single(await s.Catalog.OpenAsync(Token, Ct)).Vault;

        s.Index.NoteFailed(vault, P("/Work/a.md"));
        Assert.True(s.Index.HasFailed(vault, P("/Work/a.md")));
        s.Index.CatchUp(vault);
        Assert.False(s.Index.HasFailed(vault, P("/Work/a.md")));

        s.Index.NoteFailed(vault, P("/Work/a.md"));
        var current = (await s.Store.GetAsync(P("/Work/a.md"), Ct))!;
        await s.Store.TryReplaceAsync(current, NoteWriter.Next(current, "alpha beta\n", Writers.Dev, WriterRef.Server, T0), Ct);
        Assert.False(s.Index.HasFailed(vault, P("/Work/a.md")));
    }

    [Fact]
    public async Task Texts_over_the_bound_are_counted_and_normal_notes_stay_quiet()
    {
        Assert.Equal(1, SearchIndex.Oversized(ChunkBound.Fallback, [new string('a', ChunkBound.FallbackMaxChars + 1), "short"]));
        Assert.Equal(0, SearchIndex.Oversized(ChunkBound.Unbounded, [new string('a', 10_000)]));

        var log = new CapturedLog();
        using var logs = new Microsoft.Extensions.Logging.LoggerFactory([log]);
        var s = await StartAsync(log: Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<SearchIndex>(logs));
        await s.Store.TryCreateAsync(New("/Work/a.md", "# A\n" + string.Join('\n', Enumerable.Range(0, 200).Select(i => $"line {i} of a long note"))), Ct);
        await IdleAsync(s);

        Assert.True(Assert.Single(await StoredEmbeddingsAsync(s)).Chunks.Count > 1);
        Assert.DoesNotContain(log.Lines, l => l.Contains("exceed the bound"));
    }

    [Fact]
    public async Task Pieces_of_a_long_line_are_found_by_their_part()
    {
        var s = await StartAsync();
        var line = string.Join(' ', Enumerable.Range(0, 1_200).Select(i => $"reading{i}"));
        await s.Store.TryCreateAsync(New("/Work/log.md", "# Log\n" + line + "\n"), Ct);
        await IdleAsync(s);
        var note = Assert.Single(await s.Store.ReadNotesUnderAsync(P("/Work"), Ct));
        var third = Chunker.Split(note, s.Index.Bound).Single(c => c.StartLine == 1 && c.Part == 2);

        var result = await s.Store.SearchAsync(P("/Work"), third.Text, 1, Ct);

        Assert.Equal((P("/Work/log.md"), 1, 2), (result.Hits[0].Path, result.Hits[0].StartLine, result.Hits[0].Part));
    }

    [Fact]
    public async Task A_note_stored_under_the_fallback_is_re_chunked_once_the_tokenizer_settles()
    {
        // With the real tokenizer (KUMBU_TOKENIZER) the bound settles on it; without, on the word limits alone, as
        // with no provider. Either way not on the fallback the note was stored under.
        var s = await StartAsync();
        await s.Store.TryCreateAsync(New("/Work/a.md", "# A\nalpha\n"), Ct);
        await IdleAsync(s);
        Assert.Equal("2/fallback", Assert.Single(await StoredEmbeddingsAsync(s)).Scheme);

        var models = Directory.CreateTempSubdirectory("kumbu-models-").FullName;
        try
        {
            var real = Environment.GetEnvironmentVariable("KUMBU_TOKENIZER");
            if (real is not null && File.Exists(real))
            {
                var dir = ModelFiles.Directory(models, ModelFiles.Default);
                Directory.CreateDirectory(dir);
                File.Copy(real, Path.Combine(dir, ModelFiles.Tokenizer));
            }

            var options = Microsoft.Extensions.Options.Options.Create(new KumbuOptions { Search = new SearchOptions { Enabled = false, ModelPath = models } });
            var tokenizer = new ChunkTokenizer(options, new NoHttp(), NullLogger<ChunkTokenizer>.Instance);

            // Unlocked while the bound is still the fallback: the note looks current then.
            var later = await StartAsync(s.World, tokenizer: tokenizer);
            await Task.Delay(200, Ct);
            Assert.Equal("2/fallback", (await StoredEmbeddingsAsync(later)).Single().Scheme);

            await tokenizer.StartAsync(Ct);
            await tokenizer.WhenSettledAsync(Ct);
            var vault = Assert.Single(await later.Catalog.OpenAsync(Token, Ct)).Vault;
            // The index's catch-up follows the settling a moment later.
            for (var until = DateTime.UtcNow.AddSeconds(10); vault.Embeddings[P("/Work/a.md")].Scheme == "2/fallback" && DateTime.UtcNow < until;) await Task.Delay(50, Ct);
            await IdleAsync(later);

            var expected = real is not null && File.Exists(real) ? "2/tok-f1cc44ad7faa" : "2/words";
            Assert.Equal(expected, later.Index.Bound.Scheme);
            Assert.Equal(expected, Assert.Single(await StoredEmbeddingsAsync(later)).Scheme);
        }
        finally
        {
            Directory.Delete(models, recursive: true);
        }
    }

    private static EmbeddingProvider Remote(FakeEmbedder driver, string name = "remote") => new(name, "Remote", "EU, zero data retention", driver);

    /// <summary>Two vaults, Work on the internal model and Private on the remote provider, each with a note.</summary>
    private async Task<(Setup S, FakeEmbedder Remote)> TwoProvidersAsync()
    {
        var remote = new FakeEmbedder(model: "remote:qwen3@64");
        var s = await StartAsync(external: [Remote(remote)]);
        var priv = await s.Catalog.CreateAsync("Private", Token, null, Ct);
        await s.Catalog.SetEmbeddingProviderAsync(priv, "remote", Ct);
        await s.Store.TryCreateAsync(New("/Work/farm.md", "Cats, dogs, hens and ducks.\n"), Ct);
        await s.Store.TryCreateAsync(New("/Private/diary.md", "# Garden\nThe hens laid eggs today.\n"), Ct);
        await IdleAsync(s);
        return (s, remote);
    }

    [Fact]
    public async Task A_vault_that_opted_in_is_embedded_by_its_provider_only_and_the_others_stay_on_the_server()
    {
        var (s, remote) = await TwoProvidersAsync();

        // Nothing of Work went to the remote provider, nothing of Private to the internal model.
        Assert.All(remote.Seen, t => Assert.DoesNotContain("Cats", t.Text));
        Assert.Contains(remote.Seen, t => t.Text.Contains("hens laid eggs"));
        Assert.All(s.Embedder.Seen, t => Assert.DoesNotContain("eggs", t.Text));

        var stored = await s.World.Db.GetCollection<SealedDocument>("embeddings").Find(FilterDefinition<SealedDocument>.Empty).ToListAsync(Ct);
        var vaults = (await s.Catalog.OpenAsync(Token, Ct)).ToDictionary(v => v.Id, v => v.Vault);
        Assert.Equal(
            ["Private remote:qwen3@64", "Work arctic-m"],
            stored.Select(d => $"{vaults[d.Vault].Name} {vaults[d.Vault].OpenEmbeddings(d).Embeddings.Model}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_search_over_both_embeds_the_query_once_per_provider_and_ranks_each_vault_by_its_own_model()
    {
        var (s, remote) = await TwoProvidersAsync();
        var (before, beforeRemote) = (s.Embedder.Seen.Count, remote.Seen.Count);

        var result = await s.Store.SearchAsync(P("/"), "hens", 10, Ct);

        Assert.Equal((SemanticState.Ready, 0, (string?)null), (result.State, result.Pending, result.Reason));
        Assert.Equal([("hens", true)], s.Embedder.Seen.Skip(before));
        Assert.Equal([("hens", true)], remote.Seen.Skip(beforeRemote));
        Assert.Equal(["/Private/diary.md", "/Work/farm.md"], result.Hits.Select(h => h.Path.Value).Distinct().Order(StringComparer.Ordinal));

        // A search in one vault asks that vault's provider only.
        var work = await s.Store.SearchAsync(P("/Work"), "hens", 10, Ct);
        Assert.Equal(P("/Work/farm.md"), Assert.Single(work.Hits).Path);
        Assert.Equal(beforeRemote + 1, remote.Seen.Count);
    }

    [Fact]
    public async Task The_query_is_embedded_by_all_providers_at_the_same_time()
    {
        var (s, remote) = await TwoProvidersAsync();
        var internalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Each provider's query waits until the other one has started: one after the other would never finish.
        s.Embedder.BeforeEmbed = async query =>
        {
            if (!query) return;
            internalStarted.TrySetResult();
            await remoteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };
        remote.BeforeEmbed = async query =>
        {
            if (!query) return;
            remoteStarted.TrySetResult();
            await internalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var result = await s.Store.SearchAsync(P("/"), "hens", 10, Ct);

        Assert.Equal((SemanticState.Ready, (string?)null), (result.State, result.Reason));
        Assert.Equal(["/Private/diary.md", "/Work/farm.md"], result.Hits.Select(h => h.Path.Value).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Switching_a_vault_to_another_provider_embeds_it_again_and_survives_a_restart()
    {
        var (s, remote) = await TwoProvidersAsync();
        var work = (await s.Catalog.OpenAsync(Token, Ct)).Single(v => v.Name == "Work").Vault;
        var seen = remote.Seen.Count;

        await s.Catalog.SetEmbeddingProviderAsync(work, "remote", Ct);
        s.Index.CatchUp(work);
        await IdleAsync(s);

        Assert.Contains(remote.Seen.Skip(seen), t => t.Text.Contains("Cats"));
        Assert.All(await StoredEmbeddingsAsync(s, "Work"), e => Assert.Equal("remote:qwen3@64", e.Model));

        // The choice is sealed in the vault's meta: a restart opens it with the same provider, and nothing is embedded again.
        var later = await StartAsync(s.World, external: [Remote(new FakeEmbedder(model: "remote:qwen3@64"))]);
        await IdleAsync(later);
        Assert.Equal(["remote", "remote"], (await later.Catalog.OpenAsync(Token, Ct)).Select(v => v.Vault.EmbeddingProvider));
        Assert.Equal(0, later.Embedder.Embedded);
    }

    [Fact]
    public async Task Vectors_of_another_length_under_the_same_model_key_are_pending_not_an_error()
    {
        // The provider's model now answers with 32 values instead of 64 (no Dimensions configured, so the key is the same).
        var (s, _) = await TwoProvidersAsync();
        var shorter = new FakeEmbedder(model: "remote:qwen3@64", dimensions: 32);
        var idle = await StartAsync(s.World, start: false, external: [Remote(shorter)]);

        var result = await idle.Store.SearchAsync(P("/"), "hens", 10, Ct);

        Assert.Equal(SemanticState.Ready, result.State);
        Assert.Equal(1, result.Pending);
        Assert.DoesNotContain(result.Hits, h => h.Path.Value.StartsWith("/Private/", StringComparison.Ordinal));

        var running = await StartAsync(s.World, external: [Remote(new FakeEmbedder(model: "remote:qwen3@64", dimensions: 32))]);
        await IdleAsync(running);
        Assert.All(await StoredEmbeddingsAsync(running, "Private"), e => Assert.Equal(32, e.Chunks[0].Vector.Length));
        Assert.Contains((await running.Store.SearchAsync(P("/Private"), "hens", 10, Ct)).Hits, h => h.Path.Value == "/Private/diary.md");
    }

    [Fact]
    public async Task Switching_back_to_internal_stops_sending_between_batches()
    {
        var remote = new FakeEmbedder(model: "remote:qwen3@64") { MaxBatch = 1 };
        var s = await StartAsync(external: [Remote(remote)]);
        var priv = await s.Catalog.CreateAsync("Private", Token, null, Ct);
        await s.Catalog.SetEmbeddingProviderAsync(priv, "remote", Ct);

        // The owner switches back while the first of three batches is with the provider.
        remote.BeforeEmbed = async query =>
        {
            if (query) return;
            remote.BeforeEmbed = null;
            await s.Catalog.SetEmbeddingProviderAsync(priv, EmbeddingProviders.Internal, Ct);
        };
        await s.Store.TryCreateAsync(New("/Private/long.md", "# A\n## A1\nalpha\n# B\n## B1\nbeta\n# C\n## C1\ngamma\n"), Ct);
        await IdleAsync(s);

        Assert.Single(remote.Seen);
        var stored = await StoredEmbeddingsAsync(s, "Private");
        Assert.DoesNotContain(stored, e => e.Model == "remote:qwen3@64");
    }

    [Fact]
    public async Task The_provider_choice_is_not_readable_in_the_database()
    {
        var (s, _) = await TwoProvidersAsync();

        var dump = string.Join("\n", (await s.World.Db.GetCollection<BsonDocument>("vaults").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
            .Select(d => d.ToJson() + Encoding.UTF8.GetString(d["meta"].AsByteArray)));

        Assert.DoesNotContain("remote", dump);
    }

    [Fact]
    public async Task While_a_provider_is_down_its_vaults_are_left_to_the_words_and_their_notes_wait_for_it()
    {
        var (s, remote) = await TwoProvidersAsync();
        remote.Failure = new EmbeddingUnavailableException("the embedding provider Remote couldn't be reached (ConnectionError)", providerDown: true);

        var result = await s.Store.SearchAsync(P("/"), "hens", 10, Ct);
        Assert.Equal(SemanticState.Ready, result.State);
        Assert.Equal(P("/Work/farm.md"), Assert.Single(result.Hits).Path);
        Assert.Equal(0, result.Pending);
        Assert.Equal(["Private"], result.WordsOnlyVaults);
        Assert.Equal("Private: the embedding provider Remote couldn't be reached (ConnectionError)", result.Reason);

        var only = await s.Store.SearchAsync(P("/Private"), "hens", 10, Ct);
        Assert.Equal((SemanticState.Off, "the embedding provider Remote couldn't be reached (ConnectionError)"), (only.State, only.Reason));

        // A note written meanwhile stays queued, and is embedded once the provider is back.
        await s.Store.TryCreateAsync(New("/Private/later.md", "Written while the provider was down.\n"), Ct);
        await Task.Delay(200, Ct);
        Assert.True(s.Index.Queued > 0);
        remote.Failure = null;
        await IdleAsync(s);
        Assert.Contains(remote.Seen, t => t.Text.Contains("Written while"));
    }

    [Fact]
    public async Task A_vault_whose_provider_is_no_longer_configured_is_not_indexed_and_search_says_why()
    {
        var (s, _) = await TwoProvidersAsync();

        // The admin removed the provider: the next start has only the internal model.
        var later = await StartAsync(s.World);
        await IdleAsync(later);
        await later.Store.TryCreateAsync(New("/Private/new.md", "eggs\n"), Ct);
        await IdleAsync(later);

        Assert.All(later.Embedder.Seen, t => Assert.DoesNotContain("eggs", t.Text));
        var result = await later.Store.SearchAsync(P("/Private"), "eggs", 10, Ct);
        Assert.Equal(SemanticState.Off, result.State);
        Assert.Contains("'remote' isn't configured on this server any more", result.Reason);
    }

    [Fact]
    public async Task A_note_with_more_sections_than_a_batch_holds_is_embedded_in_several_calls()
    {
        var s = await StartAsync(embedder: new FakeEmbedder { MaxBatch = 2 });
        await s.Store.TryCreateAsync(New("/Work/long.md", "# A\n## A1\nalpha\n# B\n## B1\nbeta\n# C\n## C1\ngamma\n# D\n## D1\ndelta\n# E\n## E1\nepsilon\n"), Ct);
        await IdleAsync(s);

        Assert.Equal(5, s.Embedder.Embedded);
        Assert.Equal(5, Assert.Single(await StoredEmbeddingsAsync(s)).Chunks.Count);
    }

    /// <summary>The real model, where it has been downloaded (KUMBU_MODEL_DIR): meaning across languages.</summary>
    [Fact]
    public async Task The_real_model_ranks_by_meaning()
    {
        var dir = Environment.GetEnvironmentVariable("KUMBU_MODEL_DIR");
        Assert.SkipUnless(dir is not null && File.Exists(Path.Combine(dir, ModelFiles.Default, ModelFiles.Model)), "KUMBU_MODEL_DIR with the model not set");

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
