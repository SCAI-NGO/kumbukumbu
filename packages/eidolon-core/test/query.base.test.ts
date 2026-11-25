// SPDX-License-Identifier: AGPL-3.0-or-later
// What only this engine has inside: the budget of a base's YAML, the nesting of its filters, the depth of an expression,
// the bounds of a run.
import { expect, test } from "vitest";
import { Base } from "../src/query/base";
import { QuerySyntaxError } from "../src/query/errors";
import { Evaluator } from "../src/query/evaluator";
import { QueryNote } from "../src/query/note";
import { ExpressionParser } from "../src/query/parser";
import { Properties } from "../src/query/properties";

const note = new QueryNote("a.md", "", Properties.read(""));
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
  expect(evaluator.evaluate(expr, { note, this: null, now: NOW })).toEqual({ kind: "boolean", boolean: false });
});

// Each step of a chain is a level of the parser's 64, so a long chain is a syntax error whatever the stack, and one
// within the limit is a value.
test.each([
  ["lower() calls", "title" + ".lower()".repeat(1_200), "syntax"],
  ["[0] steps", "tags" + "[0]".repeat(3_300), "syntax"],
  ["members", Array(4_000).fill("a").join("."), "syntax"],
  ["lower() calls within the limit", "title" + ".lower()".repeat(31), "null"],
  ["[0] steps within the limit", "tags" + "[0]".repeat(63), "null"],
])("a long chain of %s ends as expected", (_, source, expected) => {
  expect(source.length).toBeLessThan(10_000);
  const evaluator = new Evaluator();
  let outcome: string;
  try {
    const expr = evaluator.compile(source);
    outcome = evaluator.evaluate(expr, { note, this: null, now: NOW }).kind;
  } catch (e) {
    outcome = e instanceof QuerySyntaxError ? "syntax" : ((e as { code?: string }).code ?? String(e));
  }
  expect(outcome).toBe(expected);
});

const run = (formulas: Map<string, string>, source: string) => {
  const evaluator = new Evaluator(formulas);
  return evaluator.evaluate(evaluator.compile(source), { note, this: null, now: NOW });
};

test("formulas that each use the next twice are worked out once each", () => {
  const formulas = new Map(Array.from({ length: 30 }, (_, i) => [`f${i}`, `formula.f${i + 1} + formula.f${i + 1}`] as [string, string]));
  formulas.set("f30", "1");
  const started = performance.now();
  expect(run(formulas, "formula.f0")).toEqual({ kind: "number", number: 2 ** 30 });
  expect(performance.now() - started).toBeLessThan(1_000);
});

const ladder = (text: (i: number) => string) => {
  const formulas = new Map(Array.from({ length: ExpressionParser.MAX_DEPTH - 1 }, (_, i) => [`f${i}`, text(i)] as [string, string]));
  formulas.set(`f${ExpressionParser.MAX_DEPTH - 1}`, "1");
  for (const t of formulas.values()) expect(t.length).toBeLessThanOrEqual(ExpressionParser.MAX_LENGTH);
  return formulas;
};

// The worst the limits allow: 64 levels of formulas, each as long as an expression may be, with the next formula where
// the walk goes deepest.
test("formulas at every limit, each a long chain, compile and evaluate", () => {
  const formulas = ladder((i) => `formula.f${i + 1}` + "&&1".repeat(Math.floor((ExpressionParser.MAX_LENGTH - 12) / 3)));
  expect(run(formulas, "formula.f0")).toEqual({ kind: "boolean", boolean: true });
});

// Nested as deeply as an expression may be, 64 levels of formulas stack about 4,000 levels: more than the levels a
// thread's stack of the server holds, so too deep on both engines.
test("formulas at every limit, each nested as deeply as allowed, are too deep", () => {
  const formulas = ladder((i) => "-".repeat(ExpressionParser.MAX_DEPTH - 2) + `formula.f${i + 1}`);
  expect(codeOf(() => run(formulas, "formula.f0"))).toBe("too-deep");
});

const chain = (nesting: number) => ladder((i) => "-".repeat(nesting) + `formula.f${i + 1}`);

test("64 formulas nested 30 deep each are too deep", () => {
  expect(codeOf(() => run(chain(30), "formula.f0"))).toBe("too-deep");
});

test("formulas within the levels evaluate", () => {
  // Each formula is its negations and the reference: nesting + 1 levels, and the expression's own one.
  const nesting = Math.floor((Evaluator.MAX_LEVELS - 1) / ExpressionParser.MAX_DEPTH) - 2;
  expect(run(chain(nesting), "formula.f0")).toEqual({ kind: "number", number: nesting % 2 === 0 ? 1 : -1 });
});

