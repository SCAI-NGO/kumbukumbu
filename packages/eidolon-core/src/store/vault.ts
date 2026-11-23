// SPDX-License-Identifier: AGPL-3.0-or-later
// One vault's sealed mirror (offline mode design §4.1, §5, §3a "The first download"): what Kumbu² lists (the
// inventory), what this device holds (the bodies), and the engine's state, each a sealed record. It is the engine's
// LocalFiles and StateStore, and the vault's tree for the offline UI.
//
// Records, under the copy's prefix (`c/<copy>/`), every name an HMAC (crypto.ts), so storage shows no vault id or path:
//   m/<vault>         the vault's meta (id, tag, rights, whether its note texts are all here)
//   v/<vault>/h       the head: which generation of the state and of each entry is the current one
//   v/<vault>/s       the engine's SyncState
//   v/<vault>/e/<p>   a path's entry: what Kumbu² lists for it, and what this device holds
//   v/<vault>/b/<p>   a path's bytes, bound to the version its entry names
// Every write takes the copy's lock across tabs and reads the entry afresh inside it, so two tabs never write on a
// stale check; afterwards the other tabs are told which paths changed. Each write stamps its records with a new
// generation and moves the head to it in the same transaction: an older record put back in its place (by someone
// without the key, who can still copy records) no longer matches the head, and is read as damage, never acted on.
import type {
  FetchPolicy, FileKind, FileStat, LocalFiles, RemoteApi, RemoteEntry, StateStore, SyncState, Version, VersionsAnswer,
} from "../engine/ports";
import { eligibility, isMarker } from "../engine/paths";
import { versionOf, versionOfBytes } from "../engine/version";
import { deriveKeys, nameOf, open, seal, type MirrorKeys } from "./crypto";
import type { StorePorts, TabChannel } from "./ports";

type Bytes = Uint8Array<ArrayBuffer>;

/** Images and PDFs up to this size come with the notes, after them (design §6). */
export const SMALL_ATTACHMENT_BYTES = 1_048_576;

/** A path as the mirror knows it. Neither part: the record is gone. */
export interface Entry {
  path: string;
  /** From the last version list: what Kumbu² holds. */
  remote?: { version: Version; kind?: FileKind; size?: number };
  /** What this device holds. `text`: written as a note's text (its version is of the text, newlines normalised). */
  local?: { version: Version; size: number; mtime: number; text: boolean };
  /** Deleted on this device: the one way a path with a base comes to have no bytes here (see VaultMirror.damaged). */
  deleted?: true;
}

/** An entry with these bytes here: no longer deleted. */
function here(entry: Entry | undefined, path: string, local: NonNullable<Entry["local"]>): Entry {
  return { path, ...(entry?.remote ? { remote: entry.remote } : {}), local };
}

/** An entry with its bytes gone from this device: by a deletion here, or let go (`deleted` false). */
function gone(entry: Entry, path: string, deleted: boolean): Entry {
  return { path, ...(entry.remote ? { remote: entry.remote } : {}), ...(deleted ? { deleted: true as const } : {}) };
}

/** A record that should be here doesn't open, or is missing: damage on the device, never a deletion made on it. */
export class DamagedRecordError extends Error {
  constructor(path: string) { super(`The offline copy of ${path} is damaged on this device; it is left as it is in Kumbu².`); }
}

export interface VaultMeta {
  id: string;
  /** The tag the vault was mirrored under (design §4.6): another one in a contact means it was re-keyed. */
  tag: string;
  canWrite: boolean;
  /** Every note's text is here: from now on small attachments come too. */
  notesDone: boolean;
  /**
   * The vault's name as the user last saw it online (a member's alias, renamed at will): offline, paths name vaults
   * by it, and the contact carries no names. The host sets it from the vault list whenever it is online.
   */
  name?: string;
}

