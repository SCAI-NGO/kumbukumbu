// SPDX-License-Identifier: AGPL-3.0-or-later

/**
 * Obsidian's block ids, as the Core's `Blocks` finds them (src/Kumbu.Core/Sections/Blocks.cs, spec §4.1): `^id` after
 * a space at the end of a paragraph, list item, quote, callout or heading, or on a line of its own after one blank
 * line, naming the paragraph, list, table or quote above it. Never inside front matter or code. Both check themselves
 * against blocks.cases.json; a change here goes there too.
 */

type Kind = "heading" | "paragraph" | "list" | "table" | "quote";

// 0-based lines, `end` excluded.
type Chunk = { kind: Kind; start: number; end: number };

const inline = /[ \t]\^([A-Za-z0-9-]+)\s*$/;
const ownLine = /^\^([A-Za-z0-9-]+)\s*$/;
const item = /^[ \t]*(?:[-*+]|\d{1,9}[.)])(?:[ \t]|$)/;
const quote = /^ {0,3}>/;
const delimiterRow = /^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*$/;
const heading = /^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$/;
const fenceLine = /^ {0,3}(`{3,}|~{3,})(.*)$/;

const blank = (line: string) => line.trim() === "";

// A tab takes the column to the next multiple of four.
function indent(line: string) {
  let column = 0;
  for (const ch of line) {
    if (ch === " ") column++;
    else if (ch === "\t") column += 4 - (column % 4);
    else break;
  }
  return column;
}

// Fenced code, its fences included, as the Core's `Outline.CodeLines` tells it.
function codeLines(lines: string[]) {
  let fence: string | null = null;
  return lines.map((line) => {
    const f = fenceLine.exec(line);
    // A backtick fence's info string holds no backtick (CommonMark): "``` a`b" opens nothing.
    if (f && !(fence === null && f[1][0] === "`" && f[2].includes("`"))) {
      if (fence === null) fence = f[1];
      // A closing fence uses the same character, at least as many times, and nothing after it.
      else if (f[1][0] === fence[0] && f[1].length >= fence.length && f[2].trim() === "") fence = null;
      return true;
    }
    return fence !== null;
  });
}

function frontmatterEnd(lines: string[]) {
  if (lines.length === 0 || lines[0].trimEnd() !== "---") return 0;
  const end = lines.findIndex((l, i) => i > 0 && (l.trimEnd() === "---" || l.trimEnd() === "..."));
  return end < 0 ? 0 : end + 1;
}

// The note's chunks outside front matter and code, split at blank lines, headings and fences.
function chunks(lines: string[]): Chunk[] {
  const found: Chunk[] = [];
  const code = codeLines(lines);
  const body = frontmatterEnd(lines);
  let i = body;
  while (i < lines.length) {
    if (code[i] || blank(lines[i])) {
      i++;
      continue;
    }
    // Indented code follows a blank line (or no text at all), but in a list it continues an item.
    if (indent(lines[i]) >= 4 && (i === body || blank(lines[i - 1]) || code[i - 1]) && found.at(-1)?.kind !== "list") {
      while (i < lines.length && !code[i] && (blank(lines[i]) || indent(lines[i]) >= 4)) i++;
      continue;
    }
    if (heading.test(lines[i])) {
      found.push({ kind: "heading", start: i, end: i + 1 });
      i++;
      continue;
    }
    const kind: Kind = item.test(lines[i])
      ? "list"
      : quote.test(lines[i])
        ? "quote"
        : i + 1 < lines.length && lines[i].includes("|") && delimiterRow.test(lines[i + 1])
          ? "table"
          : "paragraph";
    const start = i++;
    while (
      i < lines.length &&
      !code[i] &&
      !blank(lines[i]) &&
      !heading.test(lines[i]) &&
      !(kind === "paragraph" && (item.test(lines[i]) || quote.test(lines[i])))
    ) {
      i++;
    }
    found.push({ kind, start, end: i });
  }
  return found;
}

// An item runs to the next item indented no deeper than its marker, or to text indented less.
function itemEnd(lines: string[], at: number, end: number) {
  const marker = indent(lines[at]);
  for (let i = at + 1; i < end; i++) {
    if (item.test(lines[i]) ? indent(lines[i]) <= marker : indent(lines[i]) < marker) return i;
  }
  return end;
}

// The note's blocks in order: their ids and 0-based lines, the end excluded.
function blocks(lines: string[]) {
  const found: { id: string; start: number; end: number }[] = [];
  const all = chunks(lines);
  all.forEach(({ kind, start, end }, c) => {
    const last = lines[end - 1];
    if (kind === "heading" || kind === "quote") {
      const m = inline.exec(last);
      if (m) found.push({ id: m[1], start, end });
    } else if (kind === "list") {
      for (let i = start; i < end; i++) {
        const m = item.test(lines[i]) ? inline.exec(lines[i]) : null;
        if (m) found.push({ id: m[1], start: i, end: itemEnd(lines, i, end) });
      }
    } else if (kind === "paragraph") {
      const own = end - start === 1 ? ownLine.exec(last) : null;
      if (own) {
        // Exactly one blank line between it and the chunk it names.
        const above = c > 0 ? all[c - 1] : null;
        if (above && above.kind !== "heading" && above.end === start - 1 && blank(lines[start - 1])) found.push({ id: own[1], start: above.start, end });
      } else {
        // Right below a paragraph's text, an id on its own line is the paragraph's last line.
        const m = inline.exec(last) ?? (end - start > 1 ? ownLine.exec(last) : null);
        if (m) found.push({ id: m[1], start, end });
      }
    }
  });
  return found;
}

// As the Core splits a note into lines: at "\n", a final one ending the last line.
const linesOf = (source: string) => (source === "" ? [] : (source.endsWith("\n") ? source.slice(0, -1) : source).split("\n"));

/** The first block `id` names, ignoring case: its 1-based lines of `source`, both ends included; null when none does. */
export function findBlock(source: string, id: string): { start: number; end: number } | null {
  const wanted = id.toLowerCase();
  const block = blocks(linesOf(source)).find((b) => b.id.toLowerCase() === wanted);
  return block ? { start: block.start + 1, end: block.end } : null;
}

/** `source` with the lines outside `block` left empty, so the block keeps the note's line numbers (as `section`). */
export function blockSource(source: string, block: { start: number; end: number }): string {
  return source
    .split("\n")
    .map((l, i) => (i + 1 >= block.start && i + 1 <= block.end ? l : ""))
    .join("\n");
}
