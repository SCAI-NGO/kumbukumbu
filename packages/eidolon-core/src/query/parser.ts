// SPDX-License-Identifier: AGPL-3.0-or-later
// Parses an expression of the Bases language (Kumbu.Core Queries/ExpressionParser). Operators bind as in JavaScript, which
// Obsidian evaluates them with. An expression is anyone's input, an agent's parameters included, so its length and nesting
// are bounded: a deep nesting is a syntax error, never a stack overflow.
import type { Expr, Operator } from "./expr";
import { QuerySyntaxError } from "./errors";
import { NULL, booleanValue, numberValue, textValue } from "./value";

export const MAX_LENGTH = 10_000;
export const MAX_NESTING = 64;

const REGEX_FLAGS = "gimsu";

// .NET's char.IsLetter, IsLetterOrDigit (decimal digits only) and IsWhiteSpace, on one UTF-16 unit.
const LETTER = /^\p{L}$/u;
const LETTER_OR_DIGIT = /^[\p{L}\p{Nd}]$/u;
const WHITE_SPACE = /^[\p{Zs}\p{Zl}\p{Zp}\t-\r\u0085]$/u;

export const isWhiteSpace = (c: string): boolean => WHITE_SPACE.test(c);

const isAsciiDigit = (c: string) => c >= "0" && c <= "9";
const isAsciiLetter = (c: string) => (c >= "a" && c <= "z") || (c >= "A" && c <= "Z");
const isNameStart = (c: string) => LETTER.test(c) || c === "_";
const isNamePart = (c: string) => LETTER_OR_DIGIT.test(c) || c === "_";

type Level = readonly (readonly [string, Operator])[];

class Parser {
  private at = 0;
  private depth = 0;

  constructor(private readonly text: string) {}

  parse(): Expr {
    const expr = this.expression();
    this.skipSpace();
    if (this.at < this.text.length) throw this.unexpected("the end of the expression");
    return expr;
  }

  private expression(): Expr {
    this.enter();
    const expr = this.or();
    this.depth--;
    return expr;
  }

  private enter(): void {
    if (++this.depth > MAX_NESTING) throw new QuerySyntaxError(`The expression is nested too deeply: at most ${MAX_NESTING} levels.`, this.at);
  }

  private or = () => this.leftToRight(() => this.and(), [["||", "Or"]]);
  private and = () => this.leftToRight(() => this.equality(), [["&&", "And"]]);
  private equality = () => this.leftToRight(() => this.comparison(), [["==", "Equal"], ["!=", "NotEqual"]]);
  private comparison = () =>
    this.leftToRight(() => this.additive(), [["<=", "LessOrEqual"], [">=", "GreaterOrEqual"], ["<", "Less"], [">", "Greater"]]);
  private additive = () => this.leftToRight(() => this.multiplicative(), [["+", "Add"], ["-", "Subtract"]]);
  private multiplicative = () =>
    this.leftToRight(() => this.unary(), [["*", "Multiply"], ["/", "Divide"], ["%", "Remainder"]]);

  // A chain of one level's operators is built in a loop, so "a && a && …" doesn't recurse.
  private leftToRight(operand: () => Expr, operators: Level): Expr {
    let left = operand();
    for (;;) {
      this.skipSpace();
      const start = this.at;
      const found = operators.find(([symbol]) => this.isOperator(symbol));
      if (found === undefined) return left;
      this.at += found[0].length;
      left = { kind: "binary", operator: found[1], left, right: operand(), position: start };
    }
  }

  // "<" must not take the first character of "<=", "!" not that of "!=", "=" stands alone only as a mistake.
  private isOperator(symbol: string): boolean {
    return this.text.startsWith(symbol, this.at) && !((symbol === "<" || symbol === ">") && this.peek(1) === "=");
  }

  private unary(): Expr {
    this.skipSpace();
    const start = this.at;
    if (this.peek() === "!" && this.peek(1) !== "=") return this.prefixed("Not", start);
    if (this.peek() === "-") return this.prefixed("Negate", start);
    return this.postfix(this.primary());
  }

  private prefixed(operator: Operator, start: number): Expr {
    this.at++;
    this.enter();
    const operand = this.unary();
    this.depth--;
    return { kind: "unary", operator, operand, position: start };
  }

  // Each step of a chain (.name, [index], a call) is a level, held until the chain ends: its tree nests one deeper per step.
  private postfix(first: Expr): Expr {
    let expr = first;
    let steps = 0;
    for (;;) {
      this.skipSpace();
      const start = this.at;
      switch (this.peek()) {
        case ".":
          this.at++;
          this.skipSpace();
          if (!isNameStart(this.peek())) throw this.unexpected("a name after '.'");
          expr = { kind: "member", target: expr, name: this.name(), position: start };
          break;
        case "[": {
          this.at++;
          const index = this.expression();
          this.expect("]", "']'");
          expr = { kind: "index", target: expr, index, position: start };
          break;
        }
        case "(":
          this.at++;
          expr = { kind: "call", callee: expr, args: this.items(")"), position: start };
          break;
        default:
          this.depth -= steps;
          return expr;
      }
      this.enter();
      steps++;
    }
  }

