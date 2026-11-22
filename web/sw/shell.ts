// SPDX-License-Identifier: AGPL-3.0-or-later
// What the app shell's service worker keeps and answers (offline mode spec §3, §4.5): the app's own built files,
// never a note, an attachment or an API answer. Shared by the worker (sw/worker.ts) and the build that lists its
// files (vite.config.ts), so it uses nothing of the DOM or of Node.

/**
 * The files to keep, from the build's file names and the public folder's: the app's page and everything under
 * /assets. Not the reader and skill pages (s.html, k.html), which strangers open and which are never cached, nor the
 * worker itself. Not the .woff and .ttf fonts: every browser with service workers takes the .woff2 the CSS names first.
 */
export function precache(built: readonly string[], publicFiles: readonly string[]): string[] {
  const assets = built.filter((name) => name.startsWith("assets/") && !/\.(woff|ttf)$/.test(name));
  return ["/index.html", ...assets.map((name) => `/${name}`), ...publicFiles.map((name) => `/${name}`)];
}

/** What the worker does with a request: answer it from the cache, try the network for a page first, or stay out. */
export type Route = "asset" | "page" | null;

// Paths the server answers itself, never with the app's page: a navigation there (an attachment opened in a tab of
// its own, an MCP client's sign-in, the reader page) goes to the server untouched.
const serverPaths = /^\/(api|mcp|oauth|\.well-known|dav|healthz|s|k|sw\.js)(\/|\.|\?|$)/;

/**
 * Only GETs of this origin. A kept file comes from the cache; a page of the app (a navigation to any other path,
 * which the server answers with index.html) from the network first. Everything else, the API above all, the worker
 * never touches, so its answers stay `no-store` and nothing of a note is kept by it.
 */
export function route(request: { url: string; mode: RequestMode; method: string }, origin: string, shell: ReadonlySet<string>): Route {
  if (request.method !== "GET") return null;
  const url = new URL(request.url);
  if (url.origin !== origin || serverPaths.test(url.pathname)) return null;
  if (request.mode === "navigate") return "page";
  return url.pathname !== "/index.html" && shell.has(url.pathname) ? "asset" : null;
}