/** The editor's unsaved text (WP7 fills it): `LocalFiles.read` gives it, and `writeIf` checks against it. */
export interface Drafts {
  text(path: string): string | null;
  /** The file under an open editor was replaced (null: removed) by a download. */
  replaced?(path: string, content: string | null): void;
}

export class MirrorLockedError extends Error {
  constructor() { super("The offline copy is locked."); }
}

/** The copy-wide part every vault's records need: ports, keys and names. */
export class Records {
  private keys: MirrorKeys | null = null;
  readonly channel: TabChannel;

  constructor(readonly p: StorePorts, readonly copy: string) {
    this.channel = p.channel(copy);
  }

  static async unlock(p: StorePorts, copy: string, raw: Bytes): Promise<Records> {
    const records = new Records(p, copy);
    records.keys = await deriveKeys(p.crypto, raw);
    return records;
  }

  get prefix(): string { return `c/${this.copy}/`; }
  get lockName(): string { return `kumbu-mirror:${this.copy}`; }
  get locked(): boolean { return this.keys === null; }

  lock(): void { this.keys = null; }

  private get k(): MirrorKeys {
    if (this.keys === null) throw new MirrorLockedError();
    return this.keys;
  }

  name(label: string): Promise<string> { return nameOf(this.p.crypto, this.k.name, label); }

  /** `bind`: what else the record belongs to (a body: its entry's version), so it opens only with that. */
  seal(key: string, plain: Bytes, bind?: string): Promise<Bytes> { return seal(this.p.crypto, this.k.seal, plain, aad(key, bind)); }

  /** Null for a record that is missing or doesn't open under this key (moved or changed on disk, or bound elsewhere). */
  async open(key: string, sealed: Bytes | null, bind?: string): Promise<Bytes | null> {
    if (sealed === null) return null;
    const plain = await open(this.p.crypto, this.k.seal, sealed, aad(key, bind));
    if (plain === null) console.warn("A record of the offline copy doesn't open; it is left out.");
    return plain;
  }

  async sealJson(key: string, value: unknown): Promise<[string, Bytes]> {
    return [key, await this.seal(key, encoder.encode(JSON.stringify(value)))];
  }

  async openJson<T>(key: string, sealed: Bytes | null): Promise<T | null> {
    const plain = await this.open(key, sealed);
    return plain === null ? null : (JSON.parse(decoder.decode(plain)) as T);
  }

  async vaultNames(id: string): Promise<{ meta: string; data: string }> {
    const v = await this.name(`vault\0${id}`);
    return { meta: `${this.prefix}m/${v}`, data: `${this.prefix}v/${v}/` };
  }
}

const aad = (key: string, bind: string | undefined) => (bind === undefined ? key : `${key}\0${bind}`);
const encoder = new TextEncoder();
const decoder = new TextDecoder();

/** Which generation of each record is current. A record of another generation is an older one put back. */
interface Head { g: number; state: number | null; seqs: Record<string, number> }
const emptyHead = (): Head => ({ g: 0, state: null, seqs: {} });

const emptyState = (): SyncState => ({ bases: {}, cursor: null, lastFullAt: null, lastSyncAt: null });
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;

/** A record as stored: its value, and the generation it was written in. */
type Stamped<K extends string, T> = { g?: number } & { [k in K]?: T };

export class VaultMirror {
  private readonly entries = new Map<string, Entry>();
  private readonly names = new Map<string, string>();
  private state: SyncState = emptyState();
  private lastMtime = 0;
  private onNotesDone: (() => void) | null = null;
  /** Purged, or its copy locked: nothing reads or writes through it any more. */
  private dropped = false;

  private constructor(private readonly r: Records, private meta: VaultMeta, private readonly keys: { meta: string; data: string }) {}

