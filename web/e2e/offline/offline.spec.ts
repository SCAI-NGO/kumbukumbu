// SPDX-License-Identifier: AGPL-3.0-or-later
// Offline mode end to end, across the server, the web app and the Eidolon core (offline mode design,
// docs/superpowers/specs/2026-11-22-offline-mode-design.md, §3, §3a, §4). Every test is a device of its own: a browser
// context with an empty IndexedDB, which turns offline on through Settings, Offline. "Offline" is the browser's own
// (context.setOffline): fetch fails as it does without a network, and the service worker serves the shell.
//
// Promises in scope, and the test that covers each:
// 1. Turning offline on keeps the vaults on this device; offline, after a reload, the sign-in form opens the copy with
//    the password, the tree and the notes read from it and a link leads to its note (§3a "Unlocking", §4.8)
//    → "offline: after a reload offline the password opens the copy …".
// 2. Offline, an edit, a new note and a trashed note go into the copy, the bar counts them, and they upload once back,
//    with "All changes uploaded" (§3, §4.9) → "offline: an edit, a new note and a trashed note upload when back …".
// 3. A note changed elsewhere meanwhile keeps both: the server's text, and this device's beside it as a conflict copy,
//    with the bar's conflicts line (§3a "The bar and the mark") → "offline: a note changed elsewhere meanwhile …".
// 4. An offline unlock ends like a session: idle, the copy locks, the editor's unsaved text goes into it first, and
//    the password opens it again (§3a "Unlocking") → "offline: idle, the copy locks …".
// 5. Signing out offline with changes waiting asks; the changes stay sealed, the session ends on the server once back,
//    and they upload at the next sign-in here (§3a "Sign in again, sign out") → "offline: signing out with changes waiting …".
// 6. Removing the device's copy elsewhere wipes it here: the sign-in page says so, and offline nothing opens any more
//    (§4.4) → "offline: removing this device's copy elsewhere wipes it here".
// 7. Attachments: a small one comes with the notes; a large one opens online only, until it was opened online once
//    (§6) → "offline: a large attachment opens offline once it was opened online".
// Each runs in Chromium (desktop) and Firefox (offline-firefox). Not in WebKit: it sends no Secure cookie to
// http://localhost, so the plain-HTTP stack can't sign it in; Safari waits for a hand test on a real device.
import type { Page } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { crc32, deflateSync } from "node:zlib";
import { dismissInstallHint, notePage } from "../calendar/helpers";
import { secrets, usernames } from "../support/env";
import { limitWindow, reservePassword } from "../support/session";
import { expect, test } from "../support/test";

// Failure screenshots would show the copy's notes only, but the sign-in form may hold the password.
test.use({ screenshot: "off" });

const bar = (page: Page) => page.getByRole("status", { name: "Offline" });
const source = (page: Page) => page.getByRole("textbox", { name: "Markdown source", exact: true });
const article = (page: Page) => page.locator("article");

/** The mirror slots this test's devices made: removed after it, as an account keeps at most 20. */
let made: string[] = [];

/** The slot ids of the copies in this browser's IndexedDB: their headers are its one record in the clear. */
function slotIds(page: Page): Promise<string[]> {
  return page.evaluate(
    () =>
      new Promise<string[]>((resolve, reject) => {
        const opening = indexedDB.open("kumbu-mirror");
        opening.onerror = () => reject(opening.error);
        opening.onsuccess = () => {
          const db = opening.result;
          if (!db.objectStoreNames.contains("records")) return resolve([]);
          const all = db.transaction("records").objectStore("records").getAll(IDBKeyRange.bound("h/", "h/\uffff"));
          all.onsuccess = () => resolve(all.result.map((v: ArrayBuffer) => (JSON.parse(new TextDecoder().decode(v)) as { slot: { id: string } }).slot.id));
          all.onerror = () => reject(all.error);
        };
      }),
  );
}

test.afterEach(async ({ aliceApi }) => {
  for (const id of made) await aliceApi.send("DELETE", `/api/account/mirror-slots/${id}`);
  made = [];
});

