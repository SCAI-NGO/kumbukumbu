// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;

namespace Kumbu.Core.Tests.Publishing;

public class FairCopyTests
{
    private static readonly VaultPath Note = P("/Work/Routing/Questions.md");

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    // Resolves a bare attachment name to the note's folder, as LinkResolver would for one lying there.
    private static VaultPath? InFolder(string target) =>
        Path.GetExtension(target) is var ext && ext.Length > 0 && ext != ".md" ? P("/Work/Routing/" + target.Split('/')[^1]) : null;

    private static FairCopy Copy(string content, bool mayCopy = true, string? ownOrigin = null) =>
        FairCopies.Make(Note, content, InFolder, _ => mayCopy, ownOrigin);

    private static string Text(string content, string? ownOrigin = null) => Copy(content, ownOrigin: ownOrigin).Markdown;

    [Fact]
    public void Frontmatter_goes() => Assert.Equal("Text", Text("---\nstatus: draft\n---\nText"));

    [Fact]
    public void The_first_heading_is_the_title_without_its_tags()
    {
        var copy = Copy("# Questions on routing #routing\n\n1. x");
        Assert.Equal("Questions on routing", copy.Title);
        Assert.Equal("1. x", copy.Markdown);
    }

    [Fact]
    public void Without_a_heading_the_title_is_the_file_name()
    {
        Assert.Equal("Questions", Copy("x").Title);
        Assert.Equal("Questions", Copy("## Not a title\nx").Title);
        Assert.Equal("## Not a title\nx", Copy("## Not a title\nx").Markdown);
    }

    [Fact]
    public void A_tag_standing_alone_goes()
    {
        Assert.Equal("Done.", Text("Done. #todo #later"));
        Assert.Equal("a\nb", Text("a\n#todo\nb"));
    }

    [Fact]
    public void A_tag_in_running_text_keeps_its_word()
    {
        Assert.Equal("We discussed routing today", Text("We discussed #routing today"));
        Assert.Equal("a/b is open", Text("#a/b is open"));
    }

    [Fact]
    public void Comments_go()
    {
        Assert.Equal("a\nb", Text("a\n%% ask about deep links later %%\nb"));
        Assert.Equal("a  b", Text("a %%x%% b"));
        Assert.Equal("b", Text("<!-- x -->\nb"));
        Assert.Equal("a\nb", Text("a\n%% one\ntwo\nthree %%\nb"));
        Assert.Equal("a", Text("a\n%% never closed\n[[Private/Plan]]"));
        // A fence inside a comment makes no code of what follows it.
        Assert.Equal("a\nPlan b", Text("a\n%%\n```\n%%\n[[Private/Plan]] %%x%%b"));
    }

    [Theory]
    [InlineData("[[Routing master]]", "Routing master")]
    [InlineData("[[Projects/Routing master]]", "Routing master")]
    [InlineData("[[Routing master|the master]]", "the master")]
    [InlineData("[[Routing master#Open]]", "Routing master")]
    [InlineData("[[#Open]]", "Open")]
    [InlineData("[see this](/Work/Private/Plan.md)", "see this")]
    [InlineData("[x](../Private/Plan.md#h)", "x")]
    [InlineData("![[Other note]]", "Other note")]
    [InlineData("[x](obsidian://open?vault=Work)", "x")]
    public void Every_link_form_becomes_its_text(string link, string text) => Assert.Equal($"a {text} b", Text($"a {link} b"));

