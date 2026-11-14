// SPDX-License-Identifier: AGPL-3.0-or-later
// Take photo (the flags report's hand-test checklist, steps 18-19; spec 2026-11-14-flags-explorer-photo-design.md §5).
// The real camera needs a device and is left out: the file chooser stands in for it.
import type { Locator, Page, TestInfo } from "@playwright/test";
import { join } from "node:path";
import { fixturesDir } from "../support/env";
import { expect, onPhone, shot, test } from "../support/test";
import { today } from "../support/time";

const whiteboard = join(fixturesDir, "tags", "photo", "whiteboard.jpg");

/** Opens a note by its address; the install hint, which covers the phone's controls, is answered with Not now. */
async function openNote(page: Page, path: string) {
  const hint = page.getByRole("region", { name: "Install Kumbu² as an app" });
  await page.addLocatorHandler(hint, async () => {
    await hint.getByRole("button", { name: "Not now" }).click();
  });
  await page.goto(`/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`);
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

/** The file tree: docked on the desktop, a drawer on the phone. */
async function openTree(page: Page, testInfo: TestInfo): Promise<Locator> {
  if (!onPhone(testInfo)) return page.getByRole("complementary", { name: "File tree" });
  await page.getByRole("button", { name: "Open file tree" }).click();
  return page.getByRole("dialog", { name: "File tree" });
}

/** Opens the ⋯ menu of the folder `name` in the tree; F-02: it closes while the tree still scrolls to the open note, so it is opened again if so. */
async function folderMenu(page: Page, tree: Locator, name: string) {
  const menu = page.getByRole("menu", { name: `Actions for ${name}` });
  await expect(async () => {
    // Not Escape: on the phone it would close the drawer too.
    if (!(await menu.isVisible())) await tree.getByRole("button", { name: `Actions for ${name}` }).click();
    await expect(menu.getByRole("menuitem").first()).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 10_000 });
  return menu;
}

test("flags #18: Take photo sits beside Upload here on the phone @photo @phone", async ({ page, seedAs, folder }, testInfo) => {
  await seedAs("tags/photo/Meetings/Agenda.md", `/Work/${folder}/Meetings/Agenda.md`);
  await openNote(page, `/Work/${folder}/Meetings/Agenda.md`);

  const menu = await folderMenu(page, await openTree(page, testInfo), "Meetings");
  await expect(menu.getByRole("menuitem", { name: "Upload here" })).toBeVisible();
  if (onPhone(testInfo)) {
    await expect(menu.getByRole("menuitem", { name: "Take photo" })).toBeVisible();
    // Beside Upload here: the next item.
    const items = (await menu.getByRole("menuitem").allInnerTexts()).map((t) => t.trim());
    expect(items.indexOf("Take photo")).toBe(items.indexOf("Upload here") + 1);
    await shot(page, testInfo, "photo");
  } else {
    await expect(menu.getByRole("menuitem", { name: /photo/i })).toHaveCount(0);
  }
});

test("flags #18: Bob is not offered Take photo @photo @phone", async ({ bobPage }, testInfo) => {
  await openNote(bobPage, "/Work/Pump/Certificate.md");
  const tree = await openTree(bobPage, testInfo);
  // Work's folders from the setup: a reader has no menu on a folder, so no Take photo and no Upload here.
  await expect(tree.getByRole("button", { name: /^Pump\b/ })).toBeVisible();
  await expect(tree.getByRole("button", { name: "Actions for Pump" })).toHaveCount(0);
  // Only the vault's own menu is left (Work's root); it offers neither.
  await tree.getByRole("button", { name: "Actions for Work" }).click();
  const menu = bobPage.getByRole("menu", { name: "Actions for Work" });
  await expect(menu.getByRole("menuitem").first()).toBeVisible();
  await expect(menu.getByRole("menuitem", { name: /photo|upload/i })).toHaveCount(0);
  await expect(bobPage.getByText(/Take photo/)).toHaveCount(0);
});

test("flags #19: three photos land as Photo <date> <time>.jpg with the upload's confirmation @photo @phone", async ({ page, aliceApi, seedAs, folder }, testInfo) => {
  test.skip(!onPhone(testInfo), "Take photo is offered on touch devices only");
  await seedAs("tags/photo/Meetings/Agenda.md", `/Work/${folder}/Meetings/Agenda.md`);
  // The date is frozen (the clock keeps running for everything else) so that the three photos fall into one second and
  // the second and third find their name taken; the server's time doesn't matter for a file's name.
  const now = new Date();
  const day = today(now);
  await page.clock.setFixedTime(now);
  await openNote(page, `/Work/${folder}/Meetings/Agenda.md`);

  for (let n = 1; n <= 3; n++) {
    const menu = await folderMenu(page, await openTree(page, testInfo), "Meetings");
    const chooser = page.waitForEvent("filechooser");
    await menu.getByRole("menuitem", { name: "Take photo" }).click();
    await (await chooser).setFiles(whiteboard);

    const sheet = page.getByRole("dialog", { name: "Upload" });
    await expect(sheet.getByRole("status")).toHaveText("0 notes and 1 file uploaded, 0 replaced, 0 files skipped");
    await sheet.getByRole("button", { name: "Done" }).click();
    await expect(sheet).toHaveCount(0);
  }

  const listing = await aliceApi.console(`ls /Work/${folder}/Meetings`);
  const photos = listing.text.split("\n").map((name) => name.trim()).filter((name) => name.endsWith(".jpg")).sort();
  expect(photos).toHaveLength(3);
  for (const name of photos) expect(name).toMatch(new RegExp(`^Photo ${day} \\d{6}( [23])?\\.jpg$`));
  const stem = photos[0].replace(/\.jpg$/, "");
  expect(photos).toEqual([`${stem}.jpg`, `${stem} 2.jpg`, `${stem} 3.jpg`]);
});
