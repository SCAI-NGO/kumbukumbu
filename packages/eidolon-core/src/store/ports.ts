// SPDX-License-Identifier: AGPL-3.0-or-later
// The sealed store's ports (offline mode design §2a): storage, the mirror slot, and what tabs share. The crypto port
// is in crypto.ts. A browser fills them with IndexedDB, fetch, Web Locks and a BroadcastChannel.
import type { Clock } from "../engine/ports";
import type { CryptoPort } from "./crypto";

type Bytes = Uint8Array<ArrayBuffer>;

/** Records by string key, in key order. Every value but a copy's header is sealed before it gets here. */
export interface RecordStorage {
  get(key: string): Promise<Bytes | null>;
  /** Every record whose key starts with `prefix`. */
  scan(prefix: string): Promise<[string, Bytes][]>;
  /** One transaction: all of it or nothing. */
  write(change: { put?: [string, Bytes][]; remove?: string[]; removePrefix?: string[] }): Promise<void>;
}

/** A vault the browser may mirror now, as a contact lists it (design §4.6). */
export interface SlotVault { id: string; tag: string; canWrite: boolean; pending: boolean; }
/** `key`: the mirror key, Base64Url. */
export interface SlotAnswer { id: string; key: string; vaults: SlotVault[]; }

/**
 * The mirror slot (`/api/account/mirror-slots`, design §4.6), a Kumbu²-only call the host makes. Both throw the
 * engine's AuthError (no session) and OfflineError (no server), as the REST client does.
 */
export interface MirrorSlotsPort {
  create(label: string): Promise<SlotAnswer & { token: string }>;
  /**
   * "gone": the 404 with `mirror-wipe`, the slot was removed; the whole copy goes. `waiting` null: not counted, as
   * before the copy is open; the server then records that it doesn't know, never "nothing waiting".
   */
  contact(token: string, waiting: number | null): Promise<SlotAnswer | "gone">;
}

/** Web Locks: one holder per name across the browser's tabs. */
export interface TabLocks {
  /** Runs `fn` while holding `name`, after any other holder. */
  run<T>(name: string, fn: () => Promise<T>): Promise<T>;
  /** Waits to hold `name` until `release`; a release before `acquired` gives up waiting. */
  hold(name: string): { acquired: Promise<void>; release(): void };
}

/** What a tab tells the copy's other unlocked tabs. Only unlocked tabs listen. */
export type TabMessage =
  | { type: "changed"; vault: string; paths: string[] }
  | { type: "purged"; vault: string | null }
  | { type: "choice" }
  /** Signed out: every tab of the copy locks. */
  | { type: "locked" };

export interface TabChannel {
  post(message: TabMessage): void;
  /** Returns a function that stops listening. */
  listen(fn: (message: TabMessage) => void): () => void;
}

export interface StorePorts {
  storage: RecordStorage;
  crypto: CryptoPort;
  slots: MirrorSlotsPort;
  locks: TabLocks;
  /** A channel for one copy: named after its id. */
  channel(copy: string): TabChannel;
  clock: Clock;
}
