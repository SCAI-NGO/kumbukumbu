// SPDX-License-Identifier: AGPL-3.0-or-later
import { linkLabel } from "./wikiLinks";

/** A heading of the note: its level (1–6), its text as shown, and its 1-based source line. */
export type Heading = { level: number; text: string; line: number };

// A code span (`` `…` ``, its backticks paired by count), in which `%%` is text.
export const codeSpan = /(`+)[\s\S]*?(?<!`)\1(?!`)/g;

/** The text with its code spans blanked, so `%%` is found where it starts or ends a comment, at the same places. */
const masked = (text: string) => text.replace(codeSpan, (span) => " ".repeat(span.length));

/** What of `text` lies outside comments, one opened before it when `open`; and whether one is open at its end. */
function outsideComments(text: string, open: boolean): { text: string; open: boolean } {
  const hidden = masked(text);
  let shown = "";
  let from = 0;
  for (let p = hidden.indexOf("%%"); p >= 0; p = hidden.indexOf("%%", p + 2)) {
    if (!open) shown += text.slice(from, p);
    open = !open;
    from = p + 2;
  }
  return { text: open ? shown : shown + text.slice(from), open };
}

/**
 * A heading's Markdown as it reads: wiki links by their label, emphasis, highlight and code marks, links' URLs and
 * comments gone.
 */
export const headingText = (md: string) =>
  outsideComments(md, false)
    .text.replace(/!?\[\[[^[\]\n]+?\]\]/g, (raw) => linkLabel(raw))
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/==(?=[^\s=])(.+?)(?<=[^\s=])==/g, "$1")
    .replace(/(\*\*|__|\*|_|~~|`)(.+?)\1/g, "$2")
    .trim();

