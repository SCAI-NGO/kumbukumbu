// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Search;

public enum SearchMode
{
    /// <summary>Cosine similarity plus the word bonus; by words alone while meaning isn't available.</summary>
    Hybrid,

    /// <summary>BM25 alone.</summary>
    Words,

    /// <summary>Cosine similarity alone; nothing while meaning isn't available.</summary>
    Meaning,
}

/// <summary>A ranked section; <paramref name="Score"/> is null when it was ranked by words (BM25 scores say
/// nothing outside their own search).</summary>
public sealed record SectionHit(Chunk Chunk, double? Score)
{
    /// <summary>For a page of a PDF: its number. <see cref="Chunk"/> then holds the PDF's path and no text (the
    /// index has only the page's vector; its text is read for the output).</summary>
    public int? Page { get; init; }
}

/// <param name="Semantic">The search by meaning, or null for <see cref="SearchMode.Words"/>.</param>
/// <param name="FellBackToWords">Ranked by words because nothing had a cosine yet (first indexing, a model switch).</param>
public sealed record SectionSearchResult(IReadOnlyList<SectionHit> Hits, SemanticResult? Semantic, bool FellBackToWords)
{
    /// <summary>Asked for meaning only, but search by meaning isn't available.</summary>
    public bool MeaningUnavailable { get; init; }
}

/// <summary>
/// The ranking of <c>semsearch</c> (concept §6), for the command and the REST API alike: embeddings for meaning,
/// BM25 for exact words and names. The hybrid ranks by cosine similarity plus a small bonus for the query's words.
/// </summary>
public static class SectionSearch
{
    private const int SnippetLength = 160;

    /// <summary>Ranks the sections of <paramref name="notes"/> (the notes below <paramref name="scope"/>) and returns
    /// the best <paramref name="max"/>; headings with nothing below them don't count. With <paramref name="pages"/>,
    /// the pages of PDFs the index found by meaning rank among them, by their cosine alone (their text isn't at hand
    /// for a word bonus); a search by words finds notes only.</summary>
    public static async Task<SectionSearchResult> RankAsync(
        IReadOnlyList<Note> notes, VaultPath scope, string query, SearchMode mode, int max, ISearchIndex index, CancellationToken ct, bool pages = false)
    {
        var chunks = notes.OrderBy(n => n.Path.Value, TextOrder.Default).SelectMany(Chunker.Split).ToList();

        // Every chunk with a cosine takes part, so the bonus can lift a section from anywhere in the scope.
        SemanticResult? semantic = null;
        var fellBack = false;
        List<(Chunk Chunk, double? Score)> ranked;
        var pageChunks = new Dictionary<Chunk, int>(ReferenceEqualityComparer.Instance);
        if (mode == SearchMode.Words) ranked = RankByWords(chunks, query);
        else
        {
            semantic = await index.SearchAsync(scope, query, int.MaxValue, ct);
            if (semantic.State == SemanticState.Ready)
            {
                var wordsOnly = semantic.WordsOnlyVaults.ToHashSet(StringComparer.Ordinal);
                var (byMeaning, byWords) = wordsOnly.Count == 0
                    ? (chunks, [])
                    : (chunks.Where(c => !wordsOnly.Contains(c.Path.Vault!)).ToList(), chunks.Where(c => wordsOnly.Contains(c.Path.Vault!)).ToList());
                ranked = RankByMeaning(byMeaning, semantic, query, mode == SearchMode.Meaning);
                if (pages) ranked = WithPages(ranked, semantic, pageChunks);

                // Right after a model switch or on first indexing nothing has a cosine yet; the words still find something.
                if (ranked.Count == 0 && semantic.Pending > 0 && mode != SearchMode.Meaning)
                {
                    ranked = RankByWords(chunks, query);
                    fellBack = true;
                }
                else if (byWords.Count > 0 && mode != SearchMode.Meaning)
                {
                    ranked = FuseByRank(ranked, RankByWords(byWords, query));
                }
            }
            else if (mode == SearchMode.Meaning) return new SectionSearchResult([], semantic, false) { MeaningUnavailable = true };
            else ranked = RankByWords(chunks, query);
        }

        // A heading with nothing below it says little; it still ranks, but the next section takes its place.
        var listed = ranked.Where(r => !IsHeadingOnly(r.Chunk)).Take(max)
            .Select(r => new SectionHit(r.Chunk, r.Score) { Page = pageChunks.TryGetValue(r.Chunk, out var page) ? page : null }).ToList();
        return new SectionSearchResult(listed, semantic, fellBack);
    }

