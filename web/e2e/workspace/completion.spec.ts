// SPDX-License-Identifier: AGPL-3.0-or-later
// Link completion (spec 2026-11-18-workspace-design.md §2; plan task 2.6): a heading and a block of another note
// after `[[Note#` and `[[Note#^`, a note's path after `](`, each inserted so that the link leads there in Read mode.
import type { Page } from "@playwright/test";
import type { Api } from "../support/api";
import { expect, test } from "../support/test";

const noteUrl = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;
const at = (path: string) => new RegExp(`${noteUrl(path).replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`);

// Names of the test's own: wiki links resolve vault-wide, and other tests have their Plan too.
const names = (folder: string) => {
  const plan = `Plan ${folder}`;
  return { plan, planPath: `/Work/${folder}/${plan}.md`, tasksPath: `/Work/${folder}/Tasks.md` };
};
// Enough text above Risks that a jump to it is seen: it starts below the fold.
const filler = Array.from({ length: 40 }, (_, i) => `Line ${i + 1} of the reasons.`).join("\n\n");
const planText = ["# Plan", "", "Why we do it. ^why", "", "## Goals", "", filler, "", "## Risks", "", "None yet."].join("\n");

async function seed(api: Api, folder: string) {
  const { planPath, tasksPath } = names(folder);
  await api.putNote(planPath, `${planText}\n`);
  await api.putNote(tasksPath, "# Tasks\n");
}

/** Tasks in Write mode, the caret at the end of its text. */
async function writeTasks(page: Page, folder: string) {
  await page.goto(noteUrl(names(folder).tasksPath));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  await source.focus();
  await page.keyboard.press("Control+End");
  return source;
}

async function done(page: Page) {
  await page.getByRole("button", { name: "Done" }).click();
  await expect(page.getByRole("button", { name: "Done" })).toHaveCount(0);
}

const list = (page: Page, name: string) => page.getByRole("listbox", { name });

test.afterEach(async ({ aliceApi, folder }) => {
  const { planPath, tasksPath } = names(folder);
  for (const path of [planPath, tasksPath]) await aliceApi.deleteNote(path).catch(() => undefined);
});

test("workspace completion #1: [[Note# lists that note's headings, and the link picked leads to the heading @workspace", async ({ page, aliceApi, folder }) => {
  await seed(aliceApi, folder);
  const { plan, planPath } = names(folder);
  const source = await writeTasks(page, folder);

  await page.keyboard.type(`\nSee [[${plan}#`);
  await expect(list(page, "Headings to link").getByRole("option")).toHaveText([/^Plan/, /^Goals/, /^Risks/]);
  await page.keyboard.type("ri");
  await expect(list(page, "Headings to link").getByRole("option")).toHaveText([/^Risks/]);
  await page.keyboard.press("Enter");
  await expect(source).toHaveValue(`# Tasks\n\nSee [[${plan}#Risks]]`);

  await done(page);
  await page.locator("article").getByRole("link", { name: `${plan} › Risks` }).click();
  await expect(page).toHaveURL(at(planPath));
  await expect(page.locator("article").getByRole("heading", { name: "Risks" })).toBeInViewport();
});

test("workspace completion #2: [[Note#^ lists the block ids that note has, and inserts the one picked @workspace", async ({ page, aliceApi, folder }) => {
  await seed(aliceApi, folder);
  const { plan, planPath } = names(folder);
  const source = await writeTasks(page, folder);

  await page.keyboard.type(`\n![[${plan}#^`);
  const option = list(page, "Blocks to link").getByRole("option");
  await expect(option).toHaveText(["Why we do it.^why"]);
  await option.click();
  await expect(source).toHaveValue(`# Tasks\n\n![[${plan}#^why]]`);
  await expect(source).toBeFocused();

  // The embed shows the block of the other note.
  await done(page);
  await expect(page.locator("article").getByText("Why we do it.")).toBeVisible();
  expect((await aliceApi.getNote(planPath)).content, "no id written into the other note").toBe(`${planText}\n`);
});

test("workspace completion #3: after ]( the notes are listed, and the Markdown link inserted opens the note @workspace", async ({ page, aliceApi, folder }) => {
  await seed(aliceApi, folder);
  const { plan, planPath } = names(folder);
  const source = await writeTasks(page, folder);

  await page.keyboard.type(`\nRead [the plan](${plan}`);
  await expect(list(page, "Notes to link").getByRole("option")).toHaveText([new RegExp(`^${plan}`)]);
  await page.keyboard.press("Enter");
  const destination = `/${folder}/${plan.replaceAll(" ", "%20")}.md`;
  await expect(source).toHaveValue(`# Tasks\n\nRead [the plan](${destination})`);

  // A heading of it after `#`, encoded.
  await page.keyboard.type(` and [its risks](${destination}#ri`);
  await expect(list(page, "Headings to link").getByRole("option")).toHaveText([/^Risks/]);
  await page.keyboard.press("Tab");
  await expect(source).toHaveValue(`# Tasks\n\nRead [the plan](${destination}) and [its risks](${destination}#Risks)`);

  await done(page);
  await page.locator("article").getByRole("link", { name: "the plan" }).click();
  await expect(page).toHaveURL(at(planPath));
  await expect(page.locator("article").getByRole("heading", { name: "Plan", exact: true })).toBeVisible();
});
