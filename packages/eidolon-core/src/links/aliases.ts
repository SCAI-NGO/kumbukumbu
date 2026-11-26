// SPDX-License-Identifier: AGPL-3.0-or-later
// The other names a note goes by (Kumbu.Core Links/NoteAliases): the top-level `aliases` of its front matter, a list or
// a single text. The server reads the whole front matter with a YAML parser; the core has none, so this reads the YAML a
// key's value can be written in (plain, quoted and block scalars, flow and block sequences) and gives no aliases for
// anything it can't read, as the server does for front matter that doesn't parse.
import { foldCase, frontmatterEnd, splitLines, trim } from "./text";

/** Aliases a note can have; the rest are ignored, so a note listing thousands can't stall every link view. */
export const MAX_PER_NOTE = 100;

/** Front matter longer than this (in characters) is not read for aliases. */
export const MAX_FRONTMATTER_LENGTH = 64 * 1024;

/** The longest alias (in characters) a note can have, as a file name has a limit; a longer one is ignored. */
export const MAX_LENGTH = 200;

const FORBIDDEN = /[/#|[\]^\n\r]/;

/** A note's aliases, each trimmed and NFC; empty values and those that can't be a link's name are skipped,
 * duplicates (ignoring case) count once. */
export function aliasesOf(content: string): string[] {
  const texts = frontmatterTexts(content, "aliases");
  const seen = new Set<string>();
  const aliases: string[] = [];
  for (const text of texts) {
    const alias = trim(text).normalize("NFC");
    if (alias.length === 0 || alias.length > MAX_LENGTH || FORBIDDEN.test(alias)) continue;
    const key = foldCase(alias);
    if (seen.has(key)) continue;
    seen.add(key);
    aliases.push(alias);
    if (aliases.length === MAX_PER_NOTE) break;
  }
  return aliases;
}

/**
 * The texts of a top-level key of the front matter, as aliases and tags are read: a list's texts, or a single text.
 * None for front matter longer than MAX_FRONTMATTER_LENGTH or that this can't read.
 */
export function frontmatterTexts(content: string, key: string): string[] {
  if (!content.startsWith("---")) return [];
  const lines = splitLines(content);
  const end = frontmatterEnd(lines, MAX_FRONTMATTER_LENGTH);
  // Most notes don't have the key; skip reading their front matter.
  if (end === 0 || !lines.slice(0, end).some((l) => l.includes(key))) return [];

  let value: Yaml;
  try {
    value = topLevel(lines.slice(1, end - 1), key);
  } catch (e) {
    if (e instanceof Unreadable) return [];
    throw e;
  }
  return typeof value === "string" ? [value] : Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : [];
}

/** A YAML value as far as aliases need it: a text, null, a list, or a map (whose content no alias is in). */
type Yaml = string | null | Yaml[] | { map: true };

const MAP: Yaml = { map: true };

/** YAML this reader can't read, or that doesn't parse. */
class Unreadable extends Error {}

function fail(): never {
  throw new Unreadable();
}

const QUOTED_KEY = /^(?:"((?:[^"\\]|\\.)*)"|'((?:[^']|'')*)')/;
const PLAIN_KEY = /^(?:[^\s#'"[\]{},&*!|>%@`-]|-\S)/;

interface Key { double?: string; single?: string; plain?: string; value?: string }

const isTab = (c: string | undefined) => c === " " || c === "\t";

/**
 * `key: "quoted"`, `'quoted': x`, `plain key:`, and what follows the colon, as the pattern
 * `^(?:"((?:[^"\\]|\\.)*)"|'((?:[^']|'')*)'|([^\s#'"[\]{},&*!|>%@`-][^\n]*?|-[^\s][^\n]*?))[ \t]*:(?:[ \t]+(.*))?$` reads
 * a line, without the pattern: a plain key ends before the first colon that ends the line or has a space or tab after it.
 */
function keyOf(line: string): Key | null {
  // The value is read as "." reads it, which takes no line break.
  const lastBreak = Math.max(line.lastIndexOf("\r"), line.lastIndexOf("\u2028"), line.lastIndexOf("\u2029"));
  // What follows the colon at `colon`: undefined for nothing, null when the line doesn't go on as a key's line does.
  const after = (colon: number): string | undefined | null => {
    if (colon + 1 === line.length) return undefined;
    if (!isTab(line[colon + 1]) || lastBreak > colon) return null;
    let start = colon + 1;
    while (isTab(line[start])) start++;
    return line.slice(start);
  };
  const quoted = QUOTED_KEY.exec(line);
  if (quoted !== null) {
    let colon = quoted[0].length;
    while (isTab(line[colon])) colon++;
    const value = line[colon] === ":" ? after(colon) : null;
    return value === null ? null : { double: quoted[1], single: quoted[2], value };
  }
  const first = PLAIN_KEY.exec(line);
  if (first === null) return null;
  for (let colon = line.indexOf(":", first[0].length); colon >= 0; colon = line.indexOf(":", colon + 1)) {
    const value = after(colon);
    if (value === null) continue;
    let end = colon;
    while (end > first[0].length && isTab(line[end - 1])) end--;
    return { plain: line.slice(0, end), value };
  }
  return null;
}

/** How many flow brackets a line opens and doesn't close, quotes and comments aside. */
function depthOf(line: string): number {
  let depth = 0;
  for (const c of stripComment(line).replace(/"(?:[^"\\]|\\.)*"|'(?:[^']|'')*'/g, "")) depth += c === "[" || c === "{" ? 1 : c === "]" || c === "}" ? -1 : 0;
  return depth;
}

function isBlank(line: string): boolean {
  const t = line.trim();
  return t.length === 0 || t.startsWith("#");
}

function indentOf(line: string): number {
  return line.length - line.trimStart().length;
}

function isItem(text: string): boolean {
  return text === "-" || text.startsWith("- ") || text.startsWith("-\t");
}

/** The value of `name` among the top-level keys of a front matter's lines; null if it has none. Two keys of one
 * name make the front matter unreadable, as the server reads it with duplicate keys checked. */
function topLevel(lines: readonly string[], name: string): Yaml {
  const keys = new Set<string>();
  let found: Yaml = null;
  for (let i = 0; i < lines.length;) {
    const line = lines[i]!;
    if (isBlank(line)) {
      i++;
      continue;
    }
    const m = keyOf(line);
    if (m === null || indentOf(line) > 0) fail();
    const key = m.double !== undefined ? unquoteDouble(m.double) : m.single !== undefined ? m.single.replace(/''/g, "'") : m.plain!;
    if (keys.has(key)) fail();
    keys.add(key);
    // The key's lines: those indented under it, its block sequence's items, and the blank lines between them.
    // A flow collection may close at the start of a line: "]".
    let open = /^[[{]/.test(m.value ?? "") ? depthOf(m.value!) : 0;
    let last = i;
    for (let j = i + 1; j < lines.length; j++) {
      const next = lines[j]!;
      if (isBlank(next)) continue;
      if (open <= 0 && indentOf(next) === 0 && !isItem(next)) break;
      if (open > 0) open += depthOf(next);
      last = j;
    }
    if (key === name) found = value(m.value ?? "", lines.slice(i + 1, last + 1));
    i = last + 1;
  }
  return found;
}

/** A key's value from what follows its colon on its own line, `inline`, and the lines below that belong to it. */
function value(inline: string, below: readonly string[]): Yaml {
  const rest = stripComment(inline).trim();
  const body = below.filter((l) => !isBlank(l));
  if (rest.length === 0) {
    if (body.length === 0) return null;
    const first = body[0]!.trimStart();
    if (isItem(first)) return blockSequence(body);
    if (keyOf(first) !== null) return MAP;
    return scalar(body.map((l) => l.trim()).join(" "));
  }
  if (rest[0] === "[" || rest[0] === "{") {
    const text = [rest, ...below.map((l) => stripComment(l).trim())].join(" ");
    const [parsed, end] = flow(text, 0);
    if (text.slice(end).trim().length > 0) fail();
    return parsed;
  }
  if (rest[0] === "|" || rest[0] === ">") return blockScalar(rest, below);
  return scalar([rest, ...body.map((l) => l.trim())].join(" "));
}

/** A block sequence's items, each "- value" with the more indented lines that follow it. */
function blockSequence(lines: readonly string[]): Yaml {
  const indent = indentOf(lines[0]!);
  const items: Yaml[] = [];
  for (let i = 0; i < lines.length;) {
    const line = lines[i]!;
    if (indentOf(line) !== indent || !isItem(line.trimStart())) fail();
    let j = i + 1;
    while (j < lines.length && indentOf(lines[j]!) > indent) j++;
    const text = line.trimStart().slice(1);
    const inline = stripComment(text).trim();
    const more = lines.slice(i + 1, j);
    if (inline.length === 0) items.push(more.length === 0 ? null : isItem(more[0]!.trimStart()) ? blockSequence(more) : MAP);
    else if (isItem(inline)) items.push([]);
    else if (keyOf(inline) !== null) items.push(MAP);
    else items.push(value(text, more));
    i = j;
  }
  return items;
}

function blockScalar(header: string, below: readonly string[]): Yaml {
  if (!/^[|>][+-]?$/.test(header)) fail();
  const content = below.filter((l) => l.trim().length > 0);
  if (content.length === 0) return "";
  const indent = Math.min(...content.map(indentOf));
  const lines = below.map((l) => l.slice(Math.min(indent, indentOf(l))));
  // Trimmed afterwards, so only the line breaks inside count: a literal keeps them, a folded one joins lines with a
  // space and keeps a break for each blank line.
  if (header[0] === "|") return lines.join("\n");
  return lines.reduce((text, line) => (line.length === 0 ? text + "\n" : text.length === 0 || text.endsWith("\n") ? text + line : `${text} ${line}`), "");
}

/** A scalar written in a block: quoted, or plain. */
function scalar(text: string): Yaml {
  const t = text.trim();
  if (t[0] === "&" || t[0] === "*" || t[0] === "!") fail();
  if (t[0] === '"' || t[0] === "'") {
    const [parsed, end] = quoted(t, 0);
    if (stripComment(t.slice(end)).trim().length > 0) fail();
    return parsed;
  }
  const plain = stripComment(t).trim();
  if (/:(\s|$)/.test(plain)) fail();
  return plainValue(plain);
}

function plainValue(text: string): string | null {
  return text === "" || text === "~" || text === "null" || text === "Null" || text === "NULL" ? null : text;
}

/** A flow collection or scalar at `i`, and the index after it. */
function flow(text: string, i: number): [Yaml, number] {
  i = skipSpace(text, i);
  const c = text[i];
  if (c === "[" || c === "{") {
    const close = c === "[" ? "]" : "}";
    const items: Yaml[] = [];
    i = skipSpace(text, i + 1);
    while (text[i] !== close) {
      if (i >= text.length) fail();
      const [item, end] = flow(text, i);
      items.push(item);
      i = skipSpace(text, end);
      if (text[i] === ",") i = skipSpace(text, i + 1);
      else if (text[i] !== close) fail();
    }
    return [c === "[" ? items : MAP, i + 1];
  }
  if (c === '"' || c === "'") {
    const [parsed, end] = quoted(text, i);
    const after = skipSpace(text, end);
    // "a": b in a sequence is a map of one key.
    return text[after] === ":" ? [MAP, skipEntry(text, after + 1)] : [parsed, end];
  }
  if (c === undefined || c === "," || c === "]" || c === "}" || c === "&" || c === "*" || c === "!") fail();
  let end = i;
  while (end < text.length && !",[]{}".includes(text[end]!)) end++;
  const plain = text.slice(i, end).trim();
  return /:(\s|$)/.test(plain) ? [MAP, end] : [plainValue(plain), end];
}

/** Past the value of a flow map entry. */
function skipEntry(text: string, i: number): number {
  return flow(text, i)[1];
}

function skipSpace(text: string, i: number): number {
  while (i < text.length && (text[i] === " " || text[i] === "\t")) i++;
  return i;
}

/** A quoted scalar starting at `i`, and the index after its closing quote. */
function quoted(text: string, i: number): [string, number] {
  const q = text[i]!;
  for (let k = i + 1; k < text.length; k++) {
    if (q === "'" && text[k] === "'") {
      if (text[k + 1] === "'") {
        k++;
        continue;
      }
      return [text.slice(i + 1, k).replace(/''/g, "'"), k + 1];
    }
    if (q === '"') {
      if (text[k] === "\\") k++;
      else if (text[k] === '"') return [unquoteDouble(text.slice(i + 1, k)), k + 1];
    }
  }
  fail();
}

const ESCAPES: Record<string, string> = {
  "0": "\0", a: "\x07", b: "\b", t: "\t", "\t": "\t", n: "\n", v: "\v", f: "\f", r: "\r", e: "\x1b", " ": " ", '"': '"',
  "/": "/", "\\": "\\", N: "\x85", _: "\xa0", L: " ", P: " ",
};

function unquoteDouble(text: string): string {
  return text.replace(/\\(x[0-9A-Fa-f]{2}|u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8}|.)/g, (_, e: string) => {
    if (e.length > 1) {
      const code = parseInt(e.slice(1), 16);
      return code <= 0x10ffff ? String.fromCodePoint(code) : fail();
    }
    return ESCAPES[e] ?? fail();
  });
}

/** The text without a comment: " #" outside quotes up to the end. */
function stripComment(text: string): string {
  let quote: string | null = null;
  for (let k = 0; k < text.length; k++) {
    const c = text[k]!;
    if (quote !== null) {
      if (quote === '"' && c === "\\") k++;
      else if (c === quote) quote = null;
    } else if ((c === '"' || c === "'") && (k === 0 || /[\s[{,:]/.test(text[k - 1]!))) {
      quote = c;
    } else if (c === "#" && (k === 0 || text[k - 1] === " " || text[k - 1] === "\t")) {
      return text.slice(0, k);
    }
  }
  return text;
}
