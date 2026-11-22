// SPDX-License-Identifier: AGPL-3.0-or-later
// The engine's ports and the types every part of the plugin shares.

export type Version = string; // 12 hex chars
/** What a path holds on a server with attachments; a server without them gives no kind. "folder": a folder marker. */
export type FileKind = "note" | "image" | "pdf" | "folder";
/** `size`: an attachment's bytes, from a server at 0.6.0 or newer, which keeps files larger than the plugin reads. */
export interface RemoteEntry { path: string; version: Version | null; kind?: FileKind; size?: number; } // vault-relative path; null = deleted
export interface VersionsAnswer {
  full: boolean; entries: RemoteEntry[]; cursor: string; state: string | null; damaged: number; unchanged: boolean;
  /** The server keeps attachments: it lists them, and serves them through readFile, putFile and removeFile. */
  attachments: boolean;
}
export interface RemoteNote { path: string; content: string; version: Version; lastWriter: string; lastWriteAt: string; }
export type PutResult = { ok: true; version: Version } | { ok: false; conflict: { content: string; version: Version } };
export type DeleteResult = { ok: true } | { ok: false; conflict: { content: string; version: Version } | null }; // null: already gone
export type MoveResult = { ok: true; version: Version } | { ok: false };
/** An attachment's bytes, exactly as stored, and their version. */
export interface RemoteFile { bytes: ArrayBuffer; version: Version; }
/** An attachment's conflict carries the version only: its bytes are read when needed. */
export type FilePutResult = { ok: true; version: Version } | { ok: false; conflict: { version: Version } };
export type FileDeleteResult = { ok: true } | { ok: false; conflict: { version: Version } | null }; // null: already gone

/** 401, or a 403 without a refusal's code: the key expired or was revoked. */
export class AuthError extends Error {}
/**
 * 400/413 (and pairing's 404/409/429): the server's sentence is the message.
 * status 403: a refusal, a 403 with a code (read-only, structural, closing, owner-inactive, or one newer than this plugin).
 */
export class RejectedError extends Error {
  constructor(message: string, readonly status: number | null = null) { super(message); }
}
/** Network failure, 5xx, 429, timeout. `retryAfterMs` comes from a Retry-After header. */
export class OfflineError extends Error {
  constructor(message: string, readonly retryAfterMs: number | null = null) { super(message); }
}

/** `id`: stays when the vault is renamed, unlike its name, which is also per member (an alias). */
export interface ListedVault { id?: string; name: string; canWrite: boolean; shared?: boolean; level?: string; discreet?: boolean; }

export interface RemoteApi {
  versions(since: string | null, state: string | null): Promise<VersionsAnswer>;
  read(path: string): Promise<RemoteNote | null>;
  put(path: string, content: string, base: Version | null): Promise<PutResult>;
  remove(path: string, base: Version): Promise<DeleteResult>;
  move(from: string, to: string): Promise<MoveResult>; // keepLinks: true, always
  /** Attachments (PDFs, images), only on a server whose versions answer says `attachments`. */
  readFile(path: string): Promise<RemoteFile | null>;
  putFile(path: string, bytes: ArrayBuffer, base: Version | null): Promise<FilePutResult>;
  removeFile(path: string, base: Version): Promise<FileDeleteResult>;
  vaults(): Promise<ListedVault[]>;
}

