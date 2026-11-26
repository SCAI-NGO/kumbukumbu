// SPDX-License-Identifier: AGPL-3.0-or-later
// Runs the shared query conformance cases (tests/query-conformance/README.md) against the offline engine; the C# engine
// runs the same files (tests/Kumbu.Core.Tests/Queries/ConformanceTests.cs), and this compares as that runner does.
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "vitest";
import { parse } from "yaml";
import { Base, type QueryResult } from "../src/query/base";
import { Evaluator } from "../src/query/evaluator";
import { QueryError, QuerySyntaxError } from "../src/query/errors";
import { QueryNote } from "../src/query/note";
import { Properties } from "../src/query/properties";
import { PropertyTypes, type PropertyTypeMap } from "../src/query/propertyTypes";
import { Query, type QueryParam } from "../src/query/query";
import { QueryVault } from "../src/query/vault";
import { asMilliseconds, formatNumber, type Value } from "../src/query/value";

const ME = "ts";
const folder = resolve(dirname(fileURLToPath(import.meta.url)), "../../../tests/query-conformance");

/** The folders of cases the runner knows; a folder below tests/query-conformance that isn't one is a failure, not a skip. */
const KNOWN = new Set(["properties", "expressions", "bases", "queries"]);

test("every folder of cases is one the runner knows", () => {
  const folders = readdirSync(folder, { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => e.name);
  expect(folders.filter((name) => !KNOWN.has(name))).toEqual([]);
});

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
    // A date with a time is told to the second, as the C# runner does.
    case "date": return `date ${value.hasTime ? value.moment.slice(0, 19) : value.moment.slice(0, 10)}`;
    case "duration": return `duration ${formatNumber(asMilliseconds(value))}`;
    case "list": return `[${value.items.map(describeValue).join(", ")}]`;
    case "object": return `{${describeMap([...value.values].map(([k, v]) => [k, describeValue(v)])).join(", ")}}`;
    case "file": return `file ${value.note.path}`;
    case "link": return value.display === null ? `link ${value.path}` : `link ${value.path}|${value.display}`;
    case "error": return `error ${value.message}`;
    default: throw new Error(`Cannot describe a ${value.kind}.`);
  }
}

// A value as the case writes it when it matches, else as it is: "error" matches any error value, and "error <text>" one
// whose message starts with the text.
function matches(expected: Node, actual: Value): boolean {
  if (typeof expected === "string" && actual.kind === "error")
    return expected === "error" || (expected.startsWith("error ") && actual.message.startsWith(expected.slice("error ".length)));
  if (Array.isArray(expected) && actual.kind === "list")
    return expected.length === actual.items.length && expected.every((e, i) => matches(e, actual.items[i]!));
  if (typeof expected === "object" && !Array.isArray(expected) && actual.kind === "object") {
    const entries = Object.entries(expected);
    return entries.length === actual.values.size && entries.every(([k, v]) => actual.values.has(k) && matches(v, actual.values.get(k)!));
  }
  return describeExpected(expected) === describeValue(actual);
}

const shown = (expected: Node | undefined, actual: Value): string =>
  expected !== undefined && matches(expected, actual) ? describeExpected(expected) : describeValue(actual);

