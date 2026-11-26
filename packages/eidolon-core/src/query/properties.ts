// SPDX-License-Identifier: AGPL-3.0-or-later
// A note's properties, typed (Kumbu.Core Queries/Properties): a plain scalar reads as what it looks like (YAML 1.2's
// core schema, and dates) and a quoted one stays text; for a run, a declared type converts a value that fits it cleanly,
// and any other keeps its own (bases-without-obsidian-bugs spec §2 item 4). The tags property gives its tags with their
// '#'. The cases are in tests/query-conformance/properties.
import { isAlias, isMap, isScalar, isSeq, parseAllDocuments, type Node, type Scalar } from "yaml";
import { frontmatterEnd, splitLines } from "../links/text";
import { PropertyTypes, type PropertyType, type PropertyTypeMap } from "./propertyTypes";
import {
  NULL, booleanValue, dateValue, listValue, numberValue, objectValue, textValue, type Value,
} from "./value";

/** A note's properties, typed; `problem` is set (and `values` empty) when the frontmatter does not parse. */
export type NoteProperties = { readonly values: ReadonlyMap<string, Value>; readonly problem: string | null };

/** What the frontmatter may hold: anchors turn a few lines into a tenfold expansion per level (Board/Frontmatter). */
export const MAX_VALUES = 10_000;
export const MAX_DEPTH = 64;
export const TOO_LARGE_PROBLEM = "The frontmatter is too large or nested too deeply.";

const NO_VALUES: ReadonlyMap<string, Value> = new Map();

/** The most notes whose properties are remembered. */
export const CACHE_LIMIT = 20_000;

// Parsed properties by the content they were parsed from (ADR 0027): a note's content is the same string for as long as
// the working set holds it unchanged, so each is parsed once, not once per query. The server keys a ConditionalWeakTable
// by the string, which goes with the string; a string can't be a WeakMap key, so this is a Map by content, bounded, the
// oldest entry out first.
const byContent = new Map<string, NoteProperties>();
const typedByContent = new Map<string, { types: PropertyTypeMap; properties: NoteProperties }>();

/** The properties as the YAML alone types them; with `types`, as a run reads them, with the vault's declared types. */
function read(content: string, types?: PropertyTypeMap): NoteProperties {
  if (types === undefined) {
    const known = byContent.get(content);
    if (known !== undefined) return known;
    return remember(byContent, content, parse(content, false, null));
  }
  const known = typedByContent.get(content);
  if (known !== undefined && sameTypes(known.types, types)) return known.properties;
  typedByContent.delete(content);
  return remember(typedByContent, content, { types, properties: parse(content, false, types) }).properties;
}

function remember<T>(cache: Map<string, T>, content: string, entry: T): T {
  cache.set(content, entry);
  if (cache.size > CACHE_LIMIT) cache.delete(cache.keys().next().value!);
  return entry;
}

// The vault's types.json is read for each query, so the same declarations come as another map.
function sameTypes(a: PropertyTypeMap, b: PropertyTypeMap): boolean {
  if (a === b) return true;
  if (a.size !== b.size) return false;
  for (const [name, type] of a) if (b.get(name) !== type) return false;
  return true;
}

/** The frontmatter with every value as the text it is written as, as the server's Frontmatter.Read gives it: for the tags. */
function asWritten(content: string): NoteProperties {
  return parse(content, true, null);
}

