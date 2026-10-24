// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Search;

public class SearchTests
{
    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    [Fact]
    public void ChunksAreSectionsWithTheirHeadingPath()
    {
        var chunks = Chunker.Split(new Note(P("/Demo/plan.md"), "---\ntags: x\n---\nIntro text.\n\n# Plan\nTop.\n## Goals\nShip it.\n\n## Risks\n\n"));

        Assert.Equal(
            [("", 3, 4, "Intro text.", "plan"), ("#Plan", 5, 7, "# Plan\nTop.", "plan > Plan"), ("#Plan#Goals", 7, 9, "## Goals\nShip it.", "plan > Plan > Goals"), ("#Plan#Risks", 10, 11, "## Risks", "plan > Plan > Risks")],
            chunks.Select(c => (c.Address, c.StartLine, c.EndLine, c.Text, c.Context)));
        Assert.Equal("plan > Plan > Goals\n## Goals\nShip it.", chunks[2].EmbeddingText);
    }

    [Fact]
    public void ALongSectionIsSplitBetweenParagraphs()
    {
        var paragraph = string.Join(' ', Enumerable.Repeat("word", 150));
        var content = "# Long\n" + string.Join("\n\n", Enumerable.Range(0, 6).Select(_ => paragraph)) + "\n";

        var chunks = Chunker.Split(new Note(P("/Demo/long.md"), content));

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= Chunker.MaxChars));
        Assert.All(chunks, c => Assert.Equal("#Long", c.Address));
        Assert.All(chunks.Skip(1), c => Assert.False(string.IsNullOrWhiteSpace(c.Text.Split('\n')[0])));
    }

    [Fact]
    public void Bm25PrefersRareTermsAndShortDocuments()
    {
        var ranked = Bm25.Rank(["the gate checks rules", "the the the the", "rules rules and more about the gate service with many other words around it"], "Gate RULES");

        Assert.Equal([0, 2], ranked.Select(r => r.Index));
        Assert.Empty(Bm25.Rank(["abc"], "!!!"));
        // Stop words and single letters don't rank anything.
        Assert.Empty(Bm25.Rank(["a farm on the hill", "the end"], "the a on x"));
        Assert.Equal(["farm"], Bm25.QueryTerms("the FARM, a farm"));
    }

    [Fact]
    public void ASingleDigitStaysAQueryTerm()
    {
        Assert.Equal(["topic", "3"], Bm25.QueryTerms("topic 3"));
        Assert.Empty(Bm25.QueryTerms("a b"));
    }

    private sealed class FakeIndex(SemanticResult result) : ISearchIndex
    {
        public string? LastQuery { get; private set; }

        public Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct)
        {
            LastQuery = query;
            return Task.FromResult(result);
        }
    }

    private static readonly InMemoryNoteStore Store = new(
        ("/Demo/gate.md", "# Gate\nThe service checks rules at the entrance.\n# Team\nAnna and Ben.\n"),
        ("/Demo/farm.md", "# Animals\nCats, dogs, hens and ducks live together.\n"),
        ("/Ideas/x.md", "rules elsewhere\n"));

    private static Task<ShellResult> Run(string line, ISearchIndex index, InMemoryNoteStore? store = null)
    {
        store ??= Store;
        var time = new FakeTimeProvider();
        return new Shell(store, new NoteWriter(store, time), new NoteMover(store, time), search: index).RunAsync(line, CancellationToken.None);
    }

    private static FakeIndex Cosines(params (string Path, int Line, double Score)[] hits) =>
        new(new SemanticResult(SemanticState.Ready, [.. hits.Select(h => new SemanticHit(P(h.Path), h.Line, h.Score))], 0));

    [Fact]
    public async Task SemsearchAddsAWordBonusToTheMeaningScore()
    {
        // The farm's cosine is high enough to stay ahead; the gate gets half the word bonus ("rules" but not "door").
        var index = new FakeIndex(new SemanticResult(SemanticState.Ready, [new(P("/Demo/farm.md"), 0, 0.8), new(P("/Demo/gate.md"), 0, 0.5)], 0));

        var result = await Run("semsearch -n 2 rules at the door /Demo", index);

        Assert.Equal(
            """
            1. /Demo/farm.md#Animals  lines 1-2  score 0.80
               Cats, dogs, hens and ducks live together.
            2. /Demo/gate.md#Gate  lines 1-2  score 0.55
               The service checks rules at the entrance.
            (ranked by words and meaning; cat "path#Section" reads one)
            """,
            result.Output);
        Assert.Equal("rules at the door", index.LastQuery);
    }

    [Fact]
    public async Task SemsearchFallsBackToWordsAndSaysWhy()
    {
        var index = new FakeIndex(new SemanticResult(SemanticState.Preparing, [], 0, "the embedding model is still being downloaded"));

        var result = await Run("semsearch rules", index);

        Assert.Equal(
            """
            1. /Ideas/x.md  lines 1-1
               rules elsewhere
            2. /Demo/gate.md#Gate  lines 1-2
               The service checks rules at the entrance.
            (ranked by words only: the embedding model is still being downloaded; cat "path#Section" reads one)
            """,
            result.Output);
        Assert.Equal(
            ShellResult.Error("semsearch: the embedding model is still being downloaded; without --meaning, it ranks by words"),
            await Run("semsearch --meaning rules", index));
    }

    [Fact]
    public async Task SemsearchSaysWhatIsNotIndexedYet()
    {
        var index = new FakeIndex(new SemanticResult(SemanticState.Ready, [new(P("/Demo/farm.md"), 0, 0.5)], 2));
        Assert.EndsWith("(ranked by words and meaning; 2 notes aren't indexed yet and aren't ranked by meaning; semsearch --words finds them; cat \"path#Section\" reads one)", (await Run("semsearch rules /Demo", index)).Output);
        Assert.EndsWith("(ranked by meaning; 2 notes aren't indexed yet and aren't ranked; cat \"path#Section\" reads one)", (await Run("semsearch --meaning rules /Demo", index)).Output);
    }

    [Fact]
    public async Task PendingNotesAreNamedWithWords()
    {
        var index = new FakeIndex(new SemanticResult(SemanticState.Ready, [new(P("/Demo/farm.md"), 0, 0.5)], 1));

        Assert.EndsWith("1 note isn't indexed yet and isn't ranked by meaning; semsearch --words finds it; cat \"path#Section\" reads one)", (await Run("semsearch rules /Demo", index)).Output);
    }

    [Fact]
    public async Task SemsearchRanksByWordsWhileNothingIsIndexedYet()
    {
        var index = new FakeIndex(new SemanticResult(SemanticState.Ready, [], 2));

        Assert.Equal(
            """
            1. /Demo/gate.md#Gate  lines 1-2
               The service checks rules at the entrance.
            (ranked by words only: 2 notes aren't indexed yet; cat "path#Section" reads one)
            """,
            (await Run("semsearch rules /Demo", index)).Output);
        Assert.Equal(
            "(nothing found for 'zebra' below /Demo)\n(ranked by words only: 2 notes aren't indexed yet)",
            (await Run("semsearch zebra /Demo", index)).Output);
        Assert.EndsWith("(ranked by meaning; 2 notes aren't indexed yet and aren't ranked)", (await Run("semsearch --meaning rules /Demo", index)).Output);
        Assert.EndsWith(
            "(ranked by words only: 1 note isn't indexed yet; cat \"path#Section\" reads one)",
            (await Run("semsearch rules /Demo", new FakeIndex(new SemanticResult(SemanticState.Ready, [], 1)))).Output);
    }

    [Fact]
    public async Task WordBonusLiftsASectionContainingTheQueryTerms()
    {
        // 0.35 + 0.1 beats 0.40: the farm section has both query terms.
        var index = Cosines(("/Demo/gate.md", 0, 0.40), ("/Demo/farm.md", 0, 0.35));

        var result = await Run("semsearch -n 1 cats ducks /Demo", index);

        Assert.StartsWith("1. /Demo/farm.md#Animals  lines 1-2  score 0.45\n", result.Output);
    }

    [Fact]
    public async Task NoQueryTermsRanksByCosineAlone()
    {
        var index = Cosines(("/Demo/farm.md", 0, 0.30), ("/Demo/gate.md", 0, 0.60));

        var result = await Run("semsearch der die das /Demo", index);

        Assert.StartsWith("1. /Demo/gate.md#Gate  lines 1-2  score 0.60\n", result.Output);
        Assert.Contains("2. /Demo/farm.md#Animals  lines 1-2  score 0.30\n", result.Output);
    }

    [Fact]
    public async Task ScoresArePrintedWithTwoDecimals()
    {
        var index = Cosines(("/Demo/farm.md", 0, 0.3456));

        Assert.Contains("score 0.35\n", (await Run("semsearch -n 1 --meaning animals /Demo", index)).Output);
        Assert.DoesNotContain("score", (await Run("semsearch --words cats /Demo", index)).Output);
    }

    [Fact]
    public async Task HeadingOnlySectionsAreLeftOut()
    {
        var store = new InMemoryNoteStore(("/Demo/diary.md", "# Diary\n## Februar 2026\n## März 2026\nWir waren im Zoo.\n"));
        var index = Cosines(("/Demo/diary.md", 1, 0.90), ("/Demo/diary.md", 2, 0.50));

        var result = await Run("semsearch -n 1 --meaning Zoo /Demo", index, store);

        Assert.StartsWith("1. /Demo/diary.md#Diary#März 2026", result.Output);
        Assert.DoesNotContain("Februar", result.Output);
    }

    [Theory]
    [InlineData("semsearch", "semsearch: missing query (semsearch <query> [path])")]
    [InlineData("semsearch /Demo", "semsearch: missing query; /Demo is the path (semsearch <query> /Demo)")]
    [InlineData("semsearch rules /Nope/x", "semsearch: /Nope/x: No such file or directory")]
    [InlineData("semsearch -n 0 rules", "semsearch: invalid number of results: '0'")]
    [InlineData("semsearch --words --meaning rules", "semsearch: --words and --meaning exclude each other; leave both out to combine them")]
    public async Task SemsearchErrors(string line, string error) =>
        Assert.Equal(ShellResult.Error(error), await Run(line, new NoSearchIndex()));

    [Fact]
    public async Task NothingFound() =>
        Assert.StartsWith("(nothing found for 'zebra' below /Demo)", (await Run("semsearch --words zebra /Demo", new NoSearchIndex())).Output);
}
