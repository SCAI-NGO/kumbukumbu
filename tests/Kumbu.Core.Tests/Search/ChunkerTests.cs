// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Search;

public partial class ChunkerTests
{
    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static Note Manual(string content) => new(P("/Work/Pump/Manual.md"), content);

    /// <summary>The bound by name; the tokenizer's skips the test where <c>KUMBU_TOKENIZER</c> isn't set.</summary>
    private static ChunkBound Bound(string name) => name switch
    {
        "fallback" => ChunkBound.Fallback,
        "unbounded" => ChunkBound.Unbounded,
        _ => ChunkBound.Of(Tokenizer(), TestTokenizer.Sha256),
    };

    private static UnigramTokenizer Tokenizer()
    {
        Assert.SkipUnless(TestTokenizer.Load() is not null, TestTokenizer.SkipReason);
        return TestTokenizer.Load()!;
    }

    public static TheoryData<string, string> EveryFixtureAndBound() =>
        new([.. ChunkerFixtures.All.Keys.SelectMany(f => new[] { "fallback", "unbounded", "tokenizer" }.Select(b => (f, b)))]);

    public static TheoryData<string, string> EveryFixtureAndBudget() =>
        new([.. ChunkerFixtures.All.Keys.SelectMany(f => new[] { "fallback", "tokenizer" }.Select(b => (f, b)))]);

    /// <summary>The ranges of the chunks with part 0 don't overlap and hold every non-blank line after the
    /// frontmatter; the pieces of a split line put together give the line.</summary>
    private static void AssertEveryLineInOneCore(string content, List<Chunk> chunks)
    {
        List<string> lines = [.. Lines.Split(content)];
        var owner = new int[lines.Count];
        foreach (var (chunk, i) in chunks.Where(c => c.Part == 0).Select((c, i) => (c, i + 1)))
        {
            Assert.True(chunk.StartLine < chunk.EndLine, $"empty range at {chunk.StartLine}");
            for (var line = chunk.StartLine; line < chunk.EndLine; line++)
            {
                Assert.True(owner[line] == 0, $"line {line} is in two cores");
                owner[line] = i;
            }
        }

        for (var line = Outline.FrontmatterEnd(lines); line < lines.Count; line++)
        {
            if (lines[line].Trim().Length > 0) Assert.True(owner[line] > 0, $"line {line} is in no core: {lines[line][..Math.Min(60, lines[line].Length)]}");
        }

        foreach (var pieces in chunks.GroupBy(c => c.StartLine).Where(g => g.Any(c => c.Part > 0)))
        {
            var line = lines[pieces.Key].Trim();
            Assert.Equal(Enumerable.Range(0, pieces.Count()), pieces.Select(c => c.Part));
            Assert.All(pieces, c => Assert.Equal(pieces.Key + 1, c.EndLine));
            if (line.Any(char.IsWhiteSpace)) Assert.Equal(Spaces().Replace(line, " "), string.Join(' ', pieces.Select(c => c.Text)));
            else Assert.Equal(line, string.Concat(pieces.Select(c => c.Text)));
        }
    }

    [Theory]
    [MemberData(nameof(EveryFixtureAndBound))]
    public void Every_line_is_in_exactly_one_core(string fixture, string bound)
    {
        var content = ChunkerFixtures.All[fixture];
        AssertEveryLineInOneCore(content, Chunker.Split(Manual(content), Bound(bound)));
    }

    [Theory]
    [MemberData(nameof(EveryFixtureAndBudget))]
    public void No_embedding_text_goes_over_the_budget(string fixture, string name)
    {
        var bound = Bound(name);
        var chunks = Chunker.Split(Manual(ChunkerFixtures.All[fixture]), bound);

        Assert.All(chunks, c => Assert.True(bound.Fits(c.EmbeddingText), $"{c.Address} {c.StartLine}/{c.Part}: {c.EmbeddingText.Length} characters"));
        if (name == "tokenizer") Assert.All(chunks, c => Assert.True(Tokenizer().Count(c.EmbeddingText) <= ChunkBound.MaxTokens));
        Assert.All(chunks, c => Assert.True(Chunker.Words(c.Text) <= Chunker.MaxWords));
    }

    [Fact]
    public void Words_are_runs_of_letters_and_digits()
    {
        Assert.Equal(0, Chunker.Words("| --- | :---: |\n## *_`[]()`_*"));
        Assert.Equal(6, Chunker.Words("| E001 | Dry run | Größe 4,5 |"));
        Assert.Equal(3, Chunker.Words("[link](https://x)"));
    }