  /** Reads the vault's records into memory; a new vault starts with none. */
  static async open(r: Records, meta: VaultMeta): Promise<VaultMirror> {
    const v = new VaultMirror(r, meta, await r.vaultNames(meta.id));
    // Under the lock, so the head and the records are of one moment.
    await r.p.locks.run(r.lockName, async () => {
      const head = await v.readHead();
      // Entries only: the bodies, attachments among them, stay on disk until asked for.
      for (const [key, sealed] of await r.p.storage.scan(`${v.keys.data}e/`)) {
        const entry = v.current(head, await r.openJson<Stamped<"entry", Entry>>(key, sealed));
        if (entry !== undefined) v.remember(entry);
      }
      v.state = (await v.readState(head)) ?? emptyState();
    });
    return v;
  }

  /**
   * The vault leaves the copy (purged, or the copy locked). From now on every port throws: an engine still running
   * on it fails its check rather than read an emptied mirror as files deleted here, and trash them in Kumbu².
   */
  drop(): void {
    this.dropped = true;
    this.entries.clear();
  }

  assertHeld(): void {
    if (this.dropped) throw new MirrorLockedError();
  }

  get id(): string { return this.meta.id; }
  get canWrite(): boolean { return this.meta.canWrite; }
  get tag(): string { return this.meta.tag; }
  get name(): string | undefined { return this.meta.name; }
  get metaKey(): string { return this.keys.meta; }
  get dataPrefix(): string { return this.keys.data; }
  private get stateKey(): string { return `${this.keys.data}s`; }
  private get headKey(): string { return `${this.keys.data}h`; }

  /** A vault taken into the copy: its meta, and nothing else under its name, whatever an earlier mirror left there. */
  static async writeMeta(r: Records, meta: VaultMeta): Promise<void> {
    const keys = await r.vaultNames(meta.id);
    await r.p.locks.run(r.lockName, async () => r.p.storage.write({ removePrefix: [keys.data], put: [await r.sealJson(keys.meta, meta)] }));
  }

  async setMeta(change: Partial<Omit<VaultMeta, "id">>): Promise<void> {
    this.assertHeld();
    this.meta = { ...this.meta, ...change };
    await this.r.p.storage.write({ put: [await this.r.sealJson(this.keys.meta, this.meta)] });
  }

  /** Every path the mirror knows, from the inventory or on this device: the tree, before the texts are all here. */
  list(): readonly Entry[] {
    return [...this.entries.values()];
  }

  entry(path: string): Entry | undefined {
    return this.entries.get(path);
  }

  /** The bytes this device holds for a path, null if none (yet). */
  async bytes(path: string): Promise<Bytes | null> {
    this.assertHeld();
    if (this.entries.get(path)?.local === undefined) return null;
    const local = this.entries.get(path)!.local!;
    const body = await this.bodyKey(path);
    const bytes = await this.r.open(body, await this.r.p.storage.get(body), local.version);
    // The entry says the bytes are here: missing, unopenable or of another version, they were damaged, not deleted.
    if (bytes === null) throw new DamagedRecordError(path);
    return bytes;
  }

  /**
   * A path the engine agreed on (it has a base) with neither bytes here nor the mark of a deletion made here: its
   * records were removed or changed on the device. The engine must not read that as a deletion and trash it in
   * Kumbu².
   */
  damaged(path: string): boolean {
    const entry = this.entries.get(path);
    return entry?.local === undefined && entry?.deleted !== true && this.state.bases[path] !== undefined;
  }

  /** Removed on this device and not yet in Kumbu²: listed by the last version list, but gone for the person. */
  removedHere(path: string): boolean {
    return this.entries.get(path)?.local === undefined && this.state.bases[path] !== undefined;
  }

  /** Changes made here that Kumbu² doesn't have yet: paths whose bytes differ from the agreed version, deletions too. */
  waiting(): number {
    let n = 0;
    for (const e of this.entries.values()) if (e.local !== undefined && this.state.bases[e.path] !== e.local.version) n++;
    for (const path of Object.keys(this.state.bases)) if (this.entries.get(path)?.local === undefined) n++;
    return n;
  }

