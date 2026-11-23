// SPDX-License-Identifier: AGPL-3.0-or-later
// The core's PasswordKdf (design §4.2): Argon2id in a worker of its own, started on the first use and kept for the
// tab. Without workers (the tests' jsdom) it runs here.
import type { PasswordKdf } from "eidolon-core/store/crypto";

type Pending = { resolve(key: Uint8Array<ArrayBuffer>): void; reject(e: Error): void };

let worker: Worker | null = null;
const pending = new Map<number, Pending>();
let next = 0;

function started(): Worker {
  if (worker !== null) return worker;
  const w = new Worker(new URL("./kdfWorker.ts", import.meta.url), { type: "module", name: "kdf" });
  w.onmessage = (event: MessageEvent<{ id: number; key?: Uint8Array<ArrayBuffer>; error?: string }>) => {
    const p = pending.get(event.data.id);
    pending.delete(event.data.id);
    if (event.data.key) p?.resolve(event.data.key);
    else p?.reject(new Error(event.data.error ?? "The password hash failed."));
  };
  w.onerror = () => {
    for (const p of pending.values()) p.reject(new Error("The password hash failed."));
    pending.clear();
    w.terminate();
    worker = null;
  };
  worker = w;
  return w;
}

export const browserKdf: PasswordKdf = {
  async derive(password, salt, params) {
    if (typeof Worker === "undefined") return (await import("./argon2")).argon2id(password, salt, params);
    const id = next++;
    const key = new Promise<Uint8Array<ArrayBuffer>>((resolve, reject) => pending.set(id, { resolve, reject }));
    started().postMessage({ id, password, salt: salt.slice(), params });
    return key;
  },
};
