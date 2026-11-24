// SPDX-License-Identifier: AGPL-3.0-or-later
// The app shell's service worker checks what it serves (offline mode design,
// docs/superpowers/specs/2026-11-22-offline-mode-design.md, §4.5; #311; ADR 0021). The build writes each kept file's
// SHA-256 into the worker (vite.config.ts); a kept file whose bytes differ is deleted and fetched again, never served,
// offline neither (web/sw/kept.ts, worker.ts). Any page of the origin can write the worker's cache: these tests do it
// from the page, as a script run in the origin would. Offline is the browser's own (context.setOffline).
//
// Promises in scope, and the test that covers each:
// 1. A kept file of /assets changed in CacheStorage is not served: online the worker deletes it and serves and keeps
//    the server's bytes again; changed again and then offline, the changed bytes are never served (the request fails,
//    or the browser's HTTP cache answers with the original ones) → "sw #1: a kept asset changed in the cache …".
// 2. The kept app page changed in CacheStorage, then a reload offline: the changed page is never shown (the start
//    fails rather than run it), where the unchanged one opened the app offline just before → "sw #2: the kept app page
//    changed …".
// Left out: a new build replacing the old cache (web/e2e/shell/shell.browser.ts, against a server of its own), and
// the worker's script itself (out of any page's reach by design: the browser keeps it).
// Chromium only (desktop): the check is the worker's own code, the same in every browser, and the run's spec
// (docs/superpowers/specs/2026-11-23-tests-0-7-0-design.md §1) leaves Firefox beyond @offline out.
import type { Page } from "@playwright/test";
import { createHash } from "node:crypto";
import { expect, shot, shotsDir070, test } from "../support/test";

test.use({ shotsDir: shotsDir070 });

const CHANGED = "changed by a page of the origin";

/** Waits until the worker controls the page and has kept its build; returns the cache's name and its kept paths. */
async function shell(page: Page): Promise<{ name: string; paths: string[] }> {
  await expect.poll(() => page.evaluate(() => navigator.serviceWorker.controller !== null), { message: "the worker controls the page", timeout: 30_000 }).toBe(true);
  let found: { name: string; paths: string[] } | undefined;
  await expect
    .poll(
      async () => {
        found = await page.evaluate(async () => {
          // The worker's prefix (web/sw/worker.ts); its activation leaves only this build's cache.
          const names = (await caches.keys()).filter((n) => n.startsWith("kumbu-shell-"));
          if (names.length !== 1) return undefined;
          const keys = await (await caches.open(names[0])).keys();
          return { name: names[0], paths: keys.map((k) => new URL(k.url).pathname) };
        });
        return found?.paths.includes("/index.html") ?? false;
      },
      { message: "one build's files are kept", timeout: 30_000 },
    )
    .toBe(true);
  return found!;
}

/** Writes `body` into the worker's cache for `path`, as any script of the origin can. */
const tamper = (page: Page, name: string, path: string, body: string, type: string) =>
  page.evaluate(
    async ({ name, path, body, type }) => (await caches.open(name)).put(path, new Response(body, { headers: { "Content-Type": type } })),
    { name, path, body, type },
  );

/** The SHA-256 of what the cache holds for `path` (null for nothing). */
const keptHash = (page: Page, name: string, path: string) =>
  page.evaluate(
    async ({ name, path }) => {
      const kept = await (await caches.open(name)).match(path);
      if (!kept) return null;
      const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", await kept.arrayBuffer()));
      return Array.from(digest, (b) => b.toString(16).padStart(2, "0")).join("");
    },
    { name, path },
  );

/** What the page gets for `path` through the worker: the SHA-256 of the bytes and whether they are the changed ones; null if the request fails. */
const served = (page: Page, path: string, changed: string) =>
  page.evaluate(
    async ({ path, changed }) => {
      try {
        const bytes = await (await fetch(path)).arrayBuffer();
        const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
        return { hash: Array.from(digest, (b) => b.toString(16).padStart(2, "0")).join(""), changed: new TextDecoder().decode(bytes) === changed };
      } catch {
        return null;
      }
    },
    { path, changed },
  );

test("sw #1: a kept asset changed in the cache is never served: online the server's bytes come back, offline the changed ones don't @sw", async ({ page }, testInfo) => {
  await page.goto("/");
  const { name, paths } = await shell(page);
  // The app's entry script: the file a script run in the origin would most want to replace.
  const path = (await (await page.request.get("/index.html")).text()).match(/<script[^>]* src="(\/assets\/[^"]+\.js)"/)?.[1];
  expect(path && paths.includes(path), "the entry script is kept").toBe(true);
  const original = createHash("sha256").update(await (await page.request.get(path!)).body()).digest("hex");
  expect(await keptHash(page, name, path!), "kept as the server has it").toBe(original);

  // Online: the changed entry is deleted, the server's bytes are served and kept again.
  await tamper(page, name, path!, CHANGED, "text/javascript");
  expect(await keptHash(page, name, path!), "the cache holds the changed bytes now").not.toBe(original);
  expect(await served(page, path!, CHANGED), "online, the server's bytes").toEqual({ hash: original, changed: false });
  expect(await keptHash(page, name, path!), "the server's bytes are kept again").toBe(original);
  await shot(page, testInfo, "sw");

  // Changed again, then offline: never the changed bytes (the request fails, or the HTTP cache has the original ones).
  await tamper(page, name, path!, CHANGED, "text/javascript");
  await page.context().setOffline(true);
  try {
    const offline = await served(page, path!, CHANGED);
    expect(offline?.changed ?? false, "offline, never the changed bytes").toBe(false);
    if (offline) expect(offline.hash, "offline, only the build's bytes").toBe(original);
    // And the changed entry is gone, so nothing serves it again.
    expect(await keptHash(page, name, path!), "the changed entry is deleted").not.toBe(createHash("sha256").update(CHANGED).digest("hex"));
  } finally {
    await page.context().setOffline(false);
  }
});

test("sw #2: the kept app page changed in the cache is never shown at an offline start @sw", async ({ page }) => {
  await page.goto("/");
  const { name } = await shell(page);

  // Unchanged, the kept page opens the app offline.
  await page.context().setOffline(true);
  try {
    await page.reload();
    await expect(page).toHaveTitle("Kumbu²");
    await expect(page.locator("#root")).not.toBeEmpty();
  } finally {
    await page.context().setOffline(false);
  }
  await page.reload();
  await shell(page);

  await tamper(page, name, "/index.html", `<!doctype html><title>Changed</title><p>${CHANGED}</p>`, "text/html");
  await page.context().setOffline(true);
  try {
    // The start fails rather than run the changed page. The page is served `no-cache`, so the HTTP cache can't answer
    // offline either: only the worker's kept page could, and it is deleted instead.
    const failed = await page.reload().then(
      () => false,
      () => true,
    );
    expect(failed, "offline, the start fails rather than run the changed page").toBe(true);
    // Chromium's own error page for a start that got no page.
    await expect(page).toHaveURL(/^chrome-error:/);
    const shown = await page.content();
    expect(shown, "offline, never the changed page").not.toContain(CHANGED);
    await expect(page).not.toHaveTitle("Changed");
  } finally {
    await page.context().setOffline(false);
  }
  // The changed page was deleted: the next online start keeps nothing of it.
  await page.goto("/");
  await expect(page).toHaveTitle("Kumbu²");
  // Pages come from the network first (worker.ts), so nothing online puts it back.
  expect(await keptHash(page, name, "/index.html"), "the changed page is deleted").toBeNull();
});
