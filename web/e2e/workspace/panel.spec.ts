// SPDX-License-Identifier: AGPL-3.0-or-later
// The side panel (spec 2026-11-18-workspace-design.md §1; plan task 1.7): beside the note from 1280 px, with its
// links and its outline; below 1280 px everything as before. The desktop project is 1280 px wide, the breakpoint.
import type { Page } from "@playwright/test";
import type { Api } from "../support/api";
import { expect, test } from "../support/test";

const noteUrl = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;

// Names of the test's own: wiki links resolve vault-wide, and other tests have their Plan too.
const names = (folder: string) => {
  const plan = `Plan ${folder}`;
  return { plan, planPath: `/Work/${folder}/${plan}.md`, tasksPath: `/Work/${folder}/Tasks.md` };
};
const planText = ["# Plan", "", "Why we do it.", "", "## Goals", "", "- one", "", "## Risks", "", "None yet."].join("\n");

/** Plan, and Tasks linking to it. */
async function seed(api: Api, folder: string) {
  const { plan, planPath, tasksPath } = names(folder);
  await api.putNote(planPath, `${planText}\n`);
  await api.putNote(tasksPath, `# Tasks\n\n- [ ] finish the [[${plan}]]\n`);
  return { planPath };
}

async function openNote(page: Page, path: string) {
  await page.goto(noteUrl(path));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

const sidePanel = (page: Page) => page.getByRole("complementary", { name: "Side panel" });
const toggle = (page: Page) => page.getByRole("button", { name: "Side panel" });
const outlineEntries = (page: Page) => sidePanel(page).getByRole("tabpanel", { name: "Outline" }).getByRole("button");

test.afterEach(async ({ aliceApi, folder }) => {
  const { planPath, tasksPath } = names(folder);
  for (const path of [planPath, tasksPath]) await aliceApi.deleteNote(path).catch(() => undefined);
});

test("workspace #1: at 1280 px the panel is beside the note, with Linked from in it and not at the note's bottom @workspace", async ({ page, aliceApi, folder }) => {
  const { planPath } = await seed(aliceApi, folder);
  await openNote(page, planPath);

  const panel = sidePanel(page);
  await expect(panel).toBeVisible();
  await expect(toggle(page)).toHaveAttribute("aria-expanded", "true");
  const linked = panel.getByRole("region", { name: "Linked from" });
  await expect(linked.getByRole("heading", { name: "Linked from · 1" })).toBeVisible();
  await expect(linked.getByRole("button", { name: new RegExp(`^Tasks\\s*Work / ${folder}$`) })).toBeVisible();
  // One place only: not below the note as well.
  await expect(page.getByRole("region", { name: "Linked from" })).toHaveCount(1);
  // Beside the note: right of the article, its top level with the note's area.
  const article = (await page.locator("article").first().boundingBox())!;
  const box = (await panel.boundingBox())!;
  expect(box.x, "right of the note").toBeGreaterThanOrEqual(article.x + article.width);
  expect(Math.round(box.width), "300 px wide").toBe(300);
  // The outline is the panel's: no Outline button in the top bar.
  await expect(page.getByRole("banner").getByRole("button", { name: "Outline" })).toHaveCount(0);
});

test("workspace #2: the Outline tab lists the note's headings and jumps to one @workspace", async ({ page, aliceApi, folder }) => {
  const { planPath } = await seed(aliceApi, folder);
  await openNote(page, planPath);

  await sidePanel(page).getByRole("tab", { name: "Outline" }).click();
  await expect(sidePanel(page).getByRole("tab", { name: "Outline" })).toHaveAttribute("aria-selected", "true");
  await expect(outlineEntries(page)).toHaveText(["Plan", "Goals", "Risks"]);
  await outlineEntries(page).filter({ hasText: "Risks" }).click();
  await expect(page.locator("article").getByRole("heading", { name: "Risks" })).toBeInViewport();
});

test("workspace #3: in Write mode the outline follows the unsaved text, and a heading puts the caret on its line @workspace", async ({ page, aliceApi, folder }) => {
  const { planPath } = await seed(aliceApi, folder);
  await openNote(page, planPath);
  await sidePanel(page).getByRole("tab", { name: "Outline" }).click();

  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  await source.focus();
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\n\n## Fresh heading");
  // Shown from the editor's text, before any save could have answered.
  await expect(outlineEntries(page)).toHaveText(["Plan", "Goals", "Risks", "Fresh heading"]);
  await expect(outlineEntries(page).filter({ hasText: "Fresh heading" })).toHaveAttribute("aria-current", "location");

  await outlineEntries(page).filter({ hasText: "Goals" }).click();
  await expect(source).toBeFocused();
  const caret = await source.evaluate((area: HTMLTextAreaElement) => ({ start: area.selectionStart, end: area.selectionEnd, line: area.value.indexOf("## Goals") }));
  expect(caret.start, "the caret at the start of the heading's line").toBe(caret.line);
  expect(caret.end).toBe(caret.line);
  await expect(outlineEntries(page).filter({ hasText: "Goals" })).toHaveAttribute("aria-current", "location");
});

test("workspace #4: Ctrl+Shift+\\ hides and shows the panel, and a reload keeps it hidden @workspace", async ({ page, aliceApi, folder }) => {
  const { planPath } = await seed(aliceApi, folder);
  await openNote(page, planPath);
  await expect(sidePanel(page)).toBeVisible();

  await page.keyboard.press("Control+Shift+Backslash");
  await expect(toggle(page)).toHaveAttribute("aria-expanded", "false");
  await expect(sidePanel(page)).toHaveCount(0);
  // Hidden, Linked from doesn't move to the note's bottom.
  await expect(page.getByRole("region", { name: "Linked from" })).toHaveCount(0);

  await page.reload();
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
  await expect(toggle(page)).toHaveAttribute("aria-expanded", "false");
  await expect(sidePanel(page)).toHaveCount(0);

  await page.keyboard.press("Control+Shift+Backslash");
  await expect(toggle(page)).toHaveAttribute("aria-expanded", "true");
  await expect(sidePanel(page).getByRole("region", { name: "Linked from" })).toBeVisible();
  await page.reload();
  await expect(sidePanel(page)).toBeVisible();
});

test("workspace #5: at 1024 px there is no panel, and Linked from is at the note's bottom @workspace", async ({ page, aliceApi, folder }) => {
  await page.setViewportSize({ width: 1024, height: 800 });
  const { planPath } = await seed(aliceApi, folder);
  await openNote(page, planPath);

  const linked = page.getByRole("region", { name: "Linked from" });
  await expect(linked.getByRole("heading", { name: "Linked from · 1" })).toBeVisible();
  await expect(sidePanel(page)).toHaveCount(0);
  await expect(toggle(page)).toHaveCount(0);
  const article = (await page.locator("article").first().boundingBox())!;
  expect((await linked.boundingBox())!.y, "below the note's text").toBeGreaterThan(article.y + article.height - 1);
  // The outline stays the top bar's sheet.
  await page.getByRole("banner").getByRole("button", { name: "Outline" }).click();
  await expect(page.getByRole("dialog", { name: "Outline" })).toBeVisible();
});
