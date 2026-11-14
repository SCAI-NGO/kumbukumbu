// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { bodiesOf, entryView, openTask, writtenView } from "../test/calendar";
import { FakeSocket } from "../test/socket";
import type { EntryView, WhatsOnView } from "./api";

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

const certificate = openTask("Certificate", { when: "2026-10-23", state: "overdue", span: "3 weeks", firstDay: "2026-10-23", lastDay: "2026-10-23" });
const trip = entryView("Trip", { path: "/Home/Trip.md", vault: "Home", when: "2026-11-12", until: "2026-11-20", state: "ongoing", span: "until Fri", spans: true, lastDay: "2026-11-20" });
const dentist = entryView("Zahnarzt", { when: "2026-11-20T14:00", time: "14:00", state: "upcoming", span: "in 7 days", firstDay: "2026-11-20", lastDay: "2026-11-20" });

const whatsOn = (items: EntryView[], extra: Partial<WhatsOnView> = {}): WhatsOnView => ({
  items,
  count: items.length,
  overdue: items.some((i) => i.state === "overdue"),
  vaults: [],
  couldntRead: 0,
  couldntReadVaults: [],
  ...extra,
});

const vault = (name: string, level = "owner") => ({ name, notes: 4, embeddings: "internal", canWrite: level !== "read", level });

/** What's on answers `view`, and `count` for the badge (by default the same, without the items). */
function api(view: WhatsOnView | (() => WhatsOnView), extra: Parameters<typeof fakeApi>[0] = {}, vaults = [vault("Work"), vault("Home")]) {
  const answer = () => (typeof view === "function" ? view() : view);
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, vaults),
    "GET /api/calendar/whats-on": (r) => (new URL(r.url).searchParams.get("count") === "true" ? json(200, { ...answer(), items: [] }) : json(200, answer())),
    ...extra,
  });
}

const page = () => screen.findByRole("heading", { level: 1, name: "What's on" });

