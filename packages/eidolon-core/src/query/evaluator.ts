// SPDX-License-Identifier: AGPL-3.0-or-later
// Evaluates expressions of the Bases language over a note (Kumbu.Core Queries/Evaluator). One rule decides what fails:
// what no note can make right (a function, method or formula that doesn't exist, formulas in a circle, a regular
// expression that doesn't compile) is a QueryError when the expression is compiled, before any note is read; a value of
// the wrong type, which messy notes always have, gives null. The cases are in tests/query-conformance/expressions.
import { RE2JS } from "re2js";
import { foldCase } from "../links/text";
import { QueryError, inExpression } from "./errors";
import type { Expr, Operator } from "./expr";
import { ExpressionParser, isWhiteSpace } from "./parser";
import type { QueryNote } from "./note";
import { Properties } from "./properties";
import {
  NULL, booleanValue, dateTimeValue, dateValue, fileValue, formatNumber, listValue, noteRefValue, numberValue, objectValue,
  regexValue, textValue, type Value,
} from "./value";

/**
 * What an expression is evaluated for: the note of the row, the note the query is shown in (`this`, if any) and the clock
 * of the run, fixed so every row sees the same `now()`: a datetime without a zone, "yyyy-MM-ddTHH:mm:ss" with up to 7
 * fractional digits.
 */
export type Scope = { readonly note: QueryNote; readonly this: QueryNote | null; readonly now: string };

/** One function, method or field of the language: its name, how it is written, and what it gives. */
export type LanguageEntry = { readonly name: string; readonly written: string; readonly gives: string };

const entry = (name: string, written: string, gives: string): LanguageEntry => ({ name, written, gives });

/** The functions, as `help query` shows them: the help and what compiles are one list, so they can't drift. */
export const FunctionReference: readonly LanguageEntry[] = [
  entry("if", "if(condition, then, else)", "then when the condition holds, else else; only the branch taken is evaluated"),
  entry("now", "now()", "the date and time of the run, in your time zone"),
  entry("today", "today()", "the date of the run"),
  entry("date", "date(text)", "the date or date and time a text writes (2026-11-23, 2026-11-23T14:00), else null"),
  entry("number", "number(value)", "a number from a number, a checkbox (1 or 0) or a text that writes one, else null"),
  entry("list", "list(value)", "the value as a list: a list stays one, null is the empty list"),
];

/** The methods, by what they are called on, as `help query` shows them. */
export const MethodReference: readonly LanguageEntry[] = [
  entry("isEmpty", "any.isEmpty()", "true for null, the empty text, list or object"),
  entry("toString", "any.toString()", "the value as text"),
  entry("contains", "text.contains(text)", "whether the text holds the other, case counting"),
  entry("startsWith", "text.startsWith(text)", "whether the text starts with the other"),
  entry("endsWith", "text.endsWith(text)", "whether the text ends with the other"),
  entry("lower", "text.lower()", "the text in lower case"),
  entry("upper", "text.upper()", "the text in upper case"),
  entry("trim", "text.trim()", "the text without spaces at either end"),
  entry("matches", "text.matches(/regex/flags)", "whether a regular expression matches (flags g, i, m, s, u)"),
  entry("contains", "list.contains(value)", "whether the list holds the value"),
  entry("join", "list.join(text)", "the items as one text, the text between them (a comma without one)"),
  entry("abs", "number.abs()", "the number without its sign"),
  entry("floor", "number.floor()", "the number rounded down"),
  entry("ceil", "number.ceil()", "the number rounded up"),
  entry("round", "number.round()", "the number rounded, halves up"),
  entry("date", "datetime.date()", "the date of a date and time"),
  entry("hasTag", "file.hasTag(tag, ...)", "whether the note carries one of the tags, or one nested below it"),
  entry("inFolder", "file.inFolder(folder)", "whether the note lies in the folder or below it"),
  entry("hasProperty", "file.hasProperty(name)", "whether the note's frontmatter has the property"),
];

