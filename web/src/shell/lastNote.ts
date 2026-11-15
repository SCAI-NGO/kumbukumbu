// SPDX-License-Identifier: AGPL-3.0-or-later
import type { TreeFolder } from "../api/notes";

/**
 * The last note opened, kept per device and user so that the app starts there again. Only ever a note of an ordinary
 * vault: nothing of a discreet one reaches the browser's storage (discreet spec §3.5). Storage may be blocked, so
 * every access is guarded.
 */
const keyOf = (userId: string) => `kumbu.lastNote.${userId}`;

export function lastNote(userId: string): string | null {
  try {
    return localStorage.getItem(keyOf(userId));
  } catch {
    return null;
  }
}

export function keepLastNote(userId: string, path: string): void {
  try {
    localStorage.setItem(keyOf(userId), path);
  } catch {
    // Not kept: the app starts as it would on a new device.
  }
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
}

export function forgetLastNote(userId: string | null | undefined): void {
  if (!userId) return;
  try {
    localStorage.removeItem(keyOf(userId));
  } catch {
    // Nothing was kept either.
  }
}

/** Whether a vault is ordinary: listed, and neither marked discreet nor among the discreet vaults shown. */
export const ordinary = (vaults: { name: string; discreet?: boolean }[] | undefined, names: ReadonlySet<string>, vault: string) =>
  !!vaults?.some((v) => v.name === vault && !v.discreet) && !names.has(vault);

/** Whether the tree holds the note. */
export function treeHas(root: TreeFolder, path: string): boolean {
  return root.notes.some((n) => n.path === path) || root.folders.some((f) => path.startsWith(`${f.path}/`) && treeHas(f, path));
}
