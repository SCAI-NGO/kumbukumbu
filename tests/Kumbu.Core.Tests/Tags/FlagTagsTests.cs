// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Tests.Tags;

public class FlagTagsTests
{
    // The brief's four notes, each with what setting the red flag makes of it.
    [Theory]
    [InlineData("# Plan\nSome text\n", "---\ntags: [flag/red]\n---\n# Plan\nSome text\n")]
    [InlineData("---\ntags: [a, b]\n---\nText\n", "---\ntags: [a, b, flag/red]\n---\nText\n")]
    [InlineData("---\ntags:\n  - a\n---\nText\n", "---\ntags:\n  - a\n  - flag/red\n---\nText\n")]
    [InlineData("---\ntitle: Plan\ntags: [a]\nstatus: open\n---\nText\n", "---\ntitle: Plan\ntags: [a, flag/red]\nstatus: open\n---\nText\n")]
    public void Set_changes_only_the_tags_line_or_item(string note, string expected) => Assert.Equal(expected, FlagTags.Set(note, "red"));

    [Theory]
    [InlineData("# Plan\nSome text\n")]
    [InlineData("# Plan\nSome text")]
    [InlineData("")]
    [InlineData("---\ntags: [a, b]\n---\nText\n")]
    [InlineData("---\ntags:\n  - a\n---\nText\n")]
    [InlineData("---\ntitle: Plan\ntags: [a]\nstatus: open\n---\nText\n")]
    [InlineData("---\ntitle: Plan\n---\nText\n")]
    [InlineData("---\ntags: [ a ] # mine\n---\n")]
    [InlineData("---\n\"tags\":\n- a\n- 'b'\n---\n")]
    public void Set_then_remove_gives_the_note_back_byte_for_byte(string note)
    {
        var set = FlagTags.Set(note, "red");
        Assert.NotNull(set);
        Assert.NotEqual(note, set);
        Assert.Equal(note, FlagTags.Set(set, null));
    }

    [Fact]
    public void Crlf_is_kept()
    {
        Assert.Equal("---\r\ntags: [flag/red]\r\n---\r\nText\r\n", FlagTags.Set("Text\r\n", "red"));
        Assert.Equal("---\r\ntags:\r\n  - a\r\n  - flag/red\r\n---\r\nText\r\n", FlagTags.Set("---\r\ntags:\r\n  - a\r\n---\r\nText\r\n", "red"));
        Assert.Equal("---\r\ntitle: x\r\ntags: [flag/red]\r\n---\r\n", FlagTags.Set("---\r\ntitle: x\r\n---\r\n", "red"));
        Assert.Equal("---\r\ntags: [a, flag/red]\r\n---\r\n", FlagTags.Set("---\r\ntags: [a, flag/blue]\r\n---\r\n", "red"));
        foreach (var note in new[] { "Text\r\n", "---\r\ntags:\r\n  - a\r\n---\r\nText\r\n", "---\r\ntitle: x\r\n---\r\n" })
        {
            Assert.Equal(note, FlagTags.Set(FlagTags.Set(note, "red")!, null));
        }
    }

    [Fact]
    public void Quoted_items_keep_their_quotes()
    {
        Assert.Equal("---\ntags: [\"a\", 'b', flag/red]\n---\n", FlagTags.Set("---\ntags: [\"a\", 'b']\n---\n", "red"));
        Assert.Equal("---\ntags: [\"a\", \"flag/red\"]\n---\n", FlagTags.Set("---\ntags: [\"a\", \"flag/blue\"]\n---\n", "red"));
        Assert.Equal("---\ntags:\n  - 'a'\n  - \"flag/red\"\n---\n", FlagTags.Set("---\ntags:\n  - 'a'\n  - \"flag/blue\"\n---\n", "red"));
        Assert.Equal("---\ntags: [\"a, b\", c]\n---\n", FlagTags.Set("---\ntags: [\"a, b\", flag/red, c]\n---\n", null));
    }

    [Fact]
    public void A_comment_after_the_list_stays()
    {
        Assert.Equal("---\ntags: [a, flag/red] # mine\n---\n", FlagTags.Set("---\ntags: [a, flag/blue] # mine\n---\n", "red"));
        Assert.Equal("---\ntags: [a] # mine\n---\n", FlagTags.Set("---\ntags: [a, flag/blue] # mine\n---\n", null));
        Assert.Equal("---\ntags: # list\n  - a # first\n  - flag/red\n---\n", FlagTags.Set("---\ntags: # list\n  - a # first\n---\n", "red"));
        Assert.Equal("---\ntags: a, flag/red # old\n---\n", FlagTags.Set("---\ntags: a # old\n---\n", "red"));
    }

    [Theory]
    [InlineData("---\ntags: a, b\n---\n", "---\ntags: a, b, flag/red\n---\n")]
    [InlineData("---\ntags: a b\n---\n", "---\ntags: a b flag/red\n---\n")]
    [InlineData("---\ntags: a\n---\n", "---\ntags: a, flag/red\n---\n")]
    [InlineData("---\ntags: \"a b\"\n---\n", "---\ntags: \"a b flag/red\"\n---\n")]
    [InlineData("---\ntags: a,b, flag/blue\n---\n", "---\ntags: a,b, flag/red\n---\n")]
    public void The_string_form_stays_a_string(string note, string expected)
    {
        var set = FlagTags.Set(note, "red");
        Assert.Equal(expected, set);
        if (!note.Contains("flag/")) Assert.Equal(note, FlagTags.Set(set!, null));
    }

