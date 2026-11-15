// SPDX-License-Identifier: AGPL-3.0-or-later
// Dates as the browser sees them: in Europe/Vienna (playwright.config.ts), never in the VM's own zone (UTC).

export const zone = "Europe/Vienna";

/** A calendar date, YYYY-MM-DD. */
export type Day = string;

const parts = (at: Date) =>
  Object.fromEntries(
    new Intl.DateTimeFormat("en-CA", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hourCycle: "h23" })
      .formatToParts(at)
      .map((p) => [p.type, p.value]),
  );

/** Today in Vienna. */
export const today = (at = new Date()): Day => {
  const p = parts(at);
  return `${p.year}-${p.month}-${p.day}`;
};

/** The time in Vienna, HH:MM. */
export const nowTime = (at = new Date()) => {
  const p = parts(at);
  return `${p.hour}:${p.minute}`;
};

const toUtc = (day: Day) => new Date(`${day}T12:00:00Z`);
const fromUtc = (d: Date): Day => d.toISOString().slice(0, 10);

/**
 * For a test that compares days across several steps: in the last minute before midnight in Vienna, waits until a few
 * seconds past it, so that every step sees the same day.
 */
export async function awayFromMidnight() {
  const p = parts(new Date());
  if (`${p.hour}:${p.minute}` < "23:59") return;
  const left = 60 - new Date().getUTCSeconds() + 5;
  // The clock's own minute, not a wait for the page.
  await new Promise((r) => setTimeout(r, left * 1000));
}

export const addDays = (day: Day, n: number): Day => {
  const d = toUtc(day);
  d.setUTCDate(d.getUTCDate() + n);
  return fromUtc(d);
};

export const addMonths = (day: Day, n: number): Day => {
  const d = toUtc(day);
  d.setUTCMonth(d.getUTCMonth() + n);
  return fromUtc(d);
};

export const addYears = (day: Day, n: number): Day => addMonths(day, 12 * n);

/** ISO 8601 week number and its year. */
export function isoWeek(day: Day): { year: number; week: number } {
  const d = toUtc(day);
  const weekday = (d.getUTCDay() + 6) % 7;
  d.setUTCDate(d.getUTCDate() - weekday + 3);
  const year = d.getUTCFullYear();
  const firstThursday = new Date(Date.UTC(year, 0, 4));
  firstThursday.setUTCDate(firstThursday.getUTCDate() - ((firstThursday.getUTCDay() + 6) % 7) + 3);
  return { year, week: 1 + Math.round((d.getTime() - firstThursday.getTime()) / (7 * 864e5)) };
}

/** Monday of the day's ISO week. */
export const monday = (day: Day): Day => addDays(day, -((toUtc(day).getUTCDay() + 6) % 7));

/** en-GB names, e.g. weekday "Thursday", "Thu"; month "November", "Nov". */
export const format = (day: Day, options: Intl.DateTimeFormatOptions) =>
  new Intl.DateTimeFormat("en-GB", { ...options, timeZone: "UTC" }).format(toUtc(day));
