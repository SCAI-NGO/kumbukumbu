// SPDX-License-Identifier: AGPL-3.0-or-later
// The sync engine (spec §6): one serial queue per vault, every decision from local, remote and base versions.
import { conflictCopyText } from "./conflict";
import { decide, type Action } from "./decide";
import { makePlan, type Choice, type FirstSyncSummary, type MergePlan } from "./firstSync";
import { attachmentKind, conflictCopyPath, eligibility, isMarker, MARKER } from "./paths";
import {
  AuthError, OfflineError, RejectedError, VaultChangedError,
  type Clock, type FetchPolicy, type FileStat, type MoveResult, type VersionsAnswer, type ConnectionMode, type LocalFiles, type NotSynced, type RemoteApi,
  type StateStore, type SyncState, type SyncStatus, type Version,
} from "./ports";
import { SerialQueue } from "./queue";
import { byteSize, MARKER_VERSION, MAX_BYTES, versionOf, versionOfBytes } from "./version";

export interface EngineOptions {
  vault: string; device: string; local: LocalFiles; remote: RemoteApi; state: StateStore; clock: Clock;
  onStatus(s: SyncStatus): void;
  onConflict(original: string, copy: string): void;
  onAuthLost(): void;
  /** The name this engine asks by is another vault's now: the host reads the vault list again. */
  onVaultChanged?(): void;
  /** A first sync finished, also one that finished on a retry after its first attempt failed. */
  onFirstSyncDone?(summary: FirstSyncSummary): void;
  /** The user closed the first sync's question without an answer: nothing was saved. */
  onFirstSyncCancelled?(): void;
  /** Without one, every attachment under the limit is downloaded. */
  fetchPolicy?: FetchPolicy;
}

/** 15 MB per PDF or image, as the server takes them; notes keep their 1 MiB (MAX_BYTES). */
export const MAX_ATTACHMENT_BYTES = 15_728_640;

/** The most bytes a file at this path may have to sync. */
export function limitOf(path: string): number {
  return attachmentKind(path) === null ? MAX_BYTES : MAX_ATTACHMENT_BYTES;
}

/** How long a folder made here waits before it is looked at: Obsidian's "New folder" names it a moment later. */
export const FOLDER_SETTLE_MS = 5000;

const DEBOUNCE_MS = 2000;
const FULL_AFTER_MS = 25 * 24 * 3600_000;
const RETRY_FIRST_MS = 5000;
const RETRY_MAX_MS = 5 * 60_000;
/** A path decided again more often than this in one go is left for the next trigger. */
const MAX_ROUNDS = 4;
/** Marks a path listed for an unexpected error: never equal to a version, so it is tried again every time. */
const UNEXPECTED = "!";
/** How often a change the server refused is tried again while one is listed: nothing tells the plugin of a new role. */
export const REFUSED_RETRY_MS = 15 * 60_000;
/** The version of an attachment too large to read: never equal to a real one, so it always differs from both sides. */
const TOO_LARGE = "!too-large";
const COPY_NAME = /\(conflict (?:.+ )?\d{4}-\d{2}-\d{2} \d{2}-\d{2}\)( \d+)?\.[^./]+$/;

/**
 * Kumbu² lists the path with a version but doesn't hand its bytes over (they don't open, or the vault is locked): not a
 * deletion. The path is listed and decided again by every check with that version; a real deletion comes in an answer.
 */
class UnservedError extends Error {}
const CANT_OPEN = "Kumbu² can't open this file just now; it is tried again with every sync.";

/** What an operation found out: decide again with this remote version. */
interface Again { remote: Version | null; }

/** Paths are compared as NFC, the form the server and other devices use. */
const keyOf = (path: string) => path.normalize("NFC");

/** A note's text or an attachment's bytes. */
type Content = string | ArrayBuffer;

/**
 * A PDF, an image or a folder marker: its bytes go through the binary ports. Its version is of the bytes, but a
 * marker's is always MARKER_VERSION: it holds nothing, so only whether it exists counts.
 */
const isFile = (path: string) => attachmentKind(path) !== null || isMarker(path);

export class SyncEngine {
  private readonly o: EngineOptions;
  private readonly queue = new SerialQueue();
  private state: SyncState = { bases: {}, cursor: null, lastFullAt: null, lastSyncAt: null };
  private stopped = true;
  private readonly debounces = new Map<string, unknown>();
  /** Folders made here waiting out FOLDER_SETTLE_MS, by key. */
  private readonly folderWaits = new Map<string, unknown>();
  /** Paths with local work not yet done, retried by every check until it succeeds. */
  private readonly dirty = new Set<string>();
  /** Key → the path as the file system spells it (NFD on some, or another case). */
  private readonly localPathOf = new Map<string, string>();
  private readonly attachments = new Set<string>();
  private readonly tooLarge = new Set<string>();
  /** Attachments over MAX_ATTACHMENT_BYTES in Kumbu², by the size the versions list gave (a 0.6.0 server keeps up to 1 GiB). */
  private readonly tooLargeThere = new Set<string>();
  /** Each attachment's size as last listed; an older server lists none. */
  private readonly remoteSizes = new Map<string, number>();
  /** `refused`: a 403 with a code, which rights can undo, unlike a file the server won't take at all. */
  private readonly rejected = new Map<string, { version: Version; detail: string; refused?: boolean }>();
  /** The vault's level as last listed, and as the last check saw it: a change makes the refused changes go again. */
  private rights: string | null = null;
  private checkedRights: string | null = null;
  private refusedRetry: unknown = null;
  /** An older server answered a marker with a 400: none is sent again until the next start. */
  private markersRefused = false;
  private caseClashes = new Map<string, string>();
  private damaged = 0;
  /**
   * Attachments Kumbu² listed but couldn't hand over (UnservedError), with the version it listed: every check decides
   * them again with it, so the cursor moves on (a broken file doesn't hold it) and a start's full check finds them anew.
   */
  private readonly unserved = new Map<string, Version>();
  private supportsAttachments = false;
  /** Others may reach this vault, and a conflict copy syncs to them, so it names no device until the server says the vault isn't shared. */
  private shared = true;
  private readonly conflicts = new Set<string>();
  private connection: ConnectionMode = "polling";
  private offline = false;
  private retryHandle: unknown = null;
  private retryDelay = 0;
  private copiesMade = 0;
  /**
   * Folders a remote delete or move took a note out of. Kumbu² has no empty folders, so each is deleted once it is
   * empty, after the job: a move's new note may land in the same folder later in that job.
   */
  private readonly leftFolders = new Set<string>();
  /** Set while a first sync hasn't finished: checks retry it, and local events wait for it. */
  private firstSyncChoose: ((plan: MergePlan) => Promise<Choice | null>) | null = null;
  private loading: Promise<void> = Promise.resolve();

