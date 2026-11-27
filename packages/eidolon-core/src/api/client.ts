// SPDX-License-Identifier: AGPL-3.0-or-later
// The Kumbu² REST API over an injected HTTP function: requestUrl in Obsidian, fetch in Node.
import {
  AuthError, OfflineError, RejectedError, VaultChangedError,
  type DeleteResult, type FileDeleteResult, type FilePutResult, type ListedVault, type MoveResult, type PutResult, type RemoteApi,
  type RemoteEntry, type RemoteFile, type RemoteNote, type Version, type VersionsAnswer,
} from "../engine/ports";
import { fileUrlFor, fromApiPath, toApiPath, urlFor } from "../engine/paths";
import { normaliseText, versionOfBytes } from "../engine/version";
import type {
  ConflictView, ErrorView, FileConflictView, FileStateView, MovedView, MoveRequest, NoteView, PutNoteRequest,
  RedeemRequest, RedeemView, VaultView, VersionsView, WsTicketView,
} from "./types";

/** `body`: JSON text, or an attachment's bytes as they are. */
export interface HttpRequest { method: string; url: string; headers: Record<string, string>; body?: string | ArrayBuffer; }
/** `bytes`: the body as it came, for an attachment; `text` is the same body decoded. */
export interface HttpResponse { status: number; headers: Record<string, string>; text: string; bytes?: ArrayBuffer; }
/** Throws only on network failure; every status comes back as a response. */
export type HttpFn = (r: HttpRequest) => Promise<HttpResponse>;

const WRONG_CODE = "This code is not valid (any more).";

export class KumbuApi implements RemoteApi {
  private readonly server: string;
  /** What the last versions answer said, for a 304, which says nothing; null until one has come. */
  private attachments: boolean | null = null;

  /**
   * `key`: a device's API key; null for a browser session, whose cookie the host's HTTP function sends. `vaultId`: sent
   * with every request, so that the server refuses one whose name is another vault's by now; a key's device holds one
   * vault and sends none.
   */
  constructor(
    private readonly http: HttpFn, server: string, private readonly vault: string, private readonly key: string | null,
    private readonly vaultId: string | null = null,
  ) {
    this.server = trimServer(server);
  }

  /**
   * Asks with attachments=1: a server that keeps attachments then lists them, each entry with its kind, and says
   * `attachments: true` (also for an empty vault); an older one ignores the parameter and answers as it always has.
   */
  async versions(since: string | null, state: string | null): Promise<VersionsAnswer> {
    const query = "?attachments=1" + (since === null ? "" : `&since=${encodeURIComponent(since)}`);
    const headers: Record<string, string> = {};
    // The server answers 304 only to a full list, so the tag goes only with one.
    // Not before an answer has said whether the server keeps attachments: a 304 couldn't tell.
    if (since === null && state !== null && this.attachments !== null) headers["If-None-Match"] = `"${state}"`;
    const r = await this.send("GET", `/api/vaults/${encodeURIComponent(this.vault)}/versions${query}`, undefined, headers);
    if (r.status === 304) {
      return { full: true, entries: [], cursor: "", state, damaged: 0, unchanged: true, attachments: this.attachments === true };
    }
    if (r.status === 404) throw new AuthError(errorOf(r) ?? `This key can't open the vault ${this.vault} any more.`);
    expectOk(r);
    const view = parse<VersionsView>(r);
    this.attachments = view.attachments === true;
    const entries: RemoteEntry[] = view.notes.map((n) => ({
      path: fromApiPath(this.vault, n.path), version: n.version, ...(n.kind === undefined ? {} : { kind: n.kind }),
      ...(typeof n.size === "number" ? { size: n.size } : {}),
    }));
    if (!view.full) for (const p of view.deleted ?? []) entries.push({ path: fromApiPath(this.vault, p), version: null });
    return {
      full: view.full, entries, cursor: view.cursor, state: view.state, damaged: view.damaged, unchanged: false,
      attachments: this.attachments === true,
    };
  }

