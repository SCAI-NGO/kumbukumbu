// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline mode's steps that web/e2e/offline/offline.spec.ts and web/e2e/devices/devices.spec.ts leave open (plan
// 2026-11-23-tests-0-7-0 Task 7; offline mode design docs/superpowers/specs/2026-11-22-offline-mode-design.md §3, §3a,
// §4; the graph's offline line, graph out of preview design §3; Similar notes' offline line, link prediction design
// §3.7). Every test is a device of its own, as in offline.spec.ts: a context with an empty IndexedDB that turns offline
// on through Settings, Offline; "offline" is the browser's own (context.setOffline). The mirror slots each test makes
// are deleted after it (an account keeps at most 20, and Alice's device list is shared); assertions look at the test's
// own rows only.
//
// Every step of §3, §3a and §4, and where it is tested ("offline.spec #n", "devices #n", "gaps #n" here; Vitest in
// web/src and packages/eidolon-core/test, xUnit in tests/Kumbu.Server.Tests):
// §3 Shape
// - Online reads from the server, offline from the copy → offline.spec #1; Vitest source.test.tsx "the tree, a note
//   and its links come from the copy, never from the server".
// - The mode: detected from failed requests and the socket, always visible, "N changes waiting" until flushed
//   → offline.spec #2; Vitest mode.test.tsx "a request that gets no answer goes offline …", core mode.test.ts.
// - Works offline: tree, reading, wiki links, embeds → offline.spec #1; words search across mirrored vaults → gaps #7
//   (decided by Chris on 2026-11-25 for 0.7.0); core search.test.ts, Vitest useSearch.test.tsx "useSearch offline";
//   backlinks and tags → core links tests; editing, new notes, trashing → offline.spec #2; images and PDFs in the mirror → offline.spec #7.
// - Needs a connection (shown, not silently failing): the graph → gaps #6; Similar notes → gaps #6, in the graph's
//   words (link prediction §2, §3.7, as Chris decided on 2026-11-24: one way for everything that needs a connection); menus (history, blame, move, rename,
//   duplicate) → gaps #2; search by meaning, console, calendar, board, settings pages → left out (not among Task 7's
//   gaps; the pane's sentence is the graph's, asserted in gaps #6); sharing, publishing, keys, admin → left out: the
//   same rule as gaps #2 (F-05: an item that needs a connection declares it, the menu dims it), so each would repeat it.
// §3a Turning it on
// - Off by default, invited once (Keep me signed in or installed) → gaps #11; Vitest OfflineInvite.test.tsx (both
//   tests); the rest of the suite answers the invitation "Not now" (its own devices turn it on through Settings).
// - Easy to reach: Settings, Offline; the bar leads there → Vitest mode.test.tsx "offline it leads to Settings,
//   Offline, and can't be put away".
// - Size before choosing, progress while it comes, inventory first, an interrupted download resumes → the vault's
//   line ("N notes · …") in every turnOn here; inventory order: core store.mirror.test.ts "downloads the inventory
//   first, then every note's text, then the small attachments"; an interrupted download resumes → gaps #8 (the network
//   cut after the third text, by routing the texts).
// §3a Unlocking
// - The sign-in form opens the copy offline, its line above, Keep me signed in hidden → offline.spec #1; Vitest
//   unlock.test.tsx "says what signing in does offline, and hides Keep me signed in"; a wrong password refused
//   plainly, without a lockout → gaps #9.
// - An offline unlock ends like a session (idle limit) → offline.spec #4; Vitest life.test.tsx.
// §3a The bar and the mark
// - The bar's states: nothing waiting → offline.spec #1, gaps #5; changes waiting → offline.spec #2; no copy →
//   offline.spec #6; uploading, done → offline.spec #2; session ended → Vitest mode.test.tsx "a 401 while uploading
//   changes keeps the place …"; conflicts → offline.spec #3.
// - The sidebar's mark → left out (not among Task 7's gaps; no role or label tells it from the vault name).
// - No flicker, 15 seconds a mode → core mode.test.ts "a mode holds for 15 seconds …" (a clock the page can't hold).
// - Conflicts stay until seen → offline.spec #3; Vitest mode.test.tsx "conflicts stay until dismissed".
// - Every tab the same → gaps #10 (the bar and its count in a second tab, the change uploaded once); core
//   mode.test.ts "every tab shows the same mode and count …".
// §3a "Needs a connection"
// - Dimmed, not hidden, "Needs a connection" as title and as a menu item's second line → gaps #2.
// - A page reached by address or left open says one sentence in its pane → gaps #6 (the graph's); the console's,
//   history's, blame's, calendar's, board's and Settings pages' → left out (not among Task 7's gaps).
// - Search says what it searches: the notice line, Hybrid and Meaning dimmed, the mode kept → gaps #7; Vitest
//   ModeChips.test.tsx; "Not searched: Archive (not kept on this device)" → left out (backlog).
// - A vault not kept here: dimmed in the switcher → left out (gaps #1 asserts its notes aren't on the device); an
//   attachment not fetched: "Opens when you're online" → offline.spec #7, gaps #1.
// §3a Settings, Offline
// - 1 the switch → offline.spec #1; 2 All my vaults and each vault → gaps #1 ("All my vaults", "a vault"); discreet
//   vaults never appear → gaps #4; 3 the attachments' budget → Vitest Offline.test.tsx "chooses vaults one by one once
//   All my vaults is off, and keeps the budget", core store.choice.test.ts "lets the least recently opened go first …";
//   4 Kept offline, set from the tree's folder menu → gaps #1 ("a folder"); 5 Storage → left out (the browser's
//   numbers; Safari's line can't run here, no WebKit); 6 devices with a copy, Rename, Remove → devices #1, #2;
//   7 remove the copy from this device → Vitest Offline.test.tsx "removes the copy from this device …".
// - Security's texts on copies → Vitest Security.test.tsx (re-encrypt, sign out other sessions); left out here.
// §3a Sign in again, sign out
// - Sign in again over the app → Vitest mode.test.tsx "a 401 while uploading changes keeps the place and asks to sign
//   in over the app"; sign-out with changes waiting, and pending until online → offline.spec #5; Vitest unlock.test.tsx
//   "keeps a mark until the server could be told", life.test.tsx "a sign-out made offline reaches the server …".
// §3a Purges and discreet vaults
// - A purge names no vault → gaps #3 (Bob online while he loses the vault: the socket's mirror frame and its contact);
//   the reconnect path (Bob offline while he loses it, back after) → left out, unclear: tried once, no notice either,
//   and nothing in the page tells which contact purged; with changes lost → Vitest mode.test.tsx "a purge names no vault"; a whole wipe
//   → offline.spec #6, devices #3.
// - A discreet note open when the connection drops → Vitest discreet/offlineEnd.test.tsx; left out here (the hold of
//   discreet text in memory only can't be read from the page).
// §4 Security
// - 4.1 everything sealed, only the header in the clear → core store.mirror.test.ts "keeps nothing in the clear but its
//   header …"; 4.2 the mirror key, Argon2id, wrapped twice → core store.mirror.test.ts, Vitest argon2.test.ts,
//   store.test.ts; offline unlock with the password → offline.spec #1.
// - 4.3 discreet vaults never mirrored → gaps #4 (shown with the PIN while offline is turned on); xUnit
//   MirrorSlotsTests.Discreet_vaults_are_never_listed_revealed_or_not; core store.mirror.test.ts "never mirrors a vault
//   the server didn't list …".
// - 4.4 wipe and revocation: vault access lost → gaps #3; vault marked discreet → xUnit
//   MirrorSlotsTests.Discreet_vaults_are_never_listed_revealed_or_not and core store.mirror.test.ts "purges a vault no
//   longer listed …" (left out of the browser: it would mark a second vault discreet with the PIN, beyond gaps #4's
//   two entries, and the purge's path is gaps #3's); slot deleted → offline.spec #6, devices #2; user deactivated →
//   devices #3; account deleted → xUnit MirrorSlotsTests.Deleting_the_account_removes_its_slots_and_the_401_says_wipe
//   (left out of the browser: Alice and Bob can't be deleted, and a user made for it is devices #3's way); restoring a
//   backup → left out (it restores the stack's database, which a test can't do beside the others; no xUnit test found
//   either); re-encryption, end other sessions → xUnit MirrorSlotsTests (Reencrypting_…, Ending_other_sessions_…);
//   removal with changes waiting takes the password → devices #2.
// - 4.5 the security architecture's changes: its document's rows are prose, not tested; the service worker that checks
//   what it serves → web/e2e/sw/sw.spec.ts.
// - 4.6 the slot API → xUnit MirrorSlotsTests; another user's token → devices #4; the 20 slots → xUnit
//   Slots_are_capped.
// - 4.7 one writer across tabs, records named by HMAC → core store tests; left out of the browser.
// - 4.8 reading the copy → offline.spec #1, #7; "Not on this device yet" → Vitest source.test.tsx.
// - 4.9 offline writing → offline.spec #2, #3; read-only members refused in the core → gaps #5; core
//   store.writing.test.ts "a read-only member writes nothing, not even into the copy".
// - 4.10 the vault choice and Keep offline → gaps #1; the budget → core store.choice.test.ts; attachments opened
//   online → offline.spec #7.
// - 4.11 the mode, the contacts, the copy's life with the session → offline.spec #4, #5, #6; Vitest life.test.tsx.
// - 4.12 the wiring → every test here.
//
// The new tests, gaps #1 to #11, in Chromium (desktop) and Firefox (offline-firefox), except #4: it shows Home with
// the PIN in a session of its own, so it is @serial (plan review point 2: offline-firefox runs in the parallel phase
// and leaves @serial out), Chromium only. It also covers Task 8's fifth place, the offline copy's vault list. #8 to
// #11 take steps off the 0.7.0 hand-test list: A4, A12, A6 with A10, and A1.
import type { Page } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { crc32, deflateSync } from "node:zlib";
import { dismissInstallHint, notePage } from "../calendar/helpers";
import type { Api } from "../support/api";
import { secrets, usernames, type User } from "../support/env";
import { endHidden, hideDiscreet, limitWindow, reserve, reservePassword, revealDiscreet } from "../support/session";
import { shareWithBob, unshare } from "../support/share";
import { expect, shot, shotsDir070, test } from "../support/test";

