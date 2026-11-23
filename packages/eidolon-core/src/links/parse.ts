// SPDX-License-Identifier: AGPL-3.0-or-later
// The links of a note as written, read as the server reads them (Kumbu.Core Links/NoteLink, WikiLink, MarkdownLink):
// wikilinks [[target#Heading|alias]] and ![[embed]], and inline Markdown links [text](path) and ![alt](path). Fenced
// code blocks and inline code don't count; frontmatter does, as Obsidian treats links in properties as links.
import { codeLines, codeSpans, splitLines } from "./text";

/**
 * A link as written. `line` is 0-based, `start` the index of the link (its "!" for an embed) in that line. `target` is
 * the path the resolver takes, empty for a heading of the note itself; a Markdown link's is percent-decoded. `subpath`
 * is "#Heading", "#^block" or "", as written. `alias` is a wikilink's "|alias" or "\|alias" (in tables), separator
 * included, or "".
 */
export interface NoteLink { wiki: boolean; embed: boolean; line: number; start: number; raw: string; target: string; subpath: string; alias: string }

/** All links of a note, both kinds, in source order. */
export function parseLinks(content: string): NoteLink[] {
  const lines = splitLines(content);
  const code = codeLines(lines);
  const links: NoteLink[] = [];
  for (let i = 0; i < lines.length; i++) {
    if (code[i]) continue;
    const line = lines[i]!;
    // Neither kind of link overlaps the other, so their starts order them.
    const found = [...wikiLinks(i, line), ...markdownLinks(i, line)];
    links.push(...found.sort((a, b) => a.start - b.start));
  }
  return links;
}

const WIKI = /(!?)\[\[([^[\]\n]+?)\]\]/g;

function wikiLinks(index: number, line: string): NoteLink[] {
  if (!line.includes("[[")) return [];
  const spans = codeSpans(line);
  const links: NoteLink[] = [];
  let span = 0;
  for (const m of line.matchAll(WIKI)) {
    // Both in order: the spans that end before this match are behind every later one too.
    while (span < spans.length && spans[span]![1] <= m.index) span++;
    if (span < spans.length && spans[span]![0] <= m.index) continue;
    let inner = m[2]!;
    let alias = "";
    const bar = inner.indexOf("|");
    if (bar >= 0) {
      const from = bar > 0 && inner[bar - 1] === "\\" ? bar - 1 : bar;
      alias = inner.slice(from);
      inner = inner.slice(0, from);
    }
    let subpath = "";
    const hash = inner.indexOf("#");
    if (hash >= 0) {
      subpath = inner.slice(hash);
      inner = inner.slice(0, hash);
    }
    // [[ ]] and [[|x]] name nothing; Obsidian doesn't treat them as links either.
    if (inner.trim().length === 0 && subpath.length === 0) continue;
    links.push({ wiki: true, embed: m[1]!.length > 0, line: index, start: m.index, raw: m[0], target: inner, subpath, alias });
  }
  return links;
}

/** External links (a scheme or "//") are never read as a vault path. */
const SCHEME = /^[A-Za-z][A-Za-z0-9+.-]*:/;

function markdownLinks(index: number, line: string): NoteLink[] {
  if (!line.includes("[")) return [];
  const scan = new Scan(line);
  const links: NoteLink[] = [];
  for (let start = 0; start < line.length; start++) {
    if (line[start] !== "[" || scan.hidden[start] || scan.escaped[start]) continue;
    const embed = start > 0 && line[start - 1] === "!" && !scan.escaped[start - 1] && !scan.code[start - 1];
    const link = markdownLinkAt(index, scan, start, embed);
    if (link === null) continue;
    links.push(link);
    start = link.start + link.raw.length - 1;
  }
  return links;
}

