// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { api, call, HttpError } from "../api/client";
import { vaultOf } from "../api/paths";
import { useEditor } from "../state/editor";

// The calendar's JSON, as `CalendarApi` answers it. Kept here, in one place, until the generated schema has them:
// then each becomes `components["schemas"][…]` and `untyped` below goes.

export type EntryKind = "dated" | "task" | "card";
export type TaskMark = "none" | "open" | "closed";
export type EntryState = "notYet" | "upcoming" | "ongoing" | "overdue" | "done" | "past";

export type EntryView = {
  path: string;
  vault: string;
  title: string;
  kind: EntryKind;
  when: string;
  until: string | null;
  task: TaskMark;
  closed: string | null;
  reason: string | null;
  upcoming: string | null;
  tz: string | null;
  byName: boolean;
  column: string | null;
  state: EntryState;
  /** "1 day", "in 7 days", "until Fri": beside the state. */
  span: string | null;
  /** "14:00", or "14:00 (Europe/London)" for a time in another zone. */
  time: string | null;
  firstDay: string;
  lastDay: string;
  spans: boolean;
  /** The board's one archivable column ("Done"), for a card. */
  boardAction: string | null;
};
export type CouldntReadView = { path: string; title: string; plain: string; readable: Record<string, string> };
export type DayView = { day: string; entries: EntryView[]; alsoOngoing: EntryView[] };
export type WeekView = { year: number; week: number; monday: string; current: boolean; spanning: EntryView[]; days: DayView[] };
export type CalendarView = { vault: string; today: string; zone: string; weeks: WeekView[]; couldntRead: CouldntReadView[]; canWrite: boolean };
export type NoteEntryView = { entry: EntryView | null; couldntRead: CouldntReadView | null; canWrite: boolean };
export type WrittenView = { path: string; version: string; note: NoteEntryView };
export type CalendarViewState = { view: "week" | "month"; grid: boolean };

export type SetEntryRequest = {
  path: string;
  when?: string;
  until?: string;
  clearUntil?: boolean;
  task?: boolean;
  upcoming?: string;
  clearUpcoming?: boolean;
  reason?: string;
  clearReason?: boolean;
};
export type CloseRequest = { path: string; reason?: string };
export type MoveEntryRequest = { path: string; when: string };
export type LaterRequest = { path: string; part: "postpone" | "moreTime"; step?: "day" | "week"; date?: string };
export type NewEntryRequest = { day: string; title: string; time?: string; task: boolean; upcoming?: string };

type Answer<T> = Promise<{ data?: T; error?: unknown; response: Response }>;
type Init = { params: { path: { vault: string }; query?: Record<string, string> }; body?: unknown };

/**
 * The app's API client without the generated types the calendar's routes don't have yet: its middleware (a session
 * that ended, the password window, a new release) and `call`'s errors still apply.
 */
const untyped = api as unknown as { GET<T>(url: string, init: Init): Answer<T>; POST<T>(url: string, init: Init): Answer<T>; PUT<T>(url: string, init: Init): Answer<T> };

export type CalendarRange = { week?: string; month?: string };

/** The vault's calendar for a week or a month (neither: this week in the person's zone), narrowed to a tag if given. */
export function useCalendar(vault: string, range: CalendarRange, tag?: string) {
  const query: Record<string, string> = {};
  if (range.week) query.week = range.week;
  else if (range.month) query.month = range.month;
  if (tag) query.tag = tag;
  return useQuery({
    queryKey: ["calendar", vault, range.week ?? null, range.month ?? null, tag ?? null],
    queryFn: () => call(untyped.GET<CalendarView>("/api/vaults/{vault}/calendar", { params: { path: { vault }, query } })),
    retry: false,
  });
}

export type CalendarAction =
  | ({ kind: "set" } & SetEntryRequest)
  | ({ kind: "move" } & MoveEntryRequest)
  | ({ kind: "close" } & CloseRequest)
  | { kind: "reopen"; path: string }
  | { kind: "unset"; path: string }
  | ({ kind: "later" } & LaterRequest)
  | { kind: "today" }
  | { kind: "this-week" }
  | ({ kind: "entries" } & NewEntryRequest);

/** Said when a write waits for the editor's save and that save fails: nothing is sent. */
export const UNSAVED = "Your typing isn't saved yet; try again in a moment.";

/** A refusal of the app's own, before anything is sent: `errorText` gives its sentence as it gives the server's. */
class UnsavedError extends HttpError {
  constructor() {
    super(409, UNSAVED, "unsaved");
  }
}

/**
 * The editor's text is saved before the calendar writes the note. In Write mode on the note, Write mode ends (its
 * autosave keeps its own base version, so a write under an open editor would make its next save conflict); otherwise
 * any pending save is flushed. False when the save failed.
 */
export async function editorSettled(path: string): Promise<boolean> {
  const editor = useEditor.getState();
  if (editor.mode === "write" && editor.currentPath === path) {
    await editor.endWrite();
    return useEditor.getState().mode === "read";
  }
  return editor.beforeRead ? editor.beforeRead({ reread: false }) : true;
}

async function send(vault: string, action: CalendarAction) {
  const { kind, ...body } = action;
  if ("path" in action && !(await editorSettled(action.path))) throw new UnsavedError();
  return call(untyped.POST<WrittenView>(`/api/vaults/{vault}/calendar/${kind}`, { params: { path: { vault } }, body }));
}

/**
 * After a change to an entry, or a new one: every view of it is asked again, the calendar's, What's on and the badge,
 * the note's header line and the note itself; a new note shows in the tree. Answers once the note is read again, so
 * Write mode after it starts on the new version.
 */
export function afterEntryWrite(queryClient: QueryClient, vault: string, path: string) {
  for (const queryKey of [["calendar"], ["whats-on"], ["note-entry"], ["tree", vault], ["vaults"]]) void queryClient.invalidateQueries({ queryKey });
  return queryClient.invalidateQueries({ queryKey: ["note", path] });
}

/** A change to an entry, or a new one (a day's +, Today, This week). A change waits for the editor's save first. */
export function useCalendarAction(vault: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationKey: ["calendar", vault],
    mutationFn: (action: CalendarAction) => send(vault, action),
    onSuccess: (written) => afterEntryWrite(queryClient, vault, written.path),
  });
}

/** The same for an entry of any vault (What's on, the note's header): the vault is the note's. */
export function useEntryAction() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (action: Extract<CalendarAction, { path: string }>) => send(vaultOf(action.path), action),
    onSuccess: (written) => afterEntryWrite(queryClient, vaultOf(written.path), written.path),
  });
}

/**
 * The note's place in the calendar, for its header line and its ⋯ menu: asked only for a note being shown (or whose
 * menu is open), which the session shows anyway.
 */
export function useNoteEntry(path: string | null, enabled = true) {
  return useQuery({
    queryKey: ["note-entry", path],
    queryFn: () =>
      call(untyped.GET<NoteEntryView>("/api/vaults/{vault}/calendar/entry", { params: { path: { vault: vaultOf(path!) }, query: { path: path! } } })),
    enabled: enabled && path !== null,
    retry: false,
  });
}

/** Keeps the person's view of this vault's calendar (week or month, the grid shown or not) for next time. */
export function useSetCalendarView(vault: string) {
  return useMutation({
    mutationFn: (view: CalendarViewState) => call(untyped.PUT<CalendarViewState>("/api/vaults/{vault}/calendar/view", { params: { path: { vault } }, body: view })),
  });
}
