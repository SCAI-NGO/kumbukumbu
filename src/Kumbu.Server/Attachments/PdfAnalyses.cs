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
/// <param name="Analyser">The sidecar's, as it answered (for display).</param>
/// <param name="Requested">This server's <see cref="PdfAnalyses.Analyser"/> when it stored the analysis.</param>
public sealed record StoredPdfAnalysis(string Analyser, PdfAnalysis? Analysis, PdfFailure? Failure, string Requested)
{
    /// <summary>
    /// Stored by a server of this analyser; one stored by an older or newer one counts as outdated and is made again
    /// when asked for. Not the sidecar's own string: a sidecar of another release would make every analysis outdated
    /// for ever.
    /// </summary>
    public bool Current => Requested == PdfAnalyses.Analyser;
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

    /// <summary>Text parts inserted at once while storing: a few, so no write carries tens of MB.</summary>
    private const int BatchBytes = 4 * 1024 * 1024;

    /// <summary>
    /// After an analysis, or the failure in its place, is stored for a blob of a vault: the search index embeds the
    /// pages of the PDFs that hold it. On the storing thread; a handler must not block.
    /// </summary>
    public event Action<UnlockedVault, byte[]>? Stored;

    /// <summary>For tests: called with the number of each text part read.</summary>
    internal Action<int>? TextPartRead { get; set; }

    /// <summary>For tests: awaited at <c>"after-text"</c>, once a store's text parts are in; a throw there is a crash.</summary>
    internal Func<string, Task>? StoreFaultAt { get; set; }

    /// <summary>
    /// Replaces the blob's analysis and takes its job off the queue (see <see cref="WriteAsync"/>). Null, and nothing
    /// stored, if the vault can't be written now (deleted, being re-keyed) or no longer holds the blob. Returns the
    /// analysis as <see cref="HeaderAsync"/> reads it back.
    /// </summary>
    public Task<PdfAnalysis?> StoreAsync(UnlockedVault vault, byte[] blobId, SidecarAnalysis analysis, CancellationToken ct) =>
        StoreAsync(vault, blobId, analysis, Analyser, ct);

    /// <summary>As stored by a server whose analyser is <paramref name="requested"/> (tests make an outdated one).</summary>
    internal async Task<PdfAnalysis?> StoreAsync(UnlockedVault vault, byte[] blobId, SidecarAnalysis analysis, string requested, CancellationToken ct)
    {
        var a = analysis.Analysis;
        var pages = a.Pages.Select(p => new PageRecord(
            p.Number, p.Width, p.Height, p.Rotation, p.Type, p.Chars, p.TextCoverage, p.ImageCoverage, p.VectorCoverage, p.Images,
            [.. p.Regions.Take(MaxRegions).Select(r => new RegionRecord(r.Kind, r.X, r.Y, r.Width, r.Height))])).ToList();
        var texts = Chunk(analysis.PageTexts);
        var header = new HeaderRecord(a.Analyser, a.PageCount, a.AnalysedPages, a.Title, a.Author, pages,
            [.. texts.Select((t, i) => new PartRecord(i + 1, t.Segments[0].Page, t.Segments[^1].Page))], null, requested);
        var now = catalog.Now;
        var head = Doc(vault, blobId, 0, vault.SealPdfAnalysis(blobId, 0, header), now);
        var parts = texts.Select((t, i) => Doc(vault, blobId, i + 1, vault.SealPdfAnalysis(blobId, i + 1, t), now)).ToList();
        if (!await WriteAsync(vault, blobId, head, parts, ct)) return null;
        Stored?.Invoke(vault, blobId);
        return header.ToAnalysis();
    }

    /// <summary>Stores why the PDF can't be analysed in place of an analysis; false as <see cref="StoreAsync"/> is null.</summary>
    public async Task<bool> StoreFailureAsync(UnlockedVault vault, byte[] blobId, PdfFailure failure, CancellationToken ct)
    {
        var header = new HeaderRecord(Analyser, 0, 0, null, null, [], [], failure, Analyser);
        if (!await WriteAsync(vault, blobId, Doc(vault, blobId, 0, vault.SealPdfAnalysis(blobId, 0, header), catalog.Now), [], ct)) return false;
        Stored?.Invoke(vault, blobId);
        return true;
    }

