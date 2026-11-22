// SPDX-License-Identifier: AGPL-3.0-or-later
// One sync engine per mirrored vault, for a host that holds many vaults, as a browser session does (offline mode
// design §2, gap 1). Mirrors are keyed by vault id, never by name: a name is per member (an alias) and can change
// while the device is away, so the names the requests need are read from the vault list on every refresh.
import type { ListedVault } from "../engine/ports";
import { SerialQueue } from "../engine/queue";

/** A vault as the host mirrors it: its state and files under `id`, its requests addressed by `name`. */
export interface VaultRef { id: string; name: string; canWrite: boolean; }

/** What the host needs of an engine: SyncEngine has it. */
export interface MirrorEngine { start(): Promise<void>; stop(): Promise<void>; check(full: boolean): Promise<void>; }

export type LeftBecause = "gone" | "discreet";

export interface VaultHostOptions {
  /** The caller's vaults, `GET /api/vaults`. */
  listVaults(): Promise<ListedVault[]>;
  /** Ids of the vaults this device holds a mirror of, from earlier visits too. */
  held(): readonly string[];
  /** The device's choice of the vaults it mirrors. */
  mirrors(vault: VaultRef): boolean;
  /** An engine for the vault, not started yet. */
  createEngine(vault: VaultRef): MirrorEngine;
  /**
   * The vault is no longer the caller's (gone: access lost, or deleted) or it is discreet, which is never mirrored
   * (design §4.3): its engine is stopped, and the host purges its mirror.
   */
  onLeft(id: string, because: LeftBecause): void;
}

export class VaultHost {
  private readonly running = new Map<string, { vault: VaultRef; engine: MirrorEngine }>();
  private readonly queue = new SerialQueue();

  constructor(private readonly o: VaultHostOptions) {}

  /**
   * Reads the vault list and brings the engines in line with it: on start and on every reconnect. A failed list
   * throws and changes nothing, so the engines keep their own retries.
   */
  refresh(): Promise<void> {
    return this.queue.push(() => this.align(), "refresh");
  }

  engine(id: string): MirrorEngine | undefined {
    return this.running.get(id)?.engine;
  }

  /** A "changed" hint names the vault as the caller sees it now. */
  hinted(name: string): void {
    for (const { vault, engine } of this.running.values()) if (vault.name === name) void engine.check(false);
  }

  stop(): Promise<void> {
    return this.queue.push(async () => {
      const all = [...this.running.values()];
      this.running.clear();
      await Promise.all(all.map(({ engine }) => engine.stop()));
    });
  }

  private async align(): Promise<void> {
    const listed = new Map<string, ListedVault & { id: string }>();
    for (const v of await this.o.listVaults()) if (v.id !== undefined) listed.set(v.id, { ...v, id: v.id });

    for (const id of new Set([...this.o.held(), ...this.running.keys()])) {
      const now = listed.get(id);
      if (now !== undefined && now.discreet !== true) continue;
      await this.stopOne(id);
      this.o.onLeft(id, now === undefined ? "gone" : "discreet");
    }

    for (const v of listed.values()) {
      if (v.discreet === true) continue;
      const vault: VaultRef = { id: v.id, name: v.name, canWrite: v.canWrite };
      const current = this.running.get(v.id);
      if (!this.o.mirrors(vault)) {
        await this.stopOne(v.id);
        continue;
      }
      if (current !== undefined && current.vault.name === vault.name) continue;
      // Renamed: the engine and its API address the vault by name, so a new one takes over the same mirror.
      await this.stopOne(v.id);
      const engine = this.o.createEngine(vault);
      this.running.set(v.id, { vault, engine });
      await engine.start();
    }
  }

  private async stopOne(id: string): Promise<void> {
    const current = this.running.get(id);
    if (current === undefined) return;
    this.running.delete(id);
    await current.engine.stop();
  }
}
