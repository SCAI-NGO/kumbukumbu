// SPDX-License-Identifier: AGPL-3.0-or-later
// Evaluates expressions of the Bases language over a note (Kumbu.Core Queries/Evaluator). As in Obsidian, what an
// expression can't give for a note (a function, method or formula that doesn't exist, formulas in a circle, a calculation
// between types that don't fit) is an error value in its cell, and it taints whatever uses it (bases spec 2026-11-25
// decision 9); compiling names each unknown function, method and formula, and each regular expression that is null, as a
// QueryWarning. Only syntax, found before any note is read, and the limits are a QueryError. The cases are in
// tests/query-conformance/expressions.
import { RE2JS } from "re2js";
import { foldCase } from "../links/text";
import { QueryError, inExpression, type QueryWarning } from "./errors";
import type { Expr, Operator } from "./expr";
import { format, fromNow } from "./moment";
import { ExpressionParser, isWhiteSpace } from "./parser";
import type { QueryNote } from "./note";
import { Properties } from "./properties";
import {
  NULL, asMilliseconds, booleanValue, dateValue, durationValue, errorValue, fileValue, formatNumber, linkValue, listValue,
  noteRefValue, numberValue, objectValue, regexValue, textValue, type Value,
} from "./value";
import { MAX_COUNT, MAX_SIZE as MAX_PATTERN_SIZE, regexShape, runs } from "./regexShape";
import { trimSlashes } from "./tags";
import { QueryVault } from "./vault";

/**
 * What an expression is evaluated for: the note of the row, the note the query is shown in (`this`, if any) and the clock
 * of the run, fixed so every row sees the same `now()`: a date and time without a zone, "yyyy-MM-ddTHH:mm:ss" with up to 7
 * fractional digits. `vault`: the files of the run, for what reaches beyond the row's file (file(), links, backlinks) and
 * for random(); left out, the row's note and the one it is shown in.
 */
export type Scope = { readonly note: QueryNote; readonly this: QueryNote | null; readonly now: string; readonly vault?: QueryVault };

/** One function, method or field of the language: its name, how it is written, and what it gives. */
export type LanguageEntry = { readonly name: string; readonly written: string; readonly gives: string };

const entry = (name: string, written: string, gives: string): LanguageEntry => ({ name, written, gives });

/** The functions, as `help query` shows them: the help and what compiles are one list, so they can't drift. */
export const FunctionReference: readonly LanguageEntry[] = [
  entry("if", "if(condition, then, else)", "then when the condition holds, else else (null without one); only the branch taken is evaluated"),
  entry("now", "now()", "the date of the run with its time, in your time zone"),
  entry("today", "today()", "the date of the run, without a time"),
  entry("date", "date(text)", "the date a text writes, with its time if it has one (2026-11-23, 2026-11-23 14:00), else null"),
  entry("duration", "duration(text)", "the duration a text writes, in the units a date takes (1d, 2 hours, 1y 2M); a text that writes none is an error"),
  entry("number", "number(value)", "a number from a number, a checkbox (1 or 0), a date (milliseconds since 1970) or a text, read as JavaScript's Number() reads it (12, 1e3, 0x1F, Infinity; the empty text is 0), else an error"),
  entry("list", "list(value)", "the value as a list: a list stays one, null stays null, anything else is a list of it"),
  entry("max", "max(number, ...)", "the largest of the numbers, nulls left out"),
  entry("min", "min(number, ...)", "the smallest of the numbers, nulls left out"),
  entry("random", "random()", "a number from 0 up to 1, one per row for the whole run"),
  entry("file", "file(path)", "the file of the vault at a path, or the one a link to it leads to ([[Ann]], a link, a file), else null"),
  entry("link", "link(path, display)", "a link to the file a path leads to, as [[path]] would, showing display if given"),
  entry("escapeHTML", "escapeHTML(text)", "the text with & < > \" ' written as HTML entities"),
];

/** The methods, by what they are called on, as `help query` shows them. */
export const MethodReference: readonly LanguageEntry[] = [
  entry("isEmpty", "any.isEmpty()", "true for null, the empty text, list or object"),
  entry("isTruthy", "any.isTruthy()", "whether the value counts as true (not null, false, 0 or the empty text)"),
  entry("isType", "any.isType(name)", "whether the value is of the type: string, number, boolean, date, duration, list, object, file, link or null"),
  entry("toString", "any.toString()", "the value as text"),
  entry("contains", "text.contains(text)", "whether the text holds the other, case counting"),
  entry("containsAll", "text.containsAll(text, ...)", "whether the text holds every one of the others"),
  entry("containsAny", "text.containsAny(text, ...)", "whether the text holds one of the others"),
  entry("startsWith", "text.startsWith(text)", "whether the text starts with the other"),
  entry("endsWith", "text.endsWith(text)", "whether the text ends with the other"),
  entry("lower", "text.lower()", "the text in lower case"),
  entry("title", "text.title()", "the text with each word's first letter in upper case"),
  entry("trim", "text.trim()", "the text without spaces at either end"),
  entry("replace", "text.replace(pattern, text)", "the text with a text replaced everywhere, or a /regex/ the first time (everywhere with g); $1 is a group"),
  entry("repeat", "text.repeat(count)", "the text that many times"),
  entry("reverse", "text.reverse()", "the text backwards"),
  entry("slice", "text.slice(start, end)", "the characters from start up to end (from the end when negative)"),
  entry("split", "text.split(separator, limit)", "the parts between a text or a /regex/, at most limit of them"),
  entry("contains", "list.contains(value)", "whether the list holds the value"),
  entry("containsAll", "list.containsAll(value, ...)", "whether the list holds every one of the values"),
  entry("containsAny", "list.containsAny(value, ...)", "whether the list holds one of the values"),
  entry("filter", "list.filter(condition)", "the items for which the condition holds; value is the item, index its place from 0"),
  entry("map", "list.map(expression)", "each item turned into the expression's value, with value and index"),
  entry("reduce", "list.reduce(expression, start)", "acc, from start, turned into the expression's value for each item in turn, with value and index"),
  entry("flat", "list.flat()", "the list with the items of lists in it put in their place"),
  entry("join", "list.join(text)", "the items as one text, the text between them (a comma without one)"),
  entry("reverse", "list.reverse()", "the items backwards"),
  entry("slice", "list.slice(start, end)", "the items from start up to end (from the end when negative)"),
  entry("sort", "list.sort()", "the items in order: numbers, dates and texts each in theirs, empty values last"),
  entry("unique", "list.unique()", "the items, each once"),
  entry("abs", "number.abs()", "the number without its sign"),
  entry("floor", "number.floor()", "the number rounded down"),
  entry("ceil", "number.ceil()", "the number rounded up"),
  entry("round", "number.round(digits)", "the number rounded, halves up, to so many digits after the point (none without)"),
  entry("toFixed", "number.toFixed(digits)", "the number as text with so many digits after the point"),
  entry("date", "date.date()", "the date without its time"),
  entry("format", "date.format(pattern)", "the date written in Moment's tokens (YYYY-MM-DD, D MMMM YYYY, HH:mm, [text] as it stands)"),
  entry("time", "date.time()", "the time as HH:mm:ss"),
  entry("relative", "date.relative()", "how far from now, in English (3 days ago, in an hour)"),
  entry("keys", "object.keys()", "the keys as a list"),
  entry("values", "object.values()", "the values as a list"),
  entry("asFile", "link.asFile()", "the file the link leads to, null if it leads to none"),
  entry("linksTo", "link.linksTo(file)", "whether the file the link leads to links to the file"),
  entry("asLink", "file.asLink(display)", "a link to the file, showing display if given"),
  entry("hasLink", "file.hasLink(file)", "whether the note links to the file (a file, a link or a path)"),
  entry("hasTag", "file.hasTag(tag, ...)", "whether the note carries one of the tags, or one nested below it, ignoring case and a leading #"),
  entry("inFolder", "file.inFolder(folder)", "whether the file lies in the folder or below it"),
  entry("hasProperty", "file.hasProperty(name)", "whether the note's frontmatter has the property"),
  entry("matches", "/regex/flags.matches(text)", "whether the regular expression matches the text (flags g, i, m, s, u; one beyond the part of RE2 both engines run, such as lookaround, is null)"),
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
  entry("links", "file.links", "the links of the note's text, as links; embeds are in file.embeds"),
  entry("embeds", "file.embeds", "the embeds of the note's text (![[photo.png]]), as links"),
  entry("backlinks", "file.backlinks", "the files that link to this one"),
  entry("file", "file.file", "the file itself"),
  entry("length", "text.length, list.length", "how many characters or items"),
  entry("year", "date.year, .month, .day", "a date's parts, and .hour, .minute, .second, .millisecond (0 for a date without a time)"),
];