  async read(path: string): Promise<RemoteNote | null> {
    const r = await this.send("GET", urlFor(this.vault, path));
    if (r.status === 404) return null;
    expectOk(r);
    const n = parse<NoteView>(r);
    return { path: fromApiPath(this.vault, n.path), content: n.content, version: n.version, lastWriter: n.lastWriter, lastWriteAt: n.lastWriteAt };
  }

  async put(path: string, content: string, base: Version | null): Promise<PutResult> {
    const body: PutNoteRequest = base === null ? { content: normaliseText(content) } : { content: normaliseText(content), version: base };
    const r = await this.send("PUT", urlFor(this.vault, path), body);
    if (r.status === 409) {
      const conflict = await this.conflictOf(r, path);
      if (conflict) return { ok: false, conflict };
      // The note is gone meanwhile: the local text wins and becomes a new note, as for "local changed, remote gone".
      if (base !== null) return this.put(path, content, null);
      throw new OfflineError(errorOf(r) ?? "The server refused the write and the note isn't there; trying again later.");
    }
    if (r.status === 404) {
      // With a base, 404 is the note gone (the server's answer to a version it can't find): create it anew.
      if (base !== null) return this.put(path, content, null);
      throw new AuthError(errorOf(r) ?? `This key can't open the vault ${this.vault} any more.`);
    }
    expectOk(r);
    return { ok: true, version: parse<NoteView>(r).version };
  }

  async remove(path: string, base: Version): Promise<DeleteResult> {
    const r = await this.send("DELETE", `${urlFor(this.vault, path)}?version=${encodeURIComponent(base)}`);
    if (r.status === 404) return { ok: false, conflict: null };
    if (r.status === 409) {
      const conflict = await this.conflictOf(r, path);
      // Still at the version sent, so it is not a change elsewhere: the server refused the delete itself.
      if (conflict?.version === base) throw new RejectedError(errorOf(r) ?? "The server refused to trash this note.");
      return { ok: false, conflict };
    }
    expectOk(r);
    return { ok: true };
  }

  async move(from: string, to: string): Promise<MoveResult> {
    const body: MoveRequest = { from: toApiPath(this.vault, from), to: toApiPath(this.vault, to), keepLinks: true };
    const r = await this.send("POST", "/api/move", body);
    if (r.status === 409 || r.status === 404) return { ok: false };
    expectOk(r);
    const view = parse<MovedView>(r);
    return view.version ? { ok: true, version: view.version } : { ok: false };
  }

