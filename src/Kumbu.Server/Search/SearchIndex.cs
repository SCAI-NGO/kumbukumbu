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
/// </summary>
public sealed class SearchIndex : BackgroundService
{
    private readonly VaultCatalog _catalog;
    private readonly IEmbedder _embedder;
    private readonly ILogger<SearchIndex> _log;
    private readonly Channel<(UnlockedVault Vault, VaultPath Path)> _queue = Channel.CreateUnbounded<(UnlockedVault, VaultPath)>();
    private readonly ConcurrentDictionary<(string Vault, VaultPath Path), byte> _queued = new();
    private int _busy;

    public SearchIndex(VaultCatalog catalog, IEmbedder embedder, ILogger<SearchIndex> log)
    {
        (_catalog, _embedder, _log) = (catalog, embedder, log);
        catalog.Unlocked += CatchUp;
    }

    public IEmbedder Embedder => _embedder;

    /// <summary>How many notes wait to be indexed or are being indexed, in all vaults.</summary>
    public int Queued => _queued.Count + Volatile.Read(ref _busy);

    /// <summary>Queues notes to be indexed. While search by meaning is off, the queue just waits; it holds each
    /// note once, however often it is written.</summary>
    public void Enqueue(UnlockedVault vault, IEnumerable<VaultPath> paths)
    {
        foreach (var path in paths)
        {
            if (_queued.TryAdd((vault.Id, path), 0)) _queue.Writer.TryWrite((vault, path));
        }
    }

    /// <summary>Every note whose current version has no embeddings of the current model, and every embedding whose
    /// note is gone. Embeddings of another model count as missing: after a switch, the vault is embedded again.</summary>
    private void CatchUp(UnlockedVault vault)
    {
        var (notes, embeddings) = (vault.Notes, vault.Embeddings);
        var otherModel = embeddings.Where(e => e.Value.Model != _embedder.Model && notes.ContainsKey(e.Key)).ToList();
        if (otherModel.Count > 0)
        {
            _log.LogInformation("Vault {Name}: {Count} notes to embed with {New} (were {Old})",
                vault.Name, otherModel.Count, _embedder.Model, string.Join(", ", otherModel.Select(e => e.Value.Model).Distinct().Order(StringComparer.Ordinal)));
        }

        Enqueue(vault, notes.Where(n => !IsCurrent(embeddings.GetValueOrDefault(n.Key), n.Value.Note)).Select(n => n.Key)
            .Concat(embeddings.Keys.Where(p => !notes.ContainsKey(p))));
    }

    private bool IsCurrent(NoteEmbeddings? embeddings, StoredNote note) => embeddings?.Model == _embedder.Model && embeddings.Version == note.Version;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _embedder.WhenReadyAsync(stoppingToken);
        await foreach (var (vault, path) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            // Off the queue before indexing: a write meanwhile queues the note again, so it isn't missed.
            Interlocked.Increment(ref _busy);
            _queued.TryRemove((vault.Id, path), out _);
            try
            {
                await IndexAsync(vault, path, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The next write or unlock tries again; one bad note must not stop the index.
                _log.LogError("Indexing a note of vault {Vault} failed: {Message}", vault.Name, e.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _busy);
            }
        }
    }

    /// <summary>Brings one note's embeddings up to its current version, or removes them if the note is gone.</summary>
    public async Task IndexAsync(UnlockedVault vault, VaultPath path, CancellationToken ct)
    {
        await vault.IndexGate.WaitAsync(ct);
        try
        {
            var filter = Builders<SealedDocument>.Filter.Eq(d => d.Vault, vault.Id) & Builders<SealedDocument>.Filter.Eq(d => d.PathId, vault.PathId(path));
            if (!vault.Notes.TryGetValue(path, out var entry))
            {
                if (vault.Embeddings.ContainsKey(path)) await _catalog.Embeddings.DeleteOneAsync(filter, ct);
                vault.Embeddings = vault.Embeddings.Remove(path);
                return;
            }

            var note = entry.Note;
            if (IsCurrent(vault.Embeddings.GetValueOrDefault(path), note)) return;

            // Vectors are reused by the hash of their text, but only the current model's: the same text gives
            // another vector under another model.
            var chunks = Chunker.Split(new Note(path, note.Content));
            var known = vault.Embeddings.Values.Where(e => e.Model == _embedder.Model).SelectMany(e => e.Chunks).GroupBy(c => c.Hash).ToDictionary(g => g.Key, g => g.First().Vector);
            var hashes = chunks.Select(c => Hash(c.EmbeddingText)).ToList();
            var missing = Enumerable.Range(0, chunks.Count).Where(i => !known.ContainsKey(hashes[i])).ToList();
            if (missing.Count > 0)
            {
                var vectors = await _embedder.EmbedAsync([.. missing.Select(i => chunks[i].EmbeddingText)], query: false, ct);
                for (var k = 0; k < missing.Count; k++) known[hashes[missing[k]]] = vectors[k];
            }

            var embeddings = new NoteEmbeddings(_embedder.Model, note.Version, [.. chunks.Select((c, i) => new EmbeddedChunk(c.StartLine, hashes[i], known[hashes[i]]))]);
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