  constructor(o: EngineOptions) {
    this.o = o;
  }

  /** Loads the state and runs a full check. */
  async start(): Promise<void> {
    this.state = await this.o.state.load();
    this.stopped = false;
    this.markersRefused = false;
    await this.check(true);
  }

  /** Cancels debounces and waits for the queue. Edits not yet uploaded are found by the next start's full check. */
  async stop(): Promise<void> {
    this.stopped = true;
    for (const h of this.debounces.values()) this.o.clock.clearTimeout(h);
    this.debounces.clear();
    for (const h of this.folderWaits.values()) this.o.clock.clearTimeout(h);
    this.folderWaits.clear();
    this.cancelRetry();
    this.armRefusedRetry();
    await this.queue.idle();
  }

  idle(): Promise<void> {
    return this.queue.idle();
  }

  /** The server keeps attachments, as its last versions answer said; false until one has come. */
  get attachmentsSupported(): boolean {
    return this.supportsAttachments;
  }

  /** What a conflict copy may name now: null to leave the device out. */
  get copyDevice(): string | null {
    return this.shared ? null : this.o.device;
  }

  /**
   * Anything but a plain "not shared" counts as shared: the list failed, the vault isn't in it, the field is absent.
   * Returns what it settled on, so a copy goes by its own answer rather than whichever answer came last.
   */
  private async readShared(): Promise<boolean> {
    let shared: boolean;
    try {
      const listed = (await this.o.remote.vaults()).find((v) => v.name === this.o.vault);
      shared = listed?.shared !== false;
      if (listed) this.rights = listed.level ?? (listed.canWrite ? "write" : "read");
    } catch {
      shared = true;
    }
    this.shared = shared;
    return shared;
  }

  /** The hint connection's mode; the engine shows "offline" over it while the server can't be reached. */
  setConnectionMode(mode: ConnectionMode): void {
    this.connection = mode;
    this.emit();
  }

  status(): SyncStatus {
    const notSynced: NotSynced[] = [];
    const other = this.supportsAttachments ? "unsupported" : "attachment";
    for (const path of [...this.attachments].sort()) notSynced.push({ path, reason: other });
    for (const path of [...this.tooLarge].sort()) notSynced.push({ path, reason: "too-large" });
    for (const path of [...this.tooLargeThere].sort()) notSynced.push({ path, reason: "too-large-in-kumbu" });
    for (const [path, r] of [...this.rejected].sort()) notSynced.push({ path, reason: "rejected", detail: r.detail });
    for (const [path, winner] of [...this.caseClashes].sort()) {
      notSynced.push({ path, reason: "case-clash", detail: `Differs only in case from ${winner}.` });
    }
    if (this.damaged > 0) {
      // With attachments the server counts damaged files too.
      const what = this.supportsAttachments ? ["note or file", "notes or files"] : ["note", "notes"];
      notSynced.push({
        path: "", reason: "damaged-vault",
        detail: `${this.damaged} ${what[this.damaged === 1 ? 0 : 1]} in Kumbu² can't be opened, so no local file is trashed because a ${what[0]} is missing.`,
      });
    }
    return {
      mode: this.offline ? "offline" : this.connection,
      // A marker is never shown: the folder it keeps is what the user sees.
      notSynced: notSynced.filter((n) => !isMarker(n.path)),
      conflicts: [...this.conflicts].sort(),
      busy: this.queue.busy,
      lastSyncAt: this.state.lastSyncAt,
    };
  }

  localChanged(path: string): void {
    if (this.stopped) return;
    const kind = eligibility(path, this.supportsAttachments);
    if (kind === "ignored") return;
    if (kind === "attachment") {
      this.attachments.add(path);
      this.emit();
      return;
    }
    const key = keyOf(path);
    this.localPathOf.set(key, path);
    this.dirty.add(key);
    this.cancelDebounce(key);
    this.debounces.set(key, this.o.clock.setTimeout(() => {
      this.debounces.delete(key);
      void this.enqueuePath(key);
    }, DEBOUNCE_MS));
  }

  localDeleted(path: string): void {
    if (this.stopped) return;
    const kind = eligibility(path, this.supportsAttachments);
    if (kind === "attachment") {
      this.attachments.delete(path);
      this.emit();
    }
    if (kind !== "sync") return;
    const key = keyOf(path);
    this.cancelDebounce(key);
    this.dirty.add(key);
    void this.enqueuePath(key);
  }

