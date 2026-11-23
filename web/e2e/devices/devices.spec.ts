// SPDX-License-Identifier: AGPL-3.0-or-later
// The devices with an offline copy, and who may keep one (offline mode design,
// docs/superpowers/specs/2026-11-22-offline-mode-design.md, §3a "Settings, Offline" item 6, §4.4, §4.6; #307, #311;
// ADRs 0019, 0020). Offline is the browser's own (context.setOffline), as in web/e2e/offline/offline.spec.ts.
//
// Promises in scope, and the test that covers each:
// 1. Settings, Offline lists the devices with a copy, this browser marked "This device"; Rename opens a dialog, the new
//    name shows, the server holds it, and the copy stays as it was (it still opens offline with the password)
//    → "devices #1: Rename …".
// 2. Remove offline copy on another device asks first, naming the changes it hadn't uploaded and that the copy goes
//    when that device next comes online, with the password as it had changes waiting; afterwards the device is gone
//    from the list and the server answers its next contact "wipe" → "devices #2: Remove offline copy …". The wipe in
//    that browser is offline.spec.ts test 6, not repeated here.
// 3. Deactivating a user deletes their copies: the admin's confirmation says so, their browser purges its copy at its
//    next request (the sign-in page says so; offline nothing opens), and reactivating doesn't bring it back (ADR 0019)
//    → "devices #3: deactivating a user …".
//    The same signed out (§4.4 "signed in or not"): a user who signed out before the deactivation; opening the sign-in
//    page asks after the copies kept here and removes theirs → "devices #5: signed out before the deactivation …".
// 4. Another user's copy in the same browser (ADR 0020, §4.11): a copy made by Alice, then Bob's session there. The main
//    protection, the client's: Bob's sign-in, the sign-in page's sweep and a reload never make a contact with Alice's
//    token, and don't wipe her copy, which still opens for her offline. The second line, the server's: that contact
//    (sent from the page with Bob's session, as the app never sends it) is refused with a 403 without a code, and the
//    slot stays listed → "devices #4: another user's copy …". No Vitest covers the client's handling of a 403 contact
//    (it never happens through the app), and this test can't either.
// Left out: the device list's "had N changes waiting" from a real browser's offline edits (the contact that reports
// them needs the browser online again, which uploads them first); #2 reports them through the contact route instead.
// Chromium only (desktop): the run's spec (docs/superpowers/specs/2026-11-23-tests-0-7-0-design.md §1) leaves Firefox
// beyond @offline out.
// The users made for #3 and #5 are deleted at the end (support/users.ts removeUser), not reactivated and left as the
// plan's Task 6 said: its review (point 12) gave the suite a cheap deletion, and a user left behind would show in
// Admin, Users for every later test and run.
// Each test's slots are deleted after it, as an account keeps at most 20; device-list assertions look at the test's
// own row only, so the tests run beside the others.
import type { Page } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { dismissInstallHint, notePage } from "../calendar/helpers";
import { baseURL, secrets, usernames } from "../support/env";
import { limitWindow, reserve, reservePassword, signIn } from "../support/session";
import { expect, shot, shotsDir070, test } from "../support/test";
import { invitedUser, removeUser, type TestUser } from "../support/users";

// Failure screenshots may show the sign-in form with a password typed.
test.use({ screenshot: "off", shotsDir: shotsDir070 });

const bar = (page: Page) => page.getByRole("status", { name: "Offline" });
const devices = (page: Page) => page.getByRole("list", { name: "Devices with a copy" }).getByRole("listitem");
const keep = (page: Page) => page.getByRole("switch", { name: "Keep my notes on this device" });

/** Who turns offline on and signs in: the password is never logged. */
type Who = { username: string; password: string; reserve: () => Promise<void> };
const alice = (): Who => ({ username: usernames.alice, password: secrets().passwords.alice, reserve: () => reservePassword("alice") });
const asUser = (u: TestUser): Who => ({ username: u.username, password: u.password, reserve: u.reservePassword });

/** The mirror slots this test made: removed after it, with Alice's password where they had changes waiting. */
let made: string[] = [];

test.afterEach(async ({ aliceApi }) => {
  for (const id of made) {
    const deleted = await aliceApi.send("DELETE", `/api/account/mirror-slots/${id}`);
    if (deleted.status() !== 400) continue;
    await reservePassword("alice");
    await aliceApi.send("POST", `/api/account/mirror-slots/${id}/remove`, { data: { password: secrets().passwords.alice } });
  }
  made = [];
});

