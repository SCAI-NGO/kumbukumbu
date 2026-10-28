// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Attachments;
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Attachments;

/// <summary>Why a PDF has no analysis, in a sentence for the model (<c>pdf</c> says it).</summary>
public sealed record PdfFailure(string Code, string Message);

/// <summary>What part 0 of a PDF's analysis holds: the measurements, or the failure that took their place.</summary>
public sealed record StoredPdfAnalysis(string Analyser, PdfAnalysis? Analysis, PdfFailure? Failure)
{
    /// <summary>Made by this server's analyser; another one's counts as outdated and is made again when asked for.</summary>
    public bool Current => Analyser == PdfAnalyses.Analyser;
}

/// <summary>
/// The analyses of PDFs (collection <c>pdf_analyses</c>), one per blob, which they share the life of: a blob's
/// release, the vault's deletion and its re-keying take them along (<see cref="BlobSweep"/>,
/// <see cref="VaultCatalog.ContentCollections"/>, <see cref="VaultCatalog"/>'s re-keying passes). Part 0 holds the
/// measurements of every page and where each page's text is; parts 1… hold the text, at most <see cref="PartBytes"/>
/// of JSON each, so that 2,000 long pages stay far from MongoDB's 16 MB a document and a few pages are read without
/// the rest. Each part is sealed under the vault key, bound to the blob's id and its number. Nothing of it is kept in
/// the unlocked vault's working set.
/// </summary>
public sealed class PdfAnalyses(VaultCatalog catalog, ILogger<PdfAnalyses> log)
{
    /// <summary>PDFs with more pages are analysed up to this one, and say so.</summary>
    public const int MaxPages = 2000;

    /// <summary>The sidecar's <c>ANALYSER</c> (<c>pdf/src/analyse.ts</c>): an analysis by another counts as outdated.</summary>
    public const string Analyser = "pdfjs-dist@6.4.299+rules.1";

    /// <summary>Regions kept per page: <c>pdf info</c> shows a few, and part 0 must stay small however many a page has.</summary>
    public const int MaxRegions = 32;

    /// <summary>JSON per text part, at most.</summary>
    internal const int PartBytes = 1024 * 1024;

    /// <summary>A page's text longer than this goes into several pieces: even escaped to six bytes a character, a piece fits a part.</summary>
    private const int PieceChars = 128 * 1024;

    private static FilterDefinitionBuilder<PdfAnalysisDocument> F => Builders<PdfAnalysisDocument>.Filter;

    /// <summary>For tests: called with the number of each text part read.</summary>
    internal Action<int>? TextPartRead { get; set; }

    /// <summary>
    /// Replaces the blob's analysis, all parts in one transaction, and takes its job off the queue. Null, and nothing
    /// written, if the vault can't be written now (deleted, being re-keyed) or no longer holds the blob. Returns the
    /// analysis as <see cref="HeaderAsync"/> reads it back.
    /// </summary>
    public async Task<PdfAnalysis?> StoreAsync(UnlockedVault vault, byte[] blobId, SidecarAnalysis analysis, CancellationToken ct)
    {
        var a = analysis.Analysis;
        var pages = a.Pages.Select(p => new PageRecord(
            p.Number, p.Width, p.Height, p.Rotation, p.Type, p.Chars, p.TextCoverage, p.ImageCoverage, p.VectorCoverage, p.Images,
            [.. p.Regions.Take(MaxRegions).Select(r => new RegionRecord(r.Kind, r.X, r.Y, r.Width, r.Height))])).ToList();
        var texts = Chunk(analysis.PageTexts);
        var header = new HeaderRecord(a.Analyser, a.PageCount, a.AnalysedPages, a.Title, a.Author, pages,
            [.. texts.Select((t, i) => new PartRecord(i + 1, t.Segments[0].Page, t.Segments[^1].Page))], null);
        var now = catalog.Now;
        List<PdfAnalysisDocument> docs =
        [
            Doc(vault, blobId, 0, vault.SealPdfAnalysis(blobId, 0, header), now),
            .. texts.Select((t, i) => Doc(vault, blobId, i + 1, vault.SealPdfAnalysis(blobId, i + 1, t), now)),
        ];
        return await WriteAsync(vault, blobId, docs, ct) ? header.ToAnalysis() : null;
    }

