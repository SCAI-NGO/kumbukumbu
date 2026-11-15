// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { addProperty, findPropertyBlocks, moveToProperties, yamlValue } from "./frontmatterText";

describe("findPropertyBlocks", () => {
  it.each([
    ["Chris's case", "# aliastest\n---\naliases: [kumbu]\n---\n", [{ start: 1, end: 3, keys: ["aliases"] }]],
    ["a rule alone", "# T\n\n---\n\ntext\n", []],
    ["a setext heading over prose", "prose\n---\nkey: v\n---", []],
    ["inside a ``` fence", "```\n\n---\nkey: v\n---\n```\n", []],
    ["inside a ~~~ fence a shorter fence doesn't close", "~~~~\n~~~\n\n---\nkey: v\n---\n~~~~\n", []],
    ["inside an indented fence", "   ```md\n\n---\nkey: v\n---\n   ```\n", []],
    ["after a closed fence", "```\nx\n```\n\n---\nkey: v\n---\n", [{ start: 4, end: 6, keys: ["key"] }]],
    ["block text", "# T\n---\nkey: |\n---\n", []],
    ["a blank line inside", "# T\n---\na: 1\n\nb: 2\n---\n", []],
    ["a key with a space", "# T\n---\nBuy milk: today\n---\n", []],
    ["an item not under a key", "# T\n---\na: 1\n- b: c\n---\n", []],
    ["nothing between the rules", "# T\n---\n---\n", []],
    ["no closing rule", "# T\n---\na: 1\n", []],
    ["a closing rule with a trailing space", "# T\n---\na: 1\n--- \n", []],
    ["a longer opening rule", "# T\n----\na: 1\n---\n", []],
    ["a key twice", "# T\n---\na: 1\na: 2\n---\n", []],
    ["inside an HTML comment across blank lines", "<!--\n\n---\nk: v\n---\n-->\n", []],
    ["inside <pre> across blank lines", "<PRE>\n\n---\nk: v\n---\n</pre>\n", []],
    ["inside <script> until its own closing tag", "<script>\n</pre>\n\n---\nk: v\n---\n</script>\n", []],
    [
      "after an HTML comment and a <style> on one line",
      "<!-- x -->\n<style>p {}</style>\n\n---\nk: v\n---\n",
      [{ start: 3, end: 5, keys: ["k"] }],
    ],
    ["an unclosed quote", "# T\n---\nk: \"unclosed\n---\n", []],
    ["a lone quote", "# T\n---\nk: '\n---\n", []],
    ["an unclosed quote in an item", "# T\n---\nk:\n  - 'a\n---\n", []],
    ["a flow map that does not read", "# T\n---\nk: {a: [1}\n---\n", []],
    ["a flow map that reads", "# T\n---\nk: {a: 1}\n---\n", [{ start: 1, end: 3, keys: ["k"] }]],
    [
      "two blocks",
      "# A\n---\na: 1\n---\n\n---\nb: [x, y]\n---\n",
      [
        { start: 1, end: 3, keys: ["a"] },
        { start: 5, end: 7, keys: ["b"] },
      ],
    ],
    ["a key with items", "## T\n---\naliases:\n  - a\n- b\n---\n", [{ start: 1, end: 5, keys: ["aliases"] }]],
    ["the real frontmatter alone", "---\naliases: [kumbu]\n---\n# T\n", []],
    ["a block right after the frontmatter's closing line", "---\na: 1\n---\n---\nb: 2\n---\n", []],
    ["a CRLF note", "# T\r\n---\r\naliases: [kumbu]\r\n---\r\n", [{ start: 1, end: 3, keys: ["aliases"] }]],
    ["a BOM before the frontmatter", "﻿---\na: 1\n---\n# T\n---\nb: 2\n---\n", [{ start: 4, end: 6, keys: ["b"] }]],
  ])("%s", (_, content, blocks) => {
    expect(findPropertyBlocks(content)).toEqual(blocks);
  });
});

