// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>similar</c> and the exclusions it shares with <c>semsearch</c> and <c>graph</c> (spec 2026-11-22-knowledge-synthesis).</summary>
public class SimilarTests
{
    /// <summary>
    /// An index whose stored vectors are the notes' chunks as the shell splits them, each given the vector of the first
    /// word of <see cref="Words"/> its text contains (a chunk with none has no embedding: the note counts as pending).
    /// </summary>
    private sealed class FakeIndex(InMemoryNoteStore store, Dictionary<string, float[]> words, Dictionary<string, string>? spaces = null) : ISearchIndex
    {
        public bool Off { get; init; }

        /// <summary>Run once the vectors below a vault or folder are read: before the candidates are ranked.</summary>
        public Action? ScopeRead { get; init; }

        public Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct) => throw new NotSupportedException();

        public async Task<StoredVectors> VectorsAsync(VaultPath scope, CancellationToken ct)
        {
            if (Off) return new StoredVectors(SemanticState.Off, [], 0, "search by meaning is off in this test");
            var notes = scope.IsNote && await store.ReadAsync(scope, ct) is { } own ? [new Note(scope, own)] : await store.ReadNotesUnderAsync(scope, ct);
            var chunks = new List<ChunkVector>();
            var pending = 0;
            foreach (var note in notes)
            {
                var embedded = Chunker.Split(note, ChunkBound.Fallback)
                    .Select(c => (Chunk: c, Word: words.Keys.FirstOrDefault(w => c.Text.Contains(w, StringComparison.Ordinal))))
                    .Where(c => c.Word is not null)
                    .Select(c => new ChunkVector(note.Path, c.Chunk.StartLine, c.Chunk.Part, spaces?.GetValueOrDefault(note.Path.Value) ?? "m", Unit(words[c.Word!])))
                    .ToList();
                if (embedded.Count == 0) pending++;
                chunks.AddRange(embedded);
            }

            if (!scope.IsNote) ScopeRead?.Invoke();
            return new StoredVectors(SemanticState.Ready, chunks, pending);
        }

