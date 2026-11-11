// SPDX-License-Identifier: AGPL-3.0-or-later

/**
 * Where a note's `%%comments%%` stand, read exactly as the server's fair copy reads them (`FairCopies.Comments` in
 * src/Kumbu.Core/Publishing/FairCopies.cs, with `TagReader.IndentedCode`): a `%%` opens and closes a comment anywhere
 * but in code, and what counts as code is the server's line by line reading, not Markdown's tree. So the view hides
 * what the copy leaves out. Both sides check themselves against comments.cases.json; a change here goes there too.
 */

// Past this many passes, comments go wherever they stand, code or not; past twice as many, everything from the first
// comment that opens goes.
const commentPasses = 8;

// What .NET counts as whitespace (char.IsWhiteSpace, and `\s` in its regexes): JavaScript's `\s` and trim() differ.
const ws = "\\t\\n\\v\\f\\r \\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000";
const white = new RegExp(`[${ws}]`);
function trimStart(s: string) {
  let i = 0;
  while (i < s.length && white.test(s[i])) i++;
  return s.slice(i);
}
function trimEnd(s: string) {
  let i = s.length;
  while (i > 0 && white.test(s[i - 1])) i--;
  return s.slice(0, i);
}
const blank = (s: string) => trimStart(s).length === 0;

// .NET's IgnoreCase on these ASCII names: also the Kelvin sign for k, nothing else outside ASCII.
const ignoringCase = (names: string) => names.replace(/[a-z]/g, (c) => (c === "k" ? "[kK\\u212A]" : `[${c}${c.toUpperCase()}]`));

