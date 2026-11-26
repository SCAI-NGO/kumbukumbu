// SPDX-License-Identifier: AGPL-3.0-or-later
// After an offline sign-in the tab knows only the username: the engines and the socket wait for the session to be read,
// and act only when it is the copy's own user's (offline mode design §4.11).
import "fake-indexeddb/auto";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, render, waitFor } from "@testing-library/react";
import { MirrorCopies } from "eidolon-core/store/mirror";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import { api } from "../api/client";
import { useSyncSocket } from "../hooks/useSyncSocket";
import { useSession } from "../state/session";
import { json } from "../test/app";
import { FakeSocket } from "../test/socket";
import { useOfflineCopy } from "./copy";
import { enginesReconnected, runEngines } from "./engines";
import { runOfflineHost } from "./host";
import { lockCopy, resetOfflineMode, useOfflineMode } from "./mode";
import { browserPorts } from "./ports";
import { unlockOffline } from "./unlock";

const key = Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url");
const home = { id: "v-home", tag: "t-home", canWrite: true, pending: false };
const listed = (id: string, name: string) => ({ id, name, canWrite: true, shared: false, level: "owner", discreet: false });
const alice = { id: "u1", username: "alice", display: "Alice", role: "user" };
const bob = { id: "u2", username: "bob", display: "Bob", role: "user" };

// jsdom has no Web Locks: one tab, so a lock is a queue per name.
const tails = new Map<string, Promise<unknown>>();
const locks = {
  request: (name: string, ...rest: unknown[]) => {
    const fn = rest[rest.length - 1] as () => Promise<unknown>;
    const run = (tails.get(name) ?? Promise.resolve()).then(fn, fn);
    tails.set(name, run.catch(() => {}));
    return run;
  },
};

const settle = () => act(() => new Promise((r) => setTimeout(r, 100)));

/** Bob's copy on this device: Home, with one note written offline still to upload; locked. */
async function bobsCopy() {
  vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(json(201, { id: "s2", token: "tok-bob", key, vaults: [home] }))));
  const copies = new MirrorCopies(await browserPorts());
  const mirror = await copies.create({ userId: bob.id, username: "bob", password: "pw", label: "Test", idleMs: 60_000 });
  const vault = await mirror.adopt(home.id);
  await vault.setMeta({ name: "Home" });
  await vault.files().writeIf("Shopping.md", null, "Milk");
  mirror.lock();
  vi.unstubAllGlobals();
}

/** Bob opens his copy on the sign-in form, with no answer from Kumbu². */
async function bobSignsInOffline() {
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new TypeError("Failed to fetch"))));
  await api.GET("/api/version").catch(() => {});
  expect(await unlockOffline("bob", "pw")).toBe(true);
  useSession.getState().signedIn({ user: { id: "", username: "bob", display: "bob", role: "user" }, previousSignInAt: null });
  await settle();
}

/** Kumbu² answers again, for the person whose session the browser holds; `asked` lists the requests in order. */
function kumbuAnswers(owner: typeof alice, vaults: unknown[]) {
  const asked: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = input instanceof Request ? input : null;
    const asking = `${request?.method ?? init?.method ?? "GET"} ${new URL(request?.url ?? String(input), "http://localhost").pathname}`;
    asked.push(asking);
    if (asking === "GET /api/session") return json(200, { user: owner, previousSignInAt: null });
    if (asking === "GET /api/vaults") return json(200, vaults);
    if (asking === "POST /api/ws-ticket") return json(200, { ticket: "t1", expiresAt: "2037-01-01T00:00:30Z" });
    if (asking === "POST /api/account/mirror-slots/contact") return json(200, { id: "s2", key, vaults: [home] });
    // The sync routes: not now, so nothing uploads.
    return json(503, { error: "Not now." });
  }));
  return asked;
}

/** What Bob's copy holds on the device, opened afresh with his password. */
async function bobsCopyOnTheDevice() {
  await lockCopy();
  const copies = new MirrorCopies(await browserPorts());
  const mirror = (await copies.unlockOffline((await copies.forUsername("bob"))!, "pw"))!;
  const state = { vaults: mirror.held().map((v) => v.id), waiting: mirror.waiting() };
  mirror.lock();
  return state;
}

const stops: (() => void)[] = [];

beforeEach(() => {
  FakeSocket.all = [];
  Object.defineProperty(navigator, "locks", { value: locks, configurable: true });
  vi.spyOn(console, "warn").mockImplementation(() => {});
  vi.spyOn(console, "error").mockImplementation(() => {});
  stops.push(runEngines());
});

afterEach(async () => {
  cleanup();
  for (const stop of stops.splice(0)) stop();
  await lockCopy();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useOfflineCopy.getState().clear();
  useSession.getState().reset();
  resetOfflineMode();
  localStorage.clear();
  await (await browserPorts()).storage.write({ removePrefix: [""] });
});

describe("after an offline sign-in", () => {
  test("the engines wait for the session to be read, and run once it is the copy's own user's", async () => {
    await bobsCopy();
    await bobSignsInOffline();
    const asked = kumbuAnswers(bob, [listed(home.id, "Home")]);
    enginesReconnected();
    await waitFor(() => expect(asked).toContain("GET /api/vaults"));
    expect(asked[0]).toBe("GET /api/session");
    expect(useSession.getState().user).toMatchObject({ id: bob.id, username: "bob" });
    await settle();
    expect(await bobsCopyOnTheDevice()).toEqual({ vaults: [home.id], waiting: 1 });
  });

  test("under another person's session the copy locks with its changes, and the tab goes to sign-in", async () => {
    await bobsCopy();
    await bobSignsInOffline();
    const asked = kumbuAnswers(alice, [listed("v-work", "Work")]);
    enginesReconnected();
    await waitFor(() => expect(useOfflineCopy.getState().mirror).toBeNull());
    await settle();
    expect(asked).not.toContain("GET /api/vaults");
    expect(asked.filter((a) => a.startsWith("POST /api/account/mirror-slots"))).toEqual([]);
    expect(useSession.getState()).toMatchObject({ user: null, ended: true });
    expect(useOfflineMode.getState().purged).toBeNull();
    expect(await bobsCopyOnTheDevice()).toEqual({ vaults: [home.id], waiting: 1 });
  });
});

function Shell() {
  useSyncSocket();
  return null;
}

/** The shell's socket, with the app's offline wiring: offline, the shell reads from the copy. */
function mountShell() {
  vi.stubGlobal("WebSocket", FakeSocket);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  stops.push(runOfflineHost(queryClient));
  const router = createMemoryRouter([{ path: "/", element: <Shell /> }]);
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe("after an offline sign-in, the sync socket", () => {
  test("reads the session before it asks for a ticket", async () => {
    await bobsCopy();
    await bobSignsInOffline();
    const asked = kumbuAnswers(bob, [listed(home.id, "Home")]);
    mountShell();
    await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
    expect(asked).toContain("GET /api/session");
    expect(asked.indexOf("GET /api/session")).toBeLessThan(asked.indexOf("POST /api/ws-ticket"));
    FakeSocket.all[0]!.open();
    await waitFor(() => expect(asked).toContain("POST /api/account/mirror-slots/contact"));
  });

  test("asks for no ticket under another person's session", async () => {
    await bobsCopy();
    await bobSignsInOffline();
    const asked = kumbuAnswers(alice, [listed("v-work", "Work")]);
    mountShell();
    await waitFor(() => expect(useOfflineCopy.getState().mirror).toBeNull());
    await settle();
    expect(asked).not.toContain("POST /api/ws-ticket");
    expect(FakeSocket.all).toHaveLength(0);
  });
});
