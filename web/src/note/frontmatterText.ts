// SPDX-License-Identifier: AGPL-3.0-or-later
import { flowMap, parseProperties, splitFrontmatter, type Property } from "./properties";

/** A block in a note's body that looks like frontmatter: 0-based lines of its opening and closing `---`. */
export type PropertyBlock = { start: number; end: number; keys: string[] };

const BOM = "\uFEFF";
const plainKey = /^[A-Za-z][A-Za-z0-9_-]*$/;
const keyLine = /^([A-Za-z][A-Za-z0-9_-]*):(\s|$)/;
const itemLine = /^\s*-(\s|$)/;
const atxHeading = /^ {0,3}#{1,6}(\s|$)/;

/** The text after a leading BOM, and the BOM to put back first. */
const withoutBom = (content: string) =>
  content.startsWith(BOM) ? { bom: BOM, text: content.slice(1) } : { bom: "", text: content };

/** The frontmatter's closing line as `splitFrontmatter` finds it, or -1 where there is none. */
export function frontmatterEnd(lines: string[]): number {
  if (lines[0]?.trimEnd() !== "---") return -1;
  return lines.findIndex((l, i) => i > 0 && (l.trimEnd() === "---" || l.trimEnd() === "..."));
}

/** Where each line starts in the text, with the text's length after the last one. */
function lineStarts(lines: string[]): number[] {
  const starts = [0];
  for (const l of lines) starts.push(starts[starts.length - 1] + l.length + 1);
  starts[starts.length - 1]--;
  return starts;
}

const endingOf = (line: string) => (line.endsWith("\r") ? "\r\n" : "\n");

/** A value `parseProperties` takes but YAML wouldn't: a quote left open, a flow map that doesn't read. */
const unfinished = (v: string) =>
  ((v.startsWith('"') || v.startsWith("'")) && (v.length < 2 || !v.endsWith(v[0]))) ||
  (v.startsWith("{") && flowMap(v) === null);

/**
 * The lines between a block's rules, as properties: each top-level key plain and once, `- item` lines only under
 * `key:`, no value left unfinished.
 */
function blockKeys(lines: string[]): string[] | null {
  let underKey = false;
  for (const line of lines) {
    if (itemLine.test(line)) {
      if (!underKey || unfinished(line.replace(/^\s*-\s?/, "").trim())) return null;
    } else {
      const m = keyLine.exec(line);
      const value = m && line.slice(m[1].length + 1).trim();
      if (value === null || unfinished(value)) return null;
      underKey = value === "";
    }
  }
  const keys = parseProperties(lines.join("\n"))?.map((p) => p.key);
  return keys && new Set(keys).size === keys.length ? keys : null;
}

/** The test for the line that ends an HTML block of CommonMark's type 1 or 2 opening at `line`, or null. */
function htmlBlockEnd(line: string): ((line: string) => boolean) | null {
  const raw = /^ {0,3}<(script|pre|style|textarea)(?=[\s>]|$)/i.exec(line);
  if (raw) {
    const closing = new RegExp(`</${raw[1]}>`, "i");
    return (l) => closing.test(l);
  }
  return /^ {0,3}<!--/.test(line) ? (l) => l.includes("-->") : null;
}

/**
 * The blocks in a note's body that look like frontmatter but render as a rule and a heading: `---` after a blank
 * line or an ATX heading, lines that read as properties, `---`. Fenced code, HTML blocks that run across blank
 * lines (comments, `<pre>`, `<script>`, `<style>`, `<textarea>`) and the real frontmatter are skipped;
 * anything doubtful is no block.
 */
export function findPropertyBlocks(content: string): PropertyBlock[] {
  const lines = withoutBom(content).text.split("\n").map((l) => l.replace(/\r$/, ""));
  const blocks: PropertyBlock[] = [];
  // The test for the line that ends the fenced code or HTML block the scan is in.
  let skipUntil: ((line: string) => boolean) | null = null;
  for (let i = frontmatterEnd(lines) + 1; i < lines.length; i++) {
    const line = lines[i];
    if (skipUntil) {
      if (skipUntil(line)) skipUntil = null;
      continue;
    }
    const f = /^ {0,3}(`{3,}|~{3,})(.*)$/.exec(line);
    // A backtick fence's info string can't hold a backtick (CommonMark); such a line is no fence.
    if (f && !(f[1][0] === "`" && f[2].includes("`"))) {
      const [char, length] = [f[1][0], f[1].length];
      skipUntil = (l) => {
        const c = /^ {0,3}(`{3,}|~{3,})\s*$/.exec(l);
        return !!c && c[1][0] === char && c[1].length >= length;
      };
      continue;
    }
    const html = htmlBlockEnd(line);
    if (html) {
      // The block may end on its opening line, after the opening tag.
      if (!html(line.replace(/^ {0,3}<(!--|\w+)/, ""))) skipUntil = html;
      continue;
    }
    // After prose, the `---` would make the line above a setext heading rather than open a block.
    if (line !== "---" || i === 0 || !(lines[i - 1].trim() === "" || atxHeading.test(lines[i - 1]))) continue;
    let end = i + 1;
    while (end < lines.length && lines[end] !== "---" && lines[end].trim() !== "") end++;
    if (end === i + 1 || lines[end] !== "---") continue;
    const keys = blockKeys(lines.slice(i + 1, end));
    if (!keys) continue;
    blocks.push({ start: i, end, keys });
    i = end;
  }
  return blocks;
}

