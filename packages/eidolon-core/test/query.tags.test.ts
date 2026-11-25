// SPDX-License-Identifier: AGPL-3.0-or-later
// One row per rule, a tag and a non-tag each, as the server's tests have them (Kumbu.Core.Tests Tags/TagReaderTests): the
// rules are Obsidian's, and the port reads a note's tags the same way.
import { expect, test } from "vitest";
import { MAX_FRONTMATTER_LENGTH } from "../src/links/aliases";
import { QueryNote } from "../src/query/note";
import { Properties } from "../src/query/properties";
import { PropertyTypes } from "../src/query/propertyTypes";
import { tagsOf } from "../src/query/tags";

const tags = (content: string) => tagsOf(content).map((t) => t.tag).join(" ");

test.each<[string, string]>([
  ["#research", "research"],
  ["text #research more", "research"],
  ["- [ ] task #todo", "todo"],
  ["a\t#tab", "tab"],
  ["word#tag", ""],
  ["(#tag)", ""],
  ["\\#escaped", ""],
  ["# Title", ""],
  ["## Title", ""],
  ["##tag", ""],
  ["# Title #inheading", "inheading"],
  ["#Title", "Title"],
  ["> quoted #tag", "tag"],
  ["| cell #tag |", "tag"],
  ["**#bold** *a* #after", "after"],
  ["#tag\r\n#next", "tag next"],
  ["#tag.", "tag"],
  ["#tag, #other;", "tag other"],
  ["#tag's", "tag"],
  ["#a+b", "a"],
  ["#tag…", "tag"],
  ["#tag—dash", "tag"],
  ["#snake_case #kebab-case #camelCase", "snake_case kebab-case camelCase"],
  ["#123", ""],
  ["#2026", ""],
  ["#v2", "v2"],
  ["#2026-plan", "2026-plan"],
  ["#y1984", "y1984"],
  ["#Grüße", "Grüße"],
  ["#日本語", "日本語"],
  ["#🚀launch", "🚀launch"],
  ["#project/kumbu", "project/kumbu"],
  ["#project/", "project"],
  ["https://example.org/page#part", ""],
  ["see [docs](https://example.org/#part)", ""],
  ["[[Note#Heading]] and [[Note#Heading|#alias]]", ""],
  ["&#123; entity", ""],
  ["`#code` #real", "real"],
  ["``a #b`` #c", "c"],
  ["```\n#fenced\n```\n#after", "after"],
  ["~~~\n#fenced\n~~~", ""],
  ["text\n\n    #indented code", ""],
  ["<!-- #hidden --> #shown", "shown"],
  ["<!--\n#hidden\n-->\n#shown", "shown"],
  ["%% #hidden %% #shown", "shown"],
  ["%%\n#hidden\n%%\n#shown", "shown"],
  ['<span style="color: #fff">x</span> #shown', "shown"],
  ["- item\n\n    - nested #nested", "nested"],
  ["- item\n    - nested #nested", "nested"],
  ["1. item\n\n    more text #inlist", "inlist"],
])("inline: %j", (content, expected) => expect(tags(content)).toBe(expected));

test.each<[string, string]>([
  ["---\ntags:\n  - research\n  - project/kumbu\n---\nbody", "research project/kumbu"],
  ['---\ntags: [research, "#idea"]\n---\n', "research idea"],
  ["---\ntags: research, idea\n---\n", "research idea"],
  ["---\ntags: research idea\n---\n", "research idea"],
  ["---\ntags: research\n---\n", "research"],
  ["---\ntags:\n- research\n---\n", "research"],
  ['---\n"tags": [a1]\n---\n', "a1"],
  ["---\ntags: [2026, ok]\n---\n", "ok"],
  ["---\ntags:\n---\n", ""],
  ["---\ntag: research\n---\n", ""],
  ['---\ntitle: "#notatag"\naliases: [x]\n---\n', ""],
  ["---\ntags: [unclosed\n---\n#body", "body"],
  ["---\ntags: [a]\ntags: [b]\n---\n#body", "body"],
  ["---\ntags: [a]\n---\n#b", "a b"],
])("frontmatter: %j", (content, expected) => expect(tags(content)).toBe(expected));

test("lines are those of the key, the list item or the text", () => {
  expect(tagsOf("---\ntitle: x\ntags:\n  - a\n  - b\n---\n\nline #c\n")).toEqual([
    { tag: "a", line: 3 }, { tag: "b", line: 4 }, { tag: "c", line: 7 },
  ]);
  expect(tagsOf("---\ntags: [a, b]\n---\n")).toEqual([{ tag: "a", line: 1 }, { tag: "b", line: 1 }]);
});

test("frontmatter longer than aliases are read from has no tags, but the text keeps its own", () => {
  const start = "---\ntags: [a]\nnotes: ";
  const end = "\n---\n#inline\n";
  const room = MAX_FRONTMATTER_LENGTH - start.length - "\n---\n".length;
  expect(tags(start + "x".repeat(room) + end)).toBe("a inline");
  expect(tags(start + "x".repeat(room + 1) + end)).toBe("inline");
});

test("a decomposed tag reads as composed", () => {
  expect(tagsOf("#Grüße")[0]!.tag).toBe("Grüße");
});

test("a note's tags list each tag once, ignoring case, as first written", () => {
  const content = "---\ntags: [Research]\n---\n#research #other #RESEARCH";
  expect(new QueryNote("A.md", content, Properties.read(content, PropertyTypes.none)).tags).toEqual(["Research", "other"]);
});

test("the tags of one content are read once and remembered", () => {
  const content = "# Notes #remembered\n";
  expect(tagsOf(content)).toBe(tagsOf(content));
});