/** The fields, by what they are read on. */
export const FieldReference: readonly LanguageEntry[] = [
  entry("name", "file.name", "the file's name with its extension (Ann.md)"),
  entry("basename", "file.basename", "the file's name without its extension (Ann)"),
  entry("path", "file.path", "the path in the vault (People/Ann.md)"),
  entry("folder", "file.folder", "the folder in the vault (People)"),
  entry("ext", "file.ext", "the extension (md)"),
  entry("tags", "file.tags", "the note's tags, frontmatter and text"),
  entry("properties", "file.properties", "the frontmatter as an object"),
  entry("length", "text.length, list.length", "how many characters or items"),
  entry("year", "date.year, .month, .day", "a date's parts; a date and time also has .hour, .minute, .second"),
];

type BinaryExpr = Extract<Expr, { kind: "binary" }>;

// The binary expressions down the left side of one, the outermost first.
function leftSpine(expr: BinaryExpr): BinaryExpr[] {
  const spine = [expr];
  for (let left = expr.left; left.kind === "binary"; left = left.left) spine.push(left);
  return spine;
}

const FUNCTIONS = new Set(FunctionReference.map((f) => f.name));
const METHODS = new Set(MethodReference.map((m) => m.name));

export class Evaluator {
  private readonly formulaExprs = new Map<string, Expr>();
  private readonly regexes = new Map<Expr, RE2JS>();

  constructor(private readonly formulaTexts: ReadonlyMap<string, string> = new Map()) {}

  /** Parses and checks an expression; throws `QuerySyntaxError` or `QueryError`. */
  compile(text: string): Expr {
    try {
      const expr = ExpressionParser.parse(text);
      this.check(expr, []);
      return expr;
    } catch (e) {
      // The parser's depth limit counts brackets and prefix operators, not the steps of a chain (a.b.c..., x[0][0]...);
      // there the stack's end is the limit, and an error like any other.
      if (e instanceof RangeError) throw new QueryError("too-deep", "The expression is nested too deeply.", 0);
      throw e;
    }
  }

  evaluate(expr: Expr, scope: Scope): Value {
    switch (expr.kind) {
      case "literal": return expr.value;
      case "name": return name(expr.name, scope);
      case "member":
        if (expr.target.kind === "name" && expr.target.name === "formula") return this.evaluate(this.formulaExprs.get(expr.name)!, scope);
        return field(this.evaluate(expr.target, scope), expr.name);
      case "index": return index(this.evaluate(expr.target, scope), this.evaluate(expr.index, scope));
      case "call":
        if (expr.callee.kind === "name") return this.call(expr.callee.name, expr.args, scope);
        if (expr.callee.kind === "member")
          return method(this.evaluate(expr.callee.target, scope), expr.callee.name, expr.args.map((a) => this.evaluate(a, scope)));
        return NULL;
      case "unary": {
        const operand = this.evaluate(expr.operand, scope);
        if (expr.operator === "Not") return booleanValue(!truthy(operand));
        return operand.kind === "number" ? numberValue(-operand.number) : NULL;
      }
      case "binary": {
        // The same walk of the left spine as in check: the innermost left first, then each right on the way out.
        const spine = leftSpine(expr);
        let value = this.evaluate(spine[spine.length - 1]!.left, scope);
        for (let i = spine.length - 1; i >= 0; i--) {
          const { operator, right } = spine[i]!;
          if (operator === "And") value = !truthy(value) ? value : this.evaluate(right, scope);
          else if (operator === "Or") value = truthy(value) ? value : this.evaluate(right, scope);
          else value = binary(operator, value, this.evaluate(right, scope));
        }
        return value;
      }
      case "list": return listValue(expr.items.map((i) => this.evaluate(i, scope)));
      case "object": {
        // A key written twice keeps its first place and its last value.
        const last = new Map<string, Expr>();
        for (const [key, value] of expr.entries) last.set(key, value);
        return objectValue(new Map([...last].map(([key, value]) => [key, this.evaluate(value, scope)])));
      }
      case "regex": return regexValue(this.regexes.get(expr)!);
    }
  }

