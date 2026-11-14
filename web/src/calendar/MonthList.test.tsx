// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { renderApp } from "../test/app";
import { bodiesOf, calendarApp, calendarOf, calendarQueries, entryView, november, openTask, week46 } from "../test/calendar";
import { FakeSocket } from "../test/socket";

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
  delete (Element.prototype as Partial<Element>).scrollIntoView;
});

/** jsdom has no scrolling: the ids of the elements scrolled into view, in order. */
function watchScrolls() {
  const scrolled: string[] = [];
  Element.prototype.scrollIntoView = function (this: Element) {
    scrolled.push(this.id);
  };
  return scrolled;
}

const dentist = entryView("Zahnarzt", { when: "2026-11-03T14:00", time: "14:00", state: "past", firstDay: "2026-11-03", lastDay: "2026-11-03" });
const certificate = openTask("Certificate", { when: "2026-11-06", state: "overdue", span: "1 week", firstDay: "2026-11-06", lastDay: "2026-11-06" });
const planning = entryView("Release planning", { when: "2026-11-15", state: "upcoming", span: "in 2 days", firstDay: "2026-11-15", lastDay: "2026-11-15" });
const templates = entryView("Look at calendar templates again", { when: "2026-W47", firstDay: "2026-11-16", lastDay: "2026-11-22", state: "upcoming" });

/** November 2026 as the spec's example has it: week 48 empty, week 47 with only a week entry. */
const month = () =>
  calendarOf(november, { "2026-11-03": [dentist], "2026-11-06": [certificate], "2026-11-15": [planning] }, {}, {}, { 47: [templates] });

/** The calendar for each query: the month when asked for one, else week 46. */
const answer = (query: URLSearchParams) => (query.get("month") ? month() : calendarOf(week46, { "2026-11-15": [planning] }));

const monthTitle = () => screen.findByRole("heading", { level: 1, name: "November 2026" });
const weekRegion = (name: string) => screen.getByRole("region", { name: new RegExp(`^${name}`) });

