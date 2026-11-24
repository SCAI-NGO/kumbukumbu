// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using Kumbu.Core.Graph;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Search;

/// <summary>A note, or one of its sections (<paramref name="Section"/>, an address such as "Effects" or "Parent#Child"), to compare with.</summary>
public sealed record SimilarSource(VaultPath Path, string? Section = null)
{
    public string Shown => Section is null ? Path.Value : $"{Path.Value}#{Section}";
}

/// <param name="Sources">One note (open discovery) or two (<c>--between</c>, closed discovery).</param>
/// <param name="Scope">Where the candidates are: the root, a vault or a folder.</param>
/// <param name="Unlinked">Leave out the notes one link from any source.</param>
/// <param name="Archive">Take in the notes of a vault's archive (a scope inside it always does).</param>
/// <param name="LeftOut">Candidates the caller leaves out (a silo, a tag, rejected suggestions); null leaves out none.</param>
/// <param name="WithoutTags">Tags whose notes the link distances don't walk through (normalised); <paramref name="LeftOut"/> is the
/// caller's to leave those notes out as candidates.</param>
public sealed record SimilarQuery(
    IReadOnlyList<SimilarSource> Sources, VaultPath Scope, int Max, bool Unlinked = false, bool Archive = false,
    Func<Note, bool>? LeftOut = null, IReadOnlyList<string>? WithoutTags = null);

/// <param name="Chunk">The note's best chunk, as the note is now.</param>
/// <param name="Score">With one source its cosine; with two the lower of <paramref name="Scores"/>.</param>
/// <param name="Scores">Per source, the best cosine of any of its chunks with <paramref name="Chunk"/>.</param>
/// <param name="Distances">Per source, how many links the note is from it in either direction; null when not connected
/// or in another vault (<see cref="SimilarHit.OtherVault"/>).</param>
public sealed record SimilarHit(Chunk Chunk, double Score, IReadOnlyList<double> Scores, IReadOnlyList<int?> Distances)
{
    /// <summary>Per source, whether the note lies in another vault: links never cross vaults.</summary>
    public required IReadOnlyList<bool> OtherVault { get; init; }
}

/// <param name="Error">Why there is no answer (no such note or section, not indexed, two models, the index off); the rest is then empty.</param>
/// <param name="Pending">Notes below the scope not indexed yet, so not compared.</param>
/// <param name="OtherModel">Candidate notes embedded by another model than the sources', so not compared.</param>
/// <param name="Reason">Vaults left out by the index, and why.</param>
public sealed record SimilarResult(IReadOnlyList<SimilarHit> Hits, int Pending, int OtherModel, string? Reason, string? Error = null);

/// <summary>
/// Search by place (spec 2026-11-22-knowledge-synthesis): the notes closest in meaning to a note or a section, each by its
/// best chunk, with its link distance. Compares stored vectors only, so nothing is sent to an embedding provider. For
/// <c>sh similar</c> and anything else that suggests notes (link prediction).
/// </summary>
public static class SimilarSearch
{
    private sealed record Resolved(SimilarSource Source, string Space, IReadOnlyList<float[]> Vectors);

    public static async Task<SimilarResult> RunAsync(INoteReader notes, ISearchIndex index, SimilarQuery query, CancellationToken ct)
    {
        var sources = new List<Resolved>();
        foreach (var source in query.Sources)
        {
            var (resolved, error) = await ResolveAsync(notes, index, source, ct);
            if (resolved is null) return Failed(error!);
            sources.Add(resolved);
        }

        if (sources.Count == 0) return Failed("no note to compare with");
        if (sources.Select(s => s.Source.Path).Distinct().Count() < sources.Count) return Failed("the notes to compare with must be different notes");
        var space = sources[0].Space;
        if (sources.Any(s => s.Space != space))
        {
            return Failed($"{string.Join(" and ", sources.Select(s => s.Source.Path.Value))} are embedded by different models, whose scores don't compare");
        }

        var stored = await index.VectorsAsync(query.Scope, ct);
        if (stored.State != SemanticState.Ready) return Failed(stored.Reason ?? "search by meaning isn't available");

        var archive = ArchiveFolder.Shown(query.Scope, query.Archive);
        var sourcePaths = sources.Select(s => s.Source.Path).ToHashSet();
        var candidates = (await notes.ReadNotesUnderAsync(query.Scope, ct))
            .Where(n => !sourcePaths.Contains(n.Path) && (archive || !ArchiveFolder.Contains(n.Path)) && query.LeftOut?.Invoke(n) != true)
            .ToDictionary(n => n.Path);

        var (best, otherModel) = Rank(sources, candidates, stored.Chunks, space, index.Bound);

        // Link distances per source, in its vault's graph, walked without the notes left out by tag or archive.
        var filter = new GraphFilter([], [], Archive: archive) { WithoutTags = query.WithoutTags ?? [] };
        var distances = new List<IReadOnlyDictionary<VaultPath, int>>();
        foreach (var source in sources)
        {
            var graph = await notes.GraphAsync(VaultPath.Root.Child(source.Source.Path.Vault!), ct);
            distances.Add(GraphQueries.Distances(graph, source.Source.Path, filter));
        }

        var hits = best.Values
            .Select(h => new SimilarHit(h.Chunk, h.Score, h.Scores,
                [.. distances.Select(d => d.TryGetValue(h.Chunk.Path, out var n) ? n : (int?)null)])
            {
                OtherVault = [.. sources.Select(s => s.Source.Path.Vault != h.Chunk.Path.Vault)],
            })
            .Where(h => !query.Unlinked || h.Distances.All(d => d != 1))
            .OrderByDescending(h => h.Score).ThenBy(h => h.Chunk.Path.Value, TextOrder.Default)
            .Take(query.Max).ToList();
        return new SimilarResult(hits, stored.Pending, otherModel, stored.Reason);
    }

