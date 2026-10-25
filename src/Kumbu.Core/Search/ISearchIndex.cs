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
public sealed record SemanticHit(VaultPath Path, int StartLine, double Score);

/// <param name="Hits">Chunks with the cosine similarity to the query, best first; only chunks embedded by the current model.</param>
/// <param name="Pending">Notes below the scope without embeddings of the current model yet (not indexed, or embedded by another model).</param>
/// <param name="Reason">Why the state isn't <see cref="SemanticState.Ready"/>, for the output. When Ready: the vaults
/// of <see cref="WordsOnlyVaults"/> and why they weren't searched by meaning.</param>
public sealed record SemanticResult(SemanticState State, IReadOnlyList<SemanticHit> Hits, int Pending, string? Reason = null)
{
    /// <summary>When Ready: vaults (by name) that weren't searched by meaning because their embedding provider is
    /// unavailable. The search ranks their notes by words; they don't count as pending.</summary>
    public IReadOnlyList<string> WordsOnlyVaults { get; init; } = [];
}

/// <summary>Search by meaning over the embeddings of the caller's vaults. <c>limit</c> caps the hits; pass
/// <see cref="int.MaxValue"/> to get a cosine for every chunk.</summary>
public interface ISearchIndex
{
    Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct);
}

public sealed class NoSearchIndex : ISearchIndex
{
    public Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct) =>
        Task.FromResult(new SemanticResult(SemanticState.Off, [], 0, "semantic search is off on this server"));
}
