// SPDX-License-Identifier: AGPL-3.0-or-later
// The sync engines of an unlocked copy (offline mode design §3, §6): one per vault the device keeps, run by the one
// tab that leads the copy, with the download order and the device's choice as their fetch policy, and the budget for
// attachments opened online kept after every change of either.
import { SyncEngine } from "../engine/engine";
import { SerialQueue } from "../engine/queue";
import type { Clock, ListedVault, LocalFiles, RemoteApi, SyncStatus } from "../engine/ports";
import { VaultHost, type MirrorEngine, type VaultRef } from "../host/vaults";
import { evictions, forget, mirrors, wanted } from "./choice";
import type { SealedMirror } from "./mirror";
import { SMALL_ATTACHMENT_BYTES, type VaultMirror } from "./vault";

export interface MirrorEnginesOptions {
  mirror: SealedMirror;
  /** The caller's vaults, `GET /api/vaults`. */
  listVaults(): Promise<ListedVault[]>;
  /** The REST client for a vault, addressed by the caller's name for it, which the server checks against the id. */
  remote(vaultName: string, vaultId: string): RemoteApi;
  /** The device's name in conflict copies. */
  device: string;
  clock: Clock;
  /** The engine's files of a vault, the editor's drafts with them; by default the mirror's own. */
  files?(vault: VaultMirror): LocalFiles;
  /** A vault was taken into the copy, under the name the caller gives it now. */
  adopted?(vault: VaultRef): Promise<void>;
  onStatus?(vaultId: string, status: SyncStatus): void;
  onConflict?(vaultId: string, original: string, copy: string): void;
  onAuthLost?(): void;
}

export class MirrorEngines {
  private readonly engines = new Map<string, SyncEngine>();
  private readonly host: VaultHost;
  private readonly queue = new SerialQueue();
  private stopped = false;
  /** This tab leads the copy: its engines run here. */
  private leading = false;

  constructor(private readonly o: MirrorEnginesOptions) {
    const m = o.mirror;
    this.host = new VaultHost({
      listVaults: o.listVaults,
      held: () => m.held().map((v) => v.id),
      mirrors: (vault) => mirrors(m.choice, vault.id),
      createEngine: (vault) => this.engineFor(vault),
      // Unlocked offline, the vault list may be another session's (the cookie is the browser's, not the copy's): a vault
      // it leaves out goes only once a contact of this copy has gone through, which is the copy's own word.
      onLeft: (id, because) => {
        if (because === "discreet" || m.contacted) void m.purge(id);
      },
    });
    // The host may listen too (the mode counts the changes waiting): both hear it.
    const heard = m.onChanged;
    m.onChanged = (vault, paths) => {
      heard?.(vault, paths);
      const engine = this.engines.get(vault);
      const held = m.vault(vault);
      if (engine === undefined || held === undefined) return;
      for (const p of paths) {
        if (held.entry(p)?.local !== undefined) engine.localChanged(p);
        else engine.localDeleted(p);
      }
    };
    m.onChoice = () => void this.refresh(true);
    // Before the records go: an engine left running would read the emptied mirror as every file deleted here.
    m.onDrop = async (id) => {
      this.engines.delete(id);
      await this.host.drop(id);
    };
  }

  /** Waits to lead the copy, then starts the engines. Resolves only in the tab that leads. */
  async start(): Promise<void> {
    await this.o.mirror.lead();
    if (this.stopped) return;
    this.leading = true;
    await this.refresh();
  }

  /**
   * Brings the engines in line with the vault list and the device's choice: on start, every reconnect, and each
   * change of the choice (`recheck`: the engines look at every path again, as the fetch policy may say yes now). A
   * vault no longer chosen is dropped from the copy (Settings has said what that loses). The vault list answered, so
   * the engines that ran before send what waits now rather than at their next retry, which a run of failures puts
   * minutes away.
   */
  async refresh(recheck = false): Promise<void> {
    if (!this.leading || this.stopped || this.o.mirror.locked) return;
    const running = new Set(this.engines.values());
    await this.host.refresh();
    const m = this.o.mirror;
    for (const vault of m.held()) if (!mirrors(m.choice, vault.id)) await m.purge(vault.id, false);
    await Promise.all([...this.engines.values()].filter((e) => recheck || running.has(e)).map((e) => e.check(recheck)));
    await this.keepBudget();
  }

