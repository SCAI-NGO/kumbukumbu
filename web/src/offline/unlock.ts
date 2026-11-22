// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline copy's unlocking and locking (offline mode design §3a "Unlocking", "Sign in again, sign out"): the
// sign-in form opens it offline after a reload, an offline unlock ends like a session, and a sign-out offline is
// pending until the device is online again.
import { IdleLock } from "eidolon-core/store/idle";
import type { SealedMirror } from "eidolon-core/store/mirror";
import { useSession } from "../state/session";
import { useOfflineCopy } from "./copy";
import { keep, mirrorCopies, useOfflineMode } from "./mode";

/** The sign-in page's line after a sign-out offline. */
export const PENDING_NOTICE = "Your session ends on the server when this device is back online.";
const PENDING = "kumbu.signOutPending";

const beforeLock = new Set<() => void | Promise<void>>();

/** Runs before the copy locks: the editor writes its unsaved text into the copy then. */
export function onBeforeLock(fn: () => void | Promise<void>): () => void {
  beforeLock.add(fn);
  return () => void beforeLock.delete(fn);
}

let idle: IdleLock | null = null;
const clock = { now: () => Date.now(), setTimeout: (fn: () => void, ms: number) => setTimeout(fn, ms), clearTimeout: (h: unknown) => clearTimeout(h as number) };

if (typeof window !== "undefined") {
  for (const type of ["keydown", "pointerdown", "wheel", "touchstart"]) window.addEventListener(type, () => idle?.touch(), { passive: true, capture: true });
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
  keep(opened);
  // Like the session it stands in for: locked after the idle limit of the last sign-in here (§3a).
  idle?.stop();
  idle = new IdleLock(clock, opened.header.idleMs, () => void lockCopy().then(() => useSession.getState().end()));
  useOfflineMode.getState().waitingChanged();
  return true;
}

/** Drops the key from this tab: unsaved editor text goes into the copy first; changes waiting stay sealed. */
export async function lockCopy(): Promise<void> {
  const { header, mirror } = useOfflineCopy.getState();
  idle?.stop();
  idle = null;
  if (mirror === null || header === null) return;
  for (const fn of [...beforeLock]) await fn();
  mirror.lock();
  useOfflineCopy.getState().set(header, null);
}

/**
 * Signs out offline (§3a): the server can't be told and the HttpOnly cookie can't be deleted, so a mark (no secret)
 * has the next start online end the session first. With `remove`, the copy goes from this device, its waiting
 * changes with it; otherwise they stay sealed and upload at the next sign-in here.
 */
export async function signOutOffline(remove: boolean): Promise<void> {
  try {
    localStorage.setItem(PENDING, "1");
  } catch {
    // Storage refused: the session ends with its own limits, as before offline mode.
  }
  const mirror: SealedMirror | null = useOfflineCopy.getState().mirror;
  if (remove && mirror !== null) {
    idle?.stop();
    idle = null;
    await mirror.remove();
  } else {
    await lockCopy();
  }
  useOfflineCopy.getState().clear();
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
export async function endPendingSignOut(): Promise<void> {
  if (!signOutPending()) return;
  try {
    const response = await fetch("/api/session", { method: "DELETE", credentials: "same-origin", signal: AbortSignal.timeout(5_000) });
    // A 401: the session had ended on its own.
    if (response.ok || response.status === 401) localStorage.removeItem(PENDING);
  } catch {
    // Still offline.
  }
}