test.beforeEach(async ({ page }) => {
  await dismissInstallHint(page);
  // The invitation to turn offline on: answered, as these devices turn it on through Settings.
  const invite = page.getByRole("region", { name: "Keep your notes on this device" });
  await page.addLocatorHandler(invite, async () => {
    await invite.getByRole("button", { name: "Not now" }).click();
  });
});

/**
 * Turns offline on for this device through Settings, Offline, and waits until every note of Work is on it: the vault's
 * line says "N notes · … of attachments" once none is missing (it says "M of N notes" before, and "0 notes · …"
 * before the vault's list has come).
 */
async function turnOn(page: Page, user: "alice" | "bob" = "alice") {
  await page.goto("/settings/offline");
  await page.getByRole("switch", { name: "Keep my notes on this device" }).click();
  // The password check waits for the rate limit's window when other tests used it up.
  test.info().setTimeout(test.info().timeout + limitWindow);
  await reservePassword(user);
  await page.getByLabel("Your password").fill(secrets().passwords[user]);
  await page.getByRole("button", { name: "Keep them here" }).click();
  await expect(page.getByRole("switch", { name: "Keep my notes on this device" })).toBeChecked();
  made.push(...(await slotIds(page)));
  const work = page.getByRole("list", { name: "Vaults on this device" }).getByRole("listitem").filter({ hasText: /^Work/ });
  // Online a reload opens the copy again with the slot's key: the only way to see the line change.
  await expect
    .poll(
      async () => {
        await page.reload();
        return (await work.innerText().catch(() => "")).replace(/\s+/g, " ");
      },
      { message: "every note of Work is on this device", timeout: 30_000, intervals: [500, 1000, 2000] },
    )
    // Not "0 notes · …": that is a vault whose list hasn't come yet.
    .toMatch(/ [1-9][\d,]* notes · /);
}

/** The browser loses its network; the bar says the notes are here once the mode has switched. */
async function goOffline(page: Page, waiting?: number) {
  await page.context().setOffline(true);
  // A request has to fail first: the mode hears of it from the app's own calls and the socket.
  await expect(bar(page)).toBeVisible({ timeout: 30_000 });
  if (waiting === undefined) await expect(bar(page)).toContainText("You're offline.");
}

/** The sign-in form offline: the username finds the copy, the password opens it. */
async function unlock(page: Page, user: "alice" | "bob" = "alice") {
  await expect(page).toHaveURL(/\/signin/);
  await expect(page.getByText("You're offline. Signing in opens the notes kept on this device.")).toBeVisible();
  await page.getByLabel("Username").fill(usernames[user]);
  await page.getByLabel("Password", { exact: true }).fill(secrets().passwords[user]);
  await page.getByRole("button", { name: "Sign in" }).click();
  // Argon2id in a worker: a second or two.
  await expect(page).not.toHaveURL(/\/signin/, { timeout: 30_000 });
}

type Folder = { path: string; folders: Folder[]; notes: { path: string }[] };

/** The paths of the notes below `/Work/<folder>/`, as the server has them. */
async function notesIn(api: { json<T>(method: "GET", url: string): Promise<T> }, folder: string): Promise<string[]> {
  const tree = await api.json<{ root: Folder }>("GET", "/api/vaults/Work/tree");
  const all = (f: Folder): string[] => [...f.notes.map((n) => n.path), ...f.folders.flatMap(all)];
  return all(tree.root).filter((p) => p.includes(`/${folder}/`));
}

/** Write mode: replaces the text, then Done (which saves, into the copy while offline). */
async function write(page: Page, text: string) {
  await page.getByRole("button", { name: "Write" }).click();
  await expect(source(page)).toBeVisible();
  await source(page).fill(text);
  await page.getByRole("button", { name: "Done" }).click();
  await expect(page.getByRole("button", { name: "Done" })).toHaveCount(0);
}