    /// <summary>
    /// <c>rg -F -i</c> over the notes, grouped by section: each section (chunk) with a line containing
    /// <paramref name="query"/>, ignoring case, in path and line order, with its first such line. A match in YAML
    /// frontmatter belongs to no section and isn't listed.
    /// </summary>
    public static IReadOnlyList<(Chunk Chunk, string FirstMatch)> Exact(IReadOnlyList<Note> notes, string query, int max)
    {
        var regex = Sh.Commands.Rg.Pattern([query], fixedStrings: true, wordRegexp: false, ignoreCase: true);
        var result = new List<(Chunk, string)>();
        foreach (var note in notes.OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            var lines = Lines.Split(note.Content);
            var matches = Enumerable.Range(0, lines.Count).Where(i => regex.IsMatch(lines[i])).ToList();
            if (matches.Count == 0) continue;
            foreach (var chunk in Chunker.Split(note))
            {
                var first = matches.FirstOrDefault(m => m >= chunk.StartLine && m < chunk.EndLine, -1);
                if (first < 0) continue;
                result.Add((chunk, lines[first]));
                if (result.Count == max) return result;
            }
        }

        return result;
    }

    /// <summary>The start of a section's text, without its heading, on one line.</summary>
    public static string Snippet(Chunk chunk)
    {
        var text = string.Join(' ', chunk.Text.Split('\n')
            .Where(l => l.Trim().Length > 0)
            .Skip(chunk.Address.Length > 0 && chunk.Text.Split('\n').Length > 1 ? 1 : 0)
            .Select(l => l.Trim()));
        if (text.Length == 0) text = chunk.Text.Trim();
        return Shorten(text);
    }