type BinaryExpr = Extract<Expr, { kind: "binary" }>;
type CallExpr = Extract<Expr, { kind: "call" }>;
type ListOf = Extract<Value, { kind: "list" }>;
type FileOf = Extract<Value, { kind: "file" }>;
type RegexOf = Extract<Value, { kind: "regex" }>;

// The binary expressions down the left side of one, the outermost first.
function leftSpine(expr: BinaryExpr): BinaryExpr[] {
  const spine = [expr];
  for (let left = expr.left; left.kind === "binary"; left = left.left) spine.push(left);
  return spine;
}

const FUNCTIONS = new Set(FunctionReference.map((f) => f.name));
const METHODS = new Set(MethodReference.map((m) => m.name));
const LAMBDAS = new Set(["filter", "map", "reduce"]);

const CYCLE = errorValue("Infinite loop detected in formula");

type ErrorOf = Extract<Value, { kind: "error" }>;
const firstError = (values: readonly Value[]): ErrorOf | undefined => values.find((v): v is ErrorOf => v.kind === "error");

/** How large a value an expression may build for a note: its characters, and its items and keys, added up. A lambda or
 * formulas that use each other twice double a value at each step, so without a bound a short expression could fill the
 * tab's memory; a property's value is far smaller. */
export const MAX_SIZE = 1_000_000;
/** How many steps evaluating an expression for a note may take: each part evaluated, each item a method goes through,
 * and each 64 characters and items of what an operation reads or builds (`work`); a regular expression's search counts
 * 4 + its size steps for each 20 characters it may read. Lambdas multiply the work (a map in a map), so it is bounded as the size is. */
export const MAX_STEPS = 1_000_000;
/** How many steps one run may take over all its notes and expressions: the bound on one note times the notes would let a
 * large vault keep the tab busy for hours. */
export const MAX_RUN_STEPS = 5_000_000;
/** How large the values of one run may be, added up: the cells and sort keys it keeps for every row. */
export const MAX_RUN_SIZE = 10_000_000;
/** How deeply a value may nest, lists in lists: what compares, shows or measures a value walks it on the stack. */
export const MAX_VALUE_DEPTH = 128;

const tooLarge = (at: Expr) => new QueryError("too-large", `A value of the expression grows past ${MAX_SIZE.toLocaleString("en")} characters and items.`, at.position);

// A list or an object is measured once, from its items' measures, so a value built of the same parts many times costs
// what it holds once to measure.
const measures = new WeakMap<Value, readonly [number, number]>();

function measure(value: Value): readonly [size: number, depth: number] {
  switch (value.kind) {
    case "text": return [1 + value.text.length, 0];
    case "file": return [1 + value.note.path.length, 0];
    case "link": return [1 + value.path.length + (value.display?.length ?? 0), 0];
    case "list":
    case "object": {
      const known = measures.get(value);
      if (known !== undefined) return known;
      let [total, deepest] = [1, 0];
      const parts: Array<[string, Value]> = value.kind === "list" ? value.items.map((i) => ["", i]) : [...value.values];
      for (const [key, part] of parts) {
        const [size, depth] = measure(part);
        total += key.length + size;
        deepest = Math.max(deepest, depth);
      }
      const result = [total, deepest + 1] as const;
      measures.set(value, result);
      return result;
    }
    // A number, a date or a duration shows in a few characters.
    default: return [8, 0];
  }
}

// The steps of what works out a calendar or many digits (format, relative, toFixed, date(), duration()): measured, each
// takes as long as so many steps of the rest.
const CALENDAR = 8;

/** The steps reading, comparing or building a value takes: one for each 64 of its characters and items. */
function work(value: Value): number {
  return value.kind === "text" || value.kind === "list" || value.kind === "object" ? Math.floor(measure(value)[0] / 64) : 0;
}

function bounded(value: Value, at: Expr): void {
  const [size, depth] = measure(value);
  if (size > MAX_SIZE) throw tooLarge(at);
  if (depth > MAX_VALUE_DEPTH) throw new QueryError("too-deep", `A value of the expression nests more than ${MAX_VALUE_DEPTH} levels deep.`, at.position);
}

// Items added up as they come, so a list too large fails before it is all built.
function built(items: Iterable<Value>, at: Expr): ListOf {
  const list: Value[] = [];
  let size = 1;
  for (const item of items) {
    if ((size += measure(item)[0]) > MAX_SIZE) throw tooLarge(at);
    list.push(item);
  }
  return { kind: "list", items: list };
}

// The items a lambda's body is evaluated for, the innermost last: value, index and, in reduce, acc.
type Frame = { readonly value: Value; readonly index: number; readonly acc: Value | null };

