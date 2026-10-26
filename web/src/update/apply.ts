// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect } from "react";
import { useLocation } from "react-router-dom";
import { useEditor, type Mode } from "../state/editor";
import { checkVersion, useUpdate } from "./store";
import { appVersion, parseVersion, type Version } from "./version";

// Only a version string and { mode, scrollTop }: the URL already says which note, and nothing of a note is kept
// in the browser (state/editor.ts). Storage may be blocked, so every access is guarded.
const reloadedKey = "kumbu.update.reloadedFor";
const restoreKey = "kumbu.update.restore";

export type Restore = { mode: Mode; scrollTop: number };

function read(key: string): string | null {
  try {
    return sessionStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string | null): void {
  try {
    if (value === null) sessionStorage.removeItem(key);
    else sessionStorage.setItem(key, value);
  } catch {
    // Not kept: without the guard a blocked storage could reload once more per visit, never in a loop (the
    // reload happens only on a navigation or a return to the page).
  }
}

const full = (v: Version) => `${v.semver}+${v.sha}`;

/** The page's reload, replaceable in tests (jsdom's `location.reload` can't be stubbed). */
export const page = { reload: () => window.location.reload() };

let scrollTop: () => number = () => 0;

/** The open note's view says how far down its reader is; null when it goes. */
export function registerScrollTop(getter: (() => number) | null): void {
  scrollTop = getter ?? (() => 0);
}

/**
 * Reloads into the server's newer build if that is safe now: Read mode always is; Write mode once the editor has
 * saved everything (no conflict, no failure). At most once per version: if this is still the old app after the
 * reload, it says so instead of trying again.
 */
export async function tryReload(reason: "auto" | "button", reload = () => page.reload()): Promise<boolean> {
  const { available, fail } = useUpdate.getState();
  if (!available) return false;
  if (read(reloadedKey) === full(available)) {
    fail(available);
    return false;
  }
  const { mode, beforeRead } = useEditor.getState();
  if (mode === "write") {
    // Before the editor has loaded there is no guard: nothing typed yet, but on its own the app doesn't guess.
    if (!beforeRead) {
      if (reason === "auto") return false;
    } else if (!(await beforeRead({ reread: false }))) {
      return false;
    }
  }
  write(reloadedKey, full(available));
  write(restoreKey, JSON.stringify({ mode, scrollTop: Math.max(0, Math.round(scrollTop())) } satisfies Restore));
  reload();
  return true;
}

/** Where the reader was before the reload, once: the note view takes it when its note is shown. */
export function takeRestore(): Restore | null {
  const text = read(restoreKey);
  if (text === null) return null;
  write(restoreKey, null);
  try {
    const value = JSON.parse(text) as Partial<Restore>;
    if ((value.mode !== "read" && value.mode !== "write") || typeof value.scrollTop !== "number") return null;
    return { mode: value.mode, scrollTop: value.scrollTop };
  } catch {
    return null;
  }
}

/** After a reload: this app is the version it reloaded for, so say so. Still the old one: the guard fires later. */
function settleReload(): void {
  const reloadedFor = read(reloadedKey);
  if (reloadedFor === null || reloadedFor !== appVersion) return;
  write(reloadedKey, null);
  useUpdate.getState().markUpdated(parseVersion(reloadedFor)?.semver ?? null);
}

/**
 * Watches for a newer server, in the router's root so that signed-out screens update too: asks when the page
 * becomes visible again (a phone app back from the background), and reloads then or at the next navigation.
 * Not the moment a response reports it: the reader would lose the page under their eyes.
 */
export function useApplyUpdates(): void {
  const { key } = useLocation();
  const available = useUpdate((s) => s.available);

  useEffect(settleReload, []);

  useEffect(() => {
    const onVisible = () => {
      if (document.visibilityState !== "visible") return;
      void checkVersion().then(() => tryReload("auto"));
    };
    document.addEventListener("visibilitychange", onVisible);
    return () => document.removeEventListener("visibilitychange", onVisible);
  }, []);

  // A version this tab already reloaded for, and still old: said at once, not at the next navigation.
  useEffect(() => {
    if (available && read(reloadedKey) === full(available)) useUpdate.getState().fail(available);
  }, [available]);

  useEffect(() => {
    if (useUpdate.getState().available) void tryReload("auto");
  }, [key]);
}