    /// <summary>Stores why the PDF can't be analysed in place of an analysis; false as <see cref="StoreAsync"/> is null.</summary>
    public Task<bool> StoreFailureAsync(UnlockedVault vault, byte[] blobId, PdfFailure failure, CancellationToken ct)
    {
        var header = new HeaderRecord(Analyser, 0, 0, null, null, [], [], failure);
        return WriteAsync(vault, blobId, [Doc(vault, blobId, 0, vault.SealPdfAnalysis(blobId, 0, header), catalog.Now)], ct);
    }

    /// <summary>Part 0, opened; null if there is none, or it doesn't open (then it counts as missing and is made again).</summary>
    public async Task<StoredPdfAnalysis?> ReadAsync(UnlockedVault vault, byte[] blobId, CancellationToken ct) =>
        await ReadHeaderAsync(vault, blobId, ct) is { } h ? new StoredPdfAnalysis(h.Analyser, h.Failure is null ? h.ToAnalysis() : null, h.Failure) : null;

    /// <summary>The measurements without the text; null without an analysis or for a failed one.</summary>
    public async Task<PdfAnalysis?> HeaderAsync(UnlockedVault vault, byte[] blobId, CancellationToken ct) => (await ReadAsync(vault, blobId, ct))?.Analysis;

    /// <summary>
    /// The text of pages <paramref name="first"/> to <paramref name="last"/> (index 0 = page <paramref name="first"/>),
    /// cut at the last page analysed; read from the parts that hold them only. Null without an analysis, for a failed
    /// one, or if a part is missing or doesn't open.
    /// </summary>
    public async Task<IReadOnlyList<string>?> TextAsync(UnlockedVault vault, byte[] blobId, int first, int last, CancellationToken ct)
    {
        if (await ReadHeaderAsync(vault, blobId, ct) is not { Failure: null } header) return null;
        first = Math.Max(1, first);
        last = Math.Min(last, header.AnalysedPages);
        if (first > last) return [];

        var wanted = header.Parts.Where(p => p.First <= last && p.Last >= first).Select(p => p.Part).ToList();
        var docs = await catalog.PdfParts.Find(F.Eq(d => d.Vault, vault.Id) & F.Eq(d => d.BlobId, blobId) & F.In(d => d.Part, wanted))
            .SortBy(d => d.Part).ToListAsync(ct);
        if (docs.Count != wanted.Count) return Missing();

        var pages = Enumerable.Range(0, last - first + 1).Select(_ => new StringBuilder()).ToArray();
        foreach (var doc in docs)
        {
            TextPartRead?.Invoke(doc.Part);
            TextRecord text;
            try
            {
                text = vault.OpenPdfAnalysis<TextRecord>(doc);
            }
            catch (Exception e) when (e is CryptographicException or JsonException or InvalidDataException)
            {
                return Missing();
            }

            foreach (var s in text.Segments.Where(s => s.Page >= first && s.Page <= last)) pages[s.Page - first].Append(s.Text);
        }

        return [.. pages.Select(p => p.ToString())];

        IReadOnlyList<string>? Missing()
        {
            log.LogWarning("A text part of a PDF analysis of vault {VaultId} is missing or doesn't open", vault.Id);
            return null;
        }
    }

    private async Task<HeaderRecord?> ReadHeaderAsync(UnlockedVault vault, byte[] blobId, CancellationToken ct)
    {
        var doc = await catalog.PdfParts.Find(F.Eq(d => d.Vault, vault.Id) & F.Eq(d => d.BlobId, blobId) & F.Eq(d => d.Part, 0)).FirstOrDefaultAsync(ct);
        if (doc is null) return null;
        try
        {
            return vault.OpenPdfAnalysis<HeaderRecord>(doc);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or InvalidDataException)
        {
            log.LogWarning("A PDF analysis of vault {VaultId} doesn't open; it counts as missing", vault.Id);
            return null;
        }
    }