  localRenamed(from: string, to: string): void {
    if (this.stopped) return;
    const fromSync = eligibility(from, this.supportsAttachments) === "sync";
    const toSync = eligibility(to, this.supportsAttachments) === "sync";
    if (!fromSync || !toSync || isFile(from) !== isFile(to)) {
      // In or out of what syncs (a dot folder, an extension change), or a note becoming a file: a delete and a create.
      this.localDeleted(from);
      this.localChanged(to);
      return;
    }
    const fk = keyOf(from);
    const tk = keyOf(to);
    this.cancelDebounce(fk);
    this.cancelDebounce(tk);
    this.localPathOf.set(tk, to);
    this.dirty.add(fk);
    this.dirty.add(tk);
    void this.run(async () => {
      if (this.firstSyncChoose !== null) return false;
      const base = this.state.bases[fk];
      const now = await this.localVersion(tk, to);
      if (base !== undefined && now === base && this.state.bases[tk] === undefined) {
        let moved: MoveResult = { ok: false };
        try {
          moved = await this.o.remote.move(fk, tk);
        } catch (e) {
          // A name the server refuses: the delete plus create below meets the same rule and lists it.
          if (!(e instanceof RejectedError)) throw e;
        }
        if (moved.ok) {
          // A move takes whatever the note is now, so an agent's edit may have come along: the base stays what
          // this device agreed on, and a different moved version is decided at once (downloaded, or a conflict).
          delete this.state.bases[fk];
          this.state.bases[tk] = base;
          this.dirty.delete(fk);
          await this.save();
          await this.syncPath(tk, moved.version, false);
          return;
        }
      }
      await this.syncPath(fk, this.state.bases[fk] ?? null, false);
      await this.syncPath(tk, this.state.bases[tk] ?? null, false);
    });
  }

  /**
   * A folder made here: once it has waited FOLDER_SETTLE_MS and is still empty, and this plugin didn't make it, it
   * gets a marker. Only while running and online: the full check can't tell an empty folder from one older than the
   * plugin, so a folder made at any other time keeps no marker.
   */
  localFolderCreated(path: string): void {
    if (this.stopped) return;
    const key = keyOf(path);
    this.cancelFolderWait(key);
    this.folderWaits.set(key, this.o.clock.setTimeout(() => {
      this.folderWaits.delete(key);
      // Made during the first sync: as one made before it, not kept.
      if (this.firstSyncChoose !== null) return;
      void this.run(async () => {
        const marker = `${path}/${MARKER}`;
        const keep = !this.stopped && !this.offline && this.firstSyncChoose === null && this.supportsAttachments
          && !this.markersRefused && eligibility(marker, true) === "sync"
          && !this.o.local.madeFolder(path) && (await this.o.local.isEmptyFolder(path));
        if (keep && (await this.o.local.writeBinaryIf(marker, null, new ArrayBuffer(0)))) this.localChanged(marker);
        return false;
      });
    }, FOLDER_SETTLE_MS));
  }

  /** A folder renamed here: its wait starts afresh under the new name, and every marker below it moves. */
  localFolderRenamed(from: string, to: string): void {
    if (this.stopped) return;
    const fk = keyOf(from);
    if (this.folderWaits.has(fk)) {
      this.cancelFolderWait(fk);
      this.localFolderCreated(to);
    }
    // Also a new marker still waiting for its debounce or uploading ("New folder" renamed after the wait): its rename
    // is queued behind the upload, so it moves once its base is there, or the new name is uploaded if there is none.
    const keys = new Set([...Object.keys(this.state.bases), ...this.dirty, ...this.debounces.keys()]);
    for (const key of keys) {
      if (isMarker(key) && key.startsWith(`${fk}/`)) this.localRenamed(key, to + key.slice(fk.length));
    }
  }

  /** A folder deleted here: no marker for it or below it, and the markers it held are removed in Kumbu². */
  localFolderDeleted(path: string): void {
    if (this.stopped) return;
    const key = keyOf(path);
    for (const k of [...this.folderWaits.keys()]) if (k === key || k.startsWith(`${key}/`)) this.cancelFolderWait(k);
    for (const k of Object.keys(this.state.bases)) if (isMarker(k) && k.startsWith(`${key}/`)) this.localDeleted(k);
  }

  /** `full`: start, visible again, hourly; otherwise (hint, "Sync now", poll) only what changed since the cursor. */
  check(full: boolean): Promise<void> {
    if (this.stopped) return Promise.resolve();
    if (this.firstSyncChoose !== null) return this.firstSyncJob().then(() => undefined);
    const last = this.state.lastSyncAt === null ? NaN : Date.parse(this.state.lastSyncAt);
    const asFull = full || this.state.cursor === null || !(this.o.clock.now() - last < FULL_AFTER_MS);
    return this.run(() => this.runCheck(asFull), asFull ? "check:full" : "check");
  }

  /** Every operation goes through the queue; failures end it without losing work. */
  /** `job` returns false when it skipped its work: that says nothing about the server being reachable. */
  private run(job: () => Promise<void | false>, key?: string): Promise<void> {
    const p = this.queue.push(async () => {
      if (this.stopped) return;
      this.emit();
      try {
        if ((await job()) !== false) this.online();
        await this.removeLeftFolders();
      } catch (e) {
        this.failed(e);
      } finally {
        this.armRefusedRetry();
        this.emit();
      }
    }, key);
    return p;
  }

  /** Refused changes become ordinary dirty paths again: the next check sends them, and a conflict keeps both texts. */
  private retryRefused(): void {
    for (const [key, entry] of this.rejected) {
      if (!entry.refused) continue;
      this.rejected.delete(key);
      this.dirty.add(key);
    }
  }

  /** A timer only while something refused is listed, so that a vault without refusals asks nothing more of the server. */
  private armRefusedRetry(): void {
    const waiting = !this.stopped && [...this.rejected.values()].some((r) => r.refused);
    if (!waiting) {
      if (this.refusedRetry !== null) this.o.clock.clearTimeout(this.refusedRetry);
      this.refusedRetry = null;
    } else if (this.refusedRetry === null) {
      this.refusedRetry = this.o.clock.setTimeout(() => {
        this.refusedRetry = null;
        this.retryRefused();
        void this.check(false);
      }, REFUSED_RETRY_MS);
    }
  }

  private enqueuePath(key: string): Promise<void> {
    return this.run(async () => {
      this.dirty.add(key);
      // Decided by the first sync, with its choice, once it runs again.
      if (this.firstSyncChoose !== null) return false;
      // As far as this device knows, the server is at the base; a 409 says otherwise.
      await this.syncPath(key, this.state.bases[key] ?? null, false);
    }, `path:${key}`);
  }

