// SPDX-License-Identifier: AGPL-3.0-or-later
// Kumbu.Core.Tests Links/WikiLinkTests and MarkdownLinkTests: the server reads a note's links the same way.
import { describe, expect, test } from "vitest";
import { type NoteLink, parseLinks } from "../src/links/parse";
import { frontmatterEnd, trim } from "../src/links/text";

const one = (text: string): NoteLink => {
  const links = parseLinks(text);
  expect(links).toHaveLength(1);
  return links[0]!;
};

describe("wikilinks", () => {
  test.each([
    ["see [[kumbu]].", "kumbu", "", "", false],
    ["[[projects/kumbu#Goal|the goal]]", "projects/kumbu", "#Goal", "|the goal", false],
    ["| [[kumbu\\|Kumbu]] | table |", "kumbu", "", "\\|Kumbu", false],
    ["![[diagram.png]]", "diagram.png", "", "", true],
    ["[[note#^block-1]]", "note", "#^block-1", "", false],
    ["[[#Heading]]", "", "#Heading", "", false],
    ["[[Überlegungen.md]]", "Überlegungen.md", "", "", false],
  ])("%s", (line, target, subpath, alias, embed) => {
    const link = one(line);
    expect(link).toMatchObject({ wiki: true, target, subpath, alias, embed });
    expect(link.raw).toBe(`${embed ? "!" : ""}[[${target}${subpath}${alias}]]`);
  });

  test("several, with positions", () => {
    expect(parseLinks("a\n[[x]] and [[y|z]]\n").map((l) => [l.line, l.start, l.raw])).toEqual([[1, 0, "[[x]]"], [1, 10, "[[y|z]]"]]);
  });

  test.each([
    "```\n[[in fence]]\n```\n",
    "~~~~\n[[in fence]]\n~~~\nstill [[code]]\n",
    "use `[[not a link]]` here\n",
    "``code with ` and [[x]]``\n",
    "[[]]\n",
    "[[ ]]\n",
    "[[|x]]\n",
    "[[broken\nacross]]\n",
  ])("not a link: %j", (text) => expect(parseLinks(text)).toEqual([]));

  test("a fence whose info string holds a backtick opens nothing", () => {
    expect(one("``` a`b\n[[x]]\n").target).toBe("x");
  });

  test("an unclosed code span hides nothing", () => {
    expect(one("a ` b [[x]]").target).toBe("x");
  });

  test("links in frontmatter count", () => {
    expect(one('---\nrelated: "[[kumbu]]"\n---\n').target).toBe("kumbu");
  });
});

