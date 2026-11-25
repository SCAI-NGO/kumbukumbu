// SPDX-License-Identifier: AGPL-3.0-or-later
// A note's properties, typed (Kumbu.Core Queries/Properties). A declared type wins over how a value is written; without
// one, a plain scalar reads as what it looks like and a quoted one stays text; a value that doesn't fit its type stays
// the text it is. The cases are in tests/query-conformance/properties.
import { isAlias, isMap, isScalar, isSeq, parseAllDocuments, type Node, type Scalar } from "yaml";
import { foldCase, frontmatterEnd, splitLines } from "../links/text";
import type { PropertyType, PropertyTypeMap } from "./propertyTypes";
import {
  NULL, booleanValue, dateTimeValue, dateValue, listValue, numberValue, objectValue, textValue, type Value,
} from "./value";

/** A note's properties, typed; `problem` is set (and `values` empty) when the frontmatter does not parse. */
export type NoteProperties = { readonly values: ReadonlyMap<string, Value>; readonly problem: string | null };

/** What the frontmatter may hold: anchors turn a few lines into a tenfold expansion per level (Board/Frontmatter). */
export const MAX_VALUES = 10_000;
export const MAX_DEPTH = 64;
export const TOO_LARGE_PROBLEM = "The frontmatter is too large or nested too deeply.";

const NO_VALUES: ReadonlyMap<string, Value> = new Map();

// Lists of text in every vault, declared or not.
const BUILT_IN: PropertyTypeMap = new Map<string, PropertyType>([
  ["TAGS", "multitext"],
  ["ALIASES", "multitext"],
  ["CSSCLASSES", "multitext"],
]);

/** The most notes whose properties are remembered. */
export const CACHE_LIMIT = 20_000;

// Parsed properties by the content they were parsed from (ADR 0027): a note's content is the same string for as long as
// the working set holds it unchanged, so each is parsed once, not once per query. The server keys a ConditionalWeakTable
// by the string, which goes with the string; a string can't be a WeakMap key, so this is a Map by content, bounded, the
// oldest entry out first.
const byContent = new Map<string, { types: PropertyTypeMap; properties: NoteProperties }>();

function read(content: string, types: PropertyTypeMap): NoteProperties {
  const known = byContent.get(content);
  if (known !== undefined && sameTypes(known.types, types)) return known.properties;
  const properties = parse(content, types);
  byContent.delete(content);
  byContent.set(content, { types, properties });
  if (byContent.size > CACHE_LIMIT) byContent.delete(byContent.keys().next().value!);
  return properties;
}

// The vault's types.json is read for each query, so the same declarations come as another map.
function sameTypes(a: PropertyTypeMap, b: PropertyTypeMap): boolean {
  if (a === b) return true;
  if (a.size !== b.size) return false;
  for (const [name, type] of a) if (b.get(name) !== type) return false;
  return true;
}

function parse(content: string, types: PropertyTypeMap): NoteProperties {
  const lines = splitLines(content);
  const end = frontmatterEnd(lines);
  if (end === 0) return { values: NO_VALUES, problem: null };

  try {
    // Aliases are resolved by the walk below, which counts them; the parser's own duplicate-key error is the walk's.
    const documents = parseAllDocuments(lines.slice(1, end - 1).join("\n"), { schema: "failsafe", uniqueKeys: false });
    for (const document of documents) if (document.errors.length > 0) return { values: NO_VALUES, problem: document.errors[0]!.message };
    const root = documents[0]?.contents;
    if (root === null || root === undefined) return { values: NO_VALUES, problem: null };
    if (isScalar(root) && isPlain(root) && isEmptyOrTilde(root)) return { values: NO_VALUES, problem: null };
    if (!isMap(root)) return { values: NO_VALUES, problem: "The frontmatter is not a map of keys and values." };
    return { values: new Budget().top(root, types), problem: null };
  } catch (e) {
    if (e instanceof TooLarge || e instanceof RangeError) return { values: NO_VALUES, problem: TOO_LARGE_PROBLEM };
    if (e instanceof KeyTwice) return { values: NO_VALUES, problem: `The frontmatter has the key '${e.key}' twice.` };
    throw e;
  }
}

class TooLarge extends Error {}

class KeyTwice extends Error {
  constructor(readonly key: string) {
    super(key);
  }
}

const isPlain = (scalar: Scalar) => scalar.type === undefined || scalar.type === "PLAIN";
const isEmptyOrTilde = (scalar: Scalar) => scalar.value === null || scalar.value === undefined || scalar.value === "" || scalar.value === "~";
const rawOf = (scalar: Scalar): string => (scalar.value === null || scalar.value === undefined ? "" : String(scalar.value));

/** Walks the YAML under the frontmatter's budget, anchors resolved: a cycle runs into MAX_DEPTH, an expansion into MAX_VALUES. */
class Budget {
  private values = 0;
  // Anchors as the walk meets them, in document order, which is where an alias may refer back to.
  private anchors = new Map<string, Node>();

  top(map: Node, types: PropertyTypeMap): Map<string, Value> {
    return this.map(map, 0, (key) => {
      const folded = foldCase(key);
      return types.get(folded) ?? BUILT_IN.get(folded) ?? null;
    });
  }

