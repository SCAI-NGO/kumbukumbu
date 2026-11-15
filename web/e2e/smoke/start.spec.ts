// SPDX-License-Identifier: AGPL-3.0-or-later
// The app starts at the last note (fixes spec 2026-11-14-0-5-0-fixes-design.md §2.5), and Ctrl+S in Write mode (§2.6).
import type { Locator, Page, TestInfo } from "@playwright/test";
import { cleanFolder, dismissInstallHint, notePage } from "../calendar/helpers";
import { endHidden, revealDiscreet } from "../support/session";
import { expect, onPhone, test } from "../support/test";

/** Opens a note by its address and waits until it is shown. */
async function openNote(page: Page, path: string) {
  await page.goto(notePage(path));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
  await expect(page.locator("article")).toBeVisible();
}

/** The file tree: docked on the desktop, a drawer on the phone. */
async function openTree(page: Page, testInfo: TestInfo): Promise<Locator> {
  if (!onPhone(testInfo)) return page.getByRole("complementary", { name: "File tree" });
  await page.getByRole("button", { name: "Open file tree" }).click();
  return page.getByRole("dialog", { name: "File tree" });
}

/** The device's kept last notes, `[key, path]`. */
const keptNotes = (page: Page) =>
  page.evaluate(() => Object.entries(localStorage).filter(([key]) => key.startsWith("kumbu.lastNote.")));

test.beforeEach(async ({ page }) => {
  await dismissInstallHint(page);
});

test.afterEach(async ({ aliceApi, folder }) => {
  await cleanFolder(aliceApi, folder);
});

test("fixes §2.5 #1: the last note opens on start, with the tree at it @smoke @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const path = `/Work/${folder}/Plan.md`;
  await aliceApi.putNote(path, "# Plan\n\nWhat the week is for.\n");
  await openNote(page, path);

  // A new start: the page loads again, and the app's memory of what was open is gone.
  await page.goto("/");
  await expect(page).toHaveURL(new RegExp(`${notePage(path).replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`));
  await expect(page.locator("article").getByText("What the week is for.")).toBeVisible();
  const tree = await openTree(page, testInfo);
  // The tree at it, as for any note opened by its address: its folder open, its row the current one.
  await expect(tree.getByRole("button", { name: new RegExp(`^${folder}\\b`) })).toHaveAttribute("aria-expanded", "true");
  const row = tree.locator('[aria-current="page"]');
  await expect(row).toHaveAccessibleName("Plan");
  await expect(row).toBeVisible();
});

test("fixes §2.5 #2: a note that is gone opens its vault's root @smoke", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Plan.md`;
  await aliceApi.putNote(path, "# Plan\n");
  await openNote(page, path);
  await aliceApi.deleteNote(path);

  await page.goto("/");
  const tree = page.getByRole("complementary", { name: "File tree" });
  // Work's root, not the vault list's first vault (Skills): the kept note was read, and is forgotten.
  await expect(tree.getByRole("heading", { level: 2, name: "Work" })).toBeVisible();
  await expect.poll(() => new URL(page.url()).pathname).toBe("/");
  await expect.poll(() => keptNotes(page)).toEqual([]);
});

test("fixes §2.5 #3: a discreet note is never kept, and sign-out forgets the last note @smoke @serial", async ({ freshSession, aliceApi, folder }) => {
  // A session of its own: signing out ends it, not the one every other test shares.
  const { page } = await freshSession("alice");
  await dismissInstallHint(page);
  const work = `/Work/${folder}/Plan.md`;
  await aliceApi.putNote(work, "# Plan\n");
  await openNote(page, work);
  await expect.poll(() => keptNotes(page)).toEqual([[expect.stringMatching(/^kumbu\.lastNote\.\S+$/), work]]);

  try {
    await revealDiscreet(page);
    await openNote(page, "/Home/Zahnarzt.md");
    await expect(page.locator("article").getByText("Zahnarzt").first()).toBeVisible();
    // Still the Work note, and nothing in the browser's storage names Home.
    expect(await keptNotes(page)).toEqual([[expect.stringMatching(/^kumbu\.lastNote\./), work]]);
    const stored = await page.evaluate(() => Object.entries(localStorage).flat().join("\n"));
    expect(stored).not.toContain("Home");
    expect(stored).not.toContain("Zahnarzt");
  } finally {
    await endHidden(page);
  }

  await openNote(page, work);
  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page).toHaveURL(/\/signin/);
  expect(await keptNotes(page)).toEqual([]);
});

test("fixes §2.6: Ctrl+S in Write mode saves and goes back to Read mode @smoke", async ({ page, aliceApi, folder }, testInfo) => {
  test.skip(onPhone(testInfo), "a keyboard's shortcut");
  const path = `/Work/${folder}/Note.md`;
  await aliceApi.putNote(path, "# Note\n");
  await openNote(page, path);

  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  await source.fill(`${await source.inputValue()}\nLighthouse.\n`);
  // Whether the key reached the page's default (the browser's Save page) is read after every handler had its turn.
  await page.evaluate(() => {
    const seen = window as unknown as { __saveKey?: boolean };
    window.addEventListener("keydown", (e) => {
      if (e.key.toLowerCase() === "s") seen.__saveKey = e.defaultPrevented;
    });
  });
  await page.keyboard.press("ControlOrMeta+s");

  await expect(page.getByRole("button", { name: "Done" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Write" })).toBeVisible();
  await expect(page.locator("article").getByText("Lighthouse.")).toBeVisible();
  expect((await aliceApi.getNote(path)).content).toContain("Lighthouse.");
  expect(await page.evaluate(() => (window as unknown as { __saveKey?: boolean }).__saveKey), "the browser's Save page doesn't open").toBe(true);
});

test("fixes §2.6: Ctrl+S on a save that meets a change elsewhere stays in Write mode @smoke", async ({ page, aliceApi, folder }, testInfo) => {
  test.skip(onPhone(testInfo), "a keyboard's shortcut");
  const path = `/Work/${folder}/Note.md`;
  await aliceApi.putNote(path, "# Note\n");
  await openNote(page, path);

  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  // Changed elsewhere while this tab writes.
  await aliceApi.putNote(path, "# Note\n\nWritten elsewhere.\n");
  await source.fill(`${await source.inputValue()}\nLighthouse.\n`);
  await page.keyboard.press("ControlOrMeta+s");

  await expect(page.getByRole("button", { name: "Done" })).toBeVisible();
  await expect(source).toHaveValue(/Lighthouse\./);
  expect((await aliceApi.getNote(path)).content, "nothing written over the other change").toBe("# Note\n\nWritten elsewhere.\n");
});
