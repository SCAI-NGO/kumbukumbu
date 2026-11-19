// SPDX-License-Identifier: AGPL-3.0-or-later
// Reading CSV, HTML and XML through `sh` (spec 2026-11-19-reading-csv-html-xml-design.md, PR #260; brief, open
// point 5). Files go in through `Api.putFile`, made in the test (the hostile ones too, never as fixtures), and are
// removed again at each test's end.
//
// Promises in scope (spec decision → test):
//  1. three commands csv, html, xml, errors as `<command>: <path>: …`, each
//     listed in `help` and the manual (the sh tool's description)         → every test below; "help: csv, html and xml …"
//  2. only text files, whatever their name (a .txt read as CSV); a file
//     kept as bytes refused by all three, a note by csv                     → "csv: refuses a file kept as bytes, a note …",
//                                                                             "html and xml: refuse a file kept as bytes",
//                                                                             "csv: a .txt holding CSV …"
//  3. csv overview: dialect, rows and columns, header, the first 10 rows    → "csv: the overview …"
//  4. --rows (a range, one row, -M, N-), --cols (name, letter, number), a
//     range B2:D20, --tsv                                                   → "csv: rows, columns, a range and tab-separated …"
//  5. dialect sniffed (';' recognised, RFC 4180 quoting), --sep, --header,
//     --no-header, no header when the first row has a number, an empty cell
//     or a name twice, a leading BOM dropped                                → "csv: a ';' file …", "csv: the overview …",
//                                                                             "csv: --sep, --header, --no-header …"
//     (decision 1: csv reads piped input too                                → the same test)
//  6. a large CSV is cut at the output limit with the spec's line; an
//     unclosed quote is reported with its line                              → "csv: a large file is cut …", "csv: refuses …"
//  7. html: a selector's text, --attr, --count, --html, a table as Markdown,
//     the outline without a selector                                        → "html: a selector's text …"
//  8. nothing runs: a <script> doesn't alter what html reads                → "html: a page's scripts never run …"
//     nothing loads (no request to a server the page names): a hand test, beside the unit test
//     tests/Kumbu.Core.Tests/Sh/HtmlTests.cs (the page with app.js, document.write and fetch against a local server).
//     The stack's server does have a route out (compose.yaml:119-121, network `default`; only `pdf` is internal,
//     :157-159), but a listener of the test's is reachable from the container only at the Docker bridge's gateway,
//     which depends on the host's Docker setup, and no command of the server's fetches anything, so a "no request"
//     here would pass as well when the listener can't be reached at all: no positive control.
//  9. xml: XPath with namespaces (d for the default, --ns, two of them), the
//     hint for an unprefixed query, --xml, --count, the overview            → "xml: XPath with namespaces …"
//     a DTD refused with exactly the spec's text (XXE, entity expansion)    → "xml: a document with a DTD is refused …"
// 10. xml: an XPath over 1,000 characters, concat() over 8 arguments
//     refused, 8 allowed (eight times the text)                             → "xml: an XPath that would build …"
//     html: a selector over 1,000 characters, over 100 alternatives, one
//     starting with a combinator, a space inside :has() refused             → "html: a selector that would cost …"
//     a call stops after 10 seconds: xml on 100,000 nodes, html on 100,000
//     siblings with an expensive pseudo-class                               → "xml: … 10 seconds", "html: … 10 seconds"
// Current behaviour, not a promise: the prefixes the root element declares work without --ns (help xml says so; the
// spec names only d and --ns); "xml: XPath with namespaces …" asserts it, marked as such.
// Left out: the parse budget of html (needs a page that parses for 10 s, more CPU
// than the plan allows).
import { randomBytes } from "node:crypto";
import { encodePath, type Api } from "../support/api";
import { expect, test as base } from "./fixtures";

