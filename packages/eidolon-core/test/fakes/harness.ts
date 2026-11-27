// SPDX-License-Identifier: AGPL-3.0-or-later
// Wires a SyncEngine to the fakes, with Vitest's fake timers as the clock.
import { vi } from "vitest";
import { SyncEngine } from "../../src/engine/engine";
import type { FirstSyncSummary } from "../../src/engine/firstSync";
import type { CameBack, Clock, StateStore, SyncState, SyncStatus } from "../../src/engine/ports";
import { versionOf } from "../../src/engine/version";
import { FakeFiles } from "./fakeFiles";
import { FakeServer } from "./fakeServer";

export class MemoryState implements StateStore {
  saves = 0;
  constructor(public state: SyncState = { bases: {}, cursor: null, lastFullAt: null, lastSyncAt: null }) {}
  load(): Promise<SyncState> { return Promise.resolve(structuredClone(this.state)); }
  save(state: SyncState): Promise<void> { this.saves++; this.state = structuredClone(state); return Promise.resolve(); }
}

export const fakeClock: Clock = {
  now: () => Date.now(),
  setTimeout: (fn, ms) => setTimeout(fn, ms),
  clearTimeout: (h) => clearTimeout(h as ReturnType<typeof setTimeout>),
};

export interface Harness {
  engine: SyncEngine; server: FakeServer; files: FakeFiles; state: MemoryState;
  conflicts: [string, string][]; cameBack: [string, CameBack][]; authLost: number; statuses: SyncStatus[]; firstSyncsDone: FirstSyncSummary[]; cancelled: number;
}

/**
 * Notes in both places, already agreed: `synced` is on the server, on disk and in the bases.
 * `localOnly`/`remoteOnly` are on one side only, without a base.
 */
export async function harness(o: {
  synced?: Record<string, string>; localOnly?: Record<string, string>; remoteOnly?: Record<string, string>;
  caseInsensitive?: boolean; start?: boolean;
  /** Attachments on the server only. */
  remoteFiles?: Record<string, Uint8Array>;
  /** A server from before attachments. */
  oldServer?: boolean;
  /** The host's fetch policy (FetchPolicy.wantBytes); none fetches every attachment under the limit. */
  wantBytes?: (path: string, size: number | null) => boolean;
} = {}): Promise<Harness> {
  vi.useFakeTimers({ now: new Date(2026, 9, 27, 10, 15), toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "Date"] });
  const server = new FakeServer(() => Date.now());
  const files = new FakeFiles();
  files.caseInsensitive = o.caseInsensitive ?? false;
  server.attachmentsSupported = o.oldServer !== true;
  const bases: Record<string, string> = {};
  for (const [p, t] of Object.entries(o.synced ?? {})) {
    await server.seed(p, t);
    files.files.set(p, t);
    bases[p] = await versionOf(t);
  }
  for (const [p, t] of Object.entries(o.localOnly ?? {})) files.files.set(p, t);
  for (const [p, t] of Object.entries(o.remoteOnly ?? {})) await server.seed(p, t);
  for (const [p, b] of Object.entries(o.remoteFiles ?? {})) await server.seedFile(p, b);
  const state = new MemoryState({ bases, cursor: null, lastFullAt: null, lastSyncAt: null });
  const h: Harness = { engine: undefined as unknown as SyncEngine, server, files, state, conflicts: [], cameBack: [], authLost: 0, statuses: [], firstSyncsDone: [], cancelled: 0 };
  h.engine = new SyncEngine({
    vault: "Work", device: "Laptop", local: files, remote: server, state, clock: fakeClock,
    onStatus: (s) => h.statuses.push(s),
    onConflict: (original, copy) => h.conflicts.push([original, copy]),
    onCameBack: (path, why) => h.cameBack.push([path, why]),
    onAuthLost: () => { h.authLost++; },
    onFirstSyncDone: (summary) => h.firstSyncsDone.push(summary),
    onFirstSyncCancelled: () => { h.cancelled++; },
    ...(o.wantBytes === undefined ? {} : { fetchPolicy: { wantBytes: o.wantBytes } }),
  });
  if (o.start !== false) await h.engine.start();
  server.calls.length = 0;
  return h;
}

/** Lets debounces fire and waits for the queue to drain. */
export async function settle(h: Harness, ms = 0): Promise<void> {
  await vi.advanceTimersByTimeAsync(ms);
  await h.engine.idle();
}

/** Waits, outside the fake timers, until `cond` holds (for steps that await real async work such as hashing). */
export async function waitFor(cond: () => boolean, ms = 5000): Promise<void> {
  const start = performance.now();
  while (!cond()) {
    if (performance.now() - start > ms) throw new Error("waitFor: condition never held");
    await new Promise((r) => setImmediate(r));
  }
}