/** The text with key lines added as the frontmatter's last lines, or a frontmatter of them created at the top. */
function withProperties(text: string, keyLines: string[]): string {
  const lines = text.split("\n");
  const end = frontmatterEnd(lines);
  if (end < 0) {
    const eol = lines.length > 1 ? endingOf(lines[0]) : "\n";
    return ["---", ...keyLines, "---"].map((l) => l + eol).join("") + text;
  }
  const at = lineStarts(lines)[end];
  return text.slice(0, at) + keyLines.map((l) => l + endingOf(lines[0])).join("") + text.slice(at);
}

/** The frontmatter's properties, or null where there is none or it can't be read. */
const propertiesOf = (text: string) => {
  const { frontmatter } = splitFrontmatter(text);
  return frontmatter === null ? null : parseProperties(frontmatter);
};

/** Whether the note has a frontmatter at the top that can't be read as properties. */
export const frontmatterUnreadable = (content: string) => {
  const { frontmatter } = splitFrontmatter(withoutBom(content).text);
  return frontmatter !== null && parseProperties(frontmatter) === null;
};

/**
 * The note's text with a block's keys moved into its frontmatter (created at the top when there is none) and the
 * block removed; every other byte as it was. Refused for a key the frontmatter has, an unreadable frontmatter, or
 * a block that is no longer there.
 */
export function moveToProperties(
  content: string,
  block: { start: number; end: number },
): { content: string } | { refused: "exists"; key: string } | { refused: "unreadable" } | { refused: "gone" } {
  const found = findPropertyBlocks(content).find((b) => b.start === block.start && b.end === block.end);
  if (!found) return { refused: "gone" };
  const { bom, text } = withoutBom(content);
  const { frontmatter } = splitFrontmatter(text);
  const props = frontmatter === null ? [] : parseProperties(frontmatter);
  if (!props) return { refused: "unreadable" };
  const key = found.keys.find((k) => props.some((p) => p.key === k));
  if (key) return { refused: "exists", key };

  const lines = text.split("\n");
  const starts = lineStarts(lines);
  const keyLines = lines.slice(found.start + 1, found.end).map((l) => l.replace(/\r$/, ""));
  const moved = withProperties(text.slice(0, starts[found.start]) + text.slice(starts[found.end + 1]), keyLines);
  // Written only when it reads back with the keys, so the box never turns into raw text.
  const after = propertiesOf(moved);
  if (!after || !found.keys.every((k) => after.some((p) => p.key === k))) return { refused: "unreadable" };
  return { content: bom + moved };
}

/**
 * The note's text with `name: value` as the frontmatter's last line (created at the top when there is none);
 * every other byte as it was. Refused for a name that isn't a plain key or is there already, an unreadable
 * frontmatter, or a value that wouldn't read back as given or holds a control character other than a tab.
 */
export function addProperty(
  content: string,
  name: string,
  value: string,
): { content: string } | { refused: "name" } | { refused: "exists" } | { refused: "unreadable" } {
  if (!plainKey.test(name)) return { refused: "name" };
  const { bom, text } = withoutBom(content);
  const { frontmatter } = splitFrontmatter(text);
  const props = frontmatter === null ? [] : parseProperties(frontmatter);
  if (!props) return { refused: "unreadable" };
  if (props.some((p) => p.key === name)) return { refused: "exists" };
  // A control character other than a tab (a line break among them) would need an escape this reading doesn't undo.
  if (/[\u0000-\u0008\u000A-\u001F\u007F-\u009F]/.test(value)) return { refused: "unreadable" };
  const added = withProperties(text, [`${name}: ${yamlValue(value)}`]);
  // Written only when it reads back as given, so the box never turns into raw text.
  if (propertiesOf(added)?.find((p) => p.key === name)?.value !== value) return { refused: "unreadable" };
  return { content: bom + added };
}

/** A property to merge into a note's, and for a text the YAML it is written as (`yamlValue` of it where not given). */
export type Merged = Property & { written?: string };

/** The lines a property the note lacks is added as: a list one `- item` per line below its key. */
const propertyLines = ({ key, value, written }: Merged): string[] => {
  if (Array.isArray(value)) return value.length ? [`${key}:`, ...value.map((v) => `  - ${yamlValue(v)}`)] : [`${key}: []`];
  const text = written ?? yamlValue(value);
  return [text === "" ? `${key}:` : `${key}: ${text}`];
};

