// SPDX-License-Identifier: AGPL-3.0-or-later
import { fileKindOfEntry, findFolder, type TreeFolder } from "../api/notes";
import { folderOf, titleOf } from "../api/paths";
import { wordMatch } from "../palette/filter";
import { frontmatterEnd, mergeProperties, yamlValue, type Merged } from "./frontmatterText";
import { parseProperties } from "./properties";
import type { Edit } from "./textEdit";

/** What a template's placeholders are filled with: the note's name without `.md`, the moment, and the user's zone. */
export type Context = { title: string; now: Date; zone: string };

type Parts = Record<"year" | "month" | "day" | "hour" | "minute" | "second" | "weekday" | "monthName", string>;

/** The moment's parts in `zone`, in English; the browser's zone for one `Intl` doesn't know. */
function partsOf(now: Date, zone: string): Parts {
  const read = (timeZone: string | undefined) => {
    const get = (options: Intl.DateTimeFormatOptions) =>
      Object.fromEntries(new Intl.DateTimeFormat("en-US", { ...options, timeZone }).formatToParts(now).map((p) => [p.type, p.value]));
    // h23: midnight is 00, where some engines' hour12: false says 24.
    const numeric = get({ year: "numeric", month: "numeric", day: "numeric", hour: "numeric", minute: "numeric", second: "numeric", hourCycle: "h23" });
    const names = get({ weekday: "long", month: "long" });
    return { ...numeric, weekday: names.weekday, monthName: names.month } as Parts;
  };
  try {
    return read(zone);
  } catch {
    return read(undefined);
  }
}

const pad = (n: string) => n.padStart(2, "0");

/** The day of the month with its English ordinal suffix: 1st, 2nd, 3rd, 4th, 11th, 21st. */
const ordinal = (day: string) => {
  const n = Number(day);
  const suffix = n % 100 >= 11 && n % 100 <= 13 ? "th" : (["th", "st", "nd", "rd"][n % 10] ?? "th");
  return `${n}${suffix}`;
};

const tokens: Record<string, (p: Parts) => string> = {
  YYYY: (p) => p.year,
  YY: (p) => p.year.slice(-2),
  MMMM: (p) => p.monthName,
  MMM: (p) => p.monthName.slice(0, 3),
  MM: (p) => pad(p.month),
  M: (p) => String(Number(p.month)),
  DD: (p) => pad(p.day),
  D: (p) => String(Number(p.day)),
  Do: (p) => ordinal(p.day),
  dddd: (p) => p.weekday,
  ddd: (p) => p.weekday.slice(0, 3),
  HH: (p) => pad(p.hour),
  H: (p) => String(Number(p.hour)),
  mm: (p) => pad(p.minute),
  ss: (p) => pad(p.second),
};

// Moment's tokens, read as Moment reads them (its format regex), so that one this doesn't fill (`DDDD`, `Wo`) stays
// as written rather than come out half-filled; `[…]` is text.
const token = /\[([^\]]*)\]|Mo|M{1,4}|Do|DDDo|D{1,4}|d{2,4}|do?|w[ow]?|W[oW]?|Qo?|N{1,5}|Y{5,6}|YYYY|YY|y{2,4}|yo?|gg(?:ggg?)?|GG(?:GGG?)?|[eEaAxX]|hh?|HH?|kk?|mm?|ss?|S{1,9}|zz?|ZZ?/g;

/** The moment in `zone` as a Moment format (`dddd, MMMM Do YYYY`) of the tokens above; any other text, and any other token, stays as written. */
export function formatMoment(now: Date, zone: string, format: string): string {
  return formatParts(partsOf(now, zone), format);
}

const formatParts = (parts: Parts, format: string) =>
  format.replace(token, (match, literal: string | undefined) => literal ?? tokens[match]?.(parts) ?? match);

/**
 * The text with `{{title}}`, `{{date}}`, `{{time}}`, `{{date:FORMAT}}` and `{{time:FORMAT}}` filled in, as Obsidian's
 * core Templates fills them. Anything else in braces, and Templater's `<% %>`, stays as written.
 */
export function fillPlaceholders(text: string, at: Context): string {
  // The moment's parts once per text, not per placeholder.
  let parts: Parts | undefined;
  // A format within a line and of at most 100 characters: an unclosed `{{date:` is never read to the end of the text,
  // so a template full of them fills in linear time; a longer format stays as written.
  return text.replace(/\{\{(title|date|time)(?::([^}\n]{0,100}))?\}\}/g, (match, name: string, format: string | undefined) => {
    if (name === "title") return format === undefined ? at.title : match;
    parts ??= partsOf(at.now, at.zone);
    return formatParts(parts, format || (name === "date" ? "YYYY-MM-DD" : "HH:mm"));
  });
}

export const noteUnreadable = "The template's properties weren't added: this note's properties can't be read.";
export const templateUnreadable = "The template's properties weren't added: the template's properties can't be read.";
export const notMerged = "The template's properties weren't added: they couldn't be merged safely into this note's.";

/**
 * A filled value that is a date, a time or a number, which YAML (and Obsidian) read as such where written plain; a
 * colon before a space or the end (`10 : 30`, `10:`) would make it a key, which YAML refuses there.
 */
const dateLike = (v: string) => /^\d[\d :.T+-]*$/.test(v) && !/:(\s|$)/.test(v);