const fenceRegex = /^ {0,3}(`{3,}|~{3,})([^\n]*)$/;
const htmlRawBlockRegex = new RegExp(`^ {0,3}<(${ignoringCase("script|pre|style|textarea")})(?=[${ws}>]|$)`);
const htmlBlockTagRegex = new RegExp(
  `^ {0,3}</?(${ignoringCase(
    "address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul",
  )})(?=[${ws}/>]|$)`,
);
const S = `[${ws}]`;
const htmlLoneTagRegex = new RegExp(
  `^ {0,3}(?:<[A-Za-z][A-Za-z0-9-]*(?:${S}+[A-Za-z_:][A-Za-z0-9_.:-]*(?:${S}*=${S}*(?:[^${ws}"'=<>\`]+|'[^']*'|"[^"]*"))?)*${S}*/?>|</[A-Za-z][A-Za-z0-9-]*${S}*>)${S}*$`,
);
const containerRegex = /^(?:[ \t]*(?:>[ \t]?|(?:[-*+]|[0-9]{1,9}[.)])[ \t]+))*/;
const listItemRegex = /^[ \t>]*(?:[-*+]|[0-9]{1,9}[.)])(?:[ \t]|$)/;
// TagReader's: `\d` is any decimal digit in .NET.
const indentListItemRegex = /^[ \t]*([-*+]|\p{Nd}{1,9}[.)])([ \t]|$)/u;
const headingLineRegex = /^[ \t>]*#{1,6}(?:[ \t]|$)/;
const thematicRegex = /^[ \t>]*([-*_])(?:[ \t]*\1){2,}[ \t]*$/;
const markersRegex = /^(?:[ \t>]|(?:[-*+]|[0-9]{1,9}[.)]|#{1,6})(?=[ \t]|$)|\[[ xX]\](?=[ \t]|$))*$/;

// OrdinalIgnoreCase, for what ends an HTML block: those ends are ASCII.
const asciiLower = (s: string) => s.replace(/[A-Z]+/g, (c) => c.toLowerCase());
const containsIgnoringCase = (s: string, what: string) => asciiLower(s).includes(asciiLower(what));

function splitLines(text: string): string[] {
  if (text.length === 0) return [];
  return (text.endsWith("\n") ? text.slice(0, -1) : text).split("\n");
}

function frontmatterEnd(lines: string[]): number {
  if (lines.length === 0 || trimEnd(lines[0]) !== "---") return 0;
  for (let i = 1; i < lines.length; i++) {
    const t = trimEnd(lines[i]);
    if (t === "---" || t === "...") return i + 1;
  }
  return 0;
}

/** What ends the raw HTML block `line` starts, or null if it starts none. The seventh kind can't interrupt a paragraph. */
function htmlBlockEnd(line: string, afterBlank: boolean): string | null {
  const t = trimStart(line);
  if (!t.startsWith("<") || line.length - t.length > 3) return null;
  const raw = htmlRawBlockRegex.exec(line);
  if (raw) return `</${raw[1]}>`;
  if (t.startsWith("<!--")) return "-->";
  if (t.startsWith("<?")) return "?>";
  if (t.startsWith("<![CDATA[")) return "]]>";
  if (t.length > 2 && t[1] === "!" && /[A-Za-z]/.test(t[2])) return ">";
  if (htmlBlockTagRegex.test(line)) return "";
  return afterBlank && htmlLoneTagRegex.test(line) ? "" : null;
}

/** Top-level fenced code, the lines of raw HTML blocks, and the line of a fence never closed (or -1). */
function fencesAndHtml(lines: string[]): { fenced: boolean[]; html: boolean[]; unclosed: number } {
  const fenced = new Array<boolean>(lines.length).fill(false);
  const html = new Array<boolean>(lines.length).fill(false);
  let fence: string | null = null;
  let opened = -1;
  let htmlEnd: string | null = null;
  let afterBlank = true;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (fence !== null) {
      fenced[i] = true;
      const c = fenceRegex.exec(line);
      if (c && c[1][0] === fence[0] && c[1].length >= fence.length && blank(c[2])) {
        fence = null;
        afterBlank = true;
      }
      continue;
    }
    if (htmlEnd !== null) {
      if (htmlEnd.length === 0 && blank(line)) {
        htmlEnd = null;
        afterBlank = true;
        continue;
      }
      html[i] = true;
      if (htmlEnd.length > 0 && containsIgnoringCase(line, htmlEnd)) htmlEnd = null;
      continue;
    }
    const f = line.length > 0 && (line[0] === "`" || line[0] === "~") ? fenceRegex.exec(line) : null;
    if (f && !(f[1][0] === "`" && f[2].includes("`"))) {
      fence = f[1];
      opened = i;
      fenced[i] = true;
      continue;
    }
    const end = htmlBlockEnd(line, afterBlank);
    if (end !== null) {
      html[i] = true;
      const rest = trimStart(line).slice(1);
      htmlEnd = end.length > 0 && containsIgnoringCase(rest, end) ? null : end;
      afterBlank = false;
      continue;
    }
    afterBlank = blank(line);
  }
  if (fence === null) return { fenced, html, unclosed: -1 };
  fenced.fill(false, opened);
  return { fenced, html, unclosed: opened };
}

function indent(line: string): number {
  let columns = 0;
  for (const c of line) {
    if (c === " ") columns++;
    else if (c === "\t") columns += 4 - (columns % 4);
    else break;
  }
  return columns;
}

/** Indented code after a blank line, outside a list (TagReader.IndentedCode). */
function indentedCode(lines: string[], fenced: boolean[]): boolean[] {
  const code = new Array<boolean>(lines.length).fill(false);
  let afterBlank = true;
  let inList = false;
  let inCode = false;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (fenced[i]) {
      afterBlank = true;
      inCode = inList = false;
      continue;
    }
    if (blank(line)) {
      afterBlank = true;
      continue;
    }
    const columns = indent(line);
    if (columns >= 4 && (inCode || (afterBlank && !inList))) {
      code[i] = inCode = true;
      continue;
    }
    inCode = false;
    if (indentListItemRegex.test(line)) inList = true;
    else if (columns === 0 && afterBlank) inList = false;
    afterBlank = false;
  }
  return code;
}

function codeLines(lines: string[]): { code: boolean[]; unclosed: number } {
  const { fenced, html, unclosed } = fencesAndHtml(lines);
  const indented = indentedCode(lines, fenced);
  return { code: fenced.map((f, i) => f || (indented[i] && !html[i])), unclosed };
}

const fenceShaped = (line: string) => fenceRegex.test(trimStart(line.replace(containerRegex, "")));

function quoteDepth(line: string): number {
  let depth = 0;
  for (const c of line) {
    if (c === ">") depth++;
    else if (c !== " " && c !== "\t") break;
  }
  return depth;
}

/** Whether `line` starts a block of its own after `previous`. */
const breaks = (previous: string, line: string) =>
  listItemRegex.test(line) ||
  headingLineRegex.test(line) ||
  headingLineRegex.test(previous) ||
  thematicRegex.test(line) ||
  thematicRegex.test(previous) ||
  fenceShaped(line) ||
  fenceShaped(previous) ||
  quoteDepth(previous) !== quoteDepth(line);

