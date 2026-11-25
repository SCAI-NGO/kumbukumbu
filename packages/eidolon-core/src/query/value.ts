// SPDX-License-Identifier: AGPL-3.0-or-later
// A typed value as a query sees it: a property, a parameter or what an expression gives (Kumbu.Core Queries/Value).
import type { QueryNote } from "./note";

/** What a pattern compiles to; the expression engine gives it its matching. Displays as the pattern. */
export type RegexMatcher = { toString(): string };

export type Value =
  | { readonly kind: "null" }
  | { readonly kind: "text"; readonly text: string }
  | { readonly kind: "number"; readonly number: number }
  | { readonly kind: "boolean"; readonly boolean: boolean }
  /**
   * A date, with a time or without one, and no zone, as Obsidian keeps it: one type, as in Obsidian. The moment is
   * "yyyy-MM-ddTHH:mm:ss", then up to 7 fractional digits when not zero; a date without a time is its midnight, so it
   * equals only midnight. A text, not a JavaScript Date, so no zone can move it.
   */
  | { readonly kind: "date"; readonly moment: string; readonly hasTime: boolean }
  | { readonly kind: "list"; readonly items: readonly Value[] }
  | { readonly kind: "object"; readonly values: ReadonlyMap<string, Value> }
  /** A note's file, as `file` and `this.file` give it: two are equal when their paths are. */
  | { readonly kind: "file"; readonly note: QueryNote }
  /** A note as `this` gives it: `this.file`, `this.note`, and its properties by name. */
  | { readonly kind: "noteRef"; readonly note: QueryNote }
  | { readonly kind: "regex"; readonly regex: RegexMatcher };

export const NULL: Value = { kind: "null" };
export const textValue = (text: string): Value => ({ kind: "text", text });
export const numberValue = (number: number): Value => ({ kind: "number", number });
export const booleanValue = (boolean: boolean): Value => ({ kind: "boolean", boolean });
/** A date from "yyyy-MM-dd", without a time, or from a moment "yyyy-MM-ddTHH:mm:ss…", with one. */
export const dateValue = (text: string): Value =>
  text.length === 10 ? { kind: "date", moment: `${text}T00:00:00`, hasTime: false } : { kind: "date", moment: text, hasTime: true };
export const listValue = (items: readonly Value[]): Value => ({ kind: "list", items });
export const objectValue = (values: ReadonlyMap<string, Value>): Value => ({ kind: "object", values });
export const fileValue = (note: QueryNote): Value => ({ kind: "file", note });
export const noteRefValue = (note: QueryNote): Value => ({ kind: "noteRef", note });
export const regexValue = (regex: RegexMatcher): Value => ({ kind: "regex", regex });

/**
 * A number as .NET writes its round-trip form ("R"), which the cases and the server's output are in: the shortest
 * digits that read back, fixed notation for exponents from -4 to 14 and otherwise "1E+21", "1E-05".
 */
export function formatNumber(n: number): string {
  if (Number.isNaN(n)) return "NaN";
  if (n === Infinity) return "Infinity";
  if (n === -Infinity) return "-Infinity";
  if (n === 0) return Object.is(n, -0) ? "-0" : "0";
  const [mantissa, exponent] = Math.abs(n).toExponential().split("e") as [string, string];
  const digits = mantissa.replace(".", "");
  const x = Number(exponent);
  const sign = n < 0 ? "-" : "";
  if (x >= 15 || x <= -5) {
    const rest = digits.length > 1 ? `.${digits.slice(1)}` : "";
    return `${sign}${digits[0]}${rest}E${x < 0 ? "-" : "+"}${String(Math.abs(x)).padStart(2, "0")}`;
  }
  if (x < 0) return `${sign}0.${"0".repeat(-x - 1)}${digits}`;
  if (digits.length <= x + 1) return `${sign}${digits}${"0".repeat(x + 1 - digits.length)}`;
  return `${sign}${digits.slice(0, x + 1)}.${digits.slice(x + 1)}`;
}
