// SPDX-License-Identifier: AGPL-3.0-or-later
// The calendar's way to the weeks and months around (fixes spec 2026-11-14-0-5-0-fixes-design.md §2.3). Each test
// narrows the view to a tag of its own, which no note carries: its pages stay empty and alike.
import type { Locator, Page } from "@playwright/test";
import { addDays, addMonths, isoWeek, monday, today } from "../support/time";
import { dismissInstallHint, expect, onPhone, tagOf, test, weekId, weekTitle, weekUrl } from "./helpers";

const MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
const monthName = (day: string) => MONTHS[Number(day.slice(5, 7)) - 1];

/** How far the links' middle lies from the middle of the page's content, in px. */
async function offCentre(page: Page, links: Locator) {
  const boxes = await links.evaluateAll((all) => all.map((l) => l.getBoundingClientRect()).map((r) => [r.left, r.right]));
  const main = await page.getByRole("main").evaluate((m) => {
    const r = m.getBoundingClientRect();
    const style = getComputedStyle(m);
    const left = r.left + m.clientLeft + parseFloat(style.paddingLeft);
    const right = r.left + m.clientLeft + m.clientWidth - parseFloat(style.paddingRight);
    return (left + right) / 2;
  });
  const middle = (Math.min(...boxes.map((b) => b[0])) + Math.max(...boxes.map((b) => b[1]))) / 2;
  return Math.abs(middle - main);
}

test.beforeEach(async ({ page }) => {
  await dismissInstallHint(page);
});

test("fixes §2.3 #1: below the week, ‹ Week N-1 and Week N+1 ›, centred @calendar @phone", async ({ page, folder }) => {
  const t = today();
  const before = isoWeek(addDays(monday(t), -7)).week;
  const after = isoWeek(addDays(monday(t), 7)).week;
  await page.goto(weekUrl("Work", t, tagOf(folder)));
  const around = page.getByRole("navigation", { name: "Weeks before and after" });
  const links = around.getByRole("link");
  await expect(links).toHaveText([`‹ Week ${before}`, `Week ${after} ›`]);
  await expect(around.getByRole("link", { name: `Week ${before}`, exact: true })).toHaveAttribute("href", new RegExp(`week=${weekId(addDays(monday(t), -7))}`));
  await expect(around.getByRole("link", { name: `Week ${after}`, exact: true })).toHaveAttribute("href", new RegExp(`week=${weekId(addDays(monday(t), 7))}`));
  expect(await offCentre(page, links), "the two links are centred below the week").toBeLessThanOrEqual(2);

  await around.getByRole("link", { name: `Week ${after}`, exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`week=${weekId(addDays(monday(t), 7))}`));
});

test("fixes §2.3 #2: below the month, ‹ <previous month> and <next month> › @calendar @phone", async ({ page, folder }) => {
  const t = today();
  const month = `${t.slice(0, 7)}-01`;
  await page.goto(`/calendar/Work?month=${t.slice(0, 7)}&tag=${tagOf(folder)}`);
  const around = page.getByRole("navigation", { name: "Months before and after" });
  const links = around.getByRole("link");
  await expect(links).toHaveText([`‹ ${monthName(addMonths(month, -1))}`, `${monthName(addMonths(month, 1))} ›`]);
  expect(await offCentre(page, links), "the two links are centred below the month").toBeLessThanOrEqual(2);

  await around.getByRole("link", { name: monthName(addMonths(month, -1)), exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`month=${addMonths(month, -1).slice(0, 7)}`));
});

test("fixes §2.3 #3: → and ← move a week on the desktop; Back to this week returns @calendar", async ({ page, folder }, testInfo) => {
  test.skip(onPhone(testInfo), "the keys turn the page where the tree is docked");
  const t = today();
  const title = page.getByRole("banner").getByRole("heading", { level: 1 });
  /** The page at the week `n` from this one: its address, and its title once its days are in. */
  const atWeek = async (n: number) => {
    await expect(page).toHaveURL(new RegExp(`week=${weekId(addDays(monday(t), 7 * n))}`));
    await expect(title).toHaveText(weekTitle(addDays(monday(t), 7 * n)));
  };
  await page.goto(weekUrl("Work", t, tagOf(folder)));
  const toolbar = page.getByRole("navigation", { name: "Calendar" });
  const back = toolbar.getByRole("link", { name: "Back to this week" });
  await expect(page.getByRole("navigation", { name: "Weeks before and after" })).toBeVisible();
  await expect(back).toHaveCount(0);

  await page.keyboard.press("ArrowRight");
  await atWeek(1);
  await page.keyboard.press("ArrowRight");
  await atWeek(2);
  await page.keyboard.press("ArrowLeft");
  await atWeek(1);
  await expect(back).toBeVisible();

  await back.click();
  await atWeek(0);
  await expect(back).toHaveCount(0);
});

test("fixes §2.3 #4: the arrow keys do nothing in the tag filter's field @calendar", async ({ page }, testInfo) => {
  test.skip(onPhone(testInfo), "the keys turn the page where the tree is docked");
  const t = today();
  // Without a tag: the tag filter is a field then.
  await page.goto(weekUrl("Work", t));
  const field = page.getByRole("search", { name: "Tag filter" }).getByRole("textbox", { name: "Tag" });
  await field.fill("pump");
  await field.press("ArrowLeft");
  await field.press("ArrowRight");
  await expect(field).toBeFocused();
  // Nothing moved: the page is still this week.
  await expect(page.getByRole("navigation", { name: "Calendar" }).getByRole("link", { name: "Back to this week" })).toHaveCount(0);
  await expect(page).toHaveURL(new RegExp(`week=${weekId(t)}$`));
});

test("fixes §2.3 #5: + Today and + This week are 48 px high @calendar @phone", async ({ page, folder }) => {
  await page.goto(weekUrl("Work", today(), tagOf(folder)));
  for (const name of ["+ Today", "+ This week"]) {
    const button = page.getByRole("button", { name, exact: true });
    await expect(button).toBeVisible();
    expect((await button.boundingBox())!.height, name).toBeGreaterThanOrEqual(48);
  }
});
