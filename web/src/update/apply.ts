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

let scrollTop: (() => number) | null = null;

/** The open note's view says how far down its reader is; null when it goes (then nothing is restored). */
export function registerScrollTop(getter: (() => number) | null): void {
  scrollTop = getter;
}

let holds = 0;

/**
 * Something a reload would lose that isn't the note editor's (a secret shown once and not saved yet, an upload
 * running) holds updates until it lets go: the next navigation or return to the page tries again.
 */
export function holdUpdates(): () => void {
  holds++;
  let released = false;
  return () => {
    if (released) return;
    released = true;
    holds--;
  };
}

/** Holds updates while `active`. */
export function useHoldUpdates(active: boolean): void {
  useEffect(() => (active ? holdUpdates() : undefined), [active]);
}

// Text typed into a field (a dialog's name, a form, the sign-in screen) is lost by a reload too: the fields typed
// into are remembered, and hold updates while they are on the page and not empty. The note editor's source has
// its own guard (beforeRead), and its text is never empty.
const notTyped = new Set(["checkbox", "radio", "range", "file", "color", "button", "submit", "reset", "hidden", "image"]);
const typed = new Set<HTMLInputElement | HTMLTextAreaElement>();
if (typeof document !== "undefined") {
  document.addEventListener(
    "input",
    (e) => {
      const field = e.target;
      if (field instanceof HTMLTextAreaElement && field.id !== "markdown-source") typed.add(field);
      else if (field instanceof HTMLInputElement && !notTyped.has(field.type)) typed.add(field);
    },
    true,
  );
}

function busy(): boolean {
  for (const field of typed) if (!field.isConnected || field.value === "") typed.delete(field);
  return holds > 0 || typed.size > 0;
}

/**
 * Reloads into the server's newer build if that is safe now: nothing holds it (a secret not saved, an upload,
 * typed text), no text a session end kept waits for its sign-in, and an open editor has saved everything (no
 * conflict, no failure); asked twice, as a keystroke may come in while the first save runs. At most once per
 * version: if this is still the old app after the reload, it says so instead of trying again. `reason` is who
 * asked: the bar's button, or the watcher; both are held back the same way.
 */
export async function tryReload(_reason: "auto" | "button", reload = () => page.reload()): Promise<boolean> {
  const { available, fail } = useUpdate.getState();
  if (!available) return false;
  if (read(reloadedKey) === full(available)) {
    fail(available);
    return false;
  }
  // A search hit's section waits to be scrolled to (memory only): the reload would open the note at the top.
  const { mode, beforeRead, draft, scrollTarget } = useEditor.getState();
  if (draft || scrollTarget || busy()) return false;
  if (beforeRead && !((await beforeRead({ reread: false })) && (await beforeRead({ reread: false })))) return false;
  if (busy()) return false;
  write(reloadedKey, full(available));
  // Only from a note's page: the URL says which note, this says where in it. Anywhere else nothing is restored.
  write(restoreKey, scrollTop ? JSON.stringify({ mode, scrollTop: Math.max(0, Math.round(scrollTop())) } satisfies Restore) : null);
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

// The server runs this app's own build again: the guard for another build is over (store.ts clears `failed`).
useUpdate.subscribe((state, previous) => {
  if (state.server !== previous.server && state.server === appVersion) write(reloadedKey, null);
});

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
    const check = () => void checkVersion().then(() => tryReload("auto"));
    const onVisible = () => document.visibilityState === "visible" && check();
    document.addEventListener("visibilitychange", onVisible);
    // A part of the app that won't load (a lazy page's old chunk, gone after a deploy) means a new release, likely.
    window.addEventListener("vite:preloadError", check);
    return () => {
      document.removeEventListener("visibilitychange", onVisible);
      window.removeEventListener("vite:preloadError", check);
    };
  }, []);

  // A version this tab already reloaded for, and still old: said at once, not at the next navigation.
  useEffect(() => {
    if (available && read(reloadedKey) === full(available)) useUpdate.getState().fail(available);
  }, [available]);

  useEffect(() => {
    if (useUpdate.getState().available) void tryReload("auto");
  }, [key]);
}