export class Evaluator {
  private readonly formulaExprs = new Map<string, Expr>();
  private readonly regexes = new Map<Expr, RegexOf | null>();
  // One value for each pattern and its flags in a run, wherever it is written, and the ones searched in this run so far.
  private readonly regexesOfRun = new Map<string, RegexOf>();
  private readonly searchedInRun = new Set<RegexOf>();
  private readonly checkedLevels = new Map<string, number>();
  private deepest = 0;
  private readonly found: QueryWarning[] = [];
  private checking = "";
  private readonly values = new Map<string, Value>();
  private readonly evaluating = new Set<string>();
  private level = 0;
  private steps = 0;
  private runSteps = 0;
  private runSize = 0;
  private frames: Frame[] = [];
  private valuesFor: Scope | null = null;
  private alone: { scope: Scope; vault: QueryVault } | null = null;

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

  /** What the expressions compiled so far name that doesn't exist, and the regular expressions that are null, each once,
   * in the order found. */
  get warnings(): readonly QueryWarning[] {
    return this.found;
  }

  /** How many levels down compiling or evaluating an expression may go, its own and those of the formulas it uses added
   * up along the way: the same as the server's, whose thread stack of 1.5 MB must hold them. */
  static readonly MAX_LEVELS = 512;

  /** Evaluates a compiled expression for a row; throws `QueryError` `too-deep` when it and the formulas it uses go more
   * than `MAX_LEVELS` levels down, `too-large` when it builds a value larger than `MAX_SIZE` or takes more than `MAX_STEPS`. */
  evaluate(expr: Expr, scope: Scope): Value {
    this.level = 0;
    this.steps = 0;
    this.frames = [];
    this.evaluating.clear();
    try {
      const value = this.eval(expr, scope);
      if ((this.runSize += measure(value)[0]) > MAX_RUN_SIZE)
        throw new QueryError("too-large", `The values of the query grow past ${MAX_RUN_SIZE.toLocaleString("en")} characters and items over its notes.`, expr.position);
      return value;
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

  private step(count: number, at: Expr): void {
    if ((this.steps += count) > MAX_STEPS)
      throw new QueryError("too-large", `The expression takes more than ${MAX_STEPS.toLocaleString("en")} steps for one note.`, at.position);
    if ((this.runSteps += count) > MAX_RUN_STEPS)
      throw new QueryError("too-large", `The query takes more than ${MAX_RUN_STEPS.toLocaleString("en")} steps over its notes.`, at.position);
  }

  // One search of a regular expression from a place. It counts what it may read, 4 + its size steps for each 20
  // characters, so a step stays about 1 µs at every size (re2js, case-insensitive on non-ASCII text, takes up to
  // 1.6 µs a step at a /32 rate): up to
  // the match's end and on by the longest match, or, when it fails or a match has no longest, the rest of the text
  // (/(?:a*c)?/ on "aaa…" reads it for each match), so a global replace or split can't take quadratic time. The first
  // search of a pattern in a run also counts the states the server's engine builds for it (regexShape), as the server does.
  private readonly search: Search = (r, m, text, from, at) => {
    if (!this.searchedInRun.has(r)) {
      this.searchedInRun.add(r);
      this.step(128 * r.size * r.size, at);
    }
    const found = m.find(from);
    const read = found && r.longest !== null ? Math.min(m.end() - from + r.longest, text.length - from) : text.length - from;
    this.step(1 + Math.floor((read * (4 + r.size)) / 20), at);
    return found;
  };

  // Counted here, not only in the check: a check cut short by a cycle, or by a formula checked before, can't see how deep
  // the formulas really go along this path.
  private eval(expr: Expr, scope: Scope): Value {
    this.down(expr);
    this.step(1, expr);
    const value = this.valueOf(expr, scope);
    bounded(value, expr);
    // What a call or a literal builds is work in its size; a name or a field gives a value that is there already.
    if (expr.kind === "call" || expr.kind === "list" || expr.kind === "object") this.step(work(value), expr);
    this.level--;
    return value;
  }

  private valueOf(expr: Expr, scope: Scope): Value {
    switch (expr.kind) {
      case "literal": return expr.value;
      case "name": return this.name(expr.name, scope, expr);
      case "member":
        if (expr.target.kind === "name" && expr.target.name === "formula") return this.formulaValue(expr.name, scope);
        return this.field(this.eval(expr.target, scope), expr.name, scope, expr);
      case "index": return index(this.eval(expr.target, scope), this.eval(expr.index, scope));
      case "call":
        if (expr.callee.kind === "name") return this.call(expr.callee.name, expr.args, scope, expr);
        if (expr.callee.kind === "member") {
          const target = this.eval(expr.callee.target, scope);
          if (LAMBDAS.has(expr.callee.name)) return this.lambda(target, expr.callee.name, expr, scope);
          return this.method(target, expr.callee.name, expr.args.map((a) => this.eval(a, scope)), scope, expr);
        }
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
          // && and || give an operand, as JavaScript's do, not Obsidian's checkbox (ADR 0041).
          if (operator === "And") value = truthy(value) ? this.eval(right, scope) : value;
          else if (operator === "Or") value = truthy(value) ? value : this.eval(right, scope);
          else {
            // Comparing or joining two values reads both and builds the result.
            const [left, other] = [value, this.eval(right, scope)];
            value = binary(operator, left, other);
            this.step(work(left) + work(other) + work(value), spine[i]!);
          }
          // Each step of a chain is a value of its own: "a" + "a" + ... grows at each.
          bounded(value, spine[i]!);
        }
        return value;
      }
      case "list": {
        const items = built((function* (self: Evaluator) { for (const i of expr.items) yield self.eval(i, scope); })(this), expr);
        return firstError(items.items) ?? items;
      }
      case "object": {
        // A key written twice keeps its first place and its last value.
        const last = new Map<string, Expr>();
        for (const [key, value] of expr.entries) last.set(key, value);
        const values = new Map<string, Value>();
        let size = 1;
        for (const [key, value] of last) {
          const v = this.eval(value, scope);
          values.set(key, v);
          if ((size += key.length + measure(v)[0]) > MAX_SIZE) throw tooLarge(expr);
        }
        return firstError([...values.values()]) ?? objectValue(values);
      }
      case "regex": return this.regexes.get(expr) ?? NULL;
    }
  }