export interface LocalFiles {
  /** True where "Plan.md" and "plan.md" are one file (macOS, Windows, Android, iOS). */
  readonly caseInsensitive: boolean;
  list(): Promise<string[]>; // vault-relative paths of all files, any extension; never a folder marker
  /**
   * Vault-relative paths of every folder marker (`<folder>/.vaultkeep`) on disk outside dot folders: dot files are in
   * no `list()`, so markers are looked for on their own.
   */
  listMarkers(): Promise<string[]>;
  /** The text the user sees: an open editor's unsaved text if there is one, else the file's. */
  read(path: string): Promise<string | null>;
  /** Bytes on disk. For a folder marker only whether it exists counts, never its content (in this and every port below). */
  size(path: string): Promise<number | null>;
  /** Size in bytes and modification time (ms) on disk, null if there is no file. A marker: whether it exists. */
  stat(path: string): Promise<FileStat | null>;
  /**
   * Writes only if the text the user sees (as `read` gives it, so unsaved typing in an editor counts) still has
   * version `expected` (null: the file must not exist). Returns false if it changed.
   */
  writeIf(path: string, expected: Version | null, content: string): Promise<boolean>;
  /** The file's bytes as they are on disk (attachments), null if there is none. A marker's are never compared. */
  readBinary(path: string): Promise<ArrayBuffer | null>;
  /**
   * writeIf for bytes: writes only if the file's bytes still have version `expected` (null: it must not exist).
   * A marker's content is never compared: with `expected` null it is created if absent (false if it exists); with any
   * other `expected` nothing is written, and the answer is whether it exists.
   */
  writeBinaryIf(path: string, expected: Version | null, bytes: ArrayBuffer): Promise<boolean>;
  trashToLocal(path: string): Promise<void>; // Obsidian's .trash, always (first-sync "Keep Kumbu²")
  /**
   * Trashes by the user's trash setting (remote deletions), only if the text the user sees (as `read` gives it)
   * still has version `expected`, checked right before trashing. Returns false, and keeps the file, if it changed.
   */
  trashIf(path: string, expected: Version): Promise<boolean>;
  /**
   * trashIf for an attachment: by the user's trash setting, only if the file's bytes still have version `expected`.
   * A marker holds nothing worth keeping: it is removed if present, whatever its content; false if there is none.
   */
  trashBinaryIf(path: string, expected: Version): Promise<boolean>;
  rename(from: string, to: string): Promise<void>;
  /** Deletes the folder if it has nothing in it at all, dot files included. True if it deleted it. */
  removeFolderIfEmpty(path: string): Promise<boolean>;
  /** The folder exists and has nothing in it at all, dot files included. */
  isEmptyFolder(path: string): Promise<boolean>;
  /** True once for a folder this LocalFiles made itself (for a download); asking forgets it. */
  madeFolder(path: string): boolean;
}

export interface FileStat { size: number; mtime: number; }

export interface SyncState {
  bases: Record<string, Version>; // vault-relative path → agreed version
  /**
   * Attachment path → the stat it had when its bytes were last hashed, and their version: a file whose stat is the
   * same is not read again only to find it unchanged. Optional: state saved before attachments has none.
   */
  hashed?: Record<string, FileStat & { version: Version }>;
  cursor: string | null; lastFullAt: string | null; lastSyncAt: string | null;
  /** The first sync's answer to its one question, kept until the first sync is done, so it is never asked twice. */
  pendingChoice?: "both" | "local" | "kumbu" | null;
}
export interface StateStore { load(): Promise<SyncState>; save(state: SyncState): Promise<void>; }
/**
 * Which attachments' bytes this device keeps (offline mode design §6), asked for each PDF or image Kumbu² lists under
 * the limit. No leaves it as one too large there: nothing is downloaded, and a file of that name here is neither
 * replaced, sent nor trashed until the answer is yes; a deletion in Kumbu² still trashes it. A host that changes an
 * answer to yes asks for a check. `size`: null from a server before 0.6.0, which lists no sizes.
 */
export interface FetchPolicy { wantBytes(path: string, size: number | null): boolean; }
export interface Clock { now(): number; setTimeout(fn: () => void, ms: number): unknown; clearTimeout(handle: unknown): void; }

/**
 * "attachment": a file other than a note on a server without attachments (today's list); "unsupported": a format
 * this plugin doesn't sync, on a server that keeps PDFs and images; "too-large-in-kumbu": a PDF or image over the
 * plugin's limit in Kumbu², which it doesn't download.
 */
export type NotSyncedReason = "attachment" | "unsupported" | "too-large" | "too-large-in-kumbu" | "rejected" | "case-clash" | "damaged-vault";
export interface NotSynced { path: string; reason: NotSyncedReason; detail?: string; }
export type ConnectionMode = "live" | "polling" | "offline";
export interface SyncStatus { mode: ConnectionMode; notSynced: NotSynced[]; conflicts: string[]; busy: boolean; lastSyncAt: string | null; }