  // Formulas are checked where they are used, so one broken formula fails only the expressions that use it.
  private check(expr: Expr, chain: string[]): void {
    switch (expr.kind) {
      case "call":
        if (expr.callee.kind === "name") {
          if (!FUNCTIONS.has(expr.callee.name))
            throw new QueryError("unknown-function", `There is no function '${expr.callee.name}'.`, expr.callee.position);
          for (const a of expr.args) this.check(a, chain);
        } else if (expr.callee.kind === "member") {
          if (!METHODS.has(expr.callee.name))
            throw new QueryError("unknown-method", `There is no method '${expr.callee.name}'.`, expr.callee.position);
          this.check(expr.callee.target, chain);
          for (const a of expr.args) this.check(a, chain);
        } else {
          throw new QueryError("unknown-function", "Only a function or a method can be called.", expr.position);
        }
        break;
      case "member":
        if (expr.target.kind === "name" && expr.target.name === "formula") this.formula(expr, chain);
        else this.check(expr.target, chain);
        break;
      case "index":
        this.check(expr.target, chain);
        this.check(expr.index, chain);
        break;
      case "unary":
        this.check(expr.operand, chain);
        break;
      case "binary": {
        // A long chain (a && b && c ...) nests to the left: walk its spine, not the stack.
        const spine = leftSpine(expr);
        this.check(spine[spine.length - 1]!.left, chain);
        for (let i = spine.length - 1; i >= 0; i--) this.check(spine[i]!.right, chain);
        break;
      }
      case "list":
        for (const i of expr.items) this.check(i, chain);
        break;
      case "object":
        for (const [, value] of expr.entries) this.check(value, chain);
        break;
      case "regex":
        this.regexes.set(expr, compileRegex(expr.pattern, expr.flags, expr.position));
        break;
    }
  }

  private formula(reference: Expr & { kind: "member" }, chain: string[]): void {
    const { name } = reference;
    const text = this.formulaTexts.get(name);
    if (text === undefined) throw new QueryError("unknown-formula", `There is no formula '${name}'.`, reference.position);
    if (chain.includes(name))
      throw new QueryError("formula-cycle", `The formulas use each other in a circle: ${[...chain, name].join(" → ")}.`, reference.position);
    if (chain.length >= ExpressionParser.MAX_DEPTH)
      throw new QueryError("too-deep", `The formulas use each other more than ${ExpressionParser.MAX_DEPTH} levels deep.`, reference.position);

    let expr = this.formulaExprs.get(name);
    if (expr === undefined) {
      expr = inExpression(text, () => ExpressionParser.parse(text));
      this.formulaExprs.set(name, expr);
    }

    const parsed = expr;
    chain.push(name);
    inExpression(text, () => this.check(parsed, chain));
    chain.pop();
  }

  private call(functionName: string, args: readonly Expr[], scope: Scope): Value {
    const argument = (i: number): Value => (i < args.length ? this.evaluate(args[i]!, scope) : NULL);
    switch (functionName) {
      // Only the branch taken is evaluated, as in JavaScript.
      case "if": return truthy(argument(0)) ? argument(1) : argument(2);
      case "now": return dateTimeValue(scope.now);
      case "today": return dateValue(scope.now.slice(0, 10));
      case "date": return dateOf(argument(0));
      case "number": {
        const v = argument(0);
        if (v.kind === "number") return v;
        if (v.kind === "boolean") return numberValue(v.boolean ? 1 : 0);
        if (v.kind === "text") {
          const read = Properties.inferred(trim(v.text));
          return read.kind === "number" ? read : NULL;
        }
        return NULL;
      }
      case "list": {
        const v = argument(0);
        return v.kind === "list" ? v : v.kind === "null" ? listValue([]) : listValue([v]);
      }
      default: return NULL;
    }
  }
}

