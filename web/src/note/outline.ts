// SPDX-License-Identifier: AGPL-3.0-or-later
import { linkLabel } from "./wikiLinks";

/** A heading of the note: its level (1–6), its text as shown, and its 1-based source line. */
export type Heading = { level: number; text: string; line: number };

// A code span (`` `…` ``, its backticks paired by count), in which `%%` is text.
const codeSpan = /(`+)[\s\S]*?(?<!`)\1(?!`)/g;

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

/**
 * The note's headings in order, as the renderer finds them: ATX (`## Goals`, at most three spaces in) and setext
 * (a paragraph over `===` or `---`, whose line is the paragraph's first), never inside fenced code or a `%%` comment
 * (remarkComments hides those). The body keeps the source's line numbers (front matter lines are empty).
 */
export function outline(body: string): Heading[] {
  const headings: Heading[] = [];
  let fence: string | null = null;
  let comment = false;
  // The paragraph the current line continues: its first line (0-based) and its text so far.
  let para: { start: number; text: string[] } | null = null;
  body.split("\n").forEach((source, i) => {
    // Fences are found on the source, comments or not: Markdown parses the code first, and a `%%` in it is text.
    const marker = /^ {0,3}(`{3,}|~{3,})/.exec(source)?.[1];
    if (marker && (fence === null || (marker[0] === fence[0] && marker.length >= fence.length))) {
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
    const m = /^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$/.exec(line);
    if (m && m[2]) headings.push({ level: m[1].length, text: headingText(m[2]), line: i + 1 });
    if (m || !paragraphLine(line)) para = null;
    else if (para) para.text.push(line.trim());
    else para = { start: i, text: [line.trim()] };
  });
  return headings;
}

/**
 * The section under `heading` (its text as shown, ignoring case): from that heading to the next of the same or a
 * higher level. The other lines are left empty, so the section keeps the note's line numbers (its links go by them);
 * null when the note has no such heading.
 */
export function section(body: string, heading: string): string | null {
  const headings = outline(body);
  const at = headings.findIndex((h) => h.text.toLowerCase() === heading.toLowerCase());
  if (at < 0) return null;
  const start = headings[at].line;
  const end = headings.slice(at + 1).find((h) => h.level <= headings[at].level)?.line ?? Infinity;
  return body
    .split("\n")
    .map((l, i) => (i + 1 >= start && i + 1 < end ? l : ""))
    .join("\n");
}
