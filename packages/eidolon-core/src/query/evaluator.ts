// SPDX-License-Identifier: AGPL-3.0-or-later
// Evaluates expressions of the Bases language over a note (Kumbu.Core Queries/Evaluator). As in Obsidian, what an
// expression can't give for a note (a function, method or formula that doesn't exist, formulas in a circle, a calculation
// between types that don't fit) is an error value in its cell, and it taints whatever uses it (bases spec 2026-11-25
// decision 9); compiling names each unknown function, method and formula as a QueryWarning. Only what can't be read at
// all (syntax, a regular expression that doesn't compile, the limits) is a QueryError, before any note is read. The cases
// are in tests/query-conformance/expressions.
import { RE2JS } from "re2js";
import { foldCase } from "../links/text";
import { QueryError, inExpression, type QueryWarning } from "./errors";
import type { Expr, Operator } from "./expr";
import { ExpressionParser, isWhiteSpace } from "./parser";
import type { QueryNote } from "./note";
import { Properties } from "./properties";
import {
  NULL, booleanValue, dateValue, errorValue, fileValue, formatNumber, listValue, noteRefValue, numberValue, objectValue,
  regexValue, textValue, type Value,
} from "./value";

/**
 * What an expression is evaluated for: the note of the row, the note the query is shown in (`this`, if any) and the clock
 * of the run, fixed so every row sees the same `now()`: a date and time without a zone, "yyyy-MM-ddTHH:mm:ss" with up to 7
 * fractional digits.
 */
export type Scope = { readonly note: QueryNote; readonly this: QueryNote | null; readonly now: string };

/** One function, method or field of the language: its name, how it is written, and what it gives. */
export type LanguageEntry = { readonly name: string; readonly written: string; readonly gives: string };

const entry = (name: string, written: string, gives: string): LanguageEntry => ({ name, written, gives });

/** The functions, as `help query` shows them: the help and what compiles are one list, so they can't drift. */
export const FunctionReference: readonly LanguageEntry[] = [
  entry("if", "if(condition, then, else)", "then when the condition holds, else else; only the branch taken is evaluated"),
  entry("now", "now()", "the date of the run with its time, in your time zone"),
  entry("today", "today()", "the date of the run, without a time"),
  entry("date", "date(text)", "the date a text writes, with its time if it has one (2026-11-23, 2026-11-23T14:00), else null"),
  entry("number", "number(value)", "a number from a number, a checkbox (1 or 0) or a text, read as JavaScript's Number() reads it (12, 1e3, 0x1F, Infinity; the empty text is 0), else an error"),
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
  entry("date", "date.date()", "the date without its time"),
  entry("hasTag", "file.hasTag(tag, ...)", "whether the note carries one of the tags, or one nested below it, ignoring case and a leading #"),
  entry("inFolder", "file.inFolder(folder)", "whether the file lies in the folder or below it"),
  entry("hasProperty", "file.hasProperty(name)", "whether the note's frontmatter has the property"),
];