// Matching in linear time: a pattern is anyone's input, and backtracking would let one hang the tab. RE2 has no
// backtracking and no lookaround; what it rejects is the regex error, as the server's non-backtracking engine does.
function compileRegex(pattern: string, flags: string, position: number): RE2JS {
  let options = 0;
  if (flags.includes("i")) options |= RE2JS.CASE_INSENSITIVE;
  if (flags.includes("m")) options |= RE2JS.MULTILINE;
  if (flags.includes("s")) options |= RE2JS.DOTALL;
  try {
    return RE2JS.compile(pattern, options);
  } catch (e) {
    throw new QueryError("regex", `The regular expression doesn't compile: ${e instanceof Error ? e.message : String(e)}`, position);
  }
}

function name(propertyName: string, scope: Scope): Value {
  switch (propertyName) {
    case "file": return fileValue(scope.note);
    case "note": return objectValue(scope.note.properties.values);
    case "this": return scope.this !== null ? noteRefValue(scope.this) : NULL;
    case "formula": return NULL;
    default: return property(scope.note, propertyName);
  }
}

const property = (note: QueryNote, propertyName: string): Value => note.properties.values.get(propertyName) ?? NULL;

const fileName = (path: string) => path.slice(path.lastIndexOf("/") + 1);
const folderOf = (path: string) => (path.lastIndexOf("/") >= 0 ? path.slice(0, path.lastIndexOf("/")) : "");

// .NET's Path.GetFileNameWithoutExtension and GetExtension: the last dot starts the extension, unless it ends the name.
const baseName = (file: string) => (file.lastIndexOf(".") >= 0 ? file.slice(0, file.lastIndexOf(".")) : file);
const extension = (file: string) => (file.lastIndexOf(".") >= 0 && file.lastIndexOf(".") < file.length - 1 ? file.slice(file.lastIndexOf(".") + 1) : "");

function field(value: Value, fieldName: string): Value {
  switch (value.kind) {
    case "file":
      switch (fieldName) {
        case "name": return textValue(fileName(value.note.path));
        case "basename": return textValue(baseName(fileName(value.note.path)));
        case "path": return textValue(value.note.path);
        case "folder": return textValue(folderOf(value.note.path));
        case "ext": return textValue(extension(fileName(value.note.path)));
        case "tags": return listValue(value.note.tags.map(textValue));
        case "properties": return objectValue(value.note.properties.values);
        default: return NULL;
      }
    case "noteRef":
      if (fieldName === "file") return fileValue(value.note);
      if (fieldName === "note") return objectValue(value.note.properties.values);
      return property(value.note, fieldName);
    case "object": return value.values.get(fieldName) ?? NULL;
    case "text": return fieldName === "length" ? numberValue(value.text.length) : NULL;
    case "list": return fieldName === "length" ? numberValue(value.items.length) : NULL;
    case "date": return part(value.date, fieldName, false);
    case "datetime": return part(value.dateTime, fieldName, true);
    default: return NULL;
  }
}

// The parts of "yyyy-MM-dd[THH:mm:ss…]"; only a datetime has a time.
function part(iso: string, partName: string, withTime: boolean): Value {
  switch (partName) {
    case "year": return numberValue(Number(iso.slice(0, 4)));
    case "month": return numberValue(Number(iso.slice(5, 7)));
    case "day": return numberValue(Number(iso.slice(8, 10)));
    case "hour": return withTime ? numberValue(Number(iso.slice(11, 13))) : NULL;
    case "minute": return withTime ? numberValue(Number(iso.slice(14, 16))) : NULL;
    case "second": return withTime ? numberValue(Number(iso.slice(17, 19))) : NULL;
    default: return NULL;
  }
}

function index(target: Value, at: Value): Value {
  if (target.kind === "list" && at.kind === "number")
    return at.number >= 0 && at.number < target.items.length && at.number === Math.floor(at.number) ? target.items[at.number]! : NULL;
  if (target.kind === "object" && at.kind === "text") return target.values.get(at.text) ?? NULL;
  if (target.kind === "noteRef" && at.kind === "text") return property(target.note, at.text);
  return NULL;
}

