// SPDX-License-Identifier: AGPL-3.0-or-later
// Writing offline (offline mode design §3, work package 7): while the host has offline writes on, the editor's saves,
// new notes and trashing go into the copy on this device instead of to the server, through the core's NoteWriter, and
// the engine uploads them once online. The answers keep the shapes of the server's, a 409 and a refusal's 403
// included, so the editor and its conflict choice work as they do online.
//
// The host (the offline mode and the engines) turns it on with `setOfflineWrites` when the app goes offline and off
// once every change has uploaded: `vaultFor` finds a web path's vault mirror (the offline source's), `written` tells
// that vault's engine (`localChanged`), and `onOfflineWrite` lets the mode count the changes waiting again.
import { EditorDrafts, NotHereError, NoteWriter, ReadOnlyError, type OpenEditor, type SaveAnswer } from "eidolon-core/store/writing";
import type { VaultMirror } from "eidolon-core/store/vault";
import { useEffect, useRef, useSyncExternalStore } from "react";
import { HttpError } from "../api/client";
import { folderOf, isAttachment, untitledName, vaultOf } from "../api/paths";
import { textOrder } from "../api/textOrder";

export interface OfflineWrites {
  /** The mirror of the vault a web path (`/Work/a.md`) is in, and the path within it; null for a vault not kept here. */
  vaultFor(path: string): { mirror: VaultMirror; inVault: string } | null;
  /** A path of the vault was written here: its engine is told. */
  written(vaultId: string, path: string): void;
}

let current: OfflineWrites | null = null;
const listeners = new Set<() => void>();
const switched = new Set<() => void>();
const writers = new WeakMap<VaultMirror, NoteWriter>();
const drafts = new Map<string, EditorDrafts>();

/** On while the app is offline (or still uploading), off (null) online. */
export function setOfflineWrites(writes: OfflineWrites | null): void {
  current = writes;
  for (const fn of switched) fn();
}

export const offlineWrites = () => current;

/** Runs after every offline write: the changes waiting are counted again. Returns a function that stops it. */
export function onOfflineWrite(fn: () => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

/** The open editors of a vault (by id), as its engine's files see them: `vault.files(draftsFor(vault.id))`. */
export function draftsFor(vaultId: string): EditorDrafts {
  let d = drafts.get(vaultId);
  if (d === undefined) drafts.set(vaultId, (d = new EditorDrafts()));
  return d;
}

function writerFor(path: string): { writer: NoteWriter; mirror: VaultMirror; inVault: string } {
  const found = current?.vaultFor(path) ?? null;
  if (found === null) throw new HttpError(503, `${vaultOf(path)} isn't kept on this device: this needs a connection.`);
  const { mirror, inVault } = found;
  let writer = writers.get(mirror);
  if (writer === undefined) {
    writer = new NoteWriter(mirror, (p) => {
      current?.written(mirror.id, p);
      for (const fn of listeners) fn();
    });
    writers.set(mirror, writer);
  }
  return { writer, mirror, inVault };
}

/** The core's refusals as the server's: a read-only vault's 403 with its code, a note not here yet. */
async function asServer<T>(path: string, fn: () => Promise<T>): Promise<T> {
  try {
    return await fn();
  } catch (e) {
    if (e instanceof ReadOnlyError) throw new HttpError(403, `You can only read ${vaultOf(path)}.`, undefined, "read-only");
    if (e instanceof NotHereError) throw new HttpError(503, e.message);
    throw e;
  }
}

/** A save as `PUT /api/notes` with a version answers it: the new version, or the text there now (a 409). */
export function saveOffline(path: string, content: string, version: string): Promise<SaveAnswer> {
  return asServer(path, () => {
    const { writer, inVault } = writerFor(path);
    return writer.save(inVault, content, version);
  });
}

/** A new note at `path`; a 409 if one is there. Answers the path. */
export function createOffline(path: string, content: string): Promise<string> {
  return asServer(path, async () => {
    const { writer, inVault } = writerFor(path);
    if ((await writer.create(inVault, content)) === null) throw new HttpError(409, `${path.slice(path.lastIndexOf("/") + 1)} is there already.`);
    return path;
  });
}

/** The first free `Untitled` note in a folder (`/Work/projects`), started with a heading, as online. */
export function createUntitledOffline(folder: string): Promise<string> {
  return asServer(folder, async () => {
    const { writer, mirror, inVault: within } = writerFor(folder);
    const names = notesIn(mirror, within);
    // A note made in the board's folder is a card, and the board needs a connection.
    if (within === "Board" && names.includes("Board.md")) throw new HttpError(503, "The board needs a connection. It's back when you are.");
    for (;;) {
      const name = untitledName(names);
      if ((await writer.create(within === "" ? name : `${within}/${name}`, "# ")) !== null) return `${folder}/${name}`;
      names.push(name);
    }
  });
}

/**
 * Trashes a note in the copy (Kumbu² trashes it on upload) and answers where to go, as online: the next note of its
 * folder, else the one before, or null.
 */
export function trashOffline(path: string): Promise<string | null> {
  return asServer(path, async () => {
    const { writer, mirror, inVault } = writerFor(path);
    const folder = folderOf(path);
    const siblings = notesIn(mirror, inVault.includes("/") ? inVault.slice(0, inVault.lastIndexOf("/")) : "")
      .map((n) => `${folder}/${n}`)
      .filter((p) => p === path || !isAttachment(p));
    const at = siblings.indexOf(path);
    const next = siblings[at + 1] ?? (at > 0 ? siblings[at - 1] : undefined) ?? null;
    await writer.trash(inVault);
    return next;
  });
}

/** The names of the files right in a folder of the mirror (`""`: the vault's top), in the tree's text order. */
function notesIn(mirror: VaultMirror, folder: string): string[] {
  const prefix = folder === "" ? "" : `${folder}/`;
  return mirror
    .list()
    .map((e) => e.path)
    .filter((p) => p.startsWith(prefix) && !p.slice(prefix.length).includes("/"))
    .map((p) => p.slice(prefix.length))
    .sort(textOrder);
}

/**
 * Makes the open editor of `path` the copy's draft for it while offline writes are on: what the engine reads is the
 * text the user sees, so a download meanwhile (uploading, back online) never replaces unsaved typing. Not online: the
 * editor saves to the server then, and the engine reading unsaved text would upload it beside the editor's own save.
 */
export function useOfflineDraft(path: string, editor: OpenEditor): void {
  const on = useSyncExternalStore((fn) => (switched.add(fn), () => switched.delete(fn)), offlineWrites);
  const latest = useRef(editor);
  latest.current = editor;
  useEffect(() => {
    const found = on?.vaultFor(path) ?? null;
    if (found === null) return;
    return draftsFor(found.mirror.id).open(found.inVault, { text: () => latest.current.text(), replaced: (c) => latest.current.replaced(c) });
  }, [on, path]);
}
