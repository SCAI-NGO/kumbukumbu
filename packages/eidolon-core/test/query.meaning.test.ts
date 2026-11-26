// SPDX-License-Identifier: AGPL-3.0-or-later
// What the cases can't show offline, where a run with meaning fails: that the meaning and its text still read.
import { expect, test } from "vitest";
import { QueryError } from "../src/query/errors";
import { QueryNote } from "../src/query/note";
import { Properties } from "../src/query/properties";
import { Query } from "../src/query/query";

const content = "---\ntype: query\nparams:\n  topic:\n    type: text\nmeaning:\n  property: summary\n  text: topic\n---\n```base\n```\n";
const query = Query.read(new QueryNote("Q.md", content, Properties.read(content)));

const codeOf = (go: () => unknown): string => {
  try {
    go();
  } catch (e) {
    return e instanceof QueryError ? e.code : String(e);
  }
  return "none";
};

test("a query with meaning reads it, gives its text, and checks without vectors", () => {
  expect(query.meaning).toEqual({ property: "summary", param: "topic", text: null, min: null, as: "closeness" });
  expect(query.meaningText(new Map([["topic", ["offline sync"]]]))).toBe("offline sync");
  expect(codeOf(() => query.meaningText(new Map([["topic", ["a".repeat(2001)]]])))).toBe("param-value");
  expect(codeOf(() => query.meaningText(new Map([["year", ["1980"]]])))).toBe("unknown-param");
  expect(query.check("2026-01-01T12:00:00")).toEqual([]);
});

test("a blank text is param-value, and a literal text is the text whatever the arguments", () => {
  expect(codeOf(() => query.meaningText(new Map([["topic", ["   "]]])))).toBe("param-value");
  const literal = "---\ntype: query\nmeaning:\n  property: description\n  text: \"offline sync\"\n---\n```base\n```\n";
  const fixed = Query.read(new QueryNote("L.md", literal, Properties.read(literal)));
  expect(fixed.meaning).toEqual({ property: "description", param: null, text: "offline sync", min: null, as: "closeness" });
  expect(fixed.meaningText(new Map())).toBe("offline sync");
});