  private async runCheck(full: boolean): Promise<void> {
    const answer = await this.o.remote.versions(full ? null : this.state.cursor, null);
    await this.readShared();
    if (this.checkedRights !== null && this.rights !== this.checkedRights) this.retryRefused();
    this.checkedRights = this.rights;
    const { remote, keys } = await this.resolve(answer);
    const damagedPossible = answer.full && answer.damaged > 0;
    let pathFailed = false;
    for (const key of keys) {
      if (this.stopped) return;
      const base = this.state.bases[key] ?? null;
      const known = answer.full ? (remote.get(key) ?? null) : remote.has(key) ? remote.get(key)! : (this.unserved.get(key) ?? base);
      // Still being typed in: its debounce uploads it (spec §6, rule 6), unless the server has news for it.
      if (this.debounces.has(key) && known === base) continue;
      try {
        await this.syncPath(key, known, damagedPossible);
      } catch (e) {
        if (e instanceof OfflineError || e instanceof AuthError) throw e;
        // One path's local trouble doesn't hold up the others; it is listed and tried again by every check.
        this.rejected.set(key, { version: UNEXPECTED, detail: `Couldn't sync: ${e instanceof Error ? e.message : "unknown error"}` });
        pathFailed = true;
      }
    }
    // The old cursor stays while a path failed, so the next check asks for it again.
    if (!pathFailed) await this.finishCheck(answer);
  }

  private async finishCheck(answer: VersionsAnswer): Promise<void> {
    // Every answer counts the vault's damaged notes; a damaged path is in neither list, so it is never "deleted".
    this.damaged = answer.damaged;
    if (answer.full) this.state.lastFullAt = new Date(this.o.clock.now()).toISOString();
    // Only after every path is done: a check that fails halfway asks again from the old cursor.
    this.state.cursor = answer.cursor;
    this.state.lastSyncAt = new Date(this.o.clock.now()).toISOString();
    await this.save();
  }

  /** The answer's versions by key, without case clashes, and the keys a check decides, in order. */
  private async resolve(answer: VersionsAnswer): Promise<{ remote: Map<string, Version | null>; keys: string[] }> {
    const isFull = answer.full;
    // Files that weren't synced a moment ago may be now: the local side is looked at in full once.
    const newlySupported = answer.attachments && !this.supportsAttachments;
    this.supportsAttachments = answer.attachments;
    const caseInsensitive = this.o.local.caseInsensitive;
    const fold = (k: string) => (caseInsensitive ? k.toLowerCase() : k);

    const localKeys = await this.scanLocal(isFull || newlySupported);

    // Which spelling owns a case-folded path: synced paths first, then the answer in path order.
    if (isFull) this.caseClashes = new Map();
    const owner = new Map<string, string>();
    for (const k of Object.keys(this.state.bases)) owner.set(fold(k), k);
    const remote = new Map<string, Version | null>();
    // A server without attachments lists notes only. With them, a deleted path comes without a kind (the server's
    // `deleted` is a list of paths): its extension says what it was, so syncPath treats it as notes' deletions are.
    const kinds = new Set([undefined, "note", ...(answer.attachments ? ["image", "pdf", "folder"] : [])]);
    const entries = answer.entries
      .filter((e) => kinds.has(e.kind))
      .map((e) => ({ key: keyOf(e.path), version: e.version, size: e.size }))
      .sort((a, b) => cmp(a.key, b.key));
    if (isFull) this.remoteSizes.clear();
    for (const { key, version, size } of entries) {
      if (version !== null && size !== undefined) this.remoteSizes.set(key, size);
      else this.remoteSizes.delete(key);
    }
    for (const { key, version } of entries) {
      const winner = owner.get(fold(key));
      if (caseInsensitive && winner !== undefined && winner !== key) {
        if (version === null) this.caseClashes.delete(key);
        else this.caseClashes.set(key, winner);
        continue;
      }
      if (version !== null) owner.set(fold(key), key);
      this.caseClashes.delete(key);
      remote.set(key, version);
    }

    const keys = new Set<string>([...remote.keys(), ...this.dirty]);
    if (isFull || newlySupported) {
      // Newly supported: an attachment deleted here while the server kept none is deleted there now.
      for (const k of Object.keys(this.state.bases)) if (isFull || isFile(k)) keys.add(k);
      for (const [k, path] of localKeys) {
        if (!isFull && !isFile(k)) continue;
        const winner = owner.get(fold(k)) ?? k;
        if (winner !== k) this.localPathOf.set(winner, path);
        if (!this.caseClashes.has(k)) keys.add(winner);
      }
    }
    return { remote, keys: [...keys].sort(cmp) };
  }

  /**
   * Runs the first sync: merges both sides, asks `choose` once if paths differ. Null when the choice was cancelled
   * (nothing saved). A failure stops the engine and is thrown; running it again finishes the job.
   */
  async firstSync(choose: (plan: MergePlan) => Promise<Choice | null>): Promise<FirstSyncSummary | null> {
    // Set before any await, so a check that comes in now is routed to the first sync by construction.
    this.stopped = false;
    this.firstSyncChoose = choose;
    this.loading = this.o.state.load().then((state) => { this.state = state; });
    const outcome = await this.firstSyncJob();
    if (outcome.error !== null) throw outcome.error;
    return outcome.summary;
  }

  /**
   * One attempt at the first sync. An offline failure schedules another through check(), with the same backoff as
   * every check, and the choice already made is kept in the state, so a retry never asks again.
   */
  private async firstSyncJob(): Promise<{ summary: FirstSyncSummary | null; error: Error | null }> {
    const outcome: { summary: FirstSyncSummary | null; error: Error | null } = { summary: null, error: null };
    await this.queue.push(async () => {
      const choose = this.firstSyncChoose;
      if (this.stopped || choose === null) return;
      this.emit();
      try {
        await this.loading;
        outcome.summary = await this.runFirstSync(choose);
        await this.removeLeftFolders();
        if (this.stopped) {
          // Not a cancelled question: the plugin keeps the connection, and the next start finishes the job.
          outcome.summary = null;
          outcome.error = new Error("The first sync was stopped.");
          return;
        }
        this.firstSyncChoose = null;
        this.online();
        if (outcome.summary === null) {
          this.stopped = true;
          // Also from a retry, whose caller isn't waiting any more: the plugin disconnects.
          this.o.onFirstSyncCancelled?.();
        } else this.o.onFirstSyncDone?.(outcome.summary);
      } catch (e) {
        outcome.error = e instanceof Error ? e : new Error("The first sync failed.");
        this.failed(e);
      } finally {
        this.emit();
      }
    }, "first-sync");
    return outcome;
  }