    /// <summary>Part 0, opened; null if there is none (text parts without it are an unfinished store), or it doesn't open
    /// (then it counts as missing and is made again).</summary>
    public async Task<StoredPdfAnalysis?> ReadAsync(UnlockedVault vault, byte[] blobId, CancellationToken ct) =>
        await ReadHeaderAsync(vault, blobId, null, ct) is { } h
            ? new StoredPdfAnalysis(h.Analyser, h.Failure is null ? h.ToAnalysis() : null, h.Failure, h.Requested ?? h.Analyser)
            : null;

    /// <summary>The measurements without the text; null without an analysis or for a failed one.</summary>
    public async Task<PdfAnalysis?> HeaderAsync(UnlockedVault vault, byte[] blobId, CancellationToken ct) => (await ReadAsync(vault, blobId, ct))?.Analysis;

    /// <summary>
    /// The text of pages <paramref name="first"/> to <paramref name="last"/> (index 0 = page <paramref name="first"/>),
    /// cut at the last page analysed; read from the parts that hold them only, at one point in time with part 0 (a
    /// snapshot), so a store meanwhile can't mix two analyses. Null without an analysis, for a failed one, or if a part
    /// is missing or doesn't open.
    /// </summary>
    public async Task<IReadOnlyList<string>?> TextAsync(UnlockedVault vault, byte[] blobId, int first, int last, CancellationToken ct)
    {
        using var session = await catalog.StartSnapshotSessionAsync(ct);
        if (await ReadHeaderAsync(vault, blobId, session, ct) is not { Failure: null } header) return null;
        first = Math.Max(1, first);
        last = Math.Min(last, header.AnalysedPages);
        if (first > last) return [];

        var wanted = header.Parts.Where(p => p.First <= last && p.Last >= first).Select(p => p.Part).ToList();
        var docs = await catalog.PdfParts.Find(session, F.Eq(d => d.Vault, vault.Id) & F.Eq(d => d.BlobId, blobId) & F.In(d => d.Part, wanted))
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

    /// <summary>
    /// The text of every page analysed (index 0 = page 1), each cut after <paramref name="maxChars"/>, for the search
    /// index: the parts are read one at a time, so a long PDF never has all its text in memory. Read at one point in
    /// time with part 0, as <see cref="TextAsync"/>. An empty list for a failed analysis (there is no text to search);
    /// null without an analysis, or if a part is missing or doesn't open.
    /// </summary>
    public async Task<IReadOnlyList<string>?> SearchTextAsync(UnlockedVault vault, byte[] blobId, int maxChars, CancellationToken ct)
    {
        using var session = await catalog.StartSnapshotSessionAsync(ct);
        if (await ReadHeaderAsync(vault, blobId, session, ct) is not { } header) return null;
        if (header.Failure is not null || header.AnalysedPages == 0) return [];

        var pages = Enumerable.Range(0, header.AnalysedPages).Select(_ => new StringBuilder()).ToArray();
        var cut = new bool[pages.Length];
        var wanted = header.Parts.Select(p => p.Part).ToList();
        var read = 0;
        using var cursor = await catalog.PdfParts.Find(session, F.Eq(d => d.Vault, vault.Id) & F.Eq(d => d.BlobId, blobId) & F.In(d => d.Part, wanted), new FindOptions { BatchSize = 1 })
            .SortBy(d => d.Part).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var doc in cursor.Current)
            {
                read++;
                TextPartRead?.Invoke(doc.Part);
                TextRecord text;
                try
                {
                    text = vault.OpenPdfAnalysis<TextRecord>(doc);
                }
                catch (Exception e) when (e is CryptographicException or JsonException or InvalidDataException)
                {
                    log.LogWarning("A text part of a PDF analysis of vault {VaultId} is missing or doesn't open", vault.Id);
                    return null;
                }

                foreach (var s in text.Segments.Where(s => s.Page >= 1 && s.Page <= pages.Length))
                {
                    if (cut[s.Page - 1]) continue;
                    var page = pages[s.Page - 1];
                    var length = Math.Min(s.Text.Length, maxChars - page.Length);
                    if (length < s.Text.Length)
                    {
                        // Cut, and not between the halves of a surrogate pair; what follows on the page isn't taken.
                        if (length > 0 && char.IsHighSurrogate(s.Text[length - 1])) length--;
                        cut[s.Page - 1] = true;
                    }

                    page.Append(s.Text.AsSpan(0, length));
                }
            }
        }