  /** Another tab changed these paths: read them, and the state, again. */
  async refresh(paths: readonly string[]): Promise<void> {
    await this.r.p.locks.run(this.r.lockName, async () => {
      if (this.dropped) return;
      const head = await this.readHead();
      for (const path of paths) {
        const entry = await this.readEntry(head, path);
        if (entry === undefined) this.entries.delete(path);
        else this.entries.set(path, entry);
      }
      this.state = (await this.readState(head)) ?? this.state;
    });
  }

  // The engine's ports.

  stateStore(): StateStore {
    return {
      load: async () => {
        this.assertHeld();
        return clone(this.state);
      },
      save: async (state) => {
        this.assertHeld();
        // A check that went through to the end has every note's text here: the small attachments' turn (design §3a).
        const notesNowDone = await this.r.p.locks.run(this.r.lockName, async () => {
          this.assertHeld();
          const head = await this.readHead();
          head.state = ++head.g;
          this.state = clone(state);
          const put = [await this.r.sealJson(this.stateKey, { g: head.g, state }), await this.r.sealJson(this.headKey, head)];
          const done = !this.meta.notesDone && state.lastSyncAt !== null;
          if (done) {
            this.meta = { ...this.meta, notesDone: true };
            put.push(await this.r.sealJson(this.keys.meta, this.meta));
          }
          await this.r.p.storage.write({ put });
          return done;
        });
        this.r.channel.post({ type: "changed", vault: this.id, paths: [] });
        if (notesNowDone) this.onNotesDone?.();
      },
    };
  }

  /**
   * Which bytes come, in the order of design §3a: none until every note's text is here, then images and PDFs up to
   * 1 MB. `onNotesDone` runs once when that point is reached: the host asks the engine for a full check then, which
   * fetches what is now wanted.
   */
  downloadOrder(onNotesDone: () => void, wanted?: (path: string) => boolean): FetchPolicy {
    this.onNotesDone = onNotesDone;
    return {
      wantBytes: (path, size) => this.meta.notesDone && size !== null && (size <= SMALL_ATTACHMENT_BYTES || wanted?.(path) === true),
    };
  }

  /** Here as Kumbu² has it: nothing changed on this device that hasn't uploaded. */
  unchanged(path: string): boolean {
    const local = this.entries.get(path)?.local;
    return local !== undefined && this.state.bases[path] === local.version;
  }

  /**
   * Drops the bytes of attachments the device no longer wants (design §6, the budget), and the engine's memory of
   * them, so the path is as one never downloaded: the fetch policy decides it again. Only while the vault's engine is
   * stopped, as it holds the state in memory; a path changed here in the meantime is left as it is.
   */
  async evict(paths: readonly string[]): Promise<void> {
    const done = await this.change(paths, (p, entry) => Promise.resolve(
      entry?.local === undefined || this.state.bases[p] !== entry.local.version ? false
        : { entry: gone(entry, p, false), body: null }));
    const state = clone(this.state);
    paths.forEach((p, i) => {
      if (!done[i]) return;
      delete state.bases[p];
      delete state.hashed?.[p];
    });
    await this.stateStore().save(state);
  }

  /** The engine's RemoteApi, which records every version list in the inventory before the engine acts on it. */
  inventory(remote: RemoteApi): RemoteApi {
    return new InventoryRemote(remote, (answer) => this.record(answer));
  }

  files(drafts?: Drafts): LocalFiles {
    return new MirrorFiles(this, drafts);
  }

