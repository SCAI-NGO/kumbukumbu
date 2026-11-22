// SPDX-License-Identifier: AGPL-3.0-or-later
// The app shell's service worker (offline mode spec §3, §10): keeps one build's files, so the app opens without a
// connection, and nothing else. Built as /sw.js by vite.config.ts, which writes the build's list into it.
import { route } from "./shell";

declare const self: ServiceWorkerGlobalScope;

/** Written by the build: the cache's name, from the content of the files it lists, and those files. */
const shell = "__KUMBU_SHELL__" as unknown as { name: string; files: string[] };
const files = new Set(shell.files);
const prefix = "kumbu-shell-";

// A new build takes over at once, not when every tab is closed: pages come from the network first, so a tab still
// on the old build loses nothing it has, and its next reload is the new build (the update flow, web/src/update).
self.addEventListener("install", (event) => {
  event.waitUntil(
    caches
      .open(shell.name)
      // index.html is served `no-cache`, so this is the server's; the rest are named by their content.
      .then((cache) => cache.addAll(shell.files))
      .then(() => self.skipWaiting()),
  );
});

// Only one build is kept: the older ones go once this one is in place.
self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((names) => Promise.all(names.filter((n) => n.startsWith(prefix) && n !== shell.name).map((n) => caches.delete(n))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const { request } = event;
  switch (route(request, self.location.origin, files)) {
    case "asset":
      event.respondWith(caches.match(request, { cacheName: shell.name }).then((kept) => kept ?? fetch(request)));
      break;
    case "page":
      // The server's page whenever it answers, an error page included, so an online reload is always the server's
      // build; the kept one only when there is no network at all.
      event.respondWith(
        fetch(request).catch(async (error: unknown) => (await caches.match("/index.html", { cacheName: shell.name })) ?? Promise.reject(error)),
      );
      break;
  }
});
