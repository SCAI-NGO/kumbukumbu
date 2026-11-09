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
    public void Snippet_of_a_table_part_keeps_its_header_row()
    {
        var part = new Chunk(P("/Work/Pump/Manual.md"), "#Pump P-200#Error codes", 120, 140,
            "| Code | Meaning | Remedy |\n|------|---------|--------|\n| E115 | Dry run | Fill the inlet and restart |", "Manual > Pump P-200 > Error codes");
        var first = part with { Text = "## Error codes\n" + part.Text };

        Assert.StartsWith("| Code | Meaning | Remedy |", SectionSearch.Snippet(part));
        Assert.StartsWith("| Code | Meaning | Remedy |", SectionSearch.Snippet(first));
        Assert.Equal("| E115 | Dry run | Fill the inlet and restart |", SectionSearch.Snippet(part, "inlet"));

        // A merged chunk holds several headings; none of them is in the snippet.
        var merged = new Chunk(P("/Demo/gate.md"), "", 0, 4, "# Gate\nThe service checks rules.\n# Team\nAlice and Bob.", "gate");
        Assert.Equal("The service checks rules. Alice and Bob.", SectionSearch.Snippet(merged));
        Assert.Equal("Alice and Bob.", SectionSearch.Snippet(merged, "team alice"));

        // A comment in fenced code is no heading.
        var script = new Chunk(P("/Demo/run.md"), "#Run", 0, 5, "# Run\n```sh\n# start the pump\npump start\n```", "run > Run");
        Assert.Equal("```sh # start the pump pump start ```", SectionSearch.Snippet(script));
        Assert.Equal("# start the pump", SectionSearch.Snippet(script, "pump"));
    }

    [Fact]
    public async Task Locate_finds_a_piece_by_its_part()
    {
        // A line too long for one chunk is split into pieces on the same start line; a hit names its piece.
        var line = string.Join(' ', Enumerable.Range(0, 1_200).Select(i => $"reading{i}"));
        Assert.True(line.Length >= 10_000);
        var note = new Note(P("/Work/log.md"), "# Log\n" + line + "\n");
        Assert.True(Chunker.Split(note, ChunkBound.Fallback).Count(c => c.StartLine == 1) > 2);
        var index = new BoundIndex(new SemanticResult(SemanticState.Ready, [new(note.Path, 1, 0.6), new(note.Path, 1, 0.7) { Part = 2 }], 0), ChunkBound.Fallback);

        var result = await SectionSearch.RankAsync([note], VaultPath.Root, "reading5", SearchMode.Meaning, 10, index, CancellationToken.None);

        Assert.Equal([(1, 2, 0.7), (1, 0, 0.6)], result.Hits.Select(h => (h.Chunk.StartLine, h.Chunk.Part, Math.Round(h.Score!.Value, 2))));

        // A part the note no longer has goes to the chunk that contains the start line.
        var gone = new BoundIndex(new SemanticResult(SemanticState.Ready, [new(note.Path, 1, 0.5) { Part = 99 }], 0), ChunkBound.Fallback);
        var located = Assert.Single((await SectionSearch.RankAsync([note], VaultPath.Root, "reading5", SearchMode.Meaning, 10, gone, CancellationToken.None)).Hits);
        Assert.Equal((1, 0), (located.Chunk.StartLine, located.Chunk.Part));
    }

    [Fact]
    public async Task One_hit_per_PDF_page_with_its_best_part()
    {
        var pdf = P("/Work/manual.pdf");
        var index = new FakeIndex(new SemanticResult(SemanticState.Ready,
            [new(pdf, 3, 0.4), new(pdf, 3, 0.7) { Part = 2 }, new(pdf, 3, 0.5) { Part = 1 }, new(pdf, 5, 0.6)], 0));

        var result = await SectionSearch.RankAsync([], VaultPath.Root, "seal", SearchMode.Meaning, 10, index, CancellationToken.None, pages: true);

        Assert.Equal([(3, 2, 0.7), (5, 0, 0.6)], result.Hits.Select(h => (h.Page!.Value, h.PagePart!.Value, Math.Round(h.Score!.Value, 2))));
        Assert.All(result.Hits, h => Assert.Equal(pdf, h.Chunk.Path));
    }

    [Fact]
    public async Task Stale_notes_are_ranked_by_words_meanwhile()
    {
        // farm.md is being chunked anew: no cosines of its own, but its words still find it, fused by rank.
        var notes = await Store.ReadNotesUnderAsync(VaultPath.Root, CancellationToken.None);
        var index = new FakeIndex(new SemanticResult(SemanticState.Ready, [new(P("/Demo/gate.md"), 0, 0.5)], 1) { StaleNotes = [P("/Demo/farm.md")] });

        var hybrid = await SectionSearch.RankAsync(notes, VaultPath.Root, "hens ducks", SearchMode.Hybrid, 10, index, CancellationToken.None);
        var meaning = await SectionSearch.RankAsync(notes, VaultPath.Root, "hens ducks", SearchMode.Meaning, 10, index, CancellationToken.None);

        Assert.Equal([("/Demo/gate.md", (double?)0.5), ("/Demo/farm.md", null)], hybrid.Hits.Select(h => (h.Chunk.Path.Value, h.Score is { } s ? Math.Round(s, 2) : (double?)null)));
        Assert.False(hybrid.FellBackToWords);
        Assert.Equal(["/Demo/gate.md"], meaning.Hits.Select(h => h.Chunk.Path.Value));
    }

    private sealed class BoundIndex(SemanticResult result, ChunkBound bound) : ISearchIndex
    {
        public Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct) => Task.FromResult(result);

        public ChunkBound Bound => bound;
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
        return new Shell(store, new NoteWriter(store, time, new FixedCaller(Writers.Dev)), new NoteMover(store, time, new FixedCaller(Writers.Dev)), search: index).RunAsync(line, CancellationToken.None);
    }

    private static FakeIndex Cosines(params (string Path, int Line, double Score)[] hits) =>
        new(new SemanticResult(SemanticState.Ready, [.. hits.Select(h => new SemanticHit(P(h.Path), h.Line, h.Score))], 0));

    [Fact]
    public async Task SemsearchLeavesArchivedNotesOutUnlessAsked()
    {
        var store = new InMemoryNoteStore(
            ("/Demo/gate.md", "# Gate\nThe service checks rules at the entrance.\n"),
            ("/Demo/_archive/old.md", "# Old\nThe old rules.\n"),
            ("/Gone/_archive/a.md", "rules of old\n"));
        var index = Cosines(("/Demo/_archive/old.md", 0, 0.9), ("/Demo/gate.md", 0, 0.5), ("/Gone/_archive/a.md", 0, 0.8));

        foreach (var mode in new[] { "", "--words " })
        {
            var hidden = (await Run($"semsearch {mode}rules /Demo", index, store)).Output;
            Assert.Contains("/Demo/gate.md#Gate", hidden);
            Assert.DoesNotContain("_archive", hidden);
            Assert.DoesNotContain("_archive", (await Run($"semsearch {mode}rules", index, store)).Output);
            Assert.Contains("/Demo/_archive/old.md#Old", (await Run($"semsearch --archive {mode}rules /Demo", index, store)).Output);
            Assert.Contains("/Demo/_archive/old.md#Old", (await Run($"semsearch {mode}rules /Demo/_archive/", index, store)).Output);
            Assert.Contains("/Demo/_archive/old.md#Old", (await Run($"semsearch {mode}rules /Demo/_archive/old.md", index, store)).Output);

            // Nothing found while an archived note holds the words: the answer says how to search the archive.
            var empty = await Run($"semsearch {mode}rules /Gone", index, store);
            Assert.False(empty.IsError, empty.Output);
            Assert.StartsWith($"(nothing found for 'rules' below /Gone outside the archive; semsearch --archive {mode}rules /Gone searches it)\n", empty.Output);
            Assert.StartsWith("(nothing found for 'zebras' below /Gone)\n", (await Run($"semsearch {mode}zebras /Gone", index, store)).Output);
        }

        Assert.StartsWith(
            "(nothing found for 'old rules' outside the archive; semsearch --archive -n 3 --meaning old rules searches it)\n",
            (await Run("semsearch -n 3 --meaning old rules", Cosines(), store)).Output);
        Assert.StartsWith(
            "(nothing found for 'rules of' below /Gone too outside the archive; semsearch --archive --words rules of '/Gone too' searches it)\n",
            (await Run("semsearch --words rules of \"/Gone too\"", index, new InMemoryNoteStore(("/Gone too/_archive/a.md", "rules of old\n")))).Output);

        var tagged = new InMemoryNoteStore(("/Demo/gate.md", "rules\n"), ("/Demo/_archive/old.md", "#old rules\n"), ("/Demo/_archive/new.md", "#new rules\n"), ("/Demo/now.md", "#new\n"));
        Assert.Equal(
            ShellResult.Error("semsearch: only archived notes below /Demo carry #old; semsearch --archive --words --tag old rules /Demo searches them"),
            await Run("semsearch --words --tag old rules /Demo", index, tagged));
        // Archived notes without the tag don't count for the hint.
        Assert.StartsWith("(nothing found for 'old' below /Demo)\n", (await Run("semsearch --words --tag new old /Demo", index, tagged)).Output);
        Assert.StartsWith(
            "(nothing found for 'rules' below /Demo outside the archive; semsearch --archive --words --tag new rules /Demo searches it)\n",
            (await Run("semsearch --words --tag new rules /Demo", index, tagged)).Output);
    }

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
            2. /Demo/gate.md  lines 1-4  score 0.55
               The service checks rules at the entrance. Anna and Ben.
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
            2. /Demo/gate.md  lines 1-4
               The service checks rules at the entrance. Anna and Ben.
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

    /// <summary>Ideas' provider is unavailable: its notes are ranked by words and merged with Demo's ranking by meaning.</summary>
    private static FakeIndex DemoByMeaningIdeasByWords() =>
        new(new SemanticResult(SemanticState.Ready, [new(P("/Demo/farm.md"), 0, 0.8), new(P("/Demo/gate.md"), 0, 0.5)], 0,
            "Ideas: the embedding provider TensorX answered 503 Service Unavailable") { WordsOnlyVaults = ["Ideas"] });

    [Fact]
    public async Task AVaultWhoseProviderIsUnavailableIsRankedByWordsAndFusedByRank()
    {
        var notes = await Store.ReadNotesUnderAsync(VaultPath.Root, CancellationToken.None);

        var hybrid = await SectionSearch.RankAsync(notes, VaultPath.Root, "rules", SearchMode.Hybrid, 10, DemoByMeaningIdeasByWords(), CancellationToken.None);
        var meaning = await SectionSearch.RankAsync(notes, VaultPath.Root, "rules", SearchMode.Meaning, 10, DemoByMeaningIdeasByWords(), CancellationToken.None);

        // Reciprocal rank fusion: Demo's best by meaning first, then Ideas' best by words, then Demo's second.
        Assert.Equal(
            [("/Demo/farm.md", (double?)0.8), ("/Ideas/x.md", null), ("/Demo/gate.md", 0.6)],
            hybrid.Hits.Select(h => (h.Chunk.Path.Value, h.Score is { } s ? Math.Round(s, 2) : (double?)null)));
        Assert.False(hybrid.FellBackToWords);
        // By meaning only, there is nothing to rank Ideas' notes with.
        Assert.DoesNotContain(meaning.Hits, h => h.Chunk.Path.Value == "/Ideas/x.md");
    }

    [Theory]
    [InlineData("große meister")]
    [InlineData("Große MEISTER")]
    public async Task TheWordPartMatchesANoteNameThatIsNotInItsText(string query)
    {
        // The title lives only in the file name (a name with spaces and an ß; the query arrives decomposed).
        var name = "Der große Meister".Normalize(System.Text.NormalizationForm.FormC);
        var notes = new List<Note>
        {
            new(P($"/Test/{name}.md"), "Nothing about it here.\n"),
            new(P("/Test/Other.md"), "Something else entirely.\n"),
        };

        var result = await SectionSearch.RankAsync(notes, VaultPath.Root, query.Normalize(System.Text.NormalizationForm.FormD), SearchMode.Words, 10, new FakeIndex(new SemanticResult(SemanticState.Ready, [], 0, null)), CancellationToken.None);

        Assert.Equal([$"/Test/{name}.md"], result.Hits.Select(h => h.Chunk.Path.Value));
    }

    [Fact]
    public async Task SemsearchNamesTheVaultsRankedByWordsOnly()
    {
        var output = (await Run("semsearch rules", DemoByMeaningIdeasByWords())).Output;
        var meaning = (await Run("semsearch --meaning rules", DemoByMeaningIdeasByWords())).Output;

        Assert.Contains("/Ideas/x.md", output);
        Assert.EndsWith(
            "(ranked by words and meaning, and by words only in Ideas: the embedding provider TensorX answered 503 Service Unavailable; cat \"path#Section\" reads one)",
            output);
        Assert.EndsWith(
            "(ranked by meaning, without Ideas: the embedding provider TensorX answered 503 Service Unavailable; semsearch --words finds those notes; cat \"path#Section\" reads one)",
            meaning);
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
            1. /Demo/gate.md  lines 1-4
               The service checks rules at the entrance. Anna and Ben.
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

        Assert.StartsWith("1. /Demo/gate.md  lines 1-4  score 0.60\n", result.Output);
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
        // März is too long to merge with, so the parent and Februar form a chunk of headings alone.
        var store = new InMemoryNoteStore(("/Demo/diary.md", "# Diary\n## Februar 2026\n## März 2026\nWir waren im Zoo.\n\n" + string.Join(' ', Enumerable.Repeat("Elefanten", 400)) + "\n"));
        var index = Cosines(("/Demo/diary.md", 0, 0.90), ("/Demo/diary.md", 2, 0.50));

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
