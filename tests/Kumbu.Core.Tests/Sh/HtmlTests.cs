// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>html</c>: a page's outline, and the elements a CSS selector picks; nothing it refers to is fetched or run.</summary>
public class HtmlTests
{
    private const string Page = """
        <!doctype html>
        <html><head><title>  Costs
          2026 </title></head>
        <body>
          <h1>Costs</h1>
          <p>Intro with <a href="/a">one</a> and <a href="https://example.org/b" class="ext">two</a> and <a>no href</a>.</p>
          <h2>Spring</h2>
          <h3>  April
            detail </h3>
          <table class="costs">
            <thead><tr><th>Item</th><th>Amount</th></tr></thead>
            <tbody>
              <tr><td>Rose | red</td><td>3</td></tr>
              <tr><td>Tulip</td><td>  12 </td></tr>
            </tbody>
          </table>
          <h2>Summer</h2>
          <form action="/f"><input name="q"></form>
        </body></html>
        """;

    private readonly InMemoryNoteStore _store = new(("/Work/plan.md", "# Plan\n"));
    private readonly Shell _sh;

    public HtmlTests()
    {
        _store.AddTextFile("/Work/page.html", Page);
        _sh = TestShell.Of(_store);
    }

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    [Fact]
    public async Task Without_a_selector_it_prints_the_title_the_headings_and_counts()
    {
        var result = await Run("html /Work/page.html");

        Assert.False(result.IsError, result.Output);
        Assert.Equal("""
            title: Costs 2026
            h1 Costs
              h2 Spring
                h3 April detail
              h2 Summer
            2 links, 1 table, 1 form
            """, result.Output);
    }

    [Fact]
    public async Task A_page_without_title_or_headings_says_so()
    {
        _store.AddTextFile("/Work/bare.html", "<p>just text</p>");

        Assert.Equal("title: (none)\n(no headings)\n0 links, 0 tables, 0 forms", (await Run("html /Work/bare.html")).Output);
    }

    [Fact]
    public async Task A_selector_prints_each_elements_text_on_a_line()
    {
        Assert.Equal("one\ntwo\nno href", (await Run("html /Work/page.html a")).Output);
        Assert.Equal("April detail", (await Run("html /Work/page.html h3")).Output);
        Assert.Equal("Rose | red\nTulip", (await Run("html /Work/page.html 'table.costs td:first-child'")).Output);
    }

    [Fact]
    public async Task Html_prints_the_outer_html()
    {
        Assert.Equal("<a href=\"https://example.org/b\" class=\"ext\">two</a>", (await Run("html --html /Work/page.html a.ext")).Output);
    }

    [Fact]
    public async Task Attr_prints_one_attribute_and_skips_elements_without_it()
    {
        Assert.Equal("/a\nhttps://example.org/b", (await Run("html --attr href /Work/page.html a")).Output);
        Assert.Equal("html: 1 element matched 'h1', none with the attribute href", (await Run("html --attr href /Work/page.html h1")).Output);
        Assert.Equal("0", (await Run("html --attr href /Work/page.html h1 | wc -l")).Output.Trim());
    }

    [Fact]
    public async Task Count_prints_how_many_matched()
    {
        Assert.Equal("3", (await Run("html --count /Work/page.html a")).Output);
        Assert.Equal("0", (await Run("html --count /Work/page.html video")).Output);
    }

    [Fact]
    public async Task A_matched_table_prints_as_a_markdown_table()
    {
        Assert.Equal("""
            | Item | Amount |
            | --- | --- |
            | Rose \| red | 3 |
            | Tulip | 12 |
            """, (await Run("html /Work/page.html table")).Output);
    }

