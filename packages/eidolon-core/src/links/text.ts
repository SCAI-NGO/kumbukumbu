// SPDX-License-Identifier: AGPL-3.0-or-later
// The pieces of a note's text the link parsers share, as the server reads them (Kumbu.Core Sh/Lines, Sections/Outline).

/** Splits on "\n"; one trailing newline ends the last line rather than adding an empty one. */
export function splitLines(text: string): string[] {
  if (text.length === 0) return [];
  return (text.endsWith("\n") ? text.slice(0, -1) : text).split("\n");
}

const FENCE = /^ {0,3}(`{3,}|~{3,})(.*)$/;

/** Which lines belong to fenced code blocks (``` or ~~~), the fence lines included. */
export function codeLines(lines: readonly string[]): boolean[] {
  const code: boolean[] = [];
  let fence: string | null = null;
  for (const line of lines) {
    const f = FENCE.exec(line);
    // A backtick fence's info string holds no backtick (CommonMark): "``` a`b" opens nothing.
    if (f && !(fence === null && f[1]![0] === "`" && f[2]!.includes("`"))) {
      const marker = f[1]!;
      code.push(true);
      if (fence === null) fence = marker;
      // A closing fence uses the same character, at least as many times, and nothing after it.
      else if (marker[0] === fence[0] && marker.length >= fence.length && f[2]!.trim().length === 0) fence = null;
      continue;
    }
    code.push(fence !== null);
  }
  return code;
}

/** The line after YAML frontmatter ("---" on the first line up to the next "---" or "..."), or 0. */
export function frontmatterEnd(lines: readonly string[]): number {
  if (lines.length === 0 || trimEnd(lines[0]!) !== "---") return 0;
  for (let i = 1; i < lines.length; i++) {
    const line = trimEnd(lines[i]!);
    if (line === "---" || line === "...") return i + 1;
  }
  return 0;
}

/** Inline code: a run of backticks up to the next run of the same length; in order, in one pass. */
export function codeSpans(line: string): Array<[start: number, end: number]> {
  const runs = [...line.matchAll(/`+/g)];
  // Each run's next run of the same length, found from the right.
  const next = new Array<number>(runs.length);
  const seen = new Map<number, number>();
  for (let i = runs.length - 1; i >= 0; i--) {
    next[i] = seen.get(runs[i]![0].length) ?? -1;
    seen.set(runs[i]![0].length, i);
  }
  const spans: Array<[number, number]> = [];
  for (let i = 0; i < runs.length; i++) {
    const j = next[i]!;
    if (j < 0) continue;
    spans.push([runs[i]!.index, runs[j]!.index + runs[j]![0].length]);
    i = j;
  }
  return spans;
}

// .NET's char.IsWhiteSpace, which string.Trim uses: unlike JavaScript's trim, it keeps U+FEFF and takes U+0085.
const SPACE = "[\\t-\\r \\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]";
const LEADING = new RegExp(`^${SPACE}+`);
const TRAILING = new RegExp(`${SPACE}+$`);
const ONE_SPACE = new RegExp(`^${SPACE}$`);

/** char.IsWhiteSpace, for one UTF-16 code unit. */
export function isWhiteSpace(char: string): boolean {
  return ONE_SPACE.test(char);
}

/** string.Trim() as the server trims. */
export function trim(text: string): string {
  return text.replace(LEADING, "").replace(TRAILING, "");
}

function trimEnd(text: string): string {
  return text.replace(TRAILING, "");
}

const ASCII = /^[\x00-\x7f]*$/;

/**
 * The key OrdinalIgnoreCase compares by: each character upper-cased on its own. A character whose upper case is longer
 * ("ß" → "SS") stays as it is, so the key keeps the text's length, as .NET's simple case mapping does.
 */
export function foldCase(text: string): string {
  if (ASCII.test(text)) return text.toUpperCase();
  let folded = "";
  for (const c of text) {
    const upper = c.toUpperCase();
    folded += upper.length === c.length ? upper : c;
  }
  return folded;
}