function dateOf(v: Value): Value {
  if (v.kind === "text") {
    const read = Properties.inferred(trim(v.text));
    return read.kind === "date" || read.kind === "datetime" ? read : NULL;
  }
  return v.kind === "date" || v.kind === "datetime" ? v : NULL;
}

// .NET's string.Trim(): the characters char.IsWhiteSpace names, which are not quite JavaScript's.
function trim(text: string): string {
  let start = 0;
  let end = text.length;
  while (start < end && isWhiteSpace(text[start]!)) start++;
  while (end > start && isWhiteSpace(text[end - 1]!)) end--;
  return text.slice(start, end);
}

function method(target: Value, methodName: string, args: readonly Value[]): Value {
  const argument = (i: number): Value => args[i] ?? NULL;
  const textArgument = (i: number): string | null => {
    const a = argument(i);
    return a.kind === "text" ? a.text : null;
  };
  const bool = (b: boolean | null): Value => (b === null ? NULL : booleanValue(b));

  if (methodName === "isEmpty") {
    switch (target.kind) {
      case "null": return booleanValue(true);
      case "text": return booleanValue(target.text.length === 0);
      case "list": return booleanValue(target.items.length === 0);
      case "object": return booleanValue(target.values.size === 0);
      default: return booleanValue(false);
    }
  }
  if (target.kind === "null") return NULL;
  if (methodName === "toString") return textValue(display(target));

  switch (target.kind) {
    case "text":
      switch (methodName) {
        case "contains": return bool(textArgument(0) === null ? null : target.text.includes(textArgument(0)!));
        case "startsWith": return bool(textArgument(0) === null ? null : target.text.startsWith(textArgument(0)!));
        case "endsWith": return bool(textArgument(0) === null ? null : target.text.endsWith(textArgument(0)!));
        case "lower": return textValue(target.text.toLowerCase());
        case "upper": return textValue(target.text.toUpperCase());
        case "trim": return textValue(trim(target.text));
        case "matches": {
          const r = argument(0);
          return r.kind === "regex" ? booleanValue((r.regex as RE2JS).test(target.text)) : NULL;
        }
        default: return NULL;
      }
    case "list":
      switch (methodName) {
        case "contains": return booleanValue(target.items.some((i) => equal(i, argument(0))));
        case "join": return textValue(target.items.map(display).join(textArgument(0) ?? ","));
        default: return NULL;
      }
    case "number":
      switch (methodName) {
        case "abs": return numberValue(Math.abs(target.number));
        case "floor": return numberValue(Math.floor(target.number));
        case "ceil": return numberValue(Math.ceil(target.number));
        // Halves go up, as the server's engine rounds (floor(x + 0.5)): -2.5 becomes -2.
        case "round": return numberValue(Math.floor(target.number + 0.5));
        default: return NULL;
      }
    case "date": return methodName === "date" ? target : NULL;
    case "datetime": return methodName === "date" ? dateValue(target.dateTime.slice(0, 10)) : NULL;
    case "file":
      switch (methodName) {
        case "hasTag": return booleanValue(args.some((a) => a.kind === "text" && hasTag(target.note, a.text)));
        case "inFolder": return bool(textArgument(0) === null ? null : inFolder(target.note.path, textArgument(0)!));
        case "hasProperty": return bool(textArgument(0) === null ? null : target.note.properties.values.has(textArgument(0)!));
        default: return NULL;
      }
    default: return NULL;
  }
}

