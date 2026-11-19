// SPDX-License-Identifier: AGPL-3.0-or-later
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

const tokens: Record<string, (p: Parts) => string> = {
  YYYY: (p) => p.year,
  YY: (p) => p.year.slice(-2),
  MMMM: (p) => p.monthName,
  MMM: (p) => p.monthName.slice(0, 3),
  MM: (p) => pad(p.month),
  M: (p) => String(Number(p.month)),
  DD: (p) => pad(p.day),
  D: (p) => String(Number(p.day)),
  dddd: (p) => p.weekday,
  ddd: (p) => p.weekday.slice(0, 3),
  HH: (p) => pad(p.hour),
  H: (p) => String(Number(p.hour)),
  mm: (p) => pad(p.minute),
  ss: (p) => pad(p.second),
};

// Longest first, so `MMMM` isn't read as `MM` twice; `[…]` is text, as in Moment.
const token = new RegExp(`\\[([^\\]]*)\\]|${Object.keys(tokens).sort((a, b) => b.length - a.length).join("|")}`, "g");

/** The moment in `zone` as a Moment format (`dddd, D MMMM YYYY`) of the tokens above; any other text stays as written. */
export function formatMoment(now: Date, zone: string, format: string): string {
  const parts = partsOf(now, zone);
  return format.replace(token, (match, literal: string | undefined) => literal ?? tokens[match](parts));
}

/**
 * The text with `{{title}}`, `{{date}}`, `{{time}}`, `{{date:FORMAT}}` and `{{time:FORMAT}}` filled in, as Obsidian's
 * core Templates fills them. Anything else in braces, and Templater's `<% %>`, stays as written.
 */
export function fillPlaceholders(text: string, at: Context): string {
  return text.replace(/\{\{(title|date|time)(?::([^}]*))?\}\}/g, (match, name: string, format: string | undefined) => {
    if (name === "title") return format === undefined ? at.title : match;
    return formatMoment(at.now, at.zone, format ?? (name === "date" ? "YYYY-MM-DD" : "HH:mm"));
  });
}

export const noteUnreadable = "The template's properties weren't added: this note's properties can't be read.";
export const templateUnreadable = "The template's properties weren't added: the template's properties can't be read.";

/** A template's properties and body, its placeholders filled; null for properties it has that can't be read. */
function readTemplate(template: string, at: Context): { props: Merged[] | null; body: string } {
  const lines = template.replace(/^\uFEFF/, "").split("\n");
  const end = frontmatterEnd(lines);
  if (end < 0) return { props: [], body: fillPlaceholders(lines.join("\n"), at) };
  const yaml = lines.slice(1, end);
  const body = fillPlaceholders(lines.slice(end + 1).join("\n"), at);
  const props = parseProperties(yaml.join("\n"));
  if (!props) return { props: null, body };
  return {
    body,
    props: props.map(({ key, value }): Merged => {
      if (Array.isArray(value)) return { key, value: value.map((v) => fillPlaceholders(v, at)) };
      const filled = fillPlaceholders(value, at);
      // As the template writes it (`created: {{date}}` stays a date for Obsidian) where that still reads as the value.
      const line = yaml.find((l) => /^[^\s:#][^:]*:/.test(l) && l.slice(0, l.indexOf(":")).trim() === key)!.replace(/\r$/, "");
      const raw = fillPlaceholders(line.slice(line.indexOf(":") + 1).trim(), at);
      return { key, value: filled, written: parseProperties(`k: ${raw}`)?.[0]?.value === filled ? raw : yamlValue(filled) };
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
  const { props, body } = readTemplate(template, at);
  let merged = value;
  let hint = props ? null : templateUnreadable;
  if (props?.length) {
    const result = mergeProperties(value, props);
    if ("content" in result) merged = result.content;
    else hint = noteUnreadable;
  }
  // Where the note's body starts: past its frontmatter's closing line (and its BOM).
  const bom = value.startsWith("\uFEFF") ? 1 : 0;
  const lines = value.slice(bom).split("\n");
  const close = frontmatterEnd(lines);
  const floor = close < 0 ? bom : bom + lines.slice(0, close + 1).join("\n").length + (close + 1 < lines.length ? 1 : 0);
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
