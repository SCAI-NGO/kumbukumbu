// SPDX-License-Identifier: AGPL-3.0-or-later
// What the Mermaid and parity tests share (the mermaid report's checklist; spec 2026-11-12-mermaid-and-parity-design.md):
// the spec's example below the test's folder, the way to a note, and the clean-up of the folder.
import type { Locator, Page } from "@playwright/test";
import type { Api } from "../support/api";
import { test as base, expect } from "../support/test";

export const test = base;

export { expect };

/** The vault paths of the example's notes below the test's folder. */
export const example = (folder: string) => ({
  plan: `/Work/${folder}/Pump/Plan.md`,
  ruling: `/Work/${folder}/Pump/Ruling.md`,
  tasks: `/Work/${folder}/Pump/Tasks.md`,
  wide: `/Work/${folder}/Pump/Wide.md`,
  diary: `/Work/${folder}/Diary/2026-11-12.md`,
});

/** Seeds the example (fixtures/tags/mermaid) below `/Work/<folder>/`. */
export async function seedExample(seedAs: (from: string, to: string) => Promise<string[]>, folder: string) {
  await seedAs("tags/mermaid", `/Work/${folder}`);
  return example(folder);
}

/** The note's address in the app: `/Work/x/Note.md` → `/notes/Work/x/Note.md`. */
export const noteUrl = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;

/** Opens a note by its address; on the phone the install hint is answered with Not now, as a person would, whenever it shows. */
export async function openNote(page: Page, path: string) {
  const hint = page.getByRole("region", { name: "Install Kumbu² as an app" });
  await page.addLocatorHandler(hint, async () => {
    await hint.getByRole("button", { name: "Not now" }).click();
  });
  await page.goto(noteUrl(path));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

/** The rendered note. */
export const article = (page: Page) => page.locator("article").first();

/** The block-id paragraph of Plan, as the read view shows it. */
export const controllerLine = (page: Page) => article(page).getByText("The pump needs a new controller.", { exact: true });

/** Plan's diagram, drawn. */
export const diagram = (scope: Page | Locator) => scope.getByRole("img", { name: "Diagram (flowchart)" });

/** The image has loaded and has a size of its own. */
export async function expectLoaded(image: Locator) {
  await expect(image).toBeVisible();
  await expect.poll(() => image.evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth), "the image loaded").toBeGreaterThan(0);
}

/** The SVG behind a diagram's `blob:` image, as text: what it draws and in which colours. */
export const svgOf = (image: Locator) => image.evaluate(async (i: HTMLImageElement) => (await fetch(i.src)).text());

/** Mermaid's `default` theme fills a flowchart's nodes with #ECECFF; its `dark` theme doesn't use it. */
export const lightFill = /#ececff/i;

/** The Unlinked mentions section and its disclosure button. */
export function mentions(page: Page) {
  const section = page.getByRole("region", { name: "Unlinked mentions" });
  return { section, button: section.getByRole("button", { name: "Unlinked mentions" }) };
}

/** The mention row of this test's diary (the list is vault-wide): its folder, as the row shows it, is the test's. */
export const diaryRow = (section: Locator, folder: string) => section.getByRole("listitem").filter({ hasText: `Work / ${folder} / Diary` }).getByRole("button");

/** The block a note was opened at is lit (NoteView's `k-flash`). */
export const lit = /\bk-flash\b/;

/** No sideways scroll of the page. */
export const pageScrollsSideways = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);

/**
 * Every note below `/Work/<folder>/` goes to the trash, at each test's end (each spec's `afterEach`): the diary is a day
 * note of 12 November anywhere in the vault, and the unlinked mentions and the calendar are vault-wide.
 */
export async function cleanFolder(api: Api, folder: string) {
  const listed = await api.console(`rg --files "/Work/${folder}"`);
  if (listed.isError) return;
  for (const path of listed.text.split("\n").map((l) => l.trim()).filter((l) => l.startsWith("/Work/"))) {
    await api.deleteNote(path).catch(() => {});
  }
}
