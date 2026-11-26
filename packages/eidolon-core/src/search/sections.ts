// SPDX-License-Identifier: AGPL-3.0-or-later
// A note's sections as the server's words search reads them (Kumbu.Core Search/Chunker.Sections, Sections/Outline,
// SectionAddress.Escape) and the snippet it shows for one (SectionSearch.Snippet).
import { codeLines, foldCase, frontmatterEnd, splitLines, trim } from "../links/text";

/** The lines [startLine, endLine) of a note, 0-based; `address` is "#A#B", or "" for the text before the first heading. */
export interface Section { address: string; startLine: number; endLine: number; text: string; context: string }

interface Heading { level: number; text: string; line: number; parent: Heading | null; address: string }

/** The longest section before it comes in parts. */
const MAX_CHARS = 2000;
const SNIPPET_LENGTH = 160;

// Up to three spaces and 1-6 hashes, then a space or tab, or the line's end.
const OPENING = /^ {0,3}(#{1,6})(?=[ \t]|$)/;
// The server's pattern reads its text with ".", which takes no line break.
const BREAK = /[\r\u2028\u2029]/;

const isTab = (c: string | undefined) => c === " " || c === "\t";

/**
 * A heading as the server's pattern `^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$` reads it, without a pattern:
 * its level and text, a closing hash run and the spaces around the text dropped; null for a line that is no heading.
 */
function headingOf(line: string): { level: number; text: string } | null {
  const opening = OPENING.exec(line);
  if (opening === null || BREAK.test(line)) return null;
  let start = opening[0].length;
  while (isTab(line[start])) start++;
  let end = line.length;
  while (end > start && isTab(line[end - 1])) end--;
  let hashes = end;
  while (hashes > start && line[hashes - 1] === "#") hashes--;
  let gap = hashes;
  while (gap > start && isTab(line[gap - 1])) gap--;
  // A closing run: hashes up to the end, after a space or tab that follows some text.
  return { level: opening[1]!.length, text: line.slice(start, hashes < end && gap < hashes ? gap : end) };
}
// CommonMark: a backslash before ASCII punctuation makes it literal.
const ESCAPED = /\\([!-/:-@[-`{-~])/g;

const blank = (line: string) => trim(line).length === 0;

/** A heading's text as an address holds it: `\` and `#` escaped, and a trailing "[n]" that would read as an index. */
function escape(text: string): string {
  const escaped = text.replaceAll("\\", "\\\\").replaceAll("#", "\\#");
  const m = /\[(\d+)\]$/.exec(escaped);
  return m === null ? escaped : `${escaped.slice(0, m.index)}\\${escaped.slice(m.index)}`;
}

function headingsOf(lines: readonly string[]): Heading[] {
  const code = codeLines(lines);
  const found: { level: number; text: string; line: number; parent: number }[] = [];
  const open: number[] = [];
  for (let i = frontmatterEnd(lines); i < lines.length; i++) {
    const h = code[i] ? null : headingOf(lines[i]!);
    if (h === null) continue;
    const level = h.level;
    while (open.length > 0 && found[open[open.length - 1]!]!.level >= level) open.pop();
    found.push({ level, text: trim(h.text).replace(ESCAPED, "$1"), line: i, parent: open[open.length - 1] ?? -1 });
    open.push(found.length - 1);
  }
  // Siblings with the same text get 1-based indexes.
  const totals = new Map<string, number>();
  const key = (f: (typeof found)[number]) => `${f.parent}\0${f.text}`;
  for (const f of found) totals.set(key(f), (totals.get(key(f)) ?? 0) + 1);
  const seen = new Map<string, number>();
  const headings: Heading[] = [];
  for (const f of found) {
    let index = 0;
    if (totals.get(key(f))! > 1) seen.set(key(f), (index = (seen.get(key(f)) ?? 0) + 1));
    const parent = f.parent < 0 ? null : headings[f.parent]!;
    headings.push({ level: f.level, text: f.text, line: f.line, parent, address: `${parent?.address ?? ""}#${escape(f.text)}${index > 0 ? `[${index}]` : ""}` });
  }
  return headings;
}

/** The note's name without ".md". */
export function titleOf(path: string): string {
  const name = path.slice(path.lastIndexOf("/") + 1);
  return name.endsWith(".md") ? name.slice(0, -3) : name;
}

/** The sections of a note: the text before the first heading, then each heading up to the next of any level. */
export function sectionsOf(path: string, content: string): Section[] {
  const lines = splitLines(content);
  const headings = headingsOf(lines);
  const title = titleOf(path);
  const sections: Section[] = [];
  add(sections, "", title, lines, frontmatterEnd(lines), headings.length > 0 ? headings[0]!.line : lines.length);
  headings.forEach((h, i) => {
    const chain: string[] = [];
    for (let c: Heading | null = h; c !== null; c = c.parent) chain.unshift(c.text);
    add(sections, h.address, `${title} > ${chain.join(" > ")}`, lines, h.line, i + 1 < headings.length ? headings[i + 1]!.line : lines.length);
  });
  return sections;
}

/** The lines [start, end) as one section, or as several of at most MAX_CHARS, split between paragraphs where possible. */
function add(sections: Section[], address: string, context: string, lines: readonly string[], start: number, end: number): void {
  while (end > start && blank(lines[end - 1]!)) end--;
  while (start < end && blank(lines[start]!)) start++;
  if (start >= end) return;
  let partStart = start;
  let size = 0;
  let lastBreak = -1;
  for (let i = start; i < end; i++) {
    if (size > 0 && size + lines[i]!.length + 1 > MAX_CHARS) {
      const cut = lastBreak > partStart ? lastBreak : i;
      emit(sections, address, context, lines, partStart, cut);
      partStart = cut;
      while (partStart < end && blank(lines[partStart]!)) partStart++;
      size = lines.slice(partStart, i).reduce((sum, l) => sum + l.length + 1, 0);
      lastBreak = -1;
    }
    if (blank(lines[i]!)) lastBreak = i;
    size += lines[i]!.length + 1;
  }
  if (partStart < end) emit(sections, address, context, lines, partStart, end);
}

function emit(sections: Section[], address: string, context: string, lines: readonly string[], start: number, end: number): void {
  while (end > start && blank(lines[end - 1]!)) end--;
  if (start >= end) return;
  let text = lines.slice(start, end).join("\n");
  // A single line longer than the limit is cut; search still finds its start.
  if (text.length > MAX_CHARS * 2) text = text.slice(0, MAX_CHARS * 2);
  sections.push({ address, startLine: start, endLine: end, text, context });
}

/** The non-blank lines of a section that aren't headings; a "# comment" in fenced code is no heading. */
function body(section: Section): string[] {
  const lines = section.text.split("\n");
  const code = codeLines(lines);
  return lines.filter((l, i) => !blank(l) && (code[i] || headingOf(l) === null));
}

/** A section that holds only its heading. */
export const isHeadingOnly = (section: Section) => !blank(section.text) && body(section).length === 0;

const shorten = (text: string) => (text.length <= SNIPPET_LENGTH ? text : `${text.slice(0, SNIPPET_LENGTH)}…`);

/** The words of a text as search compares them: letters and digits, NFKC, lower case. */
export const wordsOf = (text: string): string[] => (text.normalize("NFKC").match(/[\p{L}\p{N}]+/gu) ?? []).map((w) => w.toLowerCase());

/**
 * The line of the section, below its heading, that matched best: the first that contains the whole query (ignoring
 * case), else the first with the most distinct words of it; the start of the section if no line holds any of them.
 */
export function snippetOf(section: Section, query: string): string {
  const terms = new Set(wordsOf(query));
  const whole = foldCase(query.normalize("NFKC"));
  let best: string | null = null;
  let most = 0;
  const lines = body(section).map(trim);
  for (const line of lines) {
    if (foldCase(line.normalize("NFKC")).includes(whole)) return shorten(line);
    const count = new Set(wordsOf(line).filter((w) => terms.has(w))).size;
    if (count > most) [best, most] = [line, count];
  }
  if (best !== null) return shorten(best);
  const start = lines.join(" ");
  return shorten(start.length > 0 ? start : trim(section.text));
}
