// SPDX-License-Identifier: AGPL-3.0-or-later
// In-memory RemoteApi that behaves like the server's endpoints, with scripted holds and failures.
import {
  OfflineError, RejectedError,
  type DeleteResult, type FileDeleteResult, type FileKind, type FilePutResult, type ListedVault, type MoveResult, type PutResult,
  type RemoteApi, type RemoteEntry, type RemoteFile, type RemoteNote, type Version, type VersionsAnswer,
} from "../../src/engine/ports";
import { isMarker } from "../../src/engine/paths";
import { normaliseText, versionOf, versionOfBytes } from "../../src/engine/version";

/** vaults() is not among them: it never goes through enter, so hold and failNext would do nothing. */
type Method = Exclude<keyof RemoteApi, "vaults">;
interface StoredNote { content: string; lastWriter: string; at: string; version: Version; }
interface StoredFile { bytes: Uint8Array; lastWriter: string; at: string; version: Version; }

const OVERLAP_MS = 5 * 60_000;
const WRITES = new Set<Method>(["put", "remove", "move", "putFile", "removeFile"]);

export class FakeServer implements RemoteApi {
  readonly notes = new Map<string, StoredNote>();
  /** Attachments, by path. */
  readonly files = new Map<string, StoredFile>();
  /** False: a server from before attachments, which ignores attachments=1 and answers 404 on /api/files. */
  attachmentsSupported = true;
  /** False: a server with attachments from before folder markers, which answers 400 to a marker's GET and PUT. */
  markersSupported = true;
  /** Every path touched, with the server time of the touch, for since answers. */
  readonly touches: { path: string; at: number }[] = [];
  readonly calls: { method: Method; args: unknown[] }[] = [];
  damaged = 0;
  /** Paths whose stored note doesn't open: in neither list of an answer, only counted in `damaged`. */
  readonly damagedPaths = new Set<string>();
  /** The writer label the device's own writes get. */
  deviceWriter = "Work – Obsidian Laptop";
  /**
   * A key that may not write: for a write on the path (a move's first argument is its source) it returns the sentence
   * of a 403 with a code, which the client throws as a RejectedError; null lets the write through. Reads, agentWrite
   * and the seeds are never refused.
   */
  refuse: ((path: string) => string | null) | null = null;
  /** What GET /api/vaults says of Work: `shared`, "unlisted", "absent" (a server from before sharing), or it fails. */
  listing: boolean | "unlisted" | "absent" | Error = false;
  /** The level GET /api/vaults gives for Work, if any. */
  level: string | undefined = undefined;
  vaultsAsked = 0;
  /** Whether the versions list names each attachment's size, as a server from 0.6.0 on does. */
  sizes = true;
  private readonly holds = new Map<Method, Promise<void>[]>();
  private readonly failures = new Map<Method, Error[]>();
  private readonly dropped = new Set<Method>();

  constructor(private readonly now: () => number = Date.now) {}

  async seed(path: string, content: string, writer = "agent"): Promise<void> {
    await this.store(path, content, writer);
  }

  async seedFile(path: string, bytes: Uint8Array, writer = "agent"): Promise<void> {
    await this.storeFile(path, bytes, writer);
  }

  /**
   * An agent's edit (content) or delete (null), stamped with the clock, or with `at` for a write stamped before it
   * was committed (the case the server's 5-minute overlap is for).
   */
  agentWrite(path: string, content: string | null, at?: number): Promise<string | undefined> {
    if (content === null) {
      const previous = this.notes.get(path)?.content;
      this.notes.delete(path);
      this.touch(path, at);
      return Promise.resolve(previous);
    }
    return this.store(path, content, "agent", at);
  }

  /** An agent removes an attachment or a marker (rm), stamped with the clock. */
  agentRemoveFile(path: string): void {
    this.files.delete(path);
    this.touch(path);
  }

  /** The stored note stops opening (as the server's damaged notes): gone from every list, counted in damaged. */
  damage(path: string): void {
    this.notes.delete(path);
    this.damagedPaths.add(path);
    this.damaged++;
    this.touch(path);
  }

  /** Pauses the next call of that method until the returned function is called. */
  hold(method: Method): () => void {
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    const list = this.holds.get(method) ?? [];
    list.push(gate);
    this.holds.set(method, list);
    return release;
  }