  private async runFirstSync(choose: (plan: MergePlan) => Promise<Choice | null>): Promise<FirstSyncSummary | null> {
    const answer = await this.o.remote.versions(null, null);
    await this.readShared();
    const { remote, keys } = await this.resolve(answer);
    const sides = new Map<string, { local: Version | null; remote: Version | null }>();
    for (const key of keys) {
      // A marker is never shown, nor asked about (both sides' is the same): the normal decision below syncs it.
      if (this.state.bases[key] !== undefined || isMarker(key)) continue;
      if (this.leftInKumbu(key, remote.get(key) ?? null)) continue;
      const local = await this.localVersion(key, this.localPathOf.get(key) ?? key);
      // Stays as it is whatever the choice, so it is no part of the question: syncPath lists it.
      if (local === TOO_LARGE) {
        this.tooLarge.add(key);
        continue;
      }
      sides.set(key, { local, remote: remote.get(key) ?? null });
    }
    const plan = makePlan(sides, this.status().notSynced);
    let choice: Choice = this.state.pendingChoice ?? "both";
    if (plan.differing.length > 0 && !this.state.pendingChoice) {
      const chosen = await choose(plan);
      if (chosen === null) return null;
      choice = chosen;
      this.state.pendingChoice = chosen;
      await this.save();
    }

    const counts = { uploaded: 0, downloaded: 0, unchanged: 0 };
    const copiesBefore = this.copiesMade;
    const upload = new Set(plan.toUpload);
    const download = new Set(plan.toDownload);
    const same = new Set(plan.same);
    const differing = new Set(plan.differing);
    const damagedPossible = answer.damaged > 0;
    for (const key of keys) {
      // Stopped (Disconnect, unload): no further writes on either side, and no cursor that says it finished.
      if (this.stopped) return null;
      const remoteVersion = remote.get(key) ?? null;
      if (upload.has(key)) {
        await this.syncPath(key, null, false);
        if (this.state.bases[key] !== undefined) counts.uploaded++;
      } else if (download.has(key)) {
        await this.syncPath(key, remoteVersion, false);
        if (this.state.bases[key] !== undefined) counts.downloaded++;
      } else if (same.has(key)) {
        await this.setBase(key, remoteVersion!);
        counts.unchanged++;
      } else if (differing.has(key) && choice === "local") {
        if (await this.keepLocal(key, remoteVersion!)) counts.uploaded++;
      } else if (differing.has(key) && choice === "kumbu") {
        if (await this.keepKumbu(key)) counts.downloaded++;
      } else {
        // Already synced in an earlier, interrupted run, or "Keep both": the normal decision (a conflict copy).
        await this.syncPath(key, remoteVersion, damagedPossible);
      }
    }
    if (this.stopped) return null;
    delete this.state.pendingChoice;
    await this.finishCheck(answer);
    return { ...counts, conflictCopies: this.copiesMade - copiesBefore, notSynced: this.status().notSynced };
  }

  /** The local text (or file) replaces Kumbu²'s, which stays in its history. False if it ended another way. */
  private async keepLocal(key: string, remote: Version): Promise<boolean> {
    const path = this.localPathOf.get(key) ?? key;
    if (await this.tooLargeToRead(key, path)) return false;
    const { content, version: local } = await this.readLocal(key, path);
    if (content === null || local === null) {
      await this.syncPath(key, remote, false);
      return false;
    }
    let again: Again | undefined;
    try {
      again = await this.upload(key, content, remote);
    } catch (e) {
      if (!(e instanceof RejectedError)) throw e;
      this.rejected.set(key, { version: local, detail: e.message, refused: e.status === 403 });
      return false;
    }
    // The server changed since the list: decided as usual, which keeps both texts.
    if (again !== undefined) await this.syncPath(key, again.remote, false);
    return again === undefined && this.state.bases[key] === local;
  }

  /** Kumbu²'s text replaces the local file, which goes to Obsidian's .trash first. */
  private async keepKumbu(key: string): Promise<boolean> {
    const path = this.localPathOf.get(key) ?? key;
    if (await this.tooLargeToRead(key, path)) return false;
    const theirs = await this.readRemote(key);
    if (theirs === null) {
      await this.syncPath(key, null, false);
      return false;
    }
    await this.o.local.trashToLocal(path);
    if (!(await this.writeLocalIf(path, null, theirs.content))) {
      await this.syncPath(key, theirs.version, false);
      return false;
    }
    await this.setBase(key, theirs.version);
    return true;
  }

  /** Lists the local files: spellings, attachments and conflict copies. Returns key → path of the syncable ones. */
  private async scanLocal(full: boolean): Promise<Map<string, string>> {
    const found = new Map<string, string>();
    if (full) this.attachments.clear();
    // Markers are dot files, so in no list: looked for only by a full check, and only where they sync.
    const markers = full && this.supportsAttachments ? await this.o.local.listMarkers() : [];
    for (const path of [...(await this.o.local.list()), ...markers]) {
      const kind = eligibility(path, this.supportsAttachments);
      if (kind === "attachment") this.attachments.add(path);
      if (kind !== "sync") continue;
      const key = keyOf(path);
      found.set(key, path);
      this.localPathOf.set(key, path);
      if (COPY_NAME.test(key)) this.conflicts.add(key);
    }
    for (const copy of [...this.conflicts]) if (!found.has(copy)) this.conflicts.delete(copy);
    if (full) for (const key of Object.keys(this.state.hashed ?? {})) if (!found.has(key)) delete this.state.hashed![key];
    return found;
  }