  private async record(answer: VersionsAnswer): Promise<void> {
    if (answer.unchanged) return;
    const listed = new Map<string, RemoteEntry>(answer.entries.map((e) => [e.path, e]));
    const changed = new Map<string, Entry["remote"]>();
    for (const [path, e] of listed) {
      const remote = e.version === null ? undefined : { version: e.version, ...(e.kind ? { kind: e.kind } : {}), ...(e.size !== undefined ? { size: e.size } : {}) };
      if (JSON.stringify(remote) !== JSON.stringify(this.entries.get(path)?.remote)) changed.set(path, remote);
    }
    if (answer.full) for (const e of this.entries.values()) if (e.remote !== undefined && !listed.has(e.path)) changed.set(e.path, undefined);
    if (changed.size === 0) return;
    await this.change([...changed.keys()], async (path, entry) => {
      const remote = changed.get(path);
      return { entry: { ...(entry ?? { path }), remote } };
    });
  }

  // Writes.

  /**
   * Changes paths in one transaction under the copy's lock, each decided on its entry as storage has it now. A
   * decision: the new entry (null removes the path), and the new bytes (null removes them, undefined keeps them);
   * false leaves the path as it is.
   */
  async change(
    paths: readonly string[],
    decide: (path: string, entry: Entry | undefined) => Promise<{ entry: Entry | null; body?: Bytes | null } | false>,
  ): Promise<boolean[]> {
    this.assertHeld();
    return this.r.p.locks.run(this.r.lockName, async () => {
      this.assertHeld();
      const head = await this.readHead();
      const put: [string, Bytes][] = [];
      const remove: string[] = [];
      const done: boolean[] = [];
      const after = new Map<string, Entry | null>();
      for (const path of paths) {
        const key = await this.entryKey(path);
        const now = await this.readEntry(head, path);
        const outcome = await decide(path, now);
        if (outcome === false) {
          done.push(false);
          after.set(path, now ?? null);
          continue;
        }
        done.push(true);
        let entry = outcome.entry;
        if (entry !== null && entry.local === undefined && entry.remote === undefined && !(entry.deleted && this.state.bases[path] !== undefined)) entry = null;
        if (entry === null) {
          remove.push(key);
          delete head.seqs[path];
        } else {
          head.seqs[path] = ++head.g;
          put.push(await this.r.sealJson(key, { g: head.g, entry }));
        }
        const body = await this.bodyKey(path);
        if (outcome.body === null || entry === null) remove.push(body);
        else if (outcome.body !== undefined) put.push([body, await this.r.seal(body, outcome.body, entry.local?.version)]);
        after.set(path, entry);
      }
      if (put.length > 0 || remove.length > 0) await this.r.p.storage.write({ put: [...put, await this.r.sealJson(this.headKey, head)], remove });
      for (const [path, entry] of after) {
        if (entry === null) this.entries.delete(path);
        else this.entries.set(path, entry);
      }
      if (done.some(Boolean)) this.r.channel.post({ type: "changed", vault: this.id, paths: paths.filter((_, i) => done[i]) });
      return done;
    });
  }

  /** A modification time later than every earlier one, so a rewrite within the same millisecond still counts. */
  nextMtime(): number {
    this.lastMtime = Math.max(this.r.p.clock.now(), this.lastMtime + 1);
    return this.lastMtime;
  }

  private async readHead(): Promise<Head> {
    return (await this.r.openJson<Head>(this.headKey, await this.r.p.storage.get(this.headKey))) ?? emptyHead();
  }

  private async readEntry(head: Head, path: string): Promise<Entry | undefined> {
    const key = await this.entryKey(path);
    return this.current(head, await this.r.openJson<Stamped<"entry", Entry>>(key, await this.r.p.storage.get(key)));
  }

  private async readState(head: Head): Promise<SyncState | null> {
    const stamped = await this.r.openJson<Stamped<"state", SyncState>>(this.stateKey, await this.r.p.storage.get(this.stateKey));
    if (stamped === null) return null;
    if (stamped.g === head.state && stamped.state !== undefined) return stamped.state;
    console.warn("An older state of the offline copy was put back; it is left out.");
    return null;
  }

