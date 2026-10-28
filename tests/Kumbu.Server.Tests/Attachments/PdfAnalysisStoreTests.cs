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
        Assert.Equal(new StoredPdfAnalysis(PdfAnalyses.Analyser, null, new PdfFailure("encrypted", "this PDF is password-protected; Kumbu² can't read it")), stored);
        Assert.True(stored!.Current);
        Assert.Null(await w.Analyses.HeaderAsync(w.Vault, w.BlobId, Ct));
        Assert.Null(await w.Analyses.TextAsync(w.Vault, w.BlobId, 1, 1, Ct));
        var doc = await w.Parts.Find(new BsonDocument("vault", w.Vault.Id)).SingleAsync(Ct);
        Assert.Equal(0, doc["part"].AsInt32);
        Assert.True(doc.ToBson().AsSpan().IndexOf("password"u8) < 0);
    }

    [Fact]
    public async Task An_analysis_by_another_analyser_is_outdated()
    {
        var w = await WorldAsync();
        var book = Book(2, PageText);

        await w.Analyses.StoreAsync(w.Vault, w.BlobId, book with { Analysis = book.Analysis with { Analyser = "pdfjs-dist@5.0.0+rules.1" } }, Ct);

        var stored = await w.Analyses.ReadAsync(w.Vault, w.BlobId, Ct);
        Assert.False(stored!.Current);
        Assert.Equal("pdfjs-dist@5.0.0+rules.1", stored.Analysis!.Analyser);
    }

    [Fact]
    public async Task At_most_32_regions_a_page_are_kept()
    {
        var w = await WorldAsync();

        var stored = await w.Analyses.StoreAsync(w.Vault, w.BlobId, Book(2, PageText, regions: 40), Ct);

        Assert.All(stored!.Pages, p => Assert.Equal(PdfAnalyses.MaxRegions, p.Regions.Count));
        Assert.Equal(Json(stored), Json(await w.Analyses.HeaderAsync(w.Vault, w.BlobId, Ct)));
    }
}
