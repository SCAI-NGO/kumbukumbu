// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Search;

public enum SemanticState
{
    /// <summary>The model is loaded; hits are by meaning.</summary>
    Ready,

    /// <summary>The model is being downloaded or loaded; try again in a while.</summary>
    Preparing,

    /// <summary>Semantic search is switched off, or the model can't be had.</summary>
    Off,
}

/// <summary>A chunk by meaning: the note and the first line of the chunk, as the index knows it, and its cosine similarity.</summary>
public sealed record SemanticHit(VaultPath Path, int StartLine, double Score)
{
    /// <summary>The chunk's <see cref="Chunk.Part"/> on its start line.</summary>
    public int Part { get; init; }
}

/// <param name="Hits">Chunks with the cosine similarity to the query, best first; only chunks embedded by the current model.</param>
/// <param name="Pending">Notes below the scope without embeddings of the current model and chunking yet (not indexed,
/// embedded by another model, or chunked under another scheme: <see cref="SemanticResult.StaleNotes"/>).</param>
/// <param name="Reason">Why the state isn't <see cref="SemanticState.Ready"/>, for the output. When Ready: the vaults
/// of <see cref="WordsOnlyVaults"/> and why they weren't searched by meaning.</param>
public sealed record SemanticResult(SemanticState State, IReadOnlyList<SemanticHit> Hits, int Pending, string? Reason = null)
{
    /// <summary>When Ready: vaults (by name) that weren't searched by meaning because their embedding provider is
    /// unavailable. The search ranks their notes by words; they don't count as pending.</summary>
    public IReadOnlyList<string> WordsOnlyVaults { get; init; } = [];

    /// <summary>When Ready: notes (caller paths) whose embeddings were chunked under another scheme and are being chunked
    /// anew. They have no hits and count as pending; the search ranks them by words meanwhile.</summary>
    public IReadOnlyList<VaultPath> StaleNotes { get; init; } = [];
}

/// <summary>A stored chunk's vector, as the index holds it: the note, the chunk's first line and part, and the
/// embedding space it lives in (the model's key). Vectors of two spaces don't compare.</summary>
public sealed record ChunkVector(VaultPath Path, int StartLine, int Part, string Space, float[] Vector);

/// <param name="Chunks">The chunks of the notes below the scope embedded by their vault's current model under the
/// current chunking scheme. The arrays are the index's own: read them, never write to them.</param>
/// <param name="Pending">Notes below the scope without such embeddings yet.</param>
/// <param name="Reason">Why the state isn't <see cref="SemanticState.Ready"/>; when Ready, the vaults left out and why.</param>
public sealed record StoredVectors(SemanticState State, IReadOnlyList<ChunkVector> Chunks, int Pending, string? Reason = null);

/// <summary>Search by meaning over the embeddings of the caller's vaults. <c>limit</c> caps the hits; pass
/// <see cref="int.MaxValue"/> to get a cosine for every chunk.</summary>
public interface ISearchIndex
{
    Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct);

    /// <summary>The bound the stored chunks were made with; search-time chunking uses the same.</summary>
    ChunkBound Bound => ChunkBound.Fallback;

    /// <summary>The stored vectors of the notes below <paramref name="scope"/>, for comparing notes with each other
    /// (<c>similar</c>). Nothing is embedded, so nothing is sent to an embedding provider.</summary>
    Task<StoredVectors> VectorsAsync(VaultPath scope, CancellationToken ct) =>
        Task.FromResult(new StoredVectors(SemanticState.Off, [], 0, "search by meaning is off on this server"));
}

public sealed class NoSearchIndex : ISearchIndex
{
    public Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct) =>
        Task.FromResult(new SemanticResult(SemanticState.Off, [], 0, "semantic search is off on this server"));
}