/** A line that can be (part of) a paragraph's text, and so a setext heading's: no other block starts with it. */
const paragraphLine = (line: string) => line.trim() !== "" && !/^ {4}|^\t|^ {0,3}([-*+>|#]|\d+[.)]\s|`{3}|~{3})/.test(line);

const blank = (c: string) => c === " " || c === "\t";

/**
 * An ATX heading line's level and text (`text` undefined: none; "" after a lone blank), as CommonMark's pattern
 * `^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$` reads it. `anyChar`: the text may hold a "\r", as the
 * Core's `.` takes it (the block finder's lines).
 */
export function atxHeading(line: string, anyChar = false): { level: number; text?: string } | null {
  let i = 0;
  while (i < 3 && line[i] === " ") i++;
  let h = i;
  while (line[h] === "#") h++;
  const level = h - i;
  if (level < 1 || level > 6) return null;
  if (h === line.length) return { level };
  if (!blank(line[h])) return null;
  if (!anyChar && /[\r\u2028\u2029]/.test(line.slice(h))) return null;
  // The text starts after the blanks that follow the hashes, and ends before the trailing blanks and before a closing
  // hash run that has a blank before it and something before that.
  let t = h;
  while (t < line.length && blank(line[t])) t++;
  let e = line.length;
  while (e > t && blank(line[e - 1])) e--;
  let k = e;
  while (k > t && line[k - 1] === "#") k--;
  let q = k;
  while (q > t && blank(line[q - 1])) q--;
  return { level, text: line.slice(t, k < e && q < k ? q : e) };
}

/** The fence (```` ``` ````, `~~~~`) a line opens, none being open, or closes, the `open` one being open; else null. */
export function fenceMarker(line: string, open: string | null): string | null {
  const marker = /^ {0,3}(`{3,}|~{3,})/.exec(line)?.[1];
  return marker && (open === null || (marker[0] === open[0] && marker.length >= open.length)) ? marker : null;
}

/**
 * The note's headings in order, as the renderer finds them: ATX (`## Goals`, at most three spaces in) and setext
 * (a paragraph over `===` or `---`, whose line is the paragraph's first), never inside fenced code or a `%%` comment
 * (the view hides those, comments.ts). The body keeps the source's line numbers (front matter lines are empty).
 */
export function outline(body: string): Heading[] {
  const headings: Heading[] = [];
  let fence: string | null = null;
  let comment = false;
  // The paragraph the current line continues: its first line (0-based) and its text so far.
  let para: { start: number; text: string[] } | null = null;
  body.split("\n").forEach((source, i) => {
    // Fences are found on the source, comments or not: Markdown parses the code first, and a `%%` in it is text.
    const marker = fenceMarker(source, fence);
    if (marker) {
      fence = fence === null ? marker : null;
      para = null;
      return;
    }
    if (fence !== null) return;
    // Indented code (not a paragraph's next line) holds no heading and no comment's start or end.
    if (!para && /^( {4}|\t)/.test(source) && source.trim()) return;
    // The line as it shows: what lies outside comments.
    const shown = outsideComments(source, comment);
    comment = shown.open;
    const line = shown.text;
    const setext = /^ {0,3}(=+|-+)[ \t]*$/.exec(line);
    if (setext && para) {
      headings.push({ level: setext[1][0] === "=" ? 1 : 2, text: headingText(para.text.join(" ")), line: para.start + 1 });
      para = null;
      return;
    }
    const m = atxHeading(line);
    if (m?.text) headings.push({ level: m.level, text: headingText(m.text), line: i + 1 });
    if (m || !paragraphLine(line)) para = null;
    else if (para) para.text.push(line.trim());
    else para = { start: i, text: [line.trim()] };
  });
  return headings;
}

/** Markdown's backslash escapes taken out, as the Core and Obsidian read them (`C\#` is `C#`). */
const unescaped = (text: string) => text.replace(/\\([!-/:-@[-`{-~])/g, "$1");

/** A heading's text as written in the source (`The **road**map`), from its ATX line; a setext heading's as shown. */
const writtenHeading = (lines: string[], h: Heading) => atxHeading(lines[h.line - 1].replace(/\r$/, ""))?.text?.trim() || h.text;

/**
 * A heading as the link list writes it in a link (its text as shown, `headingText`): its escapes taken out, then
 * without `# | ^ [ ]` and `%%`, blanks collapsed (`C\# notes | x` → `C notes x`).
 */
export const headingLink = (text: string) =>
  unescaped(text)
    .replace(/%%|[#|^[\]]/g, "")
    .replace(/\s+/g, " ")
    .trim();

/**
 * Whether a link's heading names the heading, shown as `shown` and written as `written`: the characters a link can't
 * hold dropped from all three, ignoring case. The list inserts the shown form; Obsidian users may write the other.
 */
const linksTo = (link: string, shown: string, written: string) => {
  const own = headingLink(link).toLowerCase();
  return own !== "" && (own === headingLink(shown).toLowerCase() || own === headingLink(written).toLowerCase());
};

/**
 * The section under `heading` (its text as shown, ignoring case, else as a link holds the heading as shown or as
 * written, `headingLink`): from that heading to the next of the same or a higher level. The other lines are left
 * empty, so the section keeps the note's line numbers (its links go by them); null when the note has no such heading.
 */
export function section(body: string, heading: string): string | null {
  const headings = outline(body);
  const lines = body.split("\n");
  let at = headings.findIndex((h) => h.text.toLowerCase() === heading.toLowerCase());
  if (at < 0) at = headings.findIndex((h) => linksTo(heading, h.text, writtenHeading(lines, h)));
  if (at < 0) return null;
  const start = headings[at].line;
  const end = headings.slice(at + 1).find((h) => h.level <= headings[at].level)?.line ?? Infinity;
  return body
    .split("\n")
    .map((l, i) => (i + 1 >= start && i + 1 < end ? l : ""))
    .join("\n");
}

/** A section address's segments, as the Core reads one: `#` goes one level down, `\#` is a `#`, a trailing `[n]` an index. */
function addressSegments(address: string): { text: string; index?: number }[] | null {
  const segments: { text: string; index?: number }[] = [];
  let text = "";
  let escapedEnd = -1;
  for (let i = 0; i <= address.length; i++) {
    if (i === address.length || address[i] === "#") {
      // A trailing [n] is an index unless its `[` was escaped.
      const m = /\[(\d+)\]$/.exec(text);
      const index = m && m.index >= escapedEnd ? Number(m[1]) : undefined;
      const own = (index === undefined ? text : text.slice(0, m!.index)).trim();
      if (!own) return null;
      segments.push({ text: own, index });
      text = "";
      escapedEnd = -1;
    } else if (address[i] === "\\" && i + 1 < address.length) {
      text += address[++i];
      escapedEnd = text.length;
    } else text += address[i];
  }
  return segments;
}

/**
 * The 1-based source line of the heading a section address names (`Roadmap`, `A#B`, `Notes[2]`), as `cat note#…`
 * resolves it: its own text and the parents above it match the segments, exactly, else ignoring case, else as a link
 * holds a heading as shown or as written (`headingLink`; the link list inserts the shown form). Of several matches
 * the first in the note wins; null when none does.
 */
export function headingLine(body: string, address: string): number | null {
  const segments = addressSegments(address);
  if (!segments) return null;
  // Each heading's parent (the nearest earlier one of a lower level) and its place among siblings of the same text.
  // A heading answers to its text as written, as the Core keeps it (`The **road**map`), and as shown (`The roadmap`);
  // both with Markdown's backslash escapes taken out, as the Core and Obsidian read them (`C\#` is `C#`).
  type Node = { written: string; shown: string; raw: [string, string]; line: number; parent: Node | null; index: number };
  const lines = body.split("\n");
  const nodes: Node[] = [];
  const open: (Node & { level: number })[] = [];
  // Per parent (null: the top), how many headings of each written text it has so far: duplicates are numbered by it.
  const seen = new Map<Node | null, Map<string, number>>();
  for (const h of outline(body)) {
    while (open.length && open[open.length - 1].level >= h.level) open.pop();
    const parent = open[open.length - 1] ?? null;
    const shown = unescaped(h.text);
    // A setext heading has no ATX line: its text as written is the one shown.
    const raw = writtenHeading(lines, h);
    const written = unescaped(raw);
    const siblings = seen.get(parent) ?? new Map<string, number>();
    seen.set(parent, siblings);
    const index = (siblings.get(written) ?? 0) + 1;
    siblings.set(written, index);
    const node = { written, shown, raw: [h.text, raw] as [string, string], line: h.line, parent, index, level: h.level };
    nodes.push(node);
    open.push(node);
  }
  // Exactly, then ignoring case, then as a link holds a heading (`[[N#C notes x]]` for `## C# notes | x`), shown or written.
  const passes = [
    (text: string, h: Node) => text === h.written || text === h.shown,
    (text: string, h: Node) => [h.written, h.shown].some((t) => t.toLowerCase() === text.toLowerCase()),
    (text: string, h: Node) => linksTo(text, ...h.raw),
  ];
  for (const same of passes) {
    const found = nodes.find((n) => {
      let h: Node | null = n;
      for (let i = segments.length - 1; i >= 0; i--, h = h.parent) {
        const { text, index } = segments[i];
        if (!h || !same(text, h) || (index !== undefined && index !== h.index)) return false;
      }
      return true;
    });
    if (found) return found.line;
  }
  return null;
}