const test = base.extend<{ put: (path: string, content: string | Buffer) => Promise<string> }>({
  // Writes a file (or a note, for `.md`) and removes it at the test's end, whatever happened.
  put: async ({ aliceApi }, use) => {
    const written: string[] = [];
    await use(async (path, content) => {
      if (path.endsWith(".md")) await aliceApi.putNote(path, content.toString());
      else await aliceApi.putFile(path, typeof content === "string" ? Buffer.from(content, "utf8") : content);
      written.push(path);
      return path;
    });
    for (const path of written) await remove(aliceApi, path);
  },
});

async function remove(api: Api, path: string) {
  if (path.endsWith(".md")) await api.deleteNote(path);
  else {
    const response = await api.send("DELETE", `/api/files/${encodePath(path)}`);
    if (!response.ok()) throw new Error(`DELETE file ${path}: ${response.status()}`);
  }
}

/** A CSV of `rows` data rows below a header, with quoting in its first rows (RFC 4180). */
function costs(rows: number) {
  const lines = ["Date,Item,Amount,Note"];
  for (let i = 1; i <= rows; i++) {
    const note = i === 1 ? '"a, b"' : i === 2 ? '"say ""hi"""' : i === 3 ? "x | y" : `note ${i}`;
    lines.push(`2026-01-${String((i % 28) + 1).padStart(2, "0")},Item ${i},${i}.50,${note}`);
  }
  return lines.join("\n") + "\n";
}

test("csv: the overview names the dialect, the shape, the header and the first 10 rows @readers", async ({ mcp, put, folder }) => {
  const path = await put(`/Work/${folder}/costs.csv`, costs(12));
  const answer = await mcp.sh(`csv '${path}'`);
  expect(answer.isError).toBe(false);
  expect(answer.text).toBe(
    [
      "dialect: separator ',', double quotes, a header row",
      "rows: 12, columns: 4",
      "header: A Date, B Item, C Amount, D Note",
      "",
      "| Date | Item | Amount | Note |",
      "| --- | --- | --- | --- |",
      "| 2026-01-02 | Item 1 | 1.50 | a, b |",
      '| 2026-01-03 | Item 2 | 2.50 | say "hi" |',
      "| 2026-01-04 | Item 3 | 3.50 | x \\| y |",
      ...[4, 5, 6, 7, 8, 9, 10].map((i) => `| 2026-01-${String(i + 1).padStart(2, "0")} | Item ${i} | ${i}.50 | note ${i} |`),
      "",
      "first 10 of 12 rows; read more with --rows 11-60, --cols or a range such as B2:D20",
    ].join("\n"),
  );
});

test("csv: rows, columns, a range and tab-separated output @readers", async ({ mcp, put, folder }) => {
  const path = await put(`/Work/${folder}/costs.csv`, costs(12));

  const byName = await mcp.sh(`csv '${path}' --rows 2-3 --cols Item,Amount`);
  expect(byName.text).toBe("| Item | Amount |\n| --- | --- |\n| Item 2 | 2.50 |\n| Item 3 | 3.50 |");

  const byLetter = await mcp.sh(`csv '${path}' --rows -2 --cols D,B`);
  expect(byLetter.text).toBe('| Note | Item |\n| --- | --- |\n| a, b | Item 1 |\n| say "hi" | Item 2 |');

  const byNumber = await mcp.sh(`csv '${path}' --rows 5 --cols 2,4`);
  expect(byNumber.text).toBe("| Item | Note |\n| --- | --- |\n| Item 5 | note 5 |");

  // Row 1 is the header line: B2:D3 is the first two data rows.
  const range = await mcp.sh(`csv '${path}' B2:D3`);
  expect(range.text).toBe('| Item | Amount | Note |\n| --- | --- | --- |\n| Item 1 | 1.50 | a, b |\n| Item 2 | 2.50 | say "hi" |');

  const tsv = await mcp.sh(`csv '${path}' --tsv --cols Item,Amount --rows 11-`);
  expect(tsv.text).toBe("Item\tAmount\nItem 11\t11.50\nItem 12\t12.50");

  const wrong = await mcp.sh(`csv '${path}' --cols amount`);
  expect(wrong.isError).toBe(true);
  expect(wrong.text).toBe(
    "csv: --cols: no column 'amount'; the header has Date, Item, Amount, Note (names are case-sensitive), or give letters (A-D) or numbers (1-4)",
  );
});

