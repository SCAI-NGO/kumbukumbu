// SPDX-License-Identifier: AGPL-3.0-or-later
// What only this engine has inside: the budget of a base's YAML, the nesting of its filters, the depth of an expression.
import { expect, test } from "vitest";
import { Base } from "../src/query/base";
import { Evaluator } from "../src/query/evaluator";
import { QueryNote } from "../src/query/note";
import { Properties } from "../src/query/properties";
import { PropertyTypes } from "../src/query/propertyTypes";

const note = new QueryNote("a.md", "", Properties.read("", PropertyTypes.none));
const NOW = "2026-01-01T12:00:00";

const codeOf = (go: () => unknown): string => {
  try {
    go();
  } catch (e) {
    return (e as { code?: string }).code ?? String(e);
  }
  return "none";
};

test("a base with more than 10 000 nodes is a base error", () => {
  const items = Array.from({ length: 10_001 }, () => "a").join(", ");
  expect(codeOf(() => Base.parse(`filters: x\nviews: [${items}]`))).toBe("base");
});

test("anchors that expand a few lines into millions of nodes end as a base error, not a hang", () => {
  const levels = ["a: &a [x, x, x, x, x, x, x, x, x, x]"];
  for (let i = 1; i < 9; i++) {
    const previous = String.fromCharCode(96 + i);
    levels.push(`${String.fromCharCode(97 + i)}: &${String.fromCharCode(97 + i)} [${Array(10).fill(`*${previous}`).join(", ")}]`);
  }
  expect(codeOf(() => Base.parse(levels.join("\n")))).toBe("base");
});

test("a base nested deeper than 64 levels is a base error", () => {
  expect(codeOf(() => Base.parse(`x: ${"[".repeat(70)}${"]".repeat(70)}`))).toBe("base");
});

// Each group is a map and a list, two levels of the YAML's own budget of 64, which therefore stops them first.
test("filter groups nested deeper than the budget are a base error", () => {
  const nested = (depth: number): string => (depth === 0 ? "true" : `{ and: [${nested(depth - 1)}] }`);
  expect(codeOf(() => Base.parse(`filters: ${nested(20)}`).run(null, [note], null, NOW))).toBe("none");
  expect(codeOf(() => Base.parse(`filters: ${nested(70)}`).run(null, [note], null, NOW))).toBe("base");
});

test("a long chain of one operator compiles and evaluates without running out of stack", () => {
  const source = Array.from({ length: 3_300 }, () => "x").join("&&");
  expect(source.length).toBeLessThan(10_000);
  const evaluator = new Evaluator();
  const expr = evaluator.compile(source);
  expect(evaluator.evaluate(expr, { note, this: null, now: NOW }).kind).toBe("null");
});

// Chains that compile (a text's lower() calls, a list's [0] steps): one stays a value (null: no such property), the other runs the stack out and is the too-deep error, never a RangeError.
test.each([
  ["lower() calls", "title" + ".lower()".repeat(1_200), "null"],
  ["[0] steps", "tags" + "[0]".repeat(3_300), "too-deep"],
])("a long chain of %s ends as expected", (_, source, expected) => {
  expect(source.length).toBeLessThan(10_000);
  const evaluator = new Evaluator();
  let outcome: string;
  try {
    const expr = evaluator.compile(source);
    outcome = evaluator.evaluate(expr, { note, this: null, now: NOW }).kind;
  } catch (e) {
    outcome = (e as { code?: string }).code ?? String(e);
  }
  expect(outcome).toBe(expected);
});
