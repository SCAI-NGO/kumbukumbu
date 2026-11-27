// SPDX-License-Identifier: AGPL-3.0-or-later
import { ownedBy, userKey, type SealedMirror } from "eidolon-core/store/mirror";
import { MirrorLockedError } from "eidolon-core/store/vault";
import { create } from "zustand";
import type { TreeFolder } from "../api/notes";
import { useOfflineCopy } from "../offline/copy";

/**
 * The last note opened, kept per device and user so that the app starts there again. Only ever a note of an ordinary
 * vault: nothing of a discreet one reaches the browser's storage (discreet spec §3.5). With the user's copy unlocked in
 * this tab it is a sealed record of the copy, and goes with it; otherwise it is kept in local storage under the hash
 * of the user id the copy's header holds, so that nothing there names the user (ADR 0045). Storage may be blocked, so
 * every access is guarded.
 */
const keyOf = (hash: string) => `kumbu.lastNote.${hash}`;
/** Where 0.6.0 kept it, under the user's id: moved on its first read. */
const oldKeyOf = (userId: string) => `kumbu.lastNote.${userId}`;

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, path: string | null): void {
  try {
    if (path === null) localStorage.removeItem(key);
    else localStorage.setItem(key, path);
  } catch {
    // Not kept: the app starts as it would on a new device.
  }
}

/** This user's copy, unlocked in this tab. */
async function copyOf(userId: string): Promise<SealedMirror | null> {
  const mirror = useOfflineCopy.getState().mirror;
  return mirror !== null && !mirror.locked && (await ownedBy(mirror.header, userId)) ? mirror : null;
}

/** The copy locked meanwhile (signed out, idle): nothing is kept, as on a new device. */
async function inCopy<T>(fn: () => Promise<T>, locked: T): Promise<T> {
  try {
    return await fn();
  } catch (e) {
    if (e instanceof MirrorLockedError) return locked;
    throw e;
  }
}

/** Per user, what was read: undefined until `loadLastNote` has read it. */
const useKept = create<Record<string, string | null>>()(() => ({}));
const loading = new Map<string, Promise<void>>();

/** The note kept for the user, once read; undefined before. */
export const useLastNote = (userId: string | null) => useKept((s) => (userId ? s[userId] : null));
export const lastNote = (userId: string): string | null => useKept.getState()[userId] ?? null;

/**
 * Reads the user's note once, after the tab's opening of their copy (`opened`). What local storage holds is newer
 * than the copy (keeping it in the copy removes it there): it moves into the copy, or under the hash.
 */
export function loadLastNote(userId: string, opened: Promise<void>): Promise<void> {
  const under = loading.get(userId);
  if (under) return under;
  const done = (async () => {
    await opened;
    const key = keyOf(await userKey(userId));
    const local = read(key) ?? read(oldKeyOf(userId));
    const mirror = await copyOf(userId);
    let kept = local;
    if (mirror !== null) {
      kept = await inCopy(async () => {
        if (local === null) return mirror.lastNote();
        await mirror.keepLastNote(local);
        write(key, null);
        return local;
      }, null);
    } else if (local !== null) write(key, local);
    write(oldKeyOf(userId), null);
    return kept;
  })()
    .catch((e: unknown) => {
      console.error("Kumbu² last note:", e);
      return null;
    })
    // A note kept or forgotten while this read was under way is the newer word.
    .then((kept) => useKept.setState((s) => (userId in s ? s : { [userId]: kept })));
  loading.set(userId, done);
  return done;
}

export async function keepLastNote(userId: string, path: string): Promise<void> {
  useKept.setState({ [userId]: path });
  const key = keyOf(await userKey(userId));
  const mirror = await copyOf(userId);
  if (mirror === null) return write(key, path);
  await inCopy(() => mirror.keepLastNote(path), undefined);
  write(key, null);
}

export async function forgetLastNote(userId: string | null | undefined): Promise<void> {
  if (!userId) return;
  useKept.setState({ [userId]: null });
  write(oldKeyOf(userId), null);
  const mirror = await copyOf(userId);
  if (mirror !== null) await inCopy(() => mirror.keepLastNote(null), undefined);
  write(keyOf(await userKey(userId)), null);
}

/**
 * A copy goes from the device (`user`: its header's hash of the user id): the note kept for that user goes with it.
 * What was read in this tab is read again.
 */
export function forgetLastNoteOf(user: string): void {
  write(keyOf(user), null);
  useKept.setState({}, true);
  loading.clear();
}

/**
 * The user whose start this page load has settled: the kept note opens once, when the app starts, and never on a
 * later `/` (the vaults asked for, another tab's note kept meanwhile).
 */
let settled: string | null = null;
export const startSettled = (userId: string) => settled === userId;
export function settleStart(userId: string | null): void {
  if (userId) settled = userId;
}
/** A new page load, for the tests. */
export function startOver(): void {
  settled = null;
  useKept.setState({}, true);
  loading.clear();
}

/** Whether a vault is ordinary: listed, and neither marked discreet nor among the discreet vaults shown. */
export const ordinary = (vaults: { name: string; discreet?: boolean }[] | undefined, names: ReadonlySet<string>, vault: string) =>
  !!vaults?.some((v) => v.name === vault && !v.discreet) && !names.has(vault);

/** Whether the tree holds the note. */
export function treeHas(root: TreeFolder, path: string): boolean {
  return root.notes.some((n) => n.path === path) || root.folders.some((f) => path.startsWith(`${f.path}/`) && treeHas(f, path));
}