  private async syncPath(key: string, remote: Version | null, damagedPossible: boolean): Promise<void> {
    // Decided now; set again below if Kumbu² still can't hand it over.
    this.unserved.delete(key);
    if (eligibility(key, this.supportsAttachments) !== "sync" || this.caseClashes.has(key)) {
      this.dirty.delete(key);
      return;
    }
    if (isMarker(key) && this.markersRefused) {
      this.dirty.delete(key);
      return;
    }
    if (this.leftInKumbu(key, remote)) {
      this.dirty.delete(key);
      return;
    }
    for (let round = 0; round < MAX_ROUNDS; round++) {
      if (this.stopped) return;
      const path = this.localPathOf.get(key) ?? key;
      const stat = isFile(key) ? await this.o.local.stat(path) : null;
      if (await this.tooLargeToRead(key, path, stat)) {
        this.dirty.delete(key);
        return;
      }
      if (this.unchangedSinceHashed(key, stat, remote)) {
        // What reading it would have found: nothing to do.
        if (this.rejected.get(key)?.version === UNEXPECTED) this.rejected.delete(key);
        this.dirty.delete(key);
        return;
      }
      const { content, version: local } = await this.readLocal(key, path, stat);
      // Gone here: nothing left that could be too large.
      if (local === null) this.tooLarge.delete(key);
      const base = this.state.bases[key] ?? null;
      const action = decide({ local, remote, base, remoteDamagedPossible: damagedPossible });
      // A change of this device's the server refused is not sent again until the file changes again, the rights change
      // (retryRefused) or the retry's timer comes round.
      const outgoing = action.kind === "upload" || action.kind === "reupload" || action.kind === "deleteRemote";
      if (outgoing && this.rejected.get(key)?.version === (local ?? "")) return;
      let again: Again | undefined;
      try {
        again = await this.act(key, path, content, local, remote, action);
      } catch (e) {
        if (e instanceof UnservedError) {
          // Dirty, and remembered with the version listed: tried again by every check.
          this.rejected.set(key, { version: UNEXPECTED, detail: `Couldn't sync: ${e.message}` });
          this.dirty.add(key);
          if (remote !== null) this.unserved.set(key, remote);
          return;
        }
        if (!(e instanceof RejectedError)) throw e;
        if (isMarker(key) && e.status !== 403) {
          // A server from before folder markers knows no such file: final for this run, and nothing to list. Markers are
          // then off both ways until the next start(), whose full check compares every one with its base again. A refusal
          // (403) says nothing about what the server keeps: that marker is only not sent again, below.
          if (!this.markersRefused) {
            console.warn(`Kumbu² Sync: this server keeps no empty folders (${e.message}); they stay in Obsidian only until the server is updated.`);
          }
          this.markersRefused = true;
          this.dirty.delete(key);
          return;
        }
        this.rejected.set(key, { version: local ?? "", detail: e.message, refused: e.status === 403 });
        this.dirty.delete(key);
        return;
      }
      if (outgoing || action.kind === "nothing" || this.rejected.get(key)?.version === UNEXPECTED) this.rejected.delete(key);
      if (again === undefined) {
        this.dirty.delete(key);
        return;
      }
      remote = again.remote;
    }
  }

  /**
   * An attachment over its limit is never read (a 200 MB PDF on a phone), and no decision overwrites or trashes it: it
   * stays as it is, listed as too large, and a different version in Kumbu² doesn't come over it (the web app's smaller
   * copy of a photo keeps the photo's name). Once it fits again, it is decided against the base it had.
   */
  private async tooLargeToRead(key: string, path: string, stat?: FileStat | null): Promise<boolean> {
    // A note is read whatever its size; a marker never is.
    if (attachmentKind(key) === null) return false;
    const size = stat === undefined ? await this.o.local.size(path) : (stat?.size ?? null);
    if (size === null || size <= MAX_ATTACHMENT_BYTES) return false;
    this.tooLarge.add(key);
    return true;
  }

  /**
   * An attachment over its limit in Kumbu² is never downloaded (requestUrl holds a whole answer in memory, and a 0.6.0
   * server keeps files up to 1 GiB), and the local file of that name is neither replaced nor sent over it: both stay
   * as they are, listed, until it is smaller there or gone. Known only from a server that lists sizes. One the fetch
   * policy declines stays in Kumbu² the same way, unlisted: the host chose it.
   */
  private leftInKumbu(key: string, remote: Version | null): boolean {
    const kind = remote === null ? null : attachmentKind(key);
    const size = kind === null ? undefined : this.remoteSizes.get(key);
    if (size !== undefined && size > MAX_ATTACHMENT_BYTES) {
      this.tooLargeThere.add(key);
      return true;
    }
    this.tooLargeThere.delete(key);
    return kind !== null && this.o.fetchPolicy?.wantBytes(key, size ?? null) === false;
  }

  /** Kumbu² is at the base, and the attachment's stat is what it was when its bytes were last found at the base. */
  private unchangedSinceHashed(key: string, stat: FileStat | null, remote: Version | null): boolean {
    const base = this.state.bases[key];
    const hashed = this.state.hashed?.[key];
    return stat !== null && base !== undefined && remote === base && hashed !== undefined && hashed.version === base
      && hashed.size === stat.size && hashed.mtime === stat.mtime;
  }

