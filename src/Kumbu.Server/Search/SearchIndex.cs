// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Server.Attachments;
using Kumbu.Server.Metrics;
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Search;

/// <summary>
/// Keeps the embeddings of unlocked vaults current, in the background (concept §6): a written note is queued,
/// split into chunks, and only chunks whose text changed are embedded again. The embeddings are sealed in
/// MongoDB (<c>embeddings</c>) and held in the vault's working set, so a search is dot products in memory.
/// A vault catches up when it is unlocked, so nothing written while it was locked, or before a restart, is missed.
/// Each vault is embedded by its own provider (<see cref="UnlockedVault.EmbeddingProvider"/>): the internal model,
/// or an external one its owner opted in to or the instance's default.
/// A PDF's pages are embedded under its path as a note's sections are, one chunk per page with text
/// (<see cref="EmbeddedChunk.StartLine"/> = the page number), from its stored analysis, once that is there.
/// </summary>
public sealed class SearchIndex : BackgroundService
{
    private readonly VaultCatalog _catalog;
    private readonly EmbeddingProviders _providers;
    private readonly ILogger<SearchIndex> _log;
    private readonly PdfAnalyses? _analyses;
    private readonly Dictionary<string, Channel<(UnlockedVault Vault, VaultPath Path)>> _queues;
    private readonly ConcurrentDictionary<(string Provider, string Vault, VaultPath Path), byte> _queued = new();
    private int _busy;

    /// <param name="analyses">Where the text of PDFs is read from; without it, PDFs aren't indexed.</param>
    public SearchIndex(VaultCatalog catalog, EmbeddingProviders providers, ILogger<SearchIndex> log, PdfAnalyses? analyses = null)
    {
        (_catalog, _providers, _log, _analyses) = (catalog, providers, log, analyses);
        // A queue per provider: one that is down or still loading holds up its own vaults only.
        _queues = providers.All.ToDictionary(p => p.Name, _ => Channel.CreateUnbounded<(UnlockedVault, VaultPath)>(), StringComparer.Ordinal);
        catalog.Unlocked += CatchUp;
        if (analyses is not null) analyses.Stored += OnAnalysed;
    }

    public EmbeddingProviders Providers => _providers;

    /// <summary>How many notes and PDFs wait to be indexed or are being indexed, in all vaults.</summary>
    public int Queued => _queued.Count + Volatile.Read(ref _busy);

    /// <summary>Queues notes to be indexed by the vault's provider. While it is off, its queue just waits; it holds
    /// each note once, however often it is written. A vault whose provider isn't configured isn't indexed.</summary>
    public void Enqueue(UnlockedVault vault, IEnumerable<VaultPath> paths)
    {
        var provider = vault.EmbeddingProvider;
        if (!_queues.TryGetValue(provider, out var queue)) return;
        foreach (var path in paths)
        {
            if (_queued.TryAdd((provider, vault.Id, path), 0)) queue.Writer.TryWrite((vault, path));
        }
    }

    /// <summary>Every note whose current version has no embeddings of the vault's model, and every embedding whose
    /// note is gone. Embeddings of another model count as missing: after a switch (of the internal model, or of the
    /// vault's provider), the vault is embedded again. PDFs likewise; one without an analysis yet is looked at and
    /// left (its analysis queues it when it is stored).</summary>
    public void CatchUp(UnlockedVault vault)
    {
        if (_providers.Get(vault.EmbeddingProvider) is not { } provider)
        {
            _log.LogWarning("Vault {VaultId} uses the embedding provider {Provider}, which isn't configured; it isn't indexed", vault.Id, vault.EmbeddingProvider);
            return;
        }

        var model = provider.Driver.ModelKey;
        var (notes, embeddings) = (vault.Notes, vault.Embeddings);
        var pdfs = Pdfs(vault).ToDictionary(p => p.Path, p => p.State.Version);
        var otherModel = embeddings.Where(e => e.Value.Model != model && (notes.ContainsKey(e.Key) || pdfs.ContainsKey(e.Key))).ToList();
        if (otherModel.Count > 0)
        {
            _log.LogInformation("Vault {VaultId}: {Count} {Notes} to embed with {New} (were {Old})",
                vault.Id, otherModel.Count, otherModel.Count == 1 ? "note" : "notes", model,
                string.Join(", ", otherModel.Select(e => e.Value.Model).Distinct().Order(StringComparer.Ordinal)));
        }

        Enqueue(vault, notes.Where(n => !IsCurrent(embeddings.GetValueOrDefault(n.Key), n.Value.Note.Version, provider.Driver)).Select(n => n.Key)
            .Concat(pdfs.Where(p => !IsCurrent(embeddings.GetValueOrDefault(p.Key), p.Value, provider.Driver)).Select(p => p.Key))
            .Concat(embeddings.Keys.Where(p => !notes.ContainsKey(p) && !pdfs.ContainsKey(p))));
    }