  failNext(method: Method, error: Error): void {
    const list = this.failures.get(method) ?? [];
    list.push(error);
    this.failures.set(method, list);
  }

  /** The next put is applied, but its answer is lost on the way back. */
  dropResponseOfNext(method: "put"): void {
    this.dropped.add(method);
  }

  count(method: Method): number {
    return this.calls.filter((c) => c.method === method).length;
  }

  async versions(since: string | null, state: string | null): Promise<VersionsAnswer> {
    await this.enter("versions", [since, state]);
    const cursorAt = this.now();
    const cursor = new Date(cursorAt).toISOString();
    const sinceAt = since === null ? NaN : Date.parse(since);
    const attachments = this.attachmentsSupported;
    // As the server: with attachments, every entry has its kind; without, attachments are nowhere in the answer.
    // A 0.6.0 server also names each attachment's size; `sizes = false` plays an older one.
    const entry = (path: string, version: Version, kind: FileKind): RemoteEntry => {
      const size = kind === "note" || !this.sizes ? undefined : this.files.get(path)?.bytes.byteLength;
      return attachments ? { path, version, kind, ...(size === undefined ? {} : { size }) } : { path, version };
    };
    const files = attachments ? this.files : new Map<string, StoredFile>();
    if (Number.isNaN(sinceAt)) {
      const entries: RemoteEntry[] = [
        ...[...this.notes].map(([path, n]) => entry(path, n.version, "note")),
        ...[...files].map(([path, f]) => entry(path, f.version, kindOf(path))),
      ];
      return { full: true, entries, cursor, state: "s" + String(cursorAt), damaged: this.damaged, unchanged: false, attachments };
    }
    const touched = new Set(this.touches.filter((t) => t.at > sinceAt - OVERLAP_MS).map((t) => t.path)
      .filter((path) => attachments || kindOf(path) === "note"));
    const entries: RemoteEntry[] = [...touched]
      .filter((path) => this.notes.has(path) || files.has(path) || !this.damagedPaths.has(path))
      .map((path) => {
        const n = this.notes.get(path) ?? files.get(path);
        return n ? entry(path, n.version, kindOf(path)) : { path, version: null };
      });
    return { full: false, entries, cursor, state: null, damaged: this.damaged, unchanged: false, attachments };
  }

  async read(path: string): Promise<RemoteNote | null> {
    await this.enter("read", [path]);
    const n = this.notes.get(path);
    return n ? { path, content: n.content, version: n.version, lastWriter: n.lastWriter, lastWriteAt: n.at } : null;
  }

  async put(path: string, content: string, base: Version | null): Promise<PutResult> {
    await this.enter("put", [path, content, base]);
    const existing = this.notes.get(path);
    if (existing && existing.version !== base) return { ok: false, conflict: { content: existing.content, version: existing.version } };
    // A base for a note that is gone is the server's 404, which the client turns into a create.
    await this.store(path, content, this.deviceWriter);
    return this.leave("put", { ok: true, version: this.notes.get(path)!.version });
  }

  async remove(path: string, base: Version): Promise<DeleteResult> {
    await this.enter("remove", [path, base]);
    const existing = this.notes.get(path);
    if (!existing) return { ok: false, conflict: null };
    if (existing.version !== base) return { ok: false, conflict: { content: existing.content, version: existing.version } };
    this.notes.delete(path);
    this.touch(path);
    return { ok: true };
  }

  async move(from: string, to: string): Promise<MoveResult> {
    await this.enter("move", [from, to]);
    const file = this.attachmentsSupported ? this.files.get(from) : undefined;
    if (file) {
      // As the server's mv (a 409): an attachment keeps its format, so its media type ("a.jpg" → "a.jpeg" may move).
      if (this.files.has(to) || this.notes.has(to) || mediaTypeOf(to) !== mediaTypeOf(from)) return { ok: false };
      this.files.delete(from);
      this.files.set(to, { ...file, lastWriter: this.deviceWriter + " (mv)" });
      this.touch(from);
      this.touch(to);
      return { ok: true, version: file.version };
    }
    const existing = this.notes.get(from);
    if (!existing || this.notes.has(to) || this.files.has(to)) return { ok: false };
    this.notes.delete(from);
    this.notes.set(to, { ...existing, lastWriter: this.deviceWriter + " (mv)" });
    this.touch(from);
    this.touch(to);
    return { ok: true, version: existing.version };
  }