    [Fact]
    public void A_table_that_fits_stays_whole()
    {
        var content = "# Pump P-200\n## Error codes\nThe codes the display shows.\n\n" + ChunkerFixtures.ErrorCodes(10);

        var chunks = Chunker.Split(Manual(content), ChunkBound.Unbounded);

        var table = Assert.Single(chunks, c => c.Text.Contains("| Code |"));
        Assert.All(Enumerable.Range(1, 10), i => Assert.Contains($"| E{i:000} |", table.Text));

        // After 300 words of prose the table doesn't fit into the same part, but it fits into the next one whole.
        var longer = "# Pump P-200\n## Error codes\n" + string.Join(' ', Enumerable.Range(0, 300).Select(i => $"word{i}")) + "\n\n" + ChunkerFixtures.ErrorCodes(10);
        var parts = Chunker.Split(Manual(longer), ChunkBound.Unbounded).Where(c => c.Address == "#Pump P-200#Error codes").ToList();

        Assert.Equal(2, parts.Count);
        Assert.StartsWith("| Code |", parts[1].Text);
        Assert.All(Enumerable.Range(1, 10), i => Assert.Contains($"| E{i:000} |", parts[1].Text));
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("tokenizer")]
    public void A_long_table_is_split_between_rows_with_its_header(string name)
    {
        var bound = Bound(name);
        var content = ChunkerFixtures.PumpManual;
        List<string> lines = [.. Lines.Split(content)];

        var parts = Chunker.Split(Manual(content), bound).Where(c => c.Text.Contains("| E")).ToList();

        Assert.True(parts.Count > 3, $"{parts.Count} parts");
        foreach (var part in parts)
        {
            Assert.Equal("#Pump P-200#Error codes", part.Address);
            Assert.Equal("Manual > Pump P-200 > Error codes", part.Context);
            var text = part.Text.Split('\n');
            var body = text.SkipWhile(l => l.StartsWith('#')).ToList();
            Assert.Equal(lines[4], body[0]);
            Assert.Equal(lines[5], body[1]);
            // No row cut: the core is the range's own lines.
            Assert.Equal(lines.Skip(part.StartLine).Take(part.EndLine - part.StartLine), text.Skip(part == parts[0] ? 0 : 2));
        }

        // The second part's range starts at its own first row; the header rows it repeats lie before it.
        Assert.True(parts[1].StartLine > ChunkerFixtures.FirstRowLine);
        Assert.Equal(-1, parts[0].RepeatedHeader);
        Assert.All(parts.Skip(1), p => Assert.Equal(ChunkerFixtures.FirstRowLine - 2, p.RepeatedHeader));
        Assert.Equal(lines[parts[1].StartLine], parts[1].Text.Split('\n')[2]);
        for (var row = ChunkerFixtures.FirstRowLine; row < ChunkerFixtures.FirstRowLine + ChunkerFixtures.TableRows; row++)
        {
            Assert.Single(parts, p => p.StartLine <= row && row < p.EndLine);
        }
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("unbounded")]
    [InlineData("tokenizer")]
    public void Short_siblings_merge_under_their_parent(string name)
    {
        var content = ChunkerFixtures.PumpManual;
        List<string> lines = [.. Lines.Split(content)];

        var chunks = Chunker.Split(Manual(content), Bound(name));

        // The heading-only parent stays with its first child (§3.3); the spec's example shows Safety alone.
        var first = chunks[0];
        Assert.Equal(("#Pump P-200", 0, 3, "Manual > Pump P-200"), (first.Address, first.StartLine, first.EndLine, first.Context));
        Assert.True(chunks.Count(c => c.Address == "#Pump P-200#Error codes") > 1);
        var last = chunks[^1];
        Assert.Equal(("#Pump P-200#Maintenance", 246, lines.FindLastIndex(l => l.Length > 0) + 1), (last.Address, last.StartLine, last.EndLine));
        Assert.Equal("Manual > Pump P-200 > Maintenance", last.Context);
        Assert.StartsWith("## Maintenance\n### Weekly\n", last.Text);
    }

    [Fact]
    public void A_split_section_ends_a_group()
    {
        var content = "# Doc\n## A\nAlice waters the poppies.\n## B\n" + string.Join("\n\n", Enumerable.Range(0, 8).Select(i => string.Join(' ', Enumerable.Repeat($"cowslip{i}", 60))))
            + "\n## C\nBob mows the meadow.\n";
        List<string> lines = [.. Lines.Split(content)];
        var c = lines.IndexOf("## C");

        var chunks = Chunker.Split(Manual(content), ChunkBound.Unbounded);

        AssertEveryLineInOneCore(content, chunks);
        Assert.DoesNotContain(chunks, x => x.StartLine <= 2 && c < x.EndLine);
        Assert.True(chunks.Count(x => x.Address == "#Doc#B") > 1);
        Assert.Equal(("#Doc#C", c), (chunks[^1].Address, chunks[^1].StartLine));
    }

    [Fact]
    public void A_table_whose_header_leaves_no_room_goes_without_it()
    {
        var (header, separator) = ChunkerFixtures.WideHeader();
        Assert.Equal(480, header.Length + separator.Length);
        var content = ChunkerFixtures.WideHeaderTable;

        var chunks = Chunker.Split(Manual(content), ChunkBound.Fallback);

        Assert.All(chunks, c => Assert.True(ChunkBound.Fallback.Fits(c.EmbeddingText)));
        AssertEveryLineInOneCore(content, chunks);
        Assert.True(chunks.Count(c => c.Text.Contains("| 1") || c.Text.Contains("| 2")) > 1);
    }

    [Fact]
    public void Merging_never_crosses_a_parent()
    {
        var chunks = Chunker.Split(Manual("# A\n## A1\nx\n## A2\ny\n# B\n## B1\nz\n"), ChunkBound.Unbounded);

        Assert.Equal([("#A", 0, 5), ("#B", 5, 8)], chunks.Select(c => (c.Address, c.StartLine, c.EndLine)));
        Assert.DoesNotContain(chunks, c => c.StartLine < 5 && c.EndLine > 5);

        // A leaf after another parent's children stays apart, though both are short.
        Assert.Equal(
            [("#A", 0, 3), ("#B", 3, 5)],
            Chunker.Split(Manual("# A\n## A1\nx\n# B\ny\n"), ChunkBound.Unbounded).Select(c => (c.Address, c.StartLine, c.EndLine)));
        Assert.Equal(
            [("#P", 0, 1), ("#P#Q", 1, 4), ("#P#R", 4, 6)],
            Chunker.Split(Manual("# P\n## Q\n### Q1\nx\n## R\ny\n"), ChunkBound.Unbounded).Select(c => (c.Address, c.StartLine, c.EndLine)));
    }

