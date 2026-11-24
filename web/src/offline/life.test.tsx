// SPDX-License-Identifier: AGPL-3.0-or-later
// The copy's life in a tab follows the session's (offline mode design §3a, §4.11): every unlocked copy locks when
// idle, a session's end locks it, the next user never gets it, a sign-out locks every tab, and the sign-in page learns
// of removed copies.
import "fake-indexeddb/auto";
import { MirrorCopies } from "eidolon-core/store/mirror";
import { afterEach, describe, expect, test, vi } from "vitest";
import { api } from "../api/client";
import { useSession } from "../state/session";
import { json, noContent, user } from "../test/app";
import { useOfflineCopy } from "./copy";
import { contact, lockCopy, onBeforeLock, openCopy } from "./mode";
import { browserPorts } from "./ports";
import { signOutPending, sweepRemovedCopies } from "./unlock";

const key = Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url");
const vaults = [{ id: "v1", tag: "t1", canWrite: true, pending: false }];
const bob = { ...user, id: "u2", username: "bob", display: "Bob" };

type Answer = (init: RequestInit | undefined, request: Request | null) => Response | Promise<Response>;

/** The server, by "METHOD /path", for both fetch(url, init) (the slot) and fetch(request) (the API client). */
function server(answers: Record<string, Answer>) {
  const calls: { key: string; headers: Headers; body: string | null }[] = [];
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = input instanceof Request ? input : null;
    const method = request?.method ?? init?.method ?? "GET";
    const path = new URL(request?.url ?? String(input), "http://localhost").pathname;
    const key = `${method} ${path}`;
    calls.push({ key, headers: new Headers(request?.headers ?? init?.headers), body: typeof init?.body === "string" ? init.body : null });
    const answer = answers[key];
    if (answer === undefined) throw new Error(`Unexpected request: ${key}`);
    return answer(init, request);
  });
  vi.stubGlobal("fetch", fetch);
  return calls;
}

const slot = () => json(200, { id: "s1", key, vaults });

/** A copy of Ada's (user u1) in IndexedDB, as turning offline on leaves it, locked. */
async function adasCopy(idleMs = 60_000) {
  server({ "POST /api/account/mirror-slots": () => json(201, { id: "s1", token: "tok-ada", key, vaults }) });
  const copies = new MirrorCopies(await browserPorts());
  (await copies.create({ userId: user.id, username: "ada", password: "pw", label: "Test", idleMs })).lock();
}

async function openedOnline(idleMs = 60_000) {
  await adasCopy(idleMs);
  useSession.getState().signedIn({ user, previousSignInAt: null });
  server({ "POST /api/account/mirror-slots/contact": slot });
  await openCopy(user.id);
  expect(useOfflineCopy.getState().mirror?.locked).toBe(false);
}

afterEach(async () => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  await lockCopy();
  useOfflineCopy.getState().clear();
  useSession.getState().reset();
  localStorage.clear();
  await (await browserPorts()).storage.write({ removePrefix: [""] });
});

describe("a copy opened with the slot's key", () => {
  test("locks at its idle limit once offline, and the sign-in form asks", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
    await openedOnline(1_000);
    vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new TypeError("Failed to fetch"))));
    await api.GET("/api/version").catch(() => {});
    await vi.advanceTimersByTimeAsync(1_000);
    expect(useOfflineCopy.getState().mirror).toBeNull();
    expect(useSession.getState().ended).toBe(true);
  });

  test("online, while the server vouches for the session, it stays open past the limit", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
    await openedOnline(1_000);
    await vi.advanceTimersByTimeAsync(5_000);
    expect(useOfflineCopy.getState().mirror?.locked).toBe(false);
  });
});