/** The frontmatter's lines with `items` after the list at `key`: in its brackets where they read there, else below the key. */
function withItems(lines: string[], key: string, items: string[]): string[] {
  const end = frontmatterEnd(lines);
  const at = lines.findIndex((l, i) => i > 0 && i < end && /^[^\s:#][^:]*:/.test(l) && l.slice(0, l.indexOf(":")).trim() === key);
  const cr = lines[at].endsWith("\r") ? "\r" : "";
  const line = lines[at].replace(/\r$/, "");
  const colon = line.indexOf(":");
  const value = line.slice(colon + 1).trim();
  if (value.startsWith("[")) {
    // Plain items only: a quoted one may hold a comma, which an inline list can't be read back with (properties.ts).
    // A trailing comma (`[a,]`) goes, so that the items follow the last one.
    if (items.every((i) => yamlValue(i) === i)) {
      const close = line.lastIndexOf("]");
      const before = line.slice(0, close).trimEnd().replace(/,$/, "").trimEnd();
      const inline = `${before}${before.endsWith("[") ? "" : ", "}${items.join(", ")}${line.slice(close)}${cr}`;
      return [...lines.slice(0, at), inline, ...lines.slice(at + 1)];
    }
    // The note's items as it writes them (`2025`, `'it''s'`), split at the commas as parseProperties splits them: it
    // reads no inline list with a comma inside quotes or nested brackets, and drops one trailing comma.
    const inner = value.slice(1, -1).trim().replace(/,$/, "").trimEnd();
    const own = inner ? inner.split(",").map((i) => i.trim()) : [];
    const below = [line.slice(0, colon + 1), ...own.map((i) => `  - ${i}`), ...items.map((i) => `  - ${yamlValue(i)}`)].map((l) => l + cr);
    return [...lines.slice(0, at), ...below, ...lines.slice(at + 1)];
  }
  let last = at;
  while (last + 1 < end && /^\s+-(\s|$)|^-\s/.test(lines[last + 1])) last++;
  const mark = last > at ? `${/^\s*-/.exec(lines[last])![0]} ` : "  - ";
  return [...lines.slice(0, last + 1), ...items.map((i) => `${mark}${yamlValue(i)}${cr}`), ...lines.slice(last + 1)];
}

/** A key as YAML names it: `"status"` and `status` are the same key. */
const nameOf = (key: string) => (/^(["']).*\1$/.test(key) ? key.slice(1, -1) : key);

/**
 * The note's text with properties merged in, as Obsidian merges a template's: a key the note lacks is added as the
 * frontmatter's last line (created at the top when there is none); a list the note has, or a key it has without a
 * value, takes the items it lacks after its own; a text it has stays. Every other byte as it was. Refused for an
 * unreadable frontmatter, and for a merge that wouldn't read back as exactly that: each of the note's properties as
 * it was or its list with the new items after its own, each added one with its value, no key twice that wasn't.
 */
export function mergeProperties(content: string, add: Merged[]): { content: string } | { refused: "unreadable" } | { refused: "merge" } {
  const { bom, text } = withoutBom(content);
  const { frontmatter } = splitFrontmatter(text);
  const props = frontmatter === null ? [] : parseProperties(frontmatter);
  if (!props) return { refused: "unreadable" };
  let lines = text.split("\n");
  const added: string[] = [];
  // What the frontmatter is to read back as.
  const expected: Property[] = [...props];
  for (const p of add) {
    const at = props.findIndex((n) => nameOf(n.key) === nameOf(p.key));
    if (at < 0) {
      added.push(...propertyLines(p));
      expected.push({ key: p.key, value: p.value });
      continue;
    }
    const has = props[at];
    // A text stays, and so does a key without a value where the template has a text too.
    if (!Array.isArray(has.value) && (has.value !== "" || !Array.isArray(p.value))) continue;
    const own = Array.isArray(has.value) ? has.value : [];
    const items = [...new Set(Array.isArray(p.value) ? p.value : [p.value])].filter((i) => i !== "" && !own.includes(i));
    if (!items.length) continue;
    lines = withItems(lines, has.key, items);
    expected[at] = { key: has.key, value: [...own, ...items] };
  }
  const merged = added.length ? withProperties(lines.join("\n"), added) : lines.join("\n");
  if (merged === text) return { content };
  // Written only when it reads back as intended, so the box never turns into raw text or changes what the note had.
  const after = propertiesOf(merged);
  const names = (ps: Property[]) => new Set(ps.map((p) => nameOf(p.key))).size;
  const same = (a: Property, b: Property) => a.key === b.key && JSON.stringify(a.value) === JSON.stringify(b.value);
  if (!after || after.length !== expected.length || !after.every((p, i) => same(p, expected[i])) || names(after) - names(props) !== after.length - props.length)
    return { refused: "merge" };
  return { content: bom + merged };
}

/**
 * A text as a YAML value: plain only when plainly safe (letters, digits, inner spaces, `_ . / -`, not starting
 * with a digit or sign, no YAML boolean or null word), else in double quotes; nothing is guessed about types.
 */
export function yamlValue(value: string): string {
  if (
    /^[A-Za-z_][A-Za-z0-9 _./-]*$/.test(value) &&
    !value.endsWith(" ") &&
    !/^(true|false|yes|no|on|off|null|~)$/i.test(value)
  )
    return value;
  // A line break can't reach here from addProperty, but yamlValue stands alone: it is exported and tested on its own.
  return `"${value.replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\r\n?|\n/g, "\\n")}"`;
}