/** The install hint and the invitation to turn offline on, answered on every page of the test's. */
async function prepare(page: Page) {
  await dismissInstallHint(page);
  const invite = page.getByRole("region", { name: "Keep your notes on this device" });
  await page.addLocatorHandler(invite, async () => {
    await invite.getByRole("button", { name: "Not now" }).click();
  });
}

test.beforeEach(async ({ page }) => {
  await prepare(page);
});

/** The headers of the copies in this browser's IndexedDB (their one record in the clear): the slot of each. */
function copies(page: Page): Promise<{ id: string; token: string }[]> {
  return page.evaluate(
    () =>
      new Promise<{ id: string; token: string }[]>((resolve, reject) => {
        const opening = indexedDB.open("kumbu-mirror");
        opening.onerror = () => reject(opening.error);
        opening.onsuccess = () => {
          const db = opening.result;
          if (!db.objectStoreNames.contains("records")) return resolve([]);
          const all = db.transaction("records").objectStore("records").getAll(IDBKeyRange.bound("h/", "h/￿"));
          all.onsuccess = () => resolve(all.result.map((v: ArrayBuffer) => (JSON.parse(new TextDecoder().decode(v)) as { slot: { id: string; token: string } }).slot));
          all.onerror = () => reject(all.error);
        };
      }),
  );
}
const slotIds = async (page: Page) => (await copies(page)).map((s) => s.id);

/**
 * Turns offline on through Settings, Offline, as offline.spec.ts does, and waits until every note of `vault` is on
 * this device ("N notes · …" in the vault's line). Returns the slot it made.
 */
async function turnOn(page: Page, who: Who, vault = "Work"): Promise<string> {
  const before = await page.goto("/settings/offline").then(() => slotIds(page));
  await keep(page).click();
  test.info().setTimeout(test.info().timeout + limitWindow);
  await who.reserve();
  await page.getByLabel("Your password").fill(who.password);
  await page.getByRole("button", { name: "Keep them here" }).click();
  await expect(keep(page)).toBeChecked();
  const mine = (await slotIds(page)).filter((id) => !before.includes(id));
  made.push(...mine);
  expect(mine, "one copy made on this device").toHaveLength(1);
  const line = page.getByRole("list", { name: "Vaults on this device" }).getByRole("listitem").filter({ hasText: new RegExp(`^${vault}`) });
  await expect
    .poll(
      async () => {
        await page.reload();
        return (await line.innerText().catch(() => "")).replace(/\s+/g, " ");
      },
      { message: `every note of ${vault} is on this device`, timeout: 30_000, intervals: [500, 1000, 2000] },
    )
    .toMatch(/ [1-9][\d,]* notes? · /);
  return mine[0];
}

/** Offline, after a reload of `at` (opened online first) or of the page as it is: the sign-in form, filled in. */
async function signInOffline(page: Page, who: Who, at?: string) {
  if (at) await page.goto(at);
  await page.context().setOffline(true);
  await page.reload();
  await expect(page).toHaveURL(/\/signin/);
  await expect(bar(page)).toBeVisible({ timeout: 30_000 });
  await page.getByLabel("Username").fill(who.username);
  await page.getByLabel("Password", { exact: true }).fill(who.password);
  await page.getByRole("button", { name: "Sign in" }).click();
}

type Slot = { id: string; label: string; waiting: number | null; contactAt: string | null; current: boolean };

