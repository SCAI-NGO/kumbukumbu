// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline mode (offline mode design §3, §3a "The bar and the mark"): when it goes offline and back, the hold
// against flicker, the done line and conflicts, and one mode across tabs.
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import type { Clock } from "../src/engine/ports";
import { HOLD_MS, ModeMachine, type ModeState } from "../src/mode/mode";
import { SharedMode, type ModeChannel, type ModeMessage } from "../src/mode/shared";
import { FakeLocks } from "./fakeStore";

const clock: Clock = {
  now: () => Date.now(),
  setTimeout: (fn, ms) => setTimeout(fn, ms),
  clearTimeout: (h) => clearTimeout(h as ReturnType<typeof setTimeout>),
};

beforeEach(() => vi.useFakeTimers({ now: 1_000_000 }));
afterEach(() => vi.useRealTimers());

function machine() {
  const seen: ModeState[] = [];
  const m = new ModeMachine(clock, (s) => seen.push(s));
  return { m, last: () => seen.at(-1)! };
}

/** Online with an open socket, the start's hold already past. */
function connected() {
  const t = machine();
  t.m.apply({ type: "socket", tab: "a", up: true });
  return t;
}

describe("going offline", () => {
  test("a failed request with the socket down goes offline at once at the start", () => {
    const { m, last } = machine();
    m.apply({ type: "failed" });
    expect(last()).toMatchObject({ offline: true, phase: "offline" });
  });

  test("a failed request while the socket is up stays online", () => {
    const { m, last } = connected();
    m.apply({ type: "failed" });
    expect(last().offline).toBe(false);
  });

  test("the socket closing after a failure goes offline", () => {
    const { m, last } = connected();
    m.apply({ type: "failed" });
    m.apply({ type: "socket", tab: "a", up: false });
    expect(last().offline).toBe(true);
  });

  test("a socket closing alone is no reason, nor a failure a later request made up for", () => {
    const { m, last } = connected();
    m.apply({ type: "socket", tab: "a", up: false });
    expect(last().offline).toBe(false);
    m.apply({ type: "socket", tab: "a", up: true });
    m.apply({ type: "failed" });
    m.apply({ type: "reached" });
    m.apply({ type: "socket", tab: "a", up: false });
    expect(last().offline).toBe(false);
  });
});

describe("coming back", () => {
  test("online once the socket is back and nothing waits", () => {
    const { m, last } = machine();
    m.apply({ type: "failed" });
    vi.advanceTimersByTime(HOLD_MS);
    m.apply({ type: "socket", tab: "a", up: true });
    expect(last()).toMatchObject({ offline: false, phase: "online", done: null });
  });

  test("stays offline, uploading, until the queues are empty; then says how many went", () => {
    const { m, last } = machine();
    m.apply({ type: "failed" });
    m.apply({ type: "waiting", count: 2 });
    m.apply({ type: "waiting", count: 3 });
    vi.advanceTimersByTime(HOLD_MS);
    m.apply({ type: "socket", tab: "a", up: true });
    expect(last()).toMatchObject({ offline: true, phase: "uploading", waiting: 3 });
    m.apply({ type: "waiting", count: 1 });
    expect(last()).toMatchObject({ offline: true, phase: "uploading", waiting: 1 });
    m.apply({ type: "waiting", count: 0 });
    expect(last()).toMatchObject({ offline: false, phase: "online", done: { uploaded: 3 } });
    m.apply({ type: "seen" });
    expect(last().done).toBeNull();
  });

  test("a session that ended with changes waiting asks to sign in, and uploads after", () => {
    const { m, last } = machine();
    m.apply({ type: "failed" });
    m.apply({ type: "waiting", count: 3 });
    vi.advanceTimersByTime(HOLD_MS);
    m.apply({ type: "sessionLost" });
    expect(last()).toMatchObject({ offline: true, phase: "signIn" });
    m.apply({ type: "signedIn" });
    m.apply({ type: "socket", tab: "a", up: true });
    expect(last().phase).toBe("uploading");
  });

  test("a session that ended with nothing waiting leaves the mode, for the usual sign-in", () => {
    const { m, last } = machine();
    m.apply({ type: "failed" });
    vi.advanceTimersByTime(HOLD_MS);
    m.apply({ type: "sessionLost" });
    expect(last().offline).toBe(false);
  });
});

