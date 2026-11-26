// SPDX-License-Identifier: AGPL-3.0-or-later
// What the TypeScript parser has inside and the shared cases can't show: its limits, where it reports, how it reads a
// regular expression literal. What an expression gives is in tests/query-conformance/expressions.
import { expect, test } from "vitest";
import { QueryError, QuerySyntaxError, inExpression } from "../src/query/errors";
import { ExpressionParser } from "../src/query/parser";

const parse = ExpressionParser.parse;
const failure = (text: string): QuerySyntaxError => {
  try {
    parse(text);
  } catch (e) {
    return e as QuerySyntaxError;
  }
  throw new Error(`"${text}" parsed.`);
};

test("an expression is at most 10 000 characters", () => {
  expect(() => parse(`1${" ".repeat(ExpressionParser.MAX_LENGTH - 1)}`)).not.toThrow();
  const error = failure(`1${" ".repeat(ExpressionParser.MAX_LENGTH)}`);
  expect(error.message).toBe("The expression is too long: at most 10000 characters.");
  expect(error.position).toBe(10_000);
});

test("nesting is bounded at 64 levels, whatever the brackets", () => {
  const nested = (open: string, close: string, levels: number) => `${open.repeat(levels)}1${close.repeat(levels)}`;
  expect(() => parse(nested("(", ")", 63))).not.toThrow();
  expect(failure(nested("(", ")", 64)).message).toBe("The expression is nested too deeply: at most 64 levels.");
  expect(failure(nested("[", "]", 70)).message).toMatch(/nested too deeply/);
  expect(failure(`${"!".repeat(70)}a`).message).toMatch(/nested too deeply/);
  expect(failure(`${"-".repeat(70)}1`).message).toMatch(/nested too deeply/);
  expect(failure(`${"f(".repeat(70)}1${")".repeat(70)}`).message).toMatch(/nested too deeply/);
});

test("a long chain of one operator is built without recursion", () => {
  expect(() => parse(Array(1900).fill("a").join(" && "))).not.toThrow();
});

test("a message says what was expected, what was found and where", () => {
  const equals = failure("a = 1");
  expect(equals.message).toBe("use == to compare");
  expect(equals.position).toBe(2);
  expect(failure("a & b").message).toBe("Expected the end of the expression, found '&' (use '&&').");
  expect(failure("(1").message).toBe("Expected ')', found the end.");
  expect(failure("1 +").position).toBe(3);
  expect(failure("a.").message).toBe("Expected a name after '.', found the end.");
  expect(failure('"open').message).toBe("The string is unterminated.");
  expect(failure('"a\\q"')).toMatchObject({ position: 2 });
});

test("a regular expression literal keeps an escaped slash and reads its flags", () => {
  expect(parse("/a\\/b/i")).toMatchObject({ kind: "regex", pattern: "a\\/b", flags: "i" });
  expect(parse("/x/gimsu")).toMatchObject({ flags: "gimsu" });
  expect(failure("/abc").message).toBe("The regular expression is unterminated.");
  // An unknown or repeated flag is the evaluator's to make null, not a syntax error.
  expect(parse("/a/ix")).toMatchObject({ kind: "regex", pattern: "a", flags: "ix" });
});

test("a name is letters, digits and underscores, as .NET counts them", () => {
  expect(parse("größe_2")).toMatchObject({ kind: "name", name: "größe_2" });
  expect(failure("2a").position).toBe(1);
});

test("an error is told in which expression it is, once", () => {
  const told = (run: () => unknown) => {
    try {
      run();
    } catch (e) {
      return e as QueryError;
    }
    throw new Error("No error.");
  };
  const syntax = told(() => inExpression("a = 1", () => parse("a = 1")));
  expect(syntax).toMatchObject({ code: "syntax", expression: "a = 1", position: 2 });
  const inner = new QueryError("unknown-formula", "m", 4, "inner");
  expect(told(() => inExpression("outer", () => { throw inner; })).expression).toBe("inner");
  expect(told(() => inExpression("outer", () => { throw new QueryError("regex", "m", 1); })).expression).toBe("outer");
});