test("devices #1: Rename gives this device a new name in the list and on the server, and the copy stays @devices", async ({ freshSession, aliceApi, folder }, testInfo) => {
  // A session of its own: "This device" is the slot this session made, so the row is this test's alone.
  const { page } = await freshSession("alice");
  await prepare(page);
  await aliceApi.putNote(`/Work/${folder}/Plan.md`, "# Plan\n\nStill here after the rename.\n");
  const mine = await turnOn(page, alice());

  const row = devices(page).filter({ hasText: "This device" });
  await expect(row).toHaveCount(1);
  const label = (await aliceApi.json<Slot[]>("GET", "/api/account/mirror-slots")).find((s) => s.id === mine)!.label;
  await expect(row).toContainText(label);
  await row.getByRole("button", { name: `Rename ${label}` }).click();
  const dialog = page.getByRole("dialog", { name: `Rename ${label}` });
  const name = dialog.getByLabel("New name");
  await expect(name).toHaveValue(label);
  // Unchanged, nothing to rename.
  await expect(dialog.getByRole("button", { name: "Rename" })).toBeDisabled();
  const renamed = `Desk ${randomBytes(3).toString("hex")}`;
  await name.fill(renamed);
  await dialog.getByRole("button", { name: "Rename" }).click();
  await expect(dialog).toHaveCount(0);
  await expect(row.getByText(renamed, { exact: true })).toBeVisible();
  await shot(page, testInfo, "devices");

  // The server holds the name, read back through another session.
  const slot = (await aliceApi.json<Slot[]>("GET", "/api/account/mirror-slots")).find((s) => s.id === mine);
  expect(slot?.label, "the server holds the new name").toBe(renamed);
  await page.reload();
  await expect(devices(page).filter({ hasText: "This device" }).getByText(renamed, { exact: true })).toBeVisible();

  // The copy is the same one, and still opens offline.
  expect(await slotIds(page)).toEqual([mine]);
  await signInOffline(page, alice(), notePage(`/Work/${folder}/Plan.md`));
  await expect(page).not.toHaveURL(/\/signin/, { timeout: 30_000 });
  await expect(page.locator("article")).toContainText("Still here after the rename.");
  await page.context().setOffline(false);
});

test("devices #2: Remove offline copy names the changes waiting, takes the password, and the device leaves the list @devices", async ({ freshSession, aliceApi }) => {
  // Another device, as the server sees one: a slot made by Alice's other session, whose contact reported 2 changes.
  const label = `Laptop ${randomBytes(3).toString("hex")}`;
  const created = await aliceApi.json<{ id: string; token: string }>("POST", "/api/account/mirror-slots", { label });
  made.push(created.id);
  await aliceApi.json("POST", "/api/account/mirror-slots/contact", { token: created.token, waiting: 2 });

  const { page } = await freshSession("alice");
  await prepare(page);
  await page.goto("/settings/offline");
  const row = devices(page).filter({ hasText: label });
  await expect(row).toHaveCount(1);
  await expect(row).toContainText(/last online .* · had 2 changes waiting/);
  await expect(row).not.toContainText("This device");
  await row.getByRole("button", { name: "Remove offline copy" }).click();

  const dialog = page.getByRole("alertdialog", { name: `Remove the copy on ${label}?` });
  await expect(dialog.locator("p").first()).toHaveText(
    "2 changes on that device haven't uploaded and will be lost. The copy goes when that device next comes online. Enter your password to remove it.",
  );
  // Without the password, nothing to confirm.
  await expect(dialog.getByRole("button", { name: "Remove" })).toBeDisabled();
  await reservePassword("alice");
  await dialog.getByLabel("Your password").fill(secrets().passwords.alice);
  await dialog.getByRole("button", { name: "Remove" }).click();
  await expect(dialog).toHaveCount(0);
  await expect(row).toHaveCount(0);

  // The server agrees, and tells that device to wipe its copy at its next contact.
  expect((await aliceApi.json<Slot[]>("GET", "/api/account/mirror-slots")).map((s) => s.id), "the slot is gone on the server").not.toContain(created.id);
  const contact = await aliceApi.send("POST", "/api/account/mirror-slots/contact", { data: { token: created.token, waiting: 0 } });
  expect(contact.status(), "that device's next contact").toBe(404);
  expect(((await contact.json()) as { code?: string }).code).toBe("mirror-wipe");
});

/** Admin, Users: Alice (de)activates `user` through the row's button, its typed confirmation, and her password if asked. */
async function toggle(page: Page, user: TestUser, action: "Deactivate" | "Reactivate") {
  await page.goto("/admin/users");
  await page.getByLabel("Filter by name").fill(user.username);
  const row = page.getByRole("listitem").filter({ hasText: user.username });
  await expect(row).toHaveCount(1);
  await row.getByRole("button", { name: new RegExp(`^${action} `) }).click();
  const dialog = page.getByRole("alertdialog", { name: new RegExp(`^${action} .*\\?$`) });
  if (action === "Deactivate") {
    // ADR 0019: the confirmation says what goes and that reactivating doesn't bring it back.
    await expect(dialog).toContainText(
      "Offline copies on their devices are deleted when those devices next reach Kumbu², with any changes not uploaded yet; reactivating doesn't bring them back.",
    );
  }
  await dialog.getByLabel(`Type ${user.username} to confirm`).fill(user.username);
  await dialog.getByRole("button", { name: action, exact: true }).click();
  // The password window of Alice's shared session may have closed: then the app asks for her password.
  const prompt = page.getByRole("alertdialog", { name: "Confirm your password" });
  const done = row.getByText(action === "Deactivate" ? "Deactivated" : "Active", { exact: true });
  await expect(prompt.or(done)).toBeVisible();
  if (await prompt.isVisible()) {
    await reservePassword("alice");
    await prompt.getByLabel("Your password").fill(secrets().passwords.alice);
    await prompt.getByRole("button", { name: "Confirm" }).click();
  }
  await expect(done).toBeVisible();
}