test("csv: a ';' file is read as one, its all-text first row as the header @readers", async ({ mcp, put, folder }) => {
  const path = await put(`/Work/${folder}/people.csv`, "Name;City\r\nAlice;Berlin\r\nBob;Wien\r\n");
  const answer = await mcp.sh(`csv '${path}'`);
  expect(answer.text).toBe(
    "dialect: separator ';', no quotes, a header row\nrows: 2, columns: 2\nheader: A Name, B City\n\n| Name | City |\n| --- | --- |\n| Alice | Berlin |\n| Bob | Wien |",
  );
  expect((await mcp.sh(`csv '${path}' --cols City`)).text).toBe("| City |\n| --- |\n| Berlin |\n| Wien |");
});

test("csv: a large file is cut at the output limit with the line on how to narrow it @readers", async ({ mcp, put, folder }) => {
  const path = await put(`/Work/${folder}/large.csv`, costs(20_000));
  const answer = await mcp.sh(`csv '${path}' --rows 1-`);
  expect(answer.isError).toBe(false);
  // The table stops at a whole row, then the spec's line (decision 6); the shell's own cut never comes.
  expect(answer.text).toMatch(/\n\| 2026-\d\d-\d\d \| Item \d+ \| \d+\.50 \| note \d+ \|\n\ncsv: \d{1,3}(,\d{3})? of 20,000 rows shown; narrow with --rows or --cols$/);
  expect(answer.text).not.toContain("[truncated");
  expect(answer.text.length).toBeLessThanOrEqual(24_000);
  const shown = Number(answer.text.match(/csv: ([\d,]+) of/)![1].replace(",", ""));
  expect(answer.text.split("\n").filter((l) => l.startsWith("| 2026-")).length, "the rows said to be shown are shown").toBe(shown);
});

test("csv: refuses a file kept as bytes, a note, and a quote left open @readers", async ({ mcp, put, folder }) => {
  // 0xff never occurs in UTF-8: the file is kept as bytes.
  const bytes = await put(`/Work/${folder}/bytes.csv`, Buffer.concat([Buffer.from([0xff, 0xfe, 0x00, 0x81]), randomBytes(64)]));
  const asBytes = await mcp.sh(`csv '${bytes}'`);
  expect(asBytes.isError).toBe(true);
  expect(asBytes.text).toBe(`csv: ${bytes} is kept as bytes (not UTF-8 text); Kumbu² can't read it as CSV`);

  const note = await put(`/Work/${folder}/Table ${folder}.md`, "| a | b |\n| --- | --- |\n| 1 | 2 |\n");
  const asNote = await mcp.sh(`csv '${note}'`);
  expect(asNote.isError).toBe(true);
  expect(asNote.text).toBe(`csv: ${note} is a note; cat reads its tables`);

  const open = await put(`/Work/${folder}/open.csv`, 'a,b\n1,"open\n2,3\n');
  const unclosed = await mcp.sh(`csv '${open}'`);
  expect(unclosed.isError).toBe(true);
  expect(unclosed.text).toBe(`csv: ${open}: line 2: a quoted field isn't closed`);
});

test("csv: a .txt holding CSV is read as CSV @readers", async ({ mcp, put, folder }) => {
  const path = await put(`/Work/${folder}/export.txt`, "Name,City\nAlice,Berlin\nBob,Wien\n");
  expect((await mcp.sh(`csv '${path}'`)).text).toBe(
    "dialect: separator ',', no quotes, a header row\nrows: 2, columns: 2\nheader: A Name, B City\n\n| Name | City |\n| --- | --- |\n| Alice | Berlin |\n| Bob | Wien |",
  );
});

