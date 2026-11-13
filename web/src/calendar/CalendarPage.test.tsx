// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { FakeSocket } from "../test/socket";
import type { CalendarView, DayView, EntryView } from "./api";

beforeEach(() => {
  FakeSocket.all = [];
  vi.stubGlobal("WebSocket", FakeSocket);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const EMPTY = 'Nothing this week yet. Tap a day to add something, or give any note a day from its ⋯ menu. Your AI can do it too: "put this on Friday".';

const entry = (title: string, extra: Partial<EntryView> = {}): EntryView => ({
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

const days = ["2026-11-09", "2026-11-10", "2026-11-11", "2026-11-12", "2026-11-13", "2026-11-14", "2026-11-15"];

/** Week 46 of Work, with `on` the entries by day. */
function calendar(on: Record<string, EntryView[]> = {}, extra: Partial<CalendarView> = {}): CalendarView {
  const dayViews: DayView[] = days.map((day) => ({ day, entries: on[day] ?? [], alsoOngoing: [] }));
  return {
    vault: "Work",
    today: "2026-11-13",
    zone: "Europe/Vienna",
    weeks: [{ year: 2026, week: 46, monday: "2026-11-09", current: true, spanning: [], days: dayViews }],
    couldntRead: [],
    canWrite: true,
    ...extra,
  };
}

const certificate = entry("Certificate", { kind: "task", task: "open", when: "2026-11-11", state: "overdue", span: "1 day", firstDay: "2026-11-11", lastDay: "2026-11-11" });
const dentist = entry("Zahnarzt", { when: "2026-11-12T14:00", time: "14:00", state: "upcoming", span: "in 1 day" });

const written = (path: string) => ({ path, version: "v1", note: { entry: null, couldntRead: null, canWrite: true } });

function api(view: CalendarView, extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults/Work/calendar": () => json(200, view),
    // The note the page goes to, once it is open.
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 4, embeddings: "internal", canWrite: true, level: "owner" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    "GET /api/notes/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "# Thursday, 12 November 2026\n", version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-13T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/history/*": () => json(200, []),
    "GET /api/version/*": () => json(404, { error: "no" }),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    "POST /api/ws-ticket": () => json(200, { ticket: "t1", expiresAt: "2026-11-13T10:00:30Z" }),
    ...extra,
  });
}

/** The JSON bodies of the requests to `path`, in order. */
const bodies = async (fetch: ReturnType<typeof fakeApi>, method: string, path: string) =>
  Promise.all(
    fetch.mock.calls
      .map(([r]) => r as Request)
      .filter((r) => r.method === method && new URL(r.url).pathname === path)
      .map((r) => r.clone().json().catch(() => null)),
  );

const title = () => screen.findByRole("heading", { level: 1, name: "Week 46 · 9–15 November 2026" });

describe("the calendar's week", () => {
  it("shows the week title and seven day headings", async () => {
    api(calendar());
    renderApp("/calendar/Work");
    await title();
    const week = screen.getByRole("region", { name: "Week 46" });
    expect(within(week).getAllByRole("heading", { level: 2 })).toHaveLength(7);
    expect(screen.getByRole("link", { name: "Back to the notes" })).toHaveAttribute("href", "/");
    expect(screen.getByRole("link", { name: "Previous week" })).toHaveAttribute("href", "/calendar/Work?week=2026-W45");
    expect(screen.getByRole("link", { name: "Next week" })).toHaveAttribute("href", "/calendar/Work?week=2026-W47");
  });

  it("asks for the week of the address", async () => {
    const fetch = api(calendar());
    renderApp("/calendar/Work?week=2026-W46");
    await title();
    const asked = fetch.mock.calls.map(([r]) => new URL((r as Request).url)).find((u) => u.pathname === "/api/vaults/Work/calendar");
    expect(asked?.searchParams.get("week")).toBe("2026-W46");
  });

  it("stacks the days in one list a screen reader reads as a document", async () => {
    api(calendar({ "2026-11-11": [certificate], "2026-11-12": [dentist, entry("Standup"), entry("Release")] }));
    renderApp("/calendar/Work");
    await title();
    const headings = within(screen.getByRole("region", { name: "Week 46" })).getAllByRole("heading", { level: 2 });
    expect(headings.map((h) => h.textContent)).toEqual([
      "Monday, 9 November",
      "Tuesday, 10 November",
      "Wednesday, 11 November",
      "Thursday, 12 November",
      "Friday, 13 November",
      "Saturday, 14 November",
      "Sunday, 15 November",
    ]);
    expect(headings[2]).toHaveAccessibleName("Wednesday, 11 November, 1 entry, 1 overdue");
    expect(headings[3]).toHaveAccessibleName("Thursday, 12 November, 3 entries");
    expect(within(screen.getByRole("list", { name: "Thursday, 12 November" })).getAllByRole("listitem")).toHaveLength(3);
  });

  it("invites in an empty week with the spec's sentence", async () => {
    api(calendar());
    renderApp("/calendar/Work");
    await title();
    expect(screen.getAllByText(EMPTY)).toHaveLength(1);
  });

  it("says nothing of an empty week once something is on", async () => {
    api(calendar({ "2026-11-12": [dentist] }));
    renderApp("/calendar/Work");
    await title();
    expect(screen.queryByText(EMPTY)).not.toBeInTheDocument();
  });

  it("a to-do has a tick and Later, a note given a day only its title", async () => {
    const fetch = api(calendar({ "2026-11-11": [certificate], "2026-11-12": [dentist] }), {
      "POST /api/vaults/Work/calendar/close": () => json(200, written("/Work/Certificate.md")),
    });
    renderApp("/calendar/Work");
    await title();
    expect(screen.getByRole("link", { name: "Certificate" })).toHaveAttribute("href", "/notes/Work/Certificate.md");
    expect(screen.getByRole("link", { name: "Zahnarzt" })).toHaveAttribute("href", "/notes/Work/Zahnarzt.md");
    expect(screen.getByRole("button", { name: "Later: Certificate" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Done: Zahnarzt" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Later: Zahnarzt" })).not.toBeInTheDocument();
    const zahnarzt = screen.getByRole("link", { name: "Zahnarzt" }).closest("li")!;
    expect(zahnarzt).toHaveTextContent("14:00");
    expect(zahnarzt).toHaveTextContent("upcoming · in 1 day");

    await userEvent.click(screen.getByRole("button", { name: "Done: Certificate" }));
    await waitFor(async () => expect(await bodies(fetch, "POST", "/api/vaults/Work/calendar/close")).toEqual([{ path: "/Work/Certificate.md" }]));
    // The same short message as the note's header.
    expect(await screen.findByRole("status", { name: "Closed" })).toHaveTextContent("Closed · Undo · Add a reason");
  });

  it("Later in the week moves a to-do on", async () => {
    const fetch = api(calendar({ "2026-11-11": [certificate] }), {
      "POST /api/vaults/Work/calendar/later": () => json(200, written("/Work/Certificate.md")),
    });
    renderApp("/calendar/Work");
    await title();
    await userEvent.click(screen.getByRole("button", { name: "Later: Certificate" }));
    expect(screen.getAllByRole("menuitem").map((i) => i.textContent)).toEqual(["+1 day", "+1 week", "Pick a date…"]);
    await userEvent.click(screen.getByRole("menuitem", { name: "+1 day" }));
    await waitFor(async () =>
      expect(await bodies(fetch, "POST", "/api/vaults/Work/calendar/later")).toEqual([{ path: "/Work/Certificate.md", part: "postpone", step: "day" }]),
    );
  });

  it("overdue is said calmly with an icon and the word", async () => {
    api(calendar({ "2026-11-11": [certificate] }));
    renderApp("/calendar/Work");
    await title();
    const item = screen.getByRole("link", { name: "Certificate" }).closest("li")!;
    const state = within(item).getByText("overdue · 1 day");
    expect(state.closest(".text-warning")).not.toBeNull();
    expect(state.closest(".text-warning")!.querySelector("svg")).not.toBeNull();
    expect(document.querySelector('[class*="bg-red"], [class*="bg-warning"]')).toBeNull();
  });

  it("the day's plus creates an entry with a time", async () => {
    const fetch = api(calendar(), {
      "POST /api/vaults/Work/calendar/entries": () => json(201, written("/Work/Calendar/Zahnarzt.md")),
    });
    const router = renderApp("/calendar/Work");
    await title();
    await userEvent.click(screen.getByRole("button", { name: "Add something on Thursday, 12 November" }));
    const dialog = await screen.findByRole("dialog", { name: "Add something on Thursday, 12 November" });
    expect(within(dialog).queryByText(/folder/i)).not.toBeInTheDocument();
    await userEvent.type(within(dialog).getByLabelText("Title"), "Zahnarzt");
    await userEvent.type(within(dialog).getByLabelText("Time"), "14:00");
    await userEvent.click(within(dialog).getByRole("button", { name: "Add" }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/Calendar/Zahnarzt.md"));
    expect(await bodies(fetch, "POST", "/api/vaults/Work/calendar/entries")).toEqual([{ day: "2026-11-12", title: "Zahnarzt", time: "14:00", task: false }]);
    expect(useEditor.getState().mode).toBe("write");
  });

  it("To do reveals Show early", async () => {
    const fetch = api(calendar(), {
      "POST /api/vaults/Work/calendar/entries": () => json(201, written("/Work/Calendar/Certificate.md")),
    });
    renderApp("/calendar/Work");
    await title();
    await userEvent.click(screen.getByRole("button", { name: "Add something on Friday, 13 November" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).queryByText(/Show early/)).not.toBeInTheDocument();
    await userEvent.type(within(dialog).getByLabelText("Title"), "Certificate");
    await userEvent.click(within(dialog).getByRole("checkbox", { name: "To do" }));
    const early = within(dialog).getByRole("spinbutton", { name: "Show early: days before" });
    await userEvent.type(early, "7");
    await userEvent.click(within(dialog).getByRole("button", { name: "Add" }));

    await waitFor(async () =>
      expect(await bodies(fetch, "POST", "/api/vaults/Work/calendar/entries")).toEqual([{ day: "2026-11-13", title: "Certificate", task: true, upcoming: "7d" }]),
    );
  });

  it("Today opens or creates the day note", async () => {
    const fetch = api(calendar(), {
      "POST /api/vaults/Work/calendar/today": () => json(200, written("/Work/Calendar/2026-11-13.md")),
    });
    const router = renderApp("/calendar/Work");
    await title();
    await userEvent.click(screen.getByRole("button", { name: "Today" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/Calendar/2026-11-13.md"));
    expect(await bodies(fetch, "POST", "/api/vaults/Work/calendar/today")).toHaveLength(1);
    expect(useEditor.getState().mode).toBe("write");
  });

  it("This week opens or creates the week note", async () => {
    const fetch = api(calendar(), {
      "POST /api/vaults/Work/calendar/this-week": () => json(200, written("/Work/Calendar/2026-W46.md")),
    });
    const router = renderApp("/calendar/Work");
    await title();
    await userEvent.click(screen.getByRole("button", { name: "This week" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/Calendar/2026-W46.md"));
    expect(await bodies(fetch, "POST", "/api/vaults/Work/calendar/this-week")).toHaveLength(1);
  });

  it("the view switch keeps the person's choice", async () => {
    const fetch = api(calendar(), {
      "PUT /api/vaults/Work/calendar/view": () => json(200, { view: "month", grid: false }),
    });
    const router = renderApp("/calendar/Work");
    await title();
    const views = screen.getByRole("group", { name: "View" });
    expect(within(views).getByRole("button", { name: "Week" })).toHaveAttribute("aria-pressed", "true");
    await userEvent.click(within(views).getByRole("button", { name: "Month" }));
    await waitFor(async () => expect(await bodies(fetch, "PUT", "/api/vaults/Work/calendar/view")).toEqual([{ view: "month", grid: false }]));
    expect(router.state.location.search).toBe("?month=2026-11");
  });

  it("a read-only member sees no tick, Later or plus", async () => {
    api(calendar({ "2026-11-11": [certificate] }, { canWrite: false }));
    renderApp("/calendar/Work");
    await title();
    expect(screen.getByRole("link", { name: "Certificate" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Done: Certificate" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Later: Certificate" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Add something on/ })).not.toBeInTheDocument();
  });

  it("shows entries over more than a day above the days, with their span", async () => {
    const holiday = entry("Urlaub", { when: "2026-11-03", until: "2026-11-14", firstDay: "2026-11-03", lastDay: "2026-11-14", spans: true });
    const view = calendar();
    view.weeks[0].spanning = [holiday];
    api(view);
    renderApp("/calendar/Work");
    await title();
    const spanning = screen.getByRole("list", { name: "More than a day" });
    expect(within(spanning).getByRole("listitem")).toHaveTextContent("Urlaub");
    expect(within(spanning).getByRole("listitem")).toHaveTextContent("Tue 3 – Sat 14");
    // Something is on: the empty week's line is gone.
    expect(screen.queryByText(EMPTY)).not.toBeInTheDocument();
  });
});
