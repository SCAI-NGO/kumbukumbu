// SPDX-License-Identifier: AGPL-3.0-or-later
// The smoke set (spec §4): what must work for the product to be usable at all.
import type { Locator, Page, TestInfo } from "@playwright/test";
import { join } from "node:path";
import { encodePath } from "../support/api";
import { fixturesDir } from "../support/env";
import { endHidden, hideDiscreet, revealDiscreet } from "../support/session";
import { expect, onPhone, shot, test } from "../support/test";

/** The note's address in the app: `/Work/x/Note.md` → `/notes/Work/x/Note.md`. */
const url = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;

/** Opens a note by its address; on the phone the install hint is answered with Not now, as a person would. */
async function openNote(page: Page, path: string) {
  await page.goto(url(path));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
  const notNow = page.getByRole("button", { name: "Not now" });
  if (await notNow.isVisible()) await notNow.click();
}

/** The file tree: docked on the desktop, a drawer on the phone. */
async function openTree(page: Page, testInfo: TestInfo): Promise<Locator> {
  if (!onPhone(testInfo)) return page.getByRole("complementary", { name: "File tree" });
  await page.getByRole("button", { name: "Open file tree" }).click();
  return page.getByRole("dialog", { name: "File tree" });
}

/** The tree's row of a folder (a button named after it and its count). */
const folderRow = (tree: Locator, folder: string) => tree.getByRole("button", { name: new RegExp(`^${folder}\\b`) });

/** Write mode: types `text` after what is there, and Done (which saves). */
async function write(page: Page, text: string) {
  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  await source.fill(`${await source.inputValue()}${text}`);
  await page.getByRole("button", { name: "Done" }).click();
  await expect(page.getByRole("button", { name: "Done" })).toHaveCount(0);
}

const noteActions = (page: Page) => page.getByRole("button", { name: "Note actions" });

test("smoke #1: Alice signs in and sees Work, Welcome and Skills, and Home after the PIN @smoke @serial", async ({ freshSession }, testInfo) => {
  const { page } = await freshSession("alice");
  const vaults = page.getByRole("list", { name: "Your vaults" });
  await expect(page.getByRole("heading", { name: "Your vaults" })).toBeVisible();
  await expect(vaults.getByRole("listitem")).toHaveText([/^Skills/, /^Welcome/, /^Work/]);
  await shot(page, testInfo, "smoke");

  try {
    await revealDiscreet(page);
    await expect(vaults.getByRole("listitem")).toHaveText([/^Home/, /^Skills/, /^Welcome/, /^Work/]);
    await hideDiscreet(page);
    await expect(vaults.getByRole("listitem")).toHaveText([/^Skills/, /^Welcome/, /^Work/]);
  } finally {
    await endHidden(page);
  }
});