/** The link at `start`, every part found by a lookup in `scan`, so a line of many "[" costs its length, not its square. */
function markdownLinkAt(index: number, scan: Scan, start: number, embed: boolean): NoteLink | null {
  const line = scan.line;
  const n = line.length;
  // Link text: brackets balance, backslash escapes don't count. No "(" right after "]": plain brackets or a
  // reference-style link, neither is read.
  let i = scan.close[start]!;
  if (i < 0 || i + 1 >= n || line[i + 1] !== "(") return null;
  i = scan.nextNonSpace[i + 2]!;
  const destinationStart = i;
  let innerStart: number, innerEnd: number;
  if (i < n && line[i] === "<") {
    i = scan.nextAngle[i + 1]!;
    if (i < 0 || line[i] === "<") return null;
    [innerStart, innerEnd] = [destinationStart + 1, i];
    i++;
  } else {
    // Parentheses inside a bare destination must balance: "Plan%20(v2).md". It ends at the first space, tab or
    // control character, or at the ")" that takes the depth below where it started.
    let stop = scan.nextStop[i]!;
    const close = scan.depthDrop[i]! - 1;
    if (close >= 0 && close < stop) stop = close;
    if (stop >= n) return null;
    [innerStart, innerEnd] = [destinationStart, stop];
    i = stop;
  }

  const destinationEnd = i;
  i = scan.nextNonSpace[i]!;
  if (i > destinationEnd && i < n && (line[i] === '"' || line[i] === "'" || line[i] === "(")) {
    const next = line[i] === '"' ? scan.nextDouble : line[i] === "'" ? scan.nextSingle : scan.nextParen;
    i = next[i + 1]!;
    if (i < 0) return null;
    i = scan.nextNonSpace[i + 1]!;
  }

  if (i >= n || line[i] !== ")") return null;
  // With a wikilink anywhere in it, "[see [[x]]](y.md)" or "[a](x[[y]].md)", Obsidian reads the wikilink.
  if (scan.wikiBefore[i + 1]! > scan.wikiBefore[start]!) return null;
  const inner = line.slice(innerStart, innerEnd);
  if (inner.length === 0 || SCHEME.test(inner) || inner.startsWith("//")) return null;

  // The fragment starts at the first raw "#", so an encoded "%23" stays part of the name.
  const hash = inner.indexOf("#");
  const path = hash >= 0 ? inner.slice(0, hash) : inner;
  const subpath = hash >= 0 ? inner.slice(hash) : "";
  if (path.length === 0 && subpath.length <= 1) return null;
  const from = embed ? start - 1 : start;
  return { wiki: false, embed, line: index, start: from, raw: line.slice(from, i + 1), target: decode(path), subpath, alias: "" };
}

/**
 * What markdownLinkAt asks of a line, each worked out for every position in a pass or two. A character is escaped when
 * an odd number of backslashes stand right before it. The "next" lookups are -1 where there is none.
 */
class Scan {
  readonly escaped: boolean[];
  /** Inside inline code. */
  readonly code: boolean[];
  /** Inside inline code or a wikilink: no link starts there. */
  readonly hidden: boolean[];
  /** How many characters before each position lie inside a wikilink. */
  readonly wikiBefore: number[];
  /** The "]" that closes the "[" at each position, brackets balanced. */
  readonly close: number[];
  /** The first unescaped space, tab or control character at or after each position; the line's length if none. */
  readonly nextStop: number[];
  /** With the depth before each position counted over unescaped parentheses, the first later position where the
   * depth is lower than here: one past the ")" that closes what starts here. */
  readonly depthDrop: number[];
  readonly nextAngle: number[];
  readonly nextDouble: number[];
  readonly nextSingle: number[];
  readonly nextParen: number[];
  /** The first position at or after each that isn't a space or tab; the line's length if none. */
  readonly nextNonSpace: number[];

