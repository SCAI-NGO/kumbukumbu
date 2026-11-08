// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Search;

namespace Kumbu.Core.Skills;

/// <summary>A skill that fits a request: its place (1 first) and its fused score.</summary>
public sealed record SkillHit(Skill Skill, int Rank, double Score);

/// <summary>
/// The skills that fit a request, for <c>skills find</c>: reciprocal rank fusion, as <see cref="SectionSearch"/> merges
/// lists that can't share a score, of BM25 over name and description (counted twice), BM25 over the body of
/// <c>SKILL.md</c> and, per vault, the best cosine among its <c>SKILL.md</c> chunks. One search by meaning over the root
/// gives every vault's hits; split by vault, no cosine is compared with another vault's, whose provider may differ.
/// System skills rank by words only, and nothing is embedded here: a skill not yet indexed ranks by words.
/// </summary>
public static class SkillSearch
{
    public const int Max = 10;

    private const double HeadWeight = 2;

    public static async Task<IReadOnlyList<SkillHit>> RankAsync(
        IReadOnlyList<(Skill Skill, string? Manifest)> skills, string query, ISearchIndex index, CancellationToken ct, int max = Max)
    {
        var lists = new List<(double Weight, IReadOnlyList<int> Ranked)>
        {
            (HeadWeight, [.. Bm25.Rank([.. skills.Select(s => s.Skill.Name.Replace('-', ' ') + " " + s.Skill.Description)], query).Select(r => r.Index)]),
            (1, [.. Bm25.Rank([.. skills.Select(s => s.Manifest is null ? "" : SkillManifest.Body(s.Manifest))], query).Select(r => r.Index)]),
        };

        var byManifest = new Dictionary<VaultPath, int>();
        for (var i = 0; i < skills.Count; i++)
        {
            if (skills[i].Skill.Folder is { } folder) byManifest[folder.Child(SkillPaths.Manifest)] = i;
        }

        if (byManifest.Count > 0)
        {
            var semantic = await index.SearchAsync(VaultPath.Root, query, int.MaxValue, ct);
            if (semantic.State == SemanticState.Ready)
            {
                var wordsOnly = semantic.WordsOnlyVaults.ToHashSet(StringComparer.Ordinal);
                // Only a listed skill's SKILL.md counts: a hit on its other files, or on a skill the caller can't see, lists nothing.
                var best = new Dictionary<int, double>();
                foreach (var hit in semantic.Hits)
                {
                    if (!byManifest.TryGetValue(hit.Path, out var i) || wordsOnly.Contains(hit.Path.Vault!)) continue;
                    best[i] = Math.Max(best.GetValueOrDefault(i, double.MinValue), hit.Score);
                }

                foreach (var vault in best.GroupBy(b => skills[b.Key].Skill.Vault!, StringComparer.Ordinal))
                {
                    lists.Add((1, [.. vault.OrderByDescending(b => b.Value).ThenBy(b => b.Key).Select(b => b.Key)]));
                }
            }
        }

        var scores = new Dictionary<int, double>();
        foreach (var (weight, ranked) in lists)
        {
            for (var rank = 0; rank < ranked.Count; rank++) scores[ranked[rank]] = scores.GetValueOrDefault(ranked[rank]) + weight / (SectionSearch.RankFusionK + rank + 1);
        }

        // Ties keep the listing's order: system skills, the vault Skills, then the other vaults.
        return [.. scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key).Take(max).Select((s, i) => new SkillHit(skills[s.Key].Skill, i + 1, s.Value))];
    }
}