test("devices #3: deactivating a user wipes their copy at their next request, and reactivating doesn't bring it back @devices", async ({ browser, projectContextOptions, page }) => {
  test.setTimeout(240_000);
  // Password checks: the user's 1 (turning offline on) and 1 sign-in, each on its own ledger; Alice's up to 3
  // (deactivate and reactivate if her window has closed, the deletion).
  const user = await invitedUser(browser, projectContextOptions, "e2e-devices");
  try {
    const who = asUser(user);
    await prepare(user.page);
    // A vault of their own with a note: a copy needs something to keep.
    const send = (method: string, url: string, data?: unknown) => user.context.request.fetch(url, { method, data, headers: { Origin: baseURL } });
    expect((await send("POST", "/api/vaults", { name: "Notes" })).ok(), "the user's vault").toBe(true);
    expect((await send("PUT", "/api/notes/Notes/Plan.md", { content: "# Plan\n\nMine.\n" })).ok(), "the user's note").toBe(true);
    const mine = await turnOn(user.page, who, "Notes");
    expect(await slotIds(user.page)).toEqual([mine]);
    made = made.filter((id) => id !== mine); // the user's, not Alice's: their deletion removes it if the test fails

    await toggle(page, user, "Deactivate");

    // Their next request is the tab's own: the socket's, which closed with their sessions, asks for a ticket again
    // with the copy's token, and is answered "wipe". The copy goes, and the sign-in page says so.
    await expect(user.page).toHaveURL(/\/signin/, { timeout: 30_000 });
    await expect(user.page.getByText("The offline copy on this device was removed.")).toBeVisible();
    // The message shows before the IndexedDB wipe has finished (mode.ts wipeCopy).
    await expect.poll(() => slotIds(user.page), { message: "no copy left in this browser" }).toEqual([]);

    // Offline nothing opens: the form finds no copy of theirs.
    await signInOffline(user.page, who);
    await expect(bar(user.page)).toContainText("This device keeps no copy of your notes.");
    await expect(user.page.getByText("this device keeps no notes of this user")).toBeVisible({ timeout: 30_000 });
    await expect(user.page).toHaveURL(/\/signin/);
    await user.context.setOffline(false);

    await toggle(page, user, "Reactivate");

    // Signed in again: no copy came back, on this device or on the server.
    await reserve(`sign-in-${user.username}`);
    await user.page.goto("/signin");
    await user.page.getByLabel("Username").fill(user.username);
    await user.page.getByLabel("Password", { exact: true }).fill(user.password);
    await user.page.getByRole("button", { name: "Sign in" }).click();
    await expect(user.page).not.toHaveURL(/\/signin/);
    await user.page.goto("/settings/offline");
    await expect(user.page.getByText("No device keeps a copy of your notes.")).toBeVisible();
    await expect(keep(user.page)).not.toBeChecked();
    expect(await slotIds(user.page)).toEqual([]);
    const slots = (await (await user.context.request.get("/api/account/mirror-slots")).json()) as Slot[];
    expect(slots, "no slot on the server after the reactivation").toEqual([]);
  } finally {
    await remove(user);
  }
});

/** The test user's context closed and the user deleted; a failed deletion is noted, never in place of the test's own error. */
async function remove(user: TestUser) {
  await user.page.goto("about:blank").catch(() => undefined);
  await user.context.close();
  try {
    await removeUser(user.username);
  } catch (error) {
    test.info().annotations.push({ type: "cleanup", description: `the test user wasn't deleted: ${(error as Error).message}` });
  }
}

