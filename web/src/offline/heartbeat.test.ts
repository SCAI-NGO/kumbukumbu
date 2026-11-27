// SPDX-License-Identifier: AGPL-3.0-or-later
// The heartbeat: a network that goes without the browser saying so still shows as offline (fixes of 0.7.0, §3 item 9).
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import { startHeartbeat } from "./heartbeat";
import { useSyncStatus } from "../state/sync";
import { resetOfflineMode, useOfflineMode } from "./mode";

let visibility: DocumentVisibilityState = "visible";
const connection = new EventTarget();
let fetch: ReturnType<typeof vi.fn<(url: string, init: RequestInit) => Promise<Response>>>;
let failed: ReturnType<typeof vi.fn<() => void>>;
let reached: ReturnType<typeof vi.fn<() => void>>;
let lost: ReturnType<typeof vi.fn<() => void>>;
let drop: ReturnType<typeof vi.fn<() => void>>;
let stop: () => void;

function show(state: DocumentVisibilityState) {
  visibility = state;
  document.dispatchEvent(new Event("visibilitychange"));
}

beforeEach(() => {
  vi.useFakeTimers();
  visibility = "visible";
  Object.defineProperty(document, "visibilityState", { configurable: true, get: () => visibility });
  Object.defineProperty(navigator, "connection", { configurable: true, value: connection });
  fetch = vi.fn((_url: string, _init: RequestInit) => Promise.resolve(new Response("{}", { status: 200 })));
  vi.stubGlobal("fetch", fetch);
  failed = vi.fn<() => void>();
  reached = vi.fn<() => void>();
  lost = vi.fn<() => void>();
  useOfflineMode.setState({ failed, reached, lost });
  drop = vi.fn<() => void>();
  useSyncStatus.setState({ drop });
  stop = startHeartbeat();
});

afterEach(() => {
  stop();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  delete (navigator as { connection?: unknown }).connection;
  delete (document as { visibilityState?: unknown }).visibilityState;
  const actual = useOfflineMode.getInitialState();
  useOfflineMode.setState({ failed: actual.failed, reached: actual.reached, lost: actual.lost });
  useSyncStatus.setState({ drop: () => {} });
  resetOfflineMode();
});

describe("the heartbeat", () => {
  test("asks every 15 seconds while the tab is visible", async () => {
    await vi.advanceTimersByTimeAsync(14_999);
    expect(fetch).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(fetch).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(30_000);
    expect(fetch).toHaveBeenCalledTimes(3);
    expect(fetch.mock.calls[0]![0]).toBe("/api/version");
  });

  test("asks nothing while the tab is hidden, and at once when it shows again", async () => {
    show("hidden");
    await vi.advanceTimersByTimeAsync(60_000);
    connection.dispatchEvent(new Event("change"));
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).not.toHaveBeenCalled();

    show("visible");
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(15_000);
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  test("asks at once on the connection's change", async () => {
    connection.dispatchEvent(new Event("change"));
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  test("no answer within 5 seconds counts as one failed request", async () => {
    fetch.mockImplementation(
      (_url: string, init: RequestInit) =>
        new Promise((_resolve, reject) => init.signal!.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")))),
    );
    connection.dispatchEvent(new Event("change"));
    await vi.advanceTimersByTimeAsync(4_999);
    expect(failed).not.toHaveBeenCalled();
    // A second ask while the first waits adds no second failure.
    connection.dispatchEvent(new Event("change"));
    await vi.advanceTimersByTimeAsync(1);
    expect(lost).toHaveBeenCalledTimes(1);
    expect(failed).not.toHaveBeenCalled();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(reached).not.toHaveBeenCalled();
  });

  test("a miss takes the socket down and counts every tab's socket down, once until an answer comes again", async () => {
    // A slow server, up all along: every ask runs into the 5 seconds.
    const slow = (_url: string, init: RequestInit) =>
      new Promise<Response>((_resolve, reject) => init.signal!.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError"))));
    fetch.mockImplementation(slow);
    await vi.advanceTimersByTimeAsync(60_000);
    // Asked at 15, 30, 45 and 60 seconds; the first three missed, the fourth still waits.
    expect(fetch).toHaveBeenCalledTimes(4);
    expect(drop).toHaveBeenCalledTimes(1);
    expect(lost).toHaveBeenCalledTimes(1);
    expect(failed).toHaveBeenCalledTimes(2);
    // Answered again: the next miss takes the socket down again.
    fetch.mockImplementationOnce(() => Promise.resolve(new Response("{}", { status: 200 })));
    await vi.advanceTimersByTimeAsync(15_000);
    expect(reached).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(20_000);
    expect(drop).toHaveBeenCalledTimes(2);
    expect(lost).toHaveBeenCalledTimes(2);
  });

  test("a network error or a gateway's 503 counts as failed", async () => {
    fetch.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    connection.dispatchEvent(new Event("change"));
    await vi.advanceTimersByTimeAsync(0);
    fetch.mockResolvedValueOnce(new Response("down", { status: 503 }));
    connection.dispatchEvent(new Event("change"));
    await vi.advanceTimersByTimeAsync(0);
    expect(lost).toHaveBeenCalledTimes(1);
    expect(failed).toHaveBeenCalledTimes(1);
    expect(reached).not.toHaveBeenCalled();
  });

  test("an answer counts as reached", async () => {
    connection.dispatchEvent(new Event("change"));
    await vi.advanceTimersByTimeAsync(0);
    expect(reached).toHaveBeenCalledTimes(1);
    expect(failed).not.toHaveBeenCalled();
  });

  test("sends no cookie and no copy token", async () => {
    connection.dispatchEvent(new Event("change"));
    await vi.advanceTimersByTimeAsync(0);
    const init = fetch.mock.calls[0]![1] as RequestInit;
    expect(init.credentials).toBe("omit");
    expect(init.cache).toBe("no-store");
    expect(init.headers).toBeUndefined();
  });

  test("stops asking once stopped", async () => {
    stop();
    connection.dispatchEvent(new Event("change"));
    show("visible");
    await vi.advanceTimersByTimeAsync(60_000);
    expect(fetch).not.toHaveBeenCalled();
  });
});

describe("the heartbeat with the offline mode", () => {
  test("a slow server that is up switches the app offline at most once in a minute, and it comes back", async () => {
    const actual = useOfflineMode.getInitialState();
    useOfflineMode.setState({ failed: actual.failed, reached: actual.reached, lost: actual.lost });
    // The socket: up, and once taken down it opens again a second later, as the server is there.
    useOfflineMode.getState().socket(true);
    let drops = 0;
    useSyncStatus.setState({
      drop: () => {
        drops++;
        useOfflineMode.getState().socket(false);
        setTimeout(() => useOfflineMode.getState().socket(true), 1_000);
      },
    });
    const switches: boolean[] = [];
    const unsubscribe = useOfflineMode.subscribe((s, before) => {
      if (s.offline !== before.offline) switches.push(s.offline);
    });
    fetch.mockImplementation(
      (_url: string, init: RequestInit) =>
        new Promise((_resolve, reject) => init.signal!.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")))),
    );
    await vi.advanceTimersByTimeAsync(60_000);
    unsubscribe();
    expect(drops).toBe(1);
    expect(switches).toEqual([true, false]);
  });
});

