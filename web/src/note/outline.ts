// SPDX-License-Identifier: AGPL-3.0-or-later
import { linkLabel } from "./wikiLinks";

/** A heading of the note: its level (1–6), its text as shown, and its 1-based source line. */
export type Heading = { level: number; text: string; line: number };

/** A heading's Markdown as it reads: wiki links by their label, emphasis and code marks and links' URLs gone. */
const plain = (md: string) =>
  md
    .replace(/!?\[\[[^[\]\n]+?\]\]/g, (raw) => linkLabel(raw))
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/(\*\*|__|\*|_|~~|`)(.+?)\1/g, "$2")
    .trim();

/**
 * The note's ATX headings (`## Goals`) in order, as the renderer finds them: never inside fenced code, at most
 * three spaces of indentation. The body keeps the source's line numbers (front matter lines are empty).
 */
export function outline(body: string): Heading[] {
  const headings: Heading[] = [];
  let fence: string | null = null;
  body.split("\n").forEach((line, i) => {
    const marker = /^ {0,3}(`{3,}|~{3,})/.exec(line)?.[1];
    if (marker && (fence === null || (marker[0] === fence[0] && marker.length >= fence.length))) {
      fence = fence === null ? marker : null;
      return;
    }
    if (fence !== null) return;
    const m = /^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$/.exec(line);
    if (m && m[2]) headings.push({ level: m[1].length, text: plain(m[2]), line: i + 1 });
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