  constructor(readonly line: string) {
    const n = line.length;
    this.escaped = new Array<boolean>(n).fill(false);
    for (let k = 1; k < n; k++) this.escaped[k] = line[k - 1] === "\\" && !this.escaped[k - 1];
    const escaped = this.escaped;

    this.code = new Array<boolean>(n).fill(false);
    this.hidden = new Array<boolean>(n).fill(false);
    for (const [s, e] of codeSpans(line)) for (let k = s; k < e; k++) this.code[k] = this.hidden[k] = true;

    const wiki = new Array<boolean>(n).fill(false);
    for (const m of line.matchAll(WIKI)) for (let k = m.index; k < m.index + m[0].length; k++) wiki[k] = this.hidden[k] = true;
    this.wikiBefore = new Array<number>(n + 1).fill(0);
    for (let k = 0; k < n; k++) this.wikiBefore[k + 1] = this.wikiBefore[k]! + (wiki[k] ? 1 : 0);

    this.close = new Array<number>(n).fill(-1);
    const open: number[] = [];
    for (let k = 0; k < n; k++) {
      if (escaped[k]) continue;
      if (line[k] === "[") open.push(k);
      else if (line[k] === "]" && open.length > 0) this.close[open.pop()!] = k;
    }

    const depth = new Array<number>(n + 1).fill(0);
    for (let k = 0; k < n; k++) depth[k + 1] = depth[k]! + (escaped[k] ? 0 : line[k] === "(" ? 1 : line[k] === ")" ? -1 : 0);
    // The depth moves by one at a time, so the first lower depth is the first of one less: found from the right, by
    // the last position seen at each depth (offset by n, as depths run from -n to n).
    this.depthDrop = new Array<number>(n + 1);
    const seenAt = new Array<number>(2 * n + 2).fill(-1);
    for (let k = n; k >= 0; k--) {
      const d = depth[k]! + n;
      this.depthDrop[k] = d - 1 >= 0 ? seenAt[d - 1]! : -1;
      seenAt[d] = k;
    }

    const live = (k: number, ...chars: string[]) => !escaped[k] && chars.includes(line[k]!);
    this.nextStop = next(n, (k) => !escaped[k] && (line[k] === " " || line[k] === "\t" || isControl(line.charCodeAt(k))), n);
    this.nextAngle = next(n, (k) => live(k, "<", ">"), -1);
    this.nextDouble = next(n, (k) => live(k, '"'), -1);
    this.nextSingle = next(n, (k) => live(k, "'"), -1);
    this.nextParen = next(n, (k) => live(k, ")"), -1);
    this.nextNonSpace = next(n, (k) => line[k] !== " " && line[k] !== "\t", n);
  }
}

function next(n: number, at: (k: number) => boolean, none: number): number[] {
  const result = new Array<number>(n + 1);
  result[n] = none;
  for (let k = n - 1; k >= 0; k--) result[k] = at(k) ? k : result[k + 1]!;
  return result;
}

/** char.IsControl: C0, DEL and C1. */
function isControl(c: number): boolean {
  return c < 0x20 || (c >= 0x7f && c < 0xa0);
}

// CommonMark: a backslash before ASCII punctuation stands for that character.
const ESCAPE = /\\([!-/:-@[-`{-~])/g;

/** A Markdown destination as a path: backslash escapes, then percent-escapes, as .NET's Uri.UnescapeDataString reads them. */
export function decode(path: string): string {
  return unescapeData(path.replace(ESCAPE, "$1"));
}

/** Percent-escapes that form UTF-8 become their characters; any other "%XX" stays as written. Never throws. */
function unescapeData(text: string): string {
  if (!text.includes("%")) return text;
  let result = "";
  let i = 0;
  while (i < text.length) {
    const byte = byteAt(text, i);
    if (byte < 0) {
      result += text[i];
      i++;
      continue;
    }
    if (byte < 0x80) {
      result += String.fromCharCode(byte);
      i += 3;
      continue;
    }
    const [length, min] = byte >= 0xc2 && byte <= 0xdf ? [2, 0x80] : byte >= 0xe0 && byte <= 0xef ? [3, 0x800] : byte >= 0xf0 && byte <= 0xf4 ? [4, 0x10000] : [0, 0];
    let code = length === 2 ? byte & 0x1f : length === 3 ? byte & 0x0f : byte & 0x07;
    let ok = length > 0;
    for (let k = 1; ok && k < length; k++) {
      const more = byteAt(text, i + 3 * k);
      ok = more >= 0x80 && more <= 0xbf;
      code = (code << 6) | (more & 0x3f);
    }
    if (ok && code >= min && code <= 0x10ffff && !(code >= 0xd800 && code <= 0xdfff)) {
      result += String.fromCodePoint(code);
      i += 3 * length;
    } else {
      result += text.slice(i, i + 3);
      i += 3;
    }
  }
  return result;
}

/** The byte "%XX" at `i` stands for, or -1. */
function byteAt(text: string, i: number): number {
  if (text[i] !== "%") return -1;
  const hex = text.slice(i + 1, i + 3);
  return /^[0-9A-Fa-f]{2}$/.test(hex) ? parseInt(hex, 16) : -1;
}
