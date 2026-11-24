// SPDX-License-Identifier: AGPL-3.0-or-later
// The Published window and Compare (docs/superpowers/specs/2026-11-23-published-overview-design.md, #310; ADRs 0017,
// 0018), with the note's bar's words where the window reuses them. The reader link is a secret: kept in a variable,
// checked by status and booleans only, never in a message or a screenshot (the window itself never shows one), and no
// failure screenshots, as a reader page or a dialog behind a failure could.
//
// Promises in scope, and the test that covers each:
// 1. Opened from the drawer's account row ("Published") and from the palette ("Open / Published"); `/published`
//    loads it; the row's path is a link to the note → overview #1.
// 2. Per row: the path, the dot and word ("Up to date", "Copy is older than the note"), "with N linked notes" for a
//    bundle, the snapshot's time and who published it ("by you") → overview #2. Another publisher ("by “name”") is
//    out of reach here (Bob is a read-only member and can't publish); Vitest covers it (PublishedPage.test.tsx, "lists
//    her publications with who published them, and whether the copy is older").
// 3. "N of M copies are older than their notes." above the list, and the rows by path, across vaults → overview #9
//    (@serial: the list is every publication of Alice's; the expected one is read from GET /api/publications first).
// 4. Update copy only on an older copy; after it the row is "Up to date" → overview #2 (only where older), #3; for a
//    bundle it opens the publish dialog with its list first → overview #4.
// 5. Unpublish after the bar's question (Keep keeps it); the row goes; the reader link answers 404 → overview #5.
// 7. Compare on the row and on the note's bar, only where the copy is older: the note at the copy's version beside the
//    note now → overview #6; a version the history no longer keeps says so → overview #7 (the history keeps 10
//    versions, NoteWriter.HistoryLimit: twelve writes alternating between Alice and an agent push it out); a copy made
//    after an edit in the same session → overview #8 (F-04).
// 8. Never a reader link on the page, nor in the overview's answer → overview #2.
// Left out: 6, publishing switched off on the server (one line says so, Update copy goes, Unpublish stays): it needs a
// stack started with Kumbu__Publish__Enabled=false, a phase of its own (plan, review point 15); Vitest covers it
// (web/src/published/PublishedPage.test.tsx, "switched off, says so and offers only Unpublish"). Compare's picker of a
// bundle's notes: Vitest ("picks a note of a bundle"). Skills shared by link stay off the page (F4): nothing to see.
import type { Locator, Page, TestInfo } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { Api, encodePath, guarded } from "../support/api";
import { secrets } from "../support/env";
import { reservePassword, reserveReaderRead } from "../support/session";
import { expect, onPhone, shot, shotsDir070, test } from "../support/test";

test.use({ shotsDir: shotsDir070, screenshot: "off" });

type Overview = { path: string; snapshotAt: string; older: boolean; publishedBy: string; copied?: { path: string; version: string }[] | null };

/** The snapshot's time as the row shows it, in the browser's zone (playwright.config.ts: Europe/Vienna, en-GB). */
const snapshotTime = (at: string) =>
  new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", timeZone: "Europe/Vienna" }).format(new Date(at));

/** Notes named with the folder's random part, as links resolve vault-wide. */
function named(folder: string) {
  const id = folder.slice(-6);
  const name = (title: string) => `${title} ${id}`;
  return { name, path: (title: string) => `/Work/${folder}/${name(title)}.md`, link: (title: string) => `[[${name(title)}]]` };
}

/** Opens the password window of Alice's shared session, if it is closed (a check counts against a limit). */
async function windowOpen(api: Api) {
  const session = await api.json<{ passwordWindowOpen: boolean }>("GET", "/api/session");
  if (!session.passwordWindowOpen) await api.confirmPassword();
}

