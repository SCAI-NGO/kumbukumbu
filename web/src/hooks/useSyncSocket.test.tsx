// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, render, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useVersionPoll } from "../api/history";
import { routerFuture } from "../router";
import { useEditor } from "../state/editor";
import { useSyncStatus } from "../state/sync";
import { fakeApi, json } from "../test/app";
import { FakeSocket } from "../test/socket";
import { onDiscreetFrame, useSyncSocket } from "./useSyncSocket";

const last = () => FakeSocket.all.at(-1)!;
const ticketOf = (s: FakeSocket) => new URL(s.url).searchParams.get("ticket");

beforeEach(() => {
  FakeSocket.all = [];
  vi.stubGlobal("WebSocket", FakeSocket);
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  useSyncStatus.setState({ connected: false, editors: {} });
  useEditor.getState().reset();
});

/** The server: a new ticket for every request, and the vault list `vaults` holds now. */
function server(vaults: { name: string }[] = [{ name: "Work" }]) {
  let tickets = 0;
  const fetch = fakeApi({
    "POST /api/ws-ticket": () => json(200, { ticket: `t${++tickets}`, expiresAt: "2026-11-01T10:00:30Z" }),
    "GET /api/vaults": () => json(200, vaults),
    "GET /api/version/Work/plan.md": () => json(200, { path: "/Work/plan.md", version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-01T09:00:00Z" }),
  });
  const count = (method: string, path: string) =>
    fetch.mock.calls.filter(([r]) => (r as Request).method === method && new URL((r as Request).url).pathname === path).length;
  return { fetch, count, vaults };
}

function Shell({ poll }: { poll?: number }) {
  useSyncSocket();
  useVersionPoll("/Work/plan.md", poll !== undefined, poll);
  return null;
}

/** Inside the router, as the shell is: on `/`, or on the note at `at`. */
function mount(queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } }), poll?: number, at = "/") {
  const element = <Shell poll={poll} />;
  const router = createMemoryRouter(
    [
      { path: "/", element },
      { path: "/notes/*", element },
    ],
    { initialEntries: [at], future: routerFuture },
  );
  const view = render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} future={{ v7_startTransition: true }} />
    </QueryClientProvider>,
  );
  return { queryClient, view, router };
}

/** A second tab of the owner on `at`, its socket open. */
async function tabOn(at: string) {
  server([{ name: "Garten" }, { name: "Work" }]);
  const { router } = mount(undefined, undefined, `/notes/${at}`);
  await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
  last().open();
  return router;
}

const renamed = { type: "renamed", vault: "Garten", from: "Gartenarbeit" };

const invalidated = (queryClient: QueryClient, key: unknown[]) => queryClient.getQueryState(key)?.isInvalidated;