// Failure screenshots may show the sign-in form with the password typed; the one screenshot is taken explicitly.
test.use({ screenshot: "off", shotsDir: shotsDir070 });

const bar = (page: Page) => page.getByRole("status", { name: "Offline" });
const article = (page: Page) => page.locator("article");
const keep = (page: Page) => page.getByRole("switch", { name: "Keep my notes on this device" });
const vaultLine = (page: Page, vault: string) =>
  page.getByRole("list", { name: "Vaults on this device" }).getByRole("listitem").filter({ hasText: new RegExp(`^${vault}`) });

/** The mirror slots this test's devices made, with whose they are: removed after it. */
let made: { user: User; id: string }[] = [];

test.afterEach(async ({ aliceApi, bobApi }) => {
  test.setTimeout(240_000);
  for (const { user, id } of made) {
    const api = user === "alice" ? aliceApi : bobApi;
    const deleted = await api.send("DELETE", `/api/account/mirror-slots/${id}`);
    // Gone already (a purge or a wipe of the test's own) is as good as deleted.
    if (deleted.status() !== 400) {
      expect([204, 404], `the slot deleted: ${deleted.status()}`).toContain(deleted.status());
      continue;
    }
    // A slot whose changes waiting aren't counted yet takes the password (§4.4).
    await reservePassword(user);
    const removed = await api.send("POST", `/api/account/mirror-slots/${id}/remove`, { data: { password: secrets().passwords[user] } });
    expect(removed.ok(), `the slot removed with the password: ${removed.status()}`).toBe(true);
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

/**
 * Turns offline on through Settings, Offline, as offline.spec.ts does, and waits until every note of `vault` is on
 * this device ("N notes · …" in its line). Returns the slot it made.
 */
async function turnOn(page: Page, user: User = "alice", vault = "Work"): Promise<{ id: string; token: string }> {
  await page.goto("/settings/offline");
  const before = (await copies(page)).map((s) => s.id);
  await keep(page).click();
  test.info().setTimeout(test.info().timeout + limitWindow);
  await reservePassword(user);
  await page.getByLabel("Your password").fill(secrets().passwords[user]);
  await page.getByRole("button", { name: "Keep them here" }).click();
  // Recorded for the clean-up as soon as the copy is here, before anything else is asserted.
  let mine: { id: string; token: string }[] = [];
  try {
    await expect
      .poll(async () => (mine = (await copies(page)).filter((s) => !before.includes(s.id))).length, { message: "a copy made on this device", timeout: 30_000 })
      .toBeGreaterThan(0);
  } finally {
    made.push(...mine.map((s) => ({ user, id: s.id })));
  }
  expect(mine, "one copy made on this device").toHaveLength(1);
  await expect(keep(page)).toBeChecked();
  await untilHeld(page, vault);
  return mine[0];
}

/** Online a reload opens the copy again with the slot's key: the only way to see a vault's line change. */
async function untilHeld(page: Page, vault: string) {
  await expect
    .poll(
      async () => {
        await page.reload();
        return (await vaultLine(page, vault).innerText().catch(() => "")).replace(/\s+/g, " ");
      },
      { message: `every note of ${vault} is on this device`, timeout: 30_000, intervals: [500, 1000, 2000] },
    )
    // Not "0 notes · …": that is a vault whose list hasn't come yet.
    .toMatch(/ [1-9][\d,]* notes? · /);
}

/** The browser loses its network; the bar says so once the mode has switched. */
async function goOffline(page: Page) {
  await page.context().setOffline(true);
  await expect(bar(page)).toBeVisible({ timeout: 30_000 });
}

/** Offline, `at` loaded again (the service worker serves the shell): the sign-in form opens the copy. */
async function unlockAt(page: Page, at: string, user: User = "alice") {
  await page.goto(at);
  await expect(page).toHaveURL(/\/signin/);
  await expect(page.getByText("You're offline. Signing in opens the notes kept on this device.")).toBeVisible();
  await page.getByLabel("Username").fill(usernames[user]);
  await page.getByLabel("Password", { exact: true }).fill(secrets().passwords[user]);
  await page.getByRole("button", { name: "Sign in" }).click();
  // Argon2id in a worker: a second or two.
  await expect(page).not.toHaveURL(/\/signin/, { timeout: 30_000 });
}

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

/** "· 45.2 KB of attachments" in a vault's line, in MiB (storageText's binary units); NaN without one. */
function attachmentsMiB(line: string): number {
  const m = line.replace(/\s+/g, " ").match(/· ([\d.]+) (B|KB|MB|GB) of attachments/);
  if (!m) return NaN;
  return Number(m[1]) * { B: 1 / 1024 ** 2, KB: 1 / 1024, MB: 1, GB: 1024 }[m[2] as "B" | "KB" | "MB" | "GB"];
}

/** A line of a Welcome note's text, as the server has it: what the note shows when it is on this device. */
async function welcomeLine(api: Api): Promise<{ path: string; line: string }> {
  const path = "/Welcome/Obsidian.md";
  const line = (await api.getNote(path)).content.split("\n").find((l) => /^[A-Z][^#*`[\]|>]{30,}$/.test(l.trim()));
  expect(line, "a plain line of the Welcome note to look for").toBeDefined();
  return { path, line: line!.trim().slice(0, 30) };
}

test("gaps #1a: with All my vaults every vault is on this device, Welcome as Work @offline", async ({ page, aliceApi, folder }) => {
  test.setTimeout(180_000);
  await aliceApi.putNote(`/Work/${folder}/Plan.md`, "# Plan\n\nKept with all my vaults.\n");
  const welcome = await welcomeLine(aliceApi);
  await turnOn(page);
  // The default: All my vaults, and each vault ticked by it.
  await expect(page.getByRole("checkbox", { name: "All my vaults" })).toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Keep Welcome on this device" })).toBeChecked();
  await untilHeld(page, "Welcome");

  await page.goto(notePage(welcome.path));
  await expect(article(page)).toContainText(welcome.line);
  await goOffline(page);
  await unlockAt(page, notePage(welcome.path));
  await expect(article(page)).toContainText(welcome.line);
  await unlockAt(page, notePage(`/Work/${folder}/Plan.md`));
  await expect(article(page)).toContainText("Kept with all my vaults.");
  await page.context().setOffline(false);
});

test("gaps #1b: a vault chosen one by one: the copy keeps Work and not Welcome, unticked @offline", async ({ page, aliceApi, folder }) => {
  await aliceApi.putNote(`/Work/${folder}/Plan.md`, "# Plan\n\nKept by choice.\n");
  const welcome = await welcomeLine(aliceApi);
  await turnOn(page);
  await untilHeld(page, "Welcome");

  // Unticking All my vaults keeps today's vaults chosen one by one (§4.10); then Welcome is unticked.
  await page.getByRole("checkbox", { name: "All my vaults" }).click();
  await expect(page.getByRole("checkbox", { name: "All my vaults" })).not.toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Keep Welcome on this device" })).toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Keep Work on this device" })).toBeChecked();
  await page.getByRole("checkbox", { name: "Keep Welcome on this device" }).click();
  await expect(page.getByRole("checkbox", { name: "Keep Welcome on this device" })).not.toBeChecked();
  // A vault not held shows its size only, the note count of the vault list: no "· … of attachments".
  await expect(vaultLine(page, "Welcome")).toHaveText(/^Welcome\s*[1-9][\d,]* notes?$/);
  // The choice is the copy's, read back after a reload.
  await page.reload();
  await expect(page.getByRole("checkbox", { name: "All my vaults" })).not.toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Keep Welcome on this device" })).not.toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Keep Work on this device" })).toBeChecked();
  await expect(vaultLine(page, "Work")).toContainText(/ notes? · /);

  await goOffline(page);
  await unlockAt(page, notePage(welcome.path));
  await expect(page.getByRole("complementary", { name: "File tree" }).getByText("Welcome is not kept on this device.")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText(welcome.line)).toHaveCount(0);
  await unlockAt(page, notePage(`/Work/${folder}/Plan.md`));
  await expect(article(page)).toContainText("Kept by choice.");
  await page.context().setOffline(false);
});