  /** An attachment's bytes as stored; its version is the ETag (the token of those bytes). */
  async readFile(path: string): Promise<RemoteFile | null> {
    const r = await this.send("GET", fileUrlFor(this.vault, path));
    if (r.status === 404) return null;
    expectOk(r);
    // Never an empty file in place of a body the HTTP function couldn't hand over.
    if (r.bytes === undefined) throw new OfflineError("The server's answer came without the file's bytes.");
    const tag = header(r, "ETag")?.replace(/^W\//, "").replace(/"/g, "");
    return { bytes: r.bytes, version: tag || (await versionOfBytes(r.bytes)) };
  }

  /** Uploads the bytes as they are: created without a base, else only over that version (409: a conflict). */
  async putFile(path: string, bytes: ArrayBuffer, base: Version | null): Promise<FilePutResult> {
    const query = base === null ? "" : `?version=${encodeURIComponent(base)}`;
    const r = await this.sendBytes("PUT", fileUrlFor(this.vault, path) + query, bytes);
    if (r.status === 409) {
      const conflict = await this.fileConflictOf(r, path);
      if (conflict) return { ok: false, conflict };
      // Gone meanwhile: the local file wins and is uploaded anew, as a note is.
      if (base !== null) return this.putFile(path, bytes, null);
      throw new OfflineError(errorOf(r) ?? "The server refused the upload and the file isn't there; trying again later.");
    }
    if (r.status === 404) {
      if (base !== null) return this.putFile(path, bytes, null);
      // Not AuthError, as a note's create: a server without attachments answers 404 here too, and a key that lost
      // the vault is told so by the next versions.
      throw new RejectedError(errorOf(r) ?? "This server keeps no attachments.", 404);
    }
    expectOk(r);
    return { ok: true, version: parse<FileStateView>(r).version };
  }

  async removeFile(path: string, base: Version): Promise<FileDeleteResult> {
    const r = await this.send("DELETE", `${fileUrlFor(this.vault, path)}?version=${encodeURIComponent(base)}`);
    if (r.status === 404) return { ok: false, conflict: null };
    if (r.status === 409) {
      const conflict = await this.fileConflictOf(r, path);
      // Still at the version sent: the server refused the delete itself.
      if (conflict?.version === base) throw new RejectedError(errorOf(r) ?? "The server refused to trash this file.");
      return { ok: false, conflict };
    }
    expectOk(r);
    return { ok: true };
  }

  async wsTicket(): Promise<string> {
    const r = await this.send("POST", "/api/ws-ticket");
    expectOk(r);
    return parse<WsTicketView>(r).ticket;
  }

  /**
   * Revokes this key on the server (`DELETE /api/key`, Kumbu² 0.2.5 or newer). A key the server refuses already (401,
   * 403) is as good as revoked; anything else, an older server's 404 included, throws, and the key may still work.
   */
  async revokeSelf(): Promise<void> {
    const r = await this.send("DELETE", "/api/key");
    if (r.status === 401 || r.status === 403) return;
    expectOk(r);
  }

  async vaults(): Promise<ListedVault[]> {
    const r = await this.send("GET", "/api/vaults");
    expectOk(r);
    return parse<VaultView[]>(r).map((v) => ({
      name: v.name, canWrite: v.canWrite === true, shared: v.shared, level: v.level,
      ...(v.id === undefined ? {} : { id: v.id }), ...(v.discreet === undefined ? {} : { discreet: v.discreet }),
    }));
  }

  /** The current note of a 409: from its body, or read when the body has none. */
  private async conflictOf(r: HttpResponse, path: string): Promise<{ content: string; version: Version } | null> {
    const view = tryParse<ConflictView>(r);
    if (view?.current) return { content: view.current.content, version: view.current.version };
    const now = await this.read(path);
    return now ? { content: now.content, version: now.version } : null;
  }

  /** The current file's version of a 409: from its body, or read when the body has none. */
  private async fileConflictOf(r: HttpResponse, path: string): Promise<{ version: Version } | null> {
    const view = tryParse<FileConflictView>(r);
    if (view?.current) return { version: view.current.version };
    const now = await this.readFile(path);
    return now ? { version: now.version } : null;
  }

  private send(method: string, path: string, body?: unknown, extra: Record<string, string> = {}): Promise<HttpResponse> {
    const headers: Record<string, string> = { ...this.authorization(), ...extra };
    return this.checked(call(this.http, { method, url: this.server + path, headers }, body));
  }

  /** The body as it is: the server reads the format from the bytes and ignores the Content-Type. */
  private sendBytes(method: string, path: string, bytes: ArrayBuffer): Promise<HttpResponse> {
    const headers = { ...this.authorization(), "Content-Type": "application/octet-stream" };
    return this.checked(call(this.http, { method, url: this.server + path, headers, body: bytes }));
  }

  private authorization(): Record<string, string> {
    return {
      ...(this.key === null ? {} : { Authorization: `Bearer ${this.key}` }),
      ...(this.vaultId === null ? {} : { "Kumbu-Vault-Id": this.vaultId }),
    };
  }

  /** Before any other reading of the answer: a 409 for another vault is no conflict, and nothing may follow it. */
  private async checked(response: Promise<HttpResponse>): Promise<HttpResponse> {
    const r = await response;
    if (r.status === 409 && errorField(r, "code") === "vault-changed") {
      throw new VaultChangedError(errorOf(r) ?? `${this.vault} is another vault now.`);
    }
    return r;
  }
}

const RETRY_SAME_CODE = "Try again in a moment with the same code.";

export async function redeemPairing(http: HttpFn, server: string, code: string, device: string, plugin: string): Promise<RedeemView> {
  const body: RedeemRequest = { code: normalisePairingCode(code), device: device.trim(), plugin };
  let r: HttpResponse;
  try {
    r = await call(http, { method: "POST", url: `${trimServer(server)}/api/pairing/redeem`, headers: {} }, body);
  } catch (e) {
    throw new OfflineError(`The server can't be reached. ${RETRY_SAME_CODE}`, e instanceof OfflineError ? e.retryAfterMs : null);
  }
  // A vault being re-encrypted (503), a passing server error (5xx) and the rate limits (429, which while the
  // server-wide limit holds also meets a right code) all leave the code valid: wait and retry.
  if (r.status >= 500 || r.status === 429) {
    throw new OfflineError(`${errorOf(r) ?? `The server answered ${r.status}.`} ${RETRY_SAME_CODE}`, retryAfterMs(r));
  }
  if (r.status === 409) {
    // The code still works and the device name has nothing to do with it.
    if (errorField(r, "type") === "plugin-too-old") throw new RejectedError(errorOf(r) ?? "Update the Kumbu² plugin to sync this shared vault.", 409);
    // Every "<Vault> – Obsidian <device>" name up to " 20" is taken, and the code is used up.
    throw new RejectedError(`${errorOf(r) ?? "That key name is taken."} Change the device name and make a new code in the web app.`, 409);
  }
  if (r.status === 404 || r.status === 400) throw new RejectedError(errorOf(r) ?? WRONG_CODE, r.status);
  expectOk(r);
  return parse<RedeemView>(r);
}

/** Uppercase, without spaces and dashes; I and L read as 1, O as 0 (Crockford Base32). */
export function normalisePairingCode(input: string): string {
  return input.toUpperCase().replace(/[\s-]+/g, "").replace(/[IL]/g, "1").replace(/O/g, "0");
}

export function trimServer(server: string): string {
  return server.trim().replace(/\/+$/, "");
}

async function call(http: HttpFn, request: HttpRequest, body?: unknown): Promise<HttpResponse> {
  if (body !== undefined) {
    request.headers["Content-Type"] = "application/json";
    request.body = JSON.stringify(body);
  }
  try {
    return await http(request);
  } catch (e) {
    throw new OfflineError(e instanceof Error ? e.message : String(e));
  }
}

function expectOk(r: HttpResponse): void {
  if (r.status >= 200 && r.status < 300) return;
  const sentence = errorOf(r);
  // A refusal in a shared vault is no revoked key, and an unknown code counts too, so that a newer server doesn't stop an older plugin's sync.
  if (r.status === 403 && errorField(r, "code") !== null) throw new RejectedError(sentence ?? "The server refused this change.", 403);
  if (r.status === 401 || r.status === 403) throw new AuthError(sentence ?? "This key expired or was revoked.");
  if (r.status === 503 || r.status === 429 || r.status >= 500 || r.status === 408) {
    throw new OfflineError(sentence ?? `The server answered ${r.status}.`, retryAfterMs(r));
  }
  throw new RejectedError(sentence ?? `The server refused this (${r.status}).`);
}

function header(r: HttpResponse, name: string): string | undefined {
  const lower = name.toLowerCase();
  for (const [k, v] of Object.entries(r.headers)) if (k.toLowerCase() === lower) return v;
  return undefined;
}

function retryAfterMs(r: HttpResponse): number | null {
  const value = header(r, "Retry-After");
  if (value === undefined) return null;
  const seconds = Number(value);
  if (Number.isFinite(seconds)) return Math.max(0, seconds * 1000);
  const at = Date.parse(value);
  return Number.isNaN(at) ? null : Math.max(0, at - Date.now());
}

function parse<T>(r: HttpResponse): T {
  try {
    return JSON.parse(r.text) as T;
  } catch {
    throw new OfflineError(`The server sent an answer that isn't JSON (${r.status}).`);
  }
}

function tryParse<T>(r: HttpResponse): T | null {
  try {
    return JSON.parse(r.text) as T;
  } catch {
    return null;
  }
}

function errorOf(r: HttpResponse): string | null {
  const view = tryParse<Partial<ErrorView>>(r);
  return typeof view?.error === "string" && view.error ? view.error : null;
}

function errorField(r: HttpResponse, field: "code" | "type"): string | null {
  const value = tryParse<Partial<ErrorView>>(r)?.[field];
  return typeof value === "string" && value ? value : null;
}