/** Publishes over the API, as the dialog does once the password window is open; returns the reader link, never printed. */
async function publish(api: Api, start: string, depth = 0, expected: string[] = [start]) {
  await windowOpen(api);
  const response = await api.send("POST", `/api/publications/${encodePath(start)}`, { data: { depth, ...(depth > 0 ? { expected: expected.map((p) => Api.inVault(p)) } : {}) } });
  expect(response.status(), `published ${start} over the API`).toBe(201);
  return ((await response.json()) as { link: string }).link;
}

/** The status of a reader's read of the link (POST /api/s with the code from its fragment), without the link. */
async function readStatus(link: string) {
  await reserveReaderRead();
  const anon = await Api.anonymous();
  try {
    return (await guarded(() => anon.context.post("/api/s", { data: { code: new URL(link).hash.slice(1) } }), "POST /api/s")).status();
  } finally {
    await anon.dispose();
  }
}

const overview = (api: Api) => api.json<Overview[]>("GET", "/api/publications");
const publication = (api: Api, path: string) => api.json<Overview>("GET", `/api/publications/${encodePath(path)}`);

async function openNote(page: Page, path: string) {
  const hint = page.getByRole("region", { name: "Install Kumbu² as an app" });
  await page.addLocatorHandler(hint, async () => {
    await hint.getByRole("button", { name: "Not now" }).click();
  });
  await page.goto(`/notes/${encodePath(path)}`);
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

/** The tree's Account links: in the docked tree on the desktop, in the drawer on the phone. */
async function account(page: Page, testInfo: TestInfo): Promise<Locator> {
  if (!onPhone(testInfo)) return page.getByRole("complementary", { name: "File tree" }).getByRole("navigation", { name: "Account" });
  const drawer = page.getByRole("dialog", { name: "File tree" });
  if (!(await drawer.isVisible())) await page.getByRole("button", { name: "Open file tree" }).click();
  return drawer.getByRole("navigation", { name: "Account" });
}

const published = (page: Page) => page.getByRole("dialog", { name: "Published", exact: true });
const rows = (page: Page) => published(page).getByRole("list", { name: "Published notes" });
/** The row of `path`: the list item holding its link. */
const row = (page: Page, path: string) => rows(page).getByRole("listitem").filter({ has: page.getByRole("link", { name: path, exact: true }) });
/** The row's state line: the word, the bundle, the snapshot and who published it. */
const state = (r: Locator) => r.getByText(/^(Up to date|Copy is older than the note) · /);

/** `/published` itself. On the phone the install hint, shown once, stays behind the window, inert: nothing to answer. */
async function openPublished(page: Page) {
  await page.goto("/published");
  await expect(published(page)).toBeVisible();
}

/** Clean-ups a test registers before it writes: they run after it even when it timed out. */
let cleanUps: (() => Promise<void>)[] = [];
const afterTest = (cleanUp: () => Promise<void>) => void cleanUps.push(cleanUp);
// windowOpen may wait for the password ledger (61 s), and #9 deletes a vault with the password.
test.beforeEach(() => test.setTimeout(240_000));
test.afterEach(async () => {
  for (const cleanUp of cleanUps.reverse()) await cleanUp().catch(() => undefined);
  cleanUps = [];
});

/** Withdraws each start note's copy and removes the notes, whatever the test got to. */
const cleanUp = (api: Api, starts: string[], paths: string[]) =>
  afterTest(async () => {
    for (const s of starts) await api.send("DELETE", `/api/publications/${encodePath(s)}`).catch(() => undefined);
    for (const p of paths) await api.deleteNote(p).catch(() => undefined);
  });

test("overview #1: the account row, the palette and /published open the window, and a row's path opens the note @overview @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const n = named(folder);
  cleanUp(aliceApi, [n.path("Harbour")], [n.path("Harbour")]);
  await aliceApi.putNote(n.path("Harbour"), "# Harbour\n\nThe lighthouse.\n");
  await publish(aliceApi, n.path("Harbour"));

  // The drawer's account row.
  await openNote(page, n.path("Harbour"));
  await (await account(page, testInfo)).getByRole("link", { name: "Published", exact: true }).click();
  await expect(page).toHaveURL(/\/published$/);
  await expect(published(page)).toBeVisible();
  await expect(row(page, n.path("Harbour"))).toHaveCount(1);
  await page.keyboard.press("Escape");
  await expect(published(page)).toHaveCount(0);

  // The palette's Open / Published.
  await page.keyboard.press("Control+p");
  const palette = page.getByRole("dialog", { name: "Favourites and commands" });
  await palette.getByRole("combobox", { name: "Find a command, vault or note" }).fill("published");
  await palette.getByRole("option", { name: "Published, Open", exact: true }).click();
  await expect(page).toHaveURL(/\/published$/);
  await expect(published(page)).toBeVisible();

  // The address itself, and the row's path: a link to the note, the window gone.
  await openPublished(page);
  await row(page, n.path("Harbour")).getByRole("link", { name: n.path("Harbour"), exact: true }).click();
  await expect(published(page)).toHaveCount(0);
  await expect(page).toHaveURL(new RegExp(`/notes/${encodePath(n.path("Harbour")).replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`));
  await expect(page.locator("article").getByRole("heading", { name: "Harbour", level: 1 })).toBeVisible();
});

