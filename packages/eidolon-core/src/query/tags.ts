// SPDX-License-Identifier: AGPL-3.0-or-later
// A note's tags as Obsidian reads them: the frontmatter's `tags` property, and #tags in the text outside code, comments
// and HTML (Kumbu.Core Tags/TagReader).
import { MAX_FRONTMATTER_LENGTH } from "../links/aliases";
import { codeLines, codeSpans, frontmatterEnd, isWhiteSpace, splitLines, trim } from "../links/text";
import { Properties } from "./properties";
import { PropertyTypes } from "./propertyTypes";

/** A tag of a note: its text without '#' as written (NFC), and the 0-based line it stands on (for a frontmatter tag,
 * the line of its list item or of the `tags` key). */
export type TagUse = { readonly tag: string; readonly line: number };

export function tagsOf(content: string): TagUse[] {
  const lines = splitLines(content);
  const end = frontmatterEnd(lines);
  const result = end > 0 ? fromFrontmatter(content, lines, end) : [];
  fromText(lines, end, result);
  return result;
}

const isAsciiDigit = (code: number) => code >= 0x30 && code <= 0x39;

/** Whether `text` (without '#') is a whole tag: tag characters only, at least one of them not a digit. */
export function isTag(text: string): boolean {
  if (text.length === 0 || text.endsWith("/")) return false;
  let letter = false;
  for (let i = 0; i < text.length; i++) {
    if (!isTagChar(text[i]!)) return false;
    if (!isAsciiDigit(text.charCodeAt(i))) letter = true;
  }
  return letter;
}

/** Obsidian's tag characters: letters, digits, '_', '-', '/', and any other Unicode character (emoji among them)
 * except whitespace, ASCII punctuation and the general and supplemental punctuation blocks. */
export function isTagChar(c: string): boolean {
  if (c === "_" || c === "-" || c === "/") return true;
  const code = c.charCodeAt(0);
  // Every printable ASCII character that is no letter, digit or space is punctuation or a symbol.
  const asciiMark = code > 0x20 && code < 0x7f && !/[0-9A-Za-z]/.test(c);
  const control = code < 0x20 || (code >= 0x7f && code <= 0x9f);
  return !isWhiteSpace(c) && !control && !asciiMark && !(code >= 0x2000 && code <= 0x206f) && !(code >= 0x2e00 && code <= 0x2e7f);
}