    [Fact]
    public async Task A_table_takes_its_header_from_a_first_row_of_th_or_leaves_it_empty()
    {
        _store.AddTextFile("/Work/t.html", """
            <table id="th"><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>
            <table id="none"><tr><td>1</td><td>2</td><td>3</td></tr><tr><td>4</td></tr></table>
            """);

        Assert.Equal("| A | B |\n| --- | --- |\n| 1 | 2 |", (await Run("html /Work/t.html '#th'")).Output);
        Assert.Equal("|  |  |  |\n| --- | --- | --- |\n| 1 | 2 | 3 |\n| 4 |  |  |", (await Run("html /Work/t.html '#none'")).Output);
        // Two tables: a blank line between them.
        Assert.Contains("| 1 | 2 |\n\n|  |  |  |", (await Run("html /Work/t.html table")).Output);
    }

    [Fact]
    public async Task Nothing_matched_says_so_when_it_stands_alone()
    {
        Assert.Equal("html: nothing matched 'video'", (await Run("html /Work/page.html video")).Output);
        Assert.Equal("0", (await Run("html /Work/page.html video | wc -l")).Output.Trim());
    }

    [Theory]
    [InlineData("'a[href'")]
    [InlineData("'!!'")]
    public async Task An_invalid_selector_is_refused(string selector)
    {
        var result = await Run($"html /Work/page.html {selector}");

        Assert.True(result.IsError);
        Assert.Equal($"html: not a CSS selector: '{selector.Trim('\'')}'", result.Output);
    }

    [Fact]
    public async Task Options_that_need_a_selector_or_exclude_each_other_are_refused()
    {
        Assert.Equal("html: --count needs a selector", (await Run("html --count /Work/page.html")).Output);
        Assert.Equal("html: choose one of --html, --attr and --count", (await Run("html --html --count /Work/page.html a")).Output);
        Assert.Equal("html: missing operand (give the path of an HTML file)", (await Run("html")).Output);
        Assert.StartsWith("html: too many arguments", (await Run("html /Work/page.html a b")).Output);
    }

    [Fact]
    public async Task Many_matches_are_cut_with_a_line_that_says_how_many()
    {
        var items = string.Concat(Enumerable.Range(1, 5000).Select(i => $"<li>item number {i} of the long list</li>"));
        _store.AddTextFile("/Work/long.html", $"<ul>{items}</ul>");

        var output = (await Run("html /Work/long.html li")).Output;

        Assert.True(output.Length <= OutputLimit.MaxChars, output.Length.ToString());
        Assert.StartsWith("item number 1 of the long list\n", output);
        Assert.Matches(@"\nhtml: [\d,]+ of 5,000 elements shown; narrow the selector$", output);
    }

    [Fact]
    public async Task Only_text_files_are_read()
    {
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var files = new AttachmentWriter(_store, _store, time, new FixedCaller(Writers.Dev));
        Assert.False((await files.WriteAsync("/Work/page.bin", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0xFF }, null, null, CancellationToken.None)).Result.IsError);
        _store.AddAttachment("/Work/shot.png");