test("overview #2: a row shows its path, state, linked notes, snapshot and publisher; Update copy and Compare only where older; never a reader link @overview @phone", async ({
  page,
  aliceApi,
  folder,
}, testInfo) => {
  const n = named(folder);
  const paths = ["Single", "Start", "Alpha", "Beta"].map((t) => n.path(t));
  cleanUp(aliceApi, [n.path("Single"), n.path("Start")], paths);
  await aliceApi.putNote(n.path("Single"), "# Single\n");
  await aliceApi.putNote(n.path("Start"), `# Start\n\n${n.link("Alpha")} ${n.link("Beta")}\n`);
  for (const t of ["Alpha", "Beta"]) await aliceApi.putNote(n.path(t), `# ${t}\n`);
  await publish(aliceApi, n.path("Single"));
  await publish(aliceApi, n.path("Start"), 1, [n.path("Start"), n.path("Alpha"), n.path("Beta")]);
  // A linked note changed: the bundle's copy is older.
  await aliceApi.putNote(n.path("Beta"), "# Beta\n\nChanged after the copy.\n");
  const single = await publication(aliceApi, n.path("Single"));
  const start = await publication(aliceApi, n.path("Start"));
  expect(start.older, "the bundle's copy is older after a linked note changed").toBe(true);

  await openPublished(page);
  const up = row(page, n.path("Single"));
  const old = row(page, n.path("Start"));
  await expect(state(up)).toHaveText(`Up to date · Snapshot of ${snapshotTime(single.snapshotAt)} · by you`);
  await expect(up).toHaveAttribute("data-tone", "green");
  await expect(state(old)).toHaveText(`Copy is older than the note · with 2 linked notes · Snapshot of ${snapshotTime(start.snapshotAt)} · by you`);
  await expect(old).toHaveAttribute("data-tone", "yellow");
  // The bar's actions: Update copy and Compare only on the older copy, Unpublish on both, no Copy link.
  await expect(up.getByRole("button")).toHaveText(["Unpublish"]);
  await expect(old.getByRole("button")).toHaveText(["Update copy", "Compare", "Unpublish"]);
  // A linked note of a bundle has no row of its own.
  await expect(row(page, n.path("Alpha"))).toHaveCount(0);

  // Never a reader link: not on the page, not in the overview's answer (booleans only).
  const onPage = await published(page).evaluate(
(d) => (d.textContent ?? "").includes("/s#") || [...d.querySelectorAll("a")].some((a) => (a.getAttribute("href") ?? "").includes("/s#")));
  expect(onPage, "no reader link on the Published page").toBe(false);
  await expect(published(page).getByRole("button", { name: /Copy link/ })).toHaveCount(0);
  const answer = await aliceApi.send("GET", "/api/publications");
  const text = await answer.text();
  expect(text.includes("/s#") || /"link"/.test(text), "no link in the overview's answer").toBe(false);

  await shot(page, testInfo, "overview");
});

