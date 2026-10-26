// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Search;

/// <summary>
/// Keeps the embeddings of unlocked vaults current, in the background (concept §6): a written note is queued,
/// split into chunks, and only chunks whose text changed are embedded again. The embeddings are sealed in
/// MongoDB (<c>embeddings</c>) and held in the vault's working set, so a search is dot products in memory.
/// A vault catches up when it is unlocked, so nothing written while it was locked, or before a restart, is missed.
/// Each vault is embedded by its own provider (<see cref="UnlockedVault.EmbeddingProvider"/>): the internal model,
/// or the external one its owner opted in to.
/// </summary>
public sealed class SearchIndex : BackgroundService
{
    private readonly VaultCatalog _catalog;
    private readonly EmbeddingProviders _providers;
    private readonly ILogger<SearchIndex> _log;
    private readonly Dictionary<string, Channel<(UnlockedVault Vault, VaultPath Path)>> _queues;
    private readonly ConcurrentDictionary<(string Provider, string Vault, VaultPath Path), byte> _queued = new();
    private int _busy;

    public SearchIndex(VaultCatalog catalog, EmbeddingProviders providers, ILogger<SearchIndex> log)
    {
        (_catalog, _providers, _log) = (catalog, providers, log);
        // A queue per provider: one that is down or still loading holds up its own vaults only.
        _queues = providers.All.ToDictionary(p => p.Name, _ => Channel.CreateUnbounded<(UnlockedVault, VaultPath)>(), StringComparer.Ordinal);
        catalog.Unlocked += CatchUp;
    }

    public EmbeddingProviders Providers => _providers;

    /// <summary>How many notes wait to be indexed or are being indexed, in all vaults.</summary>
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
    /// vault's provider), the vault is embedded again.</summary>
    public void CatchUp(UnlockedVault vault)
    {
        if (_providers.Get(vault.EmbeddingProvider) is not { } provider)
        {
            _log.LogWarning("Vault {VaultId} uses the embedding provider {Provider}, which isn't configured; it isn't indexed", vault.Id, vault.EmbeddingProvider);
            return;
        }

        var model = provider.Driver.ModelKey;
        var (notes, embeddings) = (vault.Notes, vault.Embeddings);
        var otherModel = embeddings.Where(e => e.Value.Model != model && notes.ContainsKey(e.Key)).ToList();
        if (otherModel.Count > 0)
        {
            _log.LogInformation("Vault {VaultId}: {Count} {Notes} to embed with {New} (were {Old})",
                vault.Id, otherModel.Count, otherModel.Count == 1 ? "note" : "notes", model,
                string.Join(", ", otherModel.Select(e => e.Value.Model).Distinct().Order(StringComparer.Ordinal)));
        }

        Enqueue(vault, notes.Where(n => !IsCurrent(embeddings.GetValueOrDefault(n.Key), n.Value.Note, provider.Driver)).Select(n => n.Key)
            .Concat(embeddings.Keys.Where(p => !notes.ContainsKey(p))));
    }

    /// <summary>Embedded by this model, at this version, and with vectors of the length the model gives now: under one key a
    /// provider's model can change its output length (no Dimensions configured), and such vectors don't compare.</summary>
    private static bool IsCurrent(NoteEmbeddings? embeddings, StoredNote note, IEmbeddingDriver driver) =>
        embeddings?.Model == driver.ModelKey && embeddings.Version == note.Version && embeddings.Chunks.All(c => FitsLength(c, driver));

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

    /// <summary>Brings one note's embeddings up to its current version with the vault's provider, or removes them
    /// if the note is gone.</summary>
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
            if (!vault.Notes.TryGetValue(path, out var entry))
            {
                if (vault.Embeddings.ContainsKey(path)) await _catalog.Embeddings.DeleteOneAsync(filter, ct);
                vault.Embeddings = vault.Embeddings.Remove(path);
                return;
            }

            var provider = vault.EmbeddingProvider;
            if (_providers.Get(provider)?.Driver is not { } driver) return;
            var model = driver.ModelKey;
            var note = entry.Note;
            if (IsCurrent(vault.Embeddings.GetValueOrDefault(path), note, driver)) return;

            // Vectors are reused by the hash of their text, but only the current model's: the same text gives
            // another vector under another model.
            var chunks = Chunker.Split(new Note(path, note.Content));
            var known = vault.Embeddings.Values.Where(e => e.Model == model).SelectMany(e => e.Chunks).Where(c => FitsLength(c, driver)).GroupBy(c => c.Hash).ToDictionary(g => g.Key, g => g.First().Vector);
            var hashes = chunks.Select(c => Hash(c.EmbeddingText)).ToList();
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
                try
                {
                    vectors = await driver.EmbedAsync([.. batch.Select(i => chunks[i].EmbeddingText)], query: false, embedding.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return;
                }

                for (var k = 0; k < batch.Length; k++) known[hashes[batch[k]]] = vectors[k];
            }

            var embeddings = new NoteEmbeddings(model, note.Version, [.. chunks.Select((c, i) => new EmbeddedChunk(c.StartLine, hashes[i], known[hashes[i]]))]);
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
        finally
        {
            vault.IndexGate.Release();
        }
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
        base.Dispose();
    }
}
