// SPDX-License-Identifier: AGPL-3.0-or-later
// The Eidolon core's sealed store over this app's adapters: IndexedDB, the browser's WebCrypto and Argon2id.
import "fake-indexeddb/auto";
import { IDBFactory } from "fake-indexeddb";
import { MirrorCopies } from "eidolon-core/store/mirror";
import type { MirrorSlotsPort, StorePorts } from "eidolon-core/store/ports";
import { expect, test } from "vitest";
import { openRecordStorage } from "./idbStorage";
import { browserKdf } from "./kdf";

test("a copy is created, sealed in IndexedDB, and opened again offline with the password", async () => {
  const key = Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url");
  const vaults = [{ id: "v1", tag: "t1", canWrite: true, pending: false }];
  const slots: MirrorSlotsPort = {
    create: () => Promise.resolve({ id: "s1", token: "tok", key, vaults }),
    contact: () => Promise.resolve({ id: "s1", key, vaults }),
  };
  const ports: StorePorts = {
    storage: await openRecordStorage("test", new IDBFactory()),
    crypto: { subtle: crypto.subtle, randomBytes: (n) => crypto.getRandomValues(new Uint8Array(n)), kdf: browserKdf },
    slots,
    locks: { run: (_name, fn) => fn(), hold: () => ({ acquired: Promise.resolve(), release() {} }) },
    channel: () => ({ post() {}, listen: () => () => {} }),
    clock: { now: () => Date.now(), setTimeout: (fn, ms) => setTimeout(fn, ms), clearTimeout: (h) => clearTimeout(h as number) },
  };
  const copies = new MirrorCopies(ports);
  const mirror = await copies.create({ userId: "u1", username: "ada", password: "pw", label: "Test", idleMs: 1000 });
  await (await mirror.adopt("v1")).files().writeIf("Plan.md", null, "Sealed plan");
  mirror.lock();

  const again = await copies.unlockOffline((await copies.forUsername("Ada"))!, "pw");
  expect(await again!.vault("v1")!.files().read("Plan.md")).toBe("Sealed plan");
});