// A tag holds its nested tags: "project" finds #project/kumbu, not #projects.
function hasTag(note: QueryNote, tag: string): boolean {
  const wanted = foldCase(tag.replace(/^#+/, ""));
  return note.tags.some((t) => {
    const folded = foldCase(t);
    return folded === wanted || folded.startsWith(`${wanted}/`);
  });
}

function inFolder(path: string, folder: string): boolean {
  const trimmed = folder.replace(/^\/+|\/+$/g, "");
  return trimmed.length === 0 || path.startsWith(`${trimmed}/`);
}

function binary(operator: Operator, left: Value, right: Value): Value {
  switch (operator) {
    case "Equal": return booleanValue(equal(left, right));
    case "NotEqual": return booleanValue(!equal(left, right));
    case "Less": return booleanValue((compare(left, right) ?? NaN) < 0);
    case "LessOrEqual": return booleanValue((compare(left, right) ?? NaN) <= 0);
    case "Greater": return booleanValue((compare(left, right) ?? NaN) > 0);
    case "GreaterOrEqual": return booleanValue((compare(left, right) ?? NaN) >= 0);
  }
  if (operator === "Add" && (left.kind === "text" || right.kind === "text")) return textValue(display(left) + display(right));
  if (left.kind !== "number" || right.kind !== "number") return NULL;
  const l = left.number;
  const r = right.number;
  const result = operator === "Add" ? l + r : operator === "Subtract" ? l - r : operator === "Multiply" ? l * r : operator === "Divide" ? l / r : l % r;
  return Number.isFinite(result) ? numberValue(result) : NULL;
}

/** Equal within a type; values of different types are never equal, and null equals only null. */
export function equal(a: Value, b: Value): boolean {
  switch (a.kind) {
    case "null": return b.kind === "null";
    case "number": return b.kind === "number" && a.number === b.number;
    case "text": return b.kind === "text" && a.text === b.text;
    case "boolean": return b.kind === "boolean" && a.boolean === b.boolean;
    case "date": return b.kind === "date" && a.date === b.date;
    case "datetime": return b.kind === "datetime" && a.dateTime === b.dateTime;
    case "file": return b.kind === "file" && a.note.path === b.note.path;
    case "noteRef": return b.kind === "noteRef" && a.note.path === b.note.path;
    case "list": return b.kind === "list" && a.items.length === b.items.length && a.items.every((item, i) => equal(item, b.items[i]!));
    case "object":
      return b.kind === "object" && a.values.size === b.values.size
        && [...a.values].every(([key, value]) => b.values.has(key) && equal(value, b.values.get(key)!));
    default: return false;
  }
}

/**
 * The order of two values of one type (a date and a datetime count as one); null when they have none, so every
 * comparison with them is false.
 */
export function compare(a: Value, b: Value): number | null {
  if (a.kind === "number" && b.kind === "number") {
    if (Number.isNaN(a.number) || Number.isNaN(b.number)) return null;
    return a.number < b.number ? -1 : a.number > b.number ? 1 : 0;
  }
  if (a.kind === "text" && b.kind === "text") return a.text < b.text ? -1 : a.text > b.text ? 1 : 0;
  if ((a.kind === "date" || a.kind === "datetime") && (b.kind === "date" || b.kind === "datetime")) {
    const x = moment(a);
    const y = moment(b);
    return x < y ? -1 : x > y ? 1 : 0;
  }
  return null;
}

// Both written as "yyyy-MM-ddTHH:mm:ss[.fraction]" with the fraction's trailing zeros gone, so ordering the text orders the moments.
const moment = (v: Value): string => (v.kind === "date" ? `${v.date}T00:00:00` : v.kind === "datetime" ? v.dateTime : "");

/** As JavaScript counts truth: null, false, 0 and "" are false, everything else is true. */
export function truthy(v: Value): boolean {
  switch (v.kind) {
    case "null": return false;
    case "boolean": return v.boolean;
    case "number": return v.number !== 0 && !Number.isNaN(v.number);
    case "text": return v.text.length > 0;
    default: return true;
  }
}

/** A value as text, for joining texts and for `toString()`. */
export function display(v: Value): string {
  switch (v.kind) {
    case "null": return "";
    case "text": return v.text;
    case "number": return formatNumber(v.number);
    case "boolean": return v.boolean ? "true" : "false";
    case "date": return v.date;
    case "datetime": return v.dateTime.slice(0, 19);
    case "list": return v.items.map(display).join(", ");
    case "object": return `{${[...v.values].map(([key, value]) => `${key}: ${display(value)}`).join(", ")}}`;
    case "file":
    case "noteRef": return v.note.path;
    case "regex": return v.regex.toString();
  }
}