    [Theory]
    [InlineData("unbounded")]
    [InlineData("fallback")]
    public void Sections_merge_below_their_parent_only(string name)
    {
        List<(string, int, int)> Split(string content) =>
            [.. Chunker.Split(Manual(content), Bound(name)).Select(c => (c.Address, c.StartLine, c.EndLine))];

        // A2a joins A2, but A3, a child of A, doesn't join them; B, under the note, doesn't join A3.
        Assert.Equal(
            [("", 0, 1), ("#A", 1, 5), ("#A#A2", 5, 9), ("#A#A3", 9, 11), ("#B", 11, 13)],
            Split("Intro line.\n# A\nx\n## A1\ny\n## A2\nz\n### A2a\nw\n## A3\nv\n# B\nu\n"));
        Assert.Equal(
            [("#", 0, 4), ("##[2]", 4, 8), ("#-", 8, 12)],
            Split("#\nalpha\n##\nbeta\n##\ngamma\n###\ndelta\n# -\nepsilon\n## -\nzeta\n"));
        Assert.Equal(
            [("", 0, 3), ("#Q", 3, 5), ("#Q#Q1", 5, 11), ("#Q#Q2", 11, 13), ("#R", 13, 15)],
            Split("Lead.\n# P\np\n# Q\nq\n## Q1\nq1\n### Q1a\nqa\n### Q1b\nqb\n## Q2\nq2\n# R\nr\n"));
    }

    [Theory]
    // 65,536 headings: linear, a fraction of a second; a quadratic merging takes over a minute.
    [InlineData("# -\n")]
    [InlineData("#\n")]
    public async Task A_note_of_many_headings_without_words_is_split_within_seconds(string line)
    {
        var ct = TestContext.Current.CancellationToken;
        var content = string.Concat(Enumerable.Repeat(line, 65_536));
        Assert.NotEmpty(await Task.Run(() => Chunker.Split(Manual(content), ChunkBound.Unbounded), ct).WaitAsync(TimeSpan.FromSeconds(5), ct));
    }

    /// <summary>About 1 MiB of ordinary Markdown: prose, a list and a table under many headings.</summary>
    private static string Ordinary()
    {
        var text = new System.Text.StringBuilder("# Manual\n\n");
        for (var s = 0; text.Length < 1_000_000; s++)
        {
            text.Append($"## Section {s}\n\nThe pump P-{s} runs at {s % 90} bar; check the seal every week and log it.\n\n");
            text.Append("- first item\n- second item with more words\n\n| Code | Meaning |\n|------|---------|\n");
            for (var r = 0; r < 20; r++) text.Append($"| E{s:000}{r:00} | Sensor drift {r} |\n");
            text.Append('\n');
        }

        return text.ToString();
    }

    [Theory]
    // 262,144 spaces: linear, a few milliseconds; a backtracking separator row takes half a minute.
    [InlineData("unbounded")]
    [InlineData("fallback")]
    [InlineData("tokenizer")]
    public async Task A_table_row_over_a_long_line_of_spaces_is_split_within_seconds(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var bound = Bound(name);
        var ordinary = Ordinary();
        Assert.NotEmpty(await Task.Run(() => Chunker.Split(Manual(ordinary), bound), ct).WaitAsync(TimeSpan.FromSeconds(10), ct));

        var content = "# T\n" + string.Join(' ', Enumerable.Repeat("word", 400)) + "\na | b\n" + new string(' ', 262_144) + "x\n";
        Assert.NotEmpty(await Task.Run(() => Chunker.Split(Manual(content), bound), ct).WaitAsync(TimeSpan.FromSeconds(10), ct));
    }

