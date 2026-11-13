// SPDX-License-Identifier: AGPL-3.0-or-later
import type { EntryView, NoteEntryView } from "../calendar/api";
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
