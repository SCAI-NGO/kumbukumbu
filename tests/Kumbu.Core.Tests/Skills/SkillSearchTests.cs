// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Core.Skills;

namespace Kumbu.Core.Tests.Skills;

/// <summary>
/// <c>skills find</c>'s ranking: BM25 over name and description (counted twice) and over the body, plus a list by meaning
/// per vault from one search over the root, fused by rank.
/// </summary>
public class SkillSearchTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class FakeIndex(SemanticState state = SemanticState.Ready, IReadOnlyList<string>? wordsOnly = null, params SemanticHit[] hits) : ISearchIndex
    {
        public List<(VaultPath Scope, int Limit)> Calls { get; } = [];

        public Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct)
        {
            Calls.Add((scope, limit));
            return Task.FromResult(new SemanticResult(state, [.. hits.OrderByDescending(h => h.Score)], 0) { WordsOnlyVaults = wordsOnly ?? [] });
        }
    }

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private static SemanticHit Hit(string path, double score) => new(P(path), 0, score);

    private static (Skill, string?) Vault(string folder, string description, string body = "# Steps\n")
    {
        var path = P(folder);
        return (new Skill(path.Name, path, description, null, null, false, []), $"---\nname: {path.Name}\ndescription: {description}\n---\n{body}");
    }

    private static (Skill, string?) BuiltIn(string name, string description, string body = "# Steps\n") =>
        (new Skill(name, null, description, null, null, false, []), $"---\nname: {name}\ndescription: {description}\n---\n{body}");

    [Fact]
    public async Task Name_and_description_outrank_the_body()
    {
        // The body match comes first in the list, so only the head's weight puts the description match above it.
        var skills = new[]
        {
            Vault("/Work/_skills/in-body", "Helps with posts.", "# Steps\nThink about the garden first.\n"),
            Vault("/Work/_skills/in-head", "Plans the garden."),
        };

        var hits = await SkillSearch.RankAsync(skills, "garden", new FakeIndex(), Ct);

        Assert.Equal(["in-head", "in-body"], hits.Select(h => h.Skill.Name));
        Assert.Equal([1, 2], hits.Select(h => h.Rank));
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public async Task Meaning_lifts_a_skill_with_no_shared_words()
    {
        var skills = new[] { Vault("/Work/_skills/release-notes", "Drafts release notes."), Vault("/Work/_skills/other", "Something else.") };
        var index = new FakeIndex(hits: Hit("/Work/_skills/release-notes/SKILL.md", 0.8));

        var hit = Assert.Single(await SkillSearch.RankAsync(skills, "what changed in this version", index, Ct));

        Assert.Equal("release-notes", hit.Skill.Name);
        Assert.Equal(1 / 61.0, hit.Score, 10);
    }

    [Fact]
    public async Task One_search_by_meaning_over_the_root()
    {
        var skills = new[] { BuiltIn("create-skill", "Write a new skill."), Vault("/Skills/a", "First."), Vault("/Work/_skills/b", "Second."), Vault("/Private/_skills/c", "Third.") };
        var index = new FakeIndex();

        await SkillSearch.RankAsync(skills, "anything", index, Ct);

        Assert.Equal([(VaultPath.Root, int.MaxValue)], index.Calls);
    }

    [Fact]
    public async Task Each_vaults_hits_rank_on_their_own()
    {
        var skills = new[] { Vault("/Work/_skills/work-skill", "First."), Vault("/Skills/own-skill", "Second.") };
        // Work's provider scores high, Skills' low: each is still first in its own vault's list.
        var index = new FakeIndex(hits: [Hit("/Work/_skills/work-skill/SKILL.md", 0.9), Hit("/Work/_skills/work-skill/SKILL.md", 0.7), Hit("/Skills/own-skill/SKILL.md", 0.3)]);

        var hits = await SkillSearch.RankAsync(skills, "nothing in common", index, Ct);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1 / 61.0, hits[0].Score, 10);
        Assert.Equal(hits[0].Score, hits[1].Score);
    }

    [Fact]
    public async Task A_words_only_vault_gives_no_meaning_list()
    {
        var skills = new[] { Vault("/Work/_skills/work-skill", "First."), Vault("/Skills/own-skill", "Second.") };
        var index = new FakeIndex(wordsOnly: ["Work"], hits: [Hit("/Work/_skills/work-skill/SKILL.md", 0.9), Hit("/Skills/own-skill/SKILL.md", 0.3)]);

        var hit = Assert.Single(await SkillSearch.RankAsync(skills, "nothing in common", index, Ct));

        Assert.Equal("own-skill", hit.Skill.Name);
    }

    [Fact]
    public async Task Meaning_counts_only_when_the_index_is_ready()
    {
        var skills = new[] { Vault("/Work/_skills/work-skill", "First.") };
        var index = new FakeIndex(SemanticState.Preparing, hits: Hit("/Work/_skills/work-skill/SKILL.md", 0.9));

        Assert.Empty(await SkillSearch.RankAsync(skills, "nothing in common", index, Ct));
    }

    [Fact]
    public async Task System_skills_rank_by_words()
    {
        var skills = new[] { BuiltIn("other", "Plans a trip."), BuiltIn("create-skill", "Write a new skill for the user.") };
        var index = new FakeIndex();

        var hit = Assert.Single(await SkillSearch.RankAsync(skills, "write a new skill", index, Ct));

        Assert.Equal("create-skill", hit.Skill.Name);
        // No vault skill listed: nothing to search by meaning.
        Assert.Empty(index.Calls);
    }

    [Fact]
    public async Task A_skill_in_no_list_isnt_returned()
    {
        var skills = new[] { Vault("/Work/_skills/x", "First."), Vault("/Work/_skills/y", "Second.") };
        // A hit on another note of x's folder, on a note outside any skill and on a skill nobody listed.
        var index = new FakeIndex(hits: [Hit("/Work/_skills/x/examples/a.md", 0.9), Hit("/Work/plan.md", 0.8), Hit("/Work/_skills/hidden/SKILL.md", 0.7), Hit("/Work/_skills/y/SKILL.md", 0.2)]);

        var hit = Assert.Single(await SkillSearch.RankAsync(skills, "nothing in common", index, Ct));

        Assert.Equal("y", hit.Skill.Name);
    }

    [Fact]
    public async Task The_score_is_the_fused_rank()
    {
        var skills = new[] { Vault("/Work/_skills/garden", "Plans the garden.", "# Steps\nWalk through the garden.\n"), Vault("/Work/_skills/other", "Something else.") };

        var hit = Assert.Single(await SkillSearch.RankAsync(skills, "garden", new FakeIndex(), Ct));

        Assert.Equal(2 / 61.0 + 1 / 61.0, hit.Score, 10);
        Assert.Equal("0.0492", hit.Score.ToString("0.0000", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task At_most_ten()
    {
        var skills = Enumerable.Range(1, 12).Select(i => Vault($"/Work/_skills/garden-{i:00}", "Plans the garden.")).ToList();

        var hits = await SkillSearch.RankAsync(skills, "garden", new FakeIndex(), Ct);

        Assert.Equal(SkillSearch.Max, hits.Count);
        Assert.Equal(10, SkillSearch.Max);
        Assert.Equal(Enumerable.Range(1, 10), hits.Select(h => h.Rank));
        Assert.Equal(3, (await SkillSearch.RankAsync(skills, "garden", new FakeIndex(), Ct, max: 3)).Count);
    }
}
