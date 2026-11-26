// SPDX-License-Identifier: AGPL-3.0-or-later
// A pattern is anyone's input: it runs in linear time (RE2), so a note in a shared vault can't hang the tab with one.
import { expect, test } from "vitest";
import { Evaluator } from "../src/query/evaluator";
import { QueryNote } from "../src/query/note";
import { Properties } from "../src/query/properties";

const note = new QueryNote("Ann.md", "", Properties.read(""));
const matches = (text: string, regex: string) => {
  const evaluator = new Evaluator();
  const expr = evaluator.compile(`${regex}.matches(${JSON.stringify(text)})`);
  return evaluator.evaluate(expr, { note, this: null, now: "2026-01-01T12:00:00" });
};

test("a catastrophic pattern over a hostile text returns at once", () => {
  const started = performance.now();
  expect(matches(`${"a".repeat(30)}!`, "/(a+)+$/")).toEqual({ kind: "boolean", boolean: false });
  expect(matches(`${"a".repeat(5000)}!`, "/(a|aa)+$/")).toEqual({ kind: "boolean", boolean: false });
  expect(performance.now() - started).toBeLessThan(1000);
});

test("a pattern matches anywhere in the text, with its flags", () => {
  expect(matches("Kumbu", "/umb/")).toEqual({ kind: "boolean", boolean: true });
  expect(matches("Kumbu", "/^umb/")).toEqual({ kind: "boolean", boolean: false });
  expect(matches("Kumbu", "/KUMBU/i")).toEqual({ kind: "boolean", boolean: true });
  expect(matches("a\nb", "/^b$/m")).toEqual({ kind: "boolean", boolean: true });
  expect(matches("a\nb", "/a.b/")).toEqual({ kind: "boolean", boolean: false });
  expect(matches("a\nb", "/a.b/s")).toEqual({ kind: "boolean", boolean: true });
});

test("what RE2 can't do is null, with a warning: lookaround, backreferences, a broken pattern, an unknown flag", () => {
  for (const pattern of ["/a(?=b)/", "/(?<!a)b/", "/(a)\\1/", "/(a/", "/a{2,1}/", "/a/x"]) {
    const evaluator = new Evaluator();
    const expr = evaluator.compile(`${pattern}.matches("a")`);
    expect(evaluator.evaluate(expr, { note, this: null, now: "2026-01-01T12:00:00" }), pattern).toEqual({ kind: "null" });
    expect(evaluator.warnings.map((w) => w.code), pattern).toEqual(["regex"]);
  }
});

test("a pattern whose counts expand too far is null, with a warning that says why, as on the server", () => {
  const cases: Array<[string, string]> = [
    ["/(?:a|aa|aaa|aaaa){0,300}c/", "The regular expression is 3001 positions written out, more than the 64 that run in time, so it is null."],
    ["/(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?c/i", "The regular expression is 67 positions written out, more than the 64 that run in time, so it is null."],
    ["/(?:(?:){100}){100}/", "The regular expression counts a repetition more than 1000 times (alone or multiplied through nesting), which RE2 doesn't run, so it is null."],
  ];
  for (const [pattern, message] of cases) {
    const evaluator = new Evaluator();
    const expr = evaluator.compile(`${pattern}.matches("a")`);
    expect(evaluator.evaluate(expr, { note, this: null, now: "2026-01-01T12:00:00" }), pattern).toEqual({ kind: "null" });
    expect(evaluator.warnings.map((w) => w.message), pattern).toEqual([message]);
  }
});

// re2js has no DFA here: it scans about 2 µs a character at this size (2.3 s on a fast machine), so the bound is generous.
test("the costliest pattern of the largest size that runs searches a long text in time", { timeout: 20000 }, () => {
  const evaluator = new Evaluator();
  const started = performance.now();
  const expr = evaluator.compile(`/${"(?:a|aa)?".repeat(21)}c/i.matches("a".repeat(999999))`);
  expect(evaluator.evaluate(expr, { note, this: null, now: "2026-01-01T12:00:00" })).toEqual({ kind: "boolean", boolean: false });
  expect(evaluator.warnings).toEqual([]);
  expect(performance.now() - started).toBeLessThan(15000);
});