  // A formula's value for a row is worked out once, however often the row's expressions use it; one that is asked for
  // while it is being worked out uses itself, in a circle. A formula is an expression of its own: a lambda's names don't
  // reach into it, so its value is the same wherever it is used.
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
    const outer = this.frames;
    this.frames = [];
    value = this.eval(expr, scope);
    this.frames = outer;
    this.evaluating.delete(formulaName);
    this.values.set(formulaName, value);
    return value;
  }

  // filter, map and reduce evaluate their first argument for each item, with value, index and (in reduce) acc as names;
  // the body counts one level more than where it is written (bases spec 2026-11-25 decision 17).
  private lambda(target: Value, methodName: string, call: CallExpr, scope: Scope): Value {
    if (target.kind === "error" || target.kind === "null") return target;
    if (target.kind !== "list") return noMethod(target, methodName);
    const body = call.args[0];
    if (body === undefined) return NULL;
    let acc: Value | null = null;
    if (methodName === "reduce") {
      acc = call.args.length > 1 ? this.eval(call.args[1]!, scope) : NULL;
      if (acc.kind === "error") return acc;
    }
    const results: Value[] = [];
    let size = 1;
    for (let i = 0; i < target.items.length; i++) {
      this.frames.push({ value: target.items[i]!, index: i, acc });
      this.down(body);
      const value = this.eval(body, scope);
      this.level--;
      this.frames.pop();
      if (value.kind === "error") return value;
      if (methodName === "reduce") acc = value;
      else if (methodName === "filter") {
        if (truthy(value)) results.push(target.items[i]!);
      } else {
        if ((size += measure(value)[0]) > MAX_SIZE) throw tooLarge(call);
        results.push(value);
      }
    }
    return acc ?? listValue(results);
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
          for (const a of expr.args) this.check(a, chain);
        } else if (expr.callee.kind === "member") {
          const { name: methodName } = expr.callee;
          if (!METHODS.has(methodName)) this.warn("unknown-method", `There is no method '${methodName}'.`, expr.callee.position);
          this.check(expr.callee.target, chain);
          expr.args.forEach((a, i) => {
            // A lambda's body is one level deeper, as when it is evaluated.
            const body = i === 0 && LAMBDAS.has(methodName);
            if (body) this.down(a);
            this.check(a, chain);
            if (body) this.level--;
          });
        } else {
          this.warn("unknown-function", "Only a function or a method can be called.", expr.position);
          this.check(expr.callee, chain);
          for (const a of expr.args) this.check(a, chain);
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
        this.regexes.set(expr, this.compileRegex(expr.pattern, expr.flags, expr.position));
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

  // Matching in linear time: a pattern is anyone's input, and backtracking would let one hang the tab. An unknown or
  // repeated flag, a pattern that doesn't compile, and one that needs backtracking (lookaround, backreferences), which
  // RE2 has not, are null, as Obsidian gives null for its own failures (bases spec 2026-11-25 decision 15); query check
  // tells each.
  private compileRegex(pattern: string, flags: string, position: number): RegexOf | null {
    const flag = [...flags].find((f) => !"gimsu".includes(f) || flags.indexOf(f) !== flags.lastIndexOf(f));
    if (flag !== undefined) {
      this.warn("regex", `The regular expression has the unknown or repeated flag '${flag}' (flags are g, i, m, s, u), so it is null.`, position);
      return null;
    }
    const shape = regexShape(pattern);
    if (!runs(shape)) {
      this.warn("regex", shape.unsupported !== null
        ? `The regular expression has syntax at character ${shape.unsupported + 1} outside the part of RE2 that runs here, such as lookaround, a backreference, a named group, a comment, \\p{…} or a flag other than i, m and s, so it is null.`
        : shape.countTooLarge
        ? `The regular expression counts a repetition more than ${MAX_COUNT} times (alone or multiplied through nesting), which RE2 doesn't run, so it is null.`
        : `The regular expression is ${shape.size} positions written out, more than the ${MAX_PATTERN_SIZE} that run in time, so it is null.`, position);
      return null;
    }
    // As JavaScript's toString: the pattern, then the flags in their own order.
    const written = `/${pattern}/${[..."gimsu"].filter((f) => flags.includes(f)).join("")}`;
    const known = this.regexesOfRun.get(written);
    if (known !== undefined) return known;
    let options = 0;
    if (flags.includes("i")) options |= RE2JS.CASE_INSENSITIVE;
    if (flags.includes("m")) options |= RE2JS.MULTILINE;
    if (flags.includes("s")) options |= RE2JS.DOTALL;
    try {
      const value = regexValue(RE2JS.compile(pattern, options), flags.includes("g"), written, shape.longest, shape.size) as RegexOf;
      this.regexesOfRun.set(written, value);
      return value;
    } catch (e) {
      this.warn("regex", `The regular expression doesn't compile or needs backtracking, which runs here in linear time only (no lookaround, no backreferences), so it is null: ${e instanceof Error ? e.message : String(e)}`, position);
      return null;
    }
  }

  private name(propertyName: string, scope: Scope, at: Expr): Value {
    // value, index and acc are the lambda's where there is one; elsewhere they are properties.
    const frame = this.frames[this.frames.length - 1];
    if (frame !== undefined && propertyName === "value") return frame.value;
    if (frame !== undefined && propertyName === "index") return numberValue(frame.index);
    if (propertyName === "acc") {
      for (let i = this.frames.length - 1; i >= 0; i--) if (this.frames[i]!.acc !== null) return this.frames[i]!.acc!;
    }
    switch (propertyName) {
      case "file": return fileValue(scope.note);
      case "note": return this.kept(scope, scope.note, "properties", () => objectValue(scope.note.properties.values), at);
      case "this": return scope.this !== null ? noteRefValue(scope.this) : NULL;
      case "formula": return NULL;
      default: return property(scope.note, propertyName);
    }
  }

  // The files of the run; an expression evaluated on its own sees its note and the one it is shown in.
  private vault(scope: Scope): QueryVault {
    if (scope.vault !== undefined) return scope.vault;
    if (this.alone?.scope !== scope) this.alone = { scope, vault: new QueryVault(scope.this === null ? [scope.note] : [scope.note, scope.this]) };
    return this.alone.vault;
  }

  // A file's list or object field is built once a run, and counts its work then; after, it is the same value, which costs
  // nothing to give again, and whatever reads it (a lambda, a method, a comparison) counts its own work.
  private kept(scope: Scope, note: QueryNote, fieldName: string, build: () => Value, at: Expr): Value {
    const vault = this.vault(scope);
    const known = vault.keptOf(note, fieldName);
    if (known !== undefined) return known;
    const value = build();
    this.step(work(value), at);
    return vault.keep(note, fieldName, value);
  }

  private field(value: Value, fieldName: string, scope: Scope, at: Expr): Value {
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
          case "tags": return this.kept(scope, value.note, fieldName, () => listValue(value.note.tags.map(textValue)), at);
          case "properties": return this.kept(scope, value.note, fieldName, () => objectValue(value.note.properties.values), at);
          case "links":
          case "embeds": {
            const note = value.note;
            return this.kept(scope, note, fieldName, () => listValue(this.vault(scope).linksOf(note).filter((l) => l.embed === (fieldName === "embeds")).map((l) => linkValue(l.path, null))), at);
          }
          case "backlinks": {
            const note = value.note;
            return this.kept(scope, note, fieldName, () => listValue(this.vault(scope).backlinksOf(note).map(fileValue)), at);
          }
          case "file": return value;
          default: return noField(value, fieldName);
        }
      case "noteRef":
        if (fieldName === "file") return fileValue(value.note);
        if (fieldName === "note") {
          const note = value.note;
          return this.kept(scope, note, "properties", () => objectValue(note.properties.values), at);
        }
        return property(value.note, fieldName);
      case "object": return value.values.get(fieldName) ?? NULL;
      case "text": return fieldName === "length" ? numberValue(value.text.length) : noField(value, fieldName);
      case "list": return fieldName === "length" ? numberValue(value.items.length) : noField(value, fieldName);
      case "date": return part(value, fieldName);
      default: return noField(value, fieldName);
    }
  }

  private call(functionName: string, args: readonly Expr[], scope: Scope, at: Expr): Value {
    const argument = (i: number): Value => (i < args.length ? this.eval(args[i]!, scope) : NULL);
    if (!FUNCTIONS.has(functionName)) return errorValue(`Cannot find function "${functionName}"`);
    switch (functionName) {
      case "now": return dateValue(scope.now);
      case "today": return dateValue(scope.now.slice(0, 10));
      case "random": return numberValue(this.vault(scope).random(scope.note));
      case "max":
      case "min": {
        const all = args.map((a) => this.eval(a, scope));
        const error = firstError(all);
        if (error !== undefined) return error;
        const other = all.find((v) => v.kind !== "number" && v.kind !== "null");
        if (other !== undefined) return errorValue(`${functionName}() takes numbers, not ${typeName(other)}`);
        const numbers = all.flatMap((v) => (v.kind === "number" ? [v.number] : []));
        // As JavaScript's Math.max and Math.min: NaN wins.
        if (numbers.length === 0) return NULL;
        return numberValue(functionName === "max" ? Math.max(...numbers) : Math.min(...numbers));
      }
    }
    const first = argument(0);
    if (first.kind === "error") return first;
    // number(), escapeHTML(), file() and link() read their first argument.
    this.step(work(first) + (functionName === "date" || functionName === "duration" ? CALENDAR : 0), at);
    switch (functionName) {
      // Only the branch taken is evaluated, as in JavaScript.
      case "if": return truthy(first) ? argument(1) : argument(2);
      case "date": return dateOf(first);
      case "number":
        if (first.kind === "number") return first;
        if (first.kind === "boolean") return numberValue(first.boolean ? 1 : 0);
        // A date has no zone, so its milliseconds are counted as if it were UTC.
        if (first.kind === "date") return numberValue(millisecondsBetween(first.moment, "1970-01-01T00:00:00"));
        return first.kind === "text" ? numberOf(first.text) : NULL;
      case "duration": return first.kind === "text" ? durationOf(first.text) : first.kind === "duration" ? first : NULL;
      case "list": return first.kind === "list" || first.kind === "null" ? first : listValue([first]);
      case "file": return this.fileOf(first, scope);
      case "link": {
        const shown = argument(1);
        if (shown.kind === "error") return shown;
        return this.linkOf(first, shown.kind === "null" ? null : display(shown), scope);
      }
      default: // escapeHTML
        return first.kind === "null" ? first : textValue(escapeHtml(display(first)));
    }
  }

  // A path, a link or a file as the file it leads to: a text is read as a link from the row's note is ([[Ann]], Ann, People/Ann.md).
  private fileOf(v: Value, scope: Scope): Value {
    const vault = this.vault(scope);
    switch (v.kind) {
      case "file": return v;
      case "noteRef": return fileValue(v.note);
      case "link": {
        const file = vault.file(v.path);
        return file === null ? NULL : fileValue(file);
      }
      case "text": {
        const path = vault.resolve(scope.note, wikilink(v.text).target);
        return path === null ? NULL : fileValue(vault.file(path)!);
      }
      default: return NULL;
    }
  }

  private linkOf(v: Value, shown: string | null, scope: Scope): Value {
    switch (v.kind) {
      case "file":
      case "noteRef": return linkValue(v.note.path, shown);
      case "link": return linkValue(v.path, shown ?? v.display);
      case "text": {
        const { target, display: inner } = wikilink(v.text);
        return linkValue(this.vault(scope).resolve(scope.note, target) ?? target, shown ?? inner);
      }
      default: return NULL;
    }
  }

  private method(target: Value, methodName: string, args: readonly Value[], scope: Scope, at: Expr): Value {
    const argument = (i: number): Value => args[i] ?? NULL;
    const textArgument = (i: number): string | null => {
      const a = argument(i);
      return a.kind === "text" ? a.text : null;
    };
    const numberArgument = (i: number): number | null => {
      const a = argument(i);
      return a.kind === "number" ? a.number : null;
    };
    const texts = (): string[] | null => (args.every((a) => a.kind === "text") ? args.map((a) => (a as { text: string }).text) : null);
    const bool = (b: boolean | null): Value => (b === null ? NULL : booleanValue(b));
    if (target.kind === "error") return target;
    const error = firstError(args);
    if (error !== undefined) return error;
    // What a method goes through counts as steps: each item, and the target's size, for each argument it looks for and
    // for each round of a sort; and what the arguments hold. What it builds counts in eval.
    const items = target.kind === "list" ? target.items.length : target.kind === "object" ? target.values.size : 0;
    const times = methodName === "contains" || methodName === "containsAll" || methodName === "containsAny" ? Math.max(1, args.length)
      // A comparison costs less than a step: a quarter.
      : methodName === "sort" ? 1 + Math.floor(Math.floor(Math.log2(items + 1)) / 4)
      : 1;
    const calendar = methodName === "format" || methodName === "relative" || methodName === "toFixed" ? CALENDAR : 0;
    this.step((items + work(target)) * times + args.reduce((sum, a) => sum + work(a), 0) + calendar, at);

    switch (methodName) {
      case "isEmpty":
        switch (target.kind) {
          case "null": return booleanValue(true);
          case "text": return booleanValue(target.text.length === 0);
          case "list": return booleanValue(target.items.length === 0);
          case "object": return booleanValue(target.values.size === 0);
          default: return booleanValue(false);
        }
      case "isTruthy": return booleanValue(truthy(target));
      case "isType": {
        const type = textArgument(0);
        return bool(type === null ? null : typeOf(target) === type);
      }
    }
    if (target.kind === "null") return NULL;
    if (methodName === "toString") return textValue(display(target));
    const missing = () => noMethod(target, methodName);

    switch (target.kind) {
      case "text": {
        const text = target.text;
        switch (methodName) {
          case "contains": return bool(textArgument(0) === null ? null : text.includes(textArgument(0)!));
          case "containsAll": return bool(texts()?.every((s) => text.includes(s)) ?? null);
          case "containsAny": return bool(texts()?.some((s) => text.includes(s)) ?? null);
          case "startsWith": return bool(textArgument(0) === null ? null : text.startsWith(textArgument(0)!));
          case "endsWith": return bool(textArgument(0) === null ? null : text.endsWith(textArgument(0)!));
          case "lower": return textValue(text.toLowerCase());
          case "title": return textValue(title(text));
          case "trim": return textValue(trim(text));
          case "replace": return replace(text, argument(0), argument(1), at, this.search);
          case "repeat": {
            const count = numberArgument(0);
            return count === null ? NULL : repeat(text, count, at);
          }
          case "reverse": return textValue(codePoints(text).reverse().join(""));
          case "slice": {
            const range = slice(text.length, argument(0), argument(1));
            return range === null ? NULL : textValue(text.slice(range[0], range[1]));
          }
          case "split": {
            const limit = argument(1);
            if (limit.kind !== "number" && limit.kind !== "null") return NULL;
            return split(text, argument(0), limit, at, this.search) ?? NULL;
          }
          default: return missing();
        }
      }
      case "list": {
        const { items } = target;
        switch (methodName) {
          case "contains": return booleanValue(items.some((i) => equal(i, argument(0))));
          case "containsAll": return booleanValue(args.every((a) => items.some((i) => equal(i, a))));
          case "containsAny": return booleanValue(args.some((a) => items.some((i) => equal(i, a))));
          case "flat": return built(items.flatMap((i) => (i.kind === "list" ? i.items : [i])), at);
          case "join": return join(target, textArgument(0) ?? ",", at);
          case "reverse": return listValue([...items].reverse());
          case "slice": {
            const range = slice(items.length, argument(0), argument(1));
            return range === null ? NULL : listValue(items.slice(range[0], range[1]));
          }
          case "sort": return listValue([...items].sort(sorted));
          case "unique": {
            const seen = new Set<string>();
            return listValue(items.filter((i) => {
              const k = key(i);
              return !seen.has(k) && seen.add(k) !== undefined;
            }));
          }
          default: return missing();
        }
      }
      case "number":
        switch (methodName) {
          case "abs": return numberValue(Math.abs(target.number));
          case "floor": return numberValue(Math.floor(target.number));
          case "ceil": return numberValue(Math.ceil(target.number));
          case "round": {
            const digits = integer(numberArgument(0) ?? 0);
            return numberValue(digits !== 0 ? round(target.number * 10 ** digits) / 10 ** digits : round(target.number));
          }
          case "toFixed": return toFixed(target.number, numberArgument(0) ?? 0);
          default: return missing();
        }
      case "date":
        switch (methodName) {
          case "date": return dateValue(target.moment.slice(0, 10));
          case "format": {
            if (textArgument(0) === null) return NULL;
            const written = format(target.moment, textArgument(0)!, MAX_SIZE);
            if (written === null) throw tooLarge(at);
            return textValue(written);
          }
          case "time": return textValue(target.moment.slice(11, 19));
          case "relative": return textValue(fromNow(target.moment, scope.now));
          default: return missing();
        }
      case "object":
        switch (methodName) {
          case "keys": return listValue([...target.values.keys()].map(textValue));
          case "values": return listValue([...target.values.values()]);
          default: return missing();
        }
      case "link": {
        const file = this.vault(scope).file(target.path);
        switch (methodName) {
          case "asFile": return file === null ? NULL : fileValue(file);
          case "linksTo": return file === null ? booleanValue(false) : bool(this.linksTo(file, argument(0), scope));
          default: return missing();
        }
      }
      case "file":
        switch (methodName) {
          case "asLink": return linkValue(target.note.path, argument(0).kind === "null" ? null : display(argument(0)));
          case "hasLink": return bool(this.linksTo(target.note, argument(0), scope));
          case "hasTag": return booleanValue(args.some((a) => a.kind === "text" && hasTag(target.note, a.text)));
          case "inFolder": return bool(textArgument(0) === null ? null : inFolder(target.note.path, textArgument(0)!));
          case "hasProperty": return bool(textArgument(0) === null ? null : target.note.properties.values.has(textArgument(0)!));
          default: return missing();
        }
      case "regex":
        if (methodName !== "matches") return missing();
        if (textArgument(0) === null) return NULL;
        return bool(this.search(target, (target.regex as RE2JS).matcher(textArgument(0)!), textArgument(0)!, 0, at));
      default: return missing();
    }
  }

  // Whether the note links to a file, a link's file or a path read as a link from it; null for anything else.
  private linksTo(note: QueryNote, other: Value, scope: Scope): boolean | null {
    const vault = this.vault(scope);
    let path: string | null;
    switch (other.kind) {
      case "file":
      case "noteRef": path = other.note.path; break;
      case "link": path = other.path; break;
      case "text": {
        const { target } = wikilink(other.text);
        path = vault.resolve(note, target) ?? target;
        break;
      }
      default: path = null;
    }
    return path === null ? null : vault.linksOf(note).some((l) => l.path === path);
  }
}