test("gaps #1c: a folder kept offline keeps its large attachment; one in another folder opens online only @offline", async ({ page, aliceApi, folder }) => {
  test.setTimeout(180_000);
  // Both above the 1 MiB that come with the notes (SMALL_ATTACHMENT_BYTES), about 1.65 MiB each; named for the test,
  // as links resolve vault-wide and an earlier run's files stay in Work.
  const id = folder.slice(-6);
  const [kept, rest] = [`kept-${id}.png`, `rest-${id}.png`];
  await aliceApi.putFile(`/Work/${folder}/Kept/${kept}`, noisePng(760));
  await aliceApi.putFile(`/Work/${folder}/Rest/${rest}`, noisePng(760));
  await aliceApi.putNote(`/Work/${folder}/Kept/Plan.md`, `# Plan\n\n![[${kept}]]\n\n![[${rest}]]\n`);
  await turnOn(page);
  // Work's attachments here before: other tests' small ones come with the notes, so only the growth counts.
  const before = attachmentsMiB(await vaultLine(page, "Work").innerText());
  expect(before, "the vault's attachment size before Keep offline").not.toBeNaN();
  await page.goto(notePage(`/Work/${folder}/Kept/Plan.md`));
  await expect(article(page)).toBeVisible();

  // Keep offline from the folder's menu in the tree (§3a item 4).
  const tree = page.getByRole("complementary", { name: "File tree" });
  await tree.getByRole("button", { name: "Actions for Kept", exact: true }).click();
  await page.getByRole("menu", { name: "Actions for Kept" }).getByRole("menuitem", { name: "Keep offline" }).click();
  // The menu offers to stop once the choice is in the copy.
  await expect(async () => {
    await page.keyboard.press("Escape");
    await tree.getByRole("button", { name: "Actions for Kept", exact: true }).click();
    await expect(page.getByRole("menu", { name: "Actions for Kept" }).getByRole("menuitem", { name: "Don't keep offline" })).toBeVisible({ timeout: 1_000 });
  }).toPass({ timeout: 10_000 });
  await page.keyboard.press("Escape");
  await page.goto("/settings/offline");
  await expect(page.getByRole("list", { name: "Kept offline" }).getByRole("listitem")).toHaveText([new RegExp(`^Work / ${folder}/Kept\\s*Remove$`)]);
  // The engine fetches it in the background: the vault's line counts it among the attachments, one of the two only.
  await expect
    .poll(
      async () => {
        await page.reload();
        return attachmentsMiB(await vaultLine(page, "Work").innerText().catch(() => "")) - before;
      },
      { message: "the kept folder's attachment is on this device", timeout: 30_000, intervals: [1000, 2000] },
    )
    // Each image is about 1.65 MiB and the line rounds to 0.1 MB: one of them is 1.2 or more, both would be 3 or more.
    .toBeGreaterThanOrEqual(1.2);
  expect(attachmentsMiB(await vaultLine(page, "Work").innerText()) - before, "the other folder's attachment isn't").toBeLessThan(2.6);

  await page.goto(notePage(`/Work/${folder}/Kept/Plan.md`));
  await expect(article(page)).toBeVisible();
  await goOffline(page);
  const viewer = (name: string) => page.getByRole("main", { name });
  await article(page).getByRole("link", { name: `Open the image ${kept}` }).click();
  await expect(viewer(kept).getByRole("img", { name: kept })).toBeVisible();
  await page.goBack();
  await expect(article(page)).toBeVisible();
  await article(page).getByRole("link", { name: `Open the image ${rest}` }).click();
  await expect(viewer(rest).getByRole("alert")).toHaveText("Opens when you're online.");
  await page.context().setOffline(false);
});