describe("no flicker", () => {
  test("a mode holds for 15 seconds, then follows what is true then", () => {
    const { m, last } = machine();
    m.apply({ type: "failed" });
    vi.advanceTimersByTime(1_000);
    m.apply({ type: "socket", tab: "a", up: true });
    expect(last().offline).toBe(true);
    vi.advanceTimersByTime(HOLD_MS - 1_001);
    expect(last().offline).toBe(true);
    vi.advanceTimersByTime(1);
    expect(last().offline).toBe(false);
    // Back online, it holds too.
    m.apply({ type: "failed" });
    m.apply({ type: "socket", tab: "a", up: false });
    expect(last().offline).toBe(false);
    vi.advanceTimersByTime(HOLD_MS);
    expect(last().offline).toBe(true);
  });

  test("a blip within the hold leaves no trace", () => {
    const { m, last } = machine();
    m.apply({ type: "failed" });
    m.apply({ type: "socket", tab: "a", up: true });
    m.apply({ type: "socket", tab: "a", up: false });
    m.apply({ type: "failed" });
    vi.advanceTimersByTime(HOLD_MS);
    expect(last().offline).toBe(true);
  });

  test("the count follows at once while the mode holds", () => {
    const { m, last } = machine();
    m.apply({ type: "failed" });
    m.apply({ type: "waiting", count: 4 });
    expect(last().waiting).toBe(4);
  });
});

test("conflicts stay until seen, online too", () => {
  const { m, last } = machine();
  m.apply({ type: "failed" });
  m.apply({ type: "waiting", count: 1 });
  vi.advanceTimersByTime(HOLD_MS);
  m.apply({ type: "socket", tab: "a", up: true });
  m.apply({ type: "conflict", conflict: { vault: "v1", original: "Plan.md", copy: "Plan (conflict 2026-11-22 21-04).md" } });
  m.apply({ type: "waiting", count: 0 });
  vi.advanceTimersByTime(60_000);
  expect(last()).toMatchObject({ offline: false, done: { uploaded: 1 } });
  expect(last().conflicts).toHaveLength(1);
  m.apply({ type: "seen" });
  expect(last().conflicts).toEqual([]);
});

test("a session's end forgets the conflicts, the done line and the count of the person before", () => {
  const { m, last } = machine();
  m.apply({ type: "failed" });
  m.apply({ type: "waiting", count: 3 });
  vi.advanceTimersByTime(HOLD_MS);
  m.apply({ type: "socket", tab: "a", up: true });
  m.apply({ type: "conflict", conflict: { vault: "v1", original: "Plan.md", copy: "Plan (conflict 2026-11-22 21-04).md" } });
  m.apply({ type: "waiting", count: 0 });
  vi.advanceTimersByTime(60_000);
  expect(last().done).toEqual({ uploaded: 3 });
  m.apply({ type: "forget" });
  expect(last()).toMatchObject({ waiting: 0, done: null, conflicts: [] });
});

test("offline, a forgotten count leaves nothing to upload for the next person", () => {
  const { m, last } = machine();
  m.apply({ type: "failed" });
  m.apply({ type: "waiting", count: 3 });
  m.apply({ type: "forget" });
  expect(last()).toMatchObject({ offline: true, phase: "offline", waiting: 0 });
  vi.advanceTimersByTime(HOLD_MS);
  m.apply({ type: "socket", tab: "a", up: true });
  expect(last()).toMatchObject({ offline: false, done: null });
});

