// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Search;
using Kumbu.Server.Search;

namespace Kumbu.Server.Tests.Search;

/// <summary>Words hashed into a vector: texts sharing words are close. Counts what it embeds.</summary>
public sealed class FakeEmbedder(SemanticState state = SemanticState.Ready) : IEmbedder
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SemanticState State { get; private set; } = state;

    public string Model => "arctic-m";

    public string? Reason => State == SemanticState.Ready ? null : "the embedding model is loading";

    public int Embedded { get; private set; }

    public void BecomeReady()
    {
        State = SemanticState.Ready;
        _ready.TrySetResult();
    }

    public Task WhenReadyAsync(CancellationToken ct)
    {
        if (State == SemanticState.Ready) _ready.TrySetResult();
        return _ready.Task.WaitAsync(ct);
    }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool query, CancellationToken ct)
    {
        Embedded += texts.Count;
        return Task.FromResult(texts.Select(t =>
        {
            var v = new float[ModelFiles.Get(Model).Dimensions];
            foreach (var term in Bm25.Terms(t)) v[(int)((uint)term.GetHashCode(StringComparison.Ordinal) % v.Length)] += 1;
            return Vectors.Normalized(v);
        }).ToArray());
    }
}
