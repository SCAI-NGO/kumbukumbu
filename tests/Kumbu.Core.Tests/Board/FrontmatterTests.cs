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
}