    [Fact]
    public async Task The_rolls_of_a_chunk_after_a_long_line_of_spaces_come_within_seconds()
    {
        var ct = TestContext.Current.CancellationToken;
        var ordinary = Manual(Ordinary());
        var some = Chunker.Split(ordinary, ChunkBound.Fallback)[100];
        await Task.Run(() => Chunker.Rolls(ordinary, some), ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Lines: "# T", "a | b", the spaces ending in "x", "after", "more"; the line of spaces is too long to roll in.
        var note = Manual("# T\na | b\n" + new string(' ', 262_144) + "x\nafter\nmore\n");
        var rolls = await Task.Run(() =>
        {
            var after = Chunker.Split(note, ChunkBound.Unbounded).Single() with { StartLine = 3, EndLine = 4, Text = "after" };
            return Chunker.Rolls(note, after);
        }, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(("", "more"), rolls);
    }

    [Theory]
    [InlineData("unbounded")]
    [InlineData("fallback")]
    [InlineData("tokenizer")]
    public void Small_sections_merge_up_to_the_target(string name)
    {
        var bound = Bound(name);
        var content = "# Parent\n" + string.Concat(Enumerable.Range(1, 30).Select(i =>
            $"## Flower {i}\n" + string.Join(' ', Enumerable.Range(0, 19).Select(w => $"petal{w}")) + ".\n"));

        var chunks = Chunker.Split(Manual(content), bound);

        AssertEveryLineInOneCore(content, chunks);
        Assert.InRange(chunks.Count, 2, 29);
        Assert.All(chunks, c => Assert.True(Chunker.Words(c.Text) <= Chunker.TargetWords, $"{Chunker.Words(c.Text)} words"));
        Assert.All(chunks, c => Assert.True(bound.Fits(c.EmbeddingText)));
        Assert.All(chunks.Skip(1), c => Assert.Equal("#Parent", c.Address));
        if (name == "unbounded") Assert.True(chunks.Count <= 3, $"{chunks.Count} chunks");
    }

    [Fact]
    public void A_small_last_section_joins_the_group_before_it()
    {
        var sentence = string.Join(' ', Enumerable.Range(0, 59).Select(w => $"poppy{w}")) + ".";
        var content = "# Parent\n" + string.Concat(Enumerable.Range(1, 4).Select(i => $"## Flower {i}\n{sentence}\n")) + "## Last\nBob.\n";

        var chunks = Chunker.Split(Manual(content), ChunkBound.Unbounded);

        // 1 + 4 × 62 words reach the target; the last section of 2 words would be alone, so it joins.
        Assert.Single(chunks);
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("tokenizer")]
    public void A_long_German_section_splits_between_paragraphs(string name)
    {
        var content = ChunkerFixtures.LongGerman;
        List<string> lines = [.. Lines.Split(content)];

        var chunks = Chunker.Split(Manual(content), Bound(name));

        Assert.True(chunks.Count(c => c.Address == "#Kreiselpumpe P-200#Betriebsanleitung") > 3);
        if (name == "tokenizer")
        {
            Assert.All(chunks, c => Assert.True(c.StartLine == 0 || lines[c.StartLine - 1].Length == 0 || lines[c.StartLine].StartsWith('#'),
                $"chunk at line {c.StartLine} starts inside a paragraph"));
        }

        AssertEveryLineInOneCore(content, chunks);
    }

    [Fact]
    public void The_chunks_of_a_content_are_reused_for_the_same_path_and_bound_only()
    {
        var content = ChunkerFixtures.LongLine;

        var first = Chunker.Split(Manual(content), ChunkBound.Fallback);
        var again = Chunker.Split(Manual(content), ChunkBound.Fallback);
        var elsewhere = Chunker.Split(new Note(P("/Demo/log.md"), content), ChunkBound.Fallback);
        var unbounded = Chunker.Split(Manual(content), ChunkBound.Unbounded);

        Assert.Equal(first, again);
        Assert.NotSame(first, again);
        Assert.All(elsewhere, c => Assert.Equal("/Demo/log.md", c.Path.Value));
        Assert.Equal("log > Log", elsewhere[0].Context);
        Assert.NotEqual(first.Count, unbounded.Count);
        Assert.Equal(first, Chunker.Split(Manual(content), ChunkBound.Fallback));
    }

    [Fact]
    public void A_paragraph_too_long_for_a_part_starts_in_the_open_one()
    {
        var content = "# Meadow\nPoppies.\n\n" + string.Join('\n', Enumerable.Range(0, 400).Select(i => $"cowslip{i}")) + "\n";

        var chunks = Chunker.Split(Manual(content), ChunkBound.Unbounded);

        Assert.Equal([(0, 351), (351, 403)], chunks.Select(c => (c.StartLine, c.EndLine)));
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("tokenizer")]
    public void Fenced_code_splits_between_lines_only(string name)
    {
        var content = ChunkerFixtures.FencedCode;
        List<string> lines = [.. Lines.Split(content)];
        var shortFence = lines.IndexOf("int idle(void) { return 0; }") - 1;

        var chunks = Chunker.Split(Manual(content), Bound(name));

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, c => Assert.Equal(0, c.Part));
        Assert.All(chunks, c => Assert.Equal(lines.Skip(c.StartLine).Take(c.EndLine - c.StartLine), c.Text.Split('\n')));
        Assert.Single(chunks, c => c.StartLine <= shortFence && shortFence + 3 <= c.EndLine);
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("unbounded")]
    [InlineData("tokenizer")]
    public void A_line_of_10000_characters_is_split_at_spaces(string name)
    {
        var bound = Bound(name);
        var chunks = Chunker.Split(Manual(ChunkerFixtures.LongLine), bound);

        var pieces = chunks.Where(c => c.StartLine == 1).ToList();
        Assert.True(pieces.Count > 2, $"{pieces.Count} pieces");
        Assert.Equal(Enumerable.Range(0, pieces.Count), pieces.Select(c => c.Part));
        Assert.Equal(chunks.Count, chunks.Select(c => c.Key).Distinct().Count());
        Assert.All(chunks, c => Assert.True(bound.Fits(c.EmbeddingText)));
        AssertEveryLineInOneCore(ChunkerFixtures.LongLine, chunks);
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("tokenizer")]
    public void A_run_without_spaces_is_split_within_the_budget(string name)
    {
        var bound = Bound(name);
        var chunks = Chunker.Split(Manual(ChunkerFixtures.NoSpaces), bound);

        var pieces = chunks.Where(c => c.StartLine == 1).ToList();
        Assert.True(pieces.Count > 2, $"{pieces.Count} pieces");
        Assert.Equal(Enumerable.Range(0, pieces.Count), pieces.Select(c => c.Part));
        Assert.Equal(chunks.Count, chunks.Select(c => c.Key).Distinct().Count());
        Assert.All(chunks, c => Assert.True(bound.Fits(c.EmbeddingText)));
        if (name == "fallback")
        {
            // Each piece but the last is as long as the budget allows: one more character wouldn't fit.
            var line = Lines.Split(ChunkerFixtures.NoSpaces)[1];
            var offset = 0;
            foreach (var piece in pieces.SkipLast(1))
            {
                offset += piece.Text.Length;
                Assert.False(bound.Fits(piece.EmbeddingText + line[offset]));
            }
        }

        AssertEveryLineInOneCore(ChunkerFixtures.NoSpaces, chunks);
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("unbounded")]
    [InlineData("tokenizer")]
    public void A_long_heading_path_leaves_room_for_the_core(string name)
    {
        var bound = Bound(name);
        var chunks = Chunker.Split(Manual(ChunkerFixtures.DeepHeadings), bound);

        Assert.All(chunks, c => Assert.True(bound.Fits(c.EmbeddingText)));
        Assert.All(chunks, c => Assert.StartsWith("Manual > ", c.Context));
        if (name != "unbounded") Assert.All(chunks, c => Assert.True(bound.Cost(c.Context) <= bound.Budget / 4, $"context costs {bound.Cost(c.Context)}"));
        AssertEveryLineInOneCore(ChunkerFixtures.DeepHeadings, chunks);
    }

    [Fact]
    public void The_text_before_the_first_heading_is_a_chunk()
    {
        var content = "---\ntags: flora\n---\nNotes from walks.\n\n# Meadow\n## Poppy\nRed, in June.\n";

        var chunks = Chunker.Split(new Note(P("/Demo/walks.md"), content), ChunkBound.Fallback);

        Assert.Equal(
            [("", 3, 4, "Notes from walks.", "walks"), ("#Meadow", 5, 8, "# Meadow\n## Poppy\nRed, in June.", "walks > Meadow")],
            chunks.Select(c => (c.Address, c.StartLine, c.EndLine, c.Text, c.Context)));
    }

    [Fact]
    public void ChunksAreSectionsWithTheirHeadingPath()
    {
        var chunks = Chunker.Split(new Note(P("/Demo/plan.md"), "---\ntags: x\n---\nIntro text.\n\n# Plan\nTop.\n## Goals\nShip it.\n\n## Risks\n\n"), ChunkBound.Fallback);

        // Under the new rules the short sections below Plan merge into one chunk with Plan's address; Plan has
        // children, so it starts a group of its own and the text before it stays alone.
        Assert.Equal(
            [("", 3, 4, "Intro text.", "plan"), ("#Plan", 5, 11, "# Plan\nTop.\n## Goals\nShip it.\n\n## Risks", "plan > Plan")],
            chunks.Select(c => (c.Address, c.StartLine, c.EndLine, c.Text, c.Context)));
        Assert.Equal("plan > Plan\n# Plan\nTop.\n## Goals\nShip it.\n\n## Risks", chunks[1].EmbeddingText);
    }

    [Fact]
    public void Siblings_at_the_top_merge_under_the_note()
    {
        var chunks = Chunker.Split(new Note(P("/Demo/gate.md"), "# Gate\nThe service checks rules at the entrance.\n# Team\nAlice and Bob.\n"), ChunkBound.Fallback);

        Assert.Equal([("", 0, 4, "gate")], chunks.Select(c => (c.Address, c.StartLine, c.EndLine, c.Context)));
    }

    [Fact]
    public void Sections_is_the_old_split()
    {
        var chunks = Chunker.Sections(new Note(P("/Demo/plan.md"), "---\ntags: x\n---\nIntro text.\n\n# Plan\nTop.\n## Goals\nShip it.\n\n## Risks\n\n"));

        Assert.Equal(
            [("", 3, 4, "Intro text.", "plan"), ("#Plan", 5, 7, "# Plan\nTop.", "plan > Plan"), ("#Plan#Goals", 7, 9, "## Goals\nShip it.", "plan > Plan > Goals"), ("#Plan#Risks", 10, 11, "## Risks", "plan > Plan > Risks")],
            chunks.Select(c => (c.Address, c.StartLine, c.EndLine, c.Text, c.Context)));
        Assert.Equal("plan > Plan > Goals\n## Goals\nShip it.", chunks[2].EmbeddingText);

        var paragraph = string.Join(' ', Enumerable.Repeat("word", 150));
        var content = "# Long\n" + string.Join("\n\n", Enumerable.Range(0, 6).Select(_ => paragraph)) + "\n";
        var parts = Chunker.Sections(new Note(P("/Demo/long.md"), content));

        Assert.True(parts.Count >= 3);
        Assert.All(parts, c => Assert.True(c.Text.Length <= Chunker.MaxChars));
        Assert.All(parts, c => Assert.Equal("#Long", c.Address));
        Assert.All(parts.Skip(1), c => Assert.False(string.IsNullOrWhiteSpace(c.Text.Split('\n')[0])));
    }

    [Fact]
    public void Blocks_follow_the_markdown_structure()
    {
        List<string> lines = [.. Lines.Split("# Pump\nIntro line.\n| a | b |\n|---|:-:|\n| 1 | 2 |\n- one\n  more\n- two\n> quoted\n\n```\nx\n\ny\n```\nAfter.\n")];

        var blocks = Blocks.Parse(lines, 0, lines.Count, Outline.CodeLines(lines));

        Assert.Equal(
            [(BlockKind.Heading, 0, 1), (BlockKind.Paragraph, 1, 2), (BlockKind.Table, 2, 5), (BlockKind.List, 5, 8), (BlockKind.Quote, 8, 9), (BlockKind.Code, 10, 15), (BlockKind.Paragraph, 15, 16)],
            blocks.Select(b => (b.Kind, b.Start, b.End)));

        // "a | b" over "---" is a setext heading, not a table: the cells differ. A table without outer pipes ends at a
        // list item or a quote, though they hold a pipe.
        List<string> more = [.. Lines.Split("a | b\n---\nSetext.\n\nc | d\n--- | ---\n1 | 2\n- item | with pipe\n> quote | pipe\n")];
        Assert.Equal(
            [(BlockKind.Paragraph, 0, 3), (BlockKind.Table, 4, 7), (BlockKind.List, 7, 8), (BlockKind.Quote, 8, 9)],
            Blocks.Parse(more, 0, more.Count, Outline.CodeLines(more)).Select(b => (b.Kind, b.Start, b.End)));
    }

    [Fact]
    public void A_wide_row_goes_without_the_header_and_the_rows_after_it_have_it_again()
    {
        var rows = Enumerable.Range(1, 40).Select(i => i == 12 ? $"| E{i:000} | {string.Join(' ', Enumerable.Repeat("drift", 69))} | Recalibrate |" : $"| E{i:000} | Dry run at stage {i} | Fill the inlet |");
        var content = "# Pump\n## Error codes\n| Code | Meaning | Remedy |\n|------|---------|--------|\n" + string.Join('\n', rows) + "\n";
        List<string> lines = [.. Lines.Split(content)];
        var wide = lines.FindIndex(l => l.StartsWith("| E012"));
        // Too wide to share a part with the header rows, not too wide for a part of its own.
        Assert.True(lines[wide].Length > 430);

        var chunks = Chunker.Split(Manual(content), ChunkBound.Fallback);
        var parts = chunks.Where(c => c.Text.Contains("| E0")).ToList();

        AssertEveryLineInOneCore(content, chunks);
        Assert.All(parts, c => Assert.True(ChunkBound.Fallback.Fits(c.EmbeddingText)));
        var widePart = Assert.Single(parts, c => c.StartLine <= wide && wide < c.EndLine);
        Assert.Equal((wide, wide + 1), (widePart.StartLine, widePart.EndLine));
        Assert.All(parts.Skip(1).Where(c => c != widePart), c => Assert.StartsWith(lines[2] + "\n" + lines[3] + "\n", c.Text));
    }

    [Fact]
    public void Header_rows_that_fit_with_no_row_end_the_part_before()
    {
        var header = "| " + string.Join(" | ", Enumerable.Range(1, 5).Select(i => $"Column name number {i}".PadRight(26))) + " |";
        var separator = "|" + string.Join("|", Enumerable.Range(1, 5).Select(_ => new string('-', 28))) + "|";
        var rows = Enumerable.Range(1, 6).Select(i => "| " + string.Join(" | ", Enumerable.Range(1, 5).Select(c => $"value {i}.{c} of the pump".PadRight(44))) + " |");
        var content = "# T\n## Readings\nShort intro.\n\n" + header + "\n" + separator + "\n" + string.Join('\n', rows) + "\n";

        var chunks = Chunker.Split(Manual(content), ChunkBound.Fallback);

        AssertEveryLineInOneCore(content, chunks);
        Assert.All(chunks, c => Assert.True(ChunkBound.Fallback.Fits(c.EmbeddingText)));
        // No part holds the header rows alone: they go with the intro, as no row fits with them.
        Assert.DoesNotContain(chunks, c => c.Text.Split('\n').All(l => l == header || l == separator));
        Assert.Contains(chunks, c => c.Text.StartsWith("## Readings\nShort intro.\n\n" + header + "\n" + separator, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unbounded")]
    [InlineData("fallback")]
    [InlineData("tokenizer")]
    public void A_run_without_spaces_holds_at_most_the_maximum_of_words(string name)
    {
        var bound = Bound(name);
        var run = string.Join(',', Enumerable.Range(0, 3_000).Select(i => $"w{i % 10}"));
        var content = "# Dump\n" + run + "\n";

        var chunks = Chunker.Split(Manual(content), bound);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(Chunker.Words(c.Text) <= Chunker.MaxWords, $"{Chunker.Words(c.Text)} words"));
        Assert.All(chunks, c => Assert.True(bound.Fits(c.EmbeddingText)));
        AssertEveryLineInOneCore(content, chunks);
    }

    /// <summary>Long runs without spaces, each a shape that once made splitting a line quadratic.</summary>
    private static string Run(string shape) => shape switch
    {
        "hex" => string.Concat(Enumerable.Range(0, 1_000_000 / 8).Select(i => i.ToString("x8"))),
        "symbols" => string.Concat(Enumerable.Repeat("→∑≈€", 262_144 / 4)),
        // Unknown characters between zero-width spaces, which normalise to spaces: a word of its own each.
        "hieroglyphs" => string.Concat(Enumerable.Range(0, 1_000_000 / 3).Select(i => char.ConvertFromUtf32(0x13000 + i % 1000) + "​")),
        // "‗" normalises to a space and a combining mark.
        "low lines" => new string('‗', 262_144),
        // One grapheme cluster each, a million characters long.
        "combining marks" => "a" + new string('́', 1_000_000),
        "jamo" => new string('ᄀ', 1_000_000),
        "emoji" => string.Concat(Enumerable.Repeat("👩‍", 1_000_000 / 3)),
        // Many words without a space between them, which only the word limit cuts.
        "commas" => string.Concat(Enumerable.Repeat("a,", 2_000_000)),
        _ => throw new ArgumentException(shape),
    };

    public static TheoryData<string, string> EveryRunAndBound() =>
        new([.. new[] { "hex", "symbols", "hieroglyphs", "low lines", "combining marks", "jamo", "emoji", "commas" }
            .SelectMany(r => new[] { "fallback", "unbounded", "tokenizer" }.Select(b => (r, b)))]);

    // Seconds at most on a busy machine; counting the whole rest of the line for every piece took minutes.
    [Theory]
    [MemberData(nameof(EveryRunAndBound))]
    public async Task A_long_line_without_spaces_is_split_within_seconds(string run, string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var bound = Bound(name);
        var content = "# T\n" + Run(run) + "\n";

        var chunks = await Task.Run(() => Chunker.Split(Manual(content), bound), ct).WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.All(chunks, c => Assert.True(bound.Fits(c.EmbeddingText)));
        AssertEveryLineInOneCore(content, chunks);
    }

    [Theory]
    [MemberData(nameof(EveryRunAndBound))]
    public async Task A_page_with_a_long_run_without_spaces_is_split_within_seconds(string run, string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var bound = Bound(name);
        var text = Run(run);

        var chunks = await Task.Run(() => Chunker.SplitPage("Manual", 1, text, bound), ct).WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.Equal(text, string.Concat(chunks.Select(c => c.Core)));
        Assert.All(chunks, c => Assert.True(bound.Fits(c.EmbeddingText)));
    }

    [Fact]
    public async Task Splitting_a_long_run_without_spaces_memoises_no_long_words()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipUnless(TestTokenizer.Load() is not null, TestTokenizer.SkipReason);
        // A tokenizer of its own: the memo of the shared one holds what other tests counted.
        UnigramTokenizer tokenizer;
        using (var stream = File.OpenRead(Environment.GetEnvironmentVariable("KUMBU_TOKENIZER")!)) tokenizer = UnigramTokenizer.Load(stream);
        var text = Run("hex")[..262_144];

        await Task.Run(() => Chunker.SplitPage("Manual", 1, text, ChunkBound.Of(tokenizer, TestTokenizer.Sha256)), ct).WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.InRange(tokenizer.LongestMemoisedWord, 1, UnigramTokenizer.LongestMemoisedLength);
    }

    // ---- Pages of PDFs ----

    private static string Collapsed(string text) => Spaces().Replace(text, " ").Trim();

    [Theory]
    [InlineData("fallback", true)]
    [InlineData("fallback", false)]
    [InlineData("tokenizer", true)]
    [InlineData("tokenizer", false)]
    public void A_dense_page_yields_several_chunks_covering_its_text(string name, bool lineBreaks)
    {
        var bound = Bound(name);
        var page = lineBreaks ? ChunkerFixtures.DensePage : Collapsed(ChunkerFixtures.DensePage);
        Assert.True(page.Length >= 9_000);

        var chunks = Chunker.SplitPage("Manual", 4, page, bound);

        Assert.True(chunks.Count >= 3, $"{chunks.Count} chunks");
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Part));
        Assert.Equal(Collapsed(page), Collapsed(string.Join(' ', chunks.Select(c => c.Core))));
        Assert.All(chunks, c => Assert.StartsWith("Manual > p4\n", c.EmbeddingText));
        Assert.All(chunks, c => Assert.True(bound.Fits(c.EmbeddingText), $"{c.EmbeddingText.Length} characters"));
        Assert.All(chunks, c => Assert.True(Chunker.Words(c.Core) <= Chunker.MaxWords));
        Assert.All(chunks, c => Assert.Equal(c.Core.Trim('\n'), c.Core));
    }

