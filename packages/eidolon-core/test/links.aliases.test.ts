// SPDX-License-Identifier: AGPL-3.0-or-later
// Kumbu.Core.Tests Links/NoteAliasesTests, and the YAML forms the core reads without a YAML parser.
import { expect, test } from "vitest";
import { aliasesOf, MAX_FRONTMATTER_LENGTH, MAX_PER_NOTE } from "../src/links/aliases";

test.each([
  ["---\naliases: [Kumbu, K²]\n---\n# K\n", ["Kumbu", "K²"]],
  ["---\naliases:\n  - Kumbu\n  - K²\n---\n", ["Kumbu", "K²"]],
  ["---\naliases:\n- Kumbu\n- 'K²'\ntags: [x]\n---\n", ["Kumbu", "K²"]],
  ["---\naliases: Kumbu\n---\n", ["Kumbu"]],
  ["---\naliases: \"Kumbu \\u00b2\" # the name\n---\n", ["Kumbu ²"]],
  ["---\naliases: [2026, true]\n---\n", ["2026", "true"]],
  ["---\naliases: [a b, {x: 1}, [y], ~, '']\n---\n", ["a b"]],
  ["---\naliases:\n  - a b\n  - x: 1\n  - [y]\n  - ~\n  -\n  - null\n---\n", ["a b"]],
  ["---\naliases: [' Kumbu ', kumbu, KUMBU]\n---\n", ["Kumbu"]],
  ["---\naliases: [a/b, 'a#b', 'a|b', 'a[b', 'a]b', 'a^b', \"a\\nb\", ok]\n---\n", ["ok"]],
  ["---\naliases: ['it''s', \"say \\\"hi\\\"\"]\n---\n", ["it's", 'say "hi"']],
  ["---\naliases: [\n  one,\n  two\n]\n---\n", ["one", "two"]],
  ["---\ntitle: a [b\naliases: [Kumbu]\n---\n", ["Kumbu"]],
  ["---\ntitle: x\n\naliases: [Kumbu] # names\nstatus: done\n---\n", ["Kumbu"]],
  ["---\n\"aliases\": [Kumbu]\n---\n", ["Kumbu"]],
  ["---\naliases: >\n  Kumbu\n  Two\n---\n", ["Kumbu Two"]],
  ["---\naliases: |\n  Kumbu\n---\n", ["Kumbu"]],
  ["---\naliases:\n---\n", []],
  ["---\nalias: Kumbu\n---\n", []],
  ["---\nAliases: Kumbu\n---\n", []],
  ["---\naliases: [unclosed\n---\n", []],
  ["---\naliases: a: b\n---\n", []],
  ["---\naliases: {a: b}\n---\n", []],
  ["---\naliases: [Kumbu]\naliases: [Other]\n---\n", []],
  ["---\ntags: [x]\n---\naliases: [Kumbu]\n", []],
  ["aliases: [Kumbu]\n", []],
  ["---\nnested:\n  aliases: [Kumbu]\n---\n", []],
  ["---\naliases: [Kumbu]\n", []],
])("%j", (content, expected) => expect(aliasesOf(content)).toEqual(expected));

test("an alias is at most 200 characters", () => {
  const longest = "k".repeat(200);
  expect(aliasesOf(`---\naliases: [${longest}, ${"k".repeat(201)}, Kumbu]\n---\n`)).toEqual([longest, "Kumbu"]);
});

test("an alias is normalised to NFC", () => {
  expect(aliasesOf(`---\naliases: [${"Überlegung".normalize("NFD")}]\n---\n`)).toEqual(["Überlegung".normalize("NFC")]);
});

test("only the first hundred aliases count", () => {
  const aliases = aliasesOf(`---\naliases: [${Array.from({ length: 150 }, (_, i) => `a${i}`).join(", ")}]\n---\n`);
  expect(aliases).toHaveLength(MAX_PER_NOTE);
  expect(aliases.at(-1)).toBe("a99");
});

test("front matter too long to be names has no aliases", () => {
  expect(aliasesOf(`---\naliases: [Kumbu]\nnotes: ${"x".repeat(MAX_FRONTMATTER_LENGTH)}\n---\n`)).toEqual([]);
});
