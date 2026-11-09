// SPDX-License-Identifier: AGPL-3.0-or-later

/**
 * Stands in for a `$` that is no maths: remark-math pairs any two `$`, so `costs $5 and $10` would turn into maths.
 * A noncharacter (one UTF-16 unit, never in a note) keeps every offset and line where it was; `remarkRestoreDollars`
 * turns it back into `$` once the note is parsed.
 */
const placeholder = "﷐";

// What stands before a line's content: blockquote markers, list markers, indentation.
const containerPrefix = /^(?:[ \t]*(?:>|[-+*](?=[ \t]|$)|\d{1,9}[.)](?=[ \t]|$)))*[ \t]*/;
const codeFence = /^(`{3,}|~{3,})/;
// A `$$` fence may carry text after it (micromark's meta), but no `$`: `$$x$$` is inline maths.
const mathFence = /^(\${2,})[^$]*$/;
const asciiPunctuation = /[!-/:-@[-`{-~]/;
const autolink = /^<(?:[A-Za-z][A-Za-z0-9+.-]{1,31}:[^\s<>]*|[^\s<>@]+@[^\s<>]+)>/;
const bareUrl = /^(?:https?:\/\/|www\.)[^\s<]*/;

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

/**
 * The note's source as remark-math should read it, of the same length and with the same lines. A `$` that can't
 * pair by the rule Obsidian and Pandoc use (an opening `$` before a non-space, a closing one after a non-space and
 * not before a digit, both in one paragraph, runs of equal length) becomes the placeholder, as does a `$$` fence
 * line without its closing one. `\[` and `\]` on lines of their own become `$$`. Code, the bodies of `$$` blocks,
 * link destinations and autolinks are left as they are. `maths` says whether anything is left for KaTeX.
 */
export function prepareMath(source: string): { source: string; maths: boolean } {
  if (source.includes(placeholder)) return { source, maths: source.includes("$") };
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

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const fence = codeFence.exec(line.content);
    if (fence) {
      close();
      const closing = new RegExp(`^${fence[1][0] === "`" ? "`" : "~"}{${fence[1].length},}$`);
      let j = i + 1;
      while (j < lines.length && !closing.test(lines[j].content)) j++;
      i = j;
      continue;
    }
    const closer = (test: (l: Line) => boolean) => {
      for (let j = i + 1; j < lines.length; j++) if (lines[j].quotes === line.quotes && test(lines[j])) return j;
      return -1;
    };
    if (line.content === "\\[") {
      const j = closer((l) => l.content === "\\]");
      if (j >= 0) {
        close();
        write(line.start, "$$");
        write(lines[j].start, "$$");
        maths = true;
        i = j;
        continue;
      }
    }
    const math = mathFence.exec(line.content);
    if (math) {
      close();
      const size = math[1].length;
      const j = closer((l) => /^\$+$/.test(l.content) && l.content.length >= size);
      if (j < 0) {
        write(line.start, placeholder.repeat(size));
      } else {
        maths = true;
        i = j;
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

    const runs: { at: number; length: number }[] = [];
    // Where a link's destination starts: maths that ran over it would take the link apart.
    const links: number[] = [];
    for (let k = 0; k < text.length; ) {
      const c = text[k];
      if (c === "\\" && asciiPunctuation.test(text[k + 1] ?? "")) {
        k += 2;
      } else if (c === "`") {
        const length = /^`+/.exec(text.slice(k))![0].length;
        const end = new RegExp(`(?<!\`)\`{${length}}(?!\`)`, "g");
        end.lastIndex = k + length;
        const found = end.exec(text);
        k = found ? found.index + length : k + length;
      } else if (c === "<" && autolink.test(text.slice(k))) {
        k += autolink.exec(text.slice(k))![0].length;
      } else if (c === "]" && text[k + 1] === "(") {
        links.push(k);
        const end = text.indexOf(")", k + 2);
        k = end >= 0 && !text.slice(k, end).includes("\n") ? end + 1 : k + 2;
      } else if ((k === 0 || /[\s(*_~]/.test(text[k - 1])) && bareUrl.test(text.slice(k))) {
        k += bareUrl.exec(text.slice(k))![0].length;
      } else if (c === "$") {
        const length = /^\$+/.exec(text.slice(k))![0].length;
        runs.push({ at: k, length });
        k += length;
      } else {
        k++;
      }
    }

    const opens = (r: { at: number; length: number }) => /\S/.test(text[r.at + r.length] ?? " ");
    const closes = (r: { at: number; length: number }) => /\S/.test(text[r.at - 1] ?? " ") && !/\d/.test(text[r.at + r.length] ?? "");
    const hide = (r: { at: number; length: number }) => {
      for (let i = 0; i < r.length; i++) out[from[r.at + i]] = placeholder;
    };
    for (let i = 0; i < runs.length; ) {
      const until = links.find((at) => at > runs[i].at) ?? text.length;
      const j = opens(runs[i]) ? runs.findIndex((r, n) => n > i && r.at < until && r.length === runs[i].length && closes(r)) : -1;
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
