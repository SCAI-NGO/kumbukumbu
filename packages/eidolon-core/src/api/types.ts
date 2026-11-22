// SPDX-License-Identifier: AGPL-3.0-or-later
// Every request and response shape of the Kumbu² server the plugin uses, and nowhere else.
// Source: the API contract in kumbukumbu's docs/superpowers/plans/2026-10-26-obsidian-sync-server.md
// and src/Kumbu.Server/Api/NoteApi.cs, FileApi.cs and SyncApi.cs. Paths are vault-tree paths ("/Work/plan.md").

export interface ErrorView { error: string; code?: string; type?: string; }

/** GET /api/vaults/{vault}/versions[?attachments=1][&since=] (ETag "<state>"; 304 for If-None-Match without since). */
export interface VersionsView {
  full: boolean;
  cursor: string;
  state: string;
  damaged: number;
  /** With attachments=1, attachments too, and every entry with its kind. */
  notes: VersionEntry[];
  /** Only when full is false: touched paths with no note (or attachment) now. */
  deleted?: string[];
  /** True when asked with attachments=1 of a server that keeps attachments; absent from an older server. */
  attachments?: boolean;
}
/** `kind` may also be one the plugin doesn't know ("file" from 0.6.0 on): such entries are left alone. `size`: an attachment's bytes, from 0.6.0 on. */
export interface VersionEntry { path: string; version: string; kind?: "note" | "image" | "pdf" | "folder"; size?: number; }

/** GET/PUT /api/notes/{vault}/{path}. */
export interface NoteView {
  path: string;
  content: string;
  version: string;
  lastWriter: string;
  lastWriteAt: string;
  current: boolean;
  /** Sessions only; null for API key callers. */
  lastWriterKind?: string | null;
}

/** PUT /api/notes/{vault}/{path}; version omitted to create. */
export interface PutNoteRequest { content: string; version?: string; }

/** 409 on PUT and DELETE. */
export interface ConflictView { error: string; current?: { content: string; version: string }; }

/** PUT /api/files/{vault}/{path}[?version=] (raw body, 200/201), and in a 409 as `current`. GET answers bytes, ETag = version. */
export interface FileStateView {
  path: string;
  mediaType: string;
  size: number;
  width: number | null;
  height: number | null;
  version: string;
  writtenAt: string;
  writtenBy: string;
}

/** 409 on PUT and DELETE /api/files: the file as it is now, without its bytes. */
export interface FileConflictView { error: string; current?: FileStateView; }

/** POST /api/move. */
export interface MoveRequest { from: string; to: string; keepLinks: boolean; }
export interface MovedView { message: string; path: string | null; version: string | null; }

/** POST /api/ws-ticket. */
export interface WsTicketView { ticket: string; expiresAt: string; }

/** GET /api/vaults. `id` and `discreet` from 0.6.0 on; a vault's id stays when it is renamed. */
export interface VaultView { id?: string; discreet?: boolean; name: string; notes: number; embeddings: string; pending: boolean; canWrite?: boolean; shared?: boolean; level?: string; }

/** POST /api/pairing/redeem (anonymous). */
export interface RedeemRequest { code: string; device: string; plugin: string; }
export interface RedeemView { key: string; vault: string; keyName: string; }

/** WebSocket message, server → client. */
export interface HintMessage { type: "changed"; vault: string; }

/** WebSocket close code for a revoked or expired key. */
export const CLOSE_KEY_REVOKED = 4401;
