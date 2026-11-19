// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>xml</c>: a document's shape, and the nodes an XPath picks; a document with a DTD is refused, so no entity
/// is expanded and nothing outside the file is read.</summary>
public class XmlTests
{
    private const string Atom = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom" xmlns:media="http://search.yahoo.com/mrss/">
          <title>Garden</title>
          <entry>
            <title>Roses</title>
            <link href="https://example.org/roses"/>
            <media:thumbnail url="r.png"/>
          </entry>
          <entry><title>Tulips</title><link href="https://example.org/tulips"/></entry>
        </feed>
        """;

    private const string Rss = """
        <rss version="2.0">
          <channel>
            <title>News</title>
            <item>
              <title>A   first
                story</title>
              <link>https://a.example</link>
              <guid isPermaLink="false">1</guid>
            </item>
            <item><title>B</title><link>https://b.example</link></item>
          </channel>
        </rss>
        """;

    private readonly InMemoryNoteStore _store = new(("/Work/plan.md", "# Plan\n"));
    private readonly Shell _sh;

    public XmlTests()
    {
        _store.AddTextFile("/Work/atom.xml", Atom);
        _store.AddTextFile("/Work/rss.xml", Rss);
        _sh = TestShell.Of(_store);
    }

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    private async Task<ShellResult> Xml(string name, string content, string query = "")
    {
        _store.AddTextFile($"/Work/{name}", content);
        return await Run($"xml /Work/{name} {query}");
    }

    [Fact]
    public async Task Without_a_query_it_prints_the_root_its_namespaces_and_two_levels_of_elements()
    {
        Assert.Equal("""
            root: feed
            namespaces: http://www.w3.org/2005/Atom (the default: name its elements as d:<name>), media=http://search.yahoo.com/mrss/
              title 1
              entry 2
                title 2
                link 2
                media:thumbnail 1
            """, (await Run("xml /Work/atom.xml")).Output);
        Assert.Equal("""
            root: rss
            namespaces: none
              channel 1
                title 1
                item 2
            """, (await Run("xml /Work/rss.xml")).Output);
    }

    [Fact]
    public async Task A_query_prints_each_nodes_string_value_on_a_line()
    {
        Assert.Equal("A first story\nB", (await Run("xml /Work/rss.xml //item/title")).Output);
        Assert.Equal("false", (await Run("xml /Work/rss.xml //guid/@isPermaLink")).Output);
        Assert.Equal("B", (await Run("xml /Work/rss.xml '//item[2]/title/text()'")).Output);
        Assert.Equal("2.0", (await Run("xml /Work/rss.xml /rss/@version")).Output);
    }

    [Theory]
    [InlineData("'count(//item)'", "2")]
    [InlineData("'string(//channel/title)'", "News")]
    [InlineData("'count(//item) > 1'", "true")]
    [InlineData("'1 div 4'", "0.25")]
    public async Task A_number_boolean_or_string_result_prints_as_it_is(string query, string expected)
    {
        Assert.Equal(expected, (await Run($"xml /Work/rss.xml {query}")).Output);
    }

    [Fact]
    public async Task Xml_prints_the_outer_xml_and_count_the_number_of_nodes()
    {
        Assert.Equal("<item><title>B</title><link>https://b.example</link></item>", (await Run("xml --xml /Work/rss.xml '//item[2]'")).Output);
        Assert.Equal("<entry xmlns=\"http://www.w3.org/2005/Atom\"><title>Tulips</title><link href=\"https://example.org/tulips\" /></entry>", (await Run("xml --xml /Work/atom.xml '//d:entry[2]'")).Output);
        Assert.Equal("isPermaLink=\"false\"", (await Run("xml --xml /Work/rss.xml //guid/@isPermaLink")).Output);
        Assert.Equal("2", (await Run("xml --count /Work/rss.xml //item")).Output);
        Assert.Equal("0", (await Run("xml --count /Work/rss.xml //nothing")).Output);
        Assert.Equal("xml: --count needs an XPath that picks nodes; 'count(//item)' gives a number", (await Run("xml --count /Work/rss.xml 'count(//item)'")).Output);
    }

    [Fact]
    public async Task The_default_namespace_is_the_prefix_d_and_others_come_with_ns()
    {
        Assert.Equal("Roses\nTulips", (await Run("xml /Work/atom.xml //d:entry/d:title")).Output);
        Assert.Equal("r.png", (await Run("xml /Work/atom.xml //media:thumbnail/@url")).Output);
        Assert.Equal("Garden", (await Run("xml --ns a=http://www.w3.org/2005/Atom /Work/atom.xml /a:feed/a:title")).Output);
        Assert.Equal("https://example.org/roses", (await Run("xml --ns a=http://www.w3.org/2005/Atom --ns m=urn:none /Work/atom.xml '//a:entry[1]/a:link/@href'")).Output);
        Assert.Equal("xml: --ns takes prefix=uri (got 'atom')", (await Run("xml --ns atom /Work/atom.xml //x")).Output);
        Assert.Equal("xml: --ns can't define the prefix xml, which XML reserves", (await Run("xml --ns xml=urn:x /Work/atom.xml //x")).Output);
        Assert.Equal("xml: '//x:entry' uses a prefix that isn't defined; define it with --ns prefix=uri", (await Run("xml /Work/atom.xml //x:entry")).Output);
    }

    [Fact]
    public async Task A_query_without_prefixes_that_finds_nothing_in_a_namespaced_document_says_why()
    {
        Assert.Equal(
            "xml: nothing matched; the document's default namespace is http://www.w3.org/2005/Atom, so name its elements as d:<name> (//d:title)",
            (await Run("xml /Work/atom.xml //entry/title")).Output);
        Assert.Equal("xml: nothing matched '//d:nothing'", (await Run("xml /Work/atom.xml //d:nothing")).Output);
        Assert.Equal("xml: nothing matched '//nothing'", (await Run("xml /Work/rss.xml //nothing")).Output);
        Assert.Equal("0", (await Run("xml /Work/atom.xml //entry | wc -l")).Output.Trim());
    }

    [Fact]
    public async Task An_invalid_xpath_is_refused()
    {
        var result = await Run("xml /Work/rss.xml '//['");

        Assert.True(result.IsError);
        Assert.Equal("xml: not an XPath expression: '//['", result.Output);
        Assert.Equal("xml: not an XPath expression: 'foo(//item)'", (await Run("xml /Work/rss.xml 'foo(//item)'")).Output);
    }

    [Fact]
    public async Task A_document_that_isnt_well_formed_is_refused_with_its_line()
    {
        Assert.Equal(
            "xml: /Work/bad.xml isn't well-formed XML: the 'a' start tag on line 2 position 2 does not match the end tag of 'r' (line 2)",
            (await Xml("bad.xml", "<r>\n<a></r>")).Output);
        Assert.StartsWith("xml: /Work/undeclared.xml isn't well-formed XML: reference to undeclared entity 'x'", (await Xml("undeclared.xml", "<r>&x;</r>")).Output);
    }

    [Fact]
    public async Task Predefined_entities_and_character_references_are_read()
    {
        Assert.Equal("a & b < A", (await Xml("amp.xml", "<r>a &amp; b &lt; &#65;</r>", "/r")).Output);
        Assert.Equal("a x y", (await Xml("mixed.xml", "<p>a <b>x</b> <i>y</i></p>", "/p")).Output);
    }

    [Theory]
    [InlineData("<!DOCTYPE r [<!ELEMENT r ANY>]><r/>")]
    [InlineData("<?xml version=\"1.0\"?>\n<!-- a comment first -->\n<!DOCTYPE r><r/>")]
    [InlineData("<!DOCTYPE r [<!ENTITY x \"y\">]><r>&x;</r>")]
    public async Task A_document_with_a_dtd_is_refused(string content)
    {
        var expected = "xml: /Work/dtd.xml has a DTD, which Kumbu² doesn't process";
        Assert.Equal(expected, (await Xml("dtd.xml", content)).Output);
        Assert.Equal(expected, (await Run("xml /Work/dtd.xml /r")).Output);
    }

    [Fact]
    public async Task An_external_entity_to_a_local_file_is_never_read()
    {
        var secret = Path.GetTempFileName();
        File.WriteAllText(secret, "top-secret-value");
        try
        {
            var output = (await Xml("xxe.xml", $"<!DOCTYPE r [<!ENTITY x SYSTEM \"file://{secret}\">]><r>&x;</r>", "/r")).Output;
            Assert.Equal("xml: /Work/xxe.xml has a DTD, which Kumbu² doesn't process", output);
            Assert.DoesNotContain("top-secret", output);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public async Task Nothing_outside_the_file_is_fetched()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var at = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            Assert.Contains("has a DTD", (await Xml("a.xml", $"<!DOCTYPE r SYSTEM \"{at}/r.dtd\"><r/>")).Output);
            Assert.Contains("has a DTD", (await Xml("b.xml", $"<!DOCTYPE r [<!ENTITY % remote SYSTEM \"{at}/x.dtd\"> %remote;]><r/>", "/r")).Output);
            Assert.Contains("has a DTD", (await Xml("c.xml", $"<!DOCTYPE r [<!ENTITY x SYSTEM \"{at}/x.txt\">]><r>&x;</r>", "/r")).Output);
            // A schema location is a name, never fetched.
            Assert.Equal("1", (await Xml("d.xml", $"<r xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"{at}/r.xsd\">1</r>", "/r")).Output);

            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Billion_laughs_is_refused_at_once()
    {
        var dtd = new StringBuilder("<!DOCTYPE r [<!ENTITY lol \"lol\">");
        for (var i = 1; i <= 9; i++) dtd.Append($"<!ENTITY lol{i} \"{string.Concat(Enumerable.Repeat($"&lol{(i == 1 ? "" : i - 1)};", 10))}\">");
        dtd.Append("]>");
        var started = Stopwatch.GetTimestamp();

        Assert.Equal("xml: /Work/lol.xml has a DTD, which Kumbu² doesn't process", (await Xml("lol.xml", $"{dtd}<r>&lol9;</r>", "/r")).Output);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("//*[count(//*) > 0]")]
    [InlineData("count(//*[count(//*) > 0])")]
    [InlineData("//*[string-length(string(/)) > 0]")]
    public async Task An_xpath_stops_at_the_time_budget(string xpath)
    {
        // Each of these walks the whole document once for every element: quadratic.
        _store.AddTextFile("/Work/big.xml", $"<r>{string.Concat(Enumerable.Repeat("<a>x</a>", 100_000))}</r>");
        _sh.Register(new Kumbu.Core.Sh.Commands.Xml(_store) { Budget = TimeSpan.FromMilliseconds(100) });
        var started = Stopwatch.GetTimestamp();

        Assert.Equal(
            ShellResult.Error($"xml: '{xpath}' took longer than 0.1 seconds on /Work/big.xml; narrow the XPath"),
            await Run($"xml /Work/big.xml '{xpath}'"));
        // Unbounded, this takes minutes.
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Printing_the_xml_of_nested_nodes_stops_at_the_output_limit()
    {
        // Each element's XML holds all below it: printed whole for every one, 20,000 nested elements would be 4 GB.
        _store.AddTextFile("/Work/deep.xml", string.Concat(Enumerable.Repeat("<a>", 20_000)) + string.Concat(Enumerable.Repeat("</a>", 20_000)));
        var started = Stopwatch.GetTimestamp();

        var output = (await Run("xml --xml /Work/deep.xml '//a'")).Output;

        Assert.EndsWith("\nxml: the first of 20,000 nodes is cut at the output limit; narrow the XPath", output);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Many_nodes_are_cut_with_a_line_that_says_how_many()
    {
        var items = string.Concat(Enumerable.Range(1, 5000).Select(i => $"<item>item number {i} of the long list</item>"));

        var output = (await Xml("long.xml", $"<list>{items}</list>", "//item")).Output;

        Assert.True(output.Length <= OutputLimit.MaxChars, output.Length.ToString());
        Assert.StartsWith("item number 1 of the long list\n", output);
        Assert.Matches(@"\nxml: [\d,]+ of 5,000 nodes shown; narrow the XPath$", output);
    }

    [Fact]
    public async Task Only_text_files_are_read()
    {
        _store.AddAttachment("/Work/shot.png");

        Assert.Equal("xml: /Work/shot.png is not a text file ('cat /Work/shot.png' says what it is); Kumbu² reads XML from text files only", (await Run("xml /Work/shot.png")).Output);
        Assert.Equal("xml: /Work/plan.md is a note; read it with cat", (await Run("xml /Work/plan.md")).Output);
        Assert.Equal("xml: /Work: Is a directory", (await Run("xml /Work")).Output);
        Assert.StartsWith("xml: /Work/missing.xml: No such file or directory", (await Run("xml /Work/missing.xml")).Output);
        Assert.Equal("xml: missing operand (give the path of an XML file)", (await Run("xml")).Output);
        Assert.Equal("xml: choose one of --xml and --count", (await Run("xml --xml --count /Work/rss.xml //item")).Output);
        Assert.Equal("xml: --xml needs an XPath", (await Run("xml --xml /Work/rss.xml")).Output);
    }

    [Fact]
    public async Task Help_and_the_manual_name_it()
    {
        var help = (await Run("help xml")).Output;
        Assert.StartsWith("xml [--xml|--count] [--ns prefix=uri]… <path> [xpath]", help);
        Assert.Contains("\n  xml /Work/feed.xml '//item/title'", help);
        Assert.Contains("\n  xml ", (await Run("help")).Output);
        Assert.Contains("\n  xml ", Manual.ToolDescription);
    }
}