describe("the calendar's month", () => {
  it("the month is a stack of weeks with empty days left out", async () => {
    calendarApp(answer);
    renderApp("/calendar/Work?month=2026-11");
    await monthTitle();
    const weeks = screen.getAllByRole("heading", { level: 2 });
    expect(weeks.map((h) => h.textContent)).toEqual([
      "Week 44 · 26 Oct – 1 Nov",
      "Week 45 · 2–8 Nov",
      "Week 46 · 9–15 Novthis week",
      "Week 47 · 16–22 Nov",
      "Week 48 · 23–29 Nov",
      "Week 49 · 30 Nov – 6 Dec",
    ]);
    const week45 = weekRegion("Week 45");
    const days = within(week45).getAllByRole("heading", { level: 3 });
    expect(days.map((d) => d.textContent)).toEqual(["Tue 3", "Fri 6"]);
    // A screen reader hears the day in full, with its count and what is overdue.
    expect(days[1]).toHaveAccessibleName("Friday, 6 November, 1 entry, 1 overdue");
    expect(within(week45).getByRole("list", { name: "Tuesday, 3 November" })).toHaveTextContent("Zahnarzt");
    expect(within(week45).getByRole("list", { name: "Tuesday, 3 November" })).toHaveTextContent("14:00");
    // A week entry stands above the days; the week has no day of its own.
    const week47 = weekRegion("Week 47");
    expect(within(week47).getByRole("list", { name: "More than a day" })).toHaveTextContent("Look at calendar templates again");
    expect(within(week47).queryAllByRole("heading", { level: 3 })).toHaveLength(0);
    // The current week is marked.
    expect(within(weekRegion("Week 46")).getByRole("heading", { level: 2 })).toHaveTextContent("this week");
    expect(within(week45).getByRole("heading", { level: 2 })).not.toHaveTextContent("this week");
  });

  it("an empty week says nothing yet", async () => {
    calendarApp(answer);
    renderApp("/calendar/Work?month=2026-11");
    await monthTitle();
    expect(within(weekRegion("Week 48")).getByText("nothing yet")).toBeInTheDocument();
    expect(within(weekRegion("Week 47")).queryByText("nothing yet")).not.toBeInTheDocument();
    expect(within(weekRegion("Week 45")).queryByText("nothing yet")).not.toBeInTheDocument();
  });

  it("a week heading opens that week", async () => {
    calendarApp(answer);
    const router = renderApp("/calendar/Work?month=2026-11");
    await monthTitle();
    const link = screen.getByRole("link", { name: "Week 47 · 16–22 Nov" });
    expect(link).toHaveAttribute("href", "/calendar/Work?week=2026-W47");
    await userEvent.click(link);
    await waitFor(() => expect(router.state.location.search).toBe("?week=2026-W47"));
  });

  it("Show grid adds a month grid with the weekend darkened and is remembered", async () => {
    const fetch = calendarApp(answer, { view: "month", grid: false });
    renderApp("/calendar/Work?month=2026-11");
    await monthTitle();
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
    const show = screen.getByRole("button", { name: "Show grid" });
    expect(show).toHaveAttribute("aria-pressed", "false");
    await userEvent.click(show);

    const grid = await screen.findByRole("table", { name: "November 2026" });
    expect(show).toHaveAttribute("aria-pressed", "true");
    await waitFor(async () => expect(await bodiesOf(fetch, "PUT", "/api/vaults/Work/calendar/view")).toEqual([{ view: "month", grid: true }]));
    const rows = within(grid).getAllByRole("row");
    // A heading row, then a row per week with its number first.
    expect(rows).toHaveLength(7);
    const columns = within(rows[0]).getAllByRole("columnheader");
    expect(columns[0]).toHaveAccessibleName("Week");
    expect(columns.slice(1).map((c) => c.textContent)).toEqual(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]);
    expect(within(rows[3]).getByRole("rowheader")).toHaveTextContent("46");
    const cells = within(rows[3]).getAllByRole("cell");
    expect(cells.map((c) => c.classList.contains("bg-surface-2"))).toEqual([false, false, false, false, false, true, true]);
    // Days with entries are marked in words too; today is today.
    expect(within(grid).getByRole("button", { name: "3, has entries" })).toBeInTheDocument();
    expect(within(grid).getByRole("button", { name: "4" })).toBeInTheDocument();
    expect(within(grid).getByRole("button", { name: "13" })).toHaveAttribute("aria-current", "date");
    // The days of other months are left blank: 30 November is a Monday.
    expect(within(rows[1]).getAllByRole("button").map((b) => b.textContent)).toEqual(["1"]);
    expect(within(rows[6]).getAllByRole("button").map((b) => b.textContent)).toEqual(["30"]);

    // Hiding it is remembered too.
    await userEvent.click(show);
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
    await waitFor(async () =>
      expect(await bodiesOf(fetch, "PUT", "/api/vaults/Work/calendar/view")).toEqual([
        { view: "month", grid: true },
        { view: "month", grid: false },
      ]),
    );
  });

  it("a day in the grid jumps the list to it", async () => {
    const scrolled = watchScrolls();
    calendarApp(answer, { view: "month", grid: true });
    renderApp("/calendar/Work?month=2026-11");
    await monthTitle();
    const grid = await screen.findByRole("table", { name: "November 2026" });
    await userEvent.click(within(grid).getByRole("button", { name: "6, has entries" }));
    expect(scrolled.at(-1)).toBe("day-2026-11-06");
    expect(document.activeElement).toHaveTextContent("Fri 6");
    // A day with nothing on is left out of the list: its week is shown.
    await userEvent.click(within(grid).getByRole("button", { name: "25" }));
    expect(scrolled.at(-1)).toBe("week-2026-W48");
  });

  it("in the week view, the grid's day opens its week", async () => {
    const fetch = calendarApp(answer, { view: "week", grid: true });
    const router = renderApp("/calendar/Work");
    await screen.findByRole("heading", { level: 1, name: "Week 46 · 9–15 November 2026" });
    const grid = await screen.findByRole("table", { name: "November 2026" });
    expect(within(grid).getByRole("button", { name: "15, has entries" })).toBeInTheDocument();
    expect(calendarQueries(fetch)).toContainEqual({ month: "2026-11" });
    await userEvent.click(within(grid).getByRole("button", { name: "25" }));
    await waitFor(() => expect(router.state.location.search).toBe("?week=2026-W48"));
  });

  it("the last view opens next time", async () => {
    const fetch = calendarApp(answer, { view: "month", grid: true });
    const router = renderApp("/calendar/Work");
    await monthTitle();
    expect(router.state.location.search).toBe("?month=2026-11");
    expect(await screen.findByRole("table", { name: "November 2026" })).toBeInTheDocument();
    expect(calendarQueries(fetch)).toContainEqual({ month: "2026-11" });
    const views = screen.getByRole("group", { name: "View" });
    expect(within(views).getByRole("button", { name: "Month" })).toHaveAttribute("aria-pressed", "true");
  });

  it("the week stays the default when nothing is remembered", async () => {
    calendarApp(answer);
    const router = renderApp("/calendar/Work");
    await screen.findByRole("heading", { level: 1, name: "Week 46 · 9–15 November 2026" });
    expect(router.state.location.search).toBe("");
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
  });

  it("the view switch keeps the grid as it is", async () => {
    const fetch = calendarApp(answer, { view: "week", grid: true });
    const router = renderApp("/calendar/Work");
    await screen.findByRole("table", { name: "November 2026" });
    await userEvent.click(within(screen.getByRole("group", { name: "View" })).getByRole("button", { name: "Month" }));
    await waitFor(() => expect(router.state.location.search).toBe("?month=2026-11"));
    await waitFor(async () => expect(await bodiesOf(fetch, "PUT", "/api/vaults/Work/calendar/view")).toEqual([{ view: "month", grid: true }]));
  });
});
