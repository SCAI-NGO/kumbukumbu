// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;
using Kumbu.Core.Graph;
using Kumbu.Core.Links;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Graph;

public class LinkExtractTests
{
    private static VaultPath P(string path) => VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);

    private static CalendarTime Day(int year, int month, int day) => new(TimeKind.Day, new DateOnly(year, month, day));

    [Fact]
    public void Takes_links_aliases_tags_and_when()
    {
        var extract = LinkExtract.Of(P("/Work/Pumpe.md"),
            "---\naliases: [Pumpe]\nwhen: 2026-11-20\nuntil: 2026-11-21\n---\n#project/kumbu see [[Plan]] and ![x](/img/a.png)\n");

        Assert.Collection(extract.Links,
            l => Assert.Equal("Plan", l.Target),
            l =>
            {
                Assert.Equal("/img/a.png", l.Target);
                Assert.True(Assert.IsType<MarkdownLink>(l).Embed);
            });
        Assert.Equal(["Pumpe"], extract.Aliases);
        Assert.Equal(["project/kumbu"], extract.Tags);
        Assert.Equal(Day(2026, 11, 20), extract.When);
        Assert.Equal(Day(2026, 11, 21), extract.Until);
    }

    [Fact]
    public void Ignores_links_in_code()
    {
        var extract = LinkExtract.Of(P("/Work/Code.md"), "```\n[[Fenced]]\n```\nand `[[Inline]]` but [[Real]]\n");

        Assert.Equal(["Real"], extract.Links.Select(l => l.Target));
    }

    [Fact]
    public void Day_note_without_front_matter_has_no_when()
    {
        var extract = LinkExtract.Of(P("/W/2026-11-12.md"), "Met [[Anna]].\n");

        Assert.Null(extract.When);
        Assert.Null(extract.Until);
    }

    [Fact]
    public void Day_note_with_when_keeps_it()
    {
        var extract = LinkExtract.Of(P("/W/2026-11-12.md"), "---\nwhen: 2026-11-13\n---\n");

        Assert.Equal(Day(2026, 11, 13), extract.When);
    }

    [Fact]
    public void Tags_are_distinct_in_first_use_order()
    {
        var extract = LinkExtract.Of(P("/W/Tags.md"), "#b #a #B\n#a\n");

        Assert.Equal(["b", "a"], extract.Tags);
    }

    [Fact]
    public void Scheme_is_pinned()
    {
        // A change to any parser that changes this fixture's extract raises SchemeVersion and this test together (spec §4.5).
        Assert.Equal(1, LinkExtract.SchemeVersion);

        var extract = LinkExtract.Of(P("/Work/Fixture.md"),
            "---\naliases: [Fix]\nwhen: 2026-11-20T14:00\n---\nSee [[Plan#Goals|the plan]] and [doc](Other%20Note.md).\n#a/b in `[[Skipped]]`\n");

        Assert.Collection(extract.Links,
            l =>
            {
                var wiki = Assert.IsType<WikiLink>(l);
                Assert.Equal((4, 4, "Plan", "#Goals"), (wiki.Line, wiki.Start, wiki.Target, wiki.Subpath));
            },
            l =>
            {
                var markdown = Assert.IsType<MarkdownLink>(l);
                Assert.Equal((4, 32, "Other Note.md", false), (markdown.Line, markdown.Start, markdown.Target, markdown.Embed));
            });
        Assert.Equal(["Fix"], extract.Aliases);
        Assert.Equal(["a/b"], extract.Tags);
        Assert.Equal(new CalendarTime(TimeKind.Time, new DateOnly(2026, 11, 20), new TimeOnly(14, 0)), extract.When);
        Assert.Null(extract.Until);
    }
}
