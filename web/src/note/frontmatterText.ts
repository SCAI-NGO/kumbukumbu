// SPDX-License-Identifier: AGPL-3.0-or-later
import { parseProperties, splitFrontmatter } from "./properties";

/** A block in a note's body that looks like frontmatter: 0-based lines of its opening and closing `---`. */
export type PropertyBlock = { start: number; end: number; keys: string[] };

const BOM = "﻿";
const plainKey = /^[A-Za-z][A-Za-z0-9_-]*$/;
const keyLine = /^([A-Za-z][A-Za-z0-9_-]*):(\s|$)/;
const itemLine = /^\s*-(\s|$)/;
const atxHeading = /^ {0,3}#{1,6}(\s|$)/;

/** The text after a leading BOM, and the BOM to put back first. */
const withoutBom = (content: string) =>
  content.startsWith(BOM) ? { bom: BOM, text: content.slice(1) } : { bom: "", text: content };

/** The frontmatter's closing line as `splitFrontmatter` finds it, or -1 where there is none. */
function frontmatterEnd(lines: string[]): number {
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

/** The lines between a block's rules, as properties: each top-level key plain, `- item` lines only under `key:`. */
function blockKeys(lines: string[]): string[] | null {
  let underKey = false;
  for (const line of lines) {
    if (itemLine.test(line)) {
      if (!underKey) return null;
    } else {
      const m = keyLine.exec(line);
      if (!m) return null;
      underKey = line.slice(m[1].length + 1).trim() === "";
    }
  }
  return parseProperties(lines.join("\n"))?.map((p) => p.key) ?? null;
}

/**
 * The blocks in a note's body that look like frontmatter but render as a rule and a heading: `---` after a blank
 * line or an ATX heading, lines that read as properties, `---`. Fenced code and the real frontmatter are skipped;
 * anything doubtful is no block.
 */
export function findPropertyBlocks(content: string): PropertyBlock[] {
  const lines = withoutBom(content).text.split("\n").map((l) => l.replace(/\r$/, ""));
  const blocks: PropertyBlock[] = [];
  let fence: { char: string; length: number } | null = null;
  for (let i = frontmatterEnd(lines) + 1; i < lines.length; i++) {
    const line = lines[i];
    const f = /^ {0,3}(`{3,}|~{3,})(.*)$/.exec(line);
    if (fence) {
      if (f && f[1][0] === fence.char && f[1].length >= fence.length && f[2].trim() === "") fence = null;
      continue;
    }
    // A backtick fence's info string can't hold a backtick (CommonMark); such a line is no fence.
    if (f && !(f[1][0] === "`" && f[2].includes("`"))) {
      fence = { char: f[1][0], length: f[1].length };
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
 * frontmatter, or a value that wouldn't read back as given.
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
  const added = withProperties(text, [`${name}: ${yamlValue(value)}`]);
  if (propertiesOf(added)?.find((p) => p.key === name)?.value !== value) return { refused: "unreadable" };
  return { content: bom + added };
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
  return `"${value.replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\r\n?|\n/g, "\\n")}"`;
}