    [Fact]
    public void A_dense_page_is_cut_at_paragraphs_where_it_can()
    {
        var chunks = Chunker.SplitPage("Manual", 1, ChunkerFixtures.DensePage, ChunkBound.Unbounded);
        var paragraphs = ChunkerFixtures.DensePage.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToHashSet();

        Assert.True(chunks.Count >= 3, $"{chunks.Count} chunks");
        Assert.All(chunks, c => Assert.All(c.Core.Split("\n\n"), p => Assert.Contains(p, paragraphs)));
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("unbounded")]
    [InlineData("tokenizer")]
    public void Rolls_stay_within_the_page(string name)
    {
        var page = ChunkerFixtures.DensePage;
        var chunks = Chunker.SplitPage("Manual", 4, page, Bound(name));

        Assert.True(chunks.Count >= 3, $"{chunks.Count} chunks");
        Assert.Equal("", chunks[0].PreRoll);
        Assert.Equal("", chunks[^1].PostRoll);
        foreach (var chunk in chunks.Skip(1).SkipLast(1))
        {
            Assert.NotEqual("", chunk.PreRoll);
            Assert.NotEqual("", chunk.PostRoll);
            foreach (var roll in new[] { chunk.PreRoll, chunk.PostRoll })
            {
                Assert.True(Chunker.Words(roll) <= Chunker.RollWords, $"{Chunker.Words(roll)} words");
                Assert.True(roll.Length <= Chunker.RollChars, $"{roll.Length} characters");
                Assert.Equal(roll.Trim('\n'), roll);
            }

            // The rolls are the lines right before and after the core, none of its own: the lines are numbered, so
            // a line in both would be there twice.
            Assert.Contains(Collapsed(chunk.PreRoll + "\n" + chunk.Core + "\n" + chunk.PostRoll), Collapsed(page));
        }

        // The rolls hold whole lines.
        var lines = Lines.Split(page).ToHashSet();
        Assert.All(chunks, c => Assert.All(c.PreRoll.Split('\n').Concat(c.PostRoll.Split('\n')).Where(l => l.Length > 0), l => Assert.Contains(l, lines)));
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("unbounded")]
    [InlineData("tokenizer")]
    public void A_short_page_is_one_chunk(string name)
    {
        var chunks = Chunker.SplitPage("Report", 2, "\n  \nFindings\nzebras migrate across the savannah\n\n", Bound(name));

        var chunk = Assert.Single(chunks);
        Assert.Equal(new PageChunk(0, "Findings\nzebras migrate across the savannah", "", "") { Context = "Report > p2" }, chunk);
        Assert.Equal("Report > p2\nFindings\nzebras migrate across the savannah", chunk.EmbeddingText);
        Assert.Empty(Chunker.SplitPage("Report", 3, " \n\t\n", Bound(name)));
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("tokenizer")]
    public void A_long_PDF_name_is_shortened_and_leaves_room_for_the_page(string name)
    {
        var bound = Bound(name);
        var title = string.Concat(Enumerable.Range(0, 60).Select(i => $"Bericht{i:D2} "))[..600];

        var chunks = Chunker.SplitPage(title, 7, ChunkerFixtures.DensePage, bound);

        Assert.True(chunks.Count >= 3, $"{chunks.Count} chunks");
        Assert.All(chunks, c => Assert.True(bound.Fits(c.EmbeddingText), $"{c.EmbeddingText.Length} characters"));
        Assert.All(chunks, c => Assert.True(bound.Cost(c.Context) <= bound.Budget / 4, $"context costs {bound.Cost(c.Context)}"));
        Assert.All(chunks, c => Assert.StartsWith("Bericht00 ", c.Context));
        Assert.Equal(Collapsed(ChunkerFixtures.DensePage), Collapsed(string.Join(' ', chunks.Select(c => c.Core))));
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("unbounded")]
    [InlineData("tokenizer")]
    public void The_pieces_of_a_long_line_have_the_lines_around_it_as_rolls(string name)
    {
        var bound = Bound(name);
        var line = string.Join(' ', Enumerable.Range(0, 1_200).Select(i => $"Messwert{i}"));
        var page = "Kopf eins\nKopf zwei\n" + line + "\nFuß eins\nFuß zwei\n";

        var chunks = Chunker.SplitPage("Manual", 1, page, bound);

        var pieces = chunks.Where(c => c.Core.StartsWith("Messwert", StringComparison.Ordinal)).ToList();
        Assert.True(pieces.Count >= 3, $"{pieces.Count} pieces");
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Part));
        Assert.Equal(Collapsed(page), Collapsed(string.Join(' ', chunks.Select(c => c.Core))));
        Assert.All(chunks, c => Assert.True(bound.Fits(c.EmbeddingText)));
        Assert.Equal("Kopf eins\nKopf zwei", chunks[0].Core);
        Assert.Equal("Fuß eins\nFuß zwei", chunks[^1].Core);
        Assert.All(pieces, p => Assert.Equal(("Kopf eins\nKopf zwei", "Fuß eins\nFuß zwei"), (p.PreRoll, p.PostRoll)));
    }

    [Fact]
    public void Rolls_are_the_rest_of_the_enclosing_section()
    {
        // Safety and Notes merge under Pump; the section around Pump is Manual's, up to the Appendix.
        var intro = string.Join(' ', Enumerable.Range(0, 45).Select(i => $"intro{i}"));
        var safety = string.Join(' ', Enumerable.Range(0, 300).Select(i => $"safety{i}"));
        var note = Manual($"# Manual\n## Pump\n{intro}\n\n### Safety\n{safety}\n### Notes\nKeep the manual dry.\n\n## Other\nUnrelated.\n# Appendix\nNot in the roll.\n");

        var chunk = Assert.Single(Chunker.Split(note, ChunkBound.Unbounded), c => c.Text.Contains("safety0"));
        var (before, after) = Chunker.Rolls(note, chunk);

        Assert.Equal(("#Manual#Pump", "### Safety"), (chunk.Address, chunk.Text.Split('\n')[0]));
        Assert.Equal($"# Manual\n## Pump\n{intro}", before);
        Assert.Equal("## Other\nUnrelated.", after);

        // At the top, the enclosing section is the note.
        var team = string.Join(' ', Enumerable.Range(0, 50).Select(i => $"team{i}"));
        var top = Manual($"{intro}\n# Gate\n{safety}\n# Team\n{team}\n");
        var gate = Assert.Single(Chunker.Split(top, ChunkBound.Unbounded), c => c.Address == "#Gate");
        Assert.Equal((intro, "# Team\n" + team), Chunker.Rolls(top, gate));
    }

    [Fact]
    public void Rolls_find_a_heading_that_shares_its_text_with_a_sibling()
    {
        // The second Day is #Log#Day[2]: its rolls are the rest of Log, not of the note.
        string Words(string word, int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{word}{i}"));
        var note = Manual(
            $"# Intro\nNot in the roll.\n# Log\n## Day\n{Words("first", 45)}\n## Day\n{Words("second", 300)}\n## End\n{Words("end", 50)}\n# Next\nNot either.\n");

        var day = Assert.Single(Chunker.Split(note, ChunkBound.Unbounded), c => c.Text.Contains("second0"));
        var (before, after) = Chunker.Rolls(note, day);

        Assert.Equal("#Log#Day[2]", day.Address);
        Assert.Equal($"# Log\n## Day\n{Words("first", 45)}", before);
        Assert.Equal($"## End\n{Words("end", 50)}", after);
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("tokenizer")]
    public void Rolls_of_a_long_table_part_are_neighbouring_rows(string name)
    {
        var note = Manual(ChunkerFixtures.PumpManual);
        List<string> lines = [.. Lines.Split(note.Content)];
        var parts = Chunker.Split(note, Bound(name)).Where(c => c.Text.Contains("| E")).ToList();

        var (before, after) = Chunker.Rolls(note, parts[1]);

        Assert.Equal(lines[parts[0].EndLine - 1], before.Split('\n')[^1]);
        Assert.Equal(lines[parts[2].StartLine], after.Split('\n')[0]);
        Assert.All(new[] { before, after }, roll =>
        {
            Assert.InRange(Chunker.Words(roll), 1, Chunker.RollWords);
            Assert.InRange(roll.Length, 1, Chunker.RollChars);
        });

        // A short table's second part: its roll is the rest of the note, without the header rows it repeats.
        var shortNote = Manual("# Codes\n" + ChunkerFixtures.ErrorCodes(9));
        var second = Chunker.Split(shortNote, ChunkBound.Fallback)[1];
        Assert.StartsWith("| Code | Meaning | Remedy |\n|------|", second.Text);
        var shortLines = Lines.Split(shortNote.Content);
        Assert.Equal(string.Join('\n', shortLines.Take(second.StartLine).Where((_, i) => i is not (1 or 2))), Chunker.Rolls(shortNote, second).Before);
    }

    [Theory]
    [MemberData(nameof(EveryFixtureAndBound))]
    public void Rolls_are_never_embedded(string fixture, string name)
    {
        var note = Manual(ChunkerFixtures.All[fixture]);
        List<string> lines = [.. Lines.Split(note.Content)];

        foreach (var chunk in Chunker.Split(note, Bound(name)))
        {
            var (before, after) = Chunker.Rolls(note, chunk);
            List<string> embedded = [.. Lines.Split(chunk.EmbeddingText)];
            List<string> core = [.. Lines.Split(chunk.Text)];
            foreach (var line in Lines.Split(before).Concat(Lines.Split(after)).Where(l => l.Trim().Length > 0))
            {
                Assert.True(!embedded.Contains(line) || core.Contains(line), $"{line} is embedded");
            }

            // The rolls are the lines right before and after the core's own, never the core's.
            var header = chunk.RepeatedHeader;
            var above = lines.Take(chunk.StartLine).Where((_, i) => header < 0 || (i != header && i != header + 1)).Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse();
            Assert.EndsWith(before, string.Join('\n', above));
            Assert.StartsWith(after, string.Join('\n', lines.Skip(chunk.EndLine).SkipWhile(string.IsNullOrWhiteSpace)));
            Assert.All(new[] { before, after }, roll => Assert.True(Chunker.Words(roll) <= Chunker.RollWords && roll.Length <= Chunker.RollChars, roll));
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