/** Nothing but whitespace, quote, list and heading markers, a task box. */
const empty = (line: string) => blank(line) || markersRegex.test(line);

/** Code spans, start to end: a run of backticks up to the next run of the same length; not in a table row's cell. */
function codeSpans(s: string): Map<number, number> {
  const spans = new Map<number, number>();
  if (!s.includes("`")) return spans;
  const runs: { start: number; length: number; escaped: boolean }[] = [];
  for (let i = 0; i < s.length; ) {
    if (s[i] !== "`") {
      i++;
      continue;
    }
    let j = i;
    while (j < s.length && s[j] === "`") j++;
    let backslashes = 0;
    while (i - backslashes - 1 >= 0 && s[i - backslashes - 1] === "\\") backslashes++;
    runs.push({ start: i, length: j - i, escaped: backslashes % 2 === 1 });
    i = j;
  }
  const byLength = new Map<number, number[]>();
  runs.forEach((r, k) => {
    const list = byLength.get(r.length);
    if (list) list.push(k);
    else byLength.set(r.length, [k]);
  });
  for (let k = 0; k < runs.length; ) {
    let { start, length } = runs[k];
    if (runs[k].escaped) {
      start++;
      length--;
    }
    let closer = -1;
    const candidates = length > 0 ? byLength.get(length) : undefined;
    if (candidates) {
      // The first candidate after k.
      let lo = 0;
      let hi = candidates.length;
      while (lo < hi) {
        const mid = (lo + hi) >> 1;
        if (candidates[mid] < k + 1) lo = mid + 1;
        else hi = mid;
      }
      if (lo < candidates.length) closer = candidates[lo];
    }
    if (closer < 0) {
      k++;
      continue;
    }
    const end = runs[closer].start + runs[closer].length;
    let pipe = false;
    for (let q = start; q < end && !pipe; q++) pipe = s[q] === "|";
    if (!pipe) spans.set(start, end);
    k = closer + 1;
  }
  return spans;
}

/** One pass's reading of comments, block by block, a comment open from one block into the next. */
class CommentReader {
  /** What closes the comment open here, `%%` or `-->`; null outside one. */
  closer: string | null = null;
  text = "";
  at: Int32Array = new Int32Array(0);
  private opened = 0;
  private readonly found: [number, number][];
  // In the last pass nothing closes a comment.
  private readonly last: boolean;

  constructor(found: [number, number][], last: boolean) {
    this.found = found;
    this.last = last;
  }

  strip(s: string, at: Int32Array, offset: number, anywhere: boolean) {
    if (this.closer === null && !s.includes("%%") && !s.includes("<!--")) {
      this.text = s;
      this.at = at.subarray(offset, offset + s.length + 1);
      return;
    }
    const spans = anywhere ? new Map<number, number>() : codeSpans(s);
    const out: string[] = [];
    const places = new Int32Array(s.length + 1);
    let n = 0;
    let p = 0;
    while (p < s.length) {
      if (this.closer !== null) {
        if (this.last) break;
        // A code span inside a %% comment goes with it, whatever it holds.
        const skip = this.closer === "%%" ? spans.get(p) : undefined;
        if (skip !== undefined) {
          p = skip;
          continue;
        }
        if (!s.startsWith(this.closer, p)) {
          p++;
          continue;
        }
        p += this.closer.length;
        if (this.closer === "%%") this.found.push([this.opened, at[offset + p - 1] + 1]);
        this.closer = null;
        continue;
      }
      const end = spans.get(p);
      if (end !== undefined) {
        out.push(s.slice(p, end));
        for (let q = p; q < end; q++) places[n++] = at[offset + q];
        p = end;
      } else if (s.startsWith("<!--", p)) {
        this.closer = "-->";
        p += 4;
      } else if (s.startsWith("%%", p)) {
        this.closer = "%%";
        this.opened = at[offset + p];
        p += 2;
      } else {
        out.push(s[p]);
        places[n++] = at[offset + p++];
      }
    }
    places[n++] = at[offset + s.length];
    this.text = out.join("");
    this.at = places.subarray(0, n);
  }

  /** A %% comment still open at the end runs to the note's end. */
  finish(length: number) {
    if (this.closer === "%%") this.found.push([this.opened, length]);
  }
}

function merged(found: [number, number][]): [number, number][] {
  found.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  const out: [number, number][] = [];
  for (const [start, end] of found) {
    const last = out[out.length - 1];
    if (last && start <= last[1]) last[1] = Math.max(end, last[1]);
    else out.push([start, end]);
  }
  return out;
}

