// SPDX-License-Identifier: AGPL-3.0-or-later
// A copy on this "device" for tests: fake IndexedDB, the browser's WebCrypto, a slot answering at once, and the vaults
// Work (writable) and Team (read-only), turned on as the offline writes' vaults.
import { IDBFactory } from "fake-indexeddb";
import { MirrorCopies } from "eidolon-core/store/mirror";
import type { StorePorts } from "eidolon-core/store/ports";
import type { VaultMirror } from "eidolon-core/store/vault";
import { openRecordStorage } from "./idbStorage";
import { setOfflineWrites } from "./writing";

export async function offlineCopy() {
  const key = Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url");
  const listed = [{ id: "v-work", tag: "t1", canWrite: true, pending: false }, { id: "v-team", tag: "t2", canWrite: false, pending: false }];
  const ports: StorePorts = {
    storage: await openRecordStorage("offline-test", new IDBFactory()),
    crypto: { subtle: crypto.subtle, randomBytes: (n) => crypto.getRandomValues(new Uint8Array(n)) },
    slots: { create: () => Promise.resolve({ id: "s1", token: "tok", key, vaults: listed }), contact: () => Promise.resolve({ id: "s1", key, vaults: listed }) },
    locks: { run: (_name, fn) => fn(), hold: () => ({ acquired: Promise.resolve(), release() {} }) },
    channel: () => ({ post() {}, listen: () => () => {} }),
    clock: { now: () => Date.now(), setTimeout: (fn, ms) => setTimeout(fn, ms), clearTimeout: (h) => clearTimeout(h as number) },
  };
  const mirror = await new MirrorCopies(ports).create({ userId: "u1", username: "ada", password: "pw", label: "Test", idleMs: 1000, iterations: 1000 });
  const vaults: Record<string, VaultMirror> = { Work: await mirror.adopt("v-work"), Team: await mirror.adopt("v-team") };
  const written: [string, string][] = [];
  const on = () => setOfflineWrites({
    vaultFor: (path) => {
      const found = vaults[path.split("/")[1]!];
      return found ? { mirror: found, inVault: path.split("/").slice(2).join("/") } : null;
    },
    written: (vault, path) => written.push([vault, path]),
  });
  on();
  return { vaults, written, on };
}