        Assert.Equal("html: /Work/page.bin is kept as bytes (not UTF-8 text); Kumbu² can't read it as HTML", (await Run("html /Work/page.bin")).Output);
        Assert.Equal("html: /Work/shot.png is not a text file ('cat /Work/shot.png' says what it is); Kumbu² reads HTML from text files only", (await Run("html /Work/shot.png")).Output);
        Assert.Equal("html: /Work/plan.md is a note; read it with cat", (await Run("html /Work/plan.md")).Output);
        Assert.Equal("html: /Work: Is a directory", (await Run("html /Work")).Output);
        Assert.StartsWith("html: /Work/missing.html: No such file or directory", (await Run("html /Work/missing.html")).Output);
    }

    [Fact]
    public async Task Nothing_a_page_refers_to_is_fetched()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var at = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            _store.AddTextFile("/Work/refs.html", $"""
                <html><head>
                <link rel="stylesheet" href="{at}/style.css">
                <script src="{at}/app.js"></script>
                <script>document.write('<img src="{at}/written.png">'); fetch('{at}/fetched');</script>
                </head><body>
                <h1>Refs</h1>
                <img src="{at}/pic.png">
                <iframe src="{at}/frame.html"></iframe>
                <object data="{at}/object.swf"></object>
                <a href="{at}/link">link</a>
                </body></html>
                """);

            Assert.Equal("title: (none)\nh1 Refs\n1 link, 0 tables, 0 forms", (await Run("html /Work/refs.html")).Output);
            Assert.Equal($"{at}/pic.png", (await Run("html --attr src /Work/refs.html img")).Output);
            Assert.Equal("1", (await Run("html --count /Work/refs.html iframe")).Output);
            Assert.False((await Run("html --html /Work/refs.html body")).IsError);
            Assert.False((await Run("html /Work/refs.html 'link, script, object'")).IsError);
            // A script that ran would have written a second image.
            Assert.Equal("1", (await Run("html --count /Work/refs.html img")).Output);

            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("table.costs tr")]
    [InlineData("body > p a")]
    [InlineData("h2 + h3")]
    [InlineData("h1 ~ h2")]
    [InlineData("h1 ~ table td")]
    [InlineData("a, h1, h3")]
    [InlineData("thead th, tbody td")]
    [InlineData("tbody tr:nth-child(2) td")]
    [InlineData("p a:not(.ext)")]
    [InlineData("tr:has(> td)")]
    [InlineData(":is(h2, h3) ~ h2")]
    [InlineData("html body h1")]
    [InlineData("body * a")]
    [InlineData("tr:has(> td:not(.x))")]
    [InlineData("tbody:has(> tr > :is(td, th))")]
    [InlineData("p:has(> a[href^=\"http\"])")]
    [InlineData("body:has(:is(#none, h1))")]
    public async Task A_selector_picks_what_AngleSharp_picks(string selector)
    {
        var expected = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(Page).QuerySelectorAll(selector).Select(e => e.OuterHtml);

        Assert.Equal(string.Join('\n', expected), (await Run($"html --html /Work/page.html '{selector}'")).Output);
    }

    [Fact]
    public async Task Combinators_take_time_linear_in_the_page()
    {
        // AngleSharp alone tries every ancestor for each space: on 1,000 nested divs this takes minutes.
        _store.AddTextFile("/Work/deep.html", string.Concat(Enumerable.Repeat("<div>", 1000)));
        var started = Stopwatch.GetTimestamp();

        Assert.Equal("html: nothing matched 'p div div div div'", (await Run("html /Work/deep.html 'p div div div div'")).Output);
        Assert.Equal("998", (await Run("html --count /Work/deep.html 'div div div'")).Output);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("ul:has(li:has(b))", ":has() inside :has()")]
    [InlineData("ul:has(li:nth-child(2))", "inside :has(), :nth-child()")]
    [InlineData("ul:has(li:only-child)", "inside :has(), :only-child")]
    [InlineData("ul:has(:not(li:first-of-type))", "inside :has(), :first-of-type")]
    [InlineData("tr:has(td a)", "inside :has() only >")]
    [InlineData("div:has(+ p)", "inside :has() only >")]
    [InlineData("a:not(p div)", "inside :not(), :is() and :where() only > and +")]
    [InlineData("a:is(p ~ b)", "inside :not(), :is() and :where() only > and +")]
    [InlineData("svg|rect", "the combinator |")]
    [InlineData("form:has(> :valid)", "inside :has(), :valid isn't matched: only type, class, id and attribute selectors are")]
    [InlineData("form:has(> :invalid)", "inside :has(), :invalid isn't matched")]
    [InlineData("p:has(> a:lang(en))", "inside :has(), :lang() isn't matched")]
    [InlineData("p:has(> :is(a:checked))", "inside :has(), :checked isn't matched")]
    [InlineData("p:has(> a::before)", "inside :has(), ::before isn't matched")]
    [InlineData("p:has(> a, > b)", "inside :has(), a list (a, b) isn't matched")]
    [InlineData("li:nth-child(2n of p b)", "inside :nth-child() only > and +")]
    [InlineData("> a", "a selector can't start with >")]
    [InlineData(" + a", "a selector can't start with +")]
    [InlineData("~ a", "a selector can't start with ~")]
    public async Task Selectors_that_could_take_hours_are_refused(string selector, string why)
    {
        var result = await Run($"html /Work/page.html '{selector}'");

        Assert.True(result.IsError);
        Assert.StartsWith($"html: '{selector}': {why}", result.Output);
    }

    [Fact]
    public async Task A_selector_is_at_most_1000_characters_with_at_most_100_alternatives()
    {
        var hundred = string.Join(", ", Enumerable.Repeat("h1", 100));
        Assert.False((await Run($"html --count /Work/page.html '{hundred}'")).IsError);
        Assert.False((await Run($"html --count /Work/page.html '{new string(' ', 998)}h1'")).IsError);

        Assert.Equal(
            ShellResult.Error($"html: '{hundred}, h1': a selector has at most 100 alternatives; name fewer"),
            await Run($"html /Work/page.html '{hundred}, h1'"));
        var nested = $"p:not({string.Join(", ", Enumerable.Repeat("b", 60))}):is({string.Join(", ", Enumerable.Repeat("a", 41))})";
        Assert.EndsWith("a selector has at most 100 alternatives; name fewer", (await Run($"html /Work/page.html '{nested}'")).Output);
        var tooLong = $"{new string(' ', 999)}h1";
        Assert.Equal(ShellResult.Error($"html: '{tooLong}': a selector is at most 1,000 characters long"), await Run($"html /Work/page.html '{tooLong}'"));
    }

    [Fact]
    public async Task Matching_stops_at_the_time_budget()
    {
        // :nth-child counts the siblings before each element: quadratic in a long list. The budget covers parsing too,
        // which takes a fraction of a second here, and several on a loaded machine: a shorter budget can run out there.
        _store.AddTextFile("/Work/flat.html", $"<ul>{string.Concat(Enumerable.Repeat("<li>x</li>", 100_000))}</ul>");
        _sh.Register(new Html(_store) { Budget = TimeSpan.FromSeconds(5) });
        var started = Stopwatch.GetTimestamp();

        Assert.Equal(
            ShellResult.Error("html: 'li:nth-child(2n)' took longer than 5 seconds on /Work/flat.html; narrow the selector or the page"),
            await Run("html /Work/flat.html 'li:nth-child(2n)'"));
        // Unbounded, this takes about half a minute.
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Parsing_stops_at_the_time_budget()
    {
        // The tree builder looks through every open element for each tag: quadratic in the depth.
        _store.AddTextFile("/Work/deep.html", string.Concat(Enumerable.Repeat("<div>", 60_000)));
        _sh.Register(new Html(_store) { Budget = TimeSpan.FromMilliseconds(100) });
        var started = Stopwatch.GetTimestamp();

        Assert.Equal(ShellResult.Error("html: /Work/deep.html took longer than 0.1 seconds to parse; Kumbu² reads it no further"), await Run("html /Work/deep.html div"));
        // Unbounded, this takes about half a minute.
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Help_and_the_manual_name_it()
    {
        var help = (await Run("help html")).Output;
        Assert.StartsWith("html [--html|--attr <name>|--count] <path> [selector]", help);
        Assert.Contains("\n  html /Work/page.html 'table.costs'", help);
        Assert.Contains("\n  html ", (await Run("help")).Output);
        Assert.Contains("\n  html ", Manual.ToolDescription);
    }

    [Fact]
    public async Task Help_says_what_its_budget_stops()
    {
        var help = Regex.Replace((await Run("help html")).Output, @"\s+", " ");

        Assert.Equal(TimeSpan.FromSeconds(10), new Kumbu.Core.Sh.Commands.Html(_store).Budget);
        Assert.Contains("Parsing the page, and a selector's matching and printing, stop after 10 seconds", help);
        Assert.Contains("are stopped by that time limit", help);
        Assert.DoesNotContain("A call stops after 10 seconds", help);
    }
}
