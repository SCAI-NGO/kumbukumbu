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

/// <summary>A chunk by meaning: the note and the first line of the chunk, as the index knows it.</summary>
public sealed record SemanticHit(VaultPath Path, int StartLine, double Score);

/// <param name="Pending">Notes below the scope whose current version isn't indexed yet.</param>
/// <param name="Reason">Why the state isn't <see cref="SemanticState.Ready"/>, for the output.</param>
public sealed record SemanticResult(SemanticState State, IReadOnlyList<SemanticHit> Hits, int Pending, string? Reason = null);

/// <summary>Search by meaning over the embeddings of the caller's vaults.</summary>
public interface ISearchIndex
{
    Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct);
}

public sealed class NoSearchIndex : ISearchIndex
{
    public Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct) =>
        Task.FromResult(new SemanticResult(SemanticState.Off, [], 0, "semantic search is off on this server"));
}
