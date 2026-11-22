// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, beforeEach, expect, test, vi } from "vitest";
import { HintConnection, wsUrlFor, type SocketLike } from "../src/live/hints";
import { AuthError, OfflineError, type ConnectionMode } from "../src/engine/ports";
import { fakeClock } from "./fakes/harness";

class FakeSocket implements SocketLike {
  onopen: (() => void) | null = null;
  onmessage: ((e: { data: string }) => void) | null = null;
  onclose: ((e: { code: number }) => void) | null = null;
  onerror: (() => void) | null = null;
  closed = false;
  constructor(readonly url: string) {}
  close(): void { this.closed = true; }
  open(): void { this.onopen?.(); }
  send(data: unknown): void { this.onmessage?.({ data: JSON.stringify(data) }); }
  drop(code = 1006): void { this.onclose?.({ code }); }
}

let sockets: FakeSocket[];
let checks: boolean[];
let modes: ConnectionMode[];
let ticket: () => Promise<string>;

function connection(random = () => 1) {
  return new HintConnection({
    ticket: () => ticket(),
    wsUrl: (t) => wsUrlFor("https://k.example", t),
    open: (url) => { const s = new FakeSocket(url); sockets.push(s); return s; },
    vault: "Work", clock: fakeClock, random,
    check: (full) => checks.push(full),
    onMode: (m) => modes.push(m),
  });
}
const flush = () => vi.advanceTimersByTimeAsync(0);
const last = () => sockets[sockets.length - 1]!;

beforeEach(() => {
  vi.useFakeTimers();
  sockets = []; checks = []; modes = [];
  ticket = () => Promise.resolve("kumbu_wst_t");
});
afterEach(() => { vi.useRealTimers(); });

test("the WebSocket url follows the server's scheme", () => {
  expect(wsUrlFor("https://k.example/", "a b")).toBe("wss://k.example/api/ws?ticket=a%20b");
  expect(wsUrlFor("http://localhost:8080", "t")).toBe("ws://localhost:8080/api/ws?ticket=t");
  expect(wsUrlFor("HTTPS://Kumbu.example", "t")).toBe("wss://kumbu.example/api/ws?ticket=t");
});

test("a socket that can't even be constructed counts as a failed connection", async () => {
  let fail = true;
  const c = new HintConnection({
    ticket: () => ticket(), wsUrl: (t) => wsUrlFor("https://k.example", t),
    open: (url) => {
      if (fail) throw new SyntaxError("bad url");
      const s = new FakeSocket(url); sockets.push(s); return s;
    },
    vault: "Work", clock: fakeClock, random: () => 1, check: (full) => checks.push(full), onMode: (m) => modes.push(m),
  });
  c.start();
  await flush();
  fail = false;
  await vi.advanceTimersByTimeAsync(1000);
  expect(sockets).toHaveLength(1);
  c.stop();
});

test("after an open: a check and mode live", async () => {
  const c = connection();
  c.start();
  await flush();
  expect(last().url).toBe("wss://k.example/api/ws?ticket=kumbu_wst_t");
  last().open();
  expect(checks).toEqual([false]);
  expect(modes).toEqual(["live"]);
  c.stop();
});

test("a hint for the vault triggers one check, one for another vault none", async () => {
  const c = connection();
  c.start();
  await flush();
  last().open();
  checks = [];
  last().send({ type: "changed", vault: "Work" });
  last().send({ type: "changed", vault: "Private" });
  last().send({ type: "other", vault: "Work" });
  last().onmessage?.({ data: "not json" });
  expect(checks).toEqual([false]);
  c.stop();
});

test("backoff delays 1, 2, 4 s … capped at 300 s", async () => {
  const c = connection(() => 1);
  c.start();
  await flush();
  const delays: number[] = [];
  for (let i = 0; i < 11; i++) {
    const before = sockets.length;
    last().drop();
    let waited = 0;
    while (sockets.length === before) {
      await vi.advanceTimersByTimeAsync(100);
      waited += 100;
    }
    delays.push(waited / 1000);
  }
  expect(delays).toEqual([1, 2, 4, 8, 16, 32, 64, 128, 256, 300, 300]);
  c.stop();
});

