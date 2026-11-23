// SPDX-License-Identifier: AGPL-3.0-or-later
// Reading the mirror offline (offline mode design §3, work package 5): a vault's tree and its notes as the server's
// `GET /api/vaults/{vault}/tree` and `GET /api/notes/...` answer them, so the host can stand the mirror in for them.
// Paths are the host's: `/<vault name>/<path in the vault>`, the vault named as it was last seen online.
import type { SealedMirror } from "../store/mirror";
import type { VaultMirror } from "../store/vault";
import { versionOf } from "../engine/version";
import { inArchive } from "../links/archive";
import { isFolderMarker, kindOf, mediaTypeOf } from "../links/formats";
import { textOrder } from "../links/order";

export interface TreeFile {
  name: string;
  path: string;
  size: number;
  kind: "note" | "image" | "pdf" | "text" | "file";
  mediaType: string;
  version?: string;
  writtenAt?: string;
  /** Its text or bytes are on this device; without them it is listed, and "Not on this device yet". */
  held: boolean;
}

export interface TreeFolder { name: string; path: string; folders: TreeFolder[]; notes: TreeFile[]; writtenAt?: string }

export interface TreeView { root: TreeFolder; damaged: number }

export interface NoteRead {
  path: string;
  content: string;
  /** The text's version, as the copy's `writeIf` compares it (no draft). */
  version: string;
  /** The copy doesn't know who wrote it. */
  lastWriter: string;
  lastWriteAt: string;
  current: true;
}

/** A note or attachment the inventory lists whose text or bytes haven't come yet (design §3a). */
export class NotOnDeviceError extends Error {
  constructor(readonly path: string) { super("Not on this device yet. It comes when you're back online."); }
}

/** Vault names compare as the server's do: without regard to case. */
const sameName = (a: string, b: string) => a.normalize("NFC").toUpperCase() === b.normalize("NFC").toUpperCase();

/** The held vault last seen online under `name`. */
export function vaultByName(mirror: SealedMirror, name: string): VaultMirror | undefined {
  return mirror.held().find((v) => v.name !== undefined && sameName(v.name, name));
}

/** The vault of a host path and the path within it; undefined when the path names no held vault. */
export function locate(mirror: SealedMirror, path: string): { vault: VaultMirror; inVault: string } | undefined {
  const [, name, ...rest] = path.split("/");
  if (name === undefined || name === "") return undefined;
  const vault = vaultByName(mirror, name);
  return vault === undefined ? undefined : { vault, inVault: rest.join("/") };
}

/** A path the person sees: listed or held, not removed here, never a dot file (no folder marker either). */
function shown(vault: VaultMirror, path: string): boolean {
  return !vault.removedHere(path) && !path.split("/").some((s) => s.startsWith("."));
}

/**
 * The vault's tree as the server builds it: folders first, then files, both in text order; the archive left out; a
 * folder marker keeps its folder but is no file of it. Built from the inventory, so it is whole before the texts are
 * here. No flags: they come from the note's tags, read only by the local index (work package 6).
 */
export function treeOf(vault: VaultMirror, name: string): TreeView {
  const root = `/${name}`;
  const files: { segments: string[]; file: TreeFile | null; mtime: number | null }[] = [];
  for (const e of vault.list()) {
    const full = `${root}/${e.path}`;
    if (inArchive(full) || vault.removedHere(e.path)) continue;
    const segments = e.path.split("/");
    const mtime = e.local?.mtime ?? null;
    if (isFolderMarker(full) || e.remote?.kind === "folder") {
      files.push({ segments, file: null, mtime });
      continue;
    }
    if (!shown(vault, e.path)) continue;
    // Markers were taken out above, so never "folder".
    const kind = kindOf(full) as TreeFile["kind"];
    const version = kind === "note" ? undefined : (e.local?.version ?? e.remote?.version);
    files.push({
      segments,
      mtime,
      file: {
        name: segments[segments.length - 1]!, path: full, size: e.local?.size ?? e.remote?.size ?? 0, kind, mediaType: mediaTypeOf(full),
        ...(version !== undefined ? { version } : {}),
        ...(mtime !== null ? { writtenAt: new Date(mtime).toISOString() } : {}),
        held: e.local !== undefined,
      },
    });
  }
  return { root: folderOf(name, root, 0, files), damaged: 0 };
}

function folderOf(name: string, path: string, depth: number, below: { segments: string[]; file: TreeFile | null; mtime: number | null }[]): TreeFolder {
  const groups = new Map<string, typeof below>();
  const notes: TreeFile[] = [];
  for (const item of below) {
    if (item.segments.length > depth + 1) {
      const key = item.segments[depth]!;
      const group = groups.get(key);
      if (group === undefined) groups.set(key, [item]);
      else group.push(item);
    } else if (item.file !== null) {
      notes.push(item.file);
    }
  }
  const folders = [...groups.keys()].sort(textOrder).map((key) => folderOf(key, `${path}/${key}`, depth + 1, groups.get(key)!));
  notes.sort((a, b) => textOrder(a.name, b.name));
  const newest = below.reduce<number | null>((n, i) => (i.mtime === null ? n : Math.max(n ?? 0, i.mtime)), null);
  return { name, path, folders, notes, ...(newest !== null ? { writtenAt: new Date(newest).toISOString() } : {}) };
}

/**
 * A note as `GET /api/notes` answers it; null where the server would answer 404 (not listed, or removed here).
 * Throws NotOnDeviceError for one the inventory lists whose text hasn't come yet.
 */
export async function readNote(mirror: SealedMirror, path: string): Promise<NoteRead | null> {
  const found = locate(mirror, path);
  if (found === undefined) return null;
  const { vault, inVault } = found;
  const entry = vault.entry(inVault);
  if (entry === undefined || !shown(vault, inVault)) return null;
  const bytes = entry.local === undefined ? null : await vault.bytes(inVault);
  if (bytes === null || entry.local === undefined) throw new NotOnDeviceError(path);
  const content = new TextDecoder().decode(bytes);
  return {
    path, content, version: entry.local.text ? entry.local.version : await versionOf(content),
    lastWriter: "", lastWriteAt: new Date(entry.local.mtime).toISOString(), current: true,
  };
}

/**
 * An attachment's bytes on this device; null where the server would answer 404. Throws NotOnDeviceError for one the
 * inventory lists whose bytes haven't been fetched (design §3a: "Opens when you're online").
 */
export async function readFile(mirror: SealedMirror, path: string): Promise<Uint8Array<ArrayBuffer> | null> {
  const found = locate(mirror, path);
  if (found === undefined) return null;
  const { vault, inVault } = found;
  const entry = vault.entry(inVault);
  if (entry === undefined || !shown(vault, inVault)) return null;
  const bytes = await vault.bytes(inVault);
  if (bytes === null) throw new NotOnDeviceError(path);
  return bytes;
}