test("gaps #2: offline, what needs a connection stays in its menu, dimmed, with \"Needs a connection\" @offline", async ({ page, aliceApi, folder }) => {
  const plan = `/Work/${folder}/Plan.md`;
  await aliceApi.putNote(plan, "# Plan\n\nRead offline.\n");
  await turnOn(page);
  await page.goto(notePage(plan));
  await expect(article(page)).toContainText("Read offline.");
  await goOffline(page);
  await expect(bar(page)).toHaveText(/You're offline\. Your notes are here; what needs a connection is marked\./);

  await page.getByRole("button", { name: "Note actions" }).click();
  const menu = page.getByRole("menu", { name: "Note actions" });
  await expect(menu).toBeVisible();
  // §3: history, blame, move and rename, and (§4.9) duplicating need a connection: there, disabled, with the reason.
  for (const name of ["History", "Who wrote what", "Duplicate", "Rename or move"]) {
    const item = menu.getByRole("menuitem", { name: new RegExp(`^${name}`) });
    await expect(item, `${name} stays in the menu`).toHaveCount(1);
    await expect(item, `F-05: ${name} is dimmed offline`).toBeDisabled({ timeout: 2_000 });
    await expect(item, `F-05: ${name} says it needs a connection`).toContainText("Needs a connection");
  }
  await page.keyboard.press("Escape");
  await page.context().setOffline(false);
});

test("gaps #3: a vault's copy purged after access is lost: the notice names no vault @offline", async ({ bobPage, bobApi }) => {
  test.info().annotations.push({
    type: "finding",
    description:
      "F-06: a vault purged from the copy (access lost) shows no notice. Seen with Bob online: the socket's {\"type\":\"mirror\"} frame comes within 20 ms of his leaving, the contact it starts answers one vault fewer, the copy no longer holds the vault (offline: \"… is not kept on this device.\"), yet no status region ever renders in 40 s and nothing is logged; the cause isn't visible from the page (mode.ts:253 sets the notice only when that contact's own purge counts a vault). Intermittent: the notice came in 2 of 8 runs with two workers, in 0 of 9 alone",
  });
  test.setTimeout(240_000);
  // A vault of the test's own, shared with Bob; his device keeps it with All my vaults.
  const share = await shareWithBob("Purge", "write");
  let shared = true;
  try {
    await bobApi.putNote(`/${share.vault}/Plan.md`, "# Plan\n\nBob's copy.\n");
    await prepare(bobPage);
    await turnOn(bobPage, "bob", share.vault);
    await bobPage.goto(notePage(`/${share.vault}/Plan.md`));
    await expect(article(bobPage)).toContainText("Bob's copy.");

    // Bob leaves and Alice deletes the vault, from other sessions: his sockets lose it, and his browser purges it.
    await unshare(share);
    shared = false;
    const notice = bobPage.getByRole("status", { name: "Offline copy" });
    // Exact: the sentence of §3a, with no vault's name in it.
    await expect(notice, "F-06: the purge's notice").toHaveText(/^A vault's copy was removed from this device: it's no longer available here\.\s*Dismiss$/, { timeout: 45_000 });
    await expect(notice, "the notice names no vault").not.toContainText(share.vault);

    // And the copy has it no more: offline, the vault isn't kept here.
    await goOffline(bobPage);
    await unlockAt(bobPage, notePage(`/${share.vault}/Plan.md`), "bob");
    await expect(bobPage.getByRole("complementary", { name: "File tree" }).getByText(`${share.vault} is not kept on this device.`)).toBeVisible({ timeout: 30_000 });
    await expect(bobPage.getByText("Bob's copy.")).toHaveCount(0);
    await bobPage.context().setOffline(false);
  } finally {
    if (shared) await unshare(share);
  }
});

test("gaps #4: a discreet vault shown while offline is turned on is never kept, nor offered in the copy's vault list @offline @serial @discreet", async ({ freshSession, aliceApi, folder }) => {
  test.setTimeout(240_000);
  const { page } = await freshSession("alice");
  try {
    await prepare(page);
    await aliceApi.putNote(`/Work/${folder}/Plan.md`, "# Plan\n\nKept beside the hidden one.\n");
    const home = "/Home/Zahnarzt.md";
    await page.goto(notePage(`/Work/${folder}/Plan.md`));
    await revealDiscreet(page);
    await page.goto(notePage(home));
    await expect(article(page)).toContainText("Zahnarzt");
    const homeId = ((await (await page.request.get("/api/vaults")).json()) as { id: string; name: string; discreet: boolean }[]).find((v) => v.name === "Home");
    expect(homeId?.discreet, "Home is shown, and discreet").toBe(true);

    const slot = await turnOn(page);
    // Still shown after the wait on the password ledger, so the checks below aren't vacuous.
    const now = ((await (await page.request.get("/api/vaults")).json()) as { id: string; name: string; discreet: boolean }[]).find((v) => v.name === "Home");
    expect(now, "Home is still shown, and discreet, while the copy is made").toEqual(expect.objectContaining({ id: homeId!.id, discreet: true }));
    // The copy's vault list (§3a item 2, Task 8's fifth place): every vault but Home, shown as it is.
    await expect(page.getByRole("checkbox", { name: "Keep Work on this device" })).toBeVisible();
    await expect(page.getByRole("list", { name: "Vaults on this device" }).getByRole("listitem").filter({ hasText: /^Home/ })).toHaveCount(0);
    await expect(page.getByRole("checkbox", { name: "Keep Home on this device" })).toHaveCount(0);
    // The server's contact lists the vaults this browser may mirror: never Home (§4.3, §4.6).
    const listed = await page.evaluate(async (token) => {
      const r = await fetch("/api/account/mirror-slots/contact", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ token, waiting: 0 }) });
      return { status: r.status, ids: ((await r.json()) as { vaults: { id: string }[] }).vaults.map((v) => v.id) };
    }, slot.token);
    expect(listed.status).toBe(200);
    expect(listed.ids, "the contact never lists Home").not.toContain(homeId!.id);
    expect(listed.ids.length, "the contact lists the other vaults").toBeGreaterThan(0);

    // Offline, the copy has no Home: its note doesn't open, while Work's does.
    await page.goto(notePage(home));
    await goOffline(page);
    await unlockAt(page, notePage(home));
    // The sentence any vault not kept here gets, a made-up one's too: it tells Home from no other.
    await expect(page.getByRole("complementary", { name: "File tree" }).getByText("Home is not kept on this device.")).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText("Zahnarzt")).toHaveCount(0);
    await unlockAt(page, notePage(`/Work/${folder}/Plan.md`));
    await expect(article(page)).toContainText("Kept beside the hidden one.");
    await page.context().setOffline(false);
    await page.goto(notePage(`/Work/${folder}/Plan.md`));
    await hideDiscreet(page);
  } finally {
    await page.context().setOffline(false);
    await endHidden(page);
  }
});