    /// <summary>The paths whose current state is a PDF, if PDFs are indexed.</summary>
    private IEnumerable<(VaultPath Path, AttachmentState State)> Pdfs(UnlockedVault vault) =>
        _analyses is null ? [] : vault.Attachments.Where(a => IsPdf(a.Value.Attachment.Current)).Select(a => (a.Key, a.Value.Attachment.Current));

    private static bool IsPdf(AttachmentState state) => state.MediaType == "application/pdf";

    /// <summary>A stored analysis queues every path of the vault whose PDF has that blob.</summary>
    private void OnAnalysed(UnlockedVault vault, byte[] blobId)
    {
        var current = _catalog.Current(vault.Id) ?? vault;
        Enqueue(current, Pdfs(current)
            .Where(p => EncryptedNoteStore.TryParseBlobId(p.State.BlobId, out var id) && id.AsSpan().SequenceEqual(blobId))
            .Select(p => p.Path));
    }

    /// <summary>Embedded by this model, at this version, and with vectors of the length the model gives now: under one key a
    /// provider's model can change its output length (no Dimensions configured), and such vectors don't compare.</summary>
    private static bool IsCurrent(NoteEmbeddings? embeddings, string version, IEmbeddingDriver driver) =>
        embeddings?.Model == driver.ModelKey && embeddings.Version == version && embeddings.Chunks.All(c => FitsLength(c, driver));

    /// <summary>A length of 0 is a remote model that hasn't answered yet: then any length fits.</summary>
    private static bool FitsLength(EmbeddedChunk chunk, IEmbeddingDriver driver) => driver.Dimensions == 0 || chunk.Vector.Length == driver.Dimensions;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(_providers.All.Select(p => RunAsync(p, _queues[p.Name].Reader, stoppingToken)));

    private async Task RunAsync(EmbeddingProvider provider, ChannelReader<(UnlockedVault Vault, VaultPath Path)> queue, CancellationToken stoppingToken)
    {
        await provider.Driver.WhenReadyAsync(stoppingToken);
        await foreach (var (vault, path) in queue.ReadAllAsync(stoppingToken))
        {
            // Off the queue before indexing: a write meanwhile queues the note again, so it isn't missed.
            Interlocked.Increment(ref _busy);
            _queued.TryRemove((provider.Name, vault.Id, path), out _);
            try
            {
                // The owner switched the vault to another provider since: that provider's queue has the note.
                if (vault.EmbeddingProvider == provider.Name) await IndexAsync(vault, path, stoppingToken);
            }
            catch (EmbeddingUnavailableException e) when (e.ProviderDown)
            {
                // Not this note's fault: it waits in the queue until the provider is back.
                Enqueue(vault, [path]);
                await provider.Driver.WhenReadyAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The next write or unlock tries again; one bad note must not stop the index.
                _log.LogError("Indexing a note of vault {VaultId} failed: {Message}", vault.Id, e.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _busy);
            }
        }
    }