test("offline: after a reload offline the password opens the copy, the tree and notes read from it, a link leads on @offline", async ({ page, aliceApi, folder }) => {
  await aliceApi.putNote(`/Work/${folder}/Plan.md`, "# Plan\n\nRead me offline. See [[Harbour]].\n");
  await aliceApi.putNote(`/Work/${folder}/Harbour.md`, "# Harbour\n\nThe lighthouse.\n");
  await turnOn(page);
  await page.goto(notePage(`/Work/${folder}/Plan.md`));
  await expect(article(page)).toContainText("Read me offline.");

  await goOffline(page);
  await page.reload();
  // The copy is sealed after a reload: the bar knows it is here before it opens.
  await expect(bar(page)).toContainText("You're offline. Your notes are here");
  await unlock(page);
  await expect(article(page)).toContainText("Read me offline.");
  await article(page).getByRole("link", { name: "Harbour" }).click();
  await expect(article(page)).toContainText("The lighthouse.");
  await expect(page).toHaveURL(/Harbour\.md$/);
});

test("offline: an edit, a new note and a trashed note upload when back, and the bar counts them @offline", async ({ page, aliceApi, folder }) => {
  const plan = `/Work/${folder}/Plan.md`;
  const old = `/Work/${folder}/Old.md`;
  await aliceApi.putNote(plan, "# Plan\n\nBefore. [[Old]]\n");
  await aliceApi.putNote(old, "# Old\n\nBye.\n");
  await turnOn(page);
  await page.goto(notePage(plan));
  await expect(article(page)).toContainText("Before.");

  await goOffline(page);
  await write(page, "# Plan\n\nWritten offline. [[Old]]\n");
  await expect(article(page)).toContainText("Written offline.");

  // A new note in the folder, through the dock's New menu.
  await page.getByRole("button", { name: "New", exact: true }).click();
  await page.getByRole("menuitem", { name: "New note" }).click();
  await expect(source(page)).toBeVisible();
  await source(page).fill("# Made offline\n\nNew here.\n");
  await page.getByRole("button", { name: "Done" }).click();
  await expect(page.getByRole("button", { name: "Done" })).toHaveCount(0);
  const created = decodeURIComponent(new URL(page.url()).pathname.replace(/^\/notes/, ""));
  expect(created).toContain(`/Work/${folder}/`);

  // Back to Plan by the browser's history, then on to Old by its link: a reload would lock the copy.
  await page.goBack();
  await expect(article(page)).toContainText("Written offline.");
  await article(page).getByRole("link", { name: "Old" }).click();
  await expect(article(page)).toContainText("Bye.");
  await page.getByRole("button", { name: "Note actions" }).click();
  await page.getByRole("menuitem", { name: "Delete note", exact: true }).click();
  await page.getByRole("dialog", { name: "Delete Old?" }).getByRole("button", { name: "Delete", exact: true }).click();
  await expect(bar(page)).toContainText("3 changes will upload when you're back.");

  await page.context().setOffline(false);
  await expect(page.getByRole("status", { name: "Uploaded" })).toContainText("All changes uploaded", { timeout: 30_000 });
  expect((await aliceApi.getNote(plan)).content).toBe("# Plan\n\nWritten offline. [[Old]]\n");
  expect((await aliceApi.getNote(created)).content).toBe("# Made offline\n\nNew here.\n");
  expect((await aliceApi.send("GET", `/api/notes/${old.slice(1).split("/").map(encodeURIComponent).join("/")}`)).status()).toBe(404);
});

test("offline: a note changed elsewhere meanwhile keeps both, this device's text beside it as a conflict copy @offline", async ({ page, aliceApi, folder }) => {
  const plan = `/Work/${folder}/Plan.md`;
  await aliceApi.putNote(plan, "# Plan\n\nBefore.\n");
  await turnOn(page);
  await page.goto(notePage(plan));
  await expect(article(page)).toContainText("Before.");

  await goOffline(page);
  await write(page, "# Plan\n\nMine, offline.\n");
  await aliceApi.putNote(plan, "# Plan\n\nTheirs, online.\n");

  await page.context().setOffline(false);
  await expect(page.getByRole("status", { name: "Changed elsewhere" })).toContainText("One note was also changed elsewhere", { timeout: 30_000 });
  expect((await aliceApi.getNote(plan)).content).toBe("# Plan\n\nTheirs, online.\n");
  const mine = (await notesIn(aliceApi, folder)).filter((p) => p !== plan && p.includes("Plan"));
  expect(mine, "a conflict copy beside the note").toHaveLength(1);
  expect((await aliceApi.getNote(mine[0])).content).toContain("Mine, offline.");
});