test("gaps #5: a read-only member's copy (Bob in Work) queues no write: no Write offline, nothing waits @offline", async ({ bobPage, bobApi, aliceApi, folder }) => {
  const plan = `/Work/${folder}/Plan.md`;
  await aliceApi.putNote(plan, "# Plan\n\nAlice's, read by Bob.\n");
  await prepare(bobPage);
  const slot = await turnOn(bobPage, "bob");
  await bobPage.goto(notePage(plan));
  await expect(article(bobPage)).toContainText("Alice's, read by Bob.");
  await goOffline(bobPage);

  await expect(bobPage.getByRole("button", { name: "Write" })).toBeDisabled();
  await bobPage.getByRole("button", { name: "Note actions" }).click();
  const menu = bobPage.getByRole("menu", { name: "Note actions" });
  await expect(menu.getByRole("menuitem").first()).toBeVisible();
  await expect(menu.getByRole("menuitem", { name: /^Delete note/ })).toHaveCount(0);
  await expect(menu.getByRole("menuitem", { name: /^Rename or move/ })).toHaveCount(0);
  await bobPage.keyboard.press("Escape");
  await expect(bar(bobPage)).toHaveText(/^You're offline\. Your notes are here; what needs a connection is marked\./);

  // Back online: nothing uploaded, and his device reports nothing waiting.
  await bobPage.context().setOffline(false);
  await expect(bar(bobPage)).toHaveCount(0, { timeout: 30_000 });
  await expect
    .poll(async () => (await bobApi.json<{ id: string; waiting: number | null }[]>("GET", "/api/account/mirror-slots")).find((s) => s.id === slot.id)?.waiting, {
      message: "Bob's device reports no change waiting",
      timeout: 30_000,
    })
    .toBe(0);
  expect((await aliceApi.getNote(plan)).content).toBe("# Plan\n\nAlice's, read by Bob.\n");
});

test("gaps #6: offline the graph and Similar notes say they need a connection @offline", async ({ page, aliceApi, folder }, testInfo) => {
  test.setTimeout(180_000);
  const plan = `/Work/${folder}/Plan.md`;
  await aliceApi.putNote(plan, "# Plan\n\nThe lighthouse keeper's log.\n");
  await turnOn(page);
  await page.goto(notePage(plan));
  await expect(article(page)).toContainText("The lighthouse keeper's log.");
  await goOffline(page);

  // Similar notes, opened only offline (online, with search off, it would show its "off" state): it says it needs a
  // connection, as the graph does, and offers nothing to try again (link prediction §3.7).
  const similar = page.getByRole("region", { name: "Similar notes" });
  await similar.getByRole("button", { name: "Similar notes" }).click();
  await expect(similar.getByText("Similar notes need a connection. They're back when you are.", { exact: true })).toBeVisible({ timeout: 30_000 });
  await expect(similar.getByRole("button", { name: "Try again" })).toHaveCount(0);
  if (testInfo.project.name === "desktop") await shot(page, testInfo, "offline-gaps");

  // The graph: its button stays (dimmed, not hidden), and its dialog says it needs a connection (graph design §3).
  await page.getByRole("button", { name: "Graph", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Plan" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByText("Drawing the graph…")).toHaveCount(0, { timeout: 30_000 });
  await expect(dialog, "F-07: the graph says it needs a connection").toContainText("The graph needs a connection. It's back when you are.", { timeout: 5_000 });
  await page.context().setOffline(false);
});

test("gaps #7: offline, search finds words in the copy and says it finds words only @offline", async ({ page, aliceApi, folder }) => {
  // A word no other note holds, so the one result is this test's.
  const word = `kelp${randomBytes(4).toString("hex")}`;
  const log = `/Work/${folder}/Harbour log.md`;
  await aliceApi.putNote(log, `# Harbour log\n\nThe ${word} was landed at dawn.\n`);
  await turnOn(page);
  await page.goto(notePage(log));
  await expect(article(page)).toContainText("landed at dawn");
  await goOffline(page);

  await page.getByRole("button", { name: /^Search/ }).click();
  const sheet = page.getByRole("dialog", { name: "Search" });
  // §3a: every offline search runs by words, the chosen mode kept (Hybrid, dimmed with the others but Exact).
  await expect(sheet.getByRole("button", { name: "Hybrid" })).toBeDisabled();
  await expect(sheet.getByRole("button", { name: "Hybrid" })).toHaveAttribute("aria-pressed", "true");
  await expect(sheet.getByRole("switch", { name: "Include earlier versions" })).toBeDisabled();
  await sheet.getByRole("searchbox", { name: "Search" }).fill(`dawn ${word}`);
  await expect(sheet.getByText("You're offline: search finds words only. Search by meaning is back when you are.", { exact: true })).toBeVisible({ timeout: 10_000 });
  await expect(sheet.getByRole("heading", { level: 2 })).toContainText("1 result words");
  const card = sheet.getByRole("button", { name: new RegExp(`Harbour log.*${folder}`) });
  await expect(card).toContainText(`The ${word} was landed at dawn.`);

  await card.click();
  await expect(page).toHaveURL(/Harbour%20log\.md$/);
  await expect(article(page)).toContainText("landed at dawn");
  await page.context().setOffline(false);
});

const source = (page: Page) => page.getByRole("textbox", { name: "Markdown source", exact: true });

/** Write mode: replaces the text, then Done (which saves, into the copy while offline). */
async function write(page: Page, text: string) {
  await page.getByRole("button", { name: "Write" }).click();
  await expect(source(page)).toBeVisible();
  await source(page).fill(text);
  await page.getByRole("button", { name: "Done" }).click();
  await expect(page.getByRole("button", { name: "Done" })).toHaveCount(0);
}

test("gaps #8: a vault's download cut off by a lost network leaves the rest Not on this device yet, and carries on once back @offline", async ({ page, aliceApi }) => {
  test.setTimeout(240_000);
  type Folder = { folders: Folder[]; notes: { path: string }[] };
  const all = (f: Folder): string[] => [...f.notes.map((n) => n.path), ...f.folders.flatMap(all)];
  const welcome = all((await aliceApi.json<{ root: Folder }>("GET", "/api/vaults/Welcome/tree")).root).filter((p) => p.endsWith(".md"));
  expect(welcome.length, "Welcome has more notes than the three let through").toBeGreaterThan(4);

  // As A4 of the hand test: the copy already keeps its vaults, then one more is ticked and the network goes halfway.
  await turnOn(page);
  await untilHeld(page, "Welcome");
  await page.getByRole("checkbox", { name: "All my vaults" }).click();
  await page.getByRole("checkbox", { name: "Keep Welcome on this device" }).click();
  await expect(vaultLine(page, "Welcome")).toHaveText(/^Welcome\s*[1-9][\d,]* notes?$/);

  // The network goes away after the third of Welcome's texts has come: the inventory (the vault's versions) has come
  // by then, the texts are fetched one by one.
  const came: string[] = [];
  await page.context().route(/\/api\/notes\/Welcome\//, async (route) => {
    if (route.request().method() !== "GET") return route.continue();
    if (came.length >= 3) {
      await page.context().setOffline(true);
      return route.abort("internetdisconnected");
    }
    came.push(decodeURIComponent(new URL(route.request().url()).pathname).replace(/^\/api\/notes/, ""));
    return route.continue();
  });
  await page.getByRole("checkbox", { name: "Keep Welcome on this device" }).click();
  await expect(bar(page)).toBeVisible({ timeout: 30_000 });
  expect(came).toHaveLength(3);
  // No error dialog: losing the network is not a failure of the copy.
  await expect(page.getByRole("alertdialog")).toHaveCount(0);
  // Routing done with: in Firefox a page loaded while it is on bypasses the service worker, and offline nothing loads.
  await page.context().unrouteAll({ behavior: "ignoreErrors" });

  // A note that didn't come says so.
  const missing = welcome.find((path) => !came.includes(path))!;
  await unlockAt(page, notePage(missing));
  await expect(page.getByText("Not on this device yet", { exact: false })).toBeVisible({ timeout: 30_000 });

  // Back online: the download carries on by itself, with nobody asking for it. The app switches back first (a mode
  // holds 15 seconds), which in Firefox cut off a reload begun before it.
  await page.context().setOffline(false);
  await expect(bar(page)).toHaveCount(0, { timeout: 60_000 });
  // Settings, loaded again until Welcome's line says every note is here; in Firefox the app's own navigation once
  // the download goes on may cut off a load (NS_BINDING_ABORTED), which is no answer either way.
  await expect
    .poll(
      async () => {
        await page.goto("/settings/offline").catch(() => {});
        return (await vaultLine(page, "Welcome").innerText().catch(() => "")).replace(/\s+/g, " ");
      },
      { message: "every note of Welcome is on this device", timeout: 60_000, intervals: [1000, 2000] },
    )
    .toMatch(/ [1-9][\d,]* notes? · /);
});

test("gaps #9: offline, a wrong password is refused plainly, three times without a lockout, and the right one opens the copy @offline", async ({ page, aliceApi, folder }) => {
  const plan = `/Work/${folder}/Plan.md`;
  await aliceApi.putNote(plan, "# Plan\n\nBehind the right password.\n");
  await turnOn(page);
  await page.goto(notePage(plan));
  await expect(article(page)).toContainText("Behind the right password.");
  await goOffline(page);

  await page.reload();
  await expect(page).toHaveURL(/\/signin/);
  await expect(page.getByText("You're offline. Signing in opens the notes kept on this device.")).toBeVisible();
  const signIn = page.getByRole("button", { name: "Sign in" });
  for (const attempt of [1, 2, 3]) {
    await page.getByLabel("Username").fill(usernames.alice);
    await page.getByLabel("Password", { exact: true }).fill(`not-the-password-${attempt}`);
    await signIn.click();
    // Argon2id runs for a wrong password too: the answer comes once the button is free again.
    await expect(signIn).toBeEnabled({ timeout: 30_000 });
    await expect(page.getByText("The username or password is wrong, or this device keeps no notes of this user.")).toBeVisible();
    await expect(page).toHaveURL(/\/signin/);
  }
  await page.getByLabel("Password", { exact: true }).fill(secrets().passwords.alice);
  await signIn.click();
  await expect(page).not.toHaveURL(/\/signin/, { timeout: 30_000 });
  await expect(article(page)).toContainText("Behind the right password.");
  await page.context().setOffline(false);
});

test("gaps #10: offline, a second tab shows the same bar and the same count, and the change uploads once @offline", async ({ page, aliceApi, folder }) => {
  const plan = `/Work/${folder}/Plan.md`;
  const harbour = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(plan, "# Plan\n\nBefore.\n");
  await aliceApi.putNote(harbour, "# Harbour\n\nThe lighthouse.\n");
  await turnOn(page);
  await page.goto(notePage(plan));
  await expect(article(page)).toContainText("Before.");
  const second = await page.context().newPage();
  await prepare(second);
  await second.goto(notePage(harbour));
  await expect(article(second)).toContainText("The lighthouse.");
  const versions = (await aliceApi.history(plan)).length;

  await goOffline(page);
  await expect(bar(second)).toContainText("You're offline.", { timeout: 30_000 });
  await write(page, "# Plan\n\nWritten in the first tab.\n");
  await expect(bar(page)).toContainText("1 change will upload when you're back.");
  await expect(bar(second)).toContainText("1 change will upload when you're back.", { timeout: 10_000 });

  // Back online: both tabs leave the offline mode, and the one change reaches the server once.
  await page.context().setOffline(false);
  await expect(page.getByRole("status", { name: "Uploaded" })).toContainText("All changes uploaded", { timeout: 30_000 });
  await expect(bar(second)).toHaveCount(0, { timeout: 30_000 });
  expect((await aliceApi.getNote(plan)).content).toBe("# Plan\n\nWritten in the first tab.\n");
  expect((await aliceApi.history(plan)).length, "one new version, not two").toBe(versions + 1);
  await second.close();
});

test("gaps #11: after Keep me signed in the invitation comes once; Not now keeps it away after a reload @offline", async ({ browser, projectContextOptions }) => {
  test.setTimeout(test.info().timeout + limitWindow);
  // A sign-in of its own with Keep me signed in (§3a: the device is plainly the person's own); never over Alice's
  // saved session, which every other test shares.
  const context = await browser.newContext({ ...projectContextOptions, storageState: { cookies: [], origins: [] } });
  try {
    const own = await context.newPage();
    await dismissInstallHint(own);
    await reserve("sign-in-alice");
    await own.goto("/signin");
    await own.getByLabel("Username").fill(usernames.alice);
    await own.getByLabel("Password", { exact: true }).fill(secrets().passwords.alice);
    await own.getByLabel("Keep me signed in on this device").check();
    await own.getByRole("button", { name: "Sign in" }).click();
    await expect(own).not.toHaveURL(/\/signin/);

    const invite = own.getByRole("region", { name: "Keep your notes on this device" });
    await expect(invite).toContainText("Keep your notes on this device for when you're offline?", { timeout: 30_000 });
    await expect(invite.getByRole("button", { name: "Set up" })).toBeVisible();
    await invite.getByRole("button", { name: "Not now" }).click();
    await expect(invite).toHaveCount(0);

    await own.reload();
    await expect(own.getByRole("button", { name: /^Search/ })).toBeVisible({ timeout: 30_000 });
    // The invitation decides from the device's own mark, before it asks the copy store: once marked, it never shows.
    expect(await own.evaluate(() => localStorage.getItem("kumbu.offlineInvite"))).toBe("asked");
    await expect(invite).toHaveCount(0);
  } finally {
    await context.close();
  }
});
