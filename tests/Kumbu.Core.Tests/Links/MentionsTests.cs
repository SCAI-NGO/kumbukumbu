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

    [Fact]
    public void Unclosed_brackets_take_linear_time()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var mention = Assert.Single(Of("Plan " + new string('[', 200_000), "Plan"));
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Equal([(0, 4)], mention.Marks);
    }

    [Theory]
    [InlineData("Plan`a`")]
    [InlineData("Plan[a](b)")]
    public void Many_masked_spans_take_linear_time(string unit)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var mention = Assert.Single(Of(string.Concat(Enumerable.Repeat(unit, 80_000)), "Plan"));
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Equal([.. Enumerable.Range(0, 80_000).Select(k => (k * unit.Length, 4))], mention.Marks);
    }

    [Fact]
    public void Several_terms_mark_one_line_once()
    {
        var mention = Assert.Single(Mentions.In("Kumbu, also Kumbukumbu.", ["Kumbukumbu", "Kumbu"]));
        Assert.Equal([(0, 5), (12, 10)], mention.Marks.ToArray());
    }

    [Fact]
    public void Overlapping_marks_keep_the_longest() =>
        Assert.Equal([(0, 9)], Assert.Single(Mentions.In("Kumbu Two here", ["Kumbu", "Kumbu Two"])).Marks.ToArray());

    [Fact]
    public void The_minimum_length_counts_per_term() =>
        Assert.Equal([(0, 5)], Assert.Single(Mentions.In("Kumbu and K² here", ["K²", "Kumbu"])).Marks.ToArray());

    [Fact]
    public void A_single_title_keeps_its_overlapping_marks() =>
        Assert.Equal([(0, 3), (2, 3)], Assert.Single(Of("a-a-a", "a-a")).Marks);
}
