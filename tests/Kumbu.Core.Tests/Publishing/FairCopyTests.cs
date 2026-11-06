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
            "Autolinks <obsidian://open?vault=Work&file=Private%2FPlan> <file:///home/alice/Work/Private/Plan.md>.");
        var copy = FairCopies.Make(Note, content, _ => null, _ => true);
        foreach (var leak in new[] { "Private/", "Plan.md", "obsidian", "file:", "Work", "[[" })
        {
            Assert.DoesNotContain(leak, copy.Markdown, StringComparison.Ordinal);
        }

        Assert.Empty(copy.Files);
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
        Assert.Equal("[x](https://kumbu.example.org:8443/)", Text("[x](https://kumbu.example.org:8443/)", own));
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