for (const [file, root] of cases) {
  const pending = root.pending === undefined ? [] : list(root.pending).map(text);
  const run = pending.includes(ME) ? test.skip : test;
  run(file, () => {
    expect(root.description, "A case says what it shows.").toBeDefined();
    const source = text(root.source);
    expect(source === "spec" || source === "kumbu" || source.startsWith("obsidian "), `Unknown source '${source}'.`).toBe(true);
    expect(root.reason !== undefined, "A case departing from Obsidian (source: kumbu), and only such a case, gives its reason.").toBe(source === "kumbu");

    const notes = new Map(Object.entries(map(root.notes)).map(([path, content]) => [path, Properties.read(text(content), typesOf(root))]));

    if (root.query !== undefined || (root.runs !== undefined && list(root.runs).some((r) => map(r).query !== undefined))) {
      queries(root);
      return;
    }

    if (root.runs !== undefined) {
      bases(root);
      return;
    }

    if (root.cases !== undefined) {
      expressions(root);
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

// The case's types as its vault's .obsidian/types.json declares them, read as the host reads that file.
const typesOf = (root: Case): PropertyTypeMap =>
  root.types === undefined
    ? PropertyTypes.none
    : PropertyTypes.read(JSON.stringify({ types: Object.fromEntries(Object.entries(map(root.types)).map(([k, v]) => [k, text(v)])) }));

// The code a thrown error is compared by, as the C# runner names it.
const codeOf = (e: unknown): string =>
  e instanceof QuerySyntaxError ? "syntax" : e instanceof QueryError ? e.code : e instanceof Error ? e.constructor.name : String(e);

// The files of a case: a .md file is a note; any other is a file with that text, without properties. A base case with
// "file" has its base as one of the files; times are null unless the case gives them.
function queryNotes(root: Case): Map<string, QueryNote> {
  const times = root.times === undefined ? {} : map(root.times);
  const file = (path: string, content: string) => {
    const properties = QueryNote.isNotePath(path) ? Properties.read(content, typesOf(root)) : { values: new Map(), problem: null };
    const given = times[path];
    return given === undefined
      ? new QueryNote(path, content, properties)
      : new QueryNote(path, content, properties, null, text(map(given).ctime), text(map(given).mtime));
  };
  const notes = new Map(Object.entries(map(root.notes)).map(([path, content]) => [path, file(path, text(content))]));
  if (root.file !== undefined) notes.set(text(root.file), file(text(root.file), text(root.base)));
  return notes;
}

function expressions(root: Case): void {
  const notes = queryNotes(root);
  const formulas = new Map(Object.entries(root.formulas === undefined ? {} : map(root.formulas)).map(([k, v]) => [k, text(v)]));
  const self = root.this === undefined ? null : notes.get(text(root.this))!;
  const now = root.now === undefined ? DEFAULT_NOW : text(root.now);

  // One run: random() is one number per note across the case's expressions.
  const vault = new QueryVault(notes.values());
  for (const c of list(root.cases).map(map)) {
    const source = text(c.expression);
    const evaluator = new Evaluator(formulas);
    if (c.error !== undefined) {
      let code = "none";
      let message = "";
      try {
        // Most errors of the query come from compiling; the limits of what an expression builds come from evaluating it.
        const compiled = evaluator.compile(source);
        for (const note of notes.values()) evaluator.evaluate(compiled, { note, this: self, now, vault });
      } catch (e) {
        code = codeOf(e);
        message = e instanceof Error ? e.message : String(e);
      }
      expect(code, `${source}: expected the error ${text(c.error)}.`).toBe(text(c.error));
      if (c.message !== undefined)
        expect(message.startsWith(text(c.message)), `${source}: expected a message starting '${text(c.message)}', got '${message}'.`).toBe(true);
      continue;
    }

    const expr = evaluator.compile(source);
    const expected = map(c.values);
    expect(Object.keys(expected).length, `${source}: a value for every note.`).toBe(notes.size);
    for (const [path, value] of Object.entries(expected))
      expect(shown(value, evaluator.evaluate(expr, { note: notes.get(path)!, this: self, now, vault })), `${source} on ${path}`).toBe(describeExpected(value));
  }
}

// The rows as the result has them, a cell that matches the case's told as the case tells it.
const rowsOf = (result: QueryResult, run: Case) => {
  const expected = list(run.rows).map(list);
  return result.rows.map((r, i) => r.cells.map((c, j) => shown(expected[i]?.[j], c)).join(" | ")).join("\n");
};
const expectedRows = (run: Case) => list(run.rows).map((r) => list(r).map(describeExpected).join(" | ")).join("\n");

function bases(root: Case): void {
  const notes = queryNotes(root);
  const self = root.this === undefined ? null : notes.get(text(root.this))!;
  const now = root.now === undefined ? DEFAULT_NOW : text(root.now);
  const yaml = text(root.base);

  for (const run of list(root.runs).map(map)) {
    const view = run.view === undefined ? null : text(run.view);
    const go = () => Base.parse(yaml).run(view, notes.values(), self, now);
    if (run.error !== undefined) {
      let code = "none";
      try {
        go();
      } catch (e) {
        code = codeOf(e);
      }
      expect(code, `View ${view ?? "(first)"}: expected the error ${text(run.error)}.`).toBe(text(run.error));
      continue;
    }

    const result = go();
    expect(result.columns).toEqual(list(run.columns).map(text));
    expect(rowsOf(result, run)).toBe(expectedRows(run));
  }
}

// A parameter as a case writes it: a map of its type, required, and its default, options and description where it has them.
function describeParam(p: QueryParam): string {
  const parts = [`type: ${Query.name(p.type)}`, `required: ${p.required}`];
  if (p.default !== null) parts.push(`default: ${describeValue(p.default)}`);
  if (p.options.length > 0) parts.push(`options: [${p.options.join(", ")}]`);
  if (p.description !== null) parts.push(`description: ${p.description}`);
  return `{${parts.sort(ordinal).join(", ")}}`;
}

function queries(root: Case): void {
  const notes = queryNotes(root);
  const now = root.now === undefined ? DEFAULT_NOW : text(root.now);
  const main = root.query === undefined ? null : text(root.query);

  if (root.params !== undefined) {
    const expected = Object.entries(map(root.params)).map(([name, p]) => `${name}: ${describeExpected(p)}`);
    const actual = Query.read(notes.get(main!)!).params.map((p) => `${p.name}: ${describeParam(p)}`);
    expect(actual.join("\n")).toBe(expected.join("\n"));
  }

  if (root.views !== undefined) expect(Query.read(notes.get(main!)!).viewNames).toEqual(list(root.views).map(text));

  for (const run of list(root.runs).map(map)) {
    const path = run.query === undefined ? main! : text(run.query);
    const view = run.view === undefined ? null : text(run.view);
    const args = new Map(Object.entries(run.args === undefined ? {} : map(run.args))
      .map(([name, value]) => [name, Array.isArray(value) ? value.map(text) : [text(value)]]));
    const go = () => Query.read(notes.get(path)!).run(view, args, notes.values(), now);
    const label = `${path} ${[...args].map(([k, v]) => `${k}=${v.join(",")}`).join(" ")}`;
    // Offline, a case may expect another result than the server's: the offline engine has no vectors.
    const error = root.offline === undefined ? run.error : map(root.offline).error;
    if (error !== undefined) {
      let code = "none";
      try {
        go();
      } catch (e) {
        code = codeOf(e);
      }
      expect(code, `${label}: expected the error ${text(error)}.`).toBe(text(error));
      continue;
    }

    const result = go();
    expect(result.columns).toEqual(list(run.columns).map(text));
    expect(rowsOf(result, run), label).toBe(expectedRows(run));
  }
}
