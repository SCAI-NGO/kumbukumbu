// SPDX-License-Identifier: AGPL-3.0-or-later
// A note's tags as the server reads them (Kumbu.Core Tags/TagReader, TagMatch): the frontmatter's `tags`, and `#tags`
// in the text outside code, comments, wikilinks and HTML; matched ignoring case, a tag covering those nested below it.
import { frontmatterTexts } from "../links/aliases";
import { codeLines, codeSpans, foldCase, frontmatterEnd, splitLines, trim } from "../links/text";

/** Letters, digits, `_`, `-`, `/` and any other character (emoji too) except whitespace, ASCII punctuation and symbols,
 * and the general and supplemental punctuation blocks. */
function isTagChar(c: string): boolean {
  if (c === "_" || c === "-" || c === "/") return true;
  const code = c.charCodeAt(0);
  if (code <= 0x20 || (code >= 0x7f && code <= 0x9f) || trim(c).length === 0) return false;
  if (code < 0x80) return /[A-Za-z0-9]/.test(c);
  return !(code >= 0x2000 && code <= 0x206f) && !(code >= 0x2e00 && code <= 0x2e7f);
}

/** A whole tag without `#`: tag characters only, at least one of them not a digit, no trailing `/`. */
function isTag(text: string): boolean {
  if (text.length === 0 || text.endsWith("/") || !/[^0-9]/.test(text)) return false;
  for (let i = 0; i < text.length; i++) if (!isTagChar(text[i]!)) return false;
  return true;
}

/** Without a pattern, as trim does. */
function withoutTrailingSlashes(text: string): string {
  let end = text.length;
  while (end > 0 && text[end - 1] === "/") end--;
  return text.slice(0, end);
}

/** A tag as asked for, with or without `#` and a trailing `/`; null if it can't be one. */
export function normaliseTag(asked: string): string | null {
  const tag = withoutTrailingSlashes(trim(asked).replace(/^#/, "")).normalize("NFC");
  return isTag(tag) ? tag : null;
}

/** The tags of a note's text, each without `#`, NFC, in the order written: the frontmatter's first. */
export function tagsOf(content: string): string[] {
  const tags: string[] = [];
  for (const value of frontmatterTexts(content, "tags")) {
    // Older notes write "tags: a, b" or "tags: a b"; Obsidian splits those.
    for (const part of value.split(/[, \t]+/)) {
      const tag = withoutTrailingSlashes(part.replace(/^#+/, "")).normalize("NFC");
      if (isTag(tag)) tags.push(tag);
    }
  }
  const lines = splitLines(content);
  const start = frontmatterEnd(lines);
  const fenced = codeLines(lines);
  const indented = indentedCode(lines, start, fenced);
  let comment: string | null = null;
  for (let i = start; i < lines.length; i++) {
    if (fenced[i] || indented[i]) continue;
    const line = lines[i]!;
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
        [comment, p] = ["-->", p + 4];
        continue;
      }
      if (line.startsWith("%%", p)) {
        [comment, p] = ["%%", p + 2];
        continue;
      }
      if (line[p] !== "#" || (p > 0 && trim(line[p - 1]!).length > 0)) {
        p++;
        continue;
      }
      let stop = p + 1;
      while (stop < line.length && isTagChar(line[stop]!)) stop++;
      const tag = withoutTrailingSlashes(line.slice(p + 1, stop));
      if (isTag(tag)) tags.push(tag.normalize("NFC"));
      p = stop;
    }
  }
  return tags;
}

/** What a tag can't stand in on one line: inline code, wikilinks and HTML tags, as [start, end) by start. */
function skipped(line: string): Array<[number, number]> {
  const spans = line.includes("`") ? codeSpans(line) : [];
  for (const m of line.matchAll(/(!?)\[\[([^[\]\n]+?)\]\]/g)) spans.push([m.index, m.index + m[0].length]);
  for (const m of line.matchAll(/<\/?[A-Za-z][A-Za-z0-9-]*(\s[^<>]*)?\/?>/g)) spans.push([m.index, m.index + m[0].length]);
  return spans.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
}

/** Indented code blocks (four spaces or a tab) after a blank line, outside a list: there the indent is a nested item. */
function indentedCode(lines: readonly string[], start: number, fenced: readonly boolean[]): boolean[] {
  const code = lines.map(() => false);
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
    if (/^[ \t]*([-*+]|\d{1,9}[.)])([ \t]|$)/.test(line)) inList = true;
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

/** Whether a note's tags hold `asked` (normalised) or a tag nested below it, ignoring case. */
export function carries(tags: readonly string[], asked: string): boolean {
  const want = foldCase(asked);
  return tags.some((t) => {
    const tag = foldCase(t);
    return tag.startsWith(want) && (tag.length === want.length || tag[want.length] === "/");
  });
}
