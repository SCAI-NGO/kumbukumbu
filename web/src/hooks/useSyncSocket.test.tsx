// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, render, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useVersionPoll } from "../api/history";
import { useSyncStatus } from "../state/sync";
import { fakeApi, json } from "../test/app";
import { useSyncSocket } from "./useSyncSocket";

/** A WebSocket that only records: the test opens, sends into and drops it. */
class FakeSocket {
  static all: FakeSocket[] = [];
  onopen: ((e: Event) => void) | null = null;
  onmessage: ((e: MessageEvent) => void) | null = null;
  onclose: ((e: CloseEvent) => void) | null = null;
  closed = false;
  constructor(readonly url: string) {
    FakeSocket.all.push(this);
  }
  close() {
    this.closed = true;
  }
  open() {
    act(() => this.onopen?.(new Event("open")));
  }
  frame(body: unknown) {
    act(() => this.onmessage?.(new MessageEvent("message", { data: JSON.stringify(body) })));
  }
  drop(code = 1006) {
    act(() => this.onclose?.(new CloseEvent("close", { code })));
  }
}

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
  useSyncStatus.setState({ connected: false });
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

function mount(queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } }), poll?: number) {
  const view = render(
    <QueryClientProvider client={queryClient}>
      <Shell poll={poll} />
    </QueryClientProvider>,
  );
  return { queryClient, view };
}

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
});
