// SPDX-License-Identifier: AGPL-3.0-or-later
import { isTagChar } from "../tags/tags";

/**
 * Stands in for a `$` that is no maths: remark-math pairs any two `$`, so `costs $5 and $10` would turn into maths.
 * A noncharacter (one UTF-16 unit, never in a note) keeps every offset and line where it was; `remarkRestoreDollars`
 * turns it back into `$` once the note is parsed.
 */
const placeholder = "\uFDD0";

// What stands before a line's content: blockquote markers, list markers, indentation.
const containerPrefix = /^(?:[ \t]*(?:>|[-+*](?=[ \t]|$)|\d{1,9}[.)](?=[ \t]|$)))*[ \t]*/;
const codeFence = /^(`{3,}|~{3,})/;
// A `$$` fence may carry text after it (micromark's meta), but no `$`: `$$x$$` is inline maths.
const mathFence = /^(\${2,})[^$]*$/;
const asciiPunctuation = /[!-/:-@[-`{-~]/;
// Sticky: each is tried where the scan stands.
const autolink = /<(?:[A-Za-z][A-Za-z0-9+.-]{1,31}:[^\s<>]*|[^\s<>@]+@[^\s<>]+)>/y;
const wikiLink = /!?\[\[[^[\]\n]+?\]\]/y;
const bareUrl = /(?:https?:\/\/|www\.)[^\s<]*/y;
const nonSpace = /\S*/y;
const backticks = /`+/y;
const dollars = /\$+/y;

function stickyMatch(re: RegExp, text: string, at: number): string | null {
  re.lastIndex = at;
  return re.exec(text)?.[0] ?? null;
}

interface Line {
  /** Offset of the line's content (after its container prefix) in the source. */
  start: number;
  content: string;
  quotes: number;
  listItem: boolean;
}

function linesOf(source: string): Line[] {
  const lines: Line[] = [];
  let offset = 0;
  for (const line of source.split("\n")) {
    const prefix = containerPrefix.exec(line)![0];
    lines.push({
      start: offset + prefix.length,
      content: line.slice(prefix.length).replace(/[ \t\r]+$/, ""),
      quotes: prefix.split(">").length - 1,
      listItem: /[-+*\d]/.test(prefix),
    });
    offset += line.length + 1;
  }
  return lines;
}

interface Run {
  at: number;
  length: number;
  /** In a wiki link or a tag: never maths. */
  inert?: boolean;
}

/**
 * The note's source as remark-math should read it, of the same length and with the same lines. A `$` that can't
 * pair by the rule Obsidian and Pandoc use (an opening `$` before a non-space, a closing one after a non-space and
 * not before a digit, both in one paragraph, runs of equal length) becomes the placeholder, as does a `$$` fence
 * line without its closing one and a `$` in a wiki link or a tag. `\[` and `\]` on lines of their own become `$$`.
 * Code, the bodies of `$$` blocks, link destinations and autolinks are left as they are; no pair runs over a link's
 * destination or a wiki link. `maths` says whether anything is left for KaTeX. A note that holds the placeholder
 * itself is left alone, without maths.
 */
export function prepareMath(source: string): { source: string; maths: boolean } {
  if (source.includes(placeholder) || (!source.includes("$") && !source.includes("\\["))) return { source, maths: false };
  const out = source.split("");
  const lines = linesOf(source);
  let maths = false;
  const write = (at: number, text: string) => {
    for (let i = 0; i < text.length; i++) out[at + i] = text[i];
  };

  /** Lines of paragraph text, each run of them one paragraph as far as pairing goes. */
  const segments: Line[][] = [];
  let segment: Line[] = [];
  const close = () => {
    if (segment.length) segments.push(segment);
    segment = [];
  };

  /**
   * The first line after `i` that closes its block, -1 if none does before the block's container ends (a line
   * with fewer `>`), or at the end of the note when the container does. A search that failed is remembered by
   * `key`: an unclosed fence on every line would otherwise search to the end from each.
   */
  const failed = new Map<string, number>();
  const closer = (i: number, key: string, test: (l: Line) => boolean): { at: number; end: number } => {
    const depth = lines[i].quotes;
    const known = failed.get(`${key}:${depth}`);
    if (known !== undefined && known > i) return { at: -1, end: known };
    let j = i + 1;
    for (; j < lines.length && lines[j].quotes >= depth; j++) if (lines[j].quotes === depth && test(lines[j])) return { at: j, end: j };
    failed.set(`${key}:${depth}`, j);
    return { at: -1, end: j };
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const fence = codeFence.exec(line.content);
    if (fence) {
      close();
      const mark = fence[1][0];
      const size = fence[1].length;
      const found = closer(i, `${mark}${size}`, (l) => l.content.length >= size && [...l.content].every((c) => c === mark));
      // Unclosed, the code runs to the end of its container.
      i = found.at >= 0 ? found.at : found.end - 1;
      continue;
    }
    if (line.content === "\\[") {
      const found = closer(i, "\\]", (l) => l.content === "\\]");
      if (found.at >= 0) {
        close();
        write(line.start, "$$");
        write(lines[found.at].start, "$$");
        maths = true;
        i = found.at;
        continue;
      }
    }
    const math = mathFence.exec(line.content);
    if (math) {
      close();
      const size = math[1].length;
      const found = closer(i, `$${size}`, (l) => /^\$+$/.test(l.content) && l.content.length >= size);
      if (found.at < 0) {
        write(line.start, placeholder.repeat(size));
      } else {
        maths = true;
        i = found.at;
      }
      continue;
    }
    if (!line.content || /^#{1,6}(?:[ \t]|$)/.test(line.content)) {
      close();
      if (line.content) segments.push([line]);
      continue;
    }
    if (line.listItem) close();
    segment.push(line);
  }
  close();

  for (const lines of segments) {
    // The paragraph's text without its container prefixes, and where each character came from.
    let text = "";
    const from: number[] = [];
    for (const line of lines) {
      if (text) {
        text += "\n";
        from.push(-1);
      }
      text += line.content;
      for (let i = 0; i < line.content.length; i++) from.push(line.start + i);
    }

    const runs: Run[] = [];
    // Where a link's destination or label, or a wiki link, starts: maths that ran over it would take it apart.
    const links: number[] = [];
    // Inert `$` runs in `text.slice(from, to)`.
    const inert = (start: number, end: number) => {
      for (let k = text.indexOf("$", start); k >= 0 && k < end; ) {
        const length = stickyMatch(dollars, text, k)!.length;
        runs.push({ at: k, length, inert: true });
        k = text.indexOf("$", k + length);
      }
    };
    // Where no closing run of a length was found from (a later search from further on finds none either), and
    // the next `)` and line break: each searched for once, however many places ask.
    const noCodeEnd = new Map<number, number>();
    let paren = -1;
    let newline = -1;
    for (let k = 0; k < text.length; ) {
      const c = text[k];
      const before = k === 0 ? " " : text[k - 1];
      let m: string | null;
      if (c === "\\" && asciiPunctuation.test(text[k + 1] ?? "")) {
        k += 2;
      } else if (c === "`") {
        const length = stickyMatch(backticks, text, k)!.length;
        let found = -1;
        if ((noCodeEnd.get(length) ?? Infinity) > k) {
          for (let e = text.indexOf("`", k + length); e >= 0; ) {
            const run = stickyMatch(backticks, text, e)!.length;
            if (run === length) {
              found = e;
              break;
            }
            e = text.indexOf("`", e + run);
          }
          if (found < 0) noCodeEnd.set(length, k);
        }
        k = found >= 0 ? found + length : k + length;
      } else if (c === "<" && (m = stickyMatch(autolink, text, k))) {
        k += m.length;
      } else if (c === "[" && (m = stickyMatch(wikiLink, text, k))) {
        links.push(k);
        inert(k, k + m.length);
        k += m.length;
      } else if (c === "]" && text[k + 1] === "(") {
        links.push(k);
        if (paren < k) paren = text.indexOf(")", k + 2) >>> 0;
        if (newline < k) newline = text.indexOf("\n", k) >>> 0;
        k = paren < newline && paren < text.length ? paren + 1 : k + 2;
      } else if (c === "]" && text[k + 1] === "[") {
        links.push(k);
        k += 2;
      } else if (c === "#" && /\s/.test(before) && isTagChar(text[k + 1] ?? " ")) {
        const length = stickyMatch(nonSpace, text, k)!.length;
        inert(k, k + length);
        k += length;
      } else if (/[\s(*_~]/.test(before) && (c === "h" || c === "w") && (m = stickyMatch(bareUrl, text, k))) {
        k += m.length;
      } else if (c === "$") {
        const length = stickyMatch(dollars, text, k)!.length;
        runs.push({ at: k, length });
        k += length;
      } else {
        k++;
      }
    }

    const opens = (r: Run) => !r.inert && /\S/.test(text[r.at + r.length] ?? " ");
    const closes = (r: Run) => !r.inert && /\S/.test(text[r.at - 1] ?? " ") && !/\d/.test(text[r.at + r.length] ?? "");
    const hide = (r: Run) => {
      for (let i = 0; i < r.length; i++) out[from[r.at + i]] = placeholder;
    };
    // The runs that could close, by length, each list with a cursor that only moves on: one pass, however long.
    const closers = new Map<number, { at: number[]; next: number }>();
    runs.forEach((r, n) => {
      if (!closes(r)) return;
      const list = closers.get(r.length) ?? { at: [], next: 0 };
      list.at.push(n);
      closers.set(r.length, list);
    });
    let link = 0;
    for (let i = 0; i < runs.length; ) {
      while (link < links.length && links[link] <= runs[i].at) link++;
      const until = link < links.length ? links[link] : text.length;
      let j = -1;
      const list = opens(runs[i]) ? closers.get(runs[i].length) : undefined;
      if (list) {
        while (list.next < list.at.length && list.at[list.next] <= i) list.next++;
        const n = list.at[list.next];
        if (n !== undefined && runs[n].at < until) j = n;
      }
      if (j < 0) {
        hide(runs[i]);
        i++;
        continue;
      }
      // Whatever lies between is the formula's: no `$` in it may end it early.
      for (let n = i + 1; n < j; n++) hide(runs[n]);
      maths = true;
      i = j + 1;
    }
  }

  return { source: out.join(""), maths };
}

interface AnyNode {
  [key: string]: unknown;
  children?: AnyNode[];
  data?: { hChildren?: AnyNode[] };
}

/**
 * The first transform after parsing a prepared source: every placeholder back to `$` (in text, code, link URLs,
 * maths), and the file's text back to the note's own, which the link and tag plugins slice by offset.
 */
export function remarkRestoreDollars({ original }: { original: string }) {
  return (tree: AnyNode, file: { value: unknown }) => {
    file.value = original;
    const restore = (node: AnyNode) => {
      for (const [key, value] of Object.entries(node)) {
        if (typeof value === "string" && value.includes(placeholder)) node[key] = value.replaceAll(placeholder, "$");
      }
      node.children?.forEach(restore);
      node.data?.hChildren?.forEach(restore);
    };
    restore(tree);
  };
}