test("csv: --sep, --header, --no-header, the rule for a header, a byte order mark, piped input @readers", async ({ mcp, put, folder }) => {
  // A leading BOM is dropped: the first header cell is Name, not "\uFEFFName".
  const bom = await put(`/Work/${folder}/bom.csv`, "\uFEFFName,City\nAlice,Berlin\n");
  expect((await mcp.sh(`csv '${bom}' --cols Name`)).text).toBe("| Name |\n| --- |\n| Alice |");

  // '|' by --sep: sniffing would find it too, ',' given instead splits nothing.
  const piped = await put(`/Work/${folder}/piped.csv`, "Name|City\nAlice|Berlin\n");
  expect((await mcp.sh(`csv --sep '|' '${piped}' --tsv`)).text).toBe("Name\tCity\nAlice\tBerlin");
  expect((await mcp.sh(`csv --sep , '${piped}' --tsv`)).text).toBe("Name|City\nAlice|Berlin");
  const wrongSep = await mcp.sh(`csv --sep : '${piped}'`);
  expect(wrongSep.isError).toBe(true);
  expect(wrongSep.text).toBe("csv: --sep takes ',', ';', '|' or a tab ('\\t' or tab), not ':'");

  // No header when the first row has a number, an empty cell or a name twice; the columns go by letter.
  const noHeader = "dialect: separator ',', no quotes, no header (columns by letter or number)";
  for (const [name, first] of [["number", "Alice,12"], ["empty", "Alice,"], ["twice", "Alice,Alice"]]) {
    const file = await put(`/Work/${folder}/${name}.csv`, `${first}\nBob,Wien\n`);
    const answer = await mcp.sh(`csv '${file}'`);
    expect(answer.text, name).toBe(`${noHeader}\nrows: 2, columns: 2\n\n| A | B |\n| --- | --- |\n| ${first.replace(",", " | ")} |\n| Bob | Wien |`);
    // --header takes it as the header all the same.
    expect((await mcp.sh(`csv --header '${file}' --tsv`)).text, name).toBe(`${first.replace(",", "\t")}\nBob\tWien`);
  }

  // --no-header over a first row that would be one; both together are refused.
  expect((await mcp.sh(`csv --no-header '${piped}' --rows 1 --tsv`)).text).toBe("Name\tCity");
  const both = await mcp.sh(`csv --header --no-header '${piped}'`);
  expect(both.isError).toBe(true);
  expect(both.text).toBe("csv: --header and --no-header don't go together");

  // Piped input, as help csv shows it: cat of a text file into csv.
  const semicolons = await put(`/Work/${folder}/export.txt`, "Alice;Berlin\nBob;Wien\n");
  expect((await mcp.sh(`cat '${semicolons}' | csv --sep ';' --no-header --tsv`)).text).toBe("Alice\tBerlin\nBob\tWien");
});

test("html and xml: refuse a file kept as bytes @readers", async ({ mcp, put, folder }) => {
  const bytes = Buffer.concat([Buffer.from([0xff, 0xfe, 0x00, 0x81]), randomBytes(64)]);
  for (const [command, format, name] of [["html", "HTML", "page.html"], ["xml", "XML", "feed.xml"]]) {
    const path = await put(`/Work/${folder}/${name}`, bytes);
    const answer = await mcp.sh(`${command} '${path}'`);
    expect(answer.isError, command).toBe(true);
    expect(answer.text, command).toBe(`${command}: ${path} is kept as bytes (not UTF-8 text); Kumbu² can't read it as ${format}`);
  }
});