    [Fact]
    public void No_path_leaves()
    {
        var content = string.Join('\n',
            "Plain [[Private/Plan]] and [[Private/Plan.md]].",
            "Alias [[Private/Plan|the plan]], heading [[Private/Plan#Open]], both [[Private/Plan#Open|it]].",
            "Embedded ![[Private/Plan]] and ![[Private/Plan.md#Open]].",
            "Markdown [one](/Work/Private/Plan.md), [two](../Private/Plan.md#h), [three](<Private/Plan.md> \"t\"), ![four](Private/Plan.md).",
            "Encoded [five](Private/Plan%2Emd) and [[[Private/Plan]]](https://example.org).",
            "App [six](obsidian://open?vault=Work&file=Private%2FPlan).",
            "Reference [p][d], [d][] and [d].",
            "",
            "[d]: Private/Plan.md \"Work\"",
            "",
            "Html <a href=\"Private/Plan.md\">p</a> <img src=\"/Work/Private/Plan.md\">.",
            "Autolinks <obsidian://open?vault=Work&file=Private%2FPlan> <file:///home/alice/Work/Private/Plan.md>.",
            "With a space <obsidian://open?vault=Work&file=Private Plan>.",
            "",
            "Over lines [see",
            "this](Private/Plan.md), [x](",
            "Private/Plan.md), [y](https://example.org \"a",
            "b\"), [z](Private/Plan.md \"a",
            "b\") and [[[Private/Plan]]](Private/Plan.md).",
            "Tags <a",
            "href=\"Private/Plan.md\">p</a>, <a href='Private/Plan.md'",
            ">q</a>, <a title=\"x>y\" href=\"Private/Plan.md\">r</a> and <img alt='>' src=\"Private/Plan.md\"/>.",
            "",
            "> [e]: Private/Plan.md",
            "- [f]: Private/Plan.md",
            "1. [g]: <Private/Plan.md>",
            "> - [h]:",
            ">   Private/Plan.md",
            "",
            "[i]: Private/Plan.md",
            "  \"Work",
            "  title\"",
            "",
            "Uses [p][e] [p][f] [p][g] [p][h] [p][i].",
            "",
            "Own https://kumbu.example.org/notes/Work/Private/Plan.md, <http://KUMBU.example.org.:8080/notes/Work/a.md>,",
            "www.kumbu.example.org/notes/Work/b.md and kumbu.example.org/notes/Work/c.md;",
            "[own](//kumbu.example.org:9/Work) [w](https://www.kumbu.example.org/Work) [https://kumbu.example.org/Work](https://example.org).",
            "",
            "Unparsed [a](Private/Plan.md and [b]",
            "",
            "``` a`b",
            "[[Private/Plan.md]] [x](Private/Plan.md)",
            "```");
        var copy = FairCopies.Make(Note, content, _ => null, _ => true, "https://kumbu.example.org");
        foreach (var leak in new[] { "Private/", "Plan.md", "obsidian", "file:", "Work", "[[" })
        {
            Assert.DoesNotContain(leak, copy.Markdown, StringComparison.Ordinal);
        }

        Assert.Empty(copy.Files);
    }

