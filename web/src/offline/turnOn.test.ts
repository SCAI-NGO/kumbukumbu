// SPDX-License-Identifier: AGPL-3.0-or-later
import "fake-indexeddb/auto";
import { MirrorCopies, type CopyHeader, type SealedMirror } from "eidolon-core/store/mirror";
import { afterEach, describe, expect, test, vi } from "vitest";
import { useSession } from "../state/session";
import { json, noContent, user } from "../test/app";
import { useOfflineCopy } from "./copy";
import { openCopy } from "./mode";
import { browserPorts } from "./ports";
import { turnOff, turnOn } from "./turnOn";

afterEach(() => useOfflineCopy.getState().clear());

test("a copy of another user held by the tab is locked and let go when someone without one signs in", async () => {
  const lock = vi.fn();
  const header = { copy: "someone-elses" } as CopyHeader;
  useOfflineCopy.getState().set(header, { header, locked: false, lock } as unknown as SealedMirror);
  await openCopy("u-other");
  expect(lock).toHaveBeenCalled();
  expect(useOfflineCopy.getState().header).toBeNull();
});

describe("turning offline on while the signed-in user changes", () => {
  const key = Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url");
  const bob = { ...user, id: "u2", username: "bob", display: "Bob" };
  const slots = vi.fn(() => json(201, { id: "s1", token: "tok-ada", key, vaults: [] }));

  function server(confirmed: Promise<void>) {
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const request = input instanceof Request ? input : null;
      const path = new URL(request?.url ?? String(input), "http://localhost").pathname;
      const method = request?.method ?? init?.method ?? "GET";
      if (`${method} ${path}` === "POST /api/session/confirm") return (await confirmed, noContent());
      if (`${method} ${path}` === "POST /api/account/mirror-slots") return slots();
      throw new Error(`Unexpected request: ${method} ${path}`);
    }));
  }

  afterEach(async () => {
    vi.unstubAllGlobals();
    slots.mockClear();
    useSession.getState().reset();
    await (await browserPorts()).storage.write({ removePrefix: [""] });
  });

  test("Bob signed in before the slot is asked for: no slot is made, nothing kept", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    let confirm!: () => void;
    server(new Promise((r) => (confirm = r)));
    const turning = turnOn({ id: user.id, username: "ada" }, "pw").catch(() => {});
    useSession.getState().signedIn({ user: bob, previousSignInAt: null });
    confirm();
    await turning;
    expect(slots).not.toHaveBeenCalled();
    expect(useOfflineCopy.getState().mirror).toBeNull();
  });

  test("Bob signed in while the slot was made: Ada's copy is locked, not kept for him", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    server(Promise.resolve());
    slots.mockImplementationOnce(() => {
      useSession.getState().signedIn({ user: bob, previousSignInAt: null });
      return json(201, { id: "s1", token: "tok-ada", key, vaults: [] });
    });
    await turnOn({ id: user.id, username: "ada" }, "pw");
    expect(useOfflineCopy.getState().mirror).toBeNull();
  });

  test("Bob signed in and opened his copy while Ada's was being removed: his copy stays", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    server(Promise.resolve());
    await turnOn({ id: user.id, username: "ada" }, "pw");
    const ada = useOfflineCopy.getState().mirror!;
    const header = ada.header;
    let contacting!: () => void;
    const contacted = new Promise<void>((r) => (contacting = r));
    let answer!: (r: Response) => void;
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const request = input instanceof Request ? input : null;
      const path = new URL(request?.url ?? String(input), "http://localhost").pathname;
      const method = request?.method ?? init?.method ?? "GET";
      if (`${method} ${path}` === "POST /api/account/mirror-slots/contact") return (contacting(), new Promise<Response>((r) => (answer = r)));
      if (`${method} ${path}` === "DELETE /api/account/mirror-slots/s1") return noContent();
      throw new Error(`Unexpected request: ${method} ${path}`);
    }));
    // One tab: the removal takes the copy's lock, and jsdom has no Web Locks.
    Object.defineProperty(navigator, "locks", { value: { request: (_: string, fn: () => unknown) => fn() }, configurable: true });
    const turningOff = turnOff(header);
    await contacted;
    // Signed out (the copy locks) and Bob in, with his own copy open, while the contact gets no answer.
    ada.lock();
    useSession.getState().signedIn({ user: bob, previousSignInAt: null });
    const bobs = { header: { ...header, copy: "bobs" }, locked: false, lock: vi.fn(), remove: vi.fn() } as unknown as SealedMirror;
    useOfflineCopy.getState().set(bobs.header, bobs);
    answer(json(401, { error: "Signed out." }));
    await turningOff;
    expect(bobs.remove).not.toHaveBeenCalled();
    expect(useOfflineCopy.getState().mirror).toBe(bobs);
    expect(await new MirrorCopies(await browserPorts()).forUser(user.id)).toBeNull();
    Reflect.deleteProperty(navigator, "locks");
  });
});