// A cycle cuts each stage's check short, so the levels the check keeps undercount the path an evaluation takes through
// the stages: S1 → … → S63, where S63 also uses the previous stage's entry, and S64, the next stage's entry, uses S1.
test.each([0, 30])("formulas that go deeper through cycles than their check sees are too deep (nesting %i)", (nesting) => {
  const stages = 10;
  const k = 64;
  const wrap = (text: string) => `${"-".repeat(nesting)}(${text})`;
  const formulas = new Map([["A1", "1"]]);
  let entry = "A1";
  for (let s = 0; s < stages; s++) {
    for (let i = 1; i <= k; i++)
      formulas.set(`S${s}_${i}`, wrap(i < k - 1 ? `formula.S${s}_${i + 1}` : i === k - 1 ? `formula.${entry} + formula.S${s}_${k}` : `formula.S${s}_1`));
    entry = `S${s}_${k}`;
  }
  const evaluator = new Evaluator(formulas);
  expect(codeOf(() => {
    for (let s = 0; s < stages; s++) evaluator.compile(`formula.S${s}_1`);
    return evaluator.evaluate(evaluator.compile(`formula.${entry}`), { note, this: null, now: NOW });
  })).toBe("too-deep");
});

test("a formula beyond the depth limit is still too deep when its formulas were checked before", () => {
  const formulas = new Map(Array.from({ length: 70 }, (_, i) => [`f${i}`, `formula.f${i + 1}`] as [string, string]));
  formulas.set("f70", "1");
  const evaluator = new Evaluator(formulas);
  evaluator.compile("formula.f10");
  expect(codeOf(() => evaluator.compile("formula.f0"))).toBe("too-deep");
});

test("a cycle is found when part of it was checked before", () => {
  const evaluator = new Evaluator(new Map([["a", "formula.b"], ["b", "formula.c"], ["c", "1"], ["d", "formula.b + formula.d"]]));
  evaluator.compile("formula.a");
  const d = evaluator.compile("formula.d");
  expect(evaluator.warnings.map((w) => `${w.code} '${w.expression}' ${w.position}`)).toEqual(["formula-cycle 'formula.b + formula.d' 19"]);
  expect(evaluator.evaluate(d, { note, this: null, now: NOW })).toEqual({ kind: "error", message: "Infinite loop detected in formula" });
});

// A cycle no longer fails the compile, so the evaluation must stop on it by itself, and once per row.
test("a long cycle gives an error value at once", () => {
  const n = ExpressionParser.MAX_DEPTH - 1;
  const formulas = new Map(Array.from({ length: n }, (_, i) => [`f${i}`, `formula.f${(i + 1) % n} + formula.f${(i + 1) % n}`] as [string, string]));
  const started = performance.now();
  expect(run(formulas, "formula.f0")).toEqual({ kind: "error", message: "Infinite loop detected in formula" });
  expect(performance.now() - started).toBeLessThan(1000);
});

const notes = (count: number) => Array.from({ length: count }, (_, i) => new QueryNote(`n${String(i).padStart(3, "0")}.md`, "", Properties.read("")));

test("a sort by random() orders the rows by the number each shows", () => {
  const yaml = "formulas:\n  r: random()\nviews:\n  - type: table\n    order: [formula.r]\n    sort:\n      - property: formula.r\n";
  const shown = Base.parse(yaml).run(null, notes(50), null, NOW).rows.map((r) => (r.cells[0] as { number: number }).number);
  expect(shown).toEqual([...shown].sort((a, b) => a - b));
});

const thrown = (go: () => unknown): unknown => {
  try {
    go();
  } catch (e) {
    return e;
  }
  return null;
};

test("a run whose notes each stay within their steps still ends at the steps of the run", () => {
  // About 460,000 steps for each note: within the bound of one, but twelve of them pass the run's.
  const yaml = `filters: '"x".repeat(150000).split("").filter(value == "y").length == 0'\n`;
  expect(thrown(() => Base.parse(yaml).run(null, notes(12), null, NOW))).toMatchObject({ code: "too-large", message: expect.stringContaining("over its notes") });
});

test("a run whose cells each stay within their size still ends at the size of the run", () => {
  const yaml = `formulas:\n  big: '"x".repeat(900000)'\nviews:\n  - type: table\n    order: [formula.big]\n`;
  expect(Base.parse(yaml).run(null, notes(1), null, NOW).rows).toHaveLength(1);
  expect(thrown(() => Base.parse(yaml).run(null, notes(12), null, NOW))).toMatchObject({ code: "too-large", message: expect.stringContaining("over its notes") });
});
