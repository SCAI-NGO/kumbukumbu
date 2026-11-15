// SPDX-License-Identifier: AGPL-3.0-or-later
// A menu in the tree (F-02 of the UI tests report, fixes spec §2.7): it closes when the person scrolls, never when the
// tree scrolls the open note into view by itself.
import type { Locator, Page, TestInfo } from "@playwright/test";
import { cleanFolder, dismissInstallHint, notePage } from "../calendar/helpers";
import { expect, onPhone, test } from "../support/test";

/** The file tree: docked on the desktop, a drawer on the phone. */
async function openTree(page: Page, testInfo: TestInfo): Promise<Locator> {
  if (!onPhone(testInfo)) return page.getByRole("complementary", { name: "File tree" });
  await page.getByRole("button", { name: "Open file tree" }).click();
  return page.getByRole("dialog", { name: "File tree" });
}

/** The tree's scrolling list, the nearest ancestor of the open note's row that scrolls. */
const scrollerOf = (tree: Locator) =>
  tree.evaluate((t) => {
    let at = t.querySelector<HTMLElement>('[aria-current="page"]');
    while (at && !(at.scrollHeight > at.clientHeight && /auto|scroll/.test(getComputedStyle(at).overflowY))) at = at.parentElement;
    if (!at) throw new Error("No scrolling list around the open note's row");
    at.dataset.e2eScroller = "";
  });

/**
 * A scroll of the tree's list that no person makes, as the tree's own scrolling to the open note: `to` the bottom or
 * the top. Returns the list's position once two reads a frame apart agree.
 */
const scrollTree = (page: Page, to: "bottom" | "top") =>
  page.evaluate(async (to) => {
    const list = document.querySelector<HTMLElement>("[data-e2e-scroller]")!;
    list.scrollTop = to === "bottom" ? list.scrollHeight : 0;
    for (;;) {
      const before = list.scrollTop;
      await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
      if (list.scrollTop === before) return before;
    }
  }, to);

test.afterEach(async ({ aliceApi, folder }) => {
  await cleanFolder(aliceApi, folder);
});

test("smoke #F-02: a tree menu opened as the tree scrolls to the open note stays open; the person's scroll closes it @smoke @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const deep = `/Work/${folder}/Deep`;
  for (let n = 1; n <= 40; n++) await aliceApi.putNote(`${deep}/Note ${String(n).padStart(2, "0")}.md`, `# Note ${n}\n`);
  await dismissInstallHint(page);

  await page.goto(notePage(`${deep}/Note 40.md`));
  const tree = await openTree(page, testInfo);
  // At once, while the tree may still be scrolling to the open note.
  await tree.getByRole("button", { name: "Actions for Deep" }).click();
  const menu = page.getByRole("menu", { name: "Actions for Deep" });
  await expect(menu).toBeVisible();
  await expect(tree.locator('[aria-current="page"]')).toHaveAccessibleName("Note 40");

  // Scrolls the person doesn't make: to the open note at the bottom, then back to the top.
  await scrollerOf(tree);
  expect(await scrollTree(page, "bottom"), "the tree scrolled down").toBeGreaterThan(0);
  await expect(menu).toBeVisible();
  expect(await scrollTree(page, "top"), "the tree scrolled back to its top").toBe(0);
  await expect(menu).toBeVisible();

  // The person's wheel over the tree, at its left edge, away from the menu (which stands at the right, under its
  // button); the wheel reaches the page under a phone's touch too.
  const list = (await page.locator("[data-e2e-scroller]").boundingBox())!;
  await page.mouse.move(list.x + 12, Math.min(list.y + list.height, page.viewportSize()!.height) - 30);
  await page.mouse.wheel(0, 300);
  await expect(menu).toBeHidden();
});