describe("markdown links", () => {
  test.each([
    ["[a](/Work/plan.md)", "/Work/plan.md", ""],
    ["[a](Work/plan.md)", "Work/plan.md", ""],
    ["[a](./sub/plan.md)", "./sub/plan.md", ""],
    ["[a](../plan.md)", "../plan.md", ""],
    ["[a](plan.md)", "plan.md", ""],
    ["[a](plan)", "plan", ""],
    ["[a](Meeting%20Notes.md)", "Meeting Notes.md", ""],
    ["[a](<Meeting Notes.md>)", "Meeting Notes.md", ""],
    ["[a](plan.md#Goals)", "plan.md", "#Goals"],
    ["[a](plan.md#Goals%20and%20ways)", "plan.md", "#Goals%20and%20ways"],
    ["[a](plan.md#^block)", "plan.md", "#^block"],
    ["[a](#Heading)", "", "#Heading"],
    ["[a](Plan%20%232.md)", "Plan #2.md", ""],
    ["[a](B%C3%BCcher.md)", "Bücher.md", ""],
    ["[a](Plan\\_1.md)", "Plan_1.md", ""],
    // Percent-escapes that aren't UTF-8 stay as written, as .NET's Uri.UnescapeDataString leaves them.
    ["[a](100%25%zz%C3.md)", "100%%zz%C3.md", ""],
  ])("%s", (line, target, subpath) => {
    expect(one(line)).toMatchObject({ wiki: false, embed: false, target, subpath, raw: line, alias: "" });
  });

  test.each([
    ["[t](Plan%20(v2).md)", "Plan (v2).md"],
    ['[t](<a b.md> "title")', "a b.md"],
    ["[t](plan.md 'x')", "plan.md"],
    ["[t](plan.md (x))", "plan.md"],
  ])("parentheses, angles and titles: %s", (line, target) => {
    expect(one(`see ${line} here`)).toMatchObject({ target, raw: line, start: 4 });
  });

  test.each([
    "[a](https://x.org)",
    "[a](mailto:a@b.c)",
    "[a](obsidian://open?file=x)",
    "[a](//cdn.x/y.md)",
    "![x](data:image/png;base64,iVBORw0KGgo=)",
    "[a][ref]",
    "`[a](plan.md)`",
    "```\n[a](plan.md)\n```\n",
    "[a]()",
    "\\[a](plan.md)",
    "[a](plan.md",
    "[a](<plan.md)",
  ])("not a note link: %j", (text) => expect(parseLinks(text)).toEqual([]));

  // An escaped "!" is plain text, so a link follows it; an escaped backslash leaves the "!" live.
  test.each([
    ["\\![a](x.md)", false],
    ["\\\\![a](x.md)", true],
    ["![a](x.md)", true],
    ["`!`[a](x.md)", false],
  ])("only an unescaped bang makes an embed: %s", (line, embed) => {
    const link = one(line);
    expect([link.target, link.embed]).toEqual(["x.md", embed]);
    expect(line.slice(link.start)).toBe(link.raw);
    expect(link.raw[0]).toBe(embed ? "!" : "[");
  });

  test.each(["[see [this]](x.md)", "[a \\] b](x.md)"])("link text with brackets and escapes: %s", (line) => {
    expect(one(line)).toMatchObject({ target: "x.md", raw: line });
  });

  test.each(["[[plan|see (x)]]", "[[x]](y.md)", "[see [[x]]](y.md)", "[a](x[[y]].md)"])("no markdown link inside a wikilink: %s", (line) => {
    expect(one(line).wiki).toBe(true);
  });

  test("embeds of images", () => {
    const links = parseLinks('A ![shot](/Screens/shot.png) and ![a b](<img/a b.png> "title") and ![x](My%20report.pdf#page=2)\n');
    expect(links.every((l) => l.embed)).toBe(true);
    expect(links.map((l) => l.target)).toEqual(["/Screens/shot.png", "img/a b.png", "My report.pdf"]);
    expect(links.map((l) => l.raw)).toEqual(["![shot](/Screens/shot.png)", '![a b](<img/a b.png> "title")', "![x](My%20report.pdf#page=2)"]);
    expect(links.slice(0, 2).map((l) => l.start)).toEqual([2, 33]);
  });

  test("frontmatter counts", () => {
    expect(one('---\nsee: "[x](x.md)"\n---\n').target).toBe("x.md");
  });

  test("a line of many brackets costs its length", () => {
    const started = Date.now();
    expect(parseLinks("[".repeat(50_000) + "(" .repeat(50_000))).toEqual([]);
    expect(Date.now() - started).toBeLessThan(2000);
  });
});

test("both kinds in source order", () => {
  expect(parseLinks("[[a]] [b](b.md) [[c]]\n[d](d.md)").map((l) => [l.wiki, l.target, l.line])).toEqual([
    [true, "a", 0], [false, "b.md", 0], [true, "c", 0], [false, "d.md", 1],
  ]);
});

test("trims as .NET's string.Trim does", () => {
  expect(trim("\t a  b \u00a0\u0085")).toBe("a  b");
  expect(trim("\u3000\u2028x\u2029\r\n")).toBe("x");
  expect(trim("\ufeffa\ufeff")).toBe("\ufeffa\ufeff");
  expect(trim("   ")).toBe("");
  expect(trim("")).toBe("");
  expect(trim("a")).toBe("a");
});

test("stops looking for the front matter's end at its length limit", () => {
  const lines = ["---", "title: Plan", "---", "Text"];
  expect(frontmatterEnd(lines)).toBe(3);
  expect(frontmatterEnd(lines, 20)).toBe(3);
  expect(frontmatterEnd(lines, 19)).toBe(0);
  expect(frontmatterEnd(["---", "title: Plan"], 1000)).toBe(0);
});