        if (read != wanted.Count)
        {
            log.LogWarning("A text part of a PDF analysis of vault {VaultId} is missing or doesn't open", vault.Id);
            return null;
        }

        return [.. pages.Select(p => p.ToString())];
    }

    /// <summary>
    /// Queues the blobs for analysis (collection <c>pdf_jobs</c>, durable, no content), once per blob, leaving out
    /// those that have an analysis unless <paramref name="evenIfAnalysed"/> (an outdated one asked for). Nothing while
    /// the vault can't be written. Returns how many jobs are new.
    /// </summary>
    public async Task<int> EnqueueAsync(UnlockedVault vault, IReadOnlyCollection<byte[]> blobIds, bool evenIfAnalysed, CancellationToken ct)
    {
        if (blobIds.Count == 0 || !catalog.MayIndex(vault)) return 0;
        IEnumerable<byte[]> wanted = blobIds;
        if (!evenIfAnalysed)
        {
            var analysed = (await catalog.PdfParts.Find(F.Eq(d => d.Vault, vault.Id) & F.In(d => d.BlobId, blobIds) & F.Eq(d => d.Part, 0))
                .Project(d => d.BlobId).ToListAsync(ct)).Select(Convert.ToBase64String).ToHashSet();
            wanted = blobIds.Where(b => !analysed.Contains(Convert.ToBase64String(b)));
        }

        var queue = wanted.ToList();
        if (queue.Count == 0) return 0;

        // Under the gate a deletion takes too: no job outlives its vault.
        return await UnderGateAsync(vault, 0, async () =>
        {
            var added = 0;
            var j = Builders<PdfJobDocument>.Filter;
            foreach (var blobId in queue)
            {
                try
                {
                    var result = await catalog.PdfJobs.UpdateOneAsync(
                        j.Eq(d => d.Vault, vault.Id) & j.Eq(d => d.BlobId, blobId),
                        Builders<PdfJobDocument>.Update.SetOnInsert(d => d.At, catalog.Now).SetOnInsert(d => d.Tries, 0),
                        new UpdateOptions { IsUpsert = true },
                        ct);
                    if (result.UpsertedId is not null) added++;
                }
                catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
                {
                    // Queued by someone else at the same moment.
                }
            }

            return added;
        }, ct);
    }

    /// <summary>The vault's jobs, oldest first.</summary>
    public Task<List<PdfJobDocument>> JobsAsync(string vaultId, CancellationToken ct) =>
        catalog.PdfJobs.Find(Builders<PdfJobDocument>.Filter.Eq(d => d.Vault, vaultId)).SortBy(d => d.At).ToListAsync(ct);

    public Task DeleteJobAsync(string vaultId, byte[] blobId, CancellationToken ct) =>
        catalog.PdfJobs.DeleteOneAsync(Builders<PdfJobDocument>.Filter.Eq(d => d.Vault, vaultId) & Builders<PdfJobDocument>.Filter.Eq(d => d.BlobId, blobId), ct);

    /// <summary>Counts a failed try; returns the tries so far (0 if the job is gone).</summary>
    public async Task<int> CountTryAsync(string vaultId, byte[] blobId, CancellationToken ct)
    {
        var job = await catalog.PdfJobs.FindOneAndUpdateAsync(
            Builders<PdfJobDocument>.Filter.Eq(d => d.Vault, vaultId) & Builders<PdfJobDocument>.Filter.Eq(d => d.BlobId, blobId),
            Builders<PdfJobDocument>.Update.Inc(d => d.Tries, 1),
            new FindOneAndUpdateOptions<PdfJobDocument> { ReturnDocument = ReturnDocument.After },
            ct);
        return job?.Tries ?? 0;
    }

    private async Task<HeaderRecord?> ReadHeaderAsync(UnlockedVault vault, byte[] blobId, IClientSessionHandle? session, CancellationToken ct)
    {
        var filter = F.Eq(d => d.Vault, vault.Id) & F.Eq(d => d.BlobId, blobId) & F.Eq(d => d.Part, 0);
        var doc = await (session is null ? catalog.PdfParts.Find(filter) : catalog.PdfParts.Find(session, filter)).FirstOrDefaultAsync(ct);
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
    /// Stores in stages, so no write carries tens of MB: the old parts go (part 0 first), the text parts go in batches
    /// of a few MB, and one small transaction then checks the vault still holds the blob and all the text parts are
    /// there, inserts part 0 and takes the job off the queue. Until part 0 is in, there is no analysis: a crash in
    /// between leaves text parts without it, which read as none and go with the next store or the blob sweep.
    /// All of it under one hold of the vault's gate (see <see cref="UnderGateAsync{T}"/>).
    /// </summary>
    private Task<bool> WriteAsync(UnlockedVault vault, byte[] blobId, PdfAnalysisDocument head, List<PdfAnalysisDocument> texts, CancellationToken ct) =>
        UnderGateAsync(vault, false, async () =>
        {
            var mine = F.Eq(d => d.Vault, vault.Id) & F.Eq(d => d.BlobId, blobId);
            await catalog.PdfParts.DeleteOneAsync(mine & F.Eq(d => d.Part, 0), ct);
            await catalog.PdfParts.DeleteManyAsync(mine, ct);
            var (batch, bytes) = (new List<PdfAnalysisDocument>(), 0L);
            foreach (var part in texts)
            {
                if (batch.Count > 0 && bytes + part.Blob.Length > BatchBytes)
                {
                    await catalog.PdfParts.InsertManyAsync(batch, cancellationToken: ct);
                    (batch, bytes) = ([], 0);
                }

                batch.Add(part);
                bytes += part.Blob.Length;
            }

            if (batch.Count > 0) await catalog.PdfParts.InsertManyAsync(batch, cancellationToken: ct);
            if (texts.Count > 0 && StoreFaultAt is { } fault) await fault("after-text");

            using var session = await catalog.StartSessionAsync(ct);
            var stored = await session.WithTransactionAsync(
                async (s, token) =>
                {
                    var b = Builders<BlobDocument>.Filter;
                    var held = await catalog.Blobs.Find(s, b.Eq(d => d.Vault, vault.Id) & b.Eq(d => d.BlobId, blobId), new FindOptions { Hint = EncryptedNoteStore.BlobTimeIndex })
                        .Project(new MongoDB.Bson.BsonDocument { { "bid", 1 }, { "_id", 0 } }).AnyAsync(token);
                    if (!held || await catalog.PdfParts.CountDocumentsAsync(s, mine & F.Gt(d => d.Part, 0), cancellationToken: token) != texts.Count) return false;
                    await catalog.PdfParts.InsertOneAsync(s, head, cancellationToken: token);
                    var j = Builders<PdfJobDocument>.Filter;
                    await catalog.PdfJobs.DeleteOneAsync(s, j.Eq(d => d.Vault, vault.Id) & j.Eq(d => d.BlobId, blobId), cancellationToken: token);
                    return true;
                },
                cancellationToken: ct);
            if (!stored) await catalog.PdfParts.DeleteManyAsync(mine, ct);
            return stored;
        }, ct);

    /// <summary>
    /// Runs <paramref name="work"/> under the vault's gate, which the blob sweep, deletions and re-keying take too, and
    /// only while the vault may be written: an analysis or a job never outlives its blob or vault, nor is an analysis
    /// sealed under a key a re-keying replaced. Their interrupt stops the wait; then <paramref name="otherwise"/>.
    /// </summary>
    private async Task<T> UnderGateAsync<T>(UnlockedVault vault, T otherwise, Func<Task<T>> work, CancellationToken ct)
    {
        if (!catalog.MayIndex(vault)) return otherwise;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, catalog.InterruptToken(vault.Id));
        try
        {
            await vault.Gate.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return otherwise;
        }

        try
        {
            return catalog.MayIndex(vault) ? await work() : otherwise;
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

/// <summary>Part 0 as sealed. <see cref="Parts"/> says which pages each text part holds;
/// <see cref="Requested"/> is this server's analyser when it stored it; <see cref="Analyser"/> the sidecar's.</summary>
internal sealed record HeaderRecord(
    string Analyser, int PageCount, int AnalysedPages, string? Title, string? Author, List<PageRecord> Pages, List<PartRecord> Parts, PdfFailure? Failure,
    string? Requested)
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