describe("a session's end", () => {
  test("a 401 locks the copy before the app goes to sign-in", async () => {
    await openedOnline();
    server({ "GET /api/vaults": () => json(401, { error: "Signed out." }) });
    await api.GET("/api/vaults");
    await vi.waitFor(() => expect(useOfflineCopy.getState().mirror).toBeNull());
    expect(useSession.getState().ended).toBe(true);
  });

  test("the next user to sign in never gets the copy, and their contact hands in nothing of it", async () => {
    await openedOnline();
    useSession.getState().signedIn({ user: bob, previousSignInAt: null });
    const calls = server({ "POST /api/account/mirror-slots/contact": slot });
    await openCopy(bob.id);
    await contact();
    expect(useOfflineCopy.getState().mirror).toBeNull();
    expect(calls.filter((c) => c.body?.includes("tok-ada"))).toEqual([]);
    // Ada's copy is still on the device, sealed.
    expect(await new MirrorCopies(await browserPorts()).forUser(user.id)).not.toBeNull();
  });

  test("a contact begun under Ada's session doesn't go out once Bob is the one signed in", async () => {
    await openedOnline();
    const calls = server({ "POST /api/account/mirror-slots/contact": slot });
    const contacting = contact();
    useSession.getState().signedIn({ user: bob, previousSignInAt: null });
    await contacting;
    expect(calls.filter((c) => c.body?.includes("tok-ada"))).toEqual([]);
  });

  test("Ada's copy, still opening when Bob signs in, is never kept for him, and her token goes out no more", async () => {
    await adasCopy();
    useSession.getState().signedIn({ user, previousSignInAt: null });
    let answer!: () => void;
    const answered = new Promise<void>((r) => (answer = r));
    const calls = server({ "POST /api/account/mirror-slots/contact": async () => (await answered, slot()) });
    const adas = openCopy(user.id);
    await vi.waitFor(() => expect(calls).toHaveLength(1));
    useSession.getState().signedIn({ user: bob, previousSignInAt: null });
    const bobs = openCopy(bob.id);
    answer();
    await Promise.all([adas, bobs]);
    expect(useOfflineCopy.getState().mirror).toBeNull();
    // The one contact made while she was signed in, none after.
    expect(calls.filter((c) => c.body?.includes("tok-ada"))).toHaveLength(1);
  });

  test("a sign-in in a tab that holds the copy wraps it again under the password", async () => {
    await openedOnline();
    const held = useOfflineCopy.getState().mirror;
    server({ "POST /api/account/mirror-slots/contact": slot });
    await openCopy(user.id, { username: "ada", password: "new password", remember: false });
    expect(held?.locked).toBe(true);
    const copies = new MirrorCopies(await browserPorts());
    expect(await copies.unlockOffline((await copies.forUsername("ada"))!, "new password")).not.toBeNull();
  });
});

test("a sign-out in another tab locks this one, its editors written first", async () => {
  await openedOnline();
  const mine = useOfflineCopy.getState().mirror!;
  const copies = new MirrorCopies(await browserPorts());
  const other = (await copies.unlockOffline((await copies.forUser(user.id))!, "pw"))!;
  const wrote = vi.fn();
  const stop = onBeforeLock(wrote);
  other.lockEverywhere();
  await vi.waitFor(() => expect(useOfflineCopy.getState().mirror).toBeNull());
  expect(mine.locked).toBe(true);
  expect(wrote).toHaveBeenCalled();
  stop();
});

test("the sign-in page removes a copy whose slot is gone, and keeps one whose slot is there", async () => {
  await adasCopy();
  const calls = server({ "GET /api/session": () => json(401, { error: "Gone.", code: "mirror-wipe" }) });
  await sweepRemovedCopies();
  expect(calls.map((c) => c.headers.get("Kumbu-Mirror"))).toEqual(["tok-ada"]);
  expect(await new MirrorCopies(await browserPorts()).forUser(user.id)).toBeNull();

  await adasCopy();
  server({ "GET /api/session": () => json(401, { error: "Sign in first." }) });
  await sweepRemovedCopies();
  expect(await new MirrorCopies(await browserPorts()).forUser(user.id)).not.toBeNull();
});

test("a sign-out made offline reaches the server as soon as Kumbu² answers again", async () => {
  localStorage.setItem("kumbu.signOutPending", "1");
  const calls = server({ "GET /api/version": () => json(200, {}), "DELETE /api/session": () => noContent() });
  await api.GET("/api/version");
  await vi.waitFor(() => expect(signOutPending()).toBe(false));
  expect(calls.map((c) => c.key)).toContain("DELETE /api/session");
});
