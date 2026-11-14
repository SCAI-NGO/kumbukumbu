// SPDX-License-Identifier: AGPL-3.0-or-later

// A day is "YYYY-MM-DD" as the server sends it, and stays a date: never read as a Date in the local zone, where
// midnight may not exist or fall on the day before. The arithmetic below is on UTC dates made from its parts.

const MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
const WEEKDAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

type Parts = { year: number; month: number; day: number };

export function partsOf(day: string): Parts {
  const [year, month, date] = day.slice(0, 10).split("-").map(Number);
  return { year, month, day: date };
}

const utc = ({ year, month, day }: Parts) => new Date(Date.UTC(year, month - 1, day));
const dayOf = (date: Date) =>
  `${String(date.getUTCFullYear()).padStart(4, "0")}-${String(date.getUTCMonth() + 1).padStart(2, "0")}-${String(date.getUTCDate()).padStart(2, "0")}`;

export const addDays = (day: string, days: number) => {
  const date = utc(partsOf(day));
  date.setUTCDate(date.getUTCDate() + days);
  return dayOf(date);
};

/** 0 for Sunday to 6 for Saturday, as `Date.getDay`. */
const weekdayOf = (day: string) => utc(partsOf(day)).getUTCDay();

/** The ISO week a day falls in: the week of its Thursday, which also gives the week's year. */
export function weekOf(day: string): { year: number; week: number } {
  const thursday = utc(partsOf(addDays(day, 3 - ((weekdayOf(day) + 6) % 7))));
  const year = thursday.getUTCFullYear();
  const week = Math.floor((thursday.getTime() - Date.UTC(year, 0, 1)) / 86_400_000 / 7) + 1;
  return { year, week };
}

/** "2026-W46", as the calendar's address and the server take a week. */
export const weekId = ({ year, week }: { year: number; week: number }) => `${year}-W${String(week).padStart(2, "0")}`;

/** "2026-11", as the calendar's address and the server take a month. */
export const monthId = (day: string) => day.slice(0, 7);

/** "Week 46 · 9–15 November 2026"; across months "26 October – 1 November 2026", across years with both years. */
export function formatWeekTitle(week: { week: number; monday: string }): string {
  const first = partsOf(week.monday);
  const last = partsOf(addDays(week.monday, 6));
  const range =
    first.year !== last.year
      ? `${first.day} ${MONTHS[first.month - 1]} ${first.year} – ${last.day} ${MONTHS[last.month - 1]} ${last.year}`
      : first.month !== last.month
        ? `${first.day} ${MONTHS[first.month - 1]} – ${last.day} ${MONTHS[last.month - 1]} ${last.year}`
        : `${first.day}–${last.day} ${MONTHS[last.month - 1]} ${last.year}`;
  return `Week ${week.week} · ${range}`;
}

/** "Week 46 · 9–15 Nov", as a month's weeks are headed; across months "Week 44 · 26 Oct – 1 Nov". */
export function shortWeekTitle(week: { week: number; monday: string }): string {
  const first = partsOf(week.monday);
  const last = partsOf(addDays(week.monday, 6));
  const short = (month: number) => MONTHS[month - 1].slice(0, 3);
  const range = first.month === last.month ? `${first.day}–${last.day} ${short(last.month)}` : `${first.day} ${short(first.month)} – ${last.day} ${short(last.month)}`;
  return `Week ${week.week} · ${range}`;
}

/** True for Saturday and Sunday. */
export const weekend = (day: string) => weekdayOf(day) % 6 === 0;

/** "November 2026". */
export function monthTitle(day: string): string {
  const { year, month } = partsOf(day);
  return `${MONTHS[month - 1]} ${year}`;
}

/** "Thursday, 12 November". */
export function dayHeading(day: string): string {
  const { month, day: date } = partsOf(day);
  return `${WEEKDAYS[weekdayOf(day)]}, ${date} ${MONTHS[month - 1]}`;
}

/** "Thu 12"; with `month`, "Thu 12 Nov". */
export function shortDay(day: string, month = false): string {
  const parts = partsOf(day);
  return `${WEEKDAYS[weekdayOf(day)].slice(0, 3)} ${parts.day}${month ? ` ${MONTHS[parts.month - 1].slice(0, 3)}` : ""}`;
}

/** What a screen reader says for a day's heading: "Thursday, 12 November, 3 entries, 1 overdue". */
export function dayLabel(day: string, entries: readonly { state: string }[]): string {
  const count = entries.length === 0 ? "no entries" : entries.length === 1 ? "1 entry" : `${entries.length} entries`;
  const overdue = entries.filter((e) => e.state === "overdue").length;
  return `${dayHeading(day)}, ${count}${overdue > 0 ? `, ${overdue} overdue` : ""}`;
}

/** "16 Dec"; with `long`, "16 December". */
export function dayAndMonth(day: string, long = false): string {
  const { month, day: date } = partsOf(day);
  return `${date} ${long ? MONTHS[month - 1] : MONTHS[month - 1].slice(0, 3)}`;
}