  /** The entry of a record, if it is the generation the head names for its path. */
  private current(head: Head, stamped: Stamped<"entry", Entry> | null): Entry | undefined {
    if (stamped === null) return undefined;
    const entry = stamped.entry;
    if (entry !== undefined && typeof entry.path === "string" && stamped.g === head.seqs[entry.path]) return entry;
    console.warn("An older record of the offline copy was put back; it is left out.");
    return undefined;
  }

  private remember(entry: Entry): void {
    this.entries.set(entry.path, entry);
    if (entry.local !== undefined) this.lastMtime = Math.max(this.lastMtime, entry.local.mtime);
  }

  private async pathName(path: string): Promise<string> {
    let name = this.names.get(path);
    if (name === undefined) {
      name = await this.r.name(`path\0${this.meta.id}\0${path}`);
      this.names.set(path, name);
    }
    return name;
  }

  private async entryKey(path: string): Promise<string> { return `${this.keys.data}e/${await this.pathName(path)}`; }
  async bodyKey(path: string): Promise<string> { return `${this.keys.data}b/${await this.pathName(path)}`; }
}

/** The vault's mirror as the engine's LocalFiles: case-sensitive, folders implicit in the paths below them. */
class MirrorFiles implements LocalFiles {
  readonly caseInsensitive = false;

  constructor(private readonly v: VaultMirror, private readonly drafts?: Drafts) {}

  /** As Obsidian's index: nothing with a dot segment, so no folder marker. */
  async list(): Promise<string[]> {
    this.v.assertHeld();
    return this.held().filter((p) => !p.split("/").some((s) => s.startsWith(".")));
  }

  async listMarkers(): Promise<string[]> {
    this.v.assertHeld();
    return this.held().filter((p) => isMarker(p) && eligibility(p, true) === "sync");
  }

  async read(path: string): Promise<string | null> {
    this.v.assertHeld();
    if (this.v.damaged(path)) throw new DamagedRecordError(path);
    const draft = this.drafts?.text(path) ?? null;
    if (draft !== null && this.v.entry(path)?.local !== undefined) return draft;
    const bytes = await this.v.bytes(path);
    return bytes === null ? null : decoder.decode(bytes);
  }

  async size(path: string): Promise<number | null> {
    this.v.assertHeld();
    return this.v.entry(path)?.local?.size ?? null;
  }

  async stat(path: string): Promise<FileStat | null> {
    this.v.assertHeld();
    const local = this.v.entry(path)?.local;
    return local === undefined ? null : { size: local.size, mtime: local.mtime };
  }

  async readBinary(path: string): Promise<ArrayBuffer | null> {
    if (this.v.damaged(path)) throw new DamagedRecordError(path);
    return (await this.v.bytes(path))?.buffer ?? null;
  }

  async writeIf(path: string, expected: Version | null, content: string): Promise<boolean> {
    const bytes = encoder.encode(content);
    const version = await versionOf(content);
    const [done] = await this.v.change([path], async (p, entry) => {
      if ((await this.textVersion(p, entry)) !== expected) return false;
      return { entry: here(entry, p, { version, size: bytes.byteLength, mtime: this.v.nextMtime(), text: true }), body: bytes };
    });
    if (done && this.drafts?.text(path) != null) this.drafts.replaced?.(path, content);
    return done!;
  }

  async writeBinaryIf(path: string, expected: Version | null, buffer: ArrayBuffer): Promise<boolean> {
    const bytes = new Uint8Array(buffer.slice(0));
    const version = await versionOfBytes(bytes);
    const [done] = await this.v.change([path], async (p, entry) => {
      const exists = entry?.local !== undefined;
      // A marker's content is never compared: it exists or it doesn't.
      if (isMarker(p) && (expected !== null || exists)) return false;
      if ((await this.bytesVersion(p, entry)) !== expected) return false;
      return { entry: here(entry, p, { version, size: bytes.byteLength, mtime: this.v.nextMtime(), text: false }), body: bytes };
    });
    if (!done && isMarker(path)) return expected !== null && this.v.entry(path)?.local !== undefined;
    return done!;
  }