describe("moveToProperties", () => {
  it.each([
    ["no frontmatter: created at the top", "# Standup\n---\naliases: [daily]\n---\n", 1, 3, "---\naliases: [daily]\n---\n# Standup\n"],
    [
      "with frontmatter: before its closing line",
      "---\ntitle: Plan\n---\n# Plan\n---\naliases: [p]\n---\nText\n",
      4,
      6,
      "---\ntitle: Plan\naliases: [p]\n---\n# Plan\nText\n",
    ],
    [
      "CRLF throughout",
      "---\r\ntitle: Plan\r\n---\r\n# Plan\r\n---\r\naliases: [p]\r\n---\r\nText\r\n",
      4,
      6,
      "---\r\ntitle: Plan\r\naliases: [p]\r\n---\r\n# Plan\r\nText\r\n",
    ],
    ["CRLF, frontmatter created", "# S\r\n---\r\na: 1\r\n---\r\n", 1, 3, "---\r\na: 1\r\n---\r\n# S\r\n"],
    ["LF lines into a CRLF frontmatter", "---\r\nt: 1\r\n---\r\n# S\n---\na: 1\n---\n", 4, 6, "---\r\nt: 1\r\na: 1\r\n---\r\n# S\n"],
    ["an empty frontmatter", "---\n---\n# T\n---\nk: v\n---\n", 3, 5, "---\nk: v\n---\n# T\n"],
    ["a frontmatter closed by ...", "---\na: 1\n...\n# T\n---\nb: 2\n---\n", 4, 6, "---\na: 1\nb: 2\n...\n# T\n"],
    ["a body starting with a blank line", "---\na: b\n---\n\n---\nk: v\n---\ntext\n", 4, 6, "---\na: b\nk: v\n---\n\ntext\n"],
    ["a note starting with a blank line", "\n---\nk: v\n---\n", 1, 3, "---\nk: v\n---\n\n"],
    ["a block at the end without a final newline", "# T\n---\nk: v\n---", 1, 3, "---\nk: v\n---\n# T\n"],
    ["a key with items", "# T\n---\naliases:\n  - a\n  - b\n---\nx\n", 1, 5, "---\naliases:\n  - a\n  - b\n---\n# T\nx\n"],
    [
      "every other byte kept",
      '---\ntitle:  "Plan"   \ntags: [a,b]\n---\n# P  \n\t\n---\nk: v\n---\nend  ',
      6,
      8,
      '---\ntitle:  "Plan"   \ntags: [a,b]\nk: v\n---\n# P  \n\t\nend  ',
    ],
    ["a BOM kept first", "﻿# S\n---\na: 1\n---\n", 1, 3, "﻿---\na: 1\n---\n# S\n"],
    ["a BOM before the frontmatter", "﻿---\nt: 1\n---\n# S\n---\na: 1\n---\n", 4, 6, "﻿---\nt: 1\na: 1\n---\n# S\n"],
  ])("%s", (_, content, start, end, expected) => {
    expect(moveToProperties(content, { start, end })).toEqual({ content: expected });
  });

  it("refuses a key the frontmatter has, naming the first", () => {
    const content = "---\naliases: [x]\n---\n# T\n---\ntitle: T\naliases: [y]\n---\n";
    expect(moveToProperties(content, { start: 4, end: 7 })).toEqual({ refused: "exists", key: "aliases" });
  });

  it("refuses when the frontmatter can't be read", () => {
    const content = "---\nauthor:\n  name: Ada\n---\n# T\n---\nk: v\n---\n";
    expect(moveToProperties(content, { start: 5, end: 7 })).toEqual({ refused: "unreadable" });
  });

  it("refuses a block that is no longer there", () => {
    expect(moveToProperties("# T\nk: v\n", { start: 1, end: 3 })).toEqual({ refused: "gone" });
    expect(moveToProperties("# T\n---\nk: v\n---\n", { start: 0, end: 2 })).toEqual({ refused: "gone" });
    expect(moveToProperties("# T\n---\nk: v\n\n---\n", { start: 1, end: 4 })).toEqual({ refused: "gone" });
  });
});

