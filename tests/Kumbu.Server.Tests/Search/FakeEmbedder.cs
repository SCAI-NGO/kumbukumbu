// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Search;
using Kumbu.Server.Search;

namespace Kumbu.Server.Tests.Search;

/// <summary>Words hashed into a vector: texts sharing words are close. Counts what it embeds. Its vectors have the
/// model's dimensions unless given, so vectors of two fake models can't be mixed by accident.</summary>
public sealed class FakeEmbedder(SemanticState state = SemanticState.Ready, string model = "arctic-m", int? dimensions = null) : IEmbeddingDriver
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SemanticState State { get; private set; } = state;

    public string ModelKey => model;

    public int Dimensions => dimensions ?? (ModelFiles.Catalogue.TryGetValue(model, out var m) ? m.Dimensions : 64);

    public int MaxBatch { get; init; } = 64;

    /// <summary>Thrown by every call while set: a provider that is down, or refuses the texts.</summary>
    public Exception? Failure { get; set; }

    /// <summary>The texts of every call, in order (queries too), to check what would have left the server.</summary>
    public List<(string Text, bool Query)> Seen { get; } = [];

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

    /// <summary>Awaited before each call (query or not), to hold a call up or to change something in between.</summary>
    public Func<bool, Task>? BeforeEmbed { get; set; }

    /// <summary>While set, every call hangs until its token is cancelled, as a remote provider that doesn't answer.
    /// <see cref="Hanging"/> completes once the first call hangs.</summary>
    public bool Hang { get; set; }

    /// <summary>With <see cref="Hang"/>: the call ignores its token and hangs until <see cref="Release"/> completes.</summary>
    public bool IgnoreCancellation { get; init; }

    public TaskCompletionSource Hanging { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool query, CancellationToken ct)
    {
        if (BeforeEmbed is { } before) await before(query);
        if (Hang)
        {
            // Seen as it leaves, before the hang: a call that is cancelled still went out.
            lock (Seen) Seen.AddRange(texts.Select(t => (t, query)));
            Hanging.TrySetResult();
            if (IgnoreCancellation) await Release.Task;
            else await Task.Delay(Timeout.Infinite, ct);
            return Embed(texts, query, seen: true);
        }

        return Embed(texts, query);
    }

    private float[][] Embed(IReadOnlyList<string> texts, bool query, bool seen = false)
    {
        if (Failure is not null) throw Failure;
        if (texts.Count > MaxBatch) throw new ArgumentException($"At most {MaxBatch} texts per call", nameof(texts));
        Embedded += texts.Count;
        if (!seen) lock (Seen) Seen.AddRange(texts.Select(t => (t, query)));
        return [.. texts.Select(t =>
        {
            var v = new float[Dimensions];
            foreach (var term in Bm25.Terms(t)) v[(int)(Fnv1a(term) % (uint)v.Length)] += 1;
            return Vectors.Normalized(v);
        })];
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