test("help: csv, html and xml are listed in help, help attachments and the sh tool's manual @readers", async ({ mcp }) => {
  const help = (await mcp.sh("help")).text;
  expect(help).toMatch(/^  csv \[options\] \[path\] \[B2:D20\] +read a CSV file as a table: its shape, rows, columns, a range$/m);
  expect(help).toMatch(/^  html \[--html\|--attr <name>\|--count\] <path> \[selector\] +read an HTML file: its title and headings, or the elements a CSS selector picks$/m);
  expect(help).toMatch(/^  xml \[--xml\|--count\] \[--ns prefix=uri\]… <path> \[xpath\] +read an XML file: its root and elements, or the nodes an XPath picks$/m);
  expect((await mcp.sh("help csv")).text.split("\n")[0]).toBe("csv [options] [path] [B2:D20]");

  const attachments = (await mcp.sh("help attachments")).text;
  for (const line of [
    "    csv        reads a text file as a table: its shape, then rows, columns or a range B2:D20 ('help csv')",
    "    html       reads a text file as HTML: its headings, or what a CSS selector picks ('help html')",
    "    xml        reads a text file as XML: its elements, or what an XPath picks ('help xml')",
  ]) expect(attachments.split("\n")).toContain(line);

  // The manual is the sh tool's description in tools/list.
  const { message } = await mcp.raw("tools/list", {});
  const tools = (message?.result as { tools: { name: string; description?: string }[] }).tools;
  const manual = tools.find((t) => t.name === "sh")?.description ?? "";
  for (const line of [
    "  html <path> [selector]          read an HTML file: title and headings, or what a CSS selector picks (--attr, --html, --count)",
    "  xml <path> [xpath]              read an XML file: its root and elements, or what an XPath picks (--ns, --xml, --count)",
    "  csv [options] [path] [B2:D20]   read a CSV text file as a table: its shape, then --rows 2-20, --cols Date,Amount or a range (help csv)",
  ]) expect(manual.split("\n")).toContain(line);
});

const page = `<!doctype html>
<html><head><title>Costs page</title>
<script>document.title = "altered by script";</script>
</head><body>
<h1>Costs</h1>
<h2>Pumps</h2>
<p id="state">as   written</p>
<script>document.getElementById("state").textContent = "altered by script";</script>
<script>document.write('<p class="written">written by script</p>');</script>
<ul class="links"><li><a href="https://example.org/a">First link</a></li><li><a href="/Work/b">Second
  link</a></li></ul>
<table class="costs"><thead><tr><th>Item</th><th>Amount</th></tr></thead>
<tbody><tr><td>Pump</td><td>12.50</td></tr><tr><td>Valve | small</td><td>3.20</td></tr></tbody></table>
<form action="/nowhere"></form>
</body></html>
`;

test("html: a selector's text, an attribute, a count, a table and the outline @readers", async ({ mcp, put, folder }) => {
  const path = await put(`/Work/${folder}/page.html`, page);

  expect((await mcp.sh(`html '${path}' 'ul.links a'`)).text).toBe("First link\nSecond link");
  expect((await mcp.sh(`html --attr href '${path}' 'a[href]'`)).text).toBe("https://example.org/a\n/Work/b");
  expect((await mcp.sh(`html --count '${path}' 'li'`)).text).toBe("2");
  // The element's HTML as the file has it, whitespace kept, the element included.
  expect((await mcp.sh(`html --html '${path}' '#state'`)).text).toBe('<p id="state">as   written</p>');
  expect((await mcp.sh(`html '${path}' 'table.costs'`)).text).toBe("| Item | Amount |\n| --- | --- |\n| Pump | 12.50 |\n| Valve \\| small | 3.20 |");
  expect((await mcp.sh(`html '${path}'`)).text).toBe("title: Costs page\nh1 Costs\n  h2 Pumps\n2 links, 1 table, 1 form");
});

test("html: a selector that would cost too much, or be misread, is refused @readers", async ({ mcp, put, folder }) => {
  const path = await put(`/Work/${folder}/page.html`, page);
  const refusals: [string, string][] = [
    [`p${"[id]".repeat(250)}`, "a selector is at most 1,000 characters long"],
    [Array(101).fill("p").join(", "), "a selector has at most 100 alternatives; name fewer"],
    ["> a", "a selector can't start with >; name the element it relates to first (body > a)"],
  ];
  for (const [selector, why] of refusals) {
    const answer = await mcp.sh(`html '${path}' '${selector}'`);
    expect(answer.isError, selector.slice(0, 40)).toBe(true);
    expect(answer.text, selector.slice(0, 40)).toBe(`html: '${selector}': ${why}`);
  }
  // A space inside :has() would try every ancestor of every element below.
  const has = await mcp.sh(`html '${path}' 'ul:has(li a)'`);
  expect(has.isError).toBe(true);
  expect(has.text).toBe("html: 'ul:has(li a)': inside :has() only > combines selectors, as a space, + or ~ there would look at every element below every element");
  // At the bounds, a selector is read.
  expect((await mcp.sh(`html --count '${path}' '${Array(100).fill("p").join(", ")}'`)).text).toBe("1");
});