    /// <summary>
    /// Under the vault's gate, which the blob sweep, deletions and re-keying take too, and only while the vault may be
    /// written: an analysis never outlives its blob or vault, nor is it sealed under a key a re-keying replaced. Their
    /// interrupt stops the wait.
    /// </summary>
    private async Task<bool> WriteAsync(UnlockedVault vault, byte[] blobId, List<PdfAnalysisDocument> docs, CancellationToken ct)
    {
        if (!catalog.MayIndex(vault)) return false;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, catalog.InterruptToken(vault.Id));
        try
        {
            await vault.Gate.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            if (!catalog.MayIndex(vault)) return false;
            using var session = await catalog.StartSessionAsync(ct);
            return await session.WithTransactionAsync(
                async (s, token) =>
                {
                    var b = Builders<BlobDocument>.Filter;
                    var held = await catalog.Blobs.Find(s, b.Eq(d => d.Vault, vault.Id) & b.Eq(d => d.BlobId, blobId), new FindOptions { Hint = EncryptedNoteStore.BlobTimeIndex })
                        .Project(new MongoDB.Bson.BsonDocument { { "bid", 1 }, { "_id", 0 } }).AnyAsync(token);
                    if (!held) return false;
                    await catalog.PdfParts.DeleteManyAsync(s, F.Eq(d => d.Vault, vault.Id) & F.Eq(d => d.BlobId, blobId), cancellationToken: token);
                    await catalog.PdfParts.InsertManyAsync(s, docs, cancellationToken: token);
                    var j = Builders<PdfJobDocument>.Filter;
                    await catalog.PdfJobs.DeleteOneAsync(s, j.Eq(d => d.Vault, vault.Id) & j.Eq(d => d.BlobId, blobId), cancellationToken: token);
                    return true;
                },
                cancellationToken: ct);
        }
        finally
        {
            vault.Gate.Release();
        }
    }

    private static PdfAnalysisDocument Doc(UnlockedVault vault, byte[] blobId, int part, byte[] blob, DateTime at) =>
        new() { Id = MongoDB.Bson.ObjectId.GenerateNewId(), Vault = vault.Id, BlobId = blobId, Part = part, Blob = blob, At = at };

    /// <summary>The pages' text in parts of at most <see cref="PartBytes"/> of JSON, in page order; a page longer than a
    /// piece is split across pieces (never inside a surrogate pair), which may go to several parts.</summary>
    private static List<TextRecord> Chunk(IReadOnlyList<string> texts)
    {
        var parts = new List<TextRecord>();
        var current = new List<SegmentRecord>();
        long size = 0;
        for (var i = 0; i < texts.Count; i++)
        {
            foreach (var piece in Pieces(texts[i]))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(piece).Length + 24;
                if (current.Count > 0 && size + bytes > PartBytes - 64)
                {
                    parts.Add(new TextRecord(current));
                    (current, size) = ([], 0);
                }

                current.Add(new SegmentRecord(i + 1, piece));
                size += bytes;
            }
        }

        if (current.Count > 0) parts.Add(new TextRecord(current));
        return parts;
    }

    private static IEnumerable<string> Pieces(string text)
    {
        if (text.Length <= PieceChars)
        {
            yield return text;
            yield break;
        }

        for (var at = 0; at < text.Length;)
        {
            var length = Math.Min(PieceChars, text.Length - at);
            if (at + length < text.Length && char.IsHighSurrogate(text[at + length - 1])) length--;
            yield return text.Substring(at, length);
            at += length;
        }
    }
}

/// <summary>Part 0 as sealed. <see cref="Parts"/> says which pages each text part holds.</summary>
internal sealed record HeaderRecord(
    string Analyser, int PageCount, int AnalysedPages, string? Title, string? Author, List<PageRecord> Pages, List<PartRecord> Parts, PdfFailure? Failure)
{
    public PdfAnalysis ToAnalysis() => new(Analyser, PageCount, AnalysedPages, Title, Author, [.. Pages.Select(p => p.ToPage())]);
}

internal sealed record PageRecord(
    int Number, double Width, double Height, int Rotation, PageType Type, int Chars,
    double TextCoverage, double ImageCoverage, double VectorCoverage, int Images, List<RegionRecord> Regions)
{
    public PdfPage ToPage() => new(
        Number, Width, Height, Rotation, Type, Chars, TextCoverage, ImageCoverage, VectorCoverage, Images,
        [.. Regions.Select(r => new PdfRegion(r.Kind, r.X, r.Y, r.Width, r.Height))]);
}

internal sealed record RegionRecord(string Kind, double X, double Y, double Width, double Height);

internal sealed record PartRecord(int Part, int First, int Last);

/// <summary>A text part as sealed: pieces of page text in page order.</summary>
internal sealed record TextRecord(List<SegmentRecord> Segments);

internal sealed record SegmentRecord(int Page, string Text);