test("offline: idle, the copy locks, the unsaved text goes into it first, and the password opens it again @offline", async ({ page, aliceApi, folder }) => {
  const plan = `/Work/${folder}/Plan.md`;
  await aliceApi.putNote(plan, "# Plan\n\nBefore.\n");
  await turnOn(page);
  await page.goto(notePage(plan));
  await expect(article(page)).toContainText("Before.");
  await goOffline(page);

  await page.getByRole("button", { name: "Write" }).click();
  await source(page).fill("# Plan\n\nTyped, never saved by hand.\n");
  // The laptop sleeps past the copy's idle limit without "Keep me signed in" (30 minutes); waking up, the page looks
  // at the clock (§3a: no timer runs while it sleeps).
  await page.clock.install();
  await page.clock.fastForward("31:00");
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await unlock(page);
  await expect(article(page).or(source(page))).toContainText("Typed, never saved by hand.");

  await page.context().setOffline(false);
  await expect.poll(async () => (await aliceApi.getNote(plan)).content, { timeout: 30_000 }).toBe("# Plan\n\nTyped, never saved by hand.\n");
});

test("offline: signing out with changes waiting keeps them sealed; they upload at the next sign-in here @offline", async ({ freshSession, aliceApi, folder }) => {
  const { page } = await freshSession("alice");
  await dismissInstallHint(page);
  const plan = `/Work/${folder}/Plan.md`;
  await aliceApi.putNote(plan, "# Plan\n\nBefore.\n");
  await turnOn(page);
  await page.goto(notePage(plan));
  await expect(article(page)).toContainText("Before.");
  await goOffline(page);
  await write(page, "# Plan\n\nWaiting.\n");
  await expect(bar(page)).toContainText("1 change will upload when you're back.");

  await page.getByRole("button", { name: "Sign out" }).click();
  const dialog = page.getByRole("alertdialog", { name: "Sign out with changes waiting?" });
  await dialog.getByRole("button", { name: "Sign out", exact: true }).click();
  await expect(page).toHaveURL(/\/signin/);
  await expect(page.getByText("Your session ends on the server when this device is back online.")).toBeVisible();

  // Back online: the session that was open ends first, so nothing of it is left on the server.
  const cookies = await page.context().cookies();
  await page.context().setOffline(false);
  await page.reload();
  const replay = await page.context().browser()!.newContext({ storageState: { cookies, origins: [] } });
  await expect.poll(async () => (await replay.request.get("http://localhost:8080/api/session")).status(), { message: "the old session ended" }).toBe(401);
  await replay.close();
  expect((await aliceApi.getNote(plan)).content, "nothing uploaded without a session").toBe("# Plan\n\nBefore.\n");

  await page.getByLabel("Username").fill(usernames.alice);
  await page.getByLabel("Password", { exact: true }).fill(secrets().passwords.alice);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect.poll(async () => (await aliceApi.getNote(plan)).content, { timeout: 30_000 }).toBe("# Plan\n\nWaiting.\n");
});