    private static SimilarResult Failed(string error) => new([], 0, 0, null, error);

    /// <summary>The source's stored vectors, of the section's chunks if it names one, or why there are none.</summary>
    private static async Task<(Resolved?, string?)> ResolveAsync(INoteReader notes, ISearchIndex index, SimilarSource source, CancellationToken ct)
    {
        var path = source.Path;
        if (!path.IsNote || await notes.ReadAsync(path, ct) is not { } content) return (null, $"{path.Value}: not a note");
        List<Chunk>? overlapping = null;
        if (source.Section is { } section)
        {
            if (!SectionLookup.TryFind(path, content, section, out var match, out var sectionError)) return (null, sectionError);
            var (first, end) = (match.Heading.Line, match.Heading.ContentEnd);
            // A short section shares the chunk of the heading it merged under, which starts above its own heading.
            overlapping = [.. Chunker.Split(new Note(path, content), index.Bound).Where(c => c.StartLine < end && c.EndLine > first)];
        }

        var stored = await index.VectorsAsync(path, ct);
        if (stored.State != SemanticState.Ready) return (null, stored.Reason ?? "search by meaning isn't available");
        var own = stored.Chunks
            .Where(v => v.Path == path && (overlapping is null || overlapping.Any(c => c.StartLine <= v.StartLine && v.StartLine < c.EndLine)))
            .ToList();
        if (own.Count == 0)
        {
            var what = source.Section is null ? "isn't indexed yet" : "has no indexed text yet";
            return (null, $"{source.Shown} {what}; try again in a while, or semsearch for its topic meanwhile");
        }

        return (new Resolved(source, own[0].Space, [.. own.Select(c => c.Vector)]), null);
    }

    /// <summary>
    /// Each candidate note's best chunk: per source, the best cosine of any of the source's chunks with it, and over the
    /// sources the lowest of those. Chunks of another space or length are left out and their notes counted; a stored chunk
    /// is matched to the note's current chunk as semsearch matches it, and headings with nothing below them don't count.
    /// </summary>
    private static (Dictionary<VaultPath, (Chunk Chunk, double Score, double[] Scores)> Best, int OtherModel) Rank(
        IReadOnlyList<Resolved> sources, Dictionary<VaultPath, Note> candidates, IReadOnlyList<ChunkVector> stored, string space, ChunkBound bound)
    {
        var length = sources[0].Vectors[0].Length;
        var best = new Dictionary<VaultPath, (Chunk Chunk, double Score, double[] Scores)>();
        var otherModel = new HashSet<VaultPath>();
        var compared = new HashSet<VaultPath>();
        var chunksOf = new Dictionary<VaultPath, List<Chunk>>();
        foreach (var vector in stored)
        {
            if (!candidates.TryGetValue(vector.Path, out var note)) continue;
            if (vector.Space != space || vector.Vector.Length != length)
            {
                otherModel.Add(vector.Path);
                continue;
            }

            compared.Add(vector.Path);
            if (!chunksOf.TryGetValue(vector.Path, out var chunks)) chunksOf[vector.Path] = chunks = Chunker.Split(note, bound);
            var chunk = chunks.FirstOrDefault(c => c.StartLine == vector.StartLine && c.Part == vector.Part)
                ?? chunks.FirstOrDefault(c => c.StartLine <= vector.StartLine && vector.StartLine < c.EndLine);
            if (chunk is null || SectionSearch.IsHeadingOnly(chunk)) continue;

            var scores = sources.Select(s => s.Vectors.Max(v => Dot(v, vector.Vector))).ToArray();
            var score = scores.Min();
            if (!best.TryGetValue(vector.Path, out var known) || score > known.Score) best[vector.Path] = (chunk, score, scores);
        }

        otherModel.ExceptWith(compared);
        return (best, otherModel.Count);
    }

    /// <summary>The dot product of two vectors of one length: their cosine, as the stored vectors are unit length.</summary>
    public static double Dot(float[] a, float[] b)
    {
        var sum = 0f;
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var acc = Vector<float>.Zero;
            for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count) acc += new Vector<float>(a, i) * new Vector<float>(b, i);
            sum = Vector.Dot(acc, Vector<float>.One);
        }

        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