test("devices #5: signed out before the deactivation, the sign-in page removes the copy @devices", async ({ browser, projectContextOptions, page }) => {
  test.setTimeout(240_000);
  // Password checks: the user's 1 (turning offline on); Alice's up to 2 (deactivate if her window has closed, the deletion).
  const user = await invitedUser(browser, projectContextOptions, "e2e-devices");
  try {
    const who = asUser(user);
    await prepare(user.page);
    const send = (method: string, url: string, data?: unknown) => user.context.request.fetch(url, { method, data, headers: { Origin: baseURL } });
    expect((await send("POST", "/api/vaults", { name: "Notes" })).ok(), "the user's vault").toBe(true);
    expect((await send("PUT", "/api/notes/Notes/Plan.md", { content: "# Plan\n\nMine.\n" })).ok(), "the user's note").toBe(true);
    const mine = await turnOn(user.page, who, "Notes");
    made = made.filter((id) => id !== mine);

    // Signed out online with nothing waiting: the copy stays here, locked.
    await user.page.getByRole("button", { name: "Sign out" }).click();
    await expect(user.page).toHaveURL(/\/signin/);
    expect(await slotIds(user.page), "signing out keeps the copy").toEqual([mine]);
    await user.page.goto("about:blank");

    await toggle(page, user, "Deactivate");

    // Back at the sign-in page: it asks after each copy kept here with its token, and removes theirs.
    await user.page.goto("/signin");
    await expect.poll(() => slotIds(user.page), { message: "the sign-in page removed the copy", timeout: 30_000 }).toEqual([]);
    await signInOffline(user.page, who);
    await expect(bar(user.page)).toContainText("This device keeps no copy of your notes.");
    await expect(user.page.getByText("this device keeps no notes of this user")).toBeVisible({ timeout: 30_000 });
    await expect(user.page).toHaveURL(/\/signin/);
    await user.context.setOffline(false);
  } finally {
    await remove(user);
  }
});

test("devices #4: another user's copy: Bob's session never contacts with its token and doesn't wipe it; the server refuses that contact with a 403 @devices", async ({ freshSession, aliceApi, folder }) => {
  const { page } = await freshSession("alice");
  await prepare(page);
  await aliceApi.putNote(`/Work/${folder}/Plan.md`, "# Plan\n\nAlice's, kept.\n");
  const mine = await turnOn(page, alice());
  const slot = (await copies(page)).find((s) => s.id === mine)!;

  // Alice signs out; Bob signs in in the same browser.
  await page.goto(notePage(`/Work/${folder}/Plan.md`));
  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page).toHaveURL(/\/signin/);
  // Every contact from here to Bob's reload: none may hand in Alice's token (§4.11, the client's protection).
  const contacts: string[] = [];
  const listen = (request: import("@playwright/test").Request) => {
    if (request.method() === "POST" && request.url().endsWith("/api/account/mirror-slots/contact")) contacts.push(request.postData() ?? "");
  };
  page.on("request", listen);
  test.info().setTimeout(test.info().timeout + limitWindow);
  await signIn(page, "bob");
  expect(await slotIds(page), "Bob's sign-in sets Alice's copy aside, it doesn't wipe it").toContain(mine);

  // Bob's next page load keeps it too, and the app made no contact with her token meanwhile.
  await page.reload();
  await expect(page).not.toHaveURL(/\/signin/);
  expect(await slotIds(page), "Alice's copy stays in this browser").toContain(mine);
  page.off("request", listen);
  expect(contacts.filter((body) => body.includes(slot.token)), "Bob's session made no contact with Alice's token").toEqual([]);

  // The server's second line: the contact the app never makes for Bob, sent from the page with his session, is
  // refused without a wipe code, and her slot stays listed.
  const answer = await page.evaluate(async (token) => {
    const response = await fetch("/api/account/mirror-slots/contact", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ token }) });
    return { status: response.status, code: ((await response.json().catch(() => ({}))) as { code?: string }).code ?? null };
  }, slot.token);
  expect(answer, "another user's token: 403 without a code").toEqual({ status: 403, code: null });
  expect((await aliceApi.json<Slot[]>("GET", "/api/account/mirror-slots")).map((s) => s.id), "Alice's slot stays").toContain(mine);

  // And it still opens for Alice, offline, with her password.
  await signInOffline(page, alice(), notePage(`/Work/${folder}/Plan.md`));
  await expect(page).not.toHaveURL(/\/signin/, { timeout: 30_000 });
  await expect(page.locator("article")).toContainText("Alice's, kept.");
  await page.context().setOffline(false);
});
