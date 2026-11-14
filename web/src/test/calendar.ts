// SPDX-License-Identifier: AGPL-3.0-or-later
import type { CalendarView, EntryView, NoteEntryView } from "../calendar/api";
import { fakeApi, json, user } from "./app";

/** An entry of Work as the server sends it: a note given 12 November, ongoing; `extra` makes it anything else. */
export const entryView = (title: string, extra: Partial<EntryView> = {}): EntryView => ({
  path: `/Work/${title}.md`,
  vault: "Work",
  title,
  kind: "dated",
  when: "2026-11-12",
  until: null,
  task: "none",
  closed: null,
  reason: null,
  upcoming: null,
  tz: null,
  byName: false,
  column: null,
  state: "ongoing",
  span: null,
  time: null,
  firstDay: "2026-11-12",
  lastDay: "2026-11-12",
  spans: false,
  boardAction: null,
  ...extra,
});

/** An open to-do on Tuesday, 15 December. */
export const openTask = (title = "Certificate", extra: Partial<EntryView> = {}) =>
  entryView(title, { kind: "task", task: "open", when: "2026-12-15", state: "upcoming", span: "in 32 days", firstDay: "2026-12-15", lastDay: "2026-12-15", ...extra });

export const noteEntry = (entry: EntryView | null, canWrite = true): NoteEntryView => ({ entry, couldntRead: null, canWrite });

export const writtenView = (path: string, entry: EntryView | null = null, version = "v2") => ({ path, version, note: noteEntry(entry) });

/** The JSON bodies of the requests to `path`, in order. */
export const bodiesOf = async (fetch: { mock: { calls: unknown[][] } }, method: string, path: string) =>
  Promise.all(
    fetch.mock.calls
      .map(([r]) => r as Request)
      .filter((r) => r.method === method && new URL(r.url).pathname === path)
      .map((r) => r.clone().json().catch(() => null)),
  );

/**
 * The app with Work's note at `path` open (its text `content`), the vault at `level`, and the calendar saying `entry`
 * of it; `extra` answers more.
 */
export function noteWithEntry(path: string, entry: NoteEntryView | (() => NoteEntryView), extra: Parameters<typeof fakeApi>[0] = {}, level = "owner", content = "# Note\n") {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 4, embeddings: "internal", canWrite: level !== "read", level }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () =>
      json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [{ name: path.split("/").at(-1), path, size: 10 }] } }),
    "GET /api/vaults/Work/calendar/entry": () => json(200, typeof entry === "function" ? entry() : entry),
    [`GET /api/notes${path}`]: () => json(200, { path, content, version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-13T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/version/*": () => json(200, { path, version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-13T09:00:00Z" }),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    ...extra,
  });
}

/** The seven days from `monday`, "YYYY-MM-DD". */
const weekDays = (monday: string) =>
  Array.from({ length: 7 }, (_, i) => {
    const date = new Date(`${monday}T00:00:00Z`);
    date.setUTCDate(date.getUTCDate() + i);
    return date.toISOString().slice(0, 10);
  });

/** Work's calendar as the server lays it out (`on` the entries by day, `ongoing` Also ongoing by day), today 13 November. */
export function calendarOf(
  mondays: { year: number; week: number; monday: string }[],
  on: Record<string, EntryView[]> = {},
  extra: Partial<CalendarView> = {},
  ongoing: Record<string, EntryView[]> = {},
  spanning: Record<number, EntryView[]> = {},
): CalendarView {
  const today = extra.today ?? "2026-11-13";
  return {
    vault: "Work",
    today,
    zone: "Europe/Vienna",
    weeks: mondays.map(({ year, week, monday }) => {
      const days = weekDays(monday);
      return {
        year,
        week,
        monday,
        current: days.includes(today),
        spanning: spanning[week] ?? [],
        days: days.map((day) => ({ day, entries: on[day] ?? [], alsoOngoing: ongoing[day] ?? [] })),
      };
    }),
    couldntRead: [],
    canWrite: true,
    ...extra,
  };
}

/** Week 46 of 2026. */
export const week46 = [{ year: 2026, week: 46, monday: "2026-11-09" }];

/** The weeks of November 2026: 44 (26 October – 1 November) to 49 (30 November – 6 December). */
export const november = [
  { year: 2026, week: 44, monday: "2026-10-26" },
  { year: 2026, week: 45, monday: "2026-11-02" },
  { year: 2026, week: 46, monday: "2026-11-09" },
  { year: 2026, week: 47, monday: "2026-11-16" },
  { year: 2026, week: 48, monday: "2026-11-23" },
  { year: 2026, week: 49, monday: "2026-11-30" },
];

/**
 * The app on Work's calendar: `answer` gives the calendar for the query asked (`week`, `month`, `tag`), `view` the
 * remembered view (by default none stored: the server's default, the week without the grid).
 */
export function calendarApp(
  answer: (query: URLSearchParams) => CalendarView,
  view: { view: "week" | "month"; grid: boolean } = { view: "week", grid: false },
  extra: Parameters<typeof fakeApi>[0] = {},
) {
  let stored = view;
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 4, embeddings: "internal", canWrite: true, level: "owner" }]),
    "GET /api/account/calendar": () => json(200, { zone: "Europe/Vienna", serverZone: "UTC" }),
    "GET /api/vaults/Work/calendar": (r) => json(200, answer(new URL(r.url).searchParams)),
    "GET /api/vaults/Work/calendar/view": () => json(200, stored),
    "PUT /api/vaults/Work/calendar/view": async (r) => {
      stored = (await r.clone().json()) as typeof stored;
      return json(200, stored);
    },
    ...extra,
  });
}

/** The calendar queries the page sent, as their search parameters. */
export const calendarQueries = (fetch: { mock: { calls: unknown[][] } }) =>
  fetch.mock.calls
    .map(([r]) => r as Request)
    .filter((r) => r.method === "GET" && new URL(r.url).pathname === "/api/vaults/Work/calendar")
    .map((r) => Object.fromEntries(new URL(r.url).searchParams));