  private resolve(node: Node | null | undefined): Node | null {
    if (node === null || node === undefined) return null;
    if (isAlias(node)) return this.anchors.get(node.source) ?? null;
    if (node.anchor !== undefined) this.anchors.set(node.anchor, node);
    return node;
  }

  private map(node: Node, depth: number, typeOf: (key: string) => PropertyType | null): Map<string, Value> {
    const result = new Map<string, Value>();
    if (!isMap(node)) return result;
    for (const pair of node.items) {
      const key = this.resolve(pair.key as Node | null);
      // A key that is a list or a map reads as its type's name, so two of them read alike.
      const name = key === null ? "" : isScalar(key) ? rawOf(key) : isMap(key) ? "Mapping" : "Sequence";
      if (result.has(name)) throw new KeyTwice(name);
      result.set(name, this.convert(pair.value as Node | null, typeOf(name), depth + 1));
    }
    return result;
  }

  private convert(source: Node | null, type: PropertyType | null, depth: number): Value {
    if (++this.values > MAX_VALUES || depth > MAX_DEPTH) throw new TooLarge();
    const node = this.resolve(source);
    if (node === null) return scalar("", true, type);
    if (isMap(node)) return objectValue(this.map(node, depth, () => null));
    if (isSeq(node))
      return listValue(node.items.map((item) => this.convert(item as Node | null, type === "multitext" ? "text" : null, depth + 1)));
    if (isScalar(node)) return scalar(rawOf(node), isPlain(node), type);
    return NULL;
  }
}

function scalar(raw: string, plain: boolean, type: PropertyType | null): Value {
  if (plain && isNull(raw)) return NULL;
  switch (type) {
    case null: return plain ? inferred(raw) : textValue(raw);
    case "multitext": return listValue([textValue(raw)]);
    case "number": return number(raw) ?? textValue(raw);
    case "checkbox": return boolean(raw) ?? textValue(raw);
    case "date": {
      const day = date(raw);
      if (day !== null) return day;
      const moment = dateTime(raw);
      return moment !== null && moment.kind === "datetime" ? dateValue(moment.dateTime.slice(0, 10)) : textValue(raw);
    }
    case "datetime": {
      const moment = dateTime(raw);
      if (moment !== null) return moment;
      const day = date(raw);
      return day !== null && day.kind === "date" ? dateTimeValue(`${day.date}T00:00:00`) : textValue(raw);
    }
    default: return textValue(raw);
  }
}

/** A text as a value of `type`, as a declared property reads it; null when it doesn't fit. */
function typed(raw: string, type: PropertyType): Value | null {
  const value = scalar(raw, false, type);
  return value.kind === "text" && type !== "text" ? null : value;
}

/** What a plain scalar looks like: a checkbox, a number, a date, a datetime, or else text. */
function inferred(raw: string): Value {
  return boolean(raw) ?? number(raw) ?? date(raw) ?? dateTime(raw) ?? textValue(raw);
}

// YAML 1.2's core schema, which Obsidian's parser follows; hex, octal and infinities are left as text, as a person meant them.
const isNull = (raw: string) => raw === "" || raw === "~" || raw === "null" || raw === "Null" || raw === "NULL";

function boolean(raw: string): Value | null {
  if (raw === "true" || raw === "True" || raw === "TRUE") return { kind: "boolean", boolean: true };
  if (raw === "false" || raw === "False" || raw === "FALSE") return { kind: "boolean", boolean: false };
  return null;
}

// .NET's $ also matches before a final line break, and its number parser then trims it.
const NUMBER = /^[-+]?(\d+(\.\d*)?|\.\d+)([eE][-+]?\d+)?\n?$/;
const DATE = /^(\d{4})-(\d{2})-(\d{2})$/;
const DATE_TIME = /^(\d{4}-\d{2}-\d{2})[T ](\d{2}):(\d{2})(:(\d{2})(\.(\d{1,7}))?)?$/;

function number(raw: string): Value | null {
  return NUMBER.test(raw) ? numberValue(Number(raw)) : null;
}

function validDay(year: number, month: number, day: number): boolean {
  if (year < 1 || month < 1 || month > 12 || day < 1) return false;
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  return day <= [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1]!;
}

function date(raw: string): Value | null {
  const m = DATE.exec(raw);
  return m !== null && validDay(Number(m[1]), Number(m[2]), Number(m[3])) ? dateValue(raw) : null;
}

function dateTime(raw: string): Value | null {
  const m = DATE_TIME.exec(raw);
  if (m === null) return null;
  const day = m[1]!;
  if (!validDay(Number(day.slice(0, 4)), Number(day.slice(5, 7)), Number(day.slice(8, 10)))) return null;
  if (Number(m[2]) > 23 || Number(m[3]) > 59 || Number(m[5] ?? 0) > 59) return null;
  // The same moment written once: seconds always, the fraction only when it isn't zero.
  const fraction = (m[7] ?? "").replace(/0+$/, "");
  return dateTimeValue(`${day}T${m[2]}:${m[3]}:${m[5] ?? "00"}${fraction.length > 0 ? `.${fraction}` : ""}`);
}

export const Properties = { read, typed, inferred };
