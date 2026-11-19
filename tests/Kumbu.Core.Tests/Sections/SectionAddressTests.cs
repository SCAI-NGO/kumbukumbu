// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Sections;

public class SectionAddressTests
{
    private const string Note = """
        # Architecture
        ## Gates
        gates
        ## Notes
        a
        ## Notes
        b
        # Plan
        ## Notes
        c
        # C# basics
        # Array[2]
        # Q\#A
        # C# notes | x
        # C basics
        # Draft [v2] ^x 50%% off
        # A|B
        # A^B
        # The **road**map
        # See [[Plan|the plan]]
        # `code` here
        # Road **x**
        # Road x
        # Ref [[Plan#Goals]] %%hidden%% ==hi==
        """;

    private static readonly IReadOnlyList<Heading> Headings = Outline.Parse(Lines.Split(Note));

    private static string[] Resolve(string fragment)
    {
        Assert.True(SectionAddress.TryParse(fragment, out var segments), fragment);
        return [.. SectionAddress.Resolve(Headings, segments).Select(h => h.Address)];
    }

    [Theory]
    [InlineData("/W/a.md#A#B", "/W/a.md", "A#B")]
    [InlineData("/W/a.md", "/W/a.md", null)]
    [InlineData("/W/C#.md#Intro", "/W/C#.md", "Intro")]
    [InlineData("/W/x.md#", "/W/x.md", "")]
    public void SplitsAtTheFirstHashAfterMd(string arg, string path, string? fragment) =>
        Assert.Equal((path, fragment), SectionAddress.Split(arg));

    [Theory]
    [InlineData("Gates", "#Architecture#Gates")]
    [InlineData("Architecture#Gates", "#Architecture#Gates")]
    [InlineData("gates", "#Architecture#Gates")]
    [InlineData("Plan#Notes", "#Plan#Notes")]
    [InlineData("Architecture#Notes[2]", "#Architecture#Notes[2]")]
    [InlineData("Notes[2]", "#Architecture#Notes[2]")]
    [InlineData(@"C\# basics", @"#C\# basics")]
    [InlineData(@"Array\[2]", @"#Array\[2]")]
    [InlineData(@"Q\#A", @"#Q\#A")]
    [InlineData("Plan#Notes[1]", "#Plan#Notes")]
    public void ResolvesToOneHeading(string fragment, string address) => Assert.Equal([address], Resolve(fragment));

    // Obsidian writes a heading into a link without '# | ^ [ ]' and '%%': a further pass matches that form.
    [Theory]
    [InlineData("C notes x", @"#C\# notes | x")]
    [InlineData("c NOTES x", @"#C\# notes | x")]
    [InlineData("Draft v2 x 50 off", "#Draft [v2] ^x 50%% off")]
    [InlineData("QA", @"#Q\#A")]
    [InlineData("C basics", "#C basics")]
    [InlineData("c basics", "#C basics")]
    // The web app links a heading by its text as shown: Markdown marks, link targets and comments gone.
    [InlineData("The roadmap", "#The **road**map")]
    [InlineData("the ROADMAP", "#The **road**map")]
    [InlineData("See the plan", "#See [[Plan|the plan]]")]
    [InlineData("code here", "#`code` here")]
    [InlineData("Road x", "#Road x")]
    [InlineData("Road **x**", "#Road **x**")]
    [InlineData("Ref Plan › Goals hi", @"#Ref [[Plan\#Goals]] %%hidden%% ==hi==")]
    [InlineData("Ref PlanGoals hidden ==hi==", @"#Ref [[Plan\#Goals]] %%hidden%% ==hi==")]
    public void ResolvesAHeadingAsObsidianLinksIt(string fragment, string address) => Assert.Equal([address], Resolve(fragment));

    [Fact]
    public void AmbiguousInTheLinkedForm() => Assert.Equal(["#A|B", "#A^B"], Resolve("AB"));

    [Fact]
    public void AmbiguousAddressesMatchSeveral() => Assert.Equal(
        ["#Architecture#Notes[1]", "#Architecture#Notes[2]", "#Plan#Notes"], Resolve("Notes"));

    [Theory]
    [InlineData("Nope")]
    [InlineData("Plan#Gates")]
    [InlineData("Gates#Architecture")]
    [InlineData("Architecture#Notes[3]")]
    public void NoMatch(string fragment) => Assert.Empty(Resolve(fragment));

    [Theory]
    [InlineData("")]
    [InlineData("A##B")]
    [InlineData("A#")]
    [InlineData("  ")]
    public void InvalidAddresses(string fragment) => Assert.False(SectionAddress.TryParse(fragment, out _));

    [Fact]
    public void A_heading_with_code_highlight_and_a_link_is_found_by_its_shown_text()
    {
        var headings = Outline.Parse(Lines.Split("# Use ``a`b`` with ==care== and [the plan](x.md)"));
        Assert.True(SectionAddress.TryParse("Use `ab`` with care and the plan", out var segments));
        Assert.Equal(["#Use ``a`b`` with ==care== and [the plan](x.md)"], SectionAddress.Resolve(headings, segments).Select(h => h.Address));
    }

    // The web app's patterns (outline.ts), which a heading's shown text must give exactly.
    [Fact]
    public void Code_spans_are_masked_as_the_web_app_finds_them()
    {
        var pattern = new Regex(@"(`+)[\s\S]*?(?<!`)\1(?!`)");
        foreach (var s in AllStrings("`a ", 9))
            Assert.Equal(pattern.Replace(s, m => new string(' ', m.Length)), SectionAddress.MaskCodeSpans(s));
    }

    [Fact]
    public void Highlights_are_taken_out_as_the_web_app_does()
    {
        var pattern = new Regex(@"==(?=[^\s=])(.+?)(?<=[^\s=])==");
        foreach (var s in AllStrings("= a\n", 8))
            Assert.Equal(pattern.Replace(s, "$1"), SectionAddress.WithoutHighlights(s));
    }

    [Fact]
    public void Markdown_links_become_their_text_as_the_web_app_shows_them()
    {
        var pattern = new Regex(@"!?\[([^\]]*)\]\([^)]*\)");
        foreach (var s in AllStrings("![]()a\n", 7))
            Assert.Equal(pattern.Replace(s, "$1"), SectionAddress.WithoutMarkdownLinks(s));
    }

    /// <summary>Every string over the alphabet up to the length, the empty one included.</summary>
    private static IEnumerable<string> AllStrings(string alphabet, int maxLength)
    {
        var level = new List<string> { "" };
        yield return "";
        for (var length = 1; length <= maxLength; length++)
        {
            level = [.. level.SelectMany(s => alphabet.Select(c => s + c))];
            foreach (var s in level) yield return s;
        }
    }
}