// A text as a wikilink: [[target#heading|display]], the brackets optional; the heading names no other file.
function wikilink(text: string): { target: string; display: string | null } {
  let inner = trim(text);
  if (inner.startsWith("[[") && inner.endsWith("]]")) inner = inner.slice(2, -2);
  let shown: string | null = null;
  const bar = inner.indexOf("|");
  if (bar >= 0) [inner, shown] = [inner.slice(0, bar), inner.slice(bar + 1)];
  const hash = inner.indexOf("#");
  if (hash >= 0) inner = inner.slice(0, hash);
  return { target: trim(inner), display: shown };
}

const escapeHtml = (text: string): string =>
  text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;").replaceAll("'", "&#39;");

// A text as JavaScript's Number() reads it: decimal with an exponent, Infinity, 0x, 0o and 0b; the empty text is 0. Trimmed
// as the server trims, so both engines read the same texts: what Number() would trim beyond that (U+FEFF) is no number.
function numberOf(text: string): Value {
  const trimmed = trim(text);
  const n = /^\s|\s$/.test(trimmed) ? NaN : Number(trimmed);
  return Number.isNaN(n) ? errorValue(`Unable to parse "${text}" as a number.`) : numberValue(n);
}

const property = (note: QueryNote, propertyName: string): Value => note.properties.values.get(propertyName) ?? NULL;

