// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { json, renderApp } from "../test/app";
import { calendarApp, calendarOf, november, week46 } from "../test/calendar";
import { FakeSocket } from "../test/socket";
import type { CouldntReadView } from "./api";

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

const plan: CouldntReadView = {
  path: "/Work/Plan.md",
  title: "Plan",
  plain: "`when: tomorrow` – I need a date like 2026-11-20",
  readable: { until: "2026-11-30", task: "open", upcoming: "7d" },
};

const unread = (canWrite = true) => (query: URLSearchParams) =>
  calendarOf(query.get("month") ? november : week46, {}, { couldntRead: [plan], canWrite });

const weekTitle = () => screen.findByRole("heading", { level: 1, name: "Week 46 · 9–15 November 2026" });

describe("dates I couldn't read", () => {
  it("Dates I couldn't read lists the note with Fix…", async () => {
    calendarApp(unread());
    renderApp("/calendar/Work");
    await weekTitle();
    const section = screen.getByRole("region", { name: "Dates I couldn't read" });
    expect(section).toHaveAttribute("id", "couldnt-read");
    const line = within(section).getByRole("listitem");
    expect(line).toHaveTextContent("Plan — when: tomorrow – I need a date like 2026-11-20");
    expect(within(line).getByText("when: tomorrow").tagName).toBe("CODE");
    expect(within(line).getByRole("link", { name: "Plan" })).toHaveAttribute("href", "/notes/Work/Plan.md");
    expect(within(line).getByRole("button", { name: "Fix… Plan" })).toHaveTextContent("Fix…");
  });

  it("shows them in the month too", async () => {
    calendarApp(unread());
    renderApp("/calendar/Work?month=2026-11");
    await screen.findByRole("heading", { level: 1, name: "November 2026" });
    expect(screen.getByRole("region", { name: "Dates I couldn't read" })).toHaveTextContent("Plan");
  });

  it("Fix opens the date dialog filled with what could be read", async () => {
    calendarApp(unread(), undefined, {
      "GET /api/vaults/Work/calendar/entry": () => json(200, { entry: null, couldntRead: plan, canWrite: true }),
    });
    renderApp("/calendar/Work");
    await weekTitle();
    await userEvent.click(screen.getByRole("button", { name: "Fix… Plan" }));
    const dialog = await screen.findByRole("dialog");
    const until = within(dialog).getByRole("group", { name: /Until/ });
    expect(within(until).getByLabelText("Day")).toHaveValue("2026-11-30");
    expect(within(dialog).getByRole("checkbox", { name: "To do" })).toBeChecked();
    expect(within(dialog).getByRole("spinbutton", { name: "Show early: days before" })).toHaveValue(7);
    // When is what couldn't be read: left for the person to give.
    const when = within(dialog).getByRole("group", { name: "When" });
    expect(within(when).getByLabelText("Day")).toHaveValue("");
  });

  it("a read-only member sees the lines without Fix…", async () => {
    calendarApp(unread(false));
    renderApp("/calendar/Work");
    await weekTitle();
    expect(screen.getByRole("region", { name: "Dates I couldn't read" })).toHaveTextContent("Plan");
    expect(screen.queryByRole("button", { name: /^Fix/ })).not.toBeInTheDocument();
  });

  it("is left out when every date could be read", async () => {
    calendarApp((query) => calendarOf(query.get("month") ? november : week46));
    renderApp("/calendar/Work");
    await weekTitle();
    expect(screen.queryByRole("region", { name: "Dates I couldn't read" })).not.toBeInTheDocument();
  });

  it("What's on's link lands on them", async () => {
    const scrolled = watchScrolls();
    calendarApp(unread());
    renderApp("/calendar/Work#couldnt-read");
    await weekTitle();
    await waitFor(() => expect(scrolled).toContain("couldnt-read"));
  });

  it("the web app never says problems", async () => {
    calendarApp(unread());
    renderApp("/calendar/Work");
    await weekTitle();
    await screen.findByRole("region", { name: "Dates I couldn't read" });
    expect(document.body.textContent).not.toMatch(/problem/i);
    expect(document.body.innerHTML).not.toMatch(/aria-label="[^"]*problem/i);
  });
});
