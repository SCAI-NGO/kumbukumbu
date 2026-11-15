// SPDX-License-Identifier: AGPL-3.0-or-later
// The properties box folds where the header already says it in words (fixes spec 2026-11-14-0-5-0-fixes-design.md §2.1).
import type { Page } from "@playwright/test";
import { addDays, today } from "../support/time";
import { cleanFolder, dayAndMonth, dismissInstallHint, entryLine, expect, note, notePage, test } from "../calendar/helpers";

/** The fold's line, "Properties · N". */
const toggle = (page: Page, count?: number) => page.getByRole("button", { name: count === undefined ? /^Properties ·/ : `Properties · ${count}`, exact: count !== undefined });
/** The box itself. */
const box = (page: Page) => page.locator("article").getByLabel("Properties", { exact: true });

/** Opens the note and waits for its header: the box folds once the note's place in the calendar is known. */
async function openNote(page: Page, path: string, title: string) {
  await page.goto(notePage(path));
  await expect(page.locator("article").getByRole("heading", { level: 1, name: title })).toBeVisible();
}

test.beforeEach(async ({ page }) => {
  await dismissInstallHint(page);
});

test.afterEach(async ({ aliceApi, folder }) => {
  await cleanFolder(aliceApi, folder);
});

test("fixes §2.1 #1: a calendar note's properties fold to Properties · 4; a tap opens them, kept on reload @calendar @phone", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Certificate.md`;
  const when = addDays(today(), 20);
  await aliceApi.putNote(path, note({ when, task: "open", upcoming: "7d", tags: "[pump]" }, "# Certificate\n\nThe pump's certificate.\n"));

  await openNote(page, path, "Certificate");
  await expect(entryLine(page)).toBeVisible();
  await expect(toggle(page, 4)).toHaveAttribute("aria-expanded", "false");
  await expect(box(page)).toBeHidden();

  await toggle(page, 4).click();
  await expect(toggle(page, 4)).toHaveAttribute("aria-expanded", "true");
  await expect(box(page)).toBeVisible();
  await expect(box(page)).toContainText(when);

  // One choice for the device: open after a reload too, and folded again once folded.
  await page.reload();
  await expect(toggle(page, 4)).toHaveAttribute("aria-expanded", "true");
  await expect(box(page)).toBeVisible();
  await toggle(page, 4).click();
  await expect(box(page)).toBeHidden();
  await page.reload();
  await expect(toggle(page, 4)).toHaveAttribute("aria-expanded", "false");
  await expect(box(page)).toBeHidden();
});

test("fixes §2.1 #2: a flagged note folds; an ordinary note keeps its box without a toggle @flags @phone", async ({ page, aliceApi, folder }) => {
  const flagged = `/Work/${folder}/Valve.md`;
  const plain = `/Work/${folder}/Pump.md`;
  await aliceApi.putNote(flagged, note({ owner: "Alice", tags: "[pump, flag/red]" }, "# Valve\n"));
  await aliceApi.putNote(plain, note({ owner: "Alice", tags: "[pump]" }, "# Pump\n"));

  await openNote(page, flagged, "Valve");
  await expect(toggle(page, 2)).toHaveAttribute("aria-expanded", "false");
  await expect(box(page)).toBeHidden();

  await openNote(page, plain, "Pump");
  await expect(box(page)).toBeVisible();
  await expect(box(page)).toContainText("owner");
  await expect(toggle(page)).toHaveCount(0);
});

// Serial: a vault's board lives at /Work/Board/, where calendar #16 makes and removes one.
test("fixes §2.1 #3: a board card says In In progress since <day> and folds @flags @serial", async ({ page, aliceApi, folder }) => {
  const card = `/Work/Board/${folder}.md`;
  const since = today();
  try {
    await aliceApi.putNote(card, note({ column: "In progress", rank: "1", moved: `{ In progress: ${since} }` }, "# Pump card\n"));
    await openNote(page, card, "Pump card");
    await expect(page.getByRole("group", { name: "On the board" })).toHaveText(`In In progress since ${dayAndMonth(since)}`);
    await expect(toggle(page, 3)).toHaveAttribute("aria-expanded", "false");
    await expect(box(page)).toBeHidden();
  } finally {
    await aliceApi.deleteNote(card).catch(() => {});
  }
});

test("fixes §2.1 #4: when an agent changes the frontmatter, the box shows open with its tint @calendar", async ({ page, aliceApi, mcp, folder }) => {
  const path = `/Work/${folder}/Certificate.md`;
  const t = today();
  await aliceApi.putNote(path, note({ when: addDays(t, 20), task: "open" }, "# Certificate\n"));
  await openNote(page, path, "Certificate");
  await expect(toggle(page, 2)).toHaveAttribute("aria-expanded", "false");

  const moved = await mcp.sh(`calendar move "${path}" ${addDays(t, 21)}`);
  expect(moved.isError, moved.text).toBe(false);
  // The page hears of the write by itself; Refresh takes it, and the change is tinted.
  await page.getByRole("button", { name: "Refresh" }).click();
  await expect(box(page)).toBeVisible();
  await expect(box(page)).toHaveAttribute("data-changed", "true");
  await expect(box(page)).toContainText(addDays(t, 21));
  // Nothing to fold while it shows a change.
  await expect(toggle(page)).toHaveCount(0);
});

test("fixes §2.1 #5: frontmatter that can't be read stays as written @calendar", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Nested.md`;
  const when = addDays(today(), 20);
  await aliceApi.putNote(path, `---\nwhen: ${when}\ntask: open\nowner:\n  name: Alice\n---\n# Nested\n`);
  await openNote(page, path, "Nested");
  await expect(entryLine(page)).toBeVisible();
  await expect(box(page)).toBeVisible();
  await expect(box(page)).toHaveJSProperty("tagName", "PRE");
  await expect(box(page)).toContainText("  name: Alice");
  await expect(toggle(page)).toHaveCount(0);
});