test("smoke #2: a note renders its text, a wikilink and an image @smoke @phone", async ({ page, seedAs, folder }) => {
  for (const name of ["Note.md", "Other.md", "pixel.png"]) await seedAs(`tags/smoke/${name}`, `/Work/${folder}/${name}`);
  await openNote(page, `/Work/${folder}/Note.md`);

  const article = page.locator("article");
  await expect(article.getByRole("heading", { name: "Harbour light", level: 1 })).toBeVisible();
  await expect(article.getByText("lighthouse")).toBeVisible();

  // A note shows an image as a chip; it opens in the lightbox, where the image has loaded.
  const chip = article.getByRole("link", { name: "Open the image pixel.png" });
  await expect(chip).toHaveAttribute("href", `/view/Work/${folder}/pixel.png`);
  await chip.click();
  const viewer = page.getByRole("main", { name: "pixel.png" });
  const image = viewer.getByRole("img", { name: "pixel.png" });
  await expect(image).toBeVisible();
  await expect.poll(() => image.evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth), "the image loaded").toBeGreaterThan(0);
  await viewer.getByRole("button", { name: "Close" }).click();

  await article.getByRole("link", { name: "Other" }).click();
  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Other\\.md$`));
  await expect(page.locator("article").getByRole("heading", { name: "Other", level: 1 })).toBeVisible();
});

test("smoke #3: an edit is saved, survives a reload, and adds one version to History @smoke", async ({ page, aliceApi, seedAs, folder }) => {
  const path = `/Work/${folder}/Note.md`;
  await seedAs("tags/smoke/Note.md", path);
  const before = (await aliceApi.history(path)).length;
  await openNote(page, path);

  await write(page, "\nThe keeper winds the clockwork.\n");
  await expect(page.locator("article").getByText("The keeper winds the clockwork.")).toBeVisible();
  await page.reload();
  await expect(page.locator("article").getByText("The keeper winds the clockwork.")).toBeVisible();

  expect((await aliceApi.history(path)).length, "one version more").toBe(before + 1);
  await noteActions(page).click();
  await page.getByRole("menuitem", { name: "History" }).click();
  await expect(page.getByRole("list", { name: "Versions" }).getByRole("listitem")).toHaveCount(before + 1);
});

test("smoke #4: a renamed note's links are rewritten @smoke", async ({ page, aliceApi, folder }) => {
  const hub = `/Work/${folder}/Hub.md`;
  await aliceApi.putNote(hub, "# Hub\n");
  await openNote(page, hub);

  // A new note in the hub's folder, through the dock's New menu; Untitled is what it is called until it is named.
  await page.getByRole("button", { name: "New", exact: true }).click();
  await page.getByRole("menuitem", { name: "New note" }).click();
  await expect(page.getByRole("textbox", { name: "Markdown source", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Done" }).click();
  await expect(page.getByRole("button", { name: "Done" })).toHaveCount(0);
  const created = decodeURIComponent(new URL(page.url()).pathname.replace(/^\/notes/, ""));
  expect(created).toContain(`/Work/${folder}/`);
  const oldName = created.slice(created.lastIndexOf("/") + 1).replace(/\.md$/, "");

  await aliceApi.putNote(hub, `# Hub\n\nSee [[${oldName}]].\n`);
  await noteActions(page).click();
  await page.getByRole("menuitem", { name: "Rename or move" }).click();
  const dialog = page.getByRole("dialog", { name: /^Rename or move/ });
  await dialog.getByLabel("Name").fill("Beacon.md");
  await dialog.getByRole("button", { name: "Move" }).click();
  await expect(page.getByRole("dialog", { name: "Note moved" }).getByRole("status")).toContainText("Rewrote 1 link");

  await expect.poll(async () => (await aliceApi.getNote(hub)).content).toContain("[[Beacon]]");
  expect((await aliceApi.getNote(hub)).content).not.toContain(oldName);
});