test("html: a page's scripts never run: what html reads is the file as written @readers", async ({ mcp, put, folder }) => {
  const path = await put(`/Work/${folder}/page.html`, page);
  // Each script would have changed what these read: the paragraph's text, the title, a paragraph written in.
  expect((await mcp.sh(`html '${path}' '#state'`)).text).toBe("as written");
  expect((await mcp.sh(`html '${path}' 'title'`)).text).toBe("Costs page");
  expect((await mcp.sh(`html --count '${path}' 'p.written'`)).text).toBe("0");
  // The scripts are there, as text.
  expect((await mcp.sh(`html --count '${path}' 'script'`)).text).toBe("3");
});

const feed = `<?xml version="1.0" encoding="utf-8"?>
<feed xmlns="http://www.w3.org/2005/Atom" xmlns:k="urn:kumbu:e2e">
  <title>Feed</title>
  <entry><title>First</title><link href="https://example.org/1"/><k:cost>12.50</k:cost></entry>
  <entry><title>Second</title><link href="https://example.org/2"/><k:cost>3.20</k:cost></entry>
</feed>
`;

test("xml: XPath with namespaces, the default as d, the hint for a query without prefixes @readers", async ({ mcp, put, folder }) => {
  const path = await put(`/Work/${folder}/atom.xml`, feed);

  expect((await mcp.sh(`xml '${path}' '//d:entry/d:title'`)).text).toBe("First\nSecond");
  expect((await mcp.sh(`xml --ns a=http://www.w3.org/2005/Atom '${path}' '//a:entry/a:link/@href'`)).text).toBe(
    "https://example.org/1\nhttps://example.org/2",
  );
  expect(
    (await mcp.sh(`xml --ns a=http://www.w3.org/2005/Atom --ns e=urn:kumbu:e2e '${path}' '//a:entry[a:title="Second"]/e:cost'`)).text,
  ).toBe("3.20");
  expect((await mcp.sh(`xml --count '${path}' '//d:entry'`)).text).toBe("2");
  expect((await mcp.sh(`xml --xml '${path}' '//d:entry[1]/d:link'`)).text).toBe('<link href="https://example.org/1" xmlns="http://www.w3.org/2005/Atom" />');
  // Current behaviour, not a spec promise: a prefix the root declares works without --ns (help xml).
  expect((await mcp.sh(`xml '${path}' '//k:cost'`)).text).toBe("12.50\n3.20");
  expect((await mcp.sh(`xml '${path}' 'count(//d:entry)'`)).text).toBe("2");
  expect((await mcp.sh(`xml '${path}' '//entry'`)).text).toBe(
    "xml: nothing matched; the document's default namespace is http://www.w3.org/2005/Atom, so name its elements as d:<name> (//d:title)",
  );
  const undefinedPrefix = await mcp.sh(`xml '${path}' '//x:entry'`);
  expect(undefinedPrefix.isError).toBe(true);
  expect(undefinedPrefix.text).toBe("xml: '//x:entry' uses a prefix that isn't defined; define it with --ns prefix=uri");
  expect((await mcp.sh(`xml '${path}'`)).text).toBe(
    [
      "root: feed",
      "namespaces: http://www.w3.org/2005/Atom (the default: name its elements as d:<name>), k=urn:kumbu:e2e",
      "  title 1",
      "  entry 2",
      "    title 2",
      "    link 2",
      "    k:cost 2",
    ].join("\n"),
  );
});