    [Theory]
    [InlineData("---\ntags: [a, flag/blue, b]\n---\n", "---\ntags: [a, flag/red, b]\n---\n")]
    [InlineData("---\ntags:\n  - flag/blue\n  - a\n---\n", "---\ntags:\n  - flag/red\n  - a\n---\n")]
    [InlineData("---\ntags: flag/blue a\n---\n", "---\ntags: flag/red a\n---\n")]
    [InlineData("---\ntags: [a, flag/blue, flag/done, b]\n---\n", "---\ntags: [a, flag/red, b]\n---\n")]
    public void Setting_replaces_the_old_flag(string note, string expected) => Assert.Equal(expected, FlagTags.Set(note, "red"));

    [Theory]
    [InlineData("---\ntags: [a, flag/blue, b]\n---\nText\n")]
    [InlineData("---\ntags: [ a,  'flag/blue' , b ]\n---\nText\n")]
    [InlineData("---\ntags:\n  - a\n  - flag/blue # mine\n  - b\n---\nText\n")]
    [InlineData("---\ntags: a, flag/blue, b\n---\nText\n")]
    public void Undo_after_replacing_a_mid_list_flag_gives_the_note_back_byte_for_byte(string note)
    {
        var set = FlagTags.Set(note, "green");
        Assert.NotNull(set);
        Assert.Contains("flag/green", set);
        Assert.Equal(note, FlagTags.Set(set, "blue"));
    }

    [Theory]
    [InlineData("---\ntags: [flag/red, a, flag/maybe, flag/Done, b, flag, flag/red/x]\n---\n", "---\ntags: [a, flag/maybe, b, flag, flag/red/x]\n---\n")]
    [InlineData("---\ntags: [ flag/red, a ]\n---\n", "---\ntags: [ a ]\n---\n")]
    [InlineData("---\ntags:\n  - flag/red\n  - a\n  - flag/white\n---\n", "---\ntags:\n  - a\n---\n")]
    [InlineData("---\ntags: flag/red, a, flag/blue\n---\n", "---\ntags: a\n---\n")]
    [InlineData("---\ntags: [a, b]\n---\n#flag/red\n", "---\ntags: [a, b]\n---\n#flag/red\n")]
    [InlineData("Text\n", "Text\n")]
    public void No_flag_removes_every_known_flag_and_nothing_else(string note, string expected) => Assert.Equal(expected, FlagTags.Set(note, null));

    [Fact]
    public void Set_removes_flag_RED_and_hash_flag_red()
    {
        Assert.Equal("---\ntags: [flag/blue, a]\n---\n", FlagTags.Set("---\ntags: [flag/RED, \"#flag/red\", a]\n---\n", "blue"));
        Assert.Equal("---\ntags:\n  - a\n---\n", FlagTags.Set("---\ntags:\n  - a\n  - \"#flag/red/\"\n  - Flag/Red\n---\n", null));
    }

    [Theory]
    [InlineData("---\ntitle: x\ntags: [flag/red]\n---\nText\n", "---\ntitle: x\n---\nText\n")]
    [InlineData("---\ntitle: x\ntags:\n  - flag/red\n\n  - flag/done\nstatus: open\n---\n", "---\ntitle: x\nstatus: open\n---\n")]
    [InlineData("---\ntags: flag/red\ntitle: x\n---\n", "---\ntitle: x\n---\n")]
    [InlineData("---\ntags: [flag/red]\n---\nText", "Text")]
    public void An_empty_tags_left_is_removed(string note, string expected) => Assert.Equal(expected, FlagTags.Set(note, null));

    [Theory]
    [InlineData("---\ntags:\n---\n")]
    [InlineData("---\ntags: []\n---\n")]
    [InlineData("---\ntags: \"\"\n---\n")]
    public void An_empty_tags_gets_the_flag(string note) => Assert.Equal("---\ntags: [flag/red]\n---\n", FlagTags.Set(note, "red"));

    [Theory]
    [InlineData("---\ntags:\n  a: b\n---\n")]
    [InlineData("---\ntags: {a: b}\n---\n")]
    [InlineData("---\ntags:\n  - a: b\n---\n")]
    public void A_map_under_tags_is_refused(string note) => Assert.Null(FlagTags.Set(note, "red"));

    [Theory]
    [InlineData("---\ntags:\n  - - a\n---\n")]
    [InlineData("---\ntags: [a, [b]]\n---\n")]
    [InlineData("---\ntags: |\n  a\n---\n")]
    [InlineData("---\ntags: >-\n  a b\n---\n")]
    [InlineData("---\ntags: a\n  b\n---\n")]
    public void A_nested_list_or_a_multi_line_scalar_is_refused(string note) => Assert.Null(FlagTags.Set(note, "red"));

