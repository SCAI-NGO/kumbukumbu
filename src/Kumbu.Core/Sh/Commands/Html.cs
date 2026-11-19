// SPDX-License-Identifier: AGPL-3.0-or-later
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// Reads an HTML file (spec 2026-11-19, decisions 7 and 8): its outline, or the elements a CSS selector picks. AngleSharp's
/// parser on its own, with no loader and no scripting engine: no <c>&lt;script&gt;</c> runs, and no source a page refers
/// to (<c>&lt;img&gt;</c>, <c>&lt;link&gt;</c>, <c>&lt;iframe&gt;</c>, <c>&lt;object&gt;</c>) is fetched.
/// </summary>
public sealed class Html(INoteReader notes) : ICommand
{
    private static readonly Option[] Spec = [new(null, "html"), new(null, "attr", TakesValue: true), new(null, "count")];

    public string Name => "html";

    public string Usage => "html [--html|--attr <name>|--count] <path> [selector]";

    public string Summary => "read an HTML file: its title and headings, or the elements a CSS selector picks";

    public string Help => """
        html [--html|--attr <name>|--count] <path> [selector]
          Read an HTML file (a text file, whatever its name). Without a selector: its title, its headings as an
          outline (h1 to h6, indented by level) and how many links, tables and forms it has.
          With a CSS selector: each element it picks, one per line, as its text with whitespace collapsed;
          a <table> prints as a Markdown table (its header from th or thead).
          --html         each element's HTML instead, the element included
          --attr <name>  the value of one attribute; elements without it are left out
          --count        how many elements the selector picks
          Nothing a page refers to is fetched and no script runs: html reads the file as it is.

          html /Work/page.html
          html /Work/page.html 'table.costs'
          html /Work/page.html 'h2, h3'
          html --attr href /Work/page.html 'a[href^="http"]'
          html --count /Work/page.html 'ul.items > li'
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        var modes = new[] { "html", "attr", "count" }.Where(options.Has).ToList();
        if (modes.Count > 1) return ShellResult.Error("html: choose one of --html, --attr and --count");
        if (options.Operands.Count == 0) return ShellResult.Error("html: missing operand (give the path of an HTML file)");
        if (options.Operands.Count > 2) return ShellResult.Error($"html: too many arguments{CommandArgs.QuoteHint}");
        if (options.Operands.Count == 1 && modes.Count == 1) return ShellResult.Error($"html: --{modes[0]} needs a selector");

        var file = await TextFiles.ReadAsync(Name, "HTML", notes, options.Operands[0], ct);
        if (file.IsError) return file;

        // No context given: the parser's default has neither a loader nor scripting, so nothing is fetched or run.
        using var document = new HtmlParser().ParseDocument(file.Output);
        if (options.Operands.Count == 1) return Overview(document, options.Operands[0]);

        var selector = options.Operands[1];
        IHtmlCollection<IElement> matched;
        try
        {
            matched = document.QuerySelectorAll(selector);
        }
        catch (DomException)
        {
            return ShellResult.Error($"html: not a CSS selector: '{selector}'");
        }

        if (options.Has("count")) return ShellResult.Ok(matched.Length.ToString());
        if (matched.Length == 0) return ShellResult.Ok("") with { NoMatchHint = $"html: nothing matched '{selector}'" };

        if (options.Value("attr") is { } name)
        {
            var values = matched.Select(e => e.GetAttribute(name)).OfType<string>().ToList();
            return values.Count > 0
                ? TextFiles.Listed(Name, values, "\n", "elements", "narrow the selector")
                : ShellResult.Ok("") with { NoMatchHint = $"html: {Elements(matched.Length)} matched '{selector}', none with the attribute {name}" };
        }

        if (options.Has("html")) return TextFiles.Listed(Name, [.. matched.Select(e => e.OuterHtml)], "\n", "elements", "narrow the selector");

        var tables = matched.Any(e => e is IHtmlTableElement);
        return TextFiles.Listed(Name, [.. matched.Select(e => e is IHtmlTableElement t ? Table(t) : TextFiles.Collapse(e.TextContent))], tables ? "\n\n" : "\n", "elements", "narrow the selector");
    }

    private static ShellResult Overview(IHtmlDocument document, string path)
    {
        var title = TextFiles.Collapse(document.Title ?? "");
        var headings = document.QuerySelectorAll("h1, h2, h3, h4, h5, h6")
            .Select(h => $"{new string(' ', 2 * (h.LocalName[1] - '1'))}{h.LocalName} {TextFiles.Collapse(h.TextContent)}").ToList();
        var counts = $"{Count(document.QuerySelectorAll("a[href]").Length, "link")}, {Count(document.QuerySelectorAll("table").Length, "table")}, {Count(document.QuerySelectorAll("form").Length, "form")}";
        var head = $"title: {(title.Length > 0 ? title : "(none)")}\n";
        if (headings.Count == 0) return ShellResult.Ok($"{head}(no headings)\n{counts}");

        // The counts stay last, below the cut line if there is one.
        var listed = TextFiles.Listed("html", headings, "\n", "headings", $"pick some with a selector (html {path} h2)", reserve: head.Length + counts.Length + 1);
        return ShellResult.Ok($"{head}{listed.Output}\n{counts}");
    }

    /// <summary>A table as Markdown: the header from thead's first row, or a first row of th only, else empty cells.</summary>
    private static string Table(IHtmlTableElement table)
    {
        var rows = table.Rows.Select(r => r.Cells.Select(c => TextFiles.Collapse(c.TextContent).Replace("|", "\\|")).ToList()).ToList();
        var first = table.Rows.FirstOrDefault();
        var hasHeader = first is not null && (first.ParentElement is IHtmlTableSectionElement { LocalName: "thead" } || first.Cells.All(c => c.LocalName == "th"));
        var header = hasHeader ? rows[0] : [];
        var body = hasHeader ? rows.Skip(1) : rows;
        var width = Math.Max(1, rows.Count == 0 ? 0 : rows.Max(r => r.Count));

        string Row(IReadOnlyList<string> cells) => "| " + string.Join(" | ", Enumerable.Range(0, width).Select(i => i < cells.Count ? cells[i] : "")) + " |";
        return string.Join('\n', [Row(header), "| " + string.Join(" | ", Enumerable.Repeat("---", width)) + " |", .. body.Select(Row)]);
    }

    private static string Elements(int n) => Count(n, "element");

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