  private async act(
    key: string, path: string, content: Content | null, local: Version | null, remote: Version | null, action: Action,
  ): Promise<Again | undefined> {
    switch (action.kind) {
      case "nothing":
        if (local === null && remote === null && this.state.bases[key] !== undefined) {
          delete this.state.bases[key];
          await this.save();
        }
        return undefined;

      case "recordBase":
        this.forgetProblems(key);
        await this.setBase(key, action.version);
        return undefined;

      case "upload":
      case "reupload":
        return this.upload(key, content!, action.kind === "upload" ? action.base : null);

      case "download": {
        const theirs = await this.readRemote(key);
        if (theirs === null) {
          // An attachment listed with a version: its bytes don't open, which is no deletion (a note's text is its record).
          if (isFile(key)) throw new UnservedError(CANT_OPEN);
          return { remote: null };
        }
        if (!(await this.writeLocalIf(path, local, theirs.content))) return { remote: theirs.version };
        this.localPathOf.set(key, path);
        this.forgetProblems(key);
        await this.setBase(key, theirs.version);
        return undefined;
      }

      case "trashLocal": {
        // Checked by the adapter right before it trashes: a keystroke since the read makes it an edit to upload
        // (remote gone, local changed: reupload), not a file to trash.
        const trashed = local !== null
          && (isFile(key) ? await this.o.local.trashBinaryIf(path, local) : await this.o.local.trashIf(path, local));
        if (!trashed) return { remote };
        this.leftFolders.add(parentOf(path));
        delete this.state.bases[key];
        await this.save();
        return undefined;
      }

      case "deleteRemote": {
        const result = isFile(key) ? await this.o.remote.removeFile(key, action.base) : await this.o.remote.remove(key, action.base);
        if (!result.ok && result.conflict !== null) return { remote: result.conflict.version };
        delete this.state.bases[key];
        this.forgetProblems(key);
        await this.save();
        return undefined;
      }

      case "conflict":
        return typeof content === "string"
          ? this.conflict(key, path, content, local!, remote)
          : this.conflictFile(key, path, content!, local!, remote);
    }
  }

  /** A changed file is sent whole, as a note's text is. */
  private async upload(key: string, content: Content, base: Version | null): Promise<Again | undefined> {
    const size = typeof content === "string" ? byteSize(content) : content.byteLength;
    if (size > limitOf(key)) {
      this.tooLarge.add(key);
      return undefined;
    }
    this.tooLarge.delete(key);
    const result = typeof content === "string"
      ? await this.o.remote.put(key, content, base)
      : await this.o.remote.putFile(key, content, base);
    if (!result.ok) return { remote: result.conflict.version };
    await this.setBase(key, result.version);
    return undefined;
  }

  /** Kumbu²'s version keeps the path; the local text becomes a copy next to it (spec §7). */
  private async conflict(key: string, path: string, text: string, local: Version, remote: Version | null): Promise<Again | undefined> {
    const note = await this.o.remote.read(key);
    if (note === null) return { remote: null };
    if (note.version !== remote) return { remote: note.version };
    // A copy from a local edit's 409 comes with no check before it.
    const device = (await this.readShared()) ? null : this.o.device;
    const at = new Date(this.o.clock.now());
    const textOf = (local: string) => conflictCopyText(local, {
      device, localAt: at, remoteWriter: note.lastWriter, remoteAt: new Date(note.lastWriteAt), originalPath: key,
    });
    let copyText = textOf(text);
    const copy = await this.freeCopyPath(conflictCopyPath(path, device, at));
    if (!(await this.o.local.writeIf(copy, null, copyText))) return { remote };
    // The copy is safe on disk before the original is overwritten, so a failure from here on loses nothing.
    const copyKey = this.copyMade(key, copy);
    let replaced = await this.o.local.writeIf(path, local, note.content);
    // Typed on meanwhile: the newer text goes into the same copy, rather than a second copy for one conflict.
    for (let round = 0; !replaced && round < MAX_ROUNDS; round++) {
      const now = await this.o.local.read(path);
      if (now === null) break;
      const next = textOf(now);
      if (!(await this.o.local.writeIf(copy, await versionOf(copyText), next))) break;
      copyText = next;
      replaced = await this.o.local.writeIf(path, await versionOf(now), note.content);
    }
    if (replaced) {
      this.forgetProblems(key);
      await this.setBase(key, note.version);
    }
    // Uploaded whatever happened to the original: it is a note of its own now.
    await this.syncPath(copyKey, null, false);
    // The original changed again meanwhile (typed, deleted): decided once more, against the copy's text.
    return replaced ? undefined : { remote: note.version };
  }

  /**
   * conflict() for an attachment: Kumbu²'s file keeps the path, the local bytes go unchanged into a copy next to it
   * with the same extension (a file has no place for a callout; the notice and the settings name the copy).
   */
  private async conflictFile(key: string, path: string, bytes: ArrayBuffer, local: Version, remote: Version | null): Promise<Again | undefined> {
    const theirs = await this.o.remote.readFile(key);
    if (theirs === null) throw new UnservedError(CANT_OPEN);
    if (theirs.version !== remote) return { remote: theirs.version };
    // A copy from a local edit's 409 comes with no check before it.
    const device = (await this.readShared()) ? null : this.o.device;
    const copy = await this.freeCopyPath(conflictCopyPath(path, device, new Date(this.o.clock.now())));
    if (!(await this.o.local.writeBinaryIf(copy, null, bytes))) return { remote };
    const copyKey = this.copyMade(key, copy);
    let copyVersion = local;
    let replaced = await this.o.local.writeBinaryIf(path, local, theirs.bytes);
    // Changed again meanwhile: the newer bytes replace the copy's, rather than a second copy for one conflict.
    for (let round = 0; !replaced && round < MAX_ROUNDS; round++) {
      const now = await this.o.local.readBinary(path);
      if (now === null) break;
      if (!(await this.o.local.writeBinaryIf(copy, copyVersion, now))) break;
      copyVersion = await versionOfBytes(now);
      replaced = await this.o.local.writeBinaryIf(path, copyVersion, theirs.bytes);
    }
    if (replaced) {
      this.forgetProblems(key);
      await this.setBase(key, theirs.version);
    }
    await this.syncPath(copyKey, null, false);
    return replaced ? undefined : { remote: theirs.version };
  }