test("smoke #5: search by words and by tag finds the note @smoke", async ({ page, seedAs, folder }) => {
  for (const name of ["Note.md", "Other.md"]) await seedAs(`tags/smoke/${name}`, `/Work/${folder}/${name}`);
  await openNote(page, `/Work/${folder}/Note.md`);
  await page.getByRole("button", { name: /^Search/ }).click();
  const sheet = page.getByRole("dialog", { name: "Search" });
  const input = sheet.getByRole("searchbox", { name: "Search" });
  // Only this test's note: its folder is in its card.
  const card = sheet.getByRole("button", { name: new RegExp(`${folder}.*Note|Note.*${folder}`) });

  await input.fill("lighthouse");
  await expect(card).toBeVisible();
  await expect(sheet.getByRole("button", { name: new RegExp(`Other.*${folder}|${folder}.*Other`) })).toHaveCount(0);

  await input.fill("#smoketag");
  await sheet.getByRole("button", { name: "#smoketag" }).first().click();
  await expect(sheet.getByRole("heading", { name: /tagged #smoketag$/ })).toBeVisible();
  await expect(sheet.getByRole("list", { name: "Notes tagged #smoketag" }).getByRole("button", { name: new RegExp(`${folder}.*Note|Note.*${folder}`) })).toBeVisible();
});

test("smoke #6: a note moved to the trash is restored @smoke", async ({ page, seedAs, folder }, testInfo) => {
  for (const name of ["Note.md", "Other.md"]) await seedAs(`tags/smoke/${name}`, `/Work/${folder}/${name}`);
  await openNote(page, `/Work/${folder}/Note.md`);

  await noteActions(page).click();
  await page.getByRole("menuitem", { name: "Delete note" }).click();
  await page.getByRole("dialog", { name: "Delete Note?" }).getByRole("button", { name: "Delete" }).click();
  await expect(page.getByRole("dialog", { name: "Delete Note?" })).toHaveCount(0);

  // The folder's next note opens; Note is not in the tree any more.
  let tree = await openTree(page, testInfo);
  const folderButton = folderRow(tree, folder);
  // The tree has read the folder again (one item left) before a menu is opened.
  await expect(folderButton).toHaveAccessibleName(/ 1 item$/);
  // The next note, Other, is open, and the tree shows it: its folder is open and in view.
  await expect(folderButton).toHaveAttribute("aria-expanded", "true");
  await expect(tree.getByRole("button", { name: "Other", exact: true })).toBeVisible();
  await expect(tree.getByRole("button", { name: "Note", exact: true })).toHaveCount(0);

  // The vault's ⋯, in the toolbar above the folders (UI round spec §2.1).
  await tree.getByRole("group", { name: "Tools for this vault" }).getByRole("button", { name: "Actions for Work" }).click();
  await page.getByRole("menuitem", { name: "Trash" }).click();
  const trash = page.getByRole("dialog", { name: "Trash" });
  // The dialog lists all of Work's trash, other tests' notes too, uncapped (newest first): this test's row by its
  // folder, scrolled to wherever the others put it.
  const row = trash.getByRole("list", { name: "Removed notes" }).getByRole("listitem").filter({ hasText: folder });
  await expect(row, "this test's note is in the trash").toHaveCount(1);
  await row.scrollIntoViewIfNeeded();
  await row.getByRole("button", { name: "Restore Note" }).click();
  await expect(trash.getByRole("list", { name: "Restored notes" })).toContainText("Restored Note");
  await trash.getByRole("button", { name: "Close trash" }).click();

  tree = await openTree(page, testInfo);
  await expect(tree.getByRole("button", { name: "Note", exact: true })).toBeVisible();
  await openNote(page, `/Work/${folder}/Note.md`);
  await expect(page.locator("article").getByText("lighthouse")).toBeVisible();
});

test("smoke #7: an uploaded image opens @smoke", async ({ page, seedAs, folder }, testInfo) => {
  await seedAs("tags/smoke/Other.md", `/Work/${folder}/Other.md`);
  await openNote(page, `/Work/${folder}/Other.md`);

  const tree = await openTree(page, testInfo);
  await tree.getByRole("button", { name: `Actions for ${folder}` }).click();
  await page.getByRole("menuitem", { name: "Upload here" }).click();
  const sheet = page.getByRole("dialog", { name: "Upload" });
  await sheet.getByLabel("Choose files").setInputFiles(join(fixturesDir, "tags", "smoke", "upload.png"));
  await sheet.getByRole("button", { name: "Upload", exact: true }).click();
  await expect(sheet.getByRole("status")).toHaveText("0 notes and 1 file uploaded, 0 replaced, 0 files skipped");
  await sheet.getByRole("button", { name: "Done" }).click();

  const files = await openTree(page, testInfo);
  const folderButton = folderRow(files, folder);
  if ((await folderButton.getAttribute("aria-expanded")) !== "true") await folderButton.click();
  await files.getByRole("button", { name: "upload.png", exact: true }).click();

  const viewer = page.getByRole("main", { name: "upload.png" });
  const image = viewer.getByRole("img", { name: "upload.png" });
  await expect(image).toBeVisible();
  await expect.poll(() => image.evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth), "the image loaded").toBeGreaterThan(0);
});

test("smoke #8: Bob sees Work, can't edit, and never sees Home @smoke @serial", async ({ freshSession, bobApi, aliceApi }) => {
  const path = "/Work/Pump/Certificate.md";
  const { page } = await freshSession("bob");
  await expect(page.getByRole("heading", { name: "Your vaults" })).toBeVisible();
  // Work is shared with him, read only; Home is in neither of his lists.
  await expect(page.getByRole("list", { name: "Your vaults" }).getByRole("listitem")).toHaveText([/^Skills/, /^Welcome/]);
  await expect(page.getByRole("list", { name: /^Shared by/ }).getByRole("button", { name: /^Work.*Read only/ })).toBeVisible();
  await expect(page.getByRole("main").getByText("Home", { exact: true })).toHaveCount(0);

  await openNote(page, path);
  await expect(page.locator("article").getByText("The pump's certificate.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Write" })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Read" })).toHaveAttribute("aria-pressed", "true");

  const before = await aliceApi.getNote(path);
  const refused = await bobApi.send("PUT", `/api/notes/${encodePath(path)}`, { data: { content: "Changed by Bob.\n", version: before.version } });
  expect(refused.status()).toBe(403);
  expect((await aliceApi.getNote(path)).content, "the note is as it was").toBe(before.content);

  expect((await bobApi.send("GET", `/api/notes/${encodePath("/Home/Zahnarzt.md")}`)).ok(), "Home's note is not Bob's to read").toBe(false);
});

test("smoke #9: an edit in one tab shows in the other without a reload @smoke", async ({ page, seedAs, folder }) => {
  const path = `/Work/${folder}/Note.md`;
  await seedAs("tags/smoke/Note.md", path);
  await openNote(page, path);
  const other = await page.context().newPage();
  await openNote(other, path);
  await expect(other.locator("article").getByText("lighthouse")).toBeVisible();
  // A marker on the page itself: it is gone if the page is ever reloaded.
  await other.evaluate(() => ((window as unknown as { __mark: number }).__mark = 1));

  await write(page, "\nThe fog horn sounds twice.\n");

  // The other tab hears of it by itself and offers the new version: Refresh takes it, the page isn't reloaded.
  const bar = other.getByRole("region", { name: "Changed elsewhere" });
  await expect(bar.getByRole("status")).toContainText("You changed this note elsewhere");
  await bar.getByRole("button", { name: "Refresh" }).click();
  await expect(other.locator("article").getByText("The fog horn sounds twice.")).toBeVisible();
  expect(await other.evaluate(() => (window as unknown as { __mark?: number }).__mark), "no reload").toBe(1);
});

test("smoke #10: hidden Home leaves no trace in the tree, search or Recent changes; shown again with the PIN @smoke @serial", async ({ freshSession }, testInfo) => {
  const { page } = await freshSession("alice");
  await openNote(page, "/Work/Pump/Certificate.md");
  const tree = await openTree(page, testInfo);

  // The tree and its vault switcher.
  await expect(tree.getByRole("heading", { name: "Work", level: 2 })).toBeVisible();
  await expect(tree.getByText("Zahnarzt")).toHaveCount(0);
  await page.getByRole("button", { name: /^Switch vault/ }).click();
  const switcher = page.getByRole("dialog", { name: /vault/i });
  await expect(switcher.getByRole("button", { name: /^Work/ })).toBeVisible();
  await expect(switcher.getByText("Home")).toHaveCount(0);
  await page.keyboard.press("Escape");

  // Search.
  await page.getByRole("button", { name: /^Search/ }).click();
  const sheet = page.getByRole("dialog", { name: "Search" });
  await sheet.getByRole("searchbox", { name: "Search" }).fill("Zahnarzt");
  await expect(sheet.getByText("No notes found.")).toBeVisible();
  await expect(sheet.getByText("Zahnarzt")).toHaveCount(0);

  // Recent changes (paged, so asked for by name): nothing matches, and Home is no vault chip.
  const recent = async () => {
    await page.goto("/recent");
    await expect(page.getByRole("dialog", { name: "Recent changes" })).toBeVisible();
    await page.getByRole("searchbox", { name: "Find by name" }).fill("Zahnarzt");
  };
  await recent();
  await expect(page.getByRole("status")).toHaveText("Nothing matches that.");
  const chips = page.getByRole("group", { name: "Vaults" });
  await expect(chips.getByRole("button", { name: "Work" })).toBeVisible();
  await expect(chips.getByRole("button", { name: "Home" })).toHaveCount(0);

  // With the PIN Home is back: its switcher entry, its note in the search, in Recent changes and as a chip.
  await page.goto("/");
  try {
    await revealDiscreet(page);
    await page.getByRole("button", { name: /^Switch vault/ }).click();
    await expect(page.getByRole("dialog", { name: /vault/i }).getByRole("button", { name: /^Home/ })).toBeVisible();
    await page.keyboard.press("Escape");
    await page.getByRole("button", { name: /^Search/ }).click();
    await page.getByRole("dialog", { name: "Search" }).getByRole("searchbox", { name: "Search" }).fill("Zahnarzt");
    await expect(page.getByRole("dialog", { name: "Search" }).getByRole("button", { name: /Zahnarzt/ })).toBeVisible();
    await recent();
    await expect(page.getByRole("list", { name: "Recent changes" }).getByText("Zahnarzt")).toBeVisible();
    await expect(chips.getByRole("button", { name: "Home" })).toBeVisible();
    await page.goto("/");
    await hideDiscreet(page);
  } finally {
    await endHidden(page);
  }
});
