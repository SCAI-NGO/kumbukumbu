// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Kumbu.Core.Search;

/// <summary>Lexical ranking: Okapi BM25 over chunks, with words as Unicode letters and digits, case-folded.</summary>
public static partial class Bm25
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    public static IReadOnlyList<string> Terms(string text) =>
        [.. WordRegex().Matches(text.Normalize(System.Text.NormalizationForm.FormKC)).Select(m => m.Value.ToLowerInvariant())];

    /// <summary>
    /// Words that say nothing about a topic, in the two languages of the notes so far. Without them, a question
    /// like "animals on a farm" would give every section containing "a" or "on" a word bonus, which would push the
    /// right section down.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "can", "do", "does", "for", "from", "has", "have", "how", "i",
        "if", "in", "into", "is", "it", "its", "my", "of", "on", "or", "our", "so", "that", "the", "their", "then", "there",
        "these", "this", "to", "was", "we", "were", "what", "when", "where", "which", "who", "why", "will", "with", "you", "your",
        "aber", "als", "am", "an", "auch", "auf", "aus", "bei", "bin", "bis", "da", "das", "dass", "dem", "den", "der", "des",
        "die", "doch", "du", "ein", "eine", "einem", "einen", "einer", "es", "für", "hat", "ich", "ihr", "im", "in", "ist",
        "ja", "kann", "mit", "nach", "nicht", "noch", "nur", "oder", "sich", "sie", "sind", "so", "um", "und", "uns", "von",
        "vor", "war", "was", "wie", "wir", "wo", "zu", "zum", "zur",
    };

    /// <summary>The terms of a query that can tell notes apart: no stop words, no single letters (a single digit
    /// can: "topic 3").</summary>
    public static IReadOnlyList<string> QueryTerms(string query) =>
        [.. Terms(query).Where(t => (t.Length > 1 || char.IsDigit(t[0])) && !StopWords.Contains(t)).Distinct()];

    /// <summary>Indexes into <paramref name="documents"/> with a score above zero, best first.</summary>
    public static IReadOnlyList<(int Index, double Score)> Rank(IReadOnlyList<string> documents, string query)
    {
        var queryTerms = QueryTerms(query).ToList();
        if (queryTerms.Count == 0 || documents.Count == 0) return [];

        var docs = documents.Select(d => Terms(d).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count())).ToList();
        var lengths = docs.Select(d => d.Values.Sum()).ToList();
        var average = Math.Max(1, lengths.Average());
        var idf = queryTerms.ToDictionary(t => t, t =>
        {
            var n = docs.Count(d => d.ContainsKey(t));
            return Math.Log(1 + (docs.Count - n + 0.5) / (n + 0.5));
        });

        var result = new List<(int, double)>();
        for (var i = 0; i < docs.Count; i++)
        {
            var score = 0.0;
            foreach (var term in queryTerms)
            {
                if (!docs[i].TryGetValue(term, out var f)) continue;
                score += idf[term] * f * (K1 + 1) / (f + K1 * (1 - B + B * lengths[i] / average));
            }

            if (score > 0) result.Add((i, score));
        }

        return [.. result.OrderByDescending(r => r.Item2).ThenBy(r => r.Item1)];
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}

/// <summary>
/// The hybrid score: the cosine similarity of a section to the query, plus a small bonus for the share of the
/// query's words the section contains. Measured better than rank fusion, which loses how close a match is.
/// </summary>
public static class Fusion
{
    /// <summary>What a section containing every query term gains; enough to break ties between similar meanings.</summary>
    public const double WordBonus = 0.1;

    public static double Score(double cosine, IReadOnlyList<string> queryTerms, IReadOnlySet<string> chunkTerms)
    {
        if (queryTerms.Count == 0) return cosine;
        return cosine + WordBonus * queryTerms.Count(chunkTerms.Contains) / queryTerms.Count;
    }
}