    /// <summary>Brings one note's or PDF's embeddings up to its current version with the vault's provider, or removes
    /// them if it is gone.</summary>
    public async Task IndexAsync(UnlockedVault vault, VaultPath path, CancellationToken ct)
    {
        // Not even waiting during a re-keying run: it holds the gate until its finish.
        if (!MayIndex(vault, path)) return;
        await vault.IndexGate.WaitAsync(ct);
        try
        {
            // An upsert now would bring back an embedding of a vault that is gone, or seal one under a vault key
            // that a re-keying replaced.
            if (!MayIndex(vault, path)) return;
            var filter = Builders<SealedDocument>.Filter.Eq(d => d.Vault, vault.Id) & Builders<SealedDocument>.Filter.Eq(d => d.PathId, vault.PathId(path));
            var isNote = vault.Notes.TryGetValue(path, out var entry);
            var pdf = !isNote && _analyses is not null && vault.Attachments.TryGetValue(path, out var file) && IsPdf(file.Attachment.Current) ? file.Attachment.Current : null;
            if (!isNote && pdf is null)
            {
                await RemoveAsync(vault, path, filter, ct);
                return;
            }

            var provider = vault.EmbeddingProvider;
            if (_providers.Get(provider) is not { Driver: { } driver } configured) return;
            if (pdf is not null)
            {
                await IndexPdfAsync(vault, path, filter, configured, pdf, ct);
                return;
            }

            var note = entry.Note;
            if (IsCurrent(vault.Embeddings.GetValueOrDefault(path), note.Version, driver)) return;
            await EmbedAsync(vault, path, filter, configured, note.Version, [.. Chunker.Split(new Note(path, note.Content)).Select(c => (c.StartLine, c.EmbeddingText))], ct);
        }
        finally
        {
            vault.IndexGate.Release();
        }
    }

    private async Task RemoveAsync(UnlockedVault vault, VaultPath path, FilterDefinition<SealedDocument> filter, CancellationToken ct)
    {
        if (vault.Embeddings.ContainsKey(path)) await _catalog.Embeddings.DeleteOneAsync(filter, ct);
        vault.Embeddings = vault.Embeddings.Remove(path);
    }

    /// <summary>
    /// A PDF's pages with text, each a chunk: its file name and page number as context, then the page's text, cut
    /// where a note's chunk ends (about what the model reads). Read again whenever the PDF is queued, so a new analysis
    /// of the same version is seen; nothing is embedded if the pages are as before. A PDF whose analysis failed, or
    /// that has no text (a scan), gets embeddings without chunks: it counts as indexed. Without an analysis yet, the
    /// pages of an older version go: their text is no longer what the PDF holds.
    /// </summary>
    private async Task IndexPdfAsync(UnlockedVault vault, VaultPath path, FilterDefinition<SealedDocument> filter, EmbeddingProvider configured, AttachmentState state, CancellationToken ct)
    {
        if (!EncryptedNoteStore.TryParseBlobId(state.BlobId, out var blobId)) return;
        var texts = await _analyses!.SearchTextAsync(vault, blobId, Chunker.MaxChars, ct);
        if (texts is null)
        {
            if (vault.Embeddings.GetValueOrDefault(path) is { } older && older.Version != state.Version && MayIndex(vault, path)) await RemoveAsync(vault, path, filter, ct);
            return;
        }

        var title = path.Name[..^".pdf".Length];
        await EmbedAsync(vault, path, filter, configured, state.Version,
            [.. texts.Select((text, i) => (Page: i + 1, Text: text)).Where(p => !string.IsNullOrWhiteSpace(p.Text)).Select(p => (p.Page, $"{title} > p{p.Page}\n{p.Text}"))],
            ct);
    }

