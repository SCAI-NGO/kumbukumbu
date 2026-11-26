// SPDX-License-Identifier: AGPL-3.0-or-later
// What a device keeps offline (offline mode design §6, §3a "Settings, Offline"): which vaults, which folders whole
// ("Keep offline"), and the larger attachments opened online, kept within a budget, the least recently opened going
// first. Sealed in the copy like everything else about notes: folder paths name things (§4.1).
import { attachmentKind } from "../engine/paths";
import { SMALL_ATTACHMENT_BYTES, type VaultMirror } from "./vault";

const MB = 1_048_576;

/** The budgets Settings offers for attachments opened online, 500 MB by default (§3a). */
export const BUDGETS = [250 * MB, 500 * MB, 1024 * MB, 2048 * MB] as const;

export interface OfflineChoice {
  /** "All my vaults": new and newly shared vaults follow. */
  all: boolean;
  /** The vaults chosen one by one, while `all` is off. */
  vaults: string[];
  /** Bytes for the attachments opened online (not those under Keep offline, nor the small ones). */
  budget: number;
  /** Per vault, the folders kept offline whole; "" is the whole vault. */
  kept: Record<string, string[]>;
  /** Per vault, attachments opened online and when: the budget's order. */
  opened: Record<string, Record<string, number>>;
}

export const DEFAULT_CHOICE: OfflineChoice = { all: true, vaults: [], budget: 500 * MB, kept: {}, opened: {} };

export function mirrors(choice: OfflineChoice, vault: string): boolean {
  return choice.all || choice.vaults.includes(vault);
}

/** Below a folder kept offline, or in a vault kept whole. */
export function isKept(choice: OfflineChoice, vault: string, path: string): boolean {
  return (choice.kept[vault] ?? []).some((folder) => folder === "" || path.startsWith(`${folder}/`));
}

/**
 * Whether the device wants an attachment's bytes beyond the small ones that come with the notes: kept offline, opened
 * online, or already here (a later version of a file this device holds replaces it; the budget decides what goes).
 */
export function wanted(choice: OfflineChoice, vault: VaultMirror, path: string): boolean {
  return isKept(choice, vault.id, path) || choice.opened[vault.id]?.[path] !== undefined || vault.entry(path)?.local !== undefined;
}

/** An attachment's bytes this device holds. */
function heldAttachments(vault: VaultMirror): { path: string; size: number }[] {
  return vault.list()
    .filter((e) => e.local !== undefined && !e.local.text && attachmentKind(e.path) !== null)
    .map((e) => ({ path: e.path, size: e.local!.size }));
}

/** Bytes of attachments on this device, those under Keep offline and the small ones too: "space used" in Settings. */
export function attachmentsUsed(vaults: readonly VaultMirror[]): number {
  return vaults.reduce((n, v) => n + heldAttachments(v).reduce((m, a) => m + a.size, 0), 0);
}

/**
 * What goes to bring the attachments opened online back within the budget, least recently opened first: per vault,
 * the paths. Never one kept offline or small, nor one changed here and not uploaded yet (it would be lost).
 */
export function evictions(choice: OfflineChoice, vaults: readonly VaultMirror[]): Map<string, string[]> {
  const candidates: { vault: string; path: string; size: number; at: number }[] = [];
  let used = 0;
  for (const v of vaults) {
    for (const a of heldAttachments(v)) {
      if (a.size <= SMALL_ATTACHMENT_BYTES || isKept(choice, v.id, a.path)) continue;
      used += a.size;
      if (v.unchanged(a.path)) candidates.push({ vault: v.id, path: a.path, size: a.size, at: choice.opened[v.id]?.[a.path] ?? 0 });
    }
  }
  candidates.sort((a, b) => a.at - b.at);
  const out = new Map<string, string[]>();
  for (const c of candidates) {
    if (used <= choice.budget) break;
    used -= c.size;
    out.set(c.vault, [...(out.get(c.vault) ?? []), c.path]);
  }
  return out;
}

/** The choice with the evicted paths no longer opened, so the fetch policy says no to them again. */
export function forget(choice: OfflineChoice, evicted: Map<string, string[]>): OfflineChoice {
  const opened = { ...choice.opened };
  for (const [vault, paths] of evicted) {
    const left = { ...opened[vault] };
    for (const p of paths) delete left[p];
    opened[vault] = left;
  }
  return { ...choice, opened };
}

/** The choice without the vault: none of its folders kept or attachments opened, and not chosen. Unchanged: itself. */
export function without(choice: OfflineChoice, vault: string): OfflineChoice {
  if (!choice.vaults.includes(vault) && !(vault in choice.kept) && !(vault in choice.opened)) return choice;
  const { [vault]: _kept, ...kept } = choice.kept;
  const { [vault]: _opened, ...opened } = choice.opened;
  return { ...choice, vaults: choice.vaults.filter((v) => v !== vault), kept, opened };
}

/**
 * The choice with folders kept and attachments opened only in the vaults held. The vaults chosen stay: one may not be
 * adopted yet (ticked just before a lock, or its adoption failed offline). Unchanged: itself.
 */
export function heldOnly(choice: OfflineChoice, held: ReadonlySet<string>): OfflineChoice {
  const stray = (vault: string) => !held.has(vault);
  if (!Object.keys(choice.kept).some(stray) && !Object.keys(choice.opened).some(stray)) return choice;
  const kept = Object.fromEntries(Object.entries(choice.kept).filter(([vault]) => held.has(vault)));
  const opened = Object.fromEntries(Object.entries(choice.opened).filter(([vault]) => held.has(vault)));
  return { ...choice, kept, opened };
}