    [Theory]
    [InlineData(20000)]
    public void Deep_nesting_finishes_quickly(int n)
    {
        var inputs = new[]
        {
            string.Concat(Enumerable.Repeat("[", n)) + "a" + string.Concat(Enumerable.Repeat("](x)", n)),
            string.Concat(Enumerable.Repeat("![", n)) + "a" + string.Concat(Enumerable.Repeat("](x.png)", n)),
            string.Concat(Enumerable.Repeat("[](", n)),
            string.Concat(Enumerable.Repeat("[a](x \"", n)),
            string.Concat(Enumerable.Repeat("<a x='", n)),
            string.Concat(Enumerable.Repeat("<?", n)),
            string.Concat(Enumerable.Repeat("`` ` ", n)),
            string.Concat(Enumerable.Repeat("#a ", n)) + "b",
            string.Concat(Enumerable.Repeat("[[a|[x](y)]] ", n)),
            string.Concat(Enumerable.Repeat("https://kumbu.example.org", n)),
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var input in inputs)
        {
            var copy = FairCopies.Make(Note, input, _ => null, _ => true, "https://kumbu.example.org");
            Assert.DoesNotContain("](x", copy.Markdown, StringComparison.Ordinal);
        }

        Assert.Equal("a", FairCopies.Make(Note, inputs[0], _ => null, _ => true).Markdown);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Links_in_link_text_are_plain_text()
    {
        Assert.Equal("[a b](https://example.org)", Text("[a [b](https://example.org/b)](https://example.org)"));
        Assert.Equal("a b c", Text("[a ![b](https://example.org/b.png) c](Private/Plan.md)"));
    }

    [Fact]
    public void An_autolink_of_another_scheme_goes_even_with_spaces()
    {
        Assert.Equal("a  b", Text("a <obsidian://open?vault=Work&file=Private Plan> b"));
        // Read as an autolink, it binds more tightly than the brackets around it.
        Assert.Equal("[a ](https://example.org)", Text("[a <obsidian://x y]>](https://example.org)"));
    }

    [Fact]
    public void Footnotes_stay() => Assert.Equal("Note[^1].\n\n[^1]: A footnote.", Text("Note[^1].\n\n[^1]: A footnote."));

    [Fact]
    public void The_title_is_plain_text()
    {
        var copy = Copy("# See [Router docs](https://example.org/router) ![[map.png]] ![c](https://example.org/c.png)\n\n![[flow.png]]");
        Assert.Equal("See Router docs map.png c", copy.Title);
        Assert.Equal("![flow.png](f/0)", copy.Markdown);
        Assert.Equal(["map.png"], copy.Plain);
        Assert.Equal([P("/Work/Routing/flow.png")], copy.Files);
    }

    [Fact]
    public void A_tag_right_after_html_keeps_its_word()
    {
        Assert.Equal("x tag y", Text("x <b>#tag</b> y"));
        Assert.Equal("x tag", Text("x <b>#tag</b>"));
    }

    [Fact]
    public void A_line_left_with_its_marker_alone_goes()
    {
        Assert.Equal("- a\n- b", Text("- a\n- #todo\n- b"));
        Assert.Equal("b", Text("- [ ] %%x%%\nb"));
        Assert.Equal("b", Text("> #todo\n\nb"));
        Assert.Equal("1. a", Text("1. a\n2. <br>"));
        Assert.Equal("-\n- a", Text("-\n- a"));
    }

    [Fact]
    public void No_blank_line_leads()
    {
        Assert.Equal("Text", Text("%% x %%\n\nText"));
        Assert.Equal("Text", Text("---\na: b\n---\n\n#todo\n\nText"));
    }

    [Fact]
    public void The_safety_net_leaves_no_link_syntax()
    {
        Assert.Equal("[a", Text("[a](Private/Plan.md"));
        Assert.Equal("text", Text("[d]:\n\ntext"));
        Assert.Equal("a ", Text("a <b"));
        Assert.Equal("x \n> y", Text("x <a href=\"Private/Plan.md\"\n> y"));
        Assert.Equal("[r](https://example.org) ![x](f/0) <https://example.org>", Copy("[r](https://example.org) ![x](x.png) <https://example.org>").Markdown);
    }

    [Fact]
    public void Code_stays_as_written()
    {
        Assert.Equal("```\n[[Secret]] #tag %%c%%\n```", Text("```\n[[Secret]] #tag %%c%%\n```"));
        Assert.Equal("see `[[Secret]]` and `#tag`", Text("see `[[Secret]]` and `#tag`"));
        Assert.Equal("a\n\n    [[Secret]] <b>x</b>\nb", Text("a\n\n    [[Secret]] <b>x</b>\nb"));
    }

    [Fact]
    public void Remote_images_become_links()
    {
        Assert.Equal("[chart](https://example.org/c.png)", Text("![chart](https://example.org/c.png)"));
        Assert.Equal("[https://example.org/c.png](https://example.org/c.png)", Text("![](https://example.org/c.png)"));
    }

    [Theory]
    [InlineData("[Router docs](https://example.org/router)")]
    [InlineData("<https://example.org>")]
    [InlineData("[mail](mailto:alice@example.org)")]
    [InlineData("[r][web]\n\n[web]: https://example.org/router")]
    public void Web_links_stay(string link) => Assert.Equal(link, Text(link));

    [Fact]
    public void Links_to_the_server_itself_are_vault_links()
    {
        const string own = "https://kumbu.example.org";
        Assert.Equal("a plan b", Text("a [plan](https://kumbu.example.org/notes/Work/Private/Plan.md) b", own));
        Assert.Equal("a  b", Text("a <https://KUMBU.example.org/notes/Work/x.md> b", own));
        Assert.Equal("a shot b", Text("a ![shot](https://kumbu.example.org/api/files/Work/a.png) b", own));
        Assert.Equal("a", Text("a\n![](https://kumbu.example.org/api/files/Work/a.png)", own));
        Assert.Equal("[x](https://kumbu.example.org.evil.example/)", Text("[x](https://kumbu.example.org.evil.example/)", own));
        Assert.Equal("[x](https://evil.example/kumbu.example.org/)", Text("[x](https://evil.example/kumbu.example.org/)", own));
        // The same host is the same server, whatever the scheme or port.
        Assert.Equal("x", Text("[x](https://kumbu.example.org:8443/)", own));
        Assert.Equal("x", Text("[x](http://kumbu.example.org/)", own));
        Assert.Equal("x", Text("[x](HTTPS://Kumbu.Example.Org./a)", own));
        Assert.Equal("see .", Text("see https://kumbu.example.org/notes/Work/x.md.", own));
        Assert.Equal("see .", Text("see www.kumbu.example.org/notes/Work/x.md.", own));
        Assert.Equal("on kumbu.example.org today", Text("on kumbu.example.org today", own));
        Assert.Equal("see https://kumbu.example.org.evil.example/a", Text("see https://kumbu.example.org.evil.example/a", own));
        Assert.Equal("[x](https://kumbu.example.org/)", Text("[x](https://kumbu.example.org/)"));
    }

    [Fact]
    public void An_image_it_may_copy_is_embedded_by_number()
    {
        var one = Copy("![[flow.png]]");
        Assert.Equal("![flow.png](f/0)", one.Markdown);
        Assert.Equal([P("/Work/Routing/flow.png")], one.Files);
        Assert.Empty(one.Plain);

        var twice = Copy("![[flow.png]]\n![[flow.png|300]]");
        Assert.Equal("![flow.png](f/0)\n![flow.png](f/0)", twice.Markdown);
        Assert.Single(twice.Files);

        var two = Copy("![[flow.png]] ![map](map.png) ![](flow.png)");
        Assert.Equal("![flow.png](f/0) ![map](f/1) ![flow.png](f/0)", two.Markdown);
        Assert.Equal([P("/Work/Routing/flow.png"), P("/Work/Routing/map.png")], two.Files);
    }

    [Fact]
    public void An_attachment_it_may_not_copy_is_plain_text()
    {
        var refused = Copy("![[flow.png]] and ![[flow.png]]", mayCopy: false);
        Assert.Equal("flow.png and flow.png", refused.Markdown);
        Assert.Equal(["flow.png"], refused.Plain);
        Assert.Empty(refused.Files);

        var gone = FairCopies.Make(Note, "![[gone.png]]", _ => null, _ => true);
        Assert.Equal("gone.png", gone.Markdown);
        Assert.Equal(["gone.png"], gone.Plain);

        Assert.Equal("report.pdf", Copy("![[report.pdf]]", mayCopy: false).Markdown);
        Assert.Equal("the map", Copy("![the map](map.png)", mayCopy: false).Markdown);
        Assert.Equal("map.png", Copy("![](map.png)", mayCopy: false).Markdown);
    }

    [Fact]
    public void Raw_html_goes_its_text_stays() => Assert.Equal("a b c", Text("a <span style=\"x\">b</span> c"));

    [Fact]
    public void The_example_of_spec_6()
    {
        const string note = """
            ---
            status: draft
            ---
            # Questions on routing #routing

            1. Should the chatbot move the user between screens? See [[Routing master]].
            2. Is there a list of routes? [Router docs](https://example.org/router)
            %% ask about deep links later %%
            ![[flow.png]]
            """;
        var copy = Copy(note);
        Assert.Equal("Questions on routing", copy.Title);
        Assert.Equal("""
            1. Should the chatbot move the user between screens? See Routing master.
            2. Is there a list of routes? [Router docs](https://example.org/router)
            ![flow.png](f/0)
            """, copy.Markdown);
        Assert.Equal([P("/Work/Routing/flow.png")], copy.Files);
    }
}