const fileName = (path: string) => path.slice(path.lastIndexOf("/") + 1);
const folderOf = (path: string) => (path.lastIndexOf("/") >= 0 ? path.slice(0, path.lastIndexOf("/")) : "/");

// .NET's Path.GetFileNameWithoutExtension and GetExtension: the last dot starts the extension, unless it ends the name.
const baseName = (file: string) => (file.lastIndexOf(".") >= 0 ? file.slice(0, file.lastIndexOf(".")) : file);
const extension = (file: string) => (file.lastIndexOf(".") >= 0 && file.lastIndexOf(".") < file.length - 1 ? file.slice(file.lastIndexOf(".") + 1) : "");

const noField = (value: Value, fieldName: string): Value => errorValue(`Cannot find "${fieldName}" on type ${typeName(value)}`);
const noMethod = (value: Value, methodName: string): Value => errorValue(`Cannot find function "${methodName}" on type ${typeName(value)}`);

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
    case "link": return "Link";
    case "regex": return "RegExp";
    case "duration": return "Duration";
    case "error": return "Error";
  }
}

// The names isType takes, which the reference doesn't list: the types' own, in lower case.
function typeOf(value: Value): string {
  switch (value.kind) {
    case "text": return "string";
    case "noteRef": return "file";
    case "regex": return "regexp";
    default: return value.kind;
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
    case "millisecond": return numberValue(Number(moment.slice(20, 23).padEnd(3, "0")));
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

// JavaScript's Math.round, written as the server's engine rounds: halves go up, so -2.5 rounds to -2.
const round = (n: number) => Math.floor(n + 0.5);

// JavaScript's ToIntegerOrInfinity, which the indices and counts of its methods go through.
const integer = (n: number) => (Number.isNaN(n) ? 0 : Math.trunc(n));

// The range JavaScript's slice takes: negative indices count from the end, and an end before the start is empty.
function slice(length: number, start: Value, end: Value): [number, number] | null {
  const at = (v: Value, otherwise: number): number => {
    if (v.kind !== "number") return otherwise;
    const i = integer(v.number);
    return i < 0 ? Math.max(length + i, 0) : Math.min(i, length);
  };
  if ((start.kind !== "number" && start.kind !== "null") || (end.kind !== "number" && end.kind !== "null")) return null;
  const [from, to] = [at(start, 0), at(end, length)];
  return [from, Math.max(from, to)];
}

function repeat(text: string, count: number, at: Expr): Value {
  const n = integer(count);
  if (n < 0 || !Number.isFinite(n)) return errorValue(`Invalid count value: ${formatNumber(count)}`);
  // The empty text repeated is empty, however often: nothing to build.
  if (text.length === 0 || n === 0) return textValue("");
  if (text.length * n > MAX_SIZE) throw tooLarge(at);
  return textValue(text.repeat(n));
}

// Each word's first letter in upper case, the rest as it is; a word starts after white space. One unit for one, as .NET
// changes case, so a letter whose upper case is two (ß) stays.
function title(text: string): string {
  let result = "";
  for (let i = 0; i < text.length; i++) {
    const c = text[i]!;
    const upper = c.toUpperCase();
    result += (i === 0 || isWhiteSpace(text[i - 1]!)) && upper.length === 1 ? upper : c;
  }
  return result;
}

// The code points of a text, a lone surrogate as one of its own.
function codePoints(text: string): string[] {
  const points: string[] = [];
  for (let i = 0; i < text.length; i++) {
    const pair = /[\uD800-\uDBFF]/.test(text[i]!) && i + 1 < text.length && /[\uDC00-\uDFFF]/.test(text[i + 1]!);
    points.push(text.slice(i, pair ? i + 2 : i + 1));
    if (pair) i++;
  }
  return points;
}

function join(list: ListOf, separator: string, at: Expr): Value {
  const parts: string[] = [];
  let length = 0;
  list.items.forEach((item, i) => {
    const shown = display(item);
    length += (i > 0 ? separator.length : 0) + shown.length;
    if (length > MAX_SIZE) throw tooLarge(at);
    parts.push(shown);
  });
  return textValue(parts.join(separator));
}

// A match as JavaScript's replace and split see it: where it starts, its text, and its groups (null where a group took no part).
type Found = { index: number; text: string; groups: Array<string | null>; named: Map<string, string | null> | null };

type Search = (r: RegexOf, m: ReturnType<RE2JS["matcher"]>, text: string, from: number, at: Expr) => boolean;

function* matches(text: string, pattern: Value, all: boolean, at: Expr, search: Search): Generator<Found> {
  if (pattern.kind === "text") {
    // A text matches where it stands, the empty one before every character and at the end.
    const step = Math.max(pattern.text.length, 1);
    for (let at = text.indexOf(pattern.text); at >= 0;) {
      yield { index: at, text: pattern.text, groups: [], named: null };
      if (!all) return;
      at = at + step > text.length ? -1 : text.indexOf(pattern.text, at + step);
    }
    return;
  }
  const regex = (pattern as RegexOf).regex as RE2JS;
  const names = Object.entries(regex.namedGroups());
  const m = regex.matcher(text);
  for (let start = 0; start <= text.length;) {
    if (!search(pattern as RegexOf, m, text, start, at)) return;
    const groups = Array.from({ length: m.groupCount() }, (_, i) => m.group(i + 1));
    yield { index: m.start(), text: m.group()!, groups, named: names.length === 0 ? null : new Map(names.map(([n, i]) => [n, m.group(i)])) };
    if (!all) return;
    // An empty match moves on by one, as JavaScript's lastIndex does.
    start = m.start() + Math.max(m.end() - m.start(), 1);
  }
}

function replace(text: string, pattern: Value, replacement: Value, at: Expr, search: Search): Value {
  if ((pattern.kind !== "text" && pattern.kind !== "regex") || replacement.kind !== "text") return NULL;
  let result = "";
  let done = 0;
  for (const found of matches(text, pattern, pattern.kind === "text" || pattern.global, at, search)) {
    result += text.slice(done, found.index) + substitute(replacement.text, text, found);
    if (result.length > MAX_SIZE) throw tooLarge(at);
    done = found.index + found.text.length;
  }
  return textValue(result + text.slice(done));
}

const isDigit = (c: string | undefined) => c !== undefined && c >= "0" && c <= "9";

// JavaScript's GetSubstitution: $$, $&, $`, $', $1 to $99 and $<name>; anything else stays as written.
function substitute(template: string, text: string, found: Found): string {
  let into = "";
  for (let i = 0; i < template.length; i++) {
    const next = template[i + 1];
    if (template[i] !== "$" || next === undefined) {
      into += template[i];
      continue;
    }
    if (next === "$") into += "$";
    else if (next === "&") into += found.text;
    else if (next === "`") into += text.slice(0, found.index);
    else if (next === "'") into += text.slice(Math.min(found.index + found.text.length, text.length));
    else if (isDigit(next)) {
      let two = isDigit(template[i + 2]);
      let index = two ? Number(next) * 10 + Number(template[i + 2]) : Number(next);
      if (two && index > found.groups.length) [two, index] = [false, Number(next)];
      into += index >= 1 && index <= found.groups.length ? found.groups[index - 1] ?? "" : template.slice(i, i + (two ? 3 : 2));
      i += two ? 2 : 1;
      continue;
    } else if (next === "<" && found.named !== null && template.indexOf(">", i + 2) > 0) {
      const close = template.indexOf(">", i + 2);
      into += found.named.get(template.slice(i + 2, close)) ?? "";
      i = close;
      continue;
    } else {
      into += "$";
      continue;
    }
    i++;
  }
  return into;
}

// JavaScript's split: by a text (the empty one splits every character) or a regular expression, whose groups join the
// parts; at most limit parts, which counts as JavaScript's ToUint32 does.
function split(text: string, separator: Value, limit: Value, at: Expr, search: Search): Value | null {
  const most = limit.kind === "number" ? (Number.isFinite(limit.number) ? ((Math.trunc(limit.number) % 4294967296) + 4294967296) % 4294967296 : 0) : 4294967295;
  const parts: Value[] = [];
  let size = 1;
  // Measured as they come, as built does: a split into many parts, or groups, fails before it fills the memory.
  const add = (part: Value): boolean => {
    if (parts.length >= most) return false;
    if ((size += measure(part)[0]) > MAX_SIZE) throw tooLarge(at);
    parts.push(part);
    return true;
  };
  if (most === 0) return listValue(parts);
  switch (separator.kind) {
    case "null":
      add(textValue(text));
      return listValue(parts);
    case "text": {
      if (separator.text === "") {
        for (const c of text.split("")) if (!add(textValue(c))) break;
        return listValue(parts);
      }
      let done = 0;
      for (const found of matches(text, separator, true, at, search)) {
        if (!add(textValue(text.slice(done, found.index)))) return listValue(parts);
        done = found.index + found.text.length;
      }
      add(textValue(text.slice(done)));
      return listValue(parts);
    }
    case "regex": {
      const regex = separator.regex as RE2JS;
      const m = regex.matcher(text);
      if (text.length === 0) return listValue(search(separator, m, text, 0, at) ? [] : [textValue(text)]);
      let [p, q] = [0, 0];
      while (q < text.length) {
        if (!search(separator, m, text, q, at)) break;
        const [start, e] = [m.start(), m.end()];
        // An empty match where the part starts, or one at the very end, splits nothing.
        if (e === p || start >= text.length) {
          q = start + 1;
          continue;
        }
        if (!add(textValue(text.slice(p, start)))) return listValue(parts);
        for (let g = 1; g <= m.groupCount(); g++) {
          const group = m.group(g);
          if (!add(group === null ? NULL : textValue(group))) return listValue(parts);
        }
        [p, q] = [e, e === start ? e + 1 : e];
      }
      add(textValue(text.slice(p)));
      return listValue(parts);
    }
    default: return null;
  }
}

// JavaScript's toFixed, so many digits after the point; from 1e21 on the number as it is written otherwise.
function toFixed(x: number, digits: number): Value {
  const f = integer(digits);
  if (f < 0 || f > 100) return errorValue("toFixed() digits argument must be between 0 and 100");
  if (Number.isNaN(x) || Math.abs(x) >= 1e21) return textValue(formatNumber(x));
  return textValue(x.toFixed(f));
}

// list.sort(): within a type as compare orders them, types by their rank, empty values and then errors last.
function sorted(a: Value, b: Value): number {
  const last = (v: Value) => (v.kind === "null" ? 1 : v.kind === "error" ? 2 : 0);
  if (last(a) !== last(b)) return last(a) - last(b);
  return order(a, b);
}

/** The order of two values in a sort: within a type as `compare` has them, NaN after the other numbers (so the order holds
 * from one pair to the next), and types by their `rank`. */
export function order(a: Value, b: Value): number {
  const plain = (v: Value) => (v.kind === "number" ? v.number : v.kind === "duration" ? asMilliseconds(v) : null);
  const known = compare(a, b);
  if (known !== null) return known;
  const [x, y] = [plain(a), plain(b)];
  if (rank(a) === rank(b) && x !== null && y !== null) return Number(Number.isNaN(x)) - Number(Number.isNaN(y));
  return rank(a) - rank(b);
}

/** Where a value of a type goes among those of other types, in a sort. */
export function rank(v: Value): number {
  switch (v.kind) {
    case "number": return 0;
    case "duration": return 1;
    case "date": return 2;
    case "text": return 3;
    case "boolean": return 4;
    case "link": return 5;
    default: return 6;
  }
}

// What makes two items of list.unique() the same: their type and value, without the conversions == makes, so 1 and "1"
// stay two, as a JavaScript Set keeps them. A text has its length before it, so no text can end where another part begins.
function key(v: Value): string {
  const prefixed = (kind: string, text: string) => `${kind}${text.length}:${text}`;
  switch (v.kind) {
    case "text": return prefixed("t", v.text);
    case "number": return `n${v.number === 0 ? "0" : formatNumber(v.number)}`;
    case "list": return `[${v.items.map(key).join(",")}]`;
    case "object": return `{${[...v.values].map(([k, value]) => prefixed("", k) + key(value)).join(",")}}`;
    case "file":
    case "noteRef": return prefixed("f", v.note.path);
    case "link": return prefixed("l", v.path);
    default: return `${typeOf(v)}:${display(v)}`;
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
  const trimmed = trimSlashes(folder, true);
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
    // A link equals another, or the file, that it leads to: author == this holds where author links to the query note.
    case "file": return (b.kind === "file" && a.note.path === b.note.path) || (b.kind === "link" && b.path === a.note.path);
    case "noteRef": return (b.kind === "noteRef" && a.note.path === b.note.path) || (b.kind === "link" && b.path === a.note.path);
    case "link": return (b.kind === "link" && a.path === b.path) || ((b.kind === "file" || b.kind === "noteRef") && a.path === b.note.path);
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
  if (a.kind === "link" && b.kind === "link") return a.path < b.path ? -1 : a.path > b.path ? 1 : 0;
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
    case "link": return v.display ?? v.path;
    case "regex": return v.written;
    case "duration": return formatNumber(asMilliseconds(v));
    case "error": return `error: ${v.message}`;
  }
}
