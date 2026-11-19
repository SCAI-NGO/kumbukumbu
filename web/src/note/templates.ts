// SPDX-License-Identifier: AGPL-3.0-or-later

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