    [Fact]
    public void A_flow_list_over_several_lines_is_refused()
    {
        Assert.Null(FlagTags.Set("---\ntags: [a,\n  b]\n---\n", "red"));
        Assert.Null(FlagTags.Set("---\ntags: [a,\n  flag/red]\n---\n", null));
    }

    [Fact]
    public void A_comment_between_block_items_is_refused()
    {
        Assert.Null(FlagTags.Set("---\ntags:\n  - a\n  # c\n  - b\n---\n", "red"));
        Assert.Null(FlagTags.Set("---\ntags:\n  - a\n  # c\n  - flag/red\n---\n", null));
    }

    // Neither TagReader nor Frontmatter.Read sees frontmatter behind a byte order mark, so nothing here can say what the note's
    // tags are.
    [Theory]
    [InlineData("\uFEFFText\n", "red")]
    [InlineData("\uFEFF---\ntags: [a]\n---\nText\n", "red")]
    [InlineData("\uFEFF---\ntags: [flag/red]\n---\nText\n", null)]
    public void A_byte_order_mark_is_refused(string note, string? flag) => Assert.Null(FlagTags.Set(note, flag));

    [Fact]
    public void A_result_that_would_not_parse_is_refused() => Assert.Null(FlagTags.Set("---\n tags: [a]\n---\n", "red"));

    [Fact]
    public void A_comment_after_an_empty_tags_stays() =>
        Assert.Equal("---\ntags: [flag/red] # mine\n---\n", FlagTags.Set("---\ntags: # mine\n---\n", "red"));

    [Fact]
    public void A_block_that_was_empty_stays() => Assert.Equal("---\n---\nText\n", FlagTags.Set("---\n---\nText\n", null));

    [Fact]
    public void A_frontmatter_that_does_not_parse_is_refused() => Assert.Null(FlagTags.Set("---\ntags: [a\ntitle: : x\n---\n", "red"));

    [Fact]
    public void Tags_with_a_capital_is_another_key()
    {
        Assert.Equal("---\nTags: [flag/blue]\ntags: [flag/red]\n---\n", FlagTags.Set("---\nTags: [flag/blue]\n---\n", "red"));
        Assert.Equal("---\nTags: [flag/blue]\n---\n", FlagTags.Set("---\nTags: [flag/blue]\n---\n", null));
    }

    [Fact]
    public void The_key_written_quoted_is_the_same_key()
    {
        Assert.Equal("---\n\"tags\": [a, flag/red]\n---\n", FlagTags.Set("---\n\"tags\": [a]\n---\n", "red"));
        Assert.Equal("---\n'tags': [a]\n---\n", FlagTags.Set("---\n'tags': [a, flag/red]\n---\n", null));
    }

    [Fact]
    public void An_unknown_flag_is_an_error() => Assert.Throws<ArgumentException>(() => FlagTags.Set("Text", "maybe"));

    public static TheoryData<string, string?> Cases => new()
    {
        { "Text\n", "red" },
        { "---\ntitle: x\n---\n", "done" },
        { "---\ntags: [a, b]\n---\n", "green" },
        { "---\ntags: [a, flag/blue, b]\n---\n", "red" },
        { "---\ntags: [a, flag/blue, b]\n---\n", null },
        { "---\ntags: [flag/RED, \"#flag/red\", a]\n---\n", "white" },
        { "---\ntags: [\"a\", 'b'] # c\n---\n", "purple" },
        { "---\ntags:\n  - a\n  - flag/red\n---\n", "yellow" },
        { "---\ntags:\n  - flag/red\n---\n", null },
        { "---\ntags: a, b\n---\n", "important" },
        { "---\ntags: a b flag/done\n---\n", "red" },
        { "---\ntags: a b flag/done\n---\n", null },
        { "---\ntags:\n---\n", "red" },
        { "---\ntags: \"\"\n---\n", "blue" },
        { "---\r\ntags:\r\n  - a\r\n---\r\nText\r\n", "red" },
        { "---\nTags: [x]\ntitle: y\n---\n#flag/blue\n", "red" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_result_reads_back(string note, string? flag)
    {
        var result = FlagTags.Set(note, flag);
        Assert.NotNull(result);
        Assert.Null(Frontmatter.Read(result).Problem);
        var expected = FrontmatterTags(note).Where(t => !IsFlag(t)).Concat(flag is null ? [] : [Flags.Tag(flag)]).Order();
        Assert.Equal(expected, FrontmatterTags(result).Order());
        if (flag is not null) Assert.Equal(new NoteFlag(flag, false, Flags.Tag(flag)), Flags.Of(result));
        else Assert.False(Flags.Of(result) is { InText: false });
    }

    private static IEnumerable<string> FrontmatterTags(string content)
    {
        var end = Outline.FrontmatterEnd(Lines.Split(content));
        return TagReader.Read(content).Where(t => t.Line < end).Select(t => t.Tag).ToList();
    }

    private static bool IsFlag(string tag) =>
        tag.StartsWith("flag/", StringComparison.OrdinalIgnoreCase) && Flags.Names.Contains(tag[5..].ToLowerInvariant());
}
