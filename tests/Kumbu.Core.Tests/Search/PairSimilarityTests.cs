// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Search;

namespace Kumbu.Core.Tests.Search;

/// <summary>The score of an edge between two notes (spec 2026-11-22-graph-out-of-preview, §1).</summary>
public class PairSimilarityTests
{
    private static float[] Unit(params float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return [.. v.Select(x => x / norm)];
    }

    private static VaultPath P(string raw) => VaultPath.TryParse(raw, out var p) ? p : throw new ArgumentException(raw);

    private static readonly float[] X = Unit(1, 0, 0), Y = Unit(0, 1, 0), Z = Unit(0, 0, 1), XY = Unit(1, 1, 0);

    [Fact]
    public void The_same_chunks_score_one()
    {
        Assert.Equal(1.0, PairSimilarity.Mutual([X, Y], [X, Y])!.Value, 5);
    }

    [Fact]
    public void Unrelated_chunks_score_zero_and_opposite_ones_are_clamped_to_it()
    {
        Assert.Equal(0.0, PairSimilarity.Mutual([X], [Y])!.Value, 5);
        Assert.Equal(0.0, PairSimilarity.Mutual([X], [Unit(-1, 0, 0)])!.Value, 5);
    }

    [Fact]
    public void It_is_symmetric()
    {
        Assert.Equal(PairSimilarity.Mutual([X, XY], [Y, Z])!.Value, PairSimilarity.Mutual([Y, Z], [X, XY])!.Value, 10);
    }

    [Fact]
    public void It_averages_both_directions()
    {
        // A short note fully covered by a long one: A→B is 1, B→A is (1 + 0) / 2, the score their mean.
        Assert.Equal(0.75, PairSimilarity.Mutual([X], [X, Y])!.Value, 5);
    }

    [Fact]
    public void A_side_without_chunks_has_no_score()
    {
        Assert.Null(PairSimilarity.Mutual([], [X]));
        Assert.Null(PairSimilarity.Mutual([X], []));
    }

    [Fact]
    public void Pairs_are_scored_from_the_stored_vectors_by_path_within_one_space()
    {
        var a = P("/W/a.md");
        var b = P("/W/b.md");
        var c = P("/W/c.md");
        var d = P("/W/d.md");
        var stored = new StoredVectors(SemanticState.Ready,
        [
            new(a, 1, 0, "m", X), new(a, 5, 0, "m", Y),
            new(b, 1, 0, "m", X), new(b, 4, 0, "m", Y),
            new(d, 1, 0, "other", X),
        ], 1);

        var scores = PairSimilarity.Score(stored, [(a, b), (a, c), (a, d)]);

        Assert.Equal(1.0, scores[0]!.Value, 5);
        Assert.Null(scores[1]); // c isn't embedded yet
        Assert.Null(scores[2]); // d is in another model's space: the two don't compare
    }
}