/** The fields, by what they are read on. */
export const FieldReference: readonly LanguageEntry[] = [
  entry("name", "file.name", "the file's name, a note's without .md (Ann, photo.png)"),
  entry("basename", "file.basename", "the file's name without its extension (Ann, photo)"),
  entry("path", "file.path", "the path in the vault (People/Ann.md)"),
  entry("folder", "file.folder", "the folder in the vault (People; / at the root)"),
  entry("ext", "file.ext", "the extension (md, png)"),
  entry("size", "file.size", "the size in bytes"),
  entry("ctime", "file.ctime", "when the file was created, a date with its time"),
  entry("mtime", "file.mtime", "when the file was last changed, a date with its time"),
  entry("tags", "file.tags", "the note's tags with their #, text and frontmatter; none for other files"),
  entry("properties", "file.properties", "the frontmatter as an object; empty for other files"),
  entry("length", "text.length, list.length", "how many characters or items"),
  entry("year", "date.year, .month, .day", "a date's parts, and .hour, .minute, .second (0 for a date without a time)"),
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

const CYCLE = errorValue("Infinite loop detected in formula");

type ErrorOf = Extract<Value, { kind: "error" }>;
const firstError = (values: readonly Value[]): ErrorOf | undefined => values.find((v): v is ErrorOf => v.kind === "error");

export class Evaluator {
  private readonly formulaExprs = new Map<string, Expr>();
  private readonly regexes = new Map<Expr, RE2JS>();
  private readonly checkedLevels = new Map<string, number>();
  private deepest = 0;
  private readonly found: QueryWarning[] = [];
  private checking = "";
  private readonly values = new Map<string, Value>();
  private readonly evaluating = new Set<string>();
  private valuesFor: Scope | null = null;

  constructor(private readonly formulaTexts: ReadonlyMap<string, string> = new Map()) {}

  /** Parses and checks an expression; throws `QuerySyntaxError` or `QueryError`. */
  compile(text: string): Expr {
    try {
      const expr = ExpressionParser.parse(text);
      this.checking = text;
      this.check(expr, []);
      return expr;
    } catch (e) {
      // Defence in depth: the parser's limit and the walk of operator chains keep the stack bounded; should it still run
      // out, that is an error like any other.
      if (e instanceof RangeError) throw new QueryError("too-deep", "The expression is nested too deeply.", 0);
      throw e;
    }
  }

  /** What the expressions compiled so far name that doesn't exist, each once, in the order found. */
  get warnings(): readonly QueryWarning[] {
    return this.found;
  }

  evaluate(expr: Expr, scope: Scope): Value {
    switch (expr.kind) {
      case "literal": return expr.value;
      case "name": return name(expr.name, scope);
      case "member":
        if (expr.target.kind === "name" && expr.target.name === "formula") return this.formulaValue(expr.name, scope);
        return field(this.evaluate(expr.target, scope), expr.name);
      case "index": return index(this.evaluate(expr.target, scope), this.evaluate(expr.index, scope));
      case "call":
        if (expr.callee.kind === "name") return this.call(expr.callee.name, expr.args, scope);
        if (expr.callee.kind === "member")
          return method(this.evaluate(expr.callee.target, scope), expr.callee.name, expr.args.map((a) => this.evaluate(a, scope)));
        return errorValue("Only a function or a method can be called");
      case "unary": {
        const operand = this.evaluate(expr.operand, scope);
        if (operand.kind === "error") return operand;
        if (expr.operator === "Not") return booleanValue(!truthy(operand));
        return operand.kind === "number" ? numberValue(-operand.number) : NULL;
      }
      case "binary": {
        // The same walk of the left spine as in check: the innermost left first, then each right on the way out.
        const spine = leftSpine(expr);
        let value = this.evaluate(spine[spine.length - 1]!.left, scope);
        for (let i = spine.length - 1; i >= 0; i--) {
          if (value.kind === "error") return value;
          const { operator, right } = spine[i]!;
          if (operator === "And") value = !truthy(value) ? value : this.evaluate(right, scope);
          else if (operator === "Or") value = truthy(value) ? value : this.evaluate(right, scope);
          else value = binary(operator, value, this.evaluate(right, scope));
        }
        return value;
      }
      case "list": {
        const items = expr.items.map((i) => this.evaluate(i, scope));
        return firstError(items) ?? listValue(items);
      }
      case "object": {
        // A key written twice keeps its first place and its last value.
        const last = new Map<string, Expr>();
        for (const [key, value] of expr.entries) last.set(key, value);
        const values = new Map([...last].map(([key, value]) => [key, this.evaluate(value, scope)]));
        return firstError([...values.values()]) ?? objectValue(values);
      }
      case "regex": return regexValue(this.regexes.get(expr)!);
    }
  }

  // A formula's value for a row is worked out once, however often the row's expressions use it; one that is asked for
  // while it is being worked out uses itself, in a circle.
  private formulaValue(formulaName: string, scope: Scope): Value {
    const expr = this.formulaExprs.get(formulaName);
    if (expr === undefined) return errorValue(`Cannot find "${formulaName}" on type Formulas`);
    if (scope !== this.valuesFor) {
      this.values.clear();
      this.valuesFor = scope;
    }
    let value = this.values.get(formulaName);
    if (value !== undefined) return value;
    if (this.evaluating.has(formulaName)) return CYCLE;
    this.evaluating.add(formulaName);
    value = this.evaluate(expr, scope);
    this.evaluating.delete(formulaName);
    this.values.set(formulaName, value);
    return value;
  }

  private warn(code: string, message: string, position: number): void {
    if (!this.found.some((w) => w.code === code && w.message === message && w.expression === this.checking && w.position === position))
      this.found.push({ code, message, expression: this.checking, position });
  }

  // Formulas are checked where they are used, so one broken formula fails only the expressions that use it.
  private check(expr: Expr, chain: string[]): void {
    switch (expr.kind) {
      case "call":
        if (expr.callee.kind === "name") {
          if (!FUNCTIONS.has(expr.callee.name)) this.warn("unknown-function", `There is no function '${expr.callee.name}'.`, expr.callee.position);
        } else if (expr.callee.kind === "member") {
          if (!METHODS.has(expr.callee.name)) this.warn("unknown-method", `There is no method '${expr.callee.name}'.`, expr.callee.position);
          this.check(expr.callee.target, chain);
        } else {
          this.warn("unknown-function", "Only a function or a method can be called.", expr.position);
          this.check(expr.callee, chain);
        }
        for (const a of expr.args) this.check(a, chain);
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

  // A formula that doesn't exist, or uses itself, is a warning here and an error value in the cells; the depth limit
  // stays an error, since it bounds the work of every row.
  private formula(reference: Expr & { kind: "member" }, chain: string[]): void {
    const { name } = reference;
    const text = this.formulaTexts.get(name);
    if (text === undefined) {
      this.warn("unknown-formula", `There is no formula '${name}'.`, reference.position);
      return;
    }
    if (chain.includes(name)) {
      this.warn("formula-cycle", `The formulas use each other in a circle: ${[...chain, name].join(" → ")}.`, reference.position);
      return;
    }
    if (chain.length >= ExpressionParser.MAX_DEPTH)
      throw new QueryError("too-deep", `The formulas use each other more than ${ExpressionParser.MAX_DEPTH} levels deep.`, reference.position);

    // A formula is checked once: its check holds wherever it is used again, but for the depth, which depends on where.
    // So its levels of formulas are kept, and a reference from which they would pass the limit checks it again, to fail where it did.
    const levels = this.checkedLevels.get(name);
    if (levels !== undefined && chain.length + levels <= ExpressionParser.MAX_DEPTH) {
      this.deepest = Math.max(this.deepest, chain.length + levels);
      return;
    }

    let expr = this.formulaExprs.get(name);
    if (expr === undefined) {
      expr = inExpression(text, () => ExpressionParser.parse(text));
      this.formulaExprs.set(name, expr);
    }

    const parsed = expr;
    const [outer, outerText] = [this.deepest, this.checking];
    chain.push(name);
    this.deepest = chain.length;
    this.checking = text;
    inExpression(text, () => this.check(parsed, chain));
    this.checking = outerText;
    chain.pop();
    this.checkedLevels.set(name, this.deepest - chain.length);
    this.deepest = Math.max(outer, this.deepest);
  }

  private call(functionName: string, args: readonly Expr[], scope: Scope): Value {
    const argument = (i: number): Value => (i < args.length ? this.evaluate(args[i]!, scope) : NULL);
    if (!FUNCTIONS.has(functionName)) return errorValue(`Cannot find function "${functionName}"`);
    if (functionName === "now") return dateValue(scope.now);
    if (functionName === "today") return dateValue(scope.now.slice(0, 10));
    const first = argument(0);
    if (first.kind === "error") return first;
    switch (functionName) {
      // Only the branch taken is evaluated, as in JavaScript.
      case "if": return truthy(first) ? argument(1) : argument(2);
      case "date": return dateOf(first);
      case "number":
        if (first.kind === "number") return first;
        if (first.kind === "boolean") return numberValue(first.boolean ? 1 : 0);
        return first.kind === "text" ? numberOf(first.text) : NULL;
      default: // list
        return first.kind === "list" ? first : first.kind === "null" ? listValue([]) : listValue([first]);
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

// A text as JavaScript's Number() reads it: decimal with an exponent, Infinity, 0x, 0o and 0b; the empty text is 0. Trimmed
// as the server trims, so both engines read the same texts.
function numberOf(text: string): Value {
  const n = Number(trim(text));
  return Number.isNaN(n) ? errorValue(`Unable to parse "${text}" as a number.`) : numberValue(n);
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
const folderOf = (path: string) => (path.lastIndexOf("/") >= 0 ? path.slice(0, path.lastIndexOf("/")) : "/");

// .NET's Path.GetFileNameWithoutExtension and GetExtension: the last dot starts the extension, unless it ends the name.
const baseName = (file: string) => (file.lastIndexOf(".") >= 0 ? file.slice(0, file.lastIndexOf(".")) : file);
const extension = (file: string) => (file.lastIndexOf(".") >= 0 && file.lastIndexOf(".") < file.length - 1 ? file.slice(file.lastIndexOf(".") + 1) : "");

function field(value: Value, fieldName: string): Value {
  switch (value.kind) {
    case "error":
    case "null": return value;
    case "file":
      switch (fieldName) {
        case "name": return textValue(value.note.isNote ? fileName(value.note.path).slice(0, -".md".length) : fileName(value.note.path));
        case "basename": return textValue(baseName(fileName(value.note.path)));
        case "path": return textValue(value.note.path);
        case "folder": return textValue(folderOf(value.note.path));
        case "ext": return textValue(extension(fileName(value.note.path)));
        case "size": return numberValue(value.note.size);
        case "ctime": return value.note.created === null ? NULL : dateValue(value.note.created);
        case "mtime": return value.note.modified === null ? NULL : dateValue(value.note.modified);
        case "tags": return listValue(value.note.tags.map(textValue));
        case "properties": return objectValue(value.note.properties.values);
        default: return noField(value, fieldName);
      }
    case "noteRef":
      if (fieldName === "file") return fileValue(value.note);
      if (fieldName === "note") return objectValue(value.note.properties.values);
      return property(value.note, fieldName);
    case "object": return value.values.get(fieldName) ?? NULL;
    case "text": return fieldName === "length" ? numberValue(value.text.length) : noField(value, fieldName);
    case "list": return fieldName === "length" ? numberValue(value.items.length) : noField(value, fieldName);
    case "date": return part(value, fieldName);
    default: return noField(value, fieldName);
  }
}

const noField = (value: Value, fieldName: string): Value => errorValue(`Cannot find "${fieldName}" on type ${typeName(value)}`);

/** A value's type as Obsidian's messages name it. */
export function typeName(value: Value): string {
  switch (value.kind) {
    case "null": return "Null";
    case "text": return "String";
    case "number": return "Number";
    case "boolean": return "Boolean";
    case "date": return "Date";
    case "list": return "List";
    case "object": return "Object";
    case "file":
    case "noteRef": return "File";
    case "regex": return "RegExp";
    case "error": return "Error";
  }
}

// The parts of "yyyy-MM-ddTHH:mm:ss…"; a date without a time is its midnight.
function part(date: Value & { kind: "date" }, partName: string): Value {
  const { moment } = date;
  switch (partName) {
    case "year": return numberValue(Number(moment.slice(0, 4)));
    case "month": return numberValue(Number(moment.slice(5, 7)));
    case "day": return numberValue(Number(moment.slice(8, 10)));
    case "hour": return numberValue(Number(moment.slice(11, 13)));
    case "minute": return numberValue(Number(moment.slice(14, 16)));
    case "second": return numberValue(Number(moment.slice(17, 19)));
    default: return noField(date, partName);
  }
}

function index(target: Value, at: Value): Value {
  if (target.kind === "error") return target;
  if (at.kind === "error") return at;
  if (target.kind === "list" && at.kind === "number")
    return at.number >= 0 && at.number < target.items.length && at.number === Math.floor(at.number) ? target.items[at.number]! : NULL;
  if (target.kind === "object" && at.kind === "text") return target.values.get(at.text) ?? NULL;
  if (target.kind === "noteRef" && at.kind === "text") return property(target.note, at.text);
  return NULL;
}

function dateOf(v: Value): Value {
  if (v.kind === "text") {
    const read = Properties.inferred(trim(v.text));
    return read.kind === "date" ? read : NULL;
  }
  return v.kind === "date" ? v : NULL;
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
  const missing = (): Value => errorValue(`Cannot find function "${methodName}" on type ${typeName(target)}`);
  if (target.kind === "error") return target;
  const error = firstError(args);
  if (error !== undefined) return error;

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
        default: return missing();
      }
    case "list":
      switch (methodName) {
        case "contains": return booleanValue(target.items.some((i) => equal(i, argument(0))));
        case "join": return textValue(target.items.map(display).join(textArgument(0) ?? ","));
        default: return missing();
      }
    case "number":
      switch (methodName) {
        case "abs": return numberValue(Math.abs(target.number));
        case "floor": return numberValue(Math.floor(target.number));
        case "ceil": return numberValue(Math.ceil(target.number));
        // Halves go up, as the server's engine rounds (floor(x + 0.5)): -2.5 becomes -2.
        case "round": return numberValue(Math.floor(target.number + 0.5));
        default: return missing();
      }
    case "date": return methodName === "date" ? dateValue(target.moment.slice(0, 10)) : missing();
    case "file":
      switch (methodName) {
        case "hasTag": return booleanValue(args.some((a) => a.kind === "text" && hasTag(target.note, a.text)));
        case "inFolder": return bool(textArgument(0) === null ? null : inFolder(target.note.path, textArgument(0)!));
        case "hasProperty": return bool(textArgument(0) === null ? null : target.note.properties.values.has(textArgument(0)!));
        default: return missing();
      }
    default: return missing();
  }
}

// A tag holds its nested tags: "project" finds #project/kumbu, not #projects.
function hasTag(note: QueryNote, tag: string): boolean {
  const wanted = foldCase(`#${tag.replace(/^#+/, "")}`);
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
  if (left.kind === "error") return left;
  if (right.kind === "error") return right;
  switch (operator) {
    case "Equal": return booleanValue(equal(left, right));
    case "NotEqual": return booleanValue(!equal(left, right));
    case "Less": return booleanValue((compare(left, right) ?? NaN) < 0);
    case "LessOrEqual": return booleanValue((compare(left, right) ?? NaN) <= 0);
    case "Greater": return booleanValue((compare(left, right) ?? NaN) > 0);
    case "GreaterOrEqual": return booleanValue((compare(left, right) ?? NaN) >= 0);
  }
  if (operator === "Add" && (left.kind === "text" || right.kind === "text")) return textValue(display(left) + display(right));
  if (left.kind === "null" || right.kind === "null") return NULL;
  if (left.kind !== "number" || right.kind !== "number") return errorValue(`Invalid operator between ${typeName(left)} and ${typeName(right)}`);
  const l = left.number;
  const r = right.number;
  const result = operator === "Add" ? l + r : operator === "Subtract" ? l - r : operator === "Multiply" ? l * r : operator === "Divide" ? l / r : l % r;
  return numberValue(result);
}

/** Equal within a type; values of different types are never equal, and null equals only null. */
export function equal(a: Value, b: Value): boolean {
  switch (a.kind) {
    case "null": return b.kind === "null";
    case "number": return b.kind === "number" && a.number === b.number;
    case "text": return b.kind === "text" && a.text === b.text;
    case "boolean": return b.kind === "boolean" && a.boolean === b.boolean;
    case "date": return b.kind === "date" && a.moment === b.moment;
    case "file": return b.kind === "file" && a.note.path === b.note.path;
    case "noteRef": return b.kind === "noteRef" && a.note.path === b.note.path;
    case "list": return b.kind === "list" && a.items.length === b.items.length && a.items.every((item, i) => equal(item, b.items[i]!));
    case "object":
      return b.kind === "object" && a.values.size === b.values.size
        && [...a.values].every(([key, value]) => b.values.has(key) && equal(value, b.values.get(key)!));
    default: return false;
  }
}

/** The order of two values of one type; null when they have none, so every comparison with them is false. */
export function compare(a: Value, b: Value): number | null {
  if (a.kind === "number" && b.kind === "number") {
    if (Number.isNaN(a.number) || Number.isNaN(b.number)) return null;
    return a.number < b.number ? -1 : a.number > b.number ? 1 : 0;
  }
  if (a.kind === "text" && b.kind === "text") return a.text < b.text ? -1 : a.text > b.text ? 1 : 0;
  // Both written as "yyyy-MM-ddTHH:mm:ss[.fraction]" with the fraction's trailing zeros gone, so ordering the text orders the moments.
  if (a.kind === "date" && b.kind === "date") return a.moment < b.moment ? -1 : a.moment > b.moment ? 1 : 0;
  return null;
}

/** As JavaScript counts truth: null, false, 0 and "" are false, everything else is true; an error, which whatever uses it
 * passes on before asking this, is false too. */
export function truthy(v: Value): boolean {
  switch (v.kind) {
    case "null":
    case "error": return false;
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
    case "date": return v.hasTime ? v.moment.slice(0, 19) : v.moment.slice(0, 10);
    case "list": return v.items.map(display).join(", ");
    case "object": return `{${[...v.values].map(([key, value]) => `${key}: ${display(value)}`).join(", ")}}`;
    case "file":
    case "noteRef": return v.note.path;
    case "regex": return v.regex.toString();
    case "error": return `error: ${v.message}`;
  }
}