  /** The mirror keeps no trash of its own: Kumbu² keeps its own, and the mirror's copy of a file is only a copy. */
  async trashToLocal(path: string): Promise<void> {
    await this.removeIf(path, () => Promise.resolve(true));
  }

  async trashIf(path: string, expected: Version): Promise<boolean> {
    const done = await this.removeIf(path, async (p, entry) => (await this.textVersion(p, entry)) === expected);
    if (done) this.drafts?.replaced?.(path, null);
    return done;
  }

  trashBinaryIf(path: string, expected: Version): Promise<boolean> {
    return this.removeIf(path, async (p, entry) => isMarker(p) || (await this.bytesVersion(p, entry)) === expected);
  }

  async rename(from: string, to: string): Promise<void> {
    const bytes = await this.v.bytes(from);
    const local = this.v.entry(from)?.local;
    if (bytes === null || local === undefined) return;
    await this.v.change([to, from], (p, entry) => Promise.resolve(p === to
      ? { entry: here(entry, to, { ...local, mtime: this.v.nextMtime() }), body: bytes }
      : { entry: entry === undefined ? null : gone(entry, p, true), body: null }));
  }

  // Folders are only the paths below them: none is ever empty, and none is made by itself.
  removeFolderIfEmpty(): Promise<boolean> { return Promise.resolve(false); }
  isEmptyFolder(): Promise<boolean> { return Promise.resolve(false); }
  madeFolder(): boolean { return false; }

  private held(): string[] {
    return this.v.list().filter((e) => e.local !== undefined).map((e) => e.path);
  }

  private async removeIf(path: string, check: (path: string, entry: Entry) => Promise<boolean>): Promise<boolean> {
    const [done] = await this.v.change([path], async (p, entry) => {
      if (entry?.local === undefined || !(await check(p, entry))) return false;
      return { entry: gone(entry, p, true), body: null };
    });
    return done!;
  }

  /** The version of the text the user sees: the draft's if one is open, else the stored text's. */
  private async textVersion(path: string, entry: Entry | undefined): Promise<Version | null> {
    if (entry?.local === undefined) return null;
    const draft = this.drafts?.text(path) ?? null;
    if (draft !== null) return versionOf(draft);
    if (entry.local.text) return entry.local.version;
    const bytes = await this.v.bytes(path);
    return bytes === null ? null : versionOf(decoder.decode(bytes));
  }

  private async bytesVersion(path: string, entry: Entry | undefined): Promise<Version | null> {
    if (entry?.local === undefined) return null;
    if (!entry.local.text) return entry.local.version;
    const bytes = await this.v.bytes(path);
    return bytes === null ? null : versionOfBytes(bytes);
  }
}

/** Passes every call on; a version list is recorded in the inventory first. */
class InventoryRemote implements RemoteApi {
  constructor(private readonly remote: RemoteApi, private readonly record: (answer: VersionsAnswer) => Promise<void>) {}

  async versions(since: string | null, state: string | null): Promise<VersionsAnswer> {
    const answer = await this.remote.versions(since, state);
    await this.record(answer);
    return answer;
  }

  read(path: string) { return this.remote.read(path); }
  put(path: string, content: string, base: Version | null) { return this.remote.put(path, content, base); }
  remove(path: string, base: Version) { return this.remote.remove(path, base); }
  move(from: string, to: string) { return this.remote.move(from, to); }
  readFile(path: string) { return this.remote.readFile(path); }
  putFile(path: string, bytes: ArrayBuffer, base: Version | null) { return this.remote.putFile(path, bytes, base); }
  removeFile(path: string, base: Version) { return this.remote.removeFile(path, base); }
  vaults() { return this.remote.vaults(); }
}
