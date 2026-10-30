// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Kumbu.Core.Attachments;
using Kumbu.Server.Attachments;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Attachments;

/// <summary>A PDF's analysis, sealed with the vault key under its blob's id, in parts that each stay small.</summary>
[Collection("mongo")]
public sealed class PdfAnalysisStoreTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int MiB = 1024 * 1024;

    private sealed record World(VaultWorld Vaults, VaultCatalog Catalog, UnlockedVault Vault, PdfAnalyses Analyses, byte[] BlobId)
    {
        public IMongoCollection<BsonDocument> Parts => Vaults.Db.GetCollection<BsonDocument>("pdf_analyses");
    }

    private async Task<World> WorldAsync(string pdf = "%PDF-1.7 the paper")
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        await catalog.CreateAsync("Work", Token, (_, _) => Task.CompletedTask, Ct);
        var store = Store(catalog);
        var vault = (await store.UnlockedVaultsAsync(Ct)).Single();
        var blobId = Convert.FromBase64String(await store.PutBlobAsync("Work", Encoding.ASCII.GetBytes(pdf), Ct));
        return new World(world, catalog, vault, new PdfAnalyses(catalog, NullLogger<PdfAnalyses>.Instance), blobId);
    }

    /// <summary>About 20 KB of text for page <paramref name="n"/>, with a word only that page holds.</summary>
    private static string PageText(int n) =>
        $"Fledermaus{n}x Seite {n} über Flüsse. " + string.Concat(Enumerable.Repeat($"Zeile {n} der Seite, Wörter und Sätze. ", 520));

    private static SidecarAnalysis Book(int pages, Func<int, string> text, int regions = 1)
    {
        var list = Enumerable.Range(1, pages).Select(n => new PdfPage(
            n, 595, 842, n % 4 * 90, (PageType)(n % 3), text(n).Length, 0.4, 0.05 * (n % 3), 0.3, n % 3,
            [.. Enumerable.Range(0, regions).Select(r => new PdfRegion(r % 2 == 0 ? "drawing" : "image", 72 + r, 300, 451, 260))])).ToList();
        return new(new PdfAnalysis(PdfAnalyses.Analyser, pages + 400, pages, "Ein Titel", "Eine Autorin", list), [.. Enumerable.Range(1, pages).Select(text)]);
    }

    private static string Json(PdfAnalysis? a) => JsonSerializer.Serialize(a);

    [Fact]
    public async Task A_2000_page_analysis_of_40_MB_is_stored_in_parts_none_over_16_MB_and_reads_back()
    {
        var w = await WorldAsync();
        var book = Book(PdfAnalyses.MaxPages, PageText);
        Assert.True(book.PageTexts.Sum(t => Encoding.UTF8.GetByteCount(t)) > 16 * 1024 * 1024 * 2);

        Assert.NotNull(await w.Analyses.StoreAsync(w.Vault, w.BlobId, book, Ct));

        var docs = await w.Parts.Find(new BsonDocument("vault", w.Vault.Id)).ToListAsync(Ct);
        Assert.True(docs.Count > 30, $"{docs.Count} parts");
        Assert.Equal(Enumerable.Range(0, docs.Count), docs.Select(d => d["part"].AsInt32).Order());
        Assert.All(docs, d => Assert.True(d.ToBson().Length < 16 * MiB));
        // Text parts hold at most 1 MiB of JSON each (plus the seal's few bytes).
        Assert.All(docs.Where(d => d["part"].AsInt32 > 0), d => Assert.True(d["blob"].AsByteArray.Length <= MiB + 64, $"{d["blob"].AsByteArray.Length} bytes"));
        Assert.All(docs, d => Assert.Equal(w.BlobId, d["bid"].AsByteArray));

        Assert.Equal(Json(book.Analysis), Json(await w.Analyses.HeaderAsync(w.Vault, w.BlobId, Ct)));
        Assert.Equal(book.PageTexts, await w.Analyses.TextAsync(w.Vault, w.BlobId, 1, PdfAnalyses.MaxPages, Ct));
    }

    [Fact]
    public async Task Three_pages_are_read_from_the_one_part_that_holds_them()
    {
        var w = await WorldAsync();
        var book = Book(PdfAnalyses.MaxPages, PageText);
        await w.Analyses.StoreAsync(w.Vault, w.BlobId, book, Ct);
        var read = new List<int>();
        w.Analyses.TextPartRead = read.Add;

        var texts = await w.Analyses.TextAsync(w.Vault, w.BlobId, 1500, 1502, Ct);

        Assert.Equal([PageText(1500), PageText(1501), PageText(1502)], texts);
        Assert.Single(read);
    }

    [Fact]
    public async Task A_range_past_the_analysed_pages_is_cut_at_the_last()
    {
        var w = await WorldAsync();
        await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(5, PageText), Ct);

        Assert.Equal([PageText(4), PageText(5)], await w.Analyses.TextAsync(w.Vault, w.BlobId, 4, 9, Ct));
        Assert.Empty((await w.Analyses.TextAsync(w.Vault, w.BlobId, 6, 9, Ct))!);
    }

    [Fact]
    public async Task A_page_of_more_text_than_a_part_holds_spans_parts_and_reads_back_whole()
    {
        var w = await WorldAsync();
        var long3 = string.Concat(Enumerable.Repeat("Ä\"\\\n€𝄞 Hundert Zeichen langer Satz, der sich oft wiederholt und Sonderzeichen trägt. ", 30_000));
        var book = Book(4, n => n == 3 ? long3 : PageText(n));

        await w.Analyses.StoreAsync(w.Vault, w.BlobId, book, Ct);

        var docs = await w.Parts.Find(new BsonDocument("vault", w.Vault.Id)).ToListAsync(Ct);
        Assert.True(docs.Count > 3);
        Assert.All(docs.Where(d => d["part"].AsInt32 > 0), d => Assert.True(d["blob"].AsByteArray.Length <= MiB + 64));
        Assert.Equal(book.PageTexts, await w.Analyses.TextAsync(w.Vault, w.BlobId, 1, 4, Ct));
        Assert.Equal([long3], await w.Analyses.TextAsync(w.Vault, w.BlobId, 3, 3, Ct));
    }

    [Fact]
    public async Task The_sealed_parts_hold_no_text_title_or_measurement_in_plain()
    {
        var w = await WorldAsync();
        await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(300, PageText), Ct);

        var docs = await w.Parts.Find(new BsonDocument("vault", w.Vault.Id)).ToListAsync(Ct);
        foreach (var bytes in docs.Select(d => d.ToBson()))
        {
            foreach (var plain in new[] { "Fledermaus150x", "Ein Titel", "Eine Autorin", "Flüsse", "drawing", "pdfjs" })
            {
                Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(plain)) < 0, plain);
            }
        }

        Assert.All(docs, d => Assert.Equal(["_id", "vault", "bid", "part", "blob", "at"], d.Names));
    }

    [Fact]
    public async Task Another_vaults_key_opens_none_of_it()
    {
        var w = await WorldAsync();
        await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(60, PageText), Ct);
        await w.Catalog.CreateAsync("Other", Token, (_, _) => Task.CompletedTask, Ct);
        var other = (await Store(w.Catalog).UnlockedVaultsAsync(Ct)).Single(v => v.Name == "Other");
        // The documents copied under the other vault's id, as someone with the database could.
        var docs = await w.Parts.Find(new BsonDocument("vault", w.Vault.Id)).ToListAsync(Ct);
        foreach (var d in docs)
        {
            d["_id"] = ObjectId.GenerateNewId();
            d["vault"] = other.Id;
        }

        await w.Parts.InsertManyAsync(docs, cancellationToken: Ct);

        Assert.Null(await w.Analyses.ReadAsync(other, w.BlobId, Ct));
        Assert.Null(await w.Analyses.TextAsync(other, w.BlobId, 1, 3, Ct));
        Assert.NotNull(await w.Analyses.HeaderAsync(w.Vault, w.BlobId, Ct));
    }

    [Fact]
    public async Task A_part_moved_to_another_blob_or_part_number_doesnt_open()
    {
        var w = await WorldAsync();
        await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(3, PageText), Ct);
        var swapped = Convert.FromBase64String(await Store(w.Catalog).PutBlobAsync("Work", "%PDF-1.7 another"u8.ToArray(), Ct));
        await w.Parts.UpdateManyAsync(new BsonDocument("vault", w.Vault.Id), Builders<BsonDocument>.Update.Set("bid", swapped), cancellationToken: Ct);

        Assert.Null(await w.Analyses.ReadAsync(w.Vault, swapped, Ct));
    }

    [Fact]
    public async Task Storing_again_replaces_every_part()
    {
        var w = await WorldAsync();
        await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(400, PageText), Ct);

        await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(2, n => $"neu {n}"), Ct);

        Assert.Equal(2, await w.Parts.CountDocumentsAsync(new BsonDocument("vault", w.Vault.Id), cancellationToken: Ct));
        Assert.Equal(["neu 1", "neu 2"], await w.Analyses.TextAsync(w.Vault, w.BlobId, 1, 2, Ct));
    }

    [Fact]
    public async Task An_analysis_of_a_blob_the_vault_doesnt_hold_is_not_stored()
    {
        var w = await WorldAsync();
        var gone = new byte[32];

        Assert.Null(await w.Analyses.StoreAsync(w.Vault, gone, Book(2, PageText), Ct));
        Assert.False(await w.Analyses.StoreFailureAsync(w.Vault, gone, new PdfFailure("encrypted", "x"), Ct));

        Assert.Equal(0, await w.Parts.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_failure_is_stored_sealed_in_part_0_and_has_no_header_or_text()
    {
        var w = await WorldAsync();

        Assert.True(await w.Analyses.StoreFailureAsync(w.Vault, w.BlobId, new PdfFailure("encrypted", "this PDF is password-protected; Kumbu² can't read it"), Ct));

        var stored = await w.Analyses.ReadAsync(w.Vault, w.BlobId, Ct);
        Assert.Equal(new StoredPdfAnalysis(PdfAnalyses.Analyser, null, new PdfFailure("encrypted", "this PDF is password-protected; Kumbu² can't read it"), PdfAnalyses.Analyser), stored);
        Assert.True(stored!.Current);
        Assert.Null(await w.Analyses.HeaderAsync(w.Vault, w.BlobId, Ct));
        Assert.Null(await w.Analyses.TextAsync(w.Vault, w.BlobId, 1, 1, Ct));
        var doc = await w.Parts.Find(new BsonDocument("vault", w.Vault.Id)).SingleAsync(Ct);
        Assert.Equal(0, doc["part"].AsInt32);
        Assert.True(doc.ToBson().AsSpan().IndexOf("password"u8) < 0);
    }

    [Fact]
    public async Task An_analysis_stored_by_a_server_of_another_analyser_is_outdated()
    {
        var w = await WorldAsync();

        await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(2, PageText), "pdfjs-dist@5.0.0+rules.1", Ct);

        var stored = await w.Analyses.ReadAsync(w.Vault, w.BlobId, Ct);
        Assert.False(stored!.Current);
        Assert.Equal("pdfjs-dist@5.0.0+rules.1", stored.Requested);
    }

    [Fact]
    public async Task An_analysis_by_a_sidecar_of_another_release_is_current_and_shows_its_analyser()
    {
        var w = await WorldAsync();
        var book = Book(2, PageText);

        await w.Analyses.StoreAsync(w.Vault, w.BlobId, book with { Analysis = book.Analysis with { Analyser = "pdfjs-dist@7.0.0+rules.2" } }, Ct);

        var stored = await w.Analyses.ReadAsync(w.Vault, w.BlobId, Ct);
        Assert.True(stored!.Current);
        Assert.Equal(("pdfjs-dist@7.0.0+rules.2", PdfAnalyses.Analyser), (stored.Analysis!.Analyser, stored.Requested));
    }

    [Fact]
    public async Task Two_parts_with_their_numbers_swapped_dont_open()
    {
        var w = await WorldAsync();
        await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(150, PageText), Ct);
        Assert.True(await w.Parts.CountDocumentsAsync(new BsonDocument("vault", w.Vault.Id), cancellationToken: Ct) >= 3);

        await SwapAsync(w, 1, 2);
        Assert.Null(await w.Analyses.TextAsync(w.Vault, w.BlobId, 1, 150, Ct));
        Assert.NotNull(await w.Analyses.ReadAsync(w.Vault, w.BlobId, Ct));

        await SwapAsync(w, 1, 2);
        await SwapAsync(w, 0, 1);
        Assert.Null(await w.Analyses.ReadAsync(w.Vault, w.BlobId, Ct));
        Assert.Null(await w.Analyses.TextAsync(w.Vault, w.BlobId, 1, 150, Ct));
    }

    /// <summary>Gives parts <paramref name="a"/> and <paramref name="b"/> each other's numbers, past the unique index.</summary>
    private static async Task SwapAsync(World w, int a, int b)
    {
        var of = (int part) => new BsonDocument { ["vault"] = w.Vault.Id, ["part"] = part };
        await w.Parts.UpdateOneAsync(of(a), Builders<BsonDocument>.Update.Set("part", -1), cancellationToken: Ct);
        await w.Parts.UpdateOneAsync(of(b), Builders<BsonDocument>.Update.Set("part", a), cancellationToken: Ct);
        await w.Parts.UpdateOneAsync(of(-1), Builders<BsonDocument>.Update.Set("part", b), cancellationToken: Ct);
    }

    [Fact]
    public async Task A_store_that_stops_after_its_text_leaves_no_analysis_and_the_next_store_or_a_sweep_clears_it()
    {
        var w = await WorldAsync();
        await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(100, PageText), Ct);
        w.Analyses.StoreFaultAt = _ => throw new InvalidOperationException("crash before part 0");

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(150, PageText), Ct));

        // The old analysis went first; the new text without its part 0 is no analysis.
        Assert.Null(await w.Analyses.ReadAsync(w.Vault, w.BlobId, Ct));
        Assert.Null(await w.Analyses.TextAsync(w.Vault, w.BlobId, 1, 1, Ct));
        var headless = await w.Parts.CountDocumentsAsync(new BsonDocument("vault", w.Vault.Id), cancellationToken: Ct);
        Assert.True(headless >= 3);
        Assert.Equal(0, await w.Parts.CountDocumentsAsync(new BsonDocument { ["vault"] = w.Vault.Id, ["part"] = 0 }, cancellationToken: Ct));

        // The blob is referenced, so only the parts' own state decides.
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7 the paper");
        var state = new AttachmentState("application/pdf", bytes.Length, null, null, Kumbu.Core.Notes.VersionToken.Of(bytes), Convert.ToBase64String(w.BlobId));
        Assert.Empty(await Store(w.Catalog).TryCommitAsync(
            new Kumbu.Core.Notes.NoteBatch { AttachmentCreates = { new StoredAttachment(P("/Work/paper.pdf"), state, 1, "lea", DateTime.UtcNow, [], null) } }, "lea", DateTime.UtcNow, Ct));

        // A sweep within the grace period leaves them (a store may be under way in another process), later it doesn't.
        var sweep = new BlobSweep(w.Catalog, NullLogger.Instance);
        await sweep.RunAsync(w.Vault, DateTime.UtcNow, Ct);
        Assert.Equal(headless, await w.Parts.CountDocumentsAsync(new BsonDocument("vault", w.Vault.Id), cancellationToken: Ct));
        await sweep.RunAsync(w.Vault, DateTime.UtcNow + BlobSweep.Grace + TimeSpan.FromMinutes(1), Ct);
        Assert.Equal(0, await w.Parts.CountDocumentsAsync(new BsonDocument("vault", w.Vault.Id), cancellationToken: Ct));

        // And a store after another crash replaces what that one left.
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(150, PageText), Ct));
        w.Analyses.StoreFaultAt = null;
        await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(2, n => $"neu {n}"), Ct);
        Assert.Equal(2, await w.Parts.CountDocumentsAsync(new BsonDocument("vault", w.Vault.Id), cancellationToken: Ct));
        Assert.Equal(["neu 1", "neu 2"], await w.Analyses.TextAsync(w.Vault, w.BlobId, 1, 2, Ct));
    }

    [Fact]
    public async Task At_most_32_regions_a_page_are_kept()
    {
        var w = await WorldAsync();

        var stored = await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(2, PageText, regions: 40), Ct);

        Assert.All(stored!.Pages, p => Assert.Equal(PdfAnalyses.MaxRegions, p.Regions.Count));
        Assert.Equal(Json(stored), Json(await w.Analyses.HeaderAsync(w.Vault, w.BlobId, Ct)));
    }

    [Fact]
    public async Task The_text_for_search_is_cut_per_page_and_never_inside_a_surrogate_pair()
    {
        var w = await WorldAsync();
        var long1 = new string('a', 1999) + "😀" + new string('b', 300_000);
        Assert.NotNull(await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(3, n => n switch { 1 => long1, 2 => "short page", _ => "" }), Ct));

        var texts = await w.Analyses.SearchTextAsync(w.Vault, w.BlobId, 2000, Ct);

        Assert.Equal([new string('a', 1999), "short page", ""], texts);
    }
}
