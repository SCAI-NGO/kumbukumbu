// SPDX-License-Identifier: AGPL-3.0-or-later
// What the TypeScript reading of properties has inside and the shared cases can't show: its budget, how it reports a key
// written twice, and its cache. Which values a note's properties are is in tests/query-conformance/properties.
import { expect, test } from "vitest";
import { CACHE_LIMIT, MAX_DEPTH, MAX_VALUES, Properties, TOO_LARGE_PROBLEM } from "../src/query/properties";
import { PropertyTypes } from "../src/query/propertyTypes";
import { formatNumber } from "../src/query/value";

const none = PropertyTypes.none;
const note = (yaml: string) => `---\n${yaml}\n---\n`;

test("anchors expanding tenfold per level are too large, not a hang", () => {
  const levels = ["a: &a [x, x, x, x, x, x, x, x, x, x]"];
  for (let i = 0; i < 6; i++) levels.push(`${String.fromCharCode(98 + i)}: &${String.fromCharCode(98 + i)} [${Array(10).fill(`*${String.fromCharCode(97 + i)}`).join(", ")}]`);
  const read = Properties.read(note(levels.join("\n")), none);
  expect(read.problem).toBe(TOO_LARGE_PROBLEM);
  expect(read.values.size).toBe(0);
});

test("an anchor that holds itself runs into the depth limit", () => {
  expect(Properties.read(note("a: &a [*a]"), none).problem).toBe(TOO_LARGE_PROBLEM);
});

test("aliases read as the value they stand for", () => {
  const read = Properties.read(note("a: &a [x, 1]\nb: *a"), none);
  expect(read.problem).toBeNull();
  expect(read.values.get("b")).toEqual(read.values.get("a"));
});

test("values and depth are limited", () => {
  const items = (n: number) => `a: [${Array(n).fill("x").join(", ")}]`;
  expect(Properties.read(note(items(MAX_VALUES - 1)), none).problem).toBeNull();
  expect(Properties.read(note(items(MAX_VALUES)), none).problem).toBe(TOO_LARGE_PROBLEM);

  const nested = (levels: number) => `a: ${"[".repeat(levels)}${"]".repeat(levels)}`;
  expect(Properties.read(note(nested(MAX_DEPTH)), none).problem).toBeNull();
  expect(Properties.read(note(nested(MAX_DEPTH + 1)), none).problem).toBe(TOO_LARGE_PROBLEM);
});

test("a key written twice names the key", () => {
  expect(Properties.read(note("rating: 5\nrating: 6"), none).problem).toBe("The frontmatter has the key 'rating' twice.");
});

test("a frontmatter that is no map is a problem, an empty one is not", () => {
  expect(Properties.read(note("- a\n- b"), none).problem).toBe("The frontmatter is not a map of keys and values.");
  expect(Properties.read(note("just text"), none).problem).toBe("The frontmatter is not a map of keys and values.");
  expect(Properties.read(note("~"), none).problem).toBeNull();
  expect(Properties.read("---\n---\n", none).problem).toBeNull();
});

test("a note is parsed once while its content stays unchanged", () => {
  const content = note(`unique: ${Math.random()}`);
  const first = Properties.read(content, none);
  expect(Properties.read(content, none)).toBe(first);
  // The same declarations in another map are the same types.
  const declared = () => PropertyTypes.read('{"types":{"unique":"text"}}');
  const typed = Properties.read(content, declared());
  expect(typed).not.toBe(first);
  expect(Properties.read(content, declared())).toBe(typed);
});

test("the oldest note's properties are dropped beyond the limit", () => {
  const first = "# first, held until the limit is passed";
  const kept = Properties.read(first, none);
  expect(Properties.read(first, none)).toBe(kept);
  for (let i = 0; i < CACHE_LIMIT; i++) Properties.read(`# note ${i}`, none);
  expect(Properties.read(first, none)).not.toBe(kept);
});

test("a number is written as .NET writes its round-trip form", () => {
  const written: Array<[number, string]> = [
    [1, "1"], [1.5, "1.5"], [-7, "-7"], [1000, "1000"], [0.0001, "0.0001"], [0.00001, "1E-05"], [123456789012345, "123456789012345"],
    [1e15, "1E+15"], [1.5e21, "1.5E+21"], [-2.5e-7, "-2.5E-07"], [0.1 + 0.2, "0.30000000000000004"], [-0, "-0"], [Infinity, "Infinity"],
  ];
  expect(written.map(([n]) => formatNumber(n))).toEqual(written.map(([, s]) => s));
});
