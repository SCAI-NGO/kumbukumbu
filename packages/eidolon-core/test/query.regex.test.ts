// SPDX-License-Identifier: AGPL-3.0-or-later
// A pattern is anyone's input: it runs in linear time (RE2), so a note in a shared vault can't hang the tab with one.
import { expect, test } from "vitest";
import { Evaluator } from "../src/query/evaluator";
import { QueryError } from "../src/query/errors";
import { QueryNote } from "../src/query/note";
import { Properties } from "../src/query/properties";

const note = new QueryNote("Ann.md", "", Properties.read(""));
const matches = (text: string, regex: string) => {
  const evaluator = new Evaluator();
  const expr = evaluator.compile(`${JSON.stringify(text)}.matches(${regex})`);
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

test("what RE2 can't do is the regex error: lookaround, backreferences, a broken pattern", () => {
  for (const pattern of ["/a(?=b)/", "/(?<!a)b/", "/(a)\\1/", "/(a/", "/a{2,1}/"]) {
    expect(() => new Evaluator().compile(`"a".matches(${pattern})`), pattern).toThrow(QueryError);
    try {
      new Evaluator().compile(`"a".matches(${pattern})`);
    } catch (e) {
      expect((e as QueryError).code).toBe("regex");
    }
  }
});