/** A template's properties and body, its placeholders filled; null for properties it has that can't be read. */
function readTemplate(template: string, at: Context): { props: Merged[] | null; body: string } {
  const lines = template.replace(/^\uFEFF/, "").split("\n");
  const end = frontmatterEnd(lines);
  if (end < 0) return { props: [], body: fillPlaceholders(lines.join("\n"), at) };
  const yaml = lines.slice(1, end);
  const body = fillPlaceholders(lines.slice(end + 1).join("\n"), at);
  const props = parseProperties(yaml.join("\n"));
  if (!props) return { props: null, body };
  // Each key's line, the first that names it, found once rather than once per property.
  const lineOf = new Map<string, string>();
  for (const l of yaml) {
    if (!/^[^\s:#][^:]*:/.test(l)) continue;
    const key = l.slice(0, l.indexOf(":")).trim();
    if (!lineOf.has(key)) lineOf.set(key, l.replace(/\r$/, ""));
  }
  return {
    body,
    props: props.map(({ key, value }): Merged => {
      if (Array.isArray(value)) return { key, value: value.map((v) => fillPlaceholders(v, at)) };
      const filled = fillPlaceholders(value, at);
      // `created: {{date}}` stays a date for Obsidian.
      const line = lineOf.get(key)!;
      const raw = fillPlaceholders(line.slice(line.indexOf(":") + 1).trim(), at);
      // As the template writes it only where that is how it would be written anyway, or where it is a date, a time or
      // a number that reads back as written: parseProperties is too lenient to vouch for other quoting (`'Bob's plan'`).
      const keep = raw === yamlValue(filled) || (dateLike(filled) && parseProperties(`k: ${raw}`)?.[0]?.value === filled);
      return { key, value: filled, written: keep ? raw : yamlValue(filled) };
    }),
  };
}

/**
 * Inserting a template into the note's text `value` with `start`–`end` selected: its body at the caret (below the
 * properties when the caret is in them), replacing the selection as typing would; its properties merged into the
 * note's (`mergeProperties`). One edit, from the first change to the caret, so it is one undo step; the caret after
 * the body. A hint says why properties weren't added.
 */
export function templateEdit(value: string, start: number, end: number, template: string, at: Context): { edit: Edit; hint: string | null } {
  const { props, body: filled } = readTemplate(template, at);
  let merged = value;
  let hint = props ? null : templateUnreadable;
  if (props?.length) {
    const result = mergeProperties(value, props);
    if ("content" in result) merged = result.content;
    else hint = result.refused === "unreadable" ? noteUnreadable : notMerged;
  }
  // Where the note's body starts: past its frontmatter's closing line (and its BOM).
  const bom = value.startsWith("\uFEFF") ? 1 : 0;
  const lines = value.slice(bom).split("\n");
  const close = frontmatterEnd(lines);
  const floor = close < 0 ? bom : bom + lines.slice(0, close + 1).join("\n").length + (close + 1 < lines.length ? 1 : 0);
  // The body in the note's line endings, where it has any; on a line of its own after a closing `---` that ends the note.
  const eol = lines.length > 1 ? (lines[0].endsWith("\r") ? "\r\n" : "\n") : null;
  const converted = eol ? filled.replace(/\r?\n/g, eol) : filled;
  const body = close >= 0 && close + 1 === lines.length ? (lines[close].endsWith("\r") ? "\n" : eol) + converted : converted;
  const from = Math.max(start, floor);
  const to = Math.max(end, from);
  // The merge changes only what is above the body.
  const shift = merged.length - value.length;
  const next = merged.slice(0, from + shift) + body + value.slice(to);
  let first = 0;
  while (first < from && value[first] === next[first]) first++;
  const caret = from + shift + body.length;
  return { edit: { start: first, end: to, text: next.slice(first, next.length - (value.length - to)), selStart: caret, selEnd: caret }, hint };
}

/** The folder a vault's templates are in, by convention as `Board` is a board's: `Templates` at its top, this spelling. */
export const templatesFolder = (vault: string) => `/${vault}/Templates`;

/** The notes in the vault's templates folder and below it, not its attachments, nor `writing`, the note they'd go into. */
export function templatePaths(root: TreeFolder, vault: string, writing: string): string[] {
  const folder = findFolder(root, templatesFolder(vault));
  const walk = (f: TreeFolder): string[] => [
    ...f.notes.filter((n) => fileKindOfEntry(n) === "note" && n.path !== writing).map((n) => n.path),
    ...f.folders.flatMap(walk),
  ];
  return folder ? walk(folder) : [];
}

/** A template as the list shows it: its name, and its sub-folder below the templates folder (`Daily / Work`, or none). */
export type Template = { path: string; name: string; folder: string };

/** The templates holding every word of the query in their sub-folder or name, those where each starts a word first, then by name. */
export function findTemplates(paths: string[], vault: string, query: string): Template[] {
  const match = wordMatch(query);
  const top = templatesFolder(vault);
  return paths
    .flatMap((path) => {
      const template = { path, name: titleOf(path), folder: folderOf(path).slice(top.length + 1).split("/").join(" / ") };
      const rank = match(`${template.folder} ${template.name}`);
      return rank === null ? [] : [{ template, rank }];
    })
    .sort((a, b) => a.rank - b.rank || a.template.name.localeCompare(b.template.name) || a.template.path.localeCompare(b.template.path))
    .map(({ template }) => template);
}