describe("tabs", () => {
  /** One browser's mode channel: every other listener hears a message, later. */
  function bus() {
    const listeners = new Set<{ owner: object; fn: (m: ModeMessage) => void }>();
    return (): ModeChannel => {
      const owner = {};
      return {
        post: (m) => { for (const l of listeners) if (l.owner !== owner) queueMicrotask(() => l.fn(structuredClone(m))); },
        listen: (fn) => { const l = { owner, fn }; listeners.add(l); return () => listeners.delete(l); },
      };
    };
  }

  async function browser(n: number) {
    const channel = bus();
    const locks = new FakeLocks();
    const seen: ModeState[][] = [];
    const tabs = Array.from({ length: n }, (_, i) => {
      seen[i] = [];
      return new SharedMode({ locks, channel: channel(), clock }, `tab${i}`, (s) => seen[i]!.push(s));
    });
    await vi.advanceTimersByTimeAsync(0);
    return { tabs, last: (i: number) => seen[i]!.at(-1), locks, channel };
  }

  test("every tab shows the same mode and count, whichever tab saw it", async () => {
    const { tabs, last } = await browser(3);
    tabs[2]!.send({ type: "failed" });
    tabs[1]!.send({ type: "waiting", count: 2 });
    await vi.advanceTimersByTimeAsync(0);
    for (const i of [0, 1, 2]) expect(last(i)).toMatchObject({ offline: true, waiting: 2 });
  });

  test("one tab's open socket is enough to come back", async () => {
    const { tabs, last } = await browser(2);
    tabs[0]!.send({ type: "failed" });
    await vi.advanceTimersByTimeAsync(HOLD_MS);
    tabs[1]!.send({ type: "socket", tab: "tab1", up: true });
    await vi.advanceTimersByTimeAsync(0);
    expect(last(0)?.offline).toBe(false);
    expect(last(1)?.offline).toBe(false);
  });

  test("when the leading tab closes, another leads on from the same state", async () => {
    const { tabs, last } = await browser(2);
    tabs[1]!.send({ type: "failed" });
    tabs[1]!.send({ type: "waiting", count: 5 });
    await vi.advanceTimersByTimeAsync(0);
    tabs[0]!.stop();
    await vi.advanceTimersByTimeAsync(0);
    tabs[1]!.send({ type: "waiting", count: 4 });
    await vi.advanceTimersByTimeAsync(0);
    expect(last(1)).toMatchObject({ offline: true, waiting: 4 });
    await vi.advanceTimersByTimeAsync(HOLD_MS);
    tabs[1]!.send({ type: "socket", tab: "tab1", up: true });
    tabs[1]!.send({ type: "waiting", count: 0 });
    await vi.advanceTimersByTimeAsync(0);
    expect(last(1)).toMatchObject({ offline: false, done: { uploaded: 5 } });
  });

  test("what a tab saw before any tab led reaches the first to lead", async () => {
    const locks = new FakeLocks();
    const blocker = locks.hold("kumbu-mode");
    await blocker.acquired;
    let heard: ModeState | undefined;
    const tab = new SharedMode({ locks, channel: bus()(), clock }, "t", (s) => (heard = s));
    tab.send({ type: "failed" });
    tab.send({ type: "waiting", count: 2 });
    blocker.release();
    await vi.advanceTimersByTimeAsync(0);
    expect(heard).toMatchObject({ offline: true, waiting: 2 });
  });

  test("a tab opened after a session's end hears nothing of that person's conflicts or count", async () => {
    const { tabs, locks, channel } = await browser(2);
    tabs[1]!.send({ type: "conflict", conflict: { vault: "v1", original: "Plan.md", copy: "Plan (conflict 2026-11-22 21-04).md" } });
    tabs[1]!.send({ type: "waiting", count: 3 });
    await vi.advanceTimersByTimeAsync(0);
    tabs[1]!.send({ type: "forget" });
    await vi.advanceTimersByTimeAsync(0);
    // The tab that told the count leads now: what it saw before the end isn't told again.
    tabs[0]!.stop();
    await vi.advanceTimersByTimeAsync(0);
    let heard: ModeState | undefined;
    new SharedMode({ locks, channel: channel(), clock }, "late", (s) => (heard = s));
    await vi.advanceTimersByTimeAsync(0);
    expect(heard).toMatchObject({ waiting: 0, conflicts: [] });
  });

  test("a tab opened later hears the state at once", async () => {
    const { tabs, locks, channel } = await browser(1);
    tabs[0]!.send({ type: "failed" });
    await vi.advanceTimersByTimeAsync(0);
    let heard: ModeState | undefined;
    new SharedMode({ locks, channel: channel(), clock }, "late", (s) => (heard = s));
    await vi.advanceTimersByTimeAsync(0);
    expect(heard?.offline).toBe(true);
  });
});
