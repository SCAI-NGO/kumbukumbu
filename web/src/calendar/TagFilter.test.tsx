// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { calendarApp, calendarOf, calendarQueries, entryView, november, week46 } from "../test/calendar";
import { FakeSocket } from "../test/socket";
import type { EntryView } from "./api";

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

const report = entryView("Weekly report", { when: "2026-11-13", firstDay: "2026-11-13", lastDay: "2026-11-13" });
const dentist = entryView("Zahnarzt", { when: "2026-11-12T14:00", time: "14:00" });

/** Only the report carries #kumbu. */
const answer = (query: URLSearchParams) => {
  const on: Record<string, EntryView[]> = query.get("tag") === "kumbu" ? { "2026-11-13": [report] } : { "2026-11-12": [dentist], "2026-11-13": [report] };
  return calendarOf(query.get("month") ? november : week46, on);
};

const filter = async (tag: string) => {
  const search = screen.getByRole("search", { name: "Tag filter" });
  await userEvent.type(within(search).getByRole("textbox", { name: "Tag" }), tag);
  await userEvent.click(within(search).getByRole("button", { name: "Filter" }));
};

describe("the tag filter", () => {
  it("the tag filter narrows the week, the month and What's on", async () => {
    const fetch = calendarApp(answer);
    const router = renderApp("/calendar/Work");
    await screen.findByRole("link", { name: "Zahnarzt" });

    await filter("#kumbu");
    await waitFor(() => expect(router.state.location.search).toBe("?tag=kumbu"));
    await waitFor(() => expect(screen.queryByRole("link", { name: "Zahnarzt" })).not.toBeInTheDocument());
    expect(screen.getByRole("link", { name: "Weekly report" })).toBeInTheDocument();
    expect(calendarQueries(fetch).at(-1)).toEqual({ tag: "kumbu" });
    // The next week keeps the filter.
    expect(screen.getByRole("link", { name: "Next week" })).toHaveAttribute("href", "/calendar/Work?week=2026-W47&tag=kumbu");

    await userEvent.click(within(screen.getByRole("group", { name: "View" })).getByRole("button", { name: "Month" }));
    await waitFor(() => expect(router.state.location.search).toBe("?month=2026-11&tag=kumbu"));
    await screen.findByRole("heading", { level: 1, name: "November 2026" });
    expect(calendarQueries(fetch).at(-1)).toEqual({ month: "2026-11", tag: "kumbu" });
    expect(screen.getByRole("link", { name: "Week 47 · 16–22 Nov" })).toHaveAttribute("href", "/calendar/Work?week=2026-W47&tag=kumbu");

    // The chip shows the tag and takes it away again.
    await userEvent.click(screen.getByRole("button", { name: "#kumbu (show everything)" }));
    await waitFor(() => expect(router.state.location.search).toBe("?month=2026-11"));
    expect(calendarQueries(fetch).at(-1)).toEqual({ month: "2026-11" });
  });

  it("reads the tag from the address", async () => {
    const fetch = calendarApp(answer);
    renderApp("/calendar/Work?week=2026-W46&tag=kumbu");
    await screen.findByRole("link", { name: "Weekly report" });
    expect(calendarQueries(fetch)).toEqual([{ week: "2026-W46", tag: "kumbu" }]);
    expect(screen.getByRole("button", { name: "#kumbu (show everything)" })).toBeInTheDocument();
  });

  it("narrows What's on", async () => {
    const asked: (string | null)[] = [];
    const fetch = fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null }),
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 4, embeddings: "internal", canWrite: true, level: "owner" }]),
      "GET /api/calendar/whats-on": (r) => {
        const query = new URL(r.url).searchParams;
        if (query.get("count") !== "true") asked.push(query.get("tag"));
        const items = query.get("tag") === "kumbu" ? [report] : [dentist, report];
        return json(200, { items, count: items.length, overdue: false, vaults: [], couldntRead: 0, couldntReadVaults: [] });
      },
    });
    const router = renderApp("/whats-on");
    await screen.findByRole("link", { name: "Zahnarzt" });
    await filter("kumbu");
    await waitFor(() => expect(router.state.location.search).toBe("?tag=kumbu"));
    await waitFor(() => expect(screen.queryByRole("link", { name: "Zahnarzt" })).not.toBeInTheDocument());
    expect(asked).toEqual([null, "kumbu"]);
    expect(fetch).toHaveBeenCalled();
  });
});