  private primary(): Expr {
    this.skipSpace();
    const start = this.at;
    const c = this.peek();
    switch (c) {
      case "(": {
        this.at++;
        const inner = this.expression();
        this.expect(")", "')'");
        return inner;
      }
      case "[":
        this.at++;
        return { kind: "list", items: this.items("]"), position: start };
      case "{":
        this.at++;
        return this.object(start);
      case '"':
      case "'":
        return { kind: "literal", value: textValue(this.string()), position: start };
      case "/":
        return this.regex();
    }

    if (isAsciiDigit(c)) return { kind: "literal", value: numberValue(this.number()), position: start };
    if (isNameStart(c)) {
      const name = this.name();
      switch (name) {
        case "true": return { kind: "literal", value: booleanValue(true), position: start };
        case "false": return { kind: "literal", value: booleanValue(false), position: start };
        case "null": return { kind: "literal", value: NULL, position: start };
        default: return { kind: "name", name, position: start };
      }
    }

    throw this.unexpected("an expression");
  }

  /** The comma-separated expressions up to `close`, which the caller has opened. */
  private items(close: string): Expr[] {
    this.enter();
    const items: Expr[] = [];
    this.skipSpace();
    if (this.peek() === close) {
      this.at++;
      this.depth--;
      return items;
    }

    for (;;) {
      items.push(this.expression());
      this.skipSpace();
      if (this.peek() === close) break;
      if (this.peek() !== ",") throw this.unexpected(`',' or '${close}'`);
      this.at++;
    }

    this.at++;
    this.depth--;
    return items;
  }

  private object(start: number): Expr {
    this.enter();
    const entries: Array<readonly [string, Expr]> = [];
    this.skipSpace();
    while (this.peek() !== "}") {
      this.skipSpace();
      const c = this.peek();
      if (c !== '"' && c !== "'" && !isNameStart(c)) throw this.unexpected("a key");
      const key = c === '"' || c === "'" ? this.string() : this.name();
      this.expect(":", "':'");
      entries.push([key, this.expression()]);
      this.skipSpace();
      if (this.peek() === "}") break;
      if (this.peek() !== ",") throw this.unexpected("',' or '}'");
      this.at++;
      this.skipSpace();
    }

    this.at++;
    this.depth--;
    return { kind: "object", entries, position: start };
  }

  private string(): string {
    const start = this.at;
    const quote = this.text[this.at++]!;
    let value = "";
    while (this.at < this.text.length && this.text[this.at] !== quote) {
      if (this.text[this.at] !== "\\") {
        value += this.text[this.at++];
        continue;
      }

      const escape = this.at;
      this.at++;
      const unescaped = ESCAPES[this.peek()];
      if (unescaped === undefined) throw new QuerySyntaxError("Unknown escape in the string: use \\\", \\', \\\\, \\n, \\t or \\r.", escape);
      value += unescaped;
      this.at++;
    }

    if (this.at >= this.text.length) throw new QuerySyntaxError("The string is unterminated.", start);
    this.at++;
    return value;
  }

  private regex(): Expr {
    const start = this.at++;
    let pattern = "";
    while (this.at < this.text.length && this.text[this.at] !== "/" && this.text[this.at] !== "\n") {
      // A backslash keeps the next character, an escaped slash included, for the regular expression to read.
      if (this.text[this.at] === "\\" && this.at + 1 < this.text.length) pattern += this.text[this.at++];
      pattern += this.text[this.at++];
    }

    if (this.at >= this.text.length || this.text[this.at] !== "/") throw new QuerySyntaxError("The regular expression is unterminated.", start);
    this.at++;
    const flags = this.at;
    while (isAsciiLetter(this.peek())) {
      if (!REGEX_FLAGS.includes(this.peek()) || this.text.slice(flags, this.at).includes(this.peek()))
        throw new QuerySyntaxError(
          `Unknown or repeated flag '${this.peek()}' of the regular expression: use ${[...REGEX_FLAGS].join(", ")}.`, this.at);
      this.at++;
    }

    return { kind: "regex", pattern, flags: this.text.slice(flags, this.at), position: start };
  }

  private number(): number {
    const start = this.at;
    while (isAsciiDigit(this.peek())) this.at++;
    if (this.peek() === "." && isAsciiDigit(this.peek(1))) {
      this.at++;
      while (isAsciiDigit(this.peek())) this.at++;
    }

    return Number(this.text.slice(start, this.at));
  }

  private name(): string {
    const start = this.at;
    while (isNamePart(this.peek())) this.at++;
    return this.text.slice(start, this.at);
  }

  private expect(c: string, what: string): void {
    this.skipSpace();
    if (this.peek() !== c) throw this.unexpected(what);
    this.at++;
  }

  private unexpected(expected: string): QuerySyntaxError {
    const found = this.at < this.text.length ? `'${this.text[this.at]}'` : "the end";
    const next = this.peek(1);
    const hint =
      this.peek() === "=" && next !== "=" ? " ('=' compares nothing: use '==')"
      : this.peek() === "&" && next !== "&" ? " (use '&&')"
      : this.peek() === "|" && next !== "|" ? " (use '||')"
      : "";
    return new QuerySyntaxError(`Expected ${expected}, found ${found}${hint}.`, this.at);
  }

  private peek(ahead = 0): string {
    return this.at + ahead < this.text.length ? this.text[this.at + ahead]! : "\0";
  }

  private skipSpace(): void {
    while (this.at < this.text.length && isWhiteSpace(this.text[this.at]!)) this.at++;
  }
}

const ESCAPES: Record<string, string | undefined> = { '"': '"', "'": "'", "\\": "\\", n: "\n", t: "\t", r: "\r" };

/** Parses an expression; throws a `QuerySyntaxError` where it is wrong. */
function parse(text: string): Expr {
  if (text.length > MAX_LENGTH) throw new QuerySyntaxError(`The expression is too long: at most ${MAX_LENGTH} characters.`, MAX_LENGTH);
  return new Parser(text).parse();
}

export const ExpressionParser = { parse, MAX_LENGTH, MAX_DEPTH: MAX_NESTING };