  /** A path written in this tab (another tab's writes come over the copy's channel, `onChanged`). */
  localChanged(vaultId: string, path: string): void {
    const engine = this.engines.get(vaultId);
    if (engine === undefined || this.stopped) return;
    if (this.o.mirror.vault(vaultId)?.entry(path)?.local !== undefined) engine.localChanged(path);
    else engine.localDeleted(path);
  }

  /** A "changed" hint, naming the vault as the caller sees it now. */
  hinted(vaultName: string): void {
    if (this.leading && !this.stopped) this.host.hinted(vaultName);
  }

  /**
   * An attachment opened online (design §6): kept on this device from now on, within the budget, which makes room by
   * letting the least recently opened go. One larger than the whole budget isn't kept. In a tab that doesn't lead,
   * only the choice changes: the leading tab hears of it and fetches it.
   */
  async opened(vaultId: string, path: string): Promise<void> {
    const m = this.o.mirror;
    if (this.stopped || m.locked) return;
    const vault = m.vault(vaultId);
    const size = vault?.entry(path)?.remote?.size;
    // A small one comes with the notes anyway.
    if (vault === undefined || size === undefined || size <= SMALL_ATTACHMENT_BYTES || size > m.choice.budget) return;
    await m.choose((c) => ({ ...c, opened: { ...c.opened, [vaultId]: { ...c.opened[vaultId], [path]: this.o.clock.now() } } }));
    const engine = this.engines.get(vaultId);
    if (engine === undefined) return;
    await engine.check(true);
    await engine.idle();
    await this.keepBudget();
  }

  async stop(): Promise<void> {
    this.stopped = true;
    await this.host.stop();
    this.engines.clear();
  }

  /** Lets attachments opened online go until the rest fits the budget; their engines pause meanwhile (vault.ts). */
  private keepBudget(): Promise<void> {
    return this.queue.push(async () => {
      const m = this.o.mirror;
      if (this.stopped || m.locked) return;
      const gone = evictions(m.choice, m.held());
      if (gone.size === 0) return;
      // First, so the restarted engines' policy says no to them.
      await m.choose((c) => forget(c, gone));
      for (const [id, paths] of gone) {
        const engine = this.engines.get(id);
        await engine?.stop();
        await m.vault(id)?.evict(paths);
        if (engine !== undefined && !this.stopped) await engine.start();
      }
    });
  }

  private engineFor(ref: VaultRef): MirrorEngine {
    const o = this.o;
    let engine: SyncEngine | null = null;
    return {
      start: async () => {
        let vault: VaultMirror;
        try {
          vault = await o.mirror.adopt(ref.id);
        } catch {
          // Not listed by the last contact: not (yet) the device's to keep; the next refresh tries again.
          return;
        }
        await o.adopted?.(ref);
        const sync: SyncEngine = new SyncEngine({
          vault: ref.name, device: o.device, local: o.files?.(vault) ?? vault.files(), remote: vault.inventory(o.remote(ref.name, ref.id)),
          state: vault.stateStore(), clock: o.clock,
          onStatus: (s) => o.onStatus?.(ref.id, s),
          onConflict: (original, copy) => o.onConflict?.(ref.id, original, copy),
          onAuthLost: () => o.onAuthLost?.(),
          // A failed list changes nothing: the engine's own retry comes, and so does the next refresh.
          onVaultChanged: () => void this.refresh().catch(() => {}),
          fetchPolicy: vault.downloadOrder(() => void sync.check(true), (path) => wanted(o.mirror.choice, vault, path)),
        });
        engine = sync;
        this.engines.set(ref.id, sync);
        await sync.start();
      },
      stop: async () => {
        if (engine === null) return;
        if (this.engines.get(ref.id) === engine) this.engines.delete(ref.id);
        await engine.stop();
      },
      check: (full) => engine?.check(full) ?? Promise.resolve(),
    };
  }
}
