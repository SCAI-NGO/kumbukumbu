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

    [Fact]
    public void A_long_table_is_split_between_rows_with_its_header()
    {
        var bound = Bound("tokenizer");
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
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