describe("addProperty", () => {
  it.each([
    ["no frontmatter: created at the top", "# T\nbody\n", "---\nowner: Alice\n---\n# T\nbody\n"],
    ["with frontmatter: its last line", "---\ntitle: Plan\n---\n# Plan\n", "---\ntitle: Plan\nowner: Alice\n---\n# Plan\n"],
    ["CRLF frontmatter", "---\r\ntitle: Plan\r\n---\r\n# Plan\r\n", "---\r\ntitle: Plan\r\nowner: Alice\r\n---\r\n# Plan\r\n"],
    ["CRLF note, frontmatter created", "# T\r\nbody\r\n", "---\r\nowner: Alice\r\n---\r\n# T\r\nbody\r\n"],
    ["a frontmatter without a final newline", "---\ntitle: Plan\n---", "---\ntitle: Plan\nowner: Alice\n---"],
    ["an empty frontmatter", "---\n---\n# T\n", "---\nowner: Alice\n---\n# T\n"],
    ["after a list", "---\ntags:\n  - a\n---\n", "---\ntags:\n  - a\nowner: Alice\n---\n"],
    ["an empty note", "", "---\nowner: Alice\n---\n"],
    ["a line without a newline", "Hello", "---\nowner: Alice\n---\nHello"],
    ["a body starting with a blank line", "\nText\n", "---\nowner: Alice\n---\n\nText\n"],
    ["a BOM kept first, frontmatter created", "﻿Hi\n", "﻿---\nowner: Alice\n---\nHi\n"],
    ["a BOM kept first, frontmatter there", "﻿---\na: 1\n---\n", "﻿---\na: 1\nowner: Alice\n---\n"],
    ["a name differing in case only", "---\nOwner: Bob\n---\n", "---\nOwner: Bob\nowner: Alice\n---\n"],
  ])("%s", (_, content, expected) => {
    expect(addProperty(content, "owner", "Alice")).toEqual({ content: expected });
  });

  it("quotes a value that isn't plainly safe", () => {
    expect(addProperty("---\na: 1\n---\n", "done", "true")).toEqual({ content: '---\na: 1\ndone: "true"\n---\n' });
    expect(addProperty("# T\n", "quote", 'say "hi"')).toEqual({ content: '---\nquote: "say \\"hi\\""\n---\n# T\n' });
  });

  it.each(["1x", "my key", "", "a:b", "-a", "ä"])("refuses the name %j", (name) => {
    expect(addProperty("# T\n", name, "v")).toEqual({ refused: "name" });
  });

  it("refuses a name that is there", () => {
    expect(addProperty("---\nowner: Bob\n---\n", "owner", "Alice")).toEqual({ refused: "exists" });
  });

  it("refuses when the frontmatter can't be read", () => {
    expect(addProperty("---\nauthor:\n  name: Ada\n---\n", "owner", "Alice")).toEqual({ refused: "unreadable" });
  });

  it("writes a # inside quotes", () => {
    expect(addProperty("# T\n", "b", "Issue #42")).toEqual({ content: '---\nb: "Issue #42"\n---\n# T\n' });
    expect(addProperty("# T\n", "note", "a #b")).toEqual({ content: '---\nnote: "a #b"\n---\n# T\n' });
  });

  it.each(["bell\u0007", "nul\u0000", "esc\u001b", "del\u007f", "two\nlines", "cr\r"])("refuses the control character in %j", (value) => {
    expect(addProperty("# T\n", "note", value)).toEqual({ refused: "unreadable" });
  });

  it("takes a tab", () => {
    expect(addProperty("# T\n", "note", "a\tb")).toEqual({ content: '---\nnote: "a\tb"\n---\n# T\n' });
  });
});

describe("yamlValue", () => {
  it.each([
    ["Alice", "Alice"],
    ["Alice Smith", "Alice Smith"],
    ["a/b.c_d-e", "a/b.c_d-e"],
    ["_x", "_x"],
    ["2026-11-15", '"2026-11-15"'],
    ["-x", '"-x"'],
    ["true", '"true"'],
    ["False", '"False"'],
    ["yes", '"yes"'],
    ["No", '"No"'],
    ["On", '"On"'],
    ["off", '"off"'],
    ["NULL", '"NULL"'],
    ["~", '"~"'],
    ["a: b", '"a: b"'],
    ['say "hi"', '"say \\"hi\\""'],
    ["back\\slash", '"back\\\\slash"'],
    ["", '""'],
    ["#tag", '"#tag"'],
    ["Alice ", '"Alice "'],
    ["two\nlines", '"two\\nlines"'],
  ])("%j → %s", (value, yaml) => {
    expect(yamlValue(value)).toBe(yaml);
  });
});
