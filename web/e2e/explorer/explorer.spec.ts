// SPDX-License-Identifier: AGPL-3.0-or-later
// The explorer's search and order (flags report, hand-test checklist 10–13; spec §3).
import type { Locator, Page, TestInfo } from "@playwright/test";
import { encodePath } from "../support/api";
import { expect, onPhone, shot, test } from "../support/test";

const FILES = ["Pump/Certificate.md", "Pump/Valve.md", "Diary/Day.md"];

/** Seeds the three notes below the test's folder and opens the first in the app, so that Work's tree shows. */
async function open(page: Page, seedAs: (from: string, to: string) => Promise<string[]>, folder: string, files = FILES) {
  for (const file of files) await seedAs(`tags/explorer/${file}`, `/Work/${folder}/${file}`);
  await page.goto(`/notes/Work/${encodePath(`${folder}/Pump/Certificate.md`)}`);
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
  const notNow = page.getByRole("button", { name: "Not now" });
  if (await notNow.isVisible()) await notNow.click();
}

/** The file tree: docked on the desktop, a drawer on the phone. */
async function openTree(page: Page, testInfo: TestInfo): Promise<Locator> {
  if (!onPhone(testInfo)) return page.getByRole("complementary", { name: "File tree" });
  const drawer = page.getByRole("dialog", { name: "File tree" });
  if (!(await drawer.isVisible())) await page.getByRole("button", { name: "Open file tree" }).click();
  await expect(drawer).toBeVisible();
  return drawer;
}

/** A tree row of a folder (its name and count) or of a note (its title, a flag's name after it). */
const folderRow = (tree: Locator, name: string) => tree.getByRole("button", { name: new RegExp(`^${name}\\b`) });

/** Opens the tree's search and types `words`. */
async function find(tree: Locator, words: string) {
  await tree.getByRole("button", { name: "Find in this vault" }).click();
  await tree.getByRole("searchbox", { name: "Find by name" }).fill(words);
}

/** The titles of the rows below `folder`'s own row, as the tree nests them (the rows of other test folders stay out). */
async function rowsOf(tree: Locator, folder: string): Promise<string[]> {
  const all = await tree.locator("[data-tree-row]").evaluateAll((rows) =>
    rows.map((r) => ({ title: r.querySelector("span.truncate")?.textContent?.trim() ?? "", depth: parseInt((r.closest("li") as HTMLElement | null)?.style.paddingLeft ?? "0", 10) })),
  );
  const at = all.findIndex((r) => r.title === folder);
  if (at < 0) return [];
  const below: string[] = [];
  for (const row of all.slice(at + 1)) {
    if (row.depth <= all[at].depth) break;
    below.push(row.title);
  }
  return below;
}

test("flags #10: the tree search keeps only Pump › Certificate, opened @explorer @phone", async ({ page, seedAs, folder }, testInfo) => {
  await open(page, seedAs, folder);
  const tree = await openTree(page, testInfo);
  await find(tree, "pump cert");

  // Pump is opened by the search; the test folder is the way to it. Other test folders may show theirs: only this one's rows count.
  const mine = folderRow(tree, folder);
  await expect(mine).toHaveAttribute("aria-expanded", "true");
  await expect.poll(() => rowsOf(tree, folder)).toEqual(["Pump", "Certificate"]);
  await expect(tree.getByRole("button", { name: /^Pump\b/ }).first()).toHaveAttribute("aria-expanded", "true");
  await expect(tree.getByRole("button", { name: /^(Valve|Day|Diary)\b/ })).toHaveCount(0);
  await shot(page, testInfo, "explorer");
});

test("flags #11: flag chips narrow the tree; no match offers Search the contents @explorer", async ({ page, seedAs, folder }, testInfo) => {
  await open(page, seedAs, folder);
  const tree = await openTree(page, testInfo);
  // The test folder's name is the word, so that other folders' flagged notes stay out of it.
  await find(tree, folder);
  const chips = tree.getByRole("group", { name: "Find by flag" });

  await expect.poll(() => rowsOf(tree, folder)).toHaveLength(5); // Diary, Day, Pump, Certificate, Valve
  await chips.getByRole("button", { name: "Red" }).click();
  await expect(chips.getByRole("button", { name: "Red" })).toHaveAttribute("aria-pressed", "true");
  await expect.poll(() => rowsOf(tree, folder)).toEqual(["Pump", "Certificate"]);

  await chips.getByRole("button", { name: "Any flag" }).click();
  await expect.poll(() => rowsOf(tree, folder)).toEqual(["Pump", "Certificate", "Valve"]);
  await chips.getByRole("button", { name: "Any flag" }).click(); // lets go

  await tree.getByRole("searchbox", { name: "Find by name" }).fill("zzqxj nothing");
  await expect(tree.getByRole("status")).toHaveText("Nothing called that here.");
  await tree.getByRole("button", { name: "Search the contents…" }).click();
  const sheet = page.getByRole("dialog", { name: "Search" });
  await expect(sheet.getByRole("searchbox", { name: "Search" })).toHaveValue("zzqxj nothing");
});

