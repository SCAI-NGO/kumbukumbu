// SPDX-License-Identifier: AGPL-3.0-or-later
// Reading CSV, HTML and XML through `sh` (spec 2026-11-19-reading-csv-html-xml-design.md, PR #260; brief, open
// point 5). Files go in through `Api.putFile`, made in the test (the hostile ones too, never as fixtures), and are
// removed again at each test's end.
//
// Promises in scope (spec decision → test):
//  1. three commands csv, html, xml, errors as `<command>: <path>: …`        → every test below
//  2. only text files: a file kept as bytes, a note refused                  → "csv: refuses a file kept as bytes, a note …"
//  3. csv overview: dialect, rows and columns, header, the first 10 rows    → "csv: the overview …"
//  4. --rows, --cols (name, letter), a range B2:D20, --tsv                   → "csv: rows, columns, a range and tab-separated …"
//  5. dialect sniffed: ';' recognised, RFC 4180 quoting                      → "csv: a ';' file …", "csv: the overview …"
//  6. a large CSV is cut at the output limit with the spec's line; an
//     unclosed quote is reported with its line                              → "csv: a large file is cut …", "csv: refuses …"
//  7. html: a selector's text, --attr, --count, a table as Markdown, the
//     outline without a selector; a selector starting with a combinator
//     is refused                                                            → "html: a selector's text …"
//  8. nothing runs: a <script> doesn't alter what html reads                → "html: a page's scripts never run …"
//     nothing loads (no request to a server the page names): left out here, as the stack's server can't reach a
//     listener of the test's; covered by tests/Kumbu.Core.Tests/Sh/HtmlTests.cs (the page with app.js, document.write
//     and fetch against a local server)
//  9. xml: XPath with namespaces (d for the default, --ns, the root's own
//     prefixes), the hint for an unprefixed query, the overview            → "xml: XPath with namespaces …"
//     a DTD refused with exactly the spec's text (XXE, entity expansion)    → "xml: a document with a DTD is refused …"
//     an XPath over 1,000 characters, concat() over 8 arguments refused     → "xml: an XPath that would build …"
// 10. a call stops after 10 seconds: xml on 100,000 nodes, html on 100,000
//     siblings with an expensive pseudo-class                               → "xml: … 10 seconds", "html: … 10 seconds"
// Left out: piped input to csv (a shell feature tested by CsvTests), `--html`/`--xml` outer markup (no promise beyond
// the unit tests'), the parse budget of html (needs a page deep enough to parse for 10 s, CPU-heavy beyond the plan's).
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
  expect((await mcp.sh(`html '${path}' 'table.costs'`)).text).toBe("| Item | Amount |\n| --- | --- |\n| Pump | 12.50 |\n| Valve \\| small | 3.20 |");
  expect((await mcp.sh(`html '${path}'`)).text).toBe("title: Costs page\nh1 Costs\n  h2 Pumps\n2 links, 1 table, 1 form");

  const refused = await mcp.sh(`html '${path}' '> a'`);
  expect(refused.isError).toBe(true);
  expect(refused.text).toBe("html: '> a': a selector can't start with >; name the element it relates to first (body > a)");
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
  const eight = `string-length(concat(${Array(8).fill("/").join(",")}))`;
  expect((await mcp.sh(`xml '${path}' '${eight}'`)).isError).toBe(false);

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
