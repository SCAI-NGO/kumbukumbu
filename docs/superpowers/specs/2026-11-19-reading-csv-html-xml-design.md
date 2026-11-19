# Reading CSV, HTML and XML

**Status:** approved by Chris on 2026-11-19 ("spec passt so"). Version 0.6.0, track B, part 3 of the brief (`/SWE/Kumbu/Internal Roadmap/0.6.0 tracks/Track B Files.md`). Builds on every file type (spec `2026-11-18-files-design.md`): a CSV, HTML or XML file is a text file there when it is UTF-8 up to 15 MB, and `cat` and `rg` read it already.

## Goal

Agents read structured files the way they read notes: a range of a spreadsheet's rows and columns, the elements of a page that a CSS selector picks, the nodes of an XML document that an XPath picks, without pulling the whole file through `cat` and parsing it in their head.

Why (brief, power user feedback of 2026-11-16): agents working on a CSV over MCP is Kumbu²'s strength; `cat` of a 4 MB CSV is cut at the output limit, and a page's HTML is mostly markup. Writing comes later (CSV cells with a tool, then the DOM), once reading has proven itself.

## Decisions

1. **Three commands named after their formats: `csv`, `html`, `xml`.** Each takes one path (a text file, or a piped input for `csv`), follows the `sh` conventions (options as in `head`/`rg`, errors as `<command>: <path>: …`, the output limit with a closing line on how to narrow it), and is listed in `help` and the manual. Why three and not one `query`: each format has its own selector language, and a command named after the format is what an agent guesses first.
2. **Only text files.** A file kept as bytes (not UTF-8, over 15 MB) gets "csv: costs.csv is kept as bytes (not UTF-8 text); Kumbu² can't read it as CSV". A note (`.md`) is refused too: its tables are read with `cat`. No extension check: a `.txt` holding CSV is read as CSV when asked.

### `csv`

3. **Without options, an overview:** the dialect found (separator, quoting, header or none), the number of rows and columns, the header, and the first 10 rows as a Markdown table. Why: the first thing an agent needs is the shape.
4. **Ranges by rows and columns, or as a spreadsheet range.** `--rows 2-20` (data rows, 1-based, the header not counted; `5`, `5-`, `-20` as in `cut`), `--cols Date,Amount` (by header name, case-sensitive, or by letter `B,D` or number `2,4`), or one `A1` range `B2:D20` counted as a spreadsheet counts (row 1 is the header line when there is one). Output a Markdown table with the header, escaped (`|` and line breaks inside cells). `--tsv` prints tab-separated values instead, for piping into `sort`, `wc`, `rg`.
5. **Dialect by sniffing, overridable.** The separator is the one of `,`, `;`, tab and `|` that splits the first 20 lines into the most consistent count of fields; `--sep ';'` overrides it. RFC 4180 quoting (double quotes, doubled inside), a leading BOM dropped, CRLF or LF. A header is assumed when the first row has no empty cell and none of its cells is a number while the second row's are; `--header` and `--no-header` override. Why `;` matters: Excel in German locales writes `;` CSV.
6. **Streaming over the parsed rows, bounded.** A text file is at most 15 MB, so it is read whole and parsed row by row; the output is cut at the shell's limit with "csv: 20 of 48,113 rows shown; narrow with --rows or --cols". A malformed row (an unclosed quote) is reported with its line number rather than guessed.

### `html`

7. **CSS selectors through AngleSharp** (MIT licence, compatible with the AGPL; the de-facto HTML5 parser for .NET, `AngleSharp.Css` not needed for selectors). `html page.html 'table.costs tr'` prints each matched element's text, one per line, whitespace collapsed; `--html` prints the outer HTML; `--attr href` prints one attribute's value; `--count`. Without a selector: the title, the headings as an outline, and the number of links, tables and forms. A matched `<table>` prints as a Markdown table.
8. **Nothing runs and nothing loads.** AngleSharp is used without a scripting engine and without a loader (its defaults: no `Configuration.Default.WithDefaultLoader()`), so no `<script>`, `<link>`, `<img>` or `<iframe>` source is fetched: no SSRF, no script. A test asserts that a page referring to a local server makes no request (as the SVG spec's test does for the sidecar).

### `xml`

9. **XPath 1.0 through .NET's own `XPathDocument`**, read with an `XmlReader` with `DtdProcessing.Prohibit`, `XmlResolver = null` and a `MaxCharactersFromEntities` of 0 (hard: no XXE, no entity expansion, no external resource). A document with a DTD is refused: "xml: feed.xml has a DTD, which Kumbu² doesn't process". `xml feed.xml '//item/title'` prints each node's string value, one per line; `--xml` prints the outer XML; `--count`. Namespaces: `--ns a=http://www.w3.org/2005/Atom` (several), and the document's default namespace is offered as the prefix `d` when it has one, with a hint when a query without prefixes finds nothing in a namespaced document. Without a query: the root element, its namespaces and the element names below it with their counts, two levels deep.
   Why refuse a DTD rather than ignore it as the SVG check does: an SVG is shown as a picture, where an undeclared entity only breaks well-formedness; an XML document is read for its values, and an entity silently left out would give wrong values.

## Out

- Writing (CSV cells, DOM edits): later, once reading has proven itself (brief).
- JSON (`jq`-like): not asked for; `cat` reads JSON, and agents parse it well.
- Office files (`.xlsx`, `.docx`): kept opaque; text extraction is a later, separate decision (brief).
- Non-UTF-8 CSV (Windows-1252): stays bytes (files spec, decision 3).
- XPath 2.0/3.1, XSLT, XQuery.

## Touch points

`src/Kumbu.Core/Sh/Commands/` (`Csv.cs`, `Html.cs`, `Xml.cs`, registered where the shell's commands are), `Help.cs`, `Manual.cs`, the console's completion of command names (`web/src/console/`), `src/Kumbu.Core/Kumbu.Core.csproj` (AngleSharp), `THIRD-PARTY-NOTICES.md`, `README.md` (the `sh` list), `docs/security-architecture.md` (HTML: nothing runs or loads; XML: no DTD).

## Size

A few hours of building with tests (subagent-driven, most of it waiting on CI): CSV is the largest part (dialects, ranges, the table output), HTML and XML are thin layers over their parsers.