        private static float[] Unit(float[] v)
        {
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            return [.. v.Select(x => x / norm)];
        }
    }

    // Fish oil and Raynaud's, in small: the vectors put Viscosity next to Fish, Raynaud a little farther, Island
    // farther still, and Far at a right angle.
    private static readonly Dictionary<string, float[]> Words = new()
    {
        ["fish"] = [1, 0, 0],
        ["viscosity"] = [0.9f, 0.43589f, 0],
        ["raynaud"] = [0.8f, 0.6f, 0],
        ["island"] = [0.6f, 0.8f, 0],
        ["far"] = [0, 0, 1],
    };

    private static InMemoryNoteStore Store(params (string Path, string Content)[] extra) => new(
    [
        ("/R/Fish.md", "fish oil lowers blood viscosity\n\n[[Viscosity]]\n"),
        ("/R/Viscosity.md", "viscosity of blood\n"),
        ("/R/Island.md", "island notes nobody links\n"),
        ("/R/Far.md", "far from everything\n"),
        ("/H/Raynaud.md", "raynaud and cold hands\n"),
        .. extra,
    ]);

    private static Task<ShellResult> Run(string line, InMemoryNoteStore? store = null, Dictionary<string, string>? spaces = null, bool off = false)
    {
        store ??= Store();
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var caller = new FixedCaller(Writers.Dev);
        var shell = new Shell(store, new NoteWriter(store, time, caller), new NoteMover(store, time, caller), search: new FakeIndex(store, Words, spaces) { Off = off });
        return shell.RunAsync(line, CancellationToken.None);
    }

    private static List<string> Hits(ShellResult result) => [.. result.Output.Split('\n').Where(l => l.Length > 0 && char.IsAsciiDigit(l[0]))];

    [Fact]
    public async Task Ranks_one_section_per_note_with_its_link_distance()
    {
        var result = await Run("similar /R/Fish.md");

        Assert.False(result.IsError, result.Output);
        Assert.StartsWith("similar to /R/Fish.md, below every vault: 4 notes\n", result.Output);
        Assert.Equal(
            [
                "1. /R/Viscosity.md  lines 1-1  score 0.90  1 step",
                "2. /H/Raynaud.md  lines 1-1  score 0.80  other vault",
                "3. /R/Island.md  lines 1-1  score 0.60  not connected",
                "4. /R/Far.md  lines 1-1  score 0.00  not connected",
            ],
            Hits(result));
        Assert.Contains("   viscosity of blood\n", result.Output);
        Assert.EndsWith("(by the stored embeddings, nothing sent to an embedding provider; distance counts links either way; cat \"path#Section\" reads one)", result.Output);
    }

    [Fact]
    public async Task Ends_when_its_token_is_cancelled_while_it_ranks()
    {
        var store = Store();
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var caller = new FixedCaller(Writers.Dev);
        using var left = new CancellationTokenSource();
        var shell = new Shell(store, new NoteWriter(store, time, caller), new NoteMover(store, time, caller), search: new FakeIndex(store, Words) { ScopeRead = left.Cancel });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shell.RunAsync("similar /R/Fish.md", left.Token));
    }

    [Fact]
    public async Task Answers_busy_when_ranking_runs_out_of_time()
    {
        var store = Store();
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var caller = new FixedCaller(Writers.Dev);
        var shell = new Shell(store, new NoteWriter(store, time, caller), new NoteMover(store, time, caller),
            search: new FakeIndex(store, Words) { ScopeRead = () => time.Advance(HeavyCall.TimeLimit) });

        var result = await shell.RunAsync("similar /R/Fish.md", TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("similar: the server is busy comparing notes; try again in a moment", result.Output);
    }

    [Fact]
    public async Task Unlinked_leaves_out_what_the_note_links_and_what_links_it()
    {
        var store = Store(("/R/Back.md", "fish again\n\n[[Fish]]\n"));

        var hits = Hits(await Run("similar --unlinked /R/Fish.md", store));

        Assert.DoesNotContain(hits, h => h.Contains("/R/Viscosity.md") || h.Contains("/R/Back.md"));
        Assert.Contains(hits, h => h.Contains("/R/Island.md"));
    }

    [Fact]
    public async Task Unlinked_leaves_out_the_pairs_called_not_related_from_either_side()
    {
        var store = Store(("/R/Cod.md", "---\nnot-related: [Island]\n---\nfish liver oil\n"), ("/R/Echo.md", "---\nnot-related: [Cod]\n---\nviscosity echo\n"));

        var unlinked = Hits(await Run("similar --unlinked /R/Cod.md", store));
        var all = Hits(await Run("similar /R/Cod.md", store));

        Assert.DoesNotContain(unlinked, h => h.Contains("/R/Island.md") || h.Contains("/R/Echo.md"));
        Assert.Contains(unlinked, h => h.Contains("/R/Viscosity.md"));
        Assert.Contains(all, h => h.Contains("/R/Island.md"));
    }

    [Fact]
    public async Task Distance_counts_links_through_other_notes()
    {
        var store = Store(("/R/Middle.md", "[[Viscosity]] [[Island]]\n"));

        var hits = Hits(await Run("similar /R/Fish.md /R", store));

        Assert.Contains("2. /R/Island.md  lines 1-1  score 0.60  3 steps", hits);
    }

    [Fact]
    public async Task Outside_and_a_scope_narrow_the_candidates()
    {
        Assert.Equal(["1. /H/Raynaud.md  lines 1-1  score 0.80  other vault"], Hits(await Run("similar /R/Fish.md --outside /R")));
        Assert.Equal(3, Hits(await Run("similar /R/Fish.md /R")).Count);
        Assert.Equal(2, Hits(await Run("similar /R/Fish.md --outside /R/Viscosity.md --outside /H")).Count);
    }

    [Fact]
    public async Task Without_tag_leaves_out_earlier_findings_also_from_the_distances()
    {
        var store = Store(("/R/Finding.md", "#synthesis/hypothesis\n\nfar: [[Fish]] and [[Island]]\n"));

        var with = Hits(await Run("similar /R/Fish.md", store));
        var without = Hits(await Run("similar /R/Fish.md --without-tag synthesis", store));

        Assert.Contains("3. /R/Island.md  lines 1-1  score 0.60  2 steps", with);
        Assert.Contains(with, h => h.Contains("/R/Finding.md"));
        Assert.Contains("3. /R/Island.md  lines 1-1  score 0.60  not connected", without);
        Assert.DoesNotContain(without, h => h.Contains("/R/Finding.md"));
    }

    [Fact]
    public async Task Between_ranks_by_the_lower_of_the_two_scores()
    {
        var result = await Run("similar --between /R/Fish.md /R/Island.md");

        Assert.False(result.IsError, result.Output);
        Assert.StartsWith("between A = /R/Fish.md and C = /R/Island.md, below every vault: 3 notes\n", result.Output);
        Assert.Equal(
            [
                "1. /R/Viscosity.md  lines 1-1  score 0.89 (0.90 to A, 0.89 to C)  1 step from A, not connected to C",
                "2. /H/Raynaud.md  lines 1-1  score 0.80 (0.80 to A, 0.96 to C)  other vault from A, other vault to C",
                "3. /R/Far.md  lines 1-1  score 0.00 (0.00 to A, 0.00 to C)  not connected from A, not connected to C",
            ],
            Hits(result));
    }

    [Fact]
    public async Task A_section_compares_only_its_own_text()
    {
        var long1 = string.Join(' ', Enumerable.Repeat("fish oil and its effects on blood", 40));
        var long2 = string.Join(' ', Enumerable.Repeat("far away thoughts about something else", 40));
        var store = Store(("/R/Mixed.md", $"# Oil\n\n{long1}\n\n# Elsewhere\n\n{long2}\n"));

        var oil = Hits(await Run("similar \"/R/Mixed.md#Elsewhere\"", store));

        Assert.StartsWith("1. /R/Far.md", oil[0]);
        Assert.StartsWith("1. /R/Mixed.md#Oil", Hits(await Run("similar /R/Fish.md", store))[0]);
    }

    [Fact]
    public async Task A_section_merged_into_its_parents_chunk_compares_that_chunk()
    {
        // ## Ovens is short, so it shares the chunk that starts at the title; ## Nitrogen is long enough for its own.
        var nitrogen = string.Join(' ', Enumerable.Repeat("fish oil and its effects on blood", 40));
        var store = Store(("/R/Kitchen.md", $"# Kitchen\n\n## Ovens\n\nfar too hot\n\n## Nitrogen\n\n{nitrogen}\n"));

        var result = await Run("similar \"/R/Kitchen.md#Ovens\"", store);

        Assert.False(result.IsError, result.Output);
        Assert.StartsWith("1. /R/Far.md", Hits(result)[0]);
        Assert.StartsWith("1. /R/Fish.md", Hits(await Run("similar \"/R/Kitchen.md#Nitrogen\"", store))[0]);
    }

    [Fact]
    public async Task Notes_of_another_model_and_unindexed_ones_are_counted_not_compared()
    {
        var store = Store(("/R/Empty.md", "nothing here\n"));

        var result = await Run("similar /R/Fish.md", store, new Dictionary<string, string> { ["/H/Raynaud.md"] = "other-model" });

        Assert.DoesNotContain(Hits(result), h => h.Contains("/H/Raynaud.md"));
        Assert.Contains("1 note isn't indexed yet and not compared; 1 note is embedded by another model and not compared", result.Output);
    }

    [Fact]
    public async Task Between_two_models_is_refused()
    {
        var result = await Run("similar --between /R/Fish.md /H/Raynaud.md", spaces: new Dictionary<string, string> { ["/H/Raynaud.md"] = "other-model" });

        Assert.True(result.IsError);
        Assert.Contains("embedded by different models", result.Output);
    }

    [Theory]
    [InlineData("similar /R/Nothing.md", "No such file")]
    [InlineData("similar /R/Fish.md /R/Viscosity.md", "is a note; the path to search below is a vault or a folder")]
    [InlineData("similar --between /R/Fish.md", "--between needs two notes")]
    [InlineData("similar /R/Fish.md -n 0", "-n must be 1 to 50")]
    [InlineData("similar /R/Fish.md --without-tag 'a b'", "can't be a tag")]
    [InlineData("similar /R/Fish.md --outside /", "would leave out everything")]
    [InlineData("similar \"/R/Fish.md#Nope\"", "no such section")]
    public async Task Refuses_what_it_cannot_answer(string line, string expected)
    {
        var result = await Run(line);

        Assert.True(result.IsError, result.Output);
        Assert.Contains(expected, result.Output);
    }

    [Fact]
    public async Task A_source_without_embeddings_says_so()
    {
        var result = await Run("similar /R/Empty.md", Store(("/R/Empty.md", "nothing here\n")));

        Assert.True(result.IsError);
        Assert.Contains("/R/Empty.md isn't indexed yet; try again in a while, or semsearch for its topic meanwhile", result.Output);
    }

    [Fact]
    public async Task Without_an_index_it_says_why()
    {
        var result = await Run("similar /R/Fish.md", off: true);

        Assert.True(result.IsError);
        Assert.Contains("off in this test", result.Output);
    }

    [Fact]
    public async Task Semsearch_leaves_out_a_silo_and_a_tag()
    {
        var store = Store(("/H/Finding.md", "#synthesis/hypothesis\n\nblood viscosity and raynaud\n"));

        var outside = await Run("semsearch --words --outside /R blood", store);
        var withoutTag = await Run("semsearch --words --without-tag synthesis blood", store);

        Assert.DoesNotContain("/R/", outside.Output);
        Assert.Contains("/H/Finding.md", outside.Output);
        Assert.DoesNotContain("/H/Finding.md", withoutTag.Output);
        Assert.Contains("/R/Fish.md", withoutTag.Output);
    }

    [Fact]
    public async Task Graph_path_without_tag_walks_around_earlier_findings()
    {
        var store = Store(("/R/Finding.md", "#synthesis/hypothesis\n\n[[Fish]] and [[Island]]\n"));

        Assert.Contains("2 steps", (await Run("graph --path /R/Fish.md /R/Island.md", store)).Output);
        Assert.Equal(
            "/R/Fish.md and /R/Island.md are not connected at all (no path of any length)",
            (await Run("graph --path /R/Fish.md /R/Island.md --without-tag synthesis/hypothesis", store)).Output);
    }

    [Fact]
    public async Task Graph_hubs_without_tag_counts_only_the_links_of_the_notes_kept()
    {
        var store = Store(("/R/Finding.md", "#synthesis/hypothesis\n\n[[Fish]] and [[Island]]\n"));

        var with = (await Run("graph --hubs /R", store)).Output;
        var without = (await Run("graph --hubs /R --without-tag synthesis", store)).Output;

        Assert.Contains("2  /R/Fish.md  (1 in, 1 out)", with);
        Assert.Contains("1  /R/Island.md  (1 in, 0 out)", with);
        Assert.Contains("1  /R/Fish.md  (0 in, 1 out)", without);
        Assert.Contains("0  /R/Island.md  (0 in, 0 out)", without);
        Assert.DoesNotContain("/R/Finding.md", without);
    }

    [Fact]
    public async Task Graph_orphans_without_tag_count_only_the_links_of_the_notes_kept()
    {
        var store = Store(("/R/Finding.md", "#synthesis/hypothesis\n\n[[Fish]] and [[Island]]\n"));

        var with = (await Run("graph --orphans /R", store)).Output;
        var without = (await Run("graph --orphans /R --without-tag synthesis", store)).Output;

        // Island is linked by the hypothesis alone; Far by nothing.
        Assert.DoesNotContain("/R/Island.md", with);
        Assert.Contains("/R/Far.md", with);
        Assert.Contains("/R/Island.md", without);
        Assert.Contains("/R/Far.md", without);
        Assert.DoesNotContain("/R/Finding.md", without);
    }
}
