// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline copy's unlocking and locking (offline mode design §3a "Unlocking", "Sign in again, sign out"): the
// sign-in form opens it offline after a reload, an offline unlock ends like a session, and a sign-out offline is
// pending until the device is online again.
import type { SealedMirror } from "eidolon-core/store/mirror";
import { useOfflineCopy } from "./copy";
import { keep, lockEverywhere, mirrorCopies, useOfflineMode } from "./mode";

// Where the locking was announced: it lives beside the copy's unlocking in mode.ts, which arms every copy's idle lock.
export { lockCopy, onBeforeLock } from "./mode";

/** The sign-in page's line after a sign-out offline. */
export const PENDING_NOTICE = "Your session ends on the server when this device is back online.";
const PENDING = "kumbu.signOutPending";

if (typeof window !== "undefined") {
  // Back online: the server hears of a sign-out made while away.
  window.addEventListener("online", () => void endPendingSignOut());
}

/**
 * The sign-in form offline: the username finds the copy (two people may share a browser), the password opens it.
 * False for a wrong password or a username this device keeps no copy for, alike: the form says the same for both.
 */
export async function unlockOffline(username: string, password: string): Promise<boolean> {
  if (typeof indexedDB === "undefined") return false;
  const all = await mirrorCopies();
  const header = await all.forUsername(username);
  const opened = header === null ? null : await all.unlockOffline(header, password);
  if (opened === null) return false;
  // Locked after the idle limit of the last sign-in here, like the session it stands in for (§3a).
  keep(opened);
  useOfflineMode.getState().waitingChanged();
  return true;
}

/**
 * Signs out offline (§3a): the server can't be told and the HttpOnly cookie can't be deleted, so a mark (no secret)
 * has the next start online end the session first. With `remove`, the copy goes from this device, its waiting
 * changes with it; otherwise they stay sealed and upload at the next sign-in here.
 */
export async function signOutOffline(remove: boolean): Promise<boolean> {
  let marked = true;
  try {
    localStorage.setItem(PENDING, "1");
  } catch {
    // Storage refused: the session ends with its own limits, as before offline mode.
    marked = false;
  }
  const mirror: SealedMirror | null = useOfflineCopy.getState().mirror;
  // Every tab: a forgotten second one on a borrowed device is the risk. A removal locks them anyway.
  if (remove && mirror !== null) await mirror.remove();
  else await lockEverywhere();
  useOfflineCopy.getState().clear();
  return marked;
}

export function signOutPending(): boolean {
  try {
    return localStorage.getItem(PENDING) !== null;
  } catch {
    return false;
  }
}

/**
 * Ends a session signed out offline, before anything else talks to the server: at the start, when the browser is
 * online again, and before a sign-in (which would otherwise end the new session instead). Kept for the next try
 * while the server can't be reached.
 */
let ending: Promise<void> | null = null;

export function endPendingSignOut(): Promise<void> {
  if (!signOutPending()) return Promise.resolve();
  ending ??= (async () => {
    try {
      const response = await fetch("/api/session", { method: "DELETE", credentials: "same-origin", signal: AbortSignal.timeout(5_000) });
      // A 401: the session had ended on its own.
      if (response.ok || response.status === 401) localStorage.removeItem(PENDING);
    } catch {
      // Still offline.
    } finally {
      ending = null;
    }
  })();
  return ending;
}

/**
 * At the sign-in page, online: asks once per copy kept here, with its slot's token, whether the slot still exists,
 * and removes the copies that are answered `mirror-wipe` (design §4.6). Without it, a copy removed in
 * the device list, or an account deleted, would stay until its owner signed in here again.
 */
export async function sweepRemovedCopies(): Promise<void> {
  if (typeof indexedDB === "undefined") return;
  const all = await mirrorCopies();
  for (const header of await all.headers()) {
    try {
      const response = await fetch("/api/session", { credentials: "same-origin", headers: { "Kumbu-Mirror": header.slot.token } });
      const body = (await response.json().catch(() => null)) as { code?: string } | null;
      if (response.status === 401 && body?.code === "mirror-wipe") await all.wipe(header.copy);
    } catch {
      return; // offline: the next visit asks
    }
  }
}