function fromFrontmatter(content: string, lines: readonly string[], end: number): TagUse[] {
  // Most frontmatter has no tags: no YAML parse for it.
  let key = -1;
  for (let i = 1; i < end - 1; i++) {
    if (TAGS_KEY.test(lines[i]!)) {
      key = i;
      break;
    }
  }
  // Frontmatter longer than aliases are read from has no tags either.
  if (key < 0 || lines.slice(0, end).reduce((sum, l) => sum + l.length + 1, 0) > MAX_FRONTMATTER_LENGTH) return [];
  // tags is a list of text in every vault, so a single value reads as a list of one and a number or date stays as written.
  const read = Properties.read(content, PropertyTypes.none);
  if (read.problem !== null) return [];
  const tags = read.values.get("tags");
  const values = tags?.kind === "list" ? tags.items.flatMap((v) => (v.kind === "text" ? [v.text] : [])) : [];

  const result: TagUse[] = [];
  let next = key + 1;
  for (const value of values) {
    // A block list puts each value on its own line; find it there, else the key's line stands for it.
    let line = key;
    for (let i = next; i < end - 1 && /^[ \t-]/.test(lines[i]!); i++) {
      if (!lines[i]!.includes(value)) continue;
      line = i;
      next = i + 1;
      break;
    }

    // Older notes write "tags: a, b" or "tags: a b"; Obsidian splits those.
    for (const part of value.split(/[, \t]/)) {
      const tag = part.replace(/^#+/, "").replace(/\/+$/, "").normalize("NFC");
      if (part.length > 0 && isTag(tag)) result.push({ tag, line });
    }
  }
  return result;
}

function fromText(lines: readonly string[], start: number, result: TagUse[]): void {
  const fenced = codeLines(lines);
  const indented = indentedCode(lines, start, fenced);
  let comment: string | null = null;
  for (let i = start; i < lines.length; i++) {
    if (fenced[i] || indented[i]) continue;
    const line = lines[i]!;
    // Most lines hold no '#' and open no comment: nothing to look at.
    if (comment === null && !/[#%<]/.test(line)) continue;

    const skip = skipped(line);
    let s = 0;
    let p = 0;
    while (p < line.length) {
      if (comment !== null) {
        const close = line.indexOf(comment, p);
        if (close < 0) break;
        p = close + comment.length;
        comment = null;
        continue;
      }

      const next = line.slice(p).search(/[#%<]/);
      if (next < 0) break;
      p += next;
      while (s < skip.length && skip[s]![1] <= p) s++;
      if (s < skip.length && skip[s]![0] <= p) {
        p = skip[s]![1];
        continue;
      }

      if (line.startsWith("<!--", p)) {
        comment = "-->";
        p += 4;
        continue;
      }

      if (line.startsWith("%%", p)) {
        comment = "%%";
        p += 2;
        continue;
      }

      if (line[p] !== "#" || (p > 0 && !isWhiteSpace(line[p - 1]!))) {
        p++;
        continue;
      }

      let stop = p + 1;
      while (stop < line.length && isTagChar(line[stop]!)) stop++;
      const tag = line.slice(p + 1, stop).replace(/\/+$/, "");
      if (isTag(tag)) result.push({ tag: tag.normalize("NFC"), line: i });
      p = stop;
    }
  }
}

/** What a tag can't stand in on one line: inline code, wikilinks and HTML tags, as [start, end] by start. */
function skipped(line: string): Array<[number, number]> {
  const spans = line.includes("`") ? codeSpans(line) : [];
  if (line.includes("[[")) for (const m of line.matchAll(WIKI_LINK)) spans.push([m.index, m.index + m[0].length]);
  if (line.includes("<")) for (const m of line.matchAll(HTML_TAG)) spans.push([m.index, m.index + m[0].length]);
  return spans.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
}

/** Indented code blocks (four spaces or a tab) after a blank line, outside a list: inside a list the same indent is a
 * nested item or its text, which is where Obsidian users write most of their nested tags. */
function indentedCode(lines: readonly string[], start: number, fenced: readonly boolean[]): boolean[] {
  const code = new Array<boolean>(lines.length).fill(false);
  let blank = true;
  let inList = false;
  let inCode = false;
  for (let i = start; i < lines.length; i++) {
    const line = lines[i]!;
    if (fenced[i]) {
      blank = true;
      inCode = inList = false;
      continue;
    }

    if (trim(line).length === 0) {
      blank = true;
      continue;
    }

    const indent = indentOf(line);
    if (indent >= 4 && (inCode || (blank && !inList))) {
      code[i] = inCode = true;
      continue;
    }

    inCode = false;
    if (LIST_ITEM.test(line)) inList = true;
    else if (indent === 0 && blank) inList = false;
    blank = false;
  }
  return code;
}

function indentOf(line: string): number {
  let columns = 0;
  for (const c of line) {
    if (c === " ") columns++;
    else if (c === "\t") columns += 4 - (columns % 4);
    else break;
  }
  return columns;
}

const TAGS_KEY = /^(tags|"tags"|'tags')[ \t]*:/;
const LIST_ITEM = /^[ \t]*([-*+]|\d{1,9}[.)])([ \t]|$)/;
const WIKI_LINK = /(!?)\[\[([^[\]\n]+?)\]\]/g;
const HTML_TAG = /<\/?[A-Za-z][A-Za-z0-9-]*(\s[^<>]*)?\/?>/g;
