// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { json, renderApp } from "../test/app";
import { calendarApp, calendarOf, entryView, november, week46 } from "../test/calendar";
import { dayShade } from "./dayLevels";
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
});

const { lighter: LIGHTER, off: OFF } = dayShade;

const FOUR_DAYS = ["workday", "workday", "workday", "workday", "lighter", "off", "off"];

const hike = entryView("Hike", { when: "2026-11-14", firstDay: "2026-11-14", lastDay: "2026-11-14", state: "upcoming", span: "in 1 day" });

/** Work's calendar, the person's week `days` (left out: never chosen). */
const app = (answer: Parameters<typeof calendarApp>[0], days?: string[], view?: Parameters<typeof calendarApp>[1]) =>
  calendarApp(answer, view, {
    "GET /api/account/calendar": () => json(200, days ? { zone: "Europe/Vienna", serverZone: "UTC", days } : { zone: "Europe/Vienna", serverZone: "UTC" }),
  });

/** The section of a day in the week, by its heading. */
const day = (name: string) => screen.getByRole("heading", { level: 2, name: new RegExp(`^${name}`) }).closest("section")!;

describe("days off", () => {
  it("days off are shaded in the week, lighter days lightly, from the stored choice", async () => {
    app(() => calendarOf(week46, { "2026-11-14": [hike] }), FOUR_DAYS);
    renderApp("/calendar/Work");
    await screen.findByRole("heading", { level: 1, name: "Week 46 · 9–15 November 2026" });
    await waitFor(() => expect(day("Friday, 13 November")).toHaveClass(LIGHTER));
    expect(day("Friday, 13 November")).toHaveTextContent("Lighter day");
    const saturday = day("Saturday, 14 November");
    expect(saturday).toHaveClass(OFF);
    expect(saturday).toHaveTextContent("Day off");
    expect(day("Monday, 9 November")).not.toHaveClass(OFF);
    expect(day("Monday, 9 November")).not.toHaveClass(LIGHTER);
    expect(day("Monday, 9 November")).not.toHaveTextContent(/Day off|Lighter day/);
    // Its entries and its + are all there.
    expect(within(saturday).getByRole("link", { name: /Hike/ })).toBeInTheDocument();
    expect(within(saturday).getByRole("button", { name: "Add something on Saturday, 14 November" })).toBeInTheDocument();
  });

  it("without a choice, the browser's weekend is shaded", async () => {
    app(() => calendarOf(week46));
    renderApp("/calendar/Work");
    await screen.findByRole("heading", { level: 1, name: "Week 46 · 9–15 November 2026" });
    expect(day("Saturday, 14 November")).toHaveClass(OFF);
    expect(day("Saturday, 14 November")).toHaveTextContent("Day off");
    expect(day("Sunday, 15 November")).toHaveTextContent("Day off");
    expect(day("Friday, 13 November")).not.toHaveClass(LIGHTER);
    expect(day("Friday, 13 November")).not.toHaveTextContent(/Day off|Lighter day/);
  });

  it("the month list and the grid shade the same days", async () => {
    const lighterMonday = ["lighter", "workday", "workday", "workday", "workday", "off", "off"];
    app(() => calendarOf(november, { "2026-11-14": [hike] }), lighterMonday, { view: "month", grid: true });
    renderApp("/calendar/Work?month=2026-11");
    const grid = await screen.findByRole("table", { name: "November 2026" });
    const row = within(grid).getAllByRole("row")[3];
    await waitFor(() => expect(within(row).getAllByRole("cell")[0]).toHaveClass(LIGHTER));
    expect(within(row).getAllByRole("cell").map((c) => c.classList.contains(OFF))).toEqual([false, false, false, false, false, true, true]);
    // The grid says it in words too.
    expect(within(row).getByRole("button", { name: "9, Lighter day" })).toBeInTheDocument();
    expect(within(row).getByRole("button", { name: "14, Day off, has entries" })).toBeInTheDocument();
    expect(within(row).getByRole("button", { name: "10" })).toBeInTheDocument();
    const saturday = screen.getByRole("heading", { level: 3, name: /^Saturday, 14 November/ }).closest("section")!;
    expect(saturday).toHaveClass(OFF);
    expect(saturday).toHaveTextContent("Day off");
    expect(within(saturday).getByRole("link", { name: /Hike/ })).toBeInTheDocument();
  });
});
