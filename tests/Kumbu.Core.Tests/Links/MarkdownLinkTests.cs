// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Links;

public class MarkdownLinkTests
{
    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    [Theory]
    [InlineData("[a](/Work/plan.md)", "/Work/plan.md", "")]
    [InlineData("[a](Work/plan.md)", "Work/plan.md", "")]
    [InlineData("[a](./sub/plan.md)", "./sub/plan.md", "")]
    [InlineData("[a](../plan.md)", "../plan.md", "")]
    [InlineData("[a](plan.md)", "plan.md", "")]
    [InlineData("[a](plan)", "plan", "")]
    [InlineData("[a](Meeting%20Notes.md)", "Meeting Notes.md", "")]
    [InlineData("[a](<Meeting Notes.md>)", "Meeting Notes.md", "")]
    [InlineData("[a](plan.md#Goals)", "plan.md", "#Goals")]
    [InlineData("[a](plan.md#Goals%20and%20ways)", "plan.md", "#Goals%20and%20ways")]
    [InlineData("[a](plan.md#^block)", "plan.md", "#^block")]
    [InlineData("[a](#Heading)", "", "#Heading")]
    [InlineData("[a](Plan%20%232.md)", "Plan #2.md", "")]
    public void ParsesEveryFormOfTarget(string line, string target, string subpath)
    {
        var link = Assert.IsType<MarkdownLink>(Assert.Single(NoteLink.Parse(line)));
        Assert.Equal((target, subpath, line), (link.Target, link.Subpath, link.Raw));
    }

    [Theory]
    [InlineData("[t](Plan%20(v2).md)", "Plan (v2).md")]
    [InlineData("[t](<a b.md> \"title\")", "a b.md")]
    [InlineData("[t](plan.md 'x')", "plan.md")]
    [InlineData("[t](plan.md (x))", "plan.md")]
    public void ParsesDestinationsWithParenthesesAnglesAndTitles(string line, string target)
    {
        var link = Assert.Single(MarkdownLink.Parse("see " + line + " here"));
        Assert.Equal((target, line, 4), (link.Target, link.Raw, link.Start));
    }

    [Theory]
    [InlineData("[a](https://x.org)")]
    [InlineData("[a](mailto:a@b.c)")]
    [InlineData("[a](obsidian://open?file=x)")]
    [InlineData("[a](//cdn.x/y.md)")]
    [InlineData("![a](plan.md)")]
    [InlineData("[a][ref]")]
    [InlineData("`[a](plan.md)`")]
    [InlineData("```\n[a](plan.md)\n```\n")]
    [InlineData("[a]()")]
    [InlineData("\\[a](plan.md)")]
    public void NotNoteLinks(string text) => Assert.Empty(NoteLink.Parse(text));

    [Theory]
    [InlineData("[see [this]](x.md)")]
    [InlineData("[a \\] b](x.md)")]
    public void LinkTextWithBracketsAndEscapes(string line)
    {
        var link = Assert.Single(MarkdownLink.Parse(line));
        Assert.Equal(("x.md", line), (link.Target, link.Raw));
    }

    [Theory]
    [InlineData("[[plan|see (x)]]")]
    [InlineData("[[x]](y.md)")]
    public void NoMarkdownLinkInsideAWikilink(string line) =>
        Assert.IsType<WikiLink>(Assert.Single(NoteLink.Parse(line)));

    [Fact]
    public void ParseReturnsBothKindsInSourceOrder()
    {
        var links = NoteLink.Parse("[[a]] [b](b.md) [[c]]\n[d](d.md)");
        Assert.Equal(
            [(typeof(WikiLink), "a", 0), (typeof(MarkdownLink), "b.md", 0), (typeof(WikiLink), "c", 0), (typeof(MarkdownLink), "d.md", 1)],
            links.Select(l => (l.GetType(), l.Target, l.Line)));
    }

    [Fact]
    public void FrontmatterCounts() =>
        Assert.Equal("x.md", Assert.Single(NoteLink.Parse("---\nsee: \"[x](x.md)\"\n---\n")).Target);

    [Theory]
    [InlineData("[My *text*](<old name.md#Goals> \"t\")", "[My *text*](</Work/New%20name.md#Goals> \"t\")")]
    [InlineData("[a](../x.md#^b)", "[a](/Work/New%20name.md#^b)")]
    public void WithPathChangesOnlyThePath(string line, string expected) =>
        Assert.Equal(expected, Assert.Single(MarkdownLink.Parse(line)).WithPath(P("/W/Work/New name.md")));

    [Theory]
    [InlineData("/W/Work/Plan #2 (draft).md", "/Work/Plan%20%232%20%28draft%29.md")]
    [InlineData("/W/Bücher/Ü.md", "/Bücher/Ü.md")]
    [InlineData("/W/a%b^c.md", "/a%25b%5Ec.md")]
    public void EncodeFollowsTheTable(string note, string expected) => Assert.Equal(expected, MarkdownLink.Encode(P(note)));

    [Fact]
    public void AnEncodedPathParsesBackToTheNote()
    {
        var note = P("/W/Work/Plan #2 (draft) [x] {y} 100% ^z.md");
        var link = Assert.Single(MarkdownLink.Parse($"[a]({MarkdownLink.Encode(note)})"));
        Assert.Equal("/Work/Plan #2 (draft) [x] {y} 100% ^z.md", link.Target);
    }
}
