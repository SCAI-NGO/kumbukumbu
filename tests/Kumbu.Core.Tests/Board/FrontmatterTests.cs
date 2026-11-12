// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;

namespace Kumbu.Core.Tests.Board;

public class FrontmatterTests
{
    private static IReadOnlyList<(string Key, string? Yaml)> Changes(params (string, string?)[] changes) => changes;

    [Fact]
    public void Reads_scalars_lists_and_maps()
    {
        var read = Frontmatter.Read("---\ncolumn: Doing\ndue: 2026-10-29\ntags:\n  - a\n  - b\nmoved:\n  Doing: 2026-10-27\nempty:\n---\nbody\n");

        Assert.Null(read.Problem);
        Assert.Equal("Doing", read.Values["column"]);
        Assert.Equal("2026-10-29", read.Values["due"]);
        Assert.Equal(new object?[] { "a", "b" }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(read.Values["tags"]));
        var moved = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(read.Values["moved"]);
        Assert.Equal("2026-10-27", moved["Doing"]);
        Assert.Null(read.Values["empty"]);
    }

    [Fact]
    public void A_duplicate_top_level_key_is_a_problem()
    {
        var read = Frontmatter.Read("---\ncolumns: [A]\ncolumns: [B]\n---\n");

        Assert.NotNull(read.Problem);
        Assert.Empty(read.Values);
    }

    [Fact]
    public void No_frontmatter_reads_as_empty()
    {
        var read = Frontmatter.Read("# Title\n\ncolumn: Doing\n");

        Assert.Null(read.Problem);
        Assert.Empty(read.Values);
    }

    [Fact]
    public void Malformed_yaml_is_a_problem()
    {
        var read = Frontmatter.Read("---\ncolumn: [\n---\n");

        Assert.NotNull(read.Problem);
        Assert.Empty(read.Values);
    }

    [Fact]
    public void Set_replaces_only_its_keys_byte_for_byte()
    {
        const string input = "---\n# mine\naliases:\n  - x\n    \n  - y\ncolumn: Backlog\nodd:    spaced   \n---\nbody\n";

        var output = Frontmatter.Set(input, Changes(("column", "Doing")));

        Assert.Equal(input.Replace("column: Backlog", "column: Doing"), output);
    }

    [Fact]
    public void Set_replaces_a_block_value_as_a_whole()
    {
        var output = Frontmatter.Set("---\ntitle: T\ntags:\n  - a\n  - b\nrank: m\n---\nbody\n", Changes(("tags", "[c]")));

        Assert.Equal("---\ntitle: T\ntags: [c]\nrank: m\n---\nbody\n", output);
    }

    [Fact]
    public void Set_replaces_a_block_list_written_at_the_keys_indent()
    {
        var output = Frontmatter.Set("---\ntags:\n- a\n- b\nrank: m\n---\n", Changes(("tags", "[c]")));

        Assert.Equal("---\ntags: [c]\nrank: m\n---\n", output);
    }

    [Fact]
    public void Set_adds_missing_keys_at_the_end_of_the_block()
    {
        var output = Frontmatter.Set("---\ntitle: T\n---\nbody\n", Changes(("column", "Doing"), ("rank", "m")));

        Assert.Equal("---\ntitle: T\ncolumn: Doing\nrank: m\n---\nbody\n", output);
    }

    [Fact]
    public void Set_null_removes_a_key()
    {
        var output = Frontmatter.Set("---\ntitle: T\nmoved:\n  Doing: 2026-10-27\ndue: 2026-10-29\n---\nbody\n", Changes(("moved", null), ("absent", null)));

        Assert.Equal("---\ntitle: T\ndue: 2026-10-29\n---\nbody\n", output);
    }

    [Fact]
    public void Set_creates_a_block_on_a_note_without_one() =>
        Assert.Equal("---\ncolumn: Doing\n---\ntext\n", Frontmatter.Set("text\n", Changes(("column", "Doing"))));

