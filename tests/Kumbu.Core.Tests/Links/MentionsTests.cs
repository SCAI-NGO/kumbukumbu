// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Links;

namespace Kumbu.Core.Tests.Links;

public class MentionsTests
{
    private static (int Line, (int Start, int Length)[] Marks)[] Of(string content, string title) =>
        [.. Mentions.In(content, title).Select(m => (m.Line, m.Marks.ToArray()))];

    [Fact]
    public void A_plain_mention_counts()
    {
        var mention = Assert.Single(Of("Talked about the plan with Bob.", "Plan"));
        Assert.Equal(0, mention.Line);
        Assert.Equal([(17, 4)], mention.Marks);
    }

    [Theory]
    [InlineData("planning ahead", false)]
    [InlineData("an airplane", false)]
    [InlineData("plan2 is out", false)]
    [InlineData("the plan.", true)]
    [InlineData("a (plan) here", true)]
    [InlineData("PLAN", true)]
    public void Whole_words_only(string line, bool counts) =>
        Assert.Equal(counts, Of(line, "Plan").Length == 1);

    [Theory]
    [InlineData("see `the plan` here")]
    [InlineData("```\nthe plan\n```")]
    [InlineData("text\n\n    the plan\n")]
    [InlineData("see [[Plan]]")]
    [InlineData("see [[Other|the plan]]")]
    [InlineData("see [the plan](x.md)")]
    [InlineData("see ![the plan](x.png)")]
    [InlineData("see [x](https://a.example/plan)")]
    [InlineData("see https://a.example/plan")]
    [InlineData("see <https://a.example/plan>")]
    [InlineData("---\ntitle: plan\n---\nbody")]
    public void Mentions_in_code_links_urls_and_front_matter_dont_count(string content) =>
        Assert.Empty(Of(content, "Plan"));

    [Fact]
    public void A_line_with_a_link_and_a_plain_mention_counts()
    {
        var mention = Assert.Single(Of("[[Plan]] and the plan", "Plan"));
        Assert.Equal([(17, 4)], mention.Marks);
    }

    [Fact]
    public void Two_mentions_on_a_line_are_one_entry_with_two_marks()
    {
        var mention = Assert.Single(Of("intro\nplan A or plan B", "Plan"));
        Assert.Equal(1, mention.Line);
        Assert.Equal([(0, 4), (10, 4)], mention.Marks);
    }

    [Fact]
    public void Titles_under_three_characters_have_none() => Assert.Empty(Of("AB and ab", "AB"));

    [Fact]
    public void Titles_with_spaces()
    {
        var mention = Assert.Single(Of("From the meeting notes of Monday", "Meeting Notes"));
        Assert.Equal([(9, 13)], mention.Marks);
    }

    [Fact]
    public void A_title_in_another_normalisation_counts()
    {
        var decomposed = "Über".Normalize(NormalizationForm.FormD);
        var mention = Assert.Single(Of($"das {decomposed} heute", "Über"));
        Assert.Equal([(4, decomposed.Length)], mention.Marks);
    }
}