test("flags #12: Escape and ✕ bring the full tree back @explorer @phone", async ({ page, seedAs, folder }, testInfo) => {
  await open(page, seedAs, folder);
  const tree = await openTree(page, testInfo);
  const search = tree.getByRole("search", { name: "Find in this vault" });
  const box = tree.getByRole("searchbox", { name: "Find by name" });
  const full = async () => {
    await expect(search).toHaveCount(0);
    await expect(tree.getByRole("button", { name: "Find in this vault" })).toBeVisible();
    // The vault's own rows again: the test folder is closed or open as before, and the other notes are not narrowed away.
    await expect(folderRow(tree, folder)).toBeVisible();
    await expect(folderRow(tree, "Pump").first()).toBeVisible();
  };

  await find(tree, "pump cert");
  await expect.poll(() => rowsOf(tree, folder)).toHaveLength(2);
  await box.press("Escape");
  await full();
  // The tree itself (the phone's drawer) stays: Escape closed the search alone.
  await expect(tree).toBeVisible();
  await expect.poll(() => rowsOf(tree, folder)).toEqual(["Diary", "Pump", "Certificate", "Valve"]);

  await find(tree, "pump cert");
  await expect.poll(() => rowsOf(tree, folder)).toHaveLength(2);
  await tree.getByRole("button", { name: "Close the search" }).click();
  await full();
  await expect.poll(() => rowsOf(tree, folder)).toEqual(["Diary", "Pump", "Certificate", "Valve"]);
});

test("flags #13: Last changed puts the latest folder first, folders above files, kept on reload @explorer @serial", async ({ page, aliceApi, seedAs, folder }, testInfo) => {
  // Work/<folder>: Pump (2 notes) and Diary (1) as folders, and a file of its own; Diary is written last.
  await seedAs("tags/explorer/Pump/Certificate.md", `/Work/${folder}/Pump/Certificate.md`);
  await seedAs("tags/explorer/Pump/Valve.md", `/Work/${folder}/Pump/Valve.md`);
  await aliceApi.putNote(`/Work/${folder}/Index.md`, "# Index\n");
  await seedAs("tags/explorer/Diary/Day.md", `/Work/${folder}/Diary/Day.md`);
  await page.goto(`/notes/Work/${encodePath(`${folder}/Index.md`)}`);
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
  const notNow = page.getByRole("button", { name: "Not now" });
  if (await notNow.isVisible()) await notNow.click();
  const tree = await openTree(page, testInfo);
  const order = tree.getByRole("radiogroup", { name: "Order" });

  try {
    await expect(order.getByRole("radio", { name: "Name" })).toBeChecked();
    // The test folder holds the latest change anywhere in Work, so it is first among Work's folders on Last changed.
    await order.getByText("Last changed").click();
    await expect(order.getByRole("radio", { name: "Last changed" })).toBeChecked();
    const firstFolder = async () => (await tree.locator("[data-tree-row][aria-expanded]").first().textContent())?.trim() ?? "";
    await expect.poll(firstFolder).toMatch(new RegExp(`^${folder}`));

    // Inside it: Diary (written last) before Pump, both folders above the file Index.
    const mine = folderRow(tree, folder);
    if ((await mine.getAttribute("aria-expanded")) !== "true") await mine.click();
    await expect.poll(async () => (await rowsOf(tree, folder)).slice(0, 3)).toEqual(["Diary", "Pump", "Index"]);

    // Kept on this device after a reload, and for another vault.
    await page.reload();
    await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
    if (await page.getByRole("button", { name: "Not now" }).isVisible()) await page.getByRole("button", { name: "Not now" }).click();
    const again = await openTree(page, testInfo);
    await expect(again.getByRole("radiogroup", { name: "Order" }).getByRole("radio", { name: "Last changed" })).toBeChecked();
    await expect.poll(async () => (await again.locator("[data-tree-row][aria-expanded]").first().textContent())?.trim() ?? "").toMatch(new RegExp(`^${folder}`));

    await page.goto("/notes/Welcome");
    await expect(page.getByRole("complementary", { name: "File tree" }).or(page.getByRole("button", { name: "Open file tree" })).first()).toBeVisible();
    const other = await openTree(page, testInfo);
    await expect(other.getByRole("button", { name: /Switch vault: Welcome/ })).toBeVisible();
    await expect(other.getByRole("radiogroup", { name: "Order" }).getByRole("radio", { name: "Last changed" })).toBeChecked();
  } finally {
    // Back to Name: the choice is the device's, and the other tests expect the default.
    await page.evaluate(() => localStorage.removeItem("kumbu.treeOrder")).catch(() => {});
  }
});
