// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Tags;

namespace Kumbu.Core.Tests.Tags;

/// <summary>
/// One row per rule, a tag and a non-tag each. The rules are Obsidian's: the help vault's "Tags" and "Properties"
/// pages (tag characters, at least one non-digit, nested with '/', case ignored, the tags property as a list; the
/// deprecated <c>tag</c> property no longer read since Obsidian 1.9) and the API's <c>getAllTags</c> and
/// <c>parseFrontMatterTags</c> (frontmatter and inline tags together; a plain string value).
/// </summary>
public class TagReaderTests
{
    private static string Tags(string content) => string.Join(" ", TagReader.Read(content).Select(t => t.Tag));

    [Theory]
    // Inline: '#' then tag characters, at the start of a line or after whitespace.
    [InlineData("#research", "research")]
    [InlineData("text #research more", "research")]
    [InlineData("- [ ] task #todo", "todo")]
    [InlineData("a\t#tab", "tab")]
    [InlineData("word#tag", "")]
    [InlineData("(#tag)", "")]
    [InlineData(@"\#escaped", "")]
    // Headings are no tags; a heading's text may carry one.
    [InlineData("# Title", "")]
    [InlineData("## Title", "")]
    [InlineData("##tag", "")]
    [InlineData("# Title #inheading", "inheading")]
    [InlineData("#Title", "Title")]
    [InlineData("> quoted #tag", "tag")]
    [InlineData("| cell #tag |", "tag")]
    [InlineData("**#bold** *a* #after", "after")]
    [InlineData("#tag\r\n#next", "tag next")]
    // The tag ends at the first other character.
    [InlineData("#tag.", "tag")]
    [InlineData("#tag, #other;", "tag other")]
    [InlineData("#tag's", "tag")]
    [InlineData("#a+b", "a")]
    [InlineData("#tag…", "tag")]
    [InlineData("#tag—dash", "tag")]
    [InlineData("#snake_case #kebab-case #camelCase", "snake_case kebab-case camelCase")]
    // At least one character that isn't a digit.
    [InlineData("#123", "")]
    [InlineData("#2026", "")]
    [InlineData("#v2", "v2")]
    [InlineData("#2026-plan", "2026-plan")]
    [InlineData("#y1984", "y1984")]
    // Unicode letters, and emoji: Obsidian takes "commonly accepted Unicode characters, including emojis".
    [InlineData("#Grüße", "Grüße")]
    [InlineData("#日本語", "日本語")]
    [InlineData("#🚀launch", "🚀launch")]
    // Nested; a trailing '/' is dropped.
    [InlineData("#project/kumbu", "project/kumbu")]
    [InlineData("#project/", "project")]
    // URLs and colours in prose with no space before the '#'.
    [InlineData("https://example.org/page#part", "")]
    [InlineData("see [docs](https://example.org/#part)", "")]
    [InlineData("[[Note#Heading]] and [[Note#Heading|#alias]]", "")]
    [InlineData("&#123; entity", "")]
    // Code and comments.
    [InlineData("`#code` #real", "real")]
    [InlineData("``a #b`` #c", "c")]
    [InlineData("```\n#fenced\n```\n#after", "after")]
    [InlineData("~~~\n#fenced\n~~~", "")]
    [InlineData("text\n\n    #indented code", "")]
    [InlineData("<!-- #hidden --> #shown", "shown")]
    [InlineData("<!--\n#hidden\n-->\n#shown", "shown")]
    [InlineData("%% #hidden %% #shown", "shown")]
    [InlineData("%%\n#hidden\n%%\n#shown", "shown")]
    [InlineData("<span style=\"color: #fff\">x</span> #shown", "shown")]
    // Inside a list the same indent is a nested item, not code.
    [InlineData("- item\n\n    - nested #nested", "nested")]
    [InlineData("- item\n    - nested #nested", "nested")]
    [InlineData("1. item\n\n    more text #inlist", "inlist")]
    public void Inline(string content, string expected) => Assert.Equal(expected, Tags(content));

    [Theory]
    [InlineData("---\ntags:\n  - research\n  - project/kumbu\n---\nbody", "research project/kumbu")]
    [InlineData("---\ntags: [research, \"#idea\"]\n---\n", "research idea")]
    [InlineData("---\ntags: research, idea\n---\n", "research idea")]
    [InlineData("---\ntags: research idea\n---\n", "research idea")]
    [InlineData("---\ntags: research\n---\n", "research")]
    [InlineData("---\ntags:\n- research\n---\n", "research")]
    [InlineData("---\n\"tags\": [a1]\n---\n", "a1")]
    // Numbers only and empty values are no tags.
    [InlineData("---\ntags: [2026, ok]\n---\n", "ok")]
    [InlineData("---\ntags:\n---\n", "")]
    // The deprecated 'tag' property is not read (Obsidian dropped it in 1.9).
    [InlineData("---\ntag: research\n---\n", "")]
    // Other properties are not tags; a '#' inside them is not an inline tag either.
    [InlineData("---\ntitle: \"#notatag\"\naliases: [x]\n---\n", "")]
    // Broken frontmatter: no tags from it, no error; the text is still read.
    [InlineData("---\ntags: [unclosed\n---\n#body", "body")]
    [InlineData("---\ntags: [a]\ntags: [b]\n---\n#body", "body")]
    // Frontmatter and text together, frontmatter first.
    [InlineData("---\ntags: [a]\n---\n#b", "a b")]
    public void Frontmatter(string content, string expected) => Assert.Equal(expected, Tags(content));

    [Fact]
    public void Lines_are_those_of_the_key_list_item_or_text()
    {
        var uses = TagReader.Read("---\ntitle: x\ntags:\n  - a\n  - b\n---\n\nline #c\n");

        Assert.Equal([new TagUse("a", 3), new TagUse("b", 4), new TagUse("c", 7)], uses);
        Assert.Equal([new TagUse("a", 1), new TagUse("b", 1)], TagReader.Read("---\ntags: [a, b]\n---\n"));
    }

    [Fact]
    public void A_decomposed_tag_reads_as_composed()
    {
        Assert.Equal("Grüße", Assert.Single(TagReader.Read("#Grüße")).Tag);
    }

    [Theory]
    [InlineData("research", "research", true)]
    [InlineData("Research", "research", true)]
    [InlineData("project", "project/kumbu", true)]
    [InlineData("project", "Project/Kumbu/deep", true)]
    [InlineData("project/kumbu", "project", false)]
    [InlineData("project", "projects", false)]
    [InlineData("project/kumbu", "project/kumbu2", false)]
    public void Covers(string asked, string tag, bool covers) => Assert.Equal(covers, TagMatch.Covers(asked, tag));

    [Theory]
    [InlineData("research", "research")]
    [InlineData("#research", "research")]
    [InlineData(" #project/ ", "project")]
    [InlineData("123", null)]
    [InlineData("two words", null)]
    [InlineData("#", null)]
    [InlineData("a.b", null)]
    public void Normalise(string asked, string? expected) => Assert.Equal(expected, TagMatch.Normalise(asked));
}
