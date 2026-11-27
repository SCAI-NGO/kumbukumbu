// SPDX-License-Identifier: AGPL-3.0-or-later
// The copy's life in a tab follows the session's (offline mode design §3a, §4.11): every unlocked copy locks when
// idle, a session's end locks it, the next user never gets it, a sign-out locks every tab, and the sign-in page learns
// of removed copies.
import "fake-indexeddb/auto";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MirrorCopies, REMEMBERED_IDLE_MS } from "eidolon-core/store/mirror";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import { api } from "../api/client";
import { useSession } from "../state/session";
import { json, noContent, user } from "../test/app";
import { useOfflineCopy } from "./copy";
import { contact, lockCopy, onBeforeLock, openCopy, signedInAgain, useOfflineMode } from "./mode";
import OfflineBar from "./OfflineBar";
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
  useOfflineMode.getState().dismissPurged();
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

// The copy's writes and its removal take a Web Lock, which jsdom hasn't: one tab, so each runs at once.
const oneTab = () => vi.stubGlobal("navigator", Object.assign(Object.create(navigator), { locks: { request: (_: string, fn: () => unknown) => fn() } }));

describe("a vault's copy removed", () => {
  beforeEach(oneTab);

  test("by the engines' vault list before a contact: the notice shows all the same", async () => {
    await openedOnline();
    const mirror = useOfflineCopy.getState().mirror!;
    await mirror.adopt("v1");
    // As the engines do for a vault the vault list no longer has (store/engines.ts); the contact then finds none.
    await mirror.purge("v1");
    server({ "POST /api/account/mirror-slots/contact": () => json(200, { id: "s1", key, vaults: [] }) });
    await contact();
    expect(useOfflineMode.getState().purged).toEqual({ lost: 0 });
  });

  test("not by the device's own choice: no notice", async () => {
    await openedOnline();
    const mirror = useOfflineCopy.getState().mirror!;
    await mirror.adopt("v1");
    await mirror.purge("v1", false);
    expect(useOfflineMode.getState().purged).toBeNull();
  });
});