test("overview #3: Update copy on an older copy brings the row to Up to date @overview @phone", async ({ page, aliceApi, folder }) => {
  const n = named(folder);
  cleanUp(aliceApi, [n.path("Beacon")], [n.path("Beacon")]);
  await aliceApi.putNote(n.path("Beacon"), "# Beacon\n");
  await publish(aliceApi, n.path("Beacon"));
  await aliceApi.putNote(n.path("Beacon"), "# Beacon\n\nA second line.\n");

  await windowOpen(aliceApi);
  await openPublished(page);
  const r = row(page, n.path("Beacon"));
  await expect(r).toHaveAttribute("data-tone", "yellow");
  await r.getByRole("button", { name: `Update copy of ${n.path("Beacon")}`, exact: true }).click();
  await expect(r).toHaveAttribute("data-tone", "green");
  await expect(state(r)).toHaveText(/^Up to date · Snapshot of /);
  await expect(r.getByRole("button")).toHaveText(["Unpublish"]);
  expect((await publication(aliceApi, n.path("Beacon"))).older, "the copy read back is up to date").toBe(false);
});

test("overview #4: Update copy on a bundle shows its list first, then the row is Up to date with the new linked note @overview @phone", async ({ page, aliceApi, folder }) => {
  const n = named(folder);
  const paths = ["Start", "Alpha", "Gamma"].map((t) => n.path(t));
  cleanUp(aliceApi, [n.path("Start")], paths);
  await aliceApi.putNote(n.path("Start"), `# Start\n\n${n.link("Alpha")}\n`);
  for (const t of ["Alpha", "Gamma"]) await aliceApi.putNote(n.path(t), `# ${t}\n`);
  await publish(aliceApi, n.path("Start"), 1, [n.path("Start"), n.path("Alpha")]);
  await aliceApi.putNote(n.path("Start"), `# Start\n\n${n.link("Alpha")} ${n.link("Gamma")}\n`);

  await windowOpen(aliceApi);
  await openPublished(page);
  const r = row(page, n.path("Start"));
  await expect(state(r)).toHaveText(/^Copy is older than the note · with 1 linked note · /);
  await r.getByRole("button", { name: `Update copy of ${n.path("Start")}`, exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Update the published copy" });
  await expect(dialog.getByRole("status")).toHaveText("3 notes");
  const goes = dialog.getByRole("group", { name: "What goes out" });
  for (const t of ["Start", "Alpha", "Gamma"]) await expect(goes.getByRole("checkbox", { name: `${n.name(t)} ${n.path(t)}`, exact: true })).toBeChecked();
  // Nothing went out yet: the list comes first.
  expect((await publication(aliceApi, n.path("Start"))).older, "nothing goes out before the list is confirmed").toBe(true);
  await dialog.getByRole("button", { name: "Update copy", exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(r).toHaveAttribute("data-tone", "green");
  await expect(state(r)).toHaveText(/^Up to date · with 2 linked notes · Snapshot of /);
  expect((await publication(aliceApi, n.path("Start"))).older, "the copy read back is up to date").toBe(false);
});

test("overview #5: Unpublish asks first, Keep keeps it; Unpublish removes the row and the reader link answers 404 @overview @phone", async ({ page, aliceApi, folder }) => {
  const n = named(folder);
  cleanUp(aliceApi, [n.path("Lantern")], [n.path("Lantern")]);
  await aliceApi.putNote(n.path("Lantern"), "# Lantern\n");
  const link = await publish(aliceApi, n.path("Lantern"));
  expect(await readStatus(link), "the reader reads the copy before it is withdrawn").toBe(200);

  await openPublished(page);
  const r = row(page, n.path("Lantern"));
  const question = page.getByRole("alertdialog", { name: "Withdraw the link? Whoever has it can't read the note any more." });
  await r.getByRole("button", { name: `Unpublish ${n.path("Lantern")}`, exact: true }).click();
  await question.getByRole("button", { name: "Keep", exact: true }).click();
  await expect(question).toHaveCount(0);
  await expect(r).toHaveCount(1);
  expect(await readStatus(link), "Keep leaves the copy readable").toBe(200);

  await r.getByRole("button", { name: `Unpublish ${n.path("Lantern")}`, exact: true }).click();
  await question.getByRole("button", { name: "Unpublish", exact: true }).click();
  await expect(question).toHaveCount(0);
  await expect(row(page, n.path("Lantern"))).toHaveCount(0);
  expect((await aliceApi.send("GET", `/api/publications/${encodePath(n.path("Lantern"))}`)).status(), "the note is no longer published").toBe(404);
  expect(await readStatus(link), "the withdrawn reader link answers 404").toBe(404);
});

/** The Compare sheet's two columns: the note at the copy's version, and the note now. */
async function compared(sheet: Locator, before: string[], now: string[]) {
  await expect(sheet.getByRole("list", { name: "Published copy" }).getByRole("listitem")).toHaveText(before);
  await expect(sheet.getByRole("list", { name: "Now" }).getByRole("listitem")).toHaveText(now);
  await expect(sheet.getByRole("list", { name: "Published copy" }).locator('[data-kind="removed"]')).toHaveText(["The old line."]);
  await expect(sheet.getByRole("list", { name: "Now" }).locator('[data-kind="added"]')).toHaveText(["The new line."]);
}

test("overview #6: Compare on the row and on the note's bar shows the copy's version beside the note now @overview @phone", async ({ page, aliceApi, folder }) => {
  const n = named(folder);
  cleanUp(aliceApi, [n.path("Tide")], [n.path("Tide")]);
  await aliceApi.putNote(n.path("Tide"), "# Tide\n\nThe old line.\n");
  await publish(aliceApi, n.path("Tide"));
  await aliceApi.putNote(n.path("Tide"), "# Tide\n\nThe new line.\n");
  const before = ["# Tide", "", "The old line.", ""];
  const now = ["# Tide", "", "The new line.", ""];

  await openPublished(page);
  await row(page, n.path("Tide")).getByRole("button", { name: `Compare copy of ${n.path("Tide")}`, exact: true }).click();
  let sheet = page.getByRole("dialog", { name: `Compare ${n.name("Tide")}` });
  await compared(sheet, before, now);
  await sheet.getByRole("button", { name: `Close compare ${n.name("Tide").toLowerCase()}` }).click();
  await expect(sheet).toHaveCount(0);

  await openNote(page, n.path("Tide"));
  const bar = page.getByRole("region", { name: "Publication" });
  await expect(bar).toHaveAttribute("data-tone", "yellow");
  await bar.getByRole("button", { name: "Compare", exact: true }).click();
  sheet = page.getByRole("dialog", { name: `Compare ${n.name("Tide")}` });
  await compared(sheet, before, now);
});

test("overview #7: Compare says so when the history no longer keeps the copy's version @overview @phone", async ({ page, aliceApi, mcp, folder }) => {
  const n = named(folder);
  const path = n.path("Reef");
  cleanUp(aliceApi, [path], [path]);
  await aliceApi.putNote(path, "# Reef\n\nWrite 0.\n");
  await publish(aliceApi, path);
  const copied = (await publication(aliceApi, path)).copied![0].version;
  // Twelve writes, Alice's and an agent's in turn: each replaces a state of the other writer's changeset, so none
  // coalesces and each leaves a version (NoteWriter.cs:132-134); the history keeps ten (NoteWriter.HistoryLimit).
  for (let i = 1; i <= 12; i++) {
    const content = `# Reef\n\nWrite ${i}.\n`;
    if (i % 2) await aliceApi.putNote(path, content);
    else {
      const answer = await mcp.call("write", { path, content, version: (await aliceApi.getNote(path)).version });
      expect(answer.isError, "the agent's write").toBe(false);
    }
  }
  const kept = (await aliceApi.history(path)) as { version: string }[];
  expect(
    kept.some((v) => v.version === copied),
    "the copy's version is out of the history",
  ).toBe(false);

  await openPublished(page);
  await row(page, path).getByRole("button", { name: `Compare copy of ${path}`, exact: true }).click();
  const sheet = page.getByRole("dialog", { name: `Compare ${n.name("Reef")}` });
  await expect(sheet.getByText("This version is no longer kept", { exact: true })).toBeVisible();
  await expect(sheet.getByRole("list", { name: "Published copy" })).toHaveCount(0);
  await expect(sheet.getByRole("list", { name: "Now" }).getByRole("listitem")).toHaveText(["# Reef", "", "Write 12.", ""]);
});

test("overview #8: Compare shows the copy's version after a note was edited, published and edited again in one session @overview", async ({ page, aliceApi, folder }) => {
  test.info().annotations.push({ type: "finding", description: "F-04: a copy made within a changeset loses its version at the next write of it, so Compare says it is no longer kept; a fix likely makes publishing a checkpoint of the note (an ADR)" });
  test.fail();
  const n = named(folder);
  const path = n.path("Shore");
  cleanUp(aliceApi, [path], [path]);
  // Written twice, as the editor's saves do, then published, then written once more: all in Alice's one changeset.
  await aliceApi.putNote(path, "# Shore\n\nDraft.\n");
  await aliceApi.putNote(path, "# Shore\n\nThe old line.\n");
  await publish(aliceApi, path);
  const copied = (await publication(aliceApi, path)).copied![0].version;
  await aliceApi.putNote(path, "# Shore\n\nThe new line.\n");
  // Read back: soft, so the window's Compare is checked too.
  const kept = (await aliceApi.history(path)) as { version: string }[];
  expect.soft(
    kept.some((v) => v.version === copied),
    "F-04: the history keeps the version the copy was made from",
  ).toBe(true);

  await openPublished(page);
  await row(page, path).getByRole("button", { name: `Compare copy of ${path}`, exact: true }).click();
  const sheet = page.getByRole("dialog", { name: `Compare ${n.name("Shore")}` });
  await expect(sheet.getByRole("list", { name: "Now" }).getByRole("listitem")).toHaveText(["# Shore", "", "The new line.", ""]);
  await expect(
    sheet.getByRole("list", { name: "Published copy" }).getByRole("listitem"),
    "F-04: the Published copy column holds the note at the copy's version",
  ).toHaveText(["# Shore", "", "The old line.", ""]);
});

test("overview #9: the count line and the rows, by path across vaults @overview @serial", async ({ page, aliceApi, folder }) => {
  const n = named(folder);
  const vault = `Overview-${randomBytes(3).toString("hex")}`;
  const other = `/${vault}/Elsewhere.md`;
  const mine = ["Aa up to date", "Bb older", "Cc older"].map((t) => n.path(t));
  afterTest(async () => {
    await reservePassword("alice");
    await aliceApi.send("POST", `/api/vaults/${encodeURIComponent(vault)}/delete`, { data: { password: secrets().passwords.alice, confirm: vault } });
  });
  cleanUp(aliceApi, [...mine, other], mine);
  await aliceApi.json("POST", "/api/vaults", { name: vault });
  // What others left published: the window lists it too.
  const before = await overview(aliceApi);
  for (const p of [...mine, other]) await aliceApi.putNote(p, "# Note\n");
  for (const p of [...mine, other]) await publish(aliceApi, p);
  for (const p of mine.slice(1)) await aliceApi.putNote(p, "# Note\n\nChanged.\n");

  const all = [...before.map((p) => p.path), ...mine, other].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
  const older = before.filter((p) => p.older).length + 2;
  // Read back: the server's list is the one expected.
  expect((await overview(aliceApi)).map((p) => p.path), "GET /api/publications lists the copies by path").toEqual(all);

  await openPublished(page);
  await expect(published(page).getByText(/ older than (its|their) notes?\.$/)).toHaveText(`${older} of ${all.length} copies are older than their notes.`);
  await expect(rows(page).getByRole("link")).toHaveText(all);
});
