// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;

namespace Kumbu.Core.Tests.Links;

public class WikiLinkTests
{
    [Theory]
    [InlineData("see [[kumbu]].", "kumbu", "", "", false)]
    [InlineData("[[projects/kumbu#Goal|the goal]]", "projects/kumbu", "#Goal", "|the goal", false)]
    [InlineData("| [[kumbu\\|Kumbu]] | table |", "kumbu", "", "\\|Kumbu", false)]
    [InlineData("![[diagram.png]]", "diagram.png", "", "", true)]
    [InlineData("[[note#^block-1]]", "note", "#^block-1", "", false)]
    [InlineData("[[#Heading]]", "", "#Heading", "", false)]
    [InlineData("[[Überlegungen.md]]", "Überlegungen.md", "", "", false)]
    public void ParsesTheParts(string line, string target, string subpath, string alias, bool embed)
    {
        var link = Assert.Single(WikiLink.Parse(line));
        Assert.Equal((target, subpath, alias, embed), (link.Target, link.Subpath, link.Alias, link.Embed));
        Assert.Equal(link.Raw, link.WithTarget(link.Target));
    }

    [Fact]
    public void FindsSeveralWithPositions()
    {
        var links = WikiLink.Parse("a\n[[x]] and [[y|z]]\n");
        Assert.Equal([(1, 0, "[[x]]"), (1, 10, "[[y|z]]")], links.Select(l => (l.Line, l.Start, l.Raw)));
    }

    [Theory]
    [InlineData("```\n[[in fence]]\n```\n")]
    [InlineData("use `[[not a link]]` here\n")]
    [InlineData("``code with ` and [[x]]``\n")]
    [InlineData("[[]]\n")]
    [InlineData("[[broken\nacross]]\n")]
    public void NotLinks(string text) => Assert.Empty(WikiLink.Parse(text));

    [Fact]
    public void LinksInFrontmatterCount() =>
        Assert.Equal("kumbu", Assert.Single(WikiLink.Parse("---\nrelated: \"[[kumbu]]\"\n---\n")).Target);

    [Fact]
    public void WithTargetKeepsEverythingButTheTarget() =>
        Assert.Equal("![[a/b#H|alias]]", Assert.Single(WikiLink.Parse("![[b#H|alias]]")).WithTarget("a/b"));
}
