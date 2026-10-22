// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Sections;

public class OutlineTests
{
    private static IReadOnlyList<Heading> Parse(string text) => Outline.Parse(Lines.Split(text));

    private static string Summary(string text) =>
        string.Join(" | ", Parse(text).Select(h => $"{h.Address} {h.Line}-{h.ContentEnd}/{h.End}"));

    [Fact]
    public void NestedSectionsEndAtTheNextHeadingOfTheSameOrHigherLevel() => Assert.Equal(
        "#A 0-6/7 | #A#B 2-4/5 | #A#C 5-6/7 | #D 7-9/9",
        Summary("# A\n\n## B\ntext\n\n## C\n\n# D\nlast\n"));

    [Fact]
    public void TrailingBlankLinesAreNotPartOfTheContent() =>
        Assert.Equal("#A 0-2/4 | #B 4-5/5", Summary("# A\ntext\n\n\n# B\n"));

    [Theory]
    [InlineData("#tag at line start\n")]
    [InlineData("    # indented four spaces is code\n")]
    [InlineData("####### seven hashes\n")]
    [InlineData("```\n# in a fence\n```\n")]
    [InlineData("~~~~\n# in a tilde fence\n~~~\n```\n# still inside: ~~~ closes only with 4\n~~~~\n")]
    [InlineData("---\ntitle: x\n# a YAML comment\n---\n")]
    public void NotHeadings(string text) => Assert.Empty(Parse(text));

    [Theory]
    [InlineData("# Title #", "Title")]
    [InlineData("##   Spaced   ", "Spaced")]
    [InlineData("   ### Indented", "Indented")]
    [InlineData("# C#", "C#")]
    [InlineData("# Ü & ß", "Ü & ß")]
    public void HeadingText(string line, string text) => Assert.Equal(text, Assert.Single(Parse(line)).Text);

    [Fact]
    public void HeadingAfterFrontmatterAndFenceCounts() =>
        Assert.Equal("#Real 5-6/6", Summary("---\na: 1\n---\n```\n```\n# Real\n"));

    [Fact]
    public void UnclosedFrontmatterIsJustText() => Assert.Equal("#H 1-2/2", Summary("---\n# H\n"));

    [Fact]
    public void DuplicatesUnderOneParentAreNumbered() => Assert.Equal(
        "#A 0-3/3 | #A#Notes[1] 1-2/2 | #A#Notes[2] 2-3/3 | #B 3-5/5 | #B#Notes 4-5/5",
        Summary("# A\n## Notes\n## Notes\n# B\n## Notes\n"));

    [Fact]
    public void AddressesEscapeHashAndAnIndexLikeEnding()
    {
        var headings = Parse("# C# basics\n# Array[2]\n# back\\slash\n");
        Assert.Equal([@"#C\# basics", @"#Array\[2]", @"#back\\slash"], headings.Select(h => h.Address));
    }
}
