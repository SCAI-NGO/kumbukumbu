// SPDX-License-Identifier: AGPL-3.0-or-later

/**
 * Registers the app shell's worker (web/sw/worker.ts), which keeps the app's built files so it opens offline.
 * Only in a production build: the dev server serves the files itself. `updateViaCache: "none"`: the browser asks the
 * server for /sw.js itself every time, never its HTTP cache. A browser without service workers, or one that refuses
 * (a private window), runs the app as before, online only.
 */
export async function registerShell(): Promise<void> {
  if (!import.meta.env.PROD || !navigator.serviceWorker) return;
  try {
    await navigator.serviceWorker.register("/sw.js", { scope: "/", updateViaCache: "none" });
  } catch {
    // Online only, as before.
  }
}

/**
 * The server runs another build: its worker is fetched now, while this tab still works, so the reload into the new
 * build (apply.ts) finds that build's files already kept, and the new build opens offline from then on.
 */
export async function refreshShell(): Promise<void> {
  try {
    await (await navigator.serviceWorker?.getRegistration())?.update();
  } catch {
    // Offline, or the server is restarting: the browser asks again at the next page load.
  }
}
