// SPDX-License-Identifier: AGPL-3.0-or-later
// What a regular expression's text says before it runs (Kumbu.Core Queries/RegexShape, bases spec 2026-11-25 decision
// 15). It reads the whole pattern, and only the part of RE2's syntax that the server's engine reads the same way:
// literals, the escapes \d \D \w \W \s \S \b \B \t \n \r \f \v \xHH and an escaped punctuation mark, classes of those and
// of ranges, the dot, ^ and $, groups (…) and (?:…), alternation, the counts ? * + {n} {n,} {n,m} and their lazy forms,
// and the flags i, m and s, alone (?i) or for a group (?i:…). Anything else (comments, lookaround, backreferences, named
// groups, \p{…}, \A, other flags, a count of a count, a lone bracket, a character outside the BMP) is unsupported, so
// the two engines can't read one pattern two ways. Read the same way as the server reads it, so the same patterns are
// null in both.
//
// For a pattern it supports it gives its size, every position written out (an atom, a class or an anchor is one, an
// alternation adds its branches, a count multiplies its body, ? counts it once, * and + twice, {n,} n + 1 times and at
// least twice: \d{4}-\d{2} is seven, (?:a|aa){0,10} thirty, \w+ two), whether a count passes RE2's bound of 1,000 (alone, or multiplied through nesting), and
// the longest text a match can be, if there is one. re2js runs the pattern as written; the server rewrites it to mean
// what RE2 means.

/** How large a pattern that runs may be: the server's linear engine builds its states as it matches, and for optional
 * groups of overlapping branches they grow steeply with the size; at 64 the costliest pattern found takes under 400 ms
 * on 1,000,000 characters there (Kumbu.Core Queries/RegexShape.MaxSize). */
export const MAX_SIZE = 64;
/** RE2's bound on a count, alone and multiplied through nesting. */
export const MAX_COUNT = 1000;

// How many times an unbounded count (* + {n,}) counts its body, at least: its states wrap around, so the server's engine
// builds more of them than for the body written out once (Kumbu.Core Queries/RegexShape.Loop). {n,} counts it n + 1
// times, n written out and the loop.
const LOOP = 2;

// Deeper groups are unsupported, so reading a pattern can't run out of stack.
const MAX_DEPTH = 100;

// Large enough to pass every bound, small enough that products stay exact.
const CAP = 1_000_000_000;

/** unsupported: where the first syntax outside the subset is, from 0; null when the whole pattern is in it. */
export type Shape = { readonly size: number; readonly longest: number | null; readonly countTooLarge: boolean; readonly unsupported: number | null };

export const runs = (shape: Shape): boolean => shape.unsupported === null && !shape.countTooLarge && shape.size <= MAX_SIZE;

// size: the positions it stands for, its counts multiplied out; longest: the longest text it matches, null when
// unbounded; product: its counts multiplied through nesting.
type Part = { size: number; longest: number | null; product: number };

const EMPTY: Part = { size: 0, longest: 0, product: 1 };
const ONE: Part = { size: 1, longest: 1, product: 1 };
const ANCHOR: Part = { size: 1, longest: 0, product: 1 };
const add = (a: number, b: number) => Math.min(a + b, CAP);
const times = (a: number, b: number) => (a === 0 || b === 0 ? 0 : a > CAP / b ? CAP : Math.min(a * b, CAP));

const then = (a: Part, b: Part): Part => ({
  size: add(a.size, b.size),
  longest: a.longest !== null && b.longest !== null ? add(a.longest, b.longest) : null, product: Math.max(a.product, b.product),
});

const or = (a: Part, b: Part): Part => ({
  size: add(a.size, b.size),
  longest: a.longest !== null && b.longest !== null ? Math.max(a.longest, b.longest) : null, product: Math.max(a.product, b.product),
});

const isDigit = (c: string | undefined) => c !== undefined && c >= "0" && c <= "9";
const isHex = (c: string | undefined) => c !== undefined && /^[0-9A-Fa-f]$/.test(c);
const isSurrogate = (c: string) => c.charCodeAt(0) >= 0xd800 && c.charCodeAt(0) <= 0xdfff;
const isCount = (c: string | undefined) => c === "?" || c === "*" || c === "+" || c === "{";
const PUNCTUATION = " !\"#$%&'()*+,-./:;<=>?@[\\]^`{|}~";

class Unsupported {
  constructor(readonly at: number) {}
}

