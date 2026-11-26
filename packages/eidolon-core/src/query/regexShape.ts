// SPDX-License-Identifier: AGPL-3.0-or-later
// What a regular expression's text says about its cost before it runs (Kumbu.Core Queries/RegexShape, bases spec
// 2026-11-25 decision 15): how many parts its counted repetitions expand to (\d{4} four, (?:a|aa){0,10} thirty), whether
// a count passes RE2's bound of 1,000 (alone, or multiplied through nesting), and the longest text a match can be, if
// there is one. Read the same way as the server reads it, so the same patterns are null in both; the syntax only as far
// as these need, since a pattern that doesn't compile fails later anyway.

/** How many parts the counted repetitions of a pattern that runs may expand to: the server's linear engine builds its
 * states as it matches, and for overlapping alternatives in a count they grow with the square of the expansion. */
export const MAX_EXPANDED = 200;
/** RE2's bound on a count, alone and multiplied through nesting. */
export const MAX_COUNT = 1000;

// Large enough to pass every bound, small enough that products stay exact.
const CAP = 1_000_000_000;

export type Shape = { readonly expanded: number; readonly longest: number | null; readonly countTooLarge: boolean };

export const runs = (shape: Shape): boolean => !shape.countTooLarge && shape.expanded <= MAX_EXPANDED;

// size: the parts it stands for, its counts multiplied out; expanded: the parts that come from counts; longest: the
// longest text it matches, null when unbounded; product: its counts multiplied through nesting.
type Part = { size: number; expanded: number; longest: number | null; product: number };

const EMPTY: Part = { size: 0, expanded: 0, longest: 0, product: 1 };
const ONE: Part = { size: 1, expanded: 0, longest: 1, product: 1 };
const add = (a: number, b: number) => Math.min(a + b, CAP);
const times = (a: number, b: number) => (a === 0 || b === 0 ? 0 : a > CAP / b ? CAP : Math.min(a * b, CAP));

const then = (a: Part, b: Part): Part => ({
  size: add(a.size, b.size), expanded: add(a.expanded, b.expanded),
  longest: a.longest !== null && b.longest !== null ? add(a.longest, b.longest) : null, product: Math.max(a.product, b.product),
});

const or = (a: Part, b: Part): Part => ({
  size: add(a.size, b.size), expanded: add(a.expanded, b.expanded),
  longest: a.longest !== null && b.longest !== null ? Math.max(a.longest, b.longest) : null, product: Math.max(a.product, b.product),
});

const isDigit = (c: string | undefined) => c !== undefined && c >= "0" && c <= "9";

export function regexShape(p: string): Shape {
  let at = 0;
  let countTooLarge = false;

  const alternation = (): Part => {
    let part = sequence();
    while (p[at] === "|") {
      at++;
      part = or(part, sequence());
    }
    return part;
  };

  const sequence = (): Part => {
    let part = EMPTY;
    while (at < p.length && p[at] !== "|" && p[at] !== ")") part = then(part, quantified(atom()));
    return part;
  };

  const lazy = () => {
    if (p[at] === "?") at++;
  };

  // {n}, {n,} or {n,m}; anything else after { is a { of its own.
  const count = (): [number, number | null] | null => {
    let i = at + 1;
    const number = (): number => {
      let n = 0;
      while (i < p.length && isDigit(p[i])) n = Math.min(n * 10 + Number(p[i++]), CAP);
      return n;
    };
    const start = i;
    const least = number();
    if (i === start) return null;
    let most: number | null = least;
    if (p[i] === ",") {
      i++;
      const from = i;
      const m = number();
      most = i === from ? null : m;
    }
    if (p[i] !== "}") return null;
    at = i + 1;
    return [least, most];
  };

  const quantified = (part: Part): Part => {
    const c = p[at];
    if (c === "*" || c === "+") {
      at++;
      lazy();
      return { ...part, longest: null };
    }
    if (c === "?") {
      at++;
      lazy();
      return part;
    }
    const counted = c === "{" ? count() : null;
    if (counted === null) return part;
    lazy();
    const [least, most] = counted;
    const n = most ?? least;
    const product = times(part.product, Math.max(n, 1));
    if (least > MAX_COUNT || (most !== null && most > MAX_COUNT) || product > MAX_COUNT) countTooLarge = true;
    // The whole repetition is expansion: its body, inner counts included, written out so many times.
    const size = times(part.size, n);
    return { size, expanded: size, longest: most !== null && part.longest !== null ? times(part.longest, most) : null, product };
  };

  // What follows (? : a group's kind or name, or flags; false when it is flags alone, (?i), which match nothing.
  const prefix = (): boolean => {
    at++;
    const c = p[at];
    if (c === ":" || c === "=" || c === "!") {
      at++;
      return true;
    }
    if (c === "<" && (p[at + 1] === "=" || p[at + 1] === "!")) {
      at += 2;
      return true;
    }
    if (c === "<" || c === "P") {
      const close = p.indexOf(">", at);
      at = close < 0 ? p.length : close + 1;
      return true;
    }
    while (at < p.length && p[at] !== ":" && p[at] !== ")") at++;
    if (p[at] === ":") {
      at++;
      return true;
    }
    if (p[at] === ")") at++;
    return false;
  };

  const atom = (): Part => {
    switch (p[at]) {
      case "\\":
        at = Math.min(at + 2, p.length);
        return ONE;
      case "[":
        at++;
        if (p[at] === "^") at++;
        if (p[at] === "]") at++;
        while (at < p.length && p[at] !== "]") at = Math.min(at + (p[at] === "\\" ? 2 : 1), p.length);
        if (p[at] === "]") at++;
        return ONE;
      case "(": {
        at++;
        if (p[at] === "?" && !prefix()) return EMPTY;
        const inner = alternation();
        if (p[at] === ")") at++;
        return inner;
      }
      default:
        at++;
        return ONE;
    }
  };

  let part = alternation();
  // A ) without its ( ends the alternation early; the rest is read on as more of it.
  while (at < p.length) {
    at++;
    part = or(part, alternation());
  }
  return { expanded: part.expanded, longest: part.longest, countTooLarge };
}