function parse(content: string, written: boolean, types: PropertyTypeMap | null): NoteProperties {
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
    return { values: new Budget(written).top(root, types), problem: null };
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

  constructor(private readonly written: boolean) {}

  top(map: Node, types: PropertyTypeMap | null): Map<string, Value> {
    const values = this.map(map, 0, types === null ? () => null : (name) => PropertyTypes.of(name, types));
    // Obsidian gives the tags property's tags with their '#', as file.tags has them (finding 6).
    const tags = values.get("tags");
    if (tags !== undefined && !this.written) values.set("tags", tagged(tags));
    return values;
  }

  private resolve(node: Node | null | undefined): Node | null {
    if (node === null || node === undefined) return null;
    if (isAlias(node)) return this.anchors.get(node.source) ?? null;
    if (node.anchor !== undefined) this.anchors.set(node.anchor, node);
    return node;
  }

  private map(node: Node, depth: number, typeOf: (name: string) => PropertyType | null): Map<string, Value> {
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

  // Only a property's own value is declared: what a list or a map holds reads as written.
  private convert(source: Node | null, type: PropertyType | null, depth: number): Value {
    if (++this.values > MAX_VALUES || depth > MAX_DEPTH) throw new TooLarge();
    const node = this.resolve(source);
    if (node === null) return this.scalar("", true, type);
    if (isMap(node)) return objectValue(this.map(node, depth, () => null));
    if (isSeq(node)) return listValue(node.items.map((item) => this.convert(item as Node | null, null, depth + 1)));
    if (isScalar(node)) return this.scalar(rawOf(node), isPlain(node), type);
    return NULL;
  }

  private scalar(raw: string, plain: boolean, type: PropertyType | null): Value {
    if (plain && isNull(raw)) return NULL;
    return (type === null ? null : declared(raw, type)) ?? (plain && !this.written ? inferred(raw) : textValue(raw));
  }
}

function tagged(value: Value): Value {
  if (value.kind === "text" && value.text.length > 0) return value.text.startsWith("#") ? value : textValue(`#${value.text}`);
  if (value.kind === "list") return listValue(value.items.map((item) => (item.kind === "text" ? tagged(item) : item)));
  return value;
}

/** A value of `type` when the text converts to one cleanly; null when it doesn't. */
function declared(raw: string, type: PropertyType): Value | null {
  switch (type) {
    // A number or a date declared a text is the text it is written as (01010 keeps its zero).
    case "text": return textValue(raw);
    case "multitext": return listValue([textValue(raw)]);
    // A plain decimal only: "4,50" and "1e3" may mean something else than the number they'd read as.
    case "number": return PLAIN_DECIMAL.test(raw) ? decimal(raw) : null;
    case "checkbox": {
      const lower = raw.toLowerCase();
      return lower === "true" ? booleanValue(true) : lower === "false" ? booleanValue(false) : null;
    }
    // One date type: a date, with a time or without, is a date of either declared type, as it is written.
    default: return date(raw) ?? dateTime(raw);
  }
}

/** A text as a value of `type`, as a parameter of that type reads it; null when it doesn't fit. */
function typed(raw: string, type: PropertyType): Value | null {
  switch (type) {
    // An argument is anyone's input: a plain decimal number only, no NaN, infinity, hex or octal.
    case "number": return decimal(raw);
    case "checkbox": return boolean(raw);
    case "date": {
      const moment = dateTime(raw);
      return date(raw) ?? (moment !== null && moment.kind === "date" ? dateValue(moment.moment.slice(0, 10)) : null);
    }
    case "datetime": {
      const day = date(raw);
      return dateTime(raw) ?? (day !== null && day.kind === "date" ? { kind: "date", moment: day.moment, hasTime: true } : null);
    }
    default: return textValue(raw);
  }
}

/** What a plain scalar looks like: a checkbox, a number, a date with or without a time, or else text. */
function inferred(raw: string): Value {
  return boolean(raw) ?? number(raw) ?? date(raw) ?? dateTime(raw) ?? textValue(raw);
}

// YAML 1.2's core schema, which Obsidian's parser follows (finding 11).
const isNull = (raw: string) => raw === "" || raw === "~" || raw === "null" || raw === "Null" || raw === "NULL";

function boolean(raw: string): Value | null {
  if (raw === "true" || raw === "True" || raw === "TRUE") return booleanValue(true);
  if (raw === "false" || raw === "False" || raw === "FALSE") return booleanValue(false);
  return null;
}

// .NET's $ also matches before a final line break; its number parser then trims it and the datetime is read from the groups.
const NUMBER = /^[-+]?(\d+(\.\d*)?|\.\d+)([eE][-+]?\d+)?\n?$/;
// A date keeps its plain $: .NET's pattern matches before a final line break, but the exact parse of the text then fails.
const DATE = /^(\d{4})-(\d{2})-(\d{2})$/;
const DATE_TIME = /^(\d{4}-\d{2}-\d{2})[T ](\d{2}):(\d{2})(:(\d{2})(\.(\d{1,7}))?)?\n?$/;
const PLAIN_DECIMAL = /^[-+]?[0-9]+(\.[0-9]+)?$/;
const HEX = /^0x[0-9a-fA-F]+$/;
const OCTAL = /^0o[0-7]+$/;
const INFINITY = /^[-+]?\.(inf|Inf|INF)$/;
const NAN = /^\.(nan|NaN|NAN)$/;

function number(raw: string): Value | null {
  if (INFINITY.test(raw)) return numberValue(raw.startsWith("-") ? -Infinity : Infinity);
  if (NAN.test(raw)) return numberValue(NaN);
  // JavaScript's Number reads 0x and 0o as YAML writes them.
  if (HEX.test(raw) || OCTAL.test(raw)) return numberValue(Number(raw));
  return decimal(raw);
}

function decimal(raw: string): Value | null {
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
  return dateValue(`${day}T${m[2]}:${m[3]}:${m[5] ?? "00"}${fraction.length > 0 ? `.${fraction}` : ""}`);
}

export const Properties = { read, asWritten, typed, inferred };
