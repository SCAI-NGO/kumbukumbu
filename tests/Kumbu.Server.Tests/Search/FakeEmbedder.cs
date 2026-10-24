// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Search;
using Kumbu.Server.Search;

namespace Kumbu.Server.Tests.Search;

/// <summary>Words hashed into a vector: texts sharing words are close. Counts what it embeds. Its vectors have the
/// model's dimensions unless given, so vectors of two fake models can't be mixed by accident.</summary>
public sealed class FakeEmbedder(SemanticState state = SemanticState.Ready, string model = "arctic-m", int? dimensions = null) : IEmbedder
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SemanticState State { get; private set; } = state;

    public string Model => model;

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
            var v = new float[dimensions ?? ModelFiles.Get(model).Dimensions];
            foreach (var term in Bm25.Terms(t)) v[(int)(Fnv1a(term) % (uint)v.Length)] += 1;
            return Vectors.Normalized(v);
        }).ToArray());
    }

    /// <summary>FNV-1a over UTF-8: the same in every process, unlike <c>string.GetHashCode</c>, so which words
    /// share a dimension (and with it every cosine) doesn't change from run to run.</summary>
    private static uint Fnv1a(string term)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(term)) hash = (hash ^ b) * 16777619u;
        return hash;
    }
}
