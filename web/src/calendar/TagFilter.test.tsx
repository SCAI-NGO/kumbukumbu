// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { hideAgain } from "../discreet/hideAgain";
import { useDiscreet } from "../discreet/state";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { calendarApp, calendarOf, calendarQueries, entryView, november, week46 } from "../test/calendar";
import { FakeSocket } from "../test/socket";
import type { EntryView } from "./api";
import { cameFromOf, keptTagState } from "./backLink";

beforeEach(() => {
  FakeSocket.all = [];
  vi.stubGlobal("WebSocket", FakeSocket);
});

afterEach(async () => {
  await waitFor(() => expect(useDiscreet.getState().hiding).toBe(false));
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  useDiscreet.setState({ gone: new Set() });
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

/** What's on of `Work` and, while `at.shown`, the discreet `Home`; `asked` the tags it was asked for. */
function whatsOnServer() {
  const at = { shown: true };
  const asked: (string | null)[] = [];
  const vault = (name: string, discreet = false) => ({ name, notes: 4, embeddings: "internal", canWrite: true, level: "owner", discreet });
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: at.shown, until: null } }),
    "GET /api/vaults": () => json(200, at.shown ? [vault("Work"), vault("Home", true)] : [vault("Work")]),
    "GET /api/calendar/whats-on": (r) => {
      const query = new URL(r.url).searchParams;
      if (query.get("count") !== "true") asked.push(query.get("tag"));
      const items = query.get("tag") === "kumbu" ? [report] : [dentist, report];
      return json(200, { items, count: items.length, overdue: false, vaults: [], couldntRead: 0, couldntReadVaults: [] });
    },
  });
  return { at, asked };
}

describe("the tag filter of What's on, while discreet vaults are shown", () => {
  it("while discreet vaults are shown, What's on narrows by tag and the address stays as it was", async () => {
    const { asked } = whatsOnServer();
    const router = renderApp("/whats-on");
    await screen.findByRole("link", { name: "Zahnarzt" });
    await waitFor(() => expect(useDiscreet.getState().revealed).toBe(true));
    await filter("kumbu");
    await waitFor(() => expect(screen.queryByRole("link", { name: "Zahnarzt" })).not.toBeInTheDocument());
    expect(screen.getByRole("link", { name: "Weekly report" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "#kumbu (show everything)" })).toBeInTheDocument();
    expect(asked.at(-1)).toBe("kumbu");
    expect(router.state.location.search).toBe("");
  });

  /** What's on, opened while shown, and the call that hides them again. */
  const openShown = async () => {
    const { at } = whatsOnServer();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/whats-on", queryClient);
    await screen.findByRole("link", { name: "Zahnarzt" });
    await waitFor(() => expect(useDiscreet.getState().revealed).toBe(true));
    const hide = async () => {
      at.shown = false;
      await act(() => hideAgain(queryClient, router.navigate, { tell: false, pathname: "/whats-on" }));
    };
    return { router, hide };
  };

  it("hidden again, What's on shows everything", async () => {
    const { router, hide } = await openShown();
    await filter("kumbu");
    await waitFor(() => expect(screen.queryByRole("link", { name: "Zahnarzt" })).not.toBeInTheDocument());
    await hide();
    expect(await screen.findByRole("link", { name: "Zahnarzt" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "#kumbu (show everything)" })).not.toBeInTheDocument();
    expect(router.state.location.search).toBe("");
  });

  it("hidden again, its Tag field is empty", async () => {
    const { hide } = await openShown();
    await userEvent.type(screen.getByRole("textbox", { name: "Tag" }), "plan");
    await hide();
    await waitFor(() => expect(screen.getByRole("textbox", { name: "Tag" })).toHaveValue(""));
  });

  it("a tag in the address before they were shown stays shown", async () => {
    const { asked } = whatsOnServer();
    const router = renderApp("/whats-on?tag=kumbu");
    await waitFor(() => expect(useDiscreet.getState().revealed).toBe(true));
    expect(await screen.findByRole("button", { name: "#kumbu (show everything)" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Zahnarzt" })).not.toBeInTheDocument();
    expect(asked.at(-1)).toBe("kumbu");
    // Taken away, it goes from the address too.
    await userEvent.click(screen.getByRole("button", { name: "#kumbu (show everything)" }));
    await screen.findByRole("link", { name: "Zahnarzt" });
    expect(router.state.location.search).toBe("");
  });

  it("a note opened from What's on, narrowed, takes the tag along on its way back", async () => {
    whatsOnServer();
    const router = renderApp("/whats-on");
    await screen.findByRole("link", { name: "Zahnarzt" });
    await waitFor(() => expect(useDiscreet.getState().revealed).toBe(true));
    await filter("kumbu");
    await waitFor(() => expect(screen.queryByRole("link", { name: "Zahnarzt" })).not.toBeInTheDocument());
    await userEvent.click(screen.getByRole("link", { name: "Weekly report" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/Weekly%20report.md"));
    expect(cameFromOf(router.state.location.state, "/Work/Weekly report.md")).toEqual({ label: "What's on", to: "/whats-on", tag: "kumbu" });
  });

  it("back on What's on with the tag it took along, it narrows by it again, the address untouched", async () => {
    const { asked } = whatsOnServer();
    const router = renderApp({ pathname: "/whats-on", state: keptTagState("kumbu") });
    await waitFor(() => expect(useDiscreet.getState().revealed).toBe(true));
    expect(await screen.findByRole("button", { name: "#kumbu (show everything)" })).toBeInTheDocument();
    expect(await screen.findByRole("link", { name: "Weekly report" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Zahnarzt" })).not.toBeInTheDocument();
    expect(asked.at(-1)).toBe("kumbu");
    expect(router.state.location.search).toBe("");
  });

  it("a tag taken along by an earlier page load isn't taken up", async () => {
    whatsOnServer();
    renderApp({ pathname: "/whats-on", state: { keptTag: { tag: "kumbu", load: "an earlier load" } } });
    await waitFor(() => expect(useDiscreet.getState().revealed).toBe(true));
    expect(await screen.findByRole("link", { name: "Zahnarzt" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "#kumbu (show everything)" })).not.toBeInTheDocument();
  });
});