describe("the sync socket", () => {
  it("invalidates the tree on a hint, and the open note's version, but not its text", async () => {
    server();
    const queryClient = new QueryClient();
    for (const key of [["tree", "Work"], ["trash", "Work"], ["checkpoints", "Work"], ["history", "/Work/plan.md"], ["version", "/Work/plan.md"], ["note", "/Work/plan.md"], ["changes", null]]) {
      queryClient.setQueryData(key, {});
    }
    mount(queryClient);
    await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
    expect(last().url).toBe(`ws://${window.location.host}/api/ws?ticket=t1`);
    last().open();

    last().frame({ type: "changed", vault: "Work" });

    for (const key of [["tree", "Work"], ["trash", "Work"], ["checkpoints", "Work"], ["history", "/Work/plan.md"], ["version", "/Work/plan.md"], ["changes", null], ["vaults"]]) {
      expect(invalidated(queryClient, key), JSON.stringify(key)).not.toBe(false);
    }
    expect(invalidated(queryClient, ["note", "/Work/plan.md"])).toBe(false);
  });

  it("maps the caller's vault name: a hint for her alias touches that vault only, and frames it doesn't know nothing", async () => {
    server([{ name: "Work" }, { name: "Beet" }]);
    const queryClient = new QueryClient();
    queryClient.setQueryData(["tree", "Work"], {});
    queryClient.setQueryData(["tree", "Beet"], {});
    mount(queryClient);
    await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
    last().open();

    last().frame({ type: "presence", vault: "Work", path: "plan.md", editors: [] });
    last().frame({ type: "nonsense", vault: "Work" });
    expect(invalidated(queryClient, ["tree", "Work"])).toBe(false);

    last().frame({ type: "changed", vault: "Beet" });
    expect(invalidated(queryClient, ["tree", "Beet"])).toBe(true);
    expect(invalidated(queryClient, ["tree", "Work"])).toBe(false);
  });

  it("stops the version poll while connected and resumes it when the socket drops", async () => {
    const { count } = server();
    mount(undefined, 30);
    const polls = () => count("GET", "/api/version/Work/plan.md");
    await waitFor(() => expect(polls()).toBeGreaterThanOrEqual(2));
    await waitFor(() => expect(FakeSocket.all).toHaveLength(1));

    last().open();
    expect(useSyncStatus.getState().connected).toBe(true);
    await new Promise((r) => setTimeout(r, 60));
    const whileUp = polls();
    await new Promise((r) => setTimeout(r, 200));
    expect(polls()).toBe(whileUp);

    last().drop();
    expect(useSyncStatus.getState().connected).toBe(false);
    await waitFor(() => expect(polls()).toBeGreaterThanOrEqual(whileUp + 2));
  });

  it("reconnects with a fresh ticket after a drop, and asks again for what it may have missed", async () => {
    vi.useFakeTimers();
    const { count } = server();
    const queryClient = new QueryClient();
    queryClient.setQueryData(["tree", "Home"], {});
    mount(queryClient);
    await vi.advanceTimersByTimeAsync(0);
    expect(FakeSocket.all).toHaveLength(1);
    last().open();
    expect(invalidated(queryClient, ["tree", "Home"])).toBe(false);

    last().drop();
    await vi.advanceTimersByTimeAsync(999);
    expect(FakeSocket.all).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(count("POST", "/api/ws-ticket")).toBe(2);
    expect(FakeSocket.all).toHaveLength(2);
    expect(ticketOf(last())).toBe("t2");

    last().open();
    expect(invalidated(queryClient, ["tree", "Home"])).toBe(true);
  });

  it("stays down when a newer tab of the session replaced it", async () => {
    vi.useFakeTimers();
    const { count } = server();
    mount();
    await vi.advanceTimersByTimeAsync(0);
    last().open();

    last().drop(4409);
    await vi.advanceTimersByTimeAsync(60_000);

    expect(count("POST", "/api/ws-ticket")).toBe(1);
    expect(useSyncStatus.getState().connected).toBe(false);
  });

  it("connects again at once, with a fresh ticket, when the session hid its discreet vaults", async () => {
    vi.useFakeTimers();
    const { count } = server();
    const queryClient = new QueryClient();
    queryClient.setQueryData(["session"], {});
    mount(queryClient);
    await vi.advanceTimersByTimeAsync(0);
    last().open();
    // A drop before it: the back-off it built up doesn't count either.
    last().drop();
    await vi.advanceTimersByTimeAsync(1000);
    last().open();
    expect(invalidated(queryClient, ["session"])).toBe(true);
    queryClient.setQueryData(["session"], {});
    expect(invalidated(queryClient, ["session"])).toBe(false);

    last().drop(4412);
    await vi.advanceTimersByTimeAsync(0);

    expect(count("POST", "/api/ws-ticket")).toBe(3);
    expect(FakeSocket.all).toHaveLength(3);
    expect(ticketOf(last())).toBe("t3");
    // What the session shows has changed.
    expect(invalidated(queryClient, ["session"])).toBe(true);
  });

  it("backs off on a second close for hidden vaults within five seconds, and on one before the socket opened", async () => {
    vi.useFakeTimers();
    server();
    mount();
    await vi.advanceTimersByTimeAsync(0);
    last().open();
    last().drop(4412);
    await vi.advanceTimersByTimeAsync(0);
    expect(FakeSocket.all).toHaveLength(2);

    // Never opened: no loop of tickets against a server that closes every socket.
    last().drop(4412);
    await vi.advanceTimersByTimeAsync(0);
    expect(FakeSocket.all).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1000);
    expect(FakeSocket.all).toHaveLength(3);

    // Opened, but within five seconds of the last one.
    last().open();
    last().drop(4412);
    await vi.advanceTimersByTimeAsync(0);
    expect(FakeSocket.all).toHaveLength(3);
    await vi.advanceTimersByTimeAsync(1000);
    expect(FakeSocket.all).toHaveLength(4);

    // Five seconds on: at once again.
    await vi.advanceTimersByTimeAsync(5000);
    last().open();
    last().drop(4412);
    await vi.advanceTimersByTimeAsync(0);
    expect(FakeSocket.all).toHaveLength(5);
  });

  it("opens again when a vault it left out after a close for hidden vaults comes back", async () => {
    const { vaults } = server([{ name: "Tagebuch" }, { name: "Work" }]);
    const queryClient = new QueryClient();
    mount(queryClient);
    await waitFor(() => expect(queryClient.getQueryData(["vaults"])).toBeDefined());
    await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
    last().open();
    // The vault list is still the old one when the new ticket is asked for.
    last().drop(4412);
    await waitFor(() => expect(FakeSocket.all).toHaveLength(2));
    last().open();

    vaults.splice(0, 1);
    await act(() => queryClient.invalidateQueries({ queryKey: ["vaults"] }));
    await waitFor(() => expect(queryClient.getQueryData<{ name: string }[]>(["vaults"])).toHaveLength(1));
    expect(FakeSocket.all).toHaveLength(2);

    vaults.unshift({ name: "Tagebuch" });
    await act(() => queryClient.invalidateQueries({ queryKey: ["vaults"] }));

    await waitFor(() => expect(FakeSocket.all).toHaveLength(3));
    expect(ticketOf(last())).toBe("t3");
  });

  it("hands the frames about discreet vaults, which name no vault, to whoever registered for them", async () => {
    const seen = vi.fn();
    const stop = onDiscreetFrame(seen);
    try {
      server();
      const queryClient = new QueryClient();
      queryClient.setQueryData(["tree", "Work"], {});
      mount(queryClient);
      await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
      last().open();

      last().frame({ type: "hidden" });

      expect(seen).toHaveBeenCalledTimes(1);
      expect(seen.mock.calls[0][0]).toBe("hidden");
      expect(seen.mock.calls[0][1]).toBe(queryClient);
      expect(invalidated(queryClient, ["tree", "Work"])).toBe(false);

      stop();
      last().frame({ type: "hidden" });
      expect(seen).toHaveBeenCalledTimes(1);
    } finally {
      stop();
    }
  });

  it("asks for the session and the vault list again on a revealed frame", async () => {
    server();
    const queryClient = new QueryClient();
    queryClient.setQueryData(["session"], {});
    queryClient.setQueryData(["tree", "Work"], {});
    mount(queryClient);
    await waitFor(() => expect(queryClient.getQueryData(["vaults"])).toBeDefined());
    await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
    last().open();
    expect(invalidated(queryClient, ["session"])).toBe(false);

    last().frame({ type: "revealed" });

    expect(invalidated(queryClient, ["session"])).toBe(true);
    await waitFor(() => expect(invalidated(queryClient, ["vaults"])).not.toBe(false));
    expect(invalidated(queryClient, ["tree", "Work"])).toBe(false);
  });

  it("opens again with a new ticket when the vault list names a vault the ticket didn't hold", async () => {
    const { vaults } = server();
    const queryClient = new QueryClient();
    mount(queryClient);
    await waitFor(() => expect(queryClient.getQueryData(["vaults"])).toBeDefined());
    await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
    const first = last();
    first.open();

    // A note count changes, the names don't: the socket stays.
    await act(() => queryClient.invalidateQueries({ queryKey: ["vaults"] }));
    expect(FakeSocket.all).toHaveLength(1);

    vaults.push({ name: "Beet" });
    await act(() => queryClient.invalidateQueries({ queryKey: ["vaults"] }));

    await waitFor(() => expect(FakeSocket.all).toHaveLength(2));
    expect(first.closed).toBe(true);
    expect(ticketOf(last())).toBe("t2");
  });

  it("moves a tab on the old name to the new one", async () => {
    useEditor.setState({ activeVault: "Gartenarbeit", currentPath: "/Gartenarbeit/plan.md", recentPaths: ["/Gartenarbeit/plan.md", "/Work/a.md"] });
    const router = await tabOn("Gartenarbeit/plan.md");

    last().frame(renamed);

    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Garten/plan.md"));
    // In place of the old name's entry: Back doesn't lead to a note that is no longer there.
    expect(router.state.historyAction).toBe("REPLACE");
    expect(useEditor.getState().activeVault).toBe("Garten");
    expect(useEditor.getState().currentPath).toBe("/Garten/plan.md");
    expect(useEditor.getState().recentPaths).toEqual(["/Garten/plan.md", "/Work/a.md"]);
  });

  it("keeps unsaved text across the move", async () => {
    const draft = { username: "alice", path: "/Gartenarbeit/plan.md", text: "Beete, noch nicht gesichert.\n", version: "v1" };
    useEditor.setState({ activeVault: "Gartenarbeit", currentPath: "/Gartenarbeit/plan.md", draft });
    const router = await tabOn("Gartenarbeit/plan.md");

    last().frame(renamed);

    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Garten/plan.md"));
    expect(useEditor.getState().draft).toEqual({ ...draft, path: "/Garten/plan.md" });
  });

  it("leaves a tab on another vault alone", async () => {
    useEditor.setState({ activeVault: "Work", currentPath: "/Work/plan.md", recentPaths: ["/Work/plan.md"] });
    const router = await tabOn("Work/plan.md");

    last().frame({ type: "renamed", vault: "Beet", from: "Garten" });

    expect(router.state.location.pathname).toBe("/notes/Work/plan.md");
    expect(router.state.historyAction).toBe("POP");
    expect(useEditor.getState()).toMatchObject({ activeVault: "Work", currentPath: "/Work/plan.md", recentPaths: ["/Work/plan.md"] });
  });

  it("leaves the tab that renamed alone", async () => {
    // It moved on its own answer already: the old name matches nothing here.
    useEditor.setState({ activeVault: "Garten", currentPath: "/Garten/plan.md", recentPaths: ["/Garten/plan.md"] });
    const router = await tabOn("Garten/plan.md");

    last().frame(renamed);

    expect(router.state.location.pathname).toBe("/notes/Garten/plan.md");
    expect(router.state.historyAction).toBe("POP");
    expect(useEditor.getState()).toMatchObject({ activeVault: "Garten", currentPath: "/Garten/plan.md", recentPaths: ["/Garten/plan.md"] });
  });

  it("ignores a frame that names no move", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(["tree", "Gartenarbeit"], {});
    useEditor.setState({ activeVault: "Gartenarbeit", currentPath: "/Gartenarbeit/plan.md" });
    server([{ name: "Gartenarbeit" }]);
    const { router } = mount(queryClient, undefined, "/notes/Gartenarbeit/plan.md");
    await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
    last().open();

    last().frame({ type: "renamed", vault: "Gartenarbeit", from: "Gartenarbeit" });

    expect(router.state.historyAction).toBe("POP");
    expect(queryClient.getQueryData(["tree", "Gartenarbeit"])).toBeDefined();
  });

  it("ignores a frame with no from", async () => {
    useEditor.setState({ activeVault: "Gartenarbeit", currentPath: "/Gartenarbeit/plan.md", recentPaths: ["/Gartenarbeit/plan.md"] });
    const router = await tabOn("Gartenarbeit/plan.md");

    last().frame({ type: "renamed", vault: "Garten" });

    expect(router.state.location.pathname).toBe("/notes/Gartenarbeit/plan.md");
    expect(router.state.historyAction).toBe("POP");
    expect(useEditor.getState()).toMatchObject({ activeVault: "Gartenarbeit", currentPath: "/Gartenarbeit/plan.md", recentPaths: ["/Gartenarbeit/plan.md"] });
  });
});