/**
 * The frontmatter as the server reads it (Outline.FrontmatterEnd, after taking `\r\n` and `\r` as line ends): its lines
 * between the two `---`, and where the text after it starts in `content`; null without one.
 */
export function frontmatterOf(content: string): { lines: string[]; end: number } | null {
  const lines: string[] = [];
  for (let start = 0; start < content.length; ) {
    let stop = start;
    while (stop < content.length && content[stop] !== "\n" && content[stop] !== "\r") stop++;
    const line = content.slice(start, stop);
    const next = content[stop] === "\r" && content[stop + 1] === "\n" ? stop + 2 : stop + 1;
    if (lines.length === 0 && trimEnd(line) !== "---") return null;
    const t = trimEnd(line);
    if (lines.length > 0 && (t === "---" || t === "...")) return { lines: lines.slice(1), end: Math.min(next, content.length) };
    lines.push(line);
    start = next;
  }
  return null;
}

const sameLines = (a: string[], b: string[]) => a.length === b.length && a.every((l, i) => l === b[i]);

/**
 * The note's `%%comments%%`, start and end as UTF-16 offsets into `content`, in order and apart. `\r\n` and `\r` read
 * as line ends; the frontmatter holds none.
 */
export function commentRanges(content: string): [number, number][] {
  // Only taking out an HTML comment or a %% one makes a new %%.
  if (!content.includes("%%") && !content.includes("<!--")) return [];
  // at[k] is where the k-th character of the text, its line ends read as "\n", stands in content.
  let at = new Int32Array(content.length + 1);
  let text = content;
  if (content.includes("\r")) {
    const chars: string[] = [];
    for (let i = 0; i < content.length; i++) {
      if (content[i] === "\r" && content[i + 1] === "\n") continue;
      at[chars.length] = i;
      chars.push(content[i] === "\r" ? "\n" : content[i]);
    }
    at[chars.length] = content.length;
    text = chars.join("");
  } else {
    for (let i = 0; i <= content.length; i++) at[i] = i;
  }
  const all = splitLines(text);
  const skip = frontmatterEnd(all);
  let start = 0;
  for (let i = 0; i < skip; i++) start += all[i].length + 1;
  let lines = all.slice(skip);
  at = at.subarray(start);

  const found: [number, number][] = [];
  for (let pass = 0; ; pass++) {
    const anywhere = pass >= commentPasses;
    const { code, unclosed } = anywhere ? { code: new Array<boolean>(lines.length).fill(false), unclosed: -1 } : codeLines(lines);
    const reader = new CommentReader(found, pass === 2 * commentPasses);
    const result: string[] = [];
    const places = new Int32Array(at.length);
    let n = 0;
    const starts = new Int32Array(lines.length + 1);
    for (let i = 0; i < lines.length; i++) starts[i + 1] = starts[i] + lines[i].length + 1;
    const keep = (line: string, from: Int32Array, offset: number) => {
      result.push(line);
      places.set(from.subarray(offset, offset + line.length + 1), n);
      n += line.length + 1;
    };

    for (let i = 0; i < lines.length; ) {
      const open = reader.closer !== null;
      // To Markdown a fence never closed is code to the end of the note, which an open %% comment takes.
      if (reader.closer === "%%" && i === unclosed) break;
      if (!open && code[i]) {
        keep(lines[i], at, starts[i]);
        i++;
        continue;
      }
      if (blank(lines[i]) || (reader.closer === "%%" && code[i])) {
        if (!open) keep(lines[i], at, starts[i]);
        i++;
        continue;
      }
      // Code spans run over the lines of a paragraph.
      let end = i + 1;
      while (end < lines.length && !code[end] && !blank(lines[end]) && !breaks(lines[end - 1], lines[end])) end++;
      const block = lines.slice(i, end);
      reader.strip(block.join("\n"), at, starts[i], anywhere);
      const parts = reader.text.split("\n");
      const aligned = parts.length === block.length;
      let from = 0;
      for (let p = 0; p < parts.length; p++) {
        if (!empty(parts[p]) || (aligned && parts[p] === block[p])) keep(parts[p], reader.at, from);
        from += parts[p].length + 1;
      }
      i = end;
    }

    reader.finish(content.length);
    if (pass === 2 * commentPasses || sameLines(result, lines)) return merged(found);
    lines = result;
    at = places.subarray(0, n);
  }
}
