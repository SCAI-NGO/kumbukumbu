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
  NULL, asMilliseconds, booleanValue, dateValue, durationValue, errorValue, fileValue, formatNumber, listValue, noteRefValue,
  numberValue, objectValue, regexValue, textValue, type Value,
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
  entry("duration", "duration(text)", "the duration a text writes, in the units a date takes (1d, 2 hours, 1y 2M); a text that writes none is an error"),
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

// && and || give a checkbox, as Obsidian's do (finding 12), and pass on an error of the side they had to read.
const logical = (v: Value): Value => (v.kind === "error" ? v : booleanValue(truthy(v)));

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
  private level = 0;
  private valuesFor: Scope | null = null;

  constructor(private readonly formulaTexts: ReadonlyMap<string, string> = new Map()) {}

  /** Parses and checks an expression; throws `QuerySyntaxError` or `QueryError`. */
  compile(text: string): Expr {
    try {
      const expr = ExpressionParser.parse(text);
      this.checking = text;
      this.level = 0;
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

  /** How many levels down compiling or evaluating an expression may go, its own and those of the formulas it uses added
   * up along the way: the same as the server's, whose thread stack of 1.5 MB must hold them. */
  static readonly MAX_LEVELS = 512;

  /** Evaluates a compiled expression for a row; throws `QueryError` `too-deep` when it and the formulas it uses go more
   * than `MAX_LEVELS` levels down. */
  evaluate(expr: Expr, scope: Scope): Value {
    this.level = 0;
    this.evaluating.clear();
    try {
      return this.eval(expr, scope);
    } catch (e) {
      // Defence in depth, as in compile: a stack that runs out first is the same error.
      if (e instanceof RangeError) throw new QueryError("too-deep", "The expression and the formulas it uses are nested too deeply.", 0);
      throw e;
    }
  }

  // Both walks go down the formulas an expression uses, so both count their levels against one budget.
  private down(expr: Expr): void {
    if (++this.level > Evaluator.MAX_LEVELS)
      throw new QueryError("too-deep", `The expression and the formulas it uses go more than ${Evaluator.MAX_LEVELS} levels deep.`, expr.position);
  }

  // Counted here, not only in the check: a check cut short by a cycle, or by a formula checked before, can't see how deep
  // the formulas really go along this path.
  private eval(expr: Expr, scope: Scope): Value {
    this.down(expr);
    const value = this.valueOf(expr, scope);
    this.level--;
    return value;
  }

  private valueOf(expr: Expr, scope: Scope): Value {
    switch (expr.kind) {
      case "literal": return expr.value;
      case "name": return name(expr.name, scope);
      case "member":
        if (expr.target.kind === "name" && expr.target.name === "formula") return this.formulaValue(expr.name, scope);
        return field(this.eval(expr.target, scope), expr.name);
      case "index": return index(this.eval(expr.target, scope), this.eval(expr.index, scope));
      case "call":
        if (expr.callee.kind === "name") return this.call(expr.callee.name, expr.args, scope);
        if (expr.callee.kind === "member")
          return method(this.eval(expr.callee.target, scope), expr.callee.name, expr.args.map((a) => this.eval(a, scope)));
        return errorValue("Only a function or a method can be called");
      case "unary": {
        const operand = this.eval(expr.operand, scope);
        if (operand.kind === "error") return operand;
        if (expr.operator === "Not") return booleanValue(!truthy(operand));
        return operand.kind === "number" ? numberValue(-operand.number) : NULL;
      }
      case "binary": {
        // The same walk of the left spine as in check: the innermost left first, then each right on the way out.
        const spine = leftSpine(expr);
        let value = this.eval(spine[spine.length - 1]!.left, scope);
        for (let i = spine.length - 1; i >= 0; i--) {
          if (value.kind === "error") return value;
          const { operator, right } = spine[i]!;
          if (operator === "And") value = !truthy(value) ? booleanValue(false) : logical(this.eval(right, scope));
          else if (operator === "Or") value = truthy(value) ? booleanValue(true) : logical(this.eval(right, scope));
          else value = binary(operator, value, this.eval(right, scope));
        }
        return value;
      }
      case "list": {
        const items = expr.items.map((i) => this.eval(i, scope));
        return firstError(items) ?? listValue(items);
      }
      case "object": {
        // A key written twice keeps its first place and its last value.
        const last = new Map<string, Expr>();
        for (const [key, value] of expr.entries) last.set(key, value);
        const values = new Map([...last].map(([key, value]) => [key, this.eval(value, scope)]));
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
    value = this.eval(expr, scope);
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
    this.down(expr);
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
    this.level--;
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
    const argument = (i: number): Value => (i < args.length ? this.eval(args[i]!, scope) : NULL);
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
      case "duration": return first.kind === "text" ? durationOf(first.text) : first.kind === "duration" ? first : NULL;
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
// as the server trims, so both engines read the same texts: what Number() would trim beyond that (U+FEFF) is no number.
function numberOf(text: string): Value {
  const trimmed = trim(text);
  const n = /^\s|\s$/.test(trimmed) ? NaN : Number(trimmed);
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
    case "duration": return "Duration";
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
    case "Less":
    case "LessOrEqual":
    case "Greater":
    case "GreaterOrEqual": return ordered(operator, left, right);
  }
  const sign = operator === "Subtract" ? -1 : 1;
  const shifting = operator === "Add" || operator === "Subtract";
  if (shifting && left.kind === "date" && right.kind === "text") {
    const span = durationOf(right.text);
    return span.kind === "duration" ? shifted(left, span, sign) : span;
  }
  if (shifting && left.kind === "date" && right.kind === "duration") return shifted(left, right, sign);
  if (operator === "Subtract" && left.kind === "date" && right.kind === "date") return durationValue(0, 0, millisecondsBetween(left.moment, right.moment));
  if (shifting && left.kind === "duration" && right.kind === "duration")
    return durationValue(left.months + sign * right.months, left.days + sign * right.days, left.milliseconds + sign * right.milliseconds);
  if (operator === "Multiply" && left.kind === "duration" && right.kind === "number")
    return durationValue(left.months * right.number, left.days * right.number, left.milliseconds * right.number);
  if (operator === "Divide" && left.kind === "duration" && right.kind === "number")
    return durationValue(left.months / right.number, left.days / right.number, left.milliseconds / right.number);
  if (operator === "Add" && (left.kind === "text" || right.kind === "text")) return textValue(display(left) + display(right));
  return arithmetic(operator, left, right);
}

// -, *, / and % read a text that reads as a number as that number, as JavaScript does; + joins it, as above.
function arithmetic(operator: Operator, left: Value, right: Value): Value {
  const read = (v: Value): Value => {
    const n = operator !== "Add" && v.kind === "text" ? readsAsNumber(v.text) : null;
    return n === null ? v : numberValue(n);
  };
  const [a, b] = [read(left), read(right)];
  if (a.kind === "number" && b.kind === "number") {
    const [l, r] = [a.number, b.number];
    return numberValue(operator === "Add" ? l + r : operator === "Subtract" ? l - r : operator === "Multiply" ? l * r : operator === "Divide" ? l / r : l % r);
  }
  if (a.kind === "null" || b.kind === "null") return NULL;
  return errorValue(`Invalid operator between ${typeName(left)} and ${typeName(right)}`);
}

// A text of durations: a number and its unit, as often as written, the space between them optional. The short units
// count case (M a month, m a minute); their names don't.
const DURATION_TERM = /[ \t\r\n]*([+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+))[ \t\r\n]*([A-Za-z]+)/y;

function durationOf(text: string): Value {
  let [months, days, milliseconds, at] = [0, 0, 0, 0];
  for (;;) {
    DURATION_TERM.lastIndex = at;
    const term = DURATION_TERM.exec(text);
    if (term === null) break;
    const amount = Number(term[1]);
    const unit = term[2]!.length === 1 ? term[2]! : term[2]!.toLowerCase();
    switch (unit) {
      case "y": case "year": case "years": months += amount * 12; break;
      case "M": case "month": case "months": months += amount; break;
      case "w": case "week": case "weeks": days += amount * 7; break;
      case "d": case "day": case "days": days += amount; break;
      case "h": case "hour": case "hours": milliseconds += amount * 3_600_000; break;
      case "m": case "minute": case "minutes": milliseconds += amount * 60_000; break;
      case "s": case "second": case "seconds": milliseconds += amount * 1000; break;
      default: return invalidDuration(text);
    }
    at = DURATION_TERM.lastIndex;
  }
  return at > 0 && /^[ \t\r\n]*$/.test(text.slice(at)) ? durationValue(months, days, milliseconds) : invalidDuration(text);
}

const invalidDuration = (text: string): Value => errorValue(`Invalid duration "${text}"`);

// A moment "yyyy-MM-ddTHH:mm:ss[.fraction]" as milliseconds, read as UTC since a moment has no zone, and the ticks of
// 100 ns below them, so a fraction of seven digits survives, as .NET keeps it.
type Instant = { ms: number; ticks: number };
// Date.UTC reads the years 0 to 99 as 1900 to 1999; setUTCFullYear doesn't.
function utc(year: number, month: number, day: number): number {
  const at = new Date(0);
  at.setUTCFullYear(year, month, day);
  return at.getTime();
}
const MIN_MS = utc(1, 0, 1);
const MAX_MS = utc(10000, 0, 1) - 1;

function instant(moment: string): Instant {
  const day = utc(Number(moment.slice(0, 4)), Number(moment.slice(5, 7)) - 1, Number(moment.slice(8, 10)));
  const time = ((Number(moment.slice(11, 13)) * 60 + Number(moment.slice(14, 16))) * 60 + Number(moment.slice(17, 19))) * 1000;
  const fraction = moment.slice(20).padEnd(7, "0");
  return { ms: day + time + Number(fraction.slice(0, 3)), ticks: Number(fraction.slice(3)) };
}

function momentOf({ ms, ticks }: Instant): string {
  const at = new Date(ms);
  const pad = (n: number, width = 2) => String(n).padStart(width, "0");
  const fraction = (pad(at.getUTCMilliseconds(), 3) + pad(ticks, 4)).replace(/0+$/, "");
  return `${pad(at.getUTCFullYear(), 4)}-${pad(at.getUTCMonth() + 1)}-${pad(at.getUTCDate())}T${pad(at.getUTCHours())}:${pad(at.getUTCMinutes())}:${pad(at.getUTCSeconds())}${fraction.length > 0 ? `.${fraction}` : ""}`;
}

const millisecondsBetween = (a: string, b: string): number => {
  const [x, y] = [instant(a), instant(b)];
  return x.ms - y.ms + (x.ticks - y.ticks) / 10_000;
};

// Moment's absRound: halves away from zero.
const absRound = (n: number): number => (n < 0 ? -Math.round(-n) : Math.round(n));

// As Moment adds a duration: its milliseconds, then its days, then its months, which clamp the day (31 January and a
// month is the end of February); days and months are rounded, halves away from zero. A date without a time gets one
// only from milliseconds. Outside the years 1 to 9999 there is no date.
function shifted(date: Value & { kind: "date" }, span: Value & { kind: "duration" }, sign: number): Value {
  const invalid = errorValue("Invalid date");
  const inRange = (ms: number) => ms >= MIN_MS && ms <= MAX_MS;
  const start = instant(date.moment);
  const total = sign * span.milliseconds;
  const whole = Math.trunc(total);
  const ticks = start.ticks + absRound((total - whole) * 10_000);
  let ms = start.ms + whole + Math.floor(ticks / 10_000);
  if (!inRange(ms)) return invalid;
  ms += absRound(sign * span.days) * 86_400_000;
  if (!inRange(ms)) return invalid;
  const at = new Date(ms);
  const month = at.getUTCFullYear() * 12 + at.getUTCMonth() + absRound(sign * span.months);
  const [year, monthOfYear] = [Math.floor(month / 12), ((month % 12) + 12) % 12];
  if (!(year >= 1 && year <= 9999)) return invalid;
  const lastDay = new Date(utc(year, monthOfYear + 1, 0)).getUTCDate();
  at.setUTCFullYear(year, monthOfYear, Math.min(at.getUTCDate(), lastDay));
  return { kind: "date", moment: momentOf({ ms: at.getTime(), ticks: ((ticks % 10_000) + 10_000) % 10_000 }), hasTime: date.hasTime || span.milliseconds !== 0 };
}

// A text reads as a number where JavaScript's Number() reads it, the empty text not.
function readsAsNumber(text: string): number | null {
  if (trim(text).length === 0) return null;
  const n = numberOf(text);
  return n.kind === "number" ? n.number : null;
}

// Two values as numbers where one is a number or a duration and the other one too, or a text that reads as one.
function numbers(a: Value, b: Value): [number, number] | null {
  const plain = (v: Value): number | null => (v.kind === "number" ? v.number : v.kind === "duration" ? asMilliseconds(v) : null);
  const read = (v: Value): number | null => plain(v) ?? (v.kind === "text" ? readsAsNumber(v.text) : null);
  if (plain(a) === null && plain(b) === null) return null;
  const [x, y] = [read(a), read(b)];
  return x === null || y === null ? null : [x, y];
}

// Until probe 2 of the bases spec (decision 12): numbers, and texts that read as one, by their numbers (NaN orders with
// nothing, so false); texts by code point; dates by moment; anything else, null included, has no order: null.
function ordered(operator: Operator, left: Value, right: Value): Value {
  const holds = (order: number) =>
    operator === "Less" ? order < 0 : operator === "LessOrEqual" ? order <= 0 : operator === "Greater" ? order > 0 : order >= 0;
  const pair = numbers(left, right);
  if (pair !== null) {
    const [x, y] = pair;
    return booleanValue(!Number.isNaN(x) && !Number.isNaN(y) && holds(x < y ? -1 : x > y ? 1 : 0));
  }
  const order = compare(left, right);
  return order === null ? NULL : booleanValue(holds(order));
}

/** Equal as Obsidian's == is until probe 2 (bases spec decision 12): a text that reads as a number equals that number, a
 * duration its milliseconds; otherwise within a type, and null equals only null. */
export function equal(a: Value, b: Value): boolean {
  const pair = numbers(a, b);
  if (pair !== null) return pair[0] === pair[1];
  switch (a.kind) {
    case "null": return b.kind === "null";
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

/** The order of two values of one type, as a sort sees them; null when they have none. */
export function compare(a: Value, b: Value): number | null {
  const byNumber = (x: number, y: number) => (Number.isNaN(x) || Number.isNaN(y) ? null : x < y ? -1 : x > y ? 1 : 0);
  if (a.kind === "number" && b.kind === "number") return byNumber(a.number, b.number);
  if (a.kind === "duration" && b.kind === "duration") return byNumber(asMilliseconds(a), asMilliseconds(b));
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
    case "duration": return formatNumber(asMilliseconds(v));
    case "error": return `error: ${v.message}`;
  }
}