  async readFile(path: string): Promise<RemoteFile | null> {
    await this.enter("readFile", [path]);
    this.refuseMarker(path);
    const f = this.attachmentsSupported ? this.files.get(path) : undefined;
    return f ? { bytes: f.bytes.slice().buffer, version: f.version } : null;
  }

  async putFile(path: string, bytes: ArrayBuffer, base: Version | null): Promise<FilePutResult> {
    await this.enter("putFile", [path, bytes, base]);
    if (!this.attachmentsSupported) throw new RejectedError("Not found.", 404);
    this.refuseMarker(path);
    if (isMarker(path) && bytes.byteLength > 0) throw new RejectedError("A .vaultkeep keeps its folder and holds nothing; send it empty.", 400);
    const existing = this.files.get(path);
    if (existing && existing.version !== base) return { ok: false, conflict: { version: existing.version } };
    await this.storeFile(path, new Uint8Array(bytes), this.deviceWriter);
    return { ok: true, version: this.files.get(path)!.version };
  }

  async removeFile(path: string, base: Version): Promise<FileDeleteResult> {
    await this.enter("removeFile", [path, base]);
    this.refuseMarker(path);
    const existing = this.attachmentsSupported ? this.files.get(path) : undefined;
    if (!existing) return { ok: false, conflict: null };
    if (existing.version !== base) return { ok: false, conflict: { version: existing.version } };
    this.files.delete(path);
    this.touch(path);
    return { ok: true };
  }

  /** Not through enter: exact lists of calls in the tests stay about the sync's own requests. */
  vaults(): Promise<ListedVault[]> {
    this.vaultsAsked++;
    if (this.listing instanceof Error) return Promise.reject(this.listing);
    if (this.listing === "unlisted") return Promise.resolve([{ name: "Other", canWrite: true, shared: false }]);
    if (this.listing === "absent") return Promise.resolve([{ name: "Work", canWrite: true }]);
    return Promise.resolve([{ name: "Work", canWrite: true, shared: this.listing, level: this.level }]);
  }

  private refuseMarker(path: string): void {
    if (isMarker(path) && this.attachmentsSupported && !this.markersSupported) {
      throw new RejectedError(`/Work/${path} is neither a note nor a file Kumbu² keeps.`, 400);
    }
  }

  private async storeFile(path: string, bytes: Uint8Array, writer: string, at = this.now()): Promise<void> {
    const copy = bytes.slice();
    this.files.set(path, { bytes: copy, lastWriter: writer, at: new Date(at).toISOString(), version: await versionOfBytes(copy) });
    this.touch(path, at);
  }

  /** Returns the text it replaced, as it was at the moment of the write (a blind write, like an agent's). */
  private async store(path: string, content: string, writer: string, at = this.now()): Promise<string | undefined> {
    const text = normaliseText(content);
    const version = await versionOf(text);
    const previous = this.notes.get(path)?.content;
    this.notes.set(path, { content: text, lastWriter: writer, at: new Date(at).toISOString(), version });
    this.touch(path, at);
    return previous;
  }

  private touch(path: string, at = this.now()): void {
    this.touches.push({ path, at });
  }

  private async enter(method: Method, args: unknown[]): Promise<void> {
    this.calls.push({ method, args });
    const gate = this.holds.get(method)?.shift();
    if (gate) await gate;
    const failure = this.failures.get(method)?.shift();
    if (failure) throw failure;
    const sentence = WRITES.has(method) ? this.refuse?.(args[0] as string) ?? null : null;
    if (sentence !== null) throw new RejectedError(sentence, 403);
  }

  private leave<T>(method: Method, result: T): T {
    if (this.dropped.delete(method)) throw new OfflineError("the answer was lost");
    return result;
  }
}

/** As a 0.6.0 server: `.md` (exactly) is a note, the known formats keep their kind, anything else is a `file`. */
function kindOf(path: string): FileKind {
  if (isMarker(path)) return "folder";
  if (path.endsWith(".md")) return "note";
  const lower = path.toLowerCase();
  if (lower.endsWith(".pdf")) return "pdf";
  return /\.(png|jpe?g|gif|webp|svg)$/.test(lower) ? "image" : ("file" as FileKind);
}

function mediaTypeOf(path: string): string {
  const ext = path.slice(path.lastIndexOf(".") + 1).toLowerCase();
  return ext === "jpg" ? "jpeg" : ext;
}