test("offline: removing this device's copy elsewhere wipes it here @offline", async ({ freshSession, aliceApi, folder }) => {
  const { page } = await freshSession("alice");
  await dismissInstallHint(page);
  await aliceApi.putNote(`/Work/${folder}/Plan.md`, "# Plan\n");
  await turnOn(page);
  const slots = await page.request.get("/api/account/mirror-slots");
  const mine = ((await slots.json()) as { id: string; current: boolean }[]).find((s) => s.current);
  expect(mine, "this session's copy is listed as its own").toBeDefined();

  // From Alice's other session, as from Settings, Offline on another device.
  await reservePassword("alice");
  const removed = await aliceApi.send("POST", `/api/account/mirror-slots/${mine!.id}/remove`, { data: { password: secrets().passwords.alice } });
  expect(removed.ok(), `removed: ${removed.status()}`).toBe(true);
  await expect(page.getByText("The offline copy on this device was removed.")).toBeVisible({ timeout: 30_000 });

  // Nothing opens offline any more: the sign-in form finds no copy for Alice.
  await page.context().setOffline(true);
  await page.reload();
  await expect(bar(page)).toContainText("This device keeps no copy of your notes.");
  await page.getByLabel("Username").fill(usernames.alice);
  await page.getByLabel("Password", { exact: true }).fill(secrets().passwords.alice);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByText("this device keeps no notes of this user")).toBeVisible({ timeout: 30_000 });
});

/** A PNG of noise, `side` pixels square: random bytes don't compress, so its size is about side² × 3 bytes. */
function noisePng(side: number): Buffer {
  const chunk = (type: string, data: Buffer) => {
    const head = Buffer.alloc(8);
    head.writeUInt32BE(data.length, 0);
    head.write(type, 4, "ascii");
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])), 0);
    return Buffer.concat([head, data, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(side, 0);
  ihdr.writeUInt32BE(side, 4);
  ihdr.set([8, 2, 0, 0, 0], 8);
  const rows = Buffer.concat(Array.from({ length: side }, () => Buffer.concat([Buffer.from([0]), randomBytes(side * 3)])));
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", ihdr), chunk("IDAT", deflateSync(rows)), chunk("IEND", Buffer.alloc(0))]);
}

test("offline: a large attachment opens offline once it was opened online; a small one comes with the notes @offline", async ({ page, aliceApi, folder }) => {
  // The line between them is 1 MiB (SMALL_ATTACHMENT_BYTES).
  await aliceApi.putFile(`/Work/${folder}/small.png`, noisePng(100));
  await aliceApi.putFile(`/Work/${folder}/never.png`, noisePng(700));
  await aliceApi.putFile(`/Work/${folder}/opened.png`, noisePng(700));
  await aliceApi.putNote(`/Work/${folder}/Plan.md`, "# Plan\n\n![[small.png]]\n\n![[never.png]]\n\n![[opened.png]]\n");
  await turnOn(page);
  await page.goto(notePage(`/Work/${folder}/Plan.md`));
  // The image viewer: a page of its own (/view/…), reached and left within the app, as a reload would lock the copy.
  const viewer = (name: string) => page.getByRole("main", { name });
  const open = async (name: string) => {
    await article(page).getByRole("link", { name: `Open the image ${name}` }).click();
    await expect(viewer(name)).toBeVisible();
  };
  const close = async () => {
    await page.goBack();
    await expect(article(page)).toBeVisible();
  };
  // Opened online: kept on this device from now on, within the budget.
  await open("opened.png");
  await expect(viewer("opened.png").getByRole("img", { name: "opened.png" })).toBeVisible();
  await close();
  // The engine fetches it in the background: Settings, Offline counts it among the attachments.
  await expect
    .poll(
      async () => {
        await page.goto("/settings/offline");
        return (await page.getByRole("list", { name: "Vaults on this device" }).getByRole("listitem").filter({ hasText: /^Work/ }).innerText()).replace(/\s+/g, " ");
      },
      { message: "the opened attachment is on this device", timeout: 30_000, intervals: [1000, 2000] },
    )
    .toMatch(/· [1-9][\d.]* MB of attachments/);
  await page.goto(notePage(`/Work/${folder}/Plan.md`));
  await expect(article(page)).toBeVisible();

  await goOffline(page);
  await open("small.png");
  await expect(viewer("small.png").getByRole("img", { name: "small.png" })).toBeVisible();
  await close();
  await open("opened.png");
  await expect(viewer("opened.png").getByRole("img", { name: "opened.png" })).toBeVisible();
  await close();
  await open("never.png");
  await expect(viewer("never.png").getByRole("alert")).toHaveText("Opens when you're online.");
});
