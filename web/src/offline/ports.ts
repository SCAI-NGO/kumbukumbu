// SPDX-License-Identifier: AGPL-3.0-or-later
// The browser's side of the Eidolon core's sealed store (offline mode design §2a): IndexedDB, WebCrypto, the mirror
// slot, Web Locks and a BroadcastChannel per copy, and the persistence the copy asks the browser for (§5).
import type { StorePorts, TabChannel, TabLocks, TabMessage } from "eidolon-core/store/ports";
import { openRecordStorage } from "./idbStorage";
import { mirrorSlots } from "./slots";

const locks: TabLocks = {
  // The grant callback returns a promise, which request() passes on: awaited, it is the callback's result.
  run: async (name, fn) => await navigator.locks.request(name, fn),
  hold(name) {
    const giveUp = new AbortController();
    let free: (() => void) | undefined;
    const acquired = new Promise<void>((announce) => {
      navigator.locks
        .request(name, { signal: giveUp.signal }, () => new Promise<void>((resolve) => { free = resolve; announce(); }))
        .catch(() => {}); // given up before it was ours
    });
    return { acquired, release: () => (free === undefined ? giveUp.abort() : free()) };
  },
};

function channel(copy: string): TabChannel {
  const bc = new BroadcastChannel(`kumbu-mirror:${copy}`);
  return {
    post: (message) => bc.postMessage(message),
    listen(fn) {
      const heard = (e: MessageEvent<TabMessage>) => fn(e.data);
      bc.addEventListener("message", heard);
      return () => bc.removeEventListener("message", heard);
    },
  };
}

export async function browserPorts(): Promise<StorePorts> {
  return {
    storage: await openRecordStorage(),
    crypto: { subtle: crypto.subtle, randomBytes: (n) => crypto.getRandomValues(new Uint8Array(n)) },
    slots: mirrorSlots,
    locks,
    channel,
    clock: { now: () => Date.now(), setTimeout: (fn, ms) => setTimeout(fn, ms), clearTimeout: (h) => clearTimeout(h as number) },
  };
}

/** Asks the browser to keep the copy through clean-ups (on turning offline on, §5); whether it does. */
export async function keepThroughCleanUps(): Promise<boolean> {
  return (await navigator.storage?.persist?.()) ?? false;
}

/** For Settings, Offline: what the origin uses, about what it may, and whether the copy is kept through clean-ups. */
export async function storageUse(): Promise<{ usage: number | null; quota: number | null; persisted: boolean }> {
  const estimate = await navigator.storage?.estimate?.().catch(() => undefined);
  return { usage: estimate?.usage ?? null, quota: estimate?.quota ?? null, persisted: (await navigator.storage?.persisted?.()) ?? false };
}
