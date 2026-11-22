// SPDX-License-Identifier: AGPL-3.0-or-later
// Unlocking and locking the offline copy (offline mode design §3a): the sign-in form offline, the idle lock, and a
// sign-out offline that the server hears of later.
import "fake-indexeddb/auto";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MirrorCopies } from "eidolon-core/store/mirror";
import { afterEach, describe, expect, test, vi } from "vitest";
import { useSession } from "../state/session";
import { json, noContent, renderApp } from "../test/app";
import { useOfflineCopy } from "./copy";
import { useOfflineMode } from "./mode";
import { browserPorts } from "./ports";
import { endPendingSignOut, lockCopy, onBeforeLock, signOutOffline, signOutPending, unlockOffline } from "./unlock";

const key = Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url");
const vaults = [{ id: "v1", tag: "t1", canWrite: true, pending: false }];

/** A copy of Ada's in this browser's IndexedDB, as turning offline on leaves it; locked again. */
async function adasCopy(idleMs = 60_000) {
  vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(json(201, { id: "s1", token: "tok", key, vaults }))));
  const copies = new MirrorCopies(await browserPorts());
  const mirror = await copies.create({ userId: "u1", username: "Ada", password: "pw", label: "Test", idleMs, iterations: 1000 });
  mirror.lock();
  vi.unstubAllGlobals();
}

afterEach(async () => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  await lockCopy();
  useOfflineCopy.getState().clear();
  useSession.getState().reset();
  localStorage.clear();
  const ports = await browserPorts();
  await ports.storage.write({ removePrefix: [""] });
});

describe("the sign-in form offline", () => {
  test("opens the copy with the username and password, and says nothing more for a wrong one", async () => {
    await adasCopy();
    expect(await unlockOffline("ada", "wrong")).toBe(false);
    expect(await unlockOffline("Grace", "pw")).toBe(false);
    expect(await unlockOffline("ada", "pw")).toBe(true);
    expect(useOfflineCopy.getState().mirror?.locked).toBe(false);
  });

  test("says what signing in does offline, and hides Keep me signed in", async () => {
    useOfflineMode.getState().failed();
    renderApp("/signin");
    expect(await screen.findByText("You're offline. Signing in opens the notes kept on this device.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Keep me signed in on this device")).toBeNull();
  });

  test("a wrong password offline is the form's error", async () => {
    await adasCopy();
    useOfflineMode.getState().failed();
    renderApp("/signin");
    await userEvent.type(await screen.findByLabelText("Username"), "ada");
    await userEvent.type(screen.getByLabelText("Password"), "nope");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByText("The username or password is wrong, or this device keeps no notes of this user.")).toBeInTheDocument();
  });
});

test("an offline unlock locks after the idle limit; the editor's text goes into the copy first", async () => {
  await adasCopy(1_000);
  const wrote = vi.fn();
  const stop = onBeforeLock(wrote);
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  expect(await unlockOffline("ada", "pw")).toBe(true);
  useSession.getState().signedIn({ user: { id: "", username: "ada", display: "ada", role: "user" }, previousSignInAt: null });
  await vi.advanceTimersByTimeAsync(1_000);
  expect(wrote).toHaveBeenCalled();
  expect(useOfflineCopy.getState().mirror).toBeNull();
  expect(useOfflineCopy.getState().header).not.toBeNull();
  expect(useSession.getState().ended).toBe(true);
  stop();
});

describe("a sign-out offline", () => {
  test("keeps a mark until the server could be told", async () => {
    await signOutOffline(false);
    expect(signOutPending()).toBe(true);
    vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new TypeError("Failed to fetch"))));
    await endPendingSignOut();
    expect(signOutPending()).toBe(true);
    const fetch = vi.fn((_url: string, _init: RequestInit) => Promise.resolve(noContent()));
    vi.stubGlobal("fetch", fetch);
    await endPendingSignOut();
    expect(fetch.mock.calls.map(([url, init]) => `${init.method} ${url}`)).toEqual(["DELETE /api/session"]);
    expect(signOutPending()).toBe(false);
  });

  test("with the copy removed, its changes go with it", async () => {
    await adasCopy();
    await unlockOffline("ada", "pw");
    await signOutOffline(true);
    expect(useOfflineCopy.getState().header).toBeNull();
    expect(await new MirrorCopies(await browserPorts()).forUsername("ada")).toBeNull();
  });

  test("without, the copy stays sealed for the next sign-in", async () => {
    await adasCopy();
    await unlockOffline("ada", "pw");
    await signOutOffline(false);
    expect(useOfflineCopy.getState().mirror).toBeNull();
    expect(await unlockOffline("ada", "pw")).toBe(true);
  });
});
