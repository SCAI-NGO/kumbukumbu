// SPDX-License-Identifier: AGPL-3.0-or-later
// Runs the shared query conformance cases (tests/query-conformance/README.md) against the offline engine; the C# engine
// runs the same files (tests/Kumbu.Core.Tests/Queries/ConformanceTests.cs), and this compares as that runner does.
// A folder of cases whose part of the engine isn't ported yet is listed as todo, so nothing is skipped silently.
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "vitest";
import { parse } from "yaml";
import { Evaluator } from "../src/query/evaluator";
import { QueryError, QuerySyntaxError } from "../src/query/errors";
import { QueryNote } from "../src/query/note";
import { Properties } from "../src/query/properties";
import { PropertyTypes } from "../src/query/propertyTypes";
import { formatNumber, type Value } from "../src/query/value";

const ME = "ts";
const folder = resolve(dirname(fileURLToPath(import.meta.url)), "../../../tests/query-conformance");

/** Folders whose cases run; the others are todo until their part of the engine lands. */
const RUN = new Set(["properties", "expressions"]);

// The clock of a case without "now": any fixed moment, so no case depends on the day it runs.
const DEFAULT_NOW = "2026-01-01T12:00:00";

/** A case file, every scalar of it text (failsafe), as the C# runner reads them: "null" is the text null, not no value. */
type Node = string | Node[] | { [key: string]: Node };
type Case = { [key: string]: Node };

function caseFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? caseFiles(join(dir, e.name)) : e.name.endsWith(".yaml") ? [join(dir, e.name)] : []);
}

const files = caseFiles(folder).map((f) => relative(folder, f).split(sep).join("/")).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
// An empty folder would pass every run while testing nothing.
if (files.length === 0) throw new Error(`No conformance cases in ${folder}.`);

// A file that can't be read or parsed fails the whole run, not one case.
const cases = new Map(files.map((f) => [f, parse(readFileSync(join(folder, f), "utf8"), { schema: "failsafe" }) as Case]));

const text = (node: Node | undefined): string => {
  if (typeof node !== "string") throw new Error("A scalar was expected.");
  return node;
};
const map = (node: Node | undefined): { [key: string]: Node } => {
  if (node === undefined || typeof node === "string" || Array.isArray(node)) throw new Error("A map was expected.");
  return node;
};
const list = (node: Node | undefined): Node[] => {
  if (!Array.isArray(node)) throw new Error("A list was expected.");
  return node;
};

const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);

// Both sides as one line per property, so a failure shows which property differs and how.
const describeMap = (entries: Array<[string, string]>) => entries.map(([k, v]) => `${k}: ${v}`).sort(ordinal);

function describeExpected(node: Node): string {
  if (typeof node === "string") return node;
  if (Array.isArray(node)) return `[${node.map(describeExpected).join(", ")}]`;
  return `{${describeMap(Object.entries(node).map(([k, v]) => [k, describeExpected(v)])).join(", ")}}`;
}

function describeValue(value: Value): string {
  switch (value.kind) {
    case "null": return "null";
    case "text": return value.text.length === 0 ? "text" : `text ${value.text}`;
    case "number": return `number ${formatNumber(value.number)}`;
    case "boolean": return `boolean ${value.boolean}`;
    case "date": return `date ${value.date}`;
    // A datetime is told to the second, as the C# runner does.
    case "datetime": return `datetime ${value.dateTime.slice(0, 19)}`;
    case "list": return `[${value.items.map(describeValue).join(", ")}]`;
    case "object": return `{${describeMap([...value.values].map(([k, v]) => [k, describeValue(v)])).join(", ")}}`;
    case "file": return `file ${value.note.path}`;
    default: throw new Error(`Cannot describe a ${value.kind}.`);
  }
}

for (const [file, root] of cases) {
  if (!RUN.has(file.split("/")[0]!)) {
    test.todo(file);
    continue;
  }

  const pending = root.pending === undefined ? [] : list(root.pending).map(text);
  const run = pending.includes(ME) ? test.skip : test;
  run(file, () => {
    expect(root.description, "A case says what it shows.").toBeDefined();
    const source = text(root.source);
    expect(source === "spec" || source.startsWith("obsidian "), `Unknown source '${source}'.`).toBe(true);

    const types = PropertyTypes.read(JSON.stringify({ types: root.types === undefined ? {} : map(root.types) }));
    const notes = new Map(Object.entries(map(root.notes)).map(([path, content]) => [path, Properties.read(text(content), types)]));

    if (root.cases !== undefined) {
      expressions(root, types);
      return;
    }

    const expect_ = map(root.expect);
    if (expect_.properties !== undefined) {
      for (const [path, values] of Object.entries(map(expect_.properties))) {
        const read = notes.get(path)!;
        expect(describeMap([...read.values].map(([k, v]) => [k, describeValue(v)])).join("\n"), path)
          .toBe(describeMap(Object.entries(map(values)).map(([k, v]) => [k, describeExpected(v)])).join("\n"));
      }
    }

    const problems = new Set(expect_.problems === undefined ? [] : list(expect_.problems).map(text));
    for (const [path, read] of notes) expect(problems.has(path), `${path}: problem '${read.problem}'.`).toBe(read.problem !== null);
  });
}

// The code a thrown error is compared by, as the C# runner names it.
const codeOf = (e: unknown): string =>
  e instanceof QuerySyntaxError ? "syntax" : e instanceof QueryError ? e.code : e instanceof Error ? e.constructor.name : String(e);

function expressions(root: Case, types: ReturnType<typeof PropertyTypes.read>): void {
  const notes = new Map(Object.entries(map(root.notes)).map(([path, content]) =>
    [path, new QueryNote(path, text(content), Properties.read(text(content), types))]));
  const formulas = new Map(Object.entries(root.formulas === undefined ? {} : map(root.formulas)).map(([k, v]) => [k, text(v)]));
  const self = root.this === undefined ? null : notes.get(text(root.this))!;
  const now = root.now === undefined ? DEFAULT_NOW : text(root.now);

  for (const c of list(root.cases).map(map)) {
    const source = text(c.expression);
    const evaluator = new Evaluator(formulas);
    if (c.error !== undefined) {
      let code = "none";
      try {
        evaluator.compile(source);
      } catch (e) {
        code = codeOf(e);
      }
      expect(code, `${source}: expected the error ${text(c.error)}.`).toBe(text(c.error));
      continue;
    }

    const expr = evaluator.compile(source);
    const expected = map(c.values);
    expect(Object.keys(expected).length, `${source}: a value for every note.`).toBe(notes.size);
    for (const [path, value] of Object.entries(expected))
      expect(describeValue(evaluator.evaluate(expr, { note: notes.get(path)!, this: self, now })), `${source} on ${path}`).toBe(describeExpected(value));
  }
}
