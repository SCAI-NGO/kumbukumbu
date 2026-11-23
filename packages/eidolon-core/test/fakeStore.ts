// SPDX-License-Identifier: AGPL-3.0-or-later
// The sealed store's ports in memory: a browser's IndexedDB, mirror slot, Web Locks and BroadcastChannel, and Node's
// WebCrypto as the browser's. Two "tabs" share one FakeBrowser. Not in fakes/, which the Obsidian plugin vendors
// with the sync code: the store isn't vendored.
import { webcrypto } from "node:crypto";
import type { Clock } from "../src/engine/ports";
import type { CryptoPort, PasswordKdf } from "../src/store/crypto";
import type { MirrorSlotsPort, RecordStorage, SlotAnswer, SlotVault, StorePorts, TabChannel, TabLocks, TabMessage } from "../src/store/ports";

type Bytes = Uint8Array<ArrayBuffer>;

/**
 * A stand-in for the host's Argon2id, fast for the tests: SHA-256 over the parameters, salt and password. It keeps
 * what the store relies on (another password, salt or parameter gives another key); the real one is the web app's.
 */
export const fakeKdf: PasswordKdf = {
  async derive(password, salt, params) {
    const label = new TextEncoder().encode(`${JSON.stringify(params)}\0${Buffer.from(salt).toString("base64url")}\0${password}`);
    return new Uint8Array(await webcrypto.subtle.digest("SHA-256", label));
  },
};

export const nodeCrypto: CryptoPort = {
  subtle: webcrypto.subtle,
  randomBytes: (n) => webcrypto.getRandomValues(new Uint8Array(n)),
  kdf: fakeKdf,
};

export class MemoryStorage implements RecordStorage {
  readonly records = new Map<string, Bytes>();
  writes = 0;

  get(key: string): Promise<Bytes | null> {
    const value = this.records.get(key);
    return Promise.resolve(value === undefined ? null : value.slice());
  }

  scan(prefix: string): Promise<[string, Bytes][]> {
    return Promise.resolve([...this.records].filter(([k]) => k.startsWith(prefix)).sort(([a], [b]) => (a < b ? -1 : 1)).map(([k, v]) => [k, v.slice()]));
  }

  write(change: { put?: [string, Bytes][]; remove?: string[]; removePrefix?: string[] }): Promise<void> {
    this.writes++;
    for (const prefix of change.removePrefix ?? []) for (const k of [...this.records.keys()]) if (k.startsWith(prefix)) this.records.delete(k);
    for (const k of change.remove ?? []) this.records.delete(k);
    for (const [k, v] of change.put ?? []) this.records.set(k, v.slice());
    return Promise.resolve();
  }

  /** Every byte in storage, keys and values, as one Latin-1 string: for looking for plaintext. */
  dump(): string {
    return [...this.records].map(([k, v]) => `${k}=${Buffer.from(v).toString("latin1")}`).join("\n");
  }
}

/** The server's side of the mirror slots: one user's, with the vaults a contact lists. */
export class FakeSlots implements MirrorSlotsPort {
  readonly slots = new Map<string, { id: string; key: string; waiting: number }>();
  vaults: SlotVault[] = [];
  contacts: { token: string; waiting: number }[] = [];
  /** Thrown by the next call: AuthError or OfflineError. */
  fail: Error | null = null;
  private n = 0;

  create(_label: string): Promise<SlotAnswer & { token: string }> {
    if (this.fail) return Promise.reject(this.fail);
    const id = `slot${++this.n}`;
    const token = `token${this.n}`;
    const key = Buffer.from(webcrypto.getRandomValues(new Uint8Array(32))).toString("base64url");
    this.slots.set(token, { id, key, waiting: 0 });
    return Promise.resolve({ id, key, token, vaults: structuredClone(this.vaults) });
  }

  contact(token: string, waiting: number): Promise<SlotAnswer | "gone"> {
    if (this.fail) return Promise.reject(this.fail);
    this.contacts.push({ token, waiting });
    const slot = this.slots.get(token);
    if (slot === undefined) return Promise.resolve("gone");
    slot.waiting = waiting;
    return Promise.resolve({ id: slot.id, key: slot.key, vaults: structuredClone(this.vaults) });
  }

  removeAll(): void {
    this.slots.clear();
  }
}

/** Web Locks within one browser: a queue per name. */
export class FakeLocks implements TabLocks {
  private readonly tails = new Map<string, Promise<void>>();

  async run<T>(name: string, fn: () => Promise<T>): Promise<T> {
    let release!: () => void;
    const before = this.tails.get(name) ?? Promise.resolve();
    this.tails.set(name, before.then(() => new Promise<void>((r) => { release = r; })));
    await before;
    try {
      return await fn();
    } finally {
      release();
    }
  }

  hold(name: string): { acquired: Promise<void>; release(): void } {
    let cancelled = false;
    let free: (() => void) | undefined;
    let announce!: () => void;
    const acquired = new Promise<void>((r) => { announce = r; });
    void this.run(name, () => (cancelled ? Promise.resolve() : new Promise<void>((r) => { free = r; announce(); })));
    return { acquired, release: () => { cancelled = true; free?.(); } };
  }
}

/** One BroadcastChannel name in one browser: a message reaches every other listener, never its sender, later. */
export class FakeBus {
  private readonly listeners = new Map<string, Set<{ fn: (m: TabMessage) => void; owner: object }>>();
  readonly posted: TabMessage[] = [];

  channel(name: string): TabChannel {
    const owner = {};
    const set = this.listeners.get(name) ?? new Set();
    this.listeners.set(name, set);
    return {
      post: (m) => {
        this.posted.push(m);
        for (const l of set) if (l.owner !== owner) setImmediate(() => l.fn(structuredClone(m)));
      },
      listen: (fn) => {
        const l = { fn, owner };
        set.add(l);
        return () => set.delete(l);
      },
    };
  }
}

export const realClock: Clock = {
  now: () => Date.now(),
  setTimeout: (fn, ms) => setTimeout(fn, ms),
  clearTimeout: (h) => clearTimeout(h as ReturnType<typeof setTimeout>),
};

/** One browser: storage, locks and channels shared by its tabs; the server's slots. */
export class FakeBrowser {
  readonly storage = new MemoryStorage();
  readonly slots = new FakeSlots();
  readonly locks = new FakeLocks();
  readonly bus = new FakeBus();

  /** A tab's ports: its own channels, everything else shared. */
  tab(clock: Clock = realClock): StorePorts {
    return { storage: this.storage, crypto: nodeCrypto, slots: this.slots, locks: this.locks, channel: (copy) => this.bus.channel(copy), clock };
  }
}

/** Lets messages between tabs arrive. */
export async function delivered(): Promise<void> {
  for (let i = 0; i < 20; i++) await new Promise((r) => setImmediate(r));
}