export function regexShape(p: string): Shape {
  let at = 0;
  let countTooLarge = false;
  let depth = 0;
  const unsupported = () => new Unsupported(at);

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
    while (at < p.length && p[at] !== "|" && p[at] !== ")") {
      const [piece, counts] = atom();
      if (isCount(p[at]) && !counts) throw unsupported();
      part = then(part, counts ? quantified(piece) : piece);
    }
    return part;
  };

  const number = (): number | null => {
    const start = at;
    let n = 0;
    while (isDigit(p[at])) n = Math.min(n * 10 + Number(p[at++]), CAP);
    return at === start ? null : n;
  };

  // {n}, {n,} or {n,m} with n ≤ m; anything else after { is unsupported.
  const count = (): [number, number | null] => {
    at++;
    const least = number();
    if (least === null) throw unsupported();
    let most: number | null = least;
    if (p[at] === ",") {
      at++;
      most = number();
    }
    if (p[at] !== "}" || (most !== null && most < least)) throw unsupported();
    at++;
    return [least, most];
  };

  const quantified = (part: Part): Part => {
    // How often its body counts toward the size, and toward RE2's bound on counts; the most it repeats.
    let n = 1;
    let counted = 1;
    let most: number | null = 1;
    const c = p[at];
    if (c === "*" || c === "+") {
      at++;
      n = LOOP;
      most = null;
    } else if (c === "?") {
      at++;
    } else if (c === "{") {
      const [least, upTo] = count();
      if (least > MAX_COUNT || (upTo !== null && upTo > MAX_COUNT)) countTooLarge = true;
      counted = Math.max(upTo ?? least, 1);
      n = upTo === null ? Math.max(least + 1, LOOP) : counted;
      most = upTo;
    } else {
      return part;
    }
    if (p[at] === "?") at++;
    // A count of a count: possessive in some engines, an error in others.
    if (isCount(p[at])) throw unsupported();
    const product = times(part.product, counted);
    if (product > MAX_COUNT) countTooLarge = true;
    // Its body, inner counts included, written out so many times.
    return { size: times(part.size, n), longest: most !== null && part.longest !== null ? times(part.longest, most) : null, product };
  };

  // An atom, and whether a count may follow it.
  const atom = (): [Part, boolean] => {
    const c = p[at]!;
    switch (c) {
      case "\\": return escape();
      case "[":
        characterClass();
        return [ONE, true];
      case "(": return group();
      case ".":
        at++;
        return [ONE, true];
      case "^":
      case "$":
        at++;
        return [ANCHOR, false];
      case "*": case "+": case "?": case "{": case "}": case "]":
        throw unsupported();
      default:
        if (isSurrogate(c)) throw unsupported();
        at++;
        return [ONE, true];
    }
  };

  const escape = (): [Part, boolean] => {
    at++;
    const e = p[at];
    if (e === undefined) throw unsupported();
    if ("dwsDWS".includes(e)) {
      at++;
      return [ONE, true];
    }
    if (e === "b" || e === "B") {
      at++;
      return [ANCHOR, false];
    }
    escaped();
    return [ONE, true];
  };

  // An escape that stands for one character: \t \n \r \f \v, \xHH, or punctuation.
  const escaped = () => {
    const e = p[at++]!;
    if ("tnrfv".includes(e)) return;
    if (e === "x") {
      if (!isHex(p[at]) || !isHex(p[at + 1])) {
        at--;
        throw unsupported();
      }
      at += 2;
      return;
    }
    if (PUNCTUATION.includes(e)) return;
    at--;
    throw unsupported();
  };

  // [ ^? item+ ] where an item is a character, an escape for one, \d \w \s, or a range of two characters; - only first
  // or last, ] never first, [ never inside.
  const characterClass = () => {
    at++;
    if (p[at] === "^") at++;
    for (let first = true; ; first = false) {
      const c = p[at];
      if (c === undefined || c === "[") throw unsupported();
      if (c === "]") {
        if (first) throw unsupported();
        at++;
        return;
      }
      if (c === "-") {
        if (!first && at + 1 < p.length && p[at + 1] !== "]") throw unsupported();
        at++;
        continue;
      }
      if (c === "\\" && at + 1 < p.length && "dws".includes(p[at + 1]!)) {
        at += 2;
        continue;
      }
      const from = classCharacter();
      if (p[at] === "-" && at + 1 < p.length && p[at + 1] !== "]") {
        at++;
        if (classCharacter() < from) throw unsupported();
      }
    }
  };

  // The character's code unit.
  const classCharacter = (): number => {
    const start = at;
    const c = p[at]!;
    if (c === "\\") {
      at++;
      const e = p[at];
      if (e === undefined || "dws".includes(e)) throw unsupported();
      escaped();
      const written = p.slice(start, at);
      if (written[1] === "x") return parseInt(written.slice(2), 16);
      return ({ t: 9, n: 10, r: 13, f: 12, v: 11 } as Record<string, number>)[written[1]!] ?? written.charCodeAt(1);
    }
    if (isSurrogate(c) || c === "[" || c === "]") throw unsupported();
    at++;
    return c.charCodeAt(0);
  };

  // (…), (?:…), (?flags:…), or (?flags) alone, which sets them for the rest of its group and matches nothing.
  const group = (): [Part, boolean] => {
    at++;
    if (p[at] === "?") {
      at++;
      let letters = "";
      while ((p[at] === "i" || p[at] === "m" || p[at] === "s") && !letters.includes(p[at]!)) letters += p[at++];
      if (p[at] === ")" && letters.length > 0) {
        at++;
        return [EMPTY, false];
      }
      if (p[at] !== ":") throw unsupported();
      at++;
    }
    if (++depth > MAX_DEPTH) throw unsupported();
    const inner = alternation();
    if (p[at] !== ")") throw unsupported();
    at++;
    depth--;
    return [inner, true];
  };

  try {
    const part = alternation();
    // A ) without its (.
    if (at < p.length) throw unsupported();
    return { size: part.size, longest: part.longest, countTooLarge, unsupported: null };
  } catch (e) {
    if (e instanceof Unsupported) return { size: 0, longest: null, countTooLarge: false, unsupported: e.at };
    throw e;
  }
}