describe("What's on", () => {
  it("lists what's on with vault and span", async () => {
    api(whatsOn([certificate, trip, dentist]));
    renderApp("/whats-on");
    await page();
    const items = await screen.findAllByRole("listitem");
    expect(items.map((i) => within(i).getByRole("link").textContent)).toEqual(["Certificate", "Trip", "Zahnarzt"]);
    const [first, second, third] = items;
    expect(within(first).getByRole("link", { name: "Certificate" })).toHaveAttribute("href", "/notes/Work/Certificate.md");
    expect(first).toHaveTextContent("Work");
    expect(first).toHaveTextContent("To do");
    // Overdue is said calmly: the word and its span in the warning colour beside an icon, never a red box.
    const overdue = within(first).getByText("overdue · 3 weeks");
    expect(overdue.closest(".text-warning")).not.toBeNull();
    expect(document.querySelector('[class*="bg-red"], [class*="bg-warning"]')).toBeNull();
    expect(second).toHaveTextContent("Home");
    expect(second).toHaveTextContent("ongoing · until Fri");
    expect(third).toHaveTextContent("upcoming · in 7 days");
    // An open to-do has ✓ and Later; a note given a day only its title.
    expect(within(first).getByRole("button", { name: "Done: Certificate" })).toBeInTheDocument();
    expect(within(first).getByRole("button", { name: "Later: Certificate" })).toBeInTheDocument();
    expect(within(third).queryByRole("button")).toBeNull();
    expect(document.body.textContent).not.toMatch(/problem/i);
  });

  it("closes an open to-do at once and asks again", async () => {
    let closed = false;
    const fetch = api(() => whatsOn(closed ? [dentist] : [certificate, dentist]), {
      "POST /api/vaults/Work/calendar/close": () => {
        closed = true;
        return json(200, writtenView(certificate.path, { ...certificate, task: "closed", state: "done" }));
      },
    });
    renderApp("/whats-on");
    await page();
    await userEvent.click(await screen.findByRole("button", { name: "Done: Certificate" }));
    expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/close")).toEqual([{ path: certificate.path }]);
    expect(await screen.findByRole("status", { name: "Closed" })).toHaveTextContent("Closed · Undo · Add a reason");
    await waitFor(() => expect(screen.queryByRole("link", { name: "Certificate" })).not.toBeInTheDocument());
  });

  it("a read-only member has no tick or Later in a vault shared with her", async () => {
    api(whatsOn([openTask("Certificate", { path: "/Home/Certificate.md", vault: "Home" })]), {}, [vault("Work"), vault("Home", "read")]);
    renderApp("/whats-on");
    await page();
    await screen.findByRole("link", { name: "Certificate" });
    await waitFor(() => expect(screen.queryByRole("button", { name: "Done: Certificate" })).not.toBeInTheDocument());
    expect(screen.queryByRole("button", { name: "Later: Certificate" })).not.toBeInTheDocument();
  });

  it("says Nothing on. Enjoy it.", async () => {
    api(whatsOn([]));
    renderApp("/whats-on");
    await page();
    expect(await screen.findByText("Nothing on. Enjoy it.")).toBeInTheDocument();
    expect(screen.queryByRole("list")).not.toBeInTheDocument();
  });

  it("names the dates it couldn't read at the bottom, with a link to them", async () => {
    api(whatsOn([dentist], { couldntRead: 2, couldntReadVaults: ["Home", "Work"] }));
    renderApp("/whats-on");
    await page();
    const link = await screen.findByRole("link", { name: "2 dates I couldn't read" });
    expect(link).toHaveAttribute("href", "/calendar/Home#couldnt-read");
    expect(link).toHaveClass("min-h-12");
    expect(document.body.textContent).not.toMatch(/problem/i);
  });

  it("one date it couldn't read is one", async () => {
    api(whatsOn([], { couldntRead: 1, couldntReadVaults: ["Work"] }));
    renderApp("/whats-on");
    await page();
    expect(await screen.findByRole("link", { name: "1 date I couldn't read" })).toHaveAttribute("href", "/calendar/Work#couldnt-read");
  });

  it("says nothing of dates it couldn't read when there are none", async () => {
    api(whatsOn([dentist]));
    renderApp("/whats-on");
    await page();
    await screen.findByRole("link", { name: "Zahnarzt" });
    expect(screen.queryByText(/couldn't read/)).not.toBeInTheDocument();
  });

  it("says how Later gave more time, and Undo gives back the old end", async () => {
    const report = openTask("Report", { when: "2026-11-09", until: "2026-11-13", state: "ongoing", firstDay: "2026-11-09", lastDay: "2026-11-13", spans: true });
    const fetch = api(whatsOn([report]), {
      "POST /api/vaults/Work/calendar/later": () => json(200, writtenView(report.path, { ...report, until: "2026-11-20", lastDay: "2026-11-20" })),
      "POST /api/vaults/Work/calendar/set": () => json(200, writtenView(report.path, report, "v3")),
    });
    renderApp("/whats-on");
    await page();
    await userEvent.click(await screen.findByRole("button", { name: "Later: Report" }));
    await userEvent.click(within(screen.getByRole("group", { name: "More time" })).getByRole("menuitem", { name: "+1 week" }));
    const toast = await screen.findByRole("status", { name: "Moved" });
    expect(toast).toHaveTextContent("More time until Fri 20 Nov · Undo");
    await userEvent.click(within(toast).getByRole("button", { name: "Undo" }));
    await waitFor(async () => expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/set")).toEqual([{ path: report.path, until: "2026-11-13" }]));
  });

  it("Undo after a card's Later gives it its old due through Later's own date", async () => {
    const card = entryView("Release", { path: "/Work/Board/Release.md", kind: "card", column: "In progress", when: "2026-11-12", state: "overdue" });
    const fetch = api(whatsOn([card]), {
      "POST /api/vaults/Work/calendar/later": () => json(200, writtenView(card.path, { ...card, when: "2026-11-19", firstDay: "2026-11-19", lastDay: "2026-11-19" })),
    });
    renderApp("/whats-on");
    await page();
    await userEvent.click(await screen.findByRole("button", { name: "Later: Release" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "+1 week" }));
    const toast = await screen.findByRole("status", { name: "Moved" });
    expect(toast).toHaveTextContent("Moved to Thu 19 Nov · Undo");
    await userEvent.click(within(toast).getByRole("button", { name: "Undo" }));
    await waitFor(async () =>
      expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/later")).toEqual([
        { path: card.path, part: "postpone", step: "week" },
        { path: card.path, part: "postpone", date: "2026-11-12" },
      ]),
    );
  });
});