  /** Books a conflict copy that is on disk now: listed, reported, and uploaded as a file of its own. */
  private copyMade(key: string, copy: string): string {
    const copyKey = keyOf(copy);
    this.localPathOf.set(copyKey, copy);
    this.conflicts.add(copyKey);
    this.dirty.add(copyKey);
    this.copiesMade++;
    this.o.onConflict(key, copyKey);
    return copyKey;
  }

  /** "x (conflict … 10-15).png", else "x (conflict … 10-15) 2.png" and so on: the number goes after the stamp. */
  private async freeCopyPath(candidate: string): Promise<string> {
    const at = candidate.lastIndexOf(")") + 1;
    let path = candidate;
    for (let n = 2; (await this.o.local.size(path)) !== null; n++) path = `${candidate.slice(0, at)} ${n}${candidate.slice(at)}`;
    return path;
  }

  /**
   * The text the user sees, or an attachment's bytes, with its version. An attachment's version is kept with the stat
   * it had before the read (`stat`, else asked now): a change during the read gives a new stat, so it is read again.
   */
  private async readLocal(key: string, path: string, stat?: FileStat | null): Promise<{ content: Content | null; version: Version | null }> {
    if (isMarker(key)) {
      // Never read: a marker with bytes in it (another tool's) is still the empty marker, and is sent empty.
      const found = (stat === undefined ? await this.o.local.stat(path) : stat) !== null;
      return found ? { content: new ArrayBuffer(0), version: MARKER_VERSION } : { content: null, version: null };
    }
    if (isFile(key)) {
      const before = stat === undefined ? await this.o.local.stat(path) : stat;
      const bytes = await this.o.local.readBinary(path);
      const version = bytes === null ? null : await versionOfBytes(bytes);
      const hashed = (this.state.hashed ??= {});
      if (version !== null && before !== null) hashed[key] = { size: before.size, mtime: before.mtime, version };
      else delete hashed[key];
      return { content: bytes, version };
    }
    const text = await this.o.local.read(path);
    return { content: text, version: text === null ? null : await versionOf(text) };
  }

  /** readLocal's version, without reading an attachment over its limit (TOO_LARGE): only a comparison needs it. */
  private async localVersion(key: string, path: string): Promise<Version | null> {
    if (attachmentKind(key) !== null && ((await this.o.local.size(path)) ?? 0) > MAX_ATTACHMENT_BYTES) return TOO_LARGE;
    return (await this.readLocal(key, path)).version;
  }

  private writeLocalIf(path: string, expected: Version | null, content: Content): Promise<boolean> {
    return typeof content === "string"
      ? this.o.local.writeIf(path, expected, content)
      : this.o.local.writeBinaryIf(path, expected, content);
  }

  private async readRemote(key: string): Promise<{ content: Content; version: Version } | null> {
    if (isFile(key)) {
      const file = await this.o.remote.readFile(key);
      return file === null ? null : { content: file.bytes, version: file.version };
    }
    const note = await this.o.remote.read(key);
    return note === null ? null : { content: note.content, version: note.version };
  }

  /** Deletes the left folders that are empty now, and then their parents the same way, never the vault root. */
  private async removeLeftFolders(): Promise<void> {
    // Deepest first, so a parent is looked at only after its emptied children are gone.
    const folders = [...this.leftFolders].sort((a, b) => b.split("/").length - a.split("/").length);
    for (let folder of folders) {
      // A dot folder (.obsidian, .trash) is never synced, so no remote change can have emptied it.
      while (folder !== "" && eligibility(folder) !== "ignored" && (await this.o.local.removeFolderIfEmpty(folder))) {
        folder = parentOf(folder);
      }
    }
    // Only now: a failure keeps them for the next job.
    this.leftFolders.clear();
  }

  private forgetProblems(key: string): void {
    this.rejected.delete(key);
    this.tooLarge.delete(key);
    this.tooLargeThere.delete(key);
  }

  private async setBase(key: string, version: Version): Promise<void> {
    this.state.bases[key] = version;
    await this.save();
  }

  private save(): Promise<void> {
    return this.o.state.save(this.state);
  }

  private failed(e: unknown): void {
    if (e instanceof AuthError) {
      this.stopped = true;
      for (const h of this.debounces.values()) this.o.clock.clearTimeout(h);
      this.debounces.clear();
      this.cancelRetry();
      this.o.onAuthLost();
      return;
    }
    // Offline, or anything unexpected: nothing is dropped (dirty paths stay), and a check is tried again later.
    if (!(e instanceof OfflineError)) console.error("Kumbu² Sync:", e);
    if (e instanceof VaultChangedError) this.o.onVaultChanged?.();
    this.offline = true;
    const backoff = this.retryDelay === 0 ? RETRY_FIRST_MS : Math.min(RETRY_MAX_MS, this.retryDelay * 2);
    this.retryDelay = backoff;
    const delay = e instanceof OfflineError && e.retryAfterMs !== null ? e.retryAfterMs : backoff;
    this.cancelRetry();
    this.retryHandle = this.o.clock.setTimeout(() => {
      this.retryHandle = null;
      void this.check(false);
    }, delay);
  }

  private online(): void {
    if (!this.offline) return;
    this.offline = false;
    this.retryDelay = 0;
    this.cancelRetry();
  }

  private cancelRetry(): void {
    if (this.retryHandle !== null) this.o.clock.clearTimeout(this.retryHandle);
    this.retryHandle = null;
  }

  private cancelFolderWait(key: string): void {
    const h = this.folderWaits.get(key);
    if (h !== undefined) this.o.clock.clearTimeout(h);
    this.folderWaits.delete(key);
  }

  private cancelDebounce(key: string): void {
    const h = this.debounces.get(key);
    if (h !== undefined) this.o.clock.clearTimeout(h);
    this.debounces.delete(key);
  }

  private emit(): void {
    this.o.onStatus(this.status());
  }
}

/** "a/b/c.md" → "a/b"; "c.md" → "". */
function parentOf(path: string): string {
  return path.slice(0, Math.max(0, path.lastIndexOf("/")));
}

/** Ordinal order, the same on every device. */
function cmp(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
