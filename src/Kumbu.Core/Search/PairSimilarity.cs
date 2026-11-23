// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Search;

/// <summary>
/// How close two notes are in meaning, as the graph's edges carry it (graph view spec §7, spec 2026-11-22-graph-out-of-preview
/// §1): for each chunk of one note its most similar chunk of the other, averaged, in both directions, and the mean of the two.
/// Unlike <see cref="SimilarSearch"/>, which ranks candidates by their best chunk, this weighs the whole of both notes, so
/// a long note that touches a topic once isn't as close as one about it. Compares stored vectors only.
/// </summary>
public static class PairSimilarity
{
    /// <summary>The score of two notes' chunk vectors (unit length), in [0, 1]; null where a side has none.</summary>
    public static double? Mutual(IReadOnlyList<float[]> a, IReadOnlyList<float[]> b)
    {
        if (a.Count == 0 || b.Count == 0) return null;
        var bestOfA = new double[a.Count];
        var bestOfB = new double[b.Count];
        Array.Fill(bestOfA, double.NegativeInfinity);
        Array.Fill(bestOfB, double.NegativeInfinity);
        for (var i = 0; i < a.Count; i++)
        {
            for (var j = 0; j < b.Count; j++)
            {
                var cos = SimilarSearch.Dot(a[i], b[j]);
                if (cos > bestOfA[i]) bestOfA[i] = cos;
                if (cos > bestOfB[j]) bestOfB[j] = cos;
            }
        }

        return Math.Clamp((bestOfA.Average() + bestOfB.Average()) / 2, 0, 1);
    }

    /// <summary>The score of each pair from <paramref name="stored"/>, in the pairs' order: null where a note has no
    /// vectors there, or the two lie in different embedding spaces (another model), whose vectors don't compare.</summary>
    public static IReadOnlyList<double?> Score(StoredVectors stored, IReadOnlyList<(VaultPath A, VaultPath B)> pairs)
    {
        var wanted = pairs.SelectMany(p => new[] { p.A, p.B }).ToHashSet();
        var byNote = stored.Chunks.Where(c => wanted.Contains(c.Path)).GroupBy(c => c.Path)
            .ToDictionary(g => g.Key, g => (g.First().Space, Vectors: (IReadOnlyList<float[]>)[.. g.Select(c => c.Vector)]));
        return [.. pairs.Select(p =>
            byNote.TryGetValue(p.A, out var a) && byNote.TryGetValue(p.B, out var b) && a.Space == b.Space ? Mutual(a.Vectors, b.Vectors) : null)];
    }
}