test("xml: a document with a DTD is refused, an external entity or an entity bomb never read @readers", async ({ mcp, put, folder }) => {
  const external = await put(
    `/Work/${folder}/external.xml`,
    `<?xml version="1.0"?>\n<!DOCTYPE r [<!ENTITY x SYSTEM "file:///etc/passwd">]>\n<r>&x;</r>\n`,
  );
  const bomb = await put(
    `/Work/${folder}/bomb.xml`,
    `<?xml version="1.0"?>\n<!DOCTYPE r [\n<!ENTITY a "aaaaaaaaaa">\n${Array.from({ length: 8 }, (_, i) => `<!ENTITY ${String.fromCharCode(98 + i)} "${`&${String.fromCharCode(97 + i)};`.repeat(10)}">`).join("\n")}\n]>\n<r>&i;</r>\n`,
  );
  for (const path of [external, bomb]) {
    for (const command of [`xml '${path}'`, `xml '${path}' '/r'`]) {
      const answer = await mcp.sh(command);
      expect(answer.isError, command).toBe(true);
      expect(answer.text, command).toBe(`xml: ${path} has a DTD, which Kumbu² doesn't process`);
    }
  }
});

test("xml: an XPath that would build or say too much is refused @readers", async ({ mcp, put, folder }) => {
  const path = await put(`/Work/${folder}/atom.xml`, feed);
  const concat = `string-length(concat(${Array(9).fill("/").join(",")}))`;
  const nine = await mcp.sh(`xml '${path}' '${concat}'`);
  expect(nine.isError).toBe(true);
  expect(nine.text).toBe(`xml: '${concat}': concat() takes at most 8 arguments in all, as each can be the whole document's text`);
  // Eight are allowed: the document's text, eight times over.
  const once = await mcp.sh(`xml '${path}' 'string-length(/)'`);
  expect(once.isError).toBe(false);
  expect(Number(once.text), "the document's text has a length").toBeGreaterThan(0);
  const eight = await mcp.sh(`xml '${path}' 'string-length(concat(${Array(8).fill("/").join(",")}))'`);
  expect(eight.isError).toBe(false);
  expect(Number(eight.text)).toBe(8 * Number(once.text));

  const long = `/${"*|".repeat(500)}*`;
  const tooLong = await mcp.sh(`xml '${path}' '${long}'`);
  expect(tooLong.isError).toBe(true);
  expect(tooLong.text).toBe(`xml: '${long}': an XPath is at most 1,000 characters long`);
});

// The two time-bound tests are CPU-heavy (10 s of the server's time each): they give the call its budget and a
// margin, and come within 20 s (plan, Task 4).
test("xml: an XPath that walks the document once per node stops after 10 seconds @readers", async ({ mcp, put, folder }) => {
  test.setTimeout(120_000);
  const path = await put(`/Work/${folder}/nodes.xml`, `<r>${"<n/>".repeat(99_999)}</r>`);
  const xpath = "//*[count(//*) > 0]";
  const started = Date.now();
  const answer = await mcp.sh(`xml '${path}' '${xpath}'`);
  const took = Date.now() - started;
  expect(answer.isError).toBe(true);
  expect(answer.text).toBe(`xml: '${xpath}' took longer than 10 seconds on ${path}; narrow the XPath`);
  expect(took, "the call came within the 10-second budget plus a margin").toBeLessThan(20_000);
});

test("html: a selector that looks through every sibling stops after 10 seconds @readers", async ({ mcp, put, folder }) => {
  test.setTimeout(120_000);
  const path = await put(`/Work/${folder}/list.html`, `<!doctype html><html><body><ul>${"<li>x</li>".repeat(100_000)}</ul></body></html>`);
  const selector = "li:nth-last-of-type(n+1)";
  const started = Date.now();
  const answer = await mcp.sh(`html '${path}' '${selector}'`);
  const took = Date.now() - started;
  expect(answer.isError).toBe(true);
  expect(answer.text).toBe(`html: '${selector}' took longer than 10 seconds on ${path}; narrow the selector or the page`);
  expect(took, "the call came within the 10-second budget plus a margin").toBeLessThan(20_000);
});