    /// <summary>Embeds the chunks (start line or page, and the text to embed) and stores them as the path's embeddings
    /// at <paramref name="version"/>; under the vault's index gate.</summary>
    private async Task EmbedAsync(
        UnlockedVault vault, VaultPath path, FilterDefinition<SealedDocument> filter, EmbeddingProvider configured, string version,
        IReadOnlyList<(int StartLine, string Text)> chunks, CancellationToken ct)
    {
        var provider = configured.Name;
        var driver = configured.Driver;
        var kind = configured.External ? KumbuMetrics.ProviderKind.External : KumbuMetrics.ProviderKind.Internal;
        var model = driver.ModelKey;
        var hashes = chunks.Select(c => Hash(c.Text)).ToList();
        if (vault.Embeddings.GetValueOrDefault(path) is { } stored && IsCurrent(stored, version, driver)
            && stored.Chunks.Select(c => (c.StartLine, c.Hash)).SequenceEqual(chunks.Select((c, i) => (c.StartLine, hashes[i]))))
        {
            return;
        }

        // Vectors are reused by the hash of their text, but only the current model's: the same text gives
        // another vector under another model.
        var known = vault.Embeddings.Values.Where(e => e.Model == model).SelectMany(e => e.Chunks).Where(c => FitsLength(c, driver)).GroupBy(c => c.Hash).ToDictionary(g => g.Key, g => g.First().Vector);
        var missing = Enumerable.Range(0, chunks.Count).Where(i => !known.ContainsKey(hashes[i])).DistinctBy(i => hashes[i]).ToList();
        // A deletion or a re-keying of the vault cancels a call under way: it waits for this gate, and a provider
        // that hangs with retries would hold it up for minutes.
        using var embedding = CancellationTokenSource.CreateLinkedTokenSource(ct, _catalog.InterruptToken(vault.Id));
        foreach (var batch in missing.Chunk(Math.Max(1, driver.MaxBatch)))
        {
            // The owner may have switched the vault meanwhile (back to internal, say): nothing more goes to this
            // provider, and nothing it answered is kept. The catch-up of the switch embeds the note with the new one.
            // Nor does anything more of a vault being deleted or re-keyed.
            if (vault.EmbeddingProvider != provider || !MayIndex(vault, path)) return;
            float[][] vectors;
            var started = Stopwatch.GetTimestamp();
            try
            {
                vectors = await driver.EmbedAsync([.. batch.Select(i => chunks[i].Text)], query: false, embedding.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // An interrupt (the vault is being deleted or re-keyed) is no failure; a cancellation neither token
                // asked for is the provider's own timeout.
                if (!embedding.Token.IsCancellationRequested) KumbuMetrics.EmbeddingFailed(kind);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                KumbuMetrics.EmbeddingFailed(kind);
                throw;
            }

            for (var k = 0; k < batch.Length; k++) known[hashes[batch[k]]] = vectors[k];
            KumbuMetrics.EmbeddingBatchDone(kind, Stopwatch.GetElapsedTime(started));
        }

        var embeddings = new NoteEmbeddings(model, version, [.. chunks.Select((c, i) => new EmbeddedChunk(c.StartLine, hashes[i], known[hashes[i]]))]);
        // Again: embedding takes seconds, and meanwhile the owner may have switched the vault's provider
        // or deleted the vault.
        if (vault.EmbeddingProvider != provider) return;
        if (!MayIndex(vault, path)) return;
        await _catalog.Embeddings.UpdateOneAsync(
            filter,
            Builders<SealedDocument>.Update
                .Set(d => d.Blob, vault.SealEmbeddings(path, embeddings))
                .SetOnInsert(d => d.Vault, vault.Id)
                .SetOnInsert(d => d.PathId, vault.PathId(path)),
            new UpdateOptions { IsUpsert = true },
            ct);
        vault.Embeddings = vault.Embeddings.SetItem(path, embeddings);
    }

    /// <summary>
    /// Whether <paramref name="vault"/> may be indexed now (<see cref="VaultCatalog.MayIndex"/>). If not, and another
    /// object of the vault is current (one made after a re-keying), the note goes to the queue again for that one:
    /// the queue holds a note once per vault, so the new object's catch-up skipped it while it waited here.
    /// </summary>
    private bool MayIndex(UnlockedVault vault, VaultPath path)
    {
        if (_catalog.MayIndex(vault)) return true;
        if (_catalog.Current(vault.Id) is { } current) Enqueue(current, [path]);
        return false;
    }

    /// <summary>Waits until nothing is queued (tests, and a search that wants a fresh index).</summary>
    public async Task IdleAsync(TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (Queued > 0 && DateTime.UtcNow < until) await Task.Delay(50, ct);
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    public override void Dispose()
    {
        _catalog.Unlocked -= CatchUp;
        if (_analyses is not null) _analyses.Stored -= OnAnalysed;
        base.Dispose();
    }
}