test("the delay has equal jitter: never below half of it", async () => {
  const c = connection(() => 0);
  c.start();
  await flush();
  last().drop();
  await vi.advanceTimersByTimeAsync(499);
  expect(sockets).toHaveLength(1);
  await vi.advanceTimersByTimeAsync(1);
  expect(sockets).toHaveLength(2);
  c.stop();
});

test("a connection that opens and closes at once still counts towards polling", async () => {
  const c = connection(() => 1);
  c.start();
  await flush();
  for (let i = 0; i < 3; i++) {
    last().open();
    await vi.advanceTimersByTimeAsync(100);
    last().drop(1001);
    await vi.advanceTimersByTimeAsync(2 ** i * 1000);
  }
  expect(modes).toContain("polling");
  c.stop();
});

test("polling starts after the third failure and stops after a successful open", async () => {
  const c = connection(() => 1);
  c.start();
  await flush();
  last().drop();
  await vi.advanceTimersByTimeAsync(1000);
  last().drop();
  await vi.advanceTimersByTimeAsync(2000);
  expect(modes).not.toContain("polling");
  last().drop();
  expect(modes).toContain("polling");
  checks = [];
  await vi.advanceTimersByTimeAsync(2 * 60_000 - 1); // reconnects, but the socket doesn't open
  expect(checks).toEqual([]);
  await vi.advanceTimersByTimeAsync(1);
  expect(checks).toEqual([false]);
  await vi.advanceTimersByTimeAsync(2 * 60_000);
  expect(checks).toEqual([false, false]);
  last().open();
  expect(modes[modes.length - 1]).toBe("live");
  checks = [];
  await vi.advanceTimersByTimeAsync(10 * 60_000);
  expect(checks).toEqual([]);
  c.stop();
});

test("a ticket that can't be fetched offline says offline and retries", async () => {
  ticket = () => Promise.reject(new OfflineError("no network"));
  const c = connection(() => 1);
  c.start();
  await flush();
  expect(modes).toEqual(["offline"]);
  ticket = () => Promise.resolve("t2");
  await vi.advanceTimersByTimeAsync(1000);
  expect(sockets).toHaveLength(1);
  c.stop();
});

test("an AuthError from the ticket lets the engine find out and tries again slowly", async () => {
  ticket = () => Promise.reject(new AuthError("revoked"));
  const c = connection();
  c.start();
  await flush();
  expect(checks).toEqual([false]);
  expect(sockets).toHaveLength(0);
  // If the engine's own call succeeds (a passing 401), the hints must not be gone for good.
  ticket = () => Promise.resolve("t2");
  await vi.advanceTimersByTimeAsync(2 * 60_000);
  expect(sockets).toHaveLength(1);
  c.stop();
});

test("close 4401 lets the engine find out and tries again slowly", async () => {
  const c = connection();
  c.start();
  await flush();
  last().open();
  checks = [];
  last().drop(4401);
  expect(checks).toEqual([false]);
  await vi.advanceTimersByTimeAsync(2 * 60_000 - 1);
  expect(sockets).toHaveLength(1);
  await vi.advanceTimersByTimeAsync(1);
  expect(sockets).toHaveLength(2);
  c.stop();
});

test("an hourly full check", async () => {
  const c = connection();
  c.start();
  await flush();
  last().open();
  checks = [];
  await vi.advanceTimersByTimeAsync(60 * 60_000);
  expect(checks).toEqual([true]);
  await vi.advanceTimersByTimeAsync(60 * 60_000);
  expect(checks).toEqual([true, true]);
  c.stop();
});

test("stop() closes the socket and clears every timer", async () => {
  const c = connection();
  c.start();
  await flush();
  last().drop();
  await vi.advanceTimersByTimeAsync(1000);
  expect(sockets).toHaveLength(2);
  c.stop();
  expect(last().closed).toBe(true);
  expect(vi.getTimerCount()).toBe(0);
  checks = [];
  await vi.advanceTimersByTimeAsync(2 * 60 * 60_000);
  expect(checks).toEqual([]);
  expect(sockets).toHaveLength(2);
});