describe("Sign in again over the app", () => {
  beforeEach(oneTab);

  /** The bar's dialog after the session ended with changes waiting; what POST /api/session was sent. */
  async function signInAgain(password: string): Promise<unknown[]> {
    const sent: unknown[] = [];
    server({
      "POST /api/session": async (_, request) => (sent.push(await request!.json()), json(200, { user, previousSignInAt: null })),
      "POST /api/account/mirror-slots/contact": slot,
    });
    useOfflineMode.setState({ offline: true, phase: "signIn", waiting: 1 });
    render(
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter>
          <OfflineBar />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    const dialog = screen.getByRole("alertdialog", { name: "Sign in again" });
    await userEvent.type(within(dialog).getByLabelText("Your password"), password);
    await userEvent.click(within(dialog).getByRole("button", { name: "Sign in" }));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull());
    cleanup();
    return sent;
  }

  test("after Sign in again the copy opens offline with the new password and not the old", async () => {
    await openedOnline();
    await signInAgain("new password");
    await vi.waitFor(async () => {
      const copies = new MirrorCopies(await browserPorts());
      expect(await copies.unlockOffline((await copies.forUsername("ada"))!, "new password")).not.toBeNull();
    }, { timeout: 15_000 });
    const copies = new MirrorCopies(await browserPorts());
    expect(await copies.unlockOffline((await copies.forUsername("ada"))!, "pw")).toBeNull();
    expect(useOfflineCopy.getState().mirror?.locked).toBe(false);
  });

  test("Sign in again keeps the copy's remember choice", async () => {
    await openedOnline(REMEMBERED_IDLE_MS);
    expect(await signInAgain("new password")).toEqual([{ username: "ada", password: "new password", remember: true }]);
    await vi.waitFor(async () => expect((await new MirrorCopies(await browserPorts()).forUser(user.id))!.idleMs).toBe(REMEMBERED_IDLE_MS));
  });

  test("Sign in again renews the copy's idle limit", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
    await openedOnline(1_000);
    await vi.advanceTimersByTimeAsync(900);
    server({ "POST /api/account/mirror-slots/contact": slot });
    await signedInAgain("ada", "new password");
    vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new TypeError("Failed to fetch"))));
    await api.GET("/api/version").catch(() => {});
    await vi.advanceTimersByTimeAsync(600);
    expect(useOfflineCopy.getState().mirror?.locked).toBe(false);
    await vi.advanceTimersByTimeAsync(500);
    expect(useOfflineCopy.getState().mirror).toBeNull();
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

  test("Ada's copy, found gone while Bob signed in, goes from the device but leaves his session alone", async () => {
    oneTab();
    await adasCopy();
    useSession.getState().signedIn({ user, previousSignInAt: null });
    let answer!: () => void;
    const answered = new Promise<void>((r) => (answer = r));
    const calls = server({ "POST /api/account/mirror-slots/contact": async () => (await answered, json(404, { error: "Gone.", code: "mirror-wipe" })) });
    const adas = openCopy(user.id);
    await vi.waitFor(() => expect(calls).toHaveLength(1));
    useSession.getState().signedIn({ user: bob, previousSignInAt: null });
    answer();
    await adas;
    expect(useSession.getState()).toMatchObject({ user: bob, ended: false, notice: null });
    expect(await new MirrorCopies(await browserPorts()).forUser(user.id)).toBeNull();
  });

  test("changing the password locks the copy in this tab", async () => {
    await openedOnline();
    const mirror = useOfflineCopy.getState().mirror!;
    useSession.getState().end("Your password is changed and every session has ended. Sign in with the new one.");
    await vi.waitFor(() => expect(useOfflineCopy.getState().mirror).toBeNull());
    expect(mirror.locked).toBe(true);
    // Sealed, on the device still: it opens again at the next sign-in.
    expect(await new MirrorCopies(await browserPorts()).forUser(user.id)).not.toBeNull();
  });

  test("a sign-out locks the copy in every tab", async () => {
    await openedOnline();
    const copies = new MirrorCopies(await browserPorts());
    const other = (await copies.unlockOffline((await copies.forUser(user.id))!, "pw"))!;
    useSession.getState().signOut();
    await vi.waitFor(() => expect(other.locked).toBe(true));
    expect(useOfflineCopy.getState().mirror).toBeNull();
  });

  test("deleting the account removes the copy from this device", async () => {
    oneTab();
    await openedOnline();
    useSession.getState().end("Your account is deleted, with all your vaults and notes.", "wipe");
    await vi.waitFor(async () => expect(await new MirrorCopies(await browserPorts()).forUser(user.id)).toBeNull());
    expect(useOfflineCopy.getState()).toMatchObject({ mirror: null, header: null });
    expect(useSession.getState().notice).toBe("Your account is deleted, with all your vaults and notes.");
  });

  test("a sign-in in a tab that holds the copy wraps it again under the password", async () => {
    oneTab();
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

test("a sign-in locks the copies of other users open in the browser, and leaves the user's own open", async () => {
  await adasCopy();
  const copies = new MirrorCopies(await browserPorts());
  const adas = (await copies.unlockOffline((await copies.forUser(user.id))!, "pw"))!;
  server({ "POST /api/account/mirror-slots": () => json(201, { id: "s2", token: "tok-bob", key, vaults }) });
  const bobs = await copies.create({ userId: bob.id, username: "bob", password: "pw", label: "Test", idleMs: 60_000 });
  useSession.getState().signedIn({ user: bob, previousSignInAt: null });
  server({ "POST /api/account/mirror-slots/contact": () => json(200, { id: "s2", key, vaults }) });
  await openCopy(bob.id);
  await vi.waitFor(() => expect(adas.locked).toBe(true));
  expect([bobs.locked, useOfflineCopy.getState().mirror?.locked]).toEqual([false, false]);
  bobs.lock();
});

test("the sign-in page removes a copy whose slot is gone, and keeps one whose slot is there", async () => {
  oneTab();
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

test("the sweep sends no cookie, and with a live cookie it still wipes a removed copy", async () => {
  oneTab();
  await adasCopy();
  const sent: (RequestCredentials | undefined)[] = [];
  // The browser holds someone's live session: with the cookie, the server answers for that session.
  server({
    "GET /api/session": (init) => (
      sent.push(init?.credentials),
      init?.credentials === "omit" ? json(401, { error: "Gone.", code: "mirror-wipe" }) : json(200, { user: bob, previousSignInAt: null })
    ),
  });
  await sweepRemovedCopies();
  expect(sent).toEqual(["omit"]);
  expect(await new MirrorCopies(await browserPorts()).forUser(user.id)).toBeNull();
});

describe("the copy's token in Kumbu-Mirror", () => {
  const mirrorHeaders = (calls: { key: string; headers: Headers }[]) => calls.map((c) => [c.key, c.headers.get("Kumbu-Mirror")]);

  test("goes with requests under its owner's session, and with nobody signed in but not with a sign-in", async () => {
    await openedOnline();
    let calls = server({ "GET /api/vaults": () => json(200, []) });
    await api.GET("/api/vaults");
    expect(mirrorHeaders(calls)).toEqual([["GET /api/vaults", "tok-ada"]]);

    await lockCopy();
    useSession.getState().signOut();
    calls = server({ "GET /api/vaults": () => json(401, { error: "Sign in first." }), "POST /api/session": () => json(401, { error: "Wrong." }) });
    await api.GET("/api/vaults");
    await api.POST("/api/session", { body: { username: "bob", password: "pw", remember: false } });
    expect(mirrorHeaders(calls)).toEqual([["GET /api/vaults", "tok-ada"], ["POST /api/session", null]]);
  });

  test("a sign-in request goes out with no offline header, whoever is signed in", async () => {
    await openedOnline();
    const calls = server({ "POST /api/session": () => json(401, { error: "Wrong." }) });
    await api.POST("/api/session", { body: { username: "ada", password: "pw", remember: false } });
    expect(mirrorHeaders(calls)).toEqual([["POST /api/session", null]]);
  });

  test("after Bob's sign-in no request carries Ada's copy token", async () => {
    await openedOnline();
    await lockCopy();
    useSession.getState().signedIn({ user: bob, previousSignInAt: null });
    const calls = server({ "GET /api/vaults": () => json(200, []), "POST /api/ws-ticket": () => json(200, { ticket: "t" }) });
    await api.GET("/api/vaults");
    await api.POST("/api/ws-ticket");
    expect(mirrorHeaders(calls)).toEqual([["GET /api/vaults", null], ["POST /api/ws-ticket", null]]);
  });
});

test("a sign-out made offline reaches the server as soon as Kumbu² answers again", async () => {
  localStorage.setItem("kumbu.signOutPending", "1");
  const calls = server({ "GET /api/version": () => json(200, {}), "DELETE /api/session": () => noContent() });
  await api.GET("/api/version");
  await vi.waitFor(() => expect(signOutPending()).toBe(false));
  expect(calls.map((c) => c.key)).toContain("DELETE /api/session");
});