    /// <summary>
    /// The first line of the section, below its heading, that contains the query or one of its words (ignoring
    /// case), trimmed like <see cref="Snippet"/>; the start of the section if no line does.
    /// </summary>
    public static string Snippet(Chunk chunk, string query)
    {
        var terms = Bm25.QueryTerms(query);
        var lines = chunk.Text.Split('\n');
        var first = lines.Skip(chunk.Address.Length > 0 && lines.Length > 1 ? 1 : 0)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0
                && (l.Contains(query, StringComparison.OrdinalIgnoreCase) || Bm25.Terms(l).Any(t => terms.Contains(t))));
        return first is null ? Snippet(chunk) : Shorten(first);
    }

    /// <summary>At most the length of a snippet, with an ellipsis if cut.</summary>
    public static string Shorten(string text) => text.Length <= SnippetLength ? text : text[..SnippetLength] + "…";

    private static List<(Chunk, double?)> RankByWords(List<Chunk> chunks, string query) =>
        [.. Bm25.Rank(chunks.Select(c => c.EmbeddingText).ToList(), query).Select(r => (chunks[r.Index], (double?)null))];

    private static List<(Chunk, double?)> RankByMeaning(List<Chunk> chunks, SemanticResult semantic, string query, bool meaningOnly)
    {
        var byKey = chunks.ToDictionary(c => c.Key);
        var terms = meaningOnly ? [] : Bm25.QueryTerms(query);
        var cosines = new Dictionary<(VaultPath, int), double>();
        foreach (var hit in semantic.Hits)
        {
            if (Locate(hit, chunks, byKey) is not { } key) continue;
            cosines[key] = Math.Max(cosines.GetValueOrDefault(key, double.MinValue), hit.Score);
        }

        // Ties keep the order of the notes and of the sections within them.
        return [.. chunks.Where(c => cosines.ContainsKey(c.Key))
            .Select(c => (Chunk: c, Score: Fusion.Score(cosines[c.Key], terms, Bm25.Terms(c.EmbeddingText).ToHashSet())))
            .OrderByDescending(r => r.Score)
            .Select(r => (r.Chunk, (double?)r.Score))];
    }

    /// <summary>The pages of PDFs among the hits, best cosine per page, merged into <paramref name="ranked"/> by score;
    /// a section goes first on a tie.</summary>
    private static List<(Chunk, double?)> WithPages(List<(Chunk Chunk, double? Score)> ranked, SemanticResult semantic, Dictionary<Chunk, int> pageChunks)
    {
        var best = new Dictionary<(VaultPath, int), double>();
        foreach (var hit in semantic.Hits)
        {
            if (!AttachmentFormat.IsPdf(hit.Path)) continue;
            best[(hit.Path, hit.StartLine)] = Math.Max(best.GetValueOrDefault((hit.Path, hit.StartLine), double.MinValue), hit.Score);
        }

        if (best.Count == 0) return ranked;
        foreach (var ((path, page), _) in best) pageChunks[new Chunk(path, "", page, page, "", "")] = page;
        var found = pageChunks.Select(p => (Chunk: p.Key, Score: (double?)best[(p.Key.Path, p.Value)]))
            .OrderBy(p => p.Chunk.Path.Value, TextOrder.Default).ThenBy(p => p.Chunk.StartLine);
        return [.. ranked.Select(r => (r.Chunk, r.Score, List: 0)).Concat(found.Select(p => (p.Chunk, p.Score, List: 1)))
            .OrderByDescending(r => r.Score).ThenBy(r => r.List).Select(r => (r.Chunk, r.Score))];
    }

    /// <summary>
    /// Reciprocal rank fusion of the vaults ranked by meaning with those whose embedding provider is unavailable,
    /// ranked by words: each section scores 1 / (60 + its rank in its own list), ties going to meaning. A cosine and a
    /// BM25 score say nothing about each other, so only the ranks are merged (the meaning list keeps its scores, the
    /// words list has none, as in a search by words). Hybrid ranking over one model uses the cosine and the word bonus
    /// instead (<see cref="Fusion"/>), which measured better; this merges lists that can't share a score.
    /// </summary>
    private static List<(Chunk, double?)> FuseByRank(List<(Chunk Chunk, double? Score)> meaning, List<(Chunk Chunk, double? Score)> words)
    {
        const double K = 60;
        return [.. meaning.Select((r, i) => (Hit: r, Fused: 1 / (K + i + 1), List: 0))
            .Concat(words.Select((r, i) => (Hit: r, Fused: 1 / (K + i + 1), List: 1)))
            .OrderByDescending(r => r.Fused)
            .ThenBy(r => r.List)
            .Select(r => r.Hit)];
    }

    private static bool IsHeadingOnly(Chunk chunk)
    {
        var text = chunk.Text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        return text.Count == 1 && Outline.HeadingRegex().IsMatch(text[0]);
    }

    /// <summary>The current chunk for a hit: the same start line, or the chunk that now contains it (the
    /// index may lag behind a write by a moment).</summary>
    private static (VaultPath, int)? Locate(SemanticHit hit, List<Chunk> chunks, Dictionary<(VaultPath, int), Chunk> byKey)
    {
        if (byKey.ContainsKey((hit.Path, hit.StartLine))) return (hit.Path, hit.StartLine);
        return chunks.FirstOrDefault(c => c.Path == hit.Path && c.StartLine <= hit.StartLine && hit.StartLine < c.EndLine)?.Key;
    }
}
