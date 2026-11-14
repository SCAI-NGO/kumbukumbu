// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Tags;

namespace Kumbu.Core.Tests.Tags;

public class FlagsTests
{
    [Fact]
    public void The_eight_names_in_their_order()
    {
        Assert.Equal(["red", "green", "blue", "purple", "yellow", "white", "done", "important"], Flags.Names);
        Assert.True(Flags.IsFlag("done"));
        Assert.False(Flags.IsFlag("maybe"));
        Assert.Equal("flag/red", Flags.Tag("red"));
    }

    [Fact]
    public void A_flag_in_the_frontmatter_is_read()
    {
        Assert.Equal(new NoteFlag("red", false, "flag/red"), Flags.Of("---\ntags: [a, flag/red]\n---\nText\n"));
        Assert.Equal(new NoteFlag("done", false, "flag/done"), Flags.Of("---\ntags:\n  - flag/done\n---\n"));
    }

    [Fact]
    public void The_frontmatter_wins_over_the_text()
    {
        Assert.Equal("blue", Flags.Of("---\ntags: [flag/blue]\n---\n#flag/red\n")?.Name);
    }

    [Fact]
    public void A_flag_in_the_text_is_read_and_says_so()
    {
        Assert.Equal(new NoteFlag("red", true, "flag/red"), Flags.Of("---\ntags: [a]\n---\nSee #flag/red\n"));
        Assert.Equal(new NoteFlag("red", true, "flag/red"), Flags.Of("#flag/red"));
    }

    [Fact]
    public void The_first_known_flag_wins()
    {
        Assert.Equal("yellow", Flags.Of("---\ntags: [flag/maybe, flag/yellow, flag/red]\n---\n")?.Name);
        Assert.Equal("important", Flags.Of("#flag/x #flag/important #flag/done")?.Name);
    }

    [Theory]
    [InlineData("#flag/maybe")]
    [InlineData("#flag/red/x")]
    [InlineData("#flag")]
    [InlineData("---\ntags: [flag, flag/maybe, flag/red/x]\n---\n")]
    [InlineData("#redflag #flags/red")]
    public void Unknown_tags_below_flag_are_no_flag(string content) => Assert.Null(Flags.Of(content));

    [Fact]
    public void Case_is_ignored()
    {
        Assert.Equal(new NoteFlag("red", false, "flag/RED"), Flags.Of("---\ntags: [flag/RED]\n---\n"));
        Assert.Equal(new NoteFlag("green", true, "Flag/Green"), Flags.Of("#Flag/Green"));
    }

    [Fact]
    public void A_flag_in_code_is_no_flag()
    {
        Assert.Null(Flags.Of("`#flag/red`\n```\n#flag/blue\n```\n"));
    }
}