    [Fact]
    public void Set_leaves_a_note_without_a_block_alone_when_only_removing() =>
        Assert.Equal("text\n", Frontmatter.Set("text\n", Changes(("column", null))));

    [Fact]
    public void Set_leaves_the_body_untouched()
    {
        const string body = "column: Backlog\n---\n\n# H\n  - x\n";

        var output = Frontmatter.Set("---\ncolumn: Backlog\n---\n" + body, Changes(("column", "Done")));

        Assert.Equal("---\ncolumn: Done\n---\n" + body, output);
    }

    [Fact]
    public void Set_replaces_a_block_list_with_a_blank_line_inside_it()
    {
        var output = Frontmatter.Set("---\ntags:\n  - a\n\n  - b\n\nrank: m\n---\nbody\n", Changes(("tags", "[x]")));

        Assert.Equal("---\ntags: [x]\n\nrank: m\n---\nbody\n", output);
        Assert.Null(Frontmatter.Read(output).Problem);
    }

    [Theory]
    [InlineData("\"column\": Ideas")]
    [InlineData("'column' : Ideas")]
    public void Set_replaces_a_quoted_key(string line)
    {
        var output = Frontmatter.Set($"---\ntitle: T\n{line}\n---\n", Changes(("column", "Doing")));

        Assert.Equal("---\ntitle: T\ncolumn: Doing\n---\n", output);
    }

    [Fact]
    public void A_block_value_leaves_no_trailing_space_after_its_key()
    {
        Assert.Equal("---\na: 1\nm:\n  k: \"v\"\n---\n", Frontmatter.Set("---\na: 1\n---\n", Changes(("m", "\n  k: \"v\""))));
        Assert.Equal("---\nm:\n  k: \"v\"\n---\nbody", Frontmatter.Set("body", Changes(("m", "\n  k: \"v\""))));
    }

    [Fact]
    public void A_self_referencing_anchor_is_a_problem_not_a_crash()
    {
        var read = Frontmatter.Read("---\na: &a [*a]\naliases: [x]\n---\nText\n");
        Assert.Empty(read.Values);
        Assert.NotNull(read.Problem);
    }

    [Fact]
    public void Anchor_expansion_is_a_problem_and_fast()
    {
        var yaml = "a: &a [x,x,x,x,x,x,x,x,x,x]\n";
        var previous = "a";
        for (var i = 0; i < 8; i++)
        {
            yaml += $"b{i}: &b{i} [{string.Join(",", Enumerable.Repeat("*" + previous, 10))}]\n";
            previous = $"b{i}";
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var read = Frontmatter.Read($"---\n{yaml}aliases: [x]\n---\nText\n");
        Assert.Empty(read.Values);
        Assert.NotNull(read.Problem);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_chain_of_anchors_deeper_than_the_limit_is_a_problem()
    {
        // 100 levels, about 5,000 values: under the value budget, over the depth limit.
        var yaml = "a0: &a0 [x]\n" + string.Concat(Enumerable.Range(1, 99).Select(i => $"a{i}: &a{i} [*a{i - 1}]\n"));
        var read = Frontmatter.Read($"---\n{yaml}---\nText\n");
        Assert.Empty(read.Values);
        Assert.NotNull(read.Problem);
    }

    [Theory]
    [InlineData("[a]: 1\n[b]: 2")]
    [InlineData("{a: 1}: x\n{b: 2}: y")]
    public void Keys_that_read_alike_are_a_problem_not_an_exception(string yaml)
    {
        var read = Frontmatter.Read($"---\n{yaml}\n---\nText\n");
        Assert.Empty(read.Values);
        Assert.NotNull(read.Problem);
    }

    [Fact]
    public void A_shared_anchor_used_a_few_times_still_reads()
    {
        var read = Frontmatter.Read("---\nbase: &b [x, y]\ncopy: *b\n---\n");
        Assert.Null(read.Problem);
        Assert.Equal(new List<object?> { "x", "y" }, read.Values["copy"]);
    }
}
