// SPDX-License-Identifier: AGPL-3.0-or-later
// Until in a day's + (spec 2026-11-16-calendar-until-on-add-design.md, PR #225).
//
// Promises in scope, and the test that covers each:
// - Until appears only once a Time is given → "calendar until #1"
// - an end before or at the start is refused in the dialog, in plain words ("Until is before the time") → "calendar until #1"
// - "12:00 until 21:00" is written as one entry, `until: <day>T21:00` beside `when`, and the week shows its end
//   → "calendar until #1"
// - left empty, no `until` is written → "calendar until #2"
// Left out: the server's own refusals (without a time, not after, malformed): the dialog never sends them, and the
// xUnit tests of AddAsync and the API cover them.
import type { Page } from "@playwright/test";
import { addDays, today, type Day } from "../support/time";
import { shot } from "../support/test";
import { dayHeading, expect, test, weekUrl } from "./helpers";

/** A day's + writes to the vault's Calendar folder, named after the title: the folder in the title keeps it the test's own. */
const entryPath = (title: string) => `/Work/Calendar/${title}.md`;

const dayRegion = (page: Page, day: Day) => page.getByRole("region", { name: new RegExp(`^${dayHeading(day)},`) });

// On the phone the install hint can sit over the dialog's buttons: answered beforehand, as Not now leaves it.
test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem("kumbu.installHint", "dismissed"));
});

/** Opens the day's + in Work's week of `day`; gives the dialog. */
async function addOn(page: Page, day: Day) {
  await page.goto(weekUrl("Work", day));
  await dayRegion(page, day).getByRole("button", { name: `Add something on ${dayHeading(day)}` }).click();
  const dialog = page.getByRole("dialog", { name: `Add something on ${dayHeading(day)}` });
  await expect(dialog).toBeVisible();
  return dialog;
}

test("calendar until #1: a day's + asks for Until once there is a time, refuses an end before it, and writes 12:00 until 21:00 as one entry @calendar @phone", async ({ page, aliceApi, folder }, testInfo) => {
  // Two weeks ahead: in no other test's this week, and never before today.
  const day = addDays(today(), 14);
  const title = `Market ${folder}`;
  try {
    const dialog = await addOn(page, day);
    await dialog.getByLabel("Title").fill(title);
    await expect(dialog.getByLabel("Until"), "no Until without a time").toHaveCount(0);

    await dialog.getByLabel("Time").fill("12:00");
    const until = dialog.getByLabel("Until");
    await expect(until).toBeVisible();
    const add = dialog.getByRole("button", { name: "Add" });
    for (const before of ["11:00", "12:00"]) {
      await until.fill(before);
      await expect(dialog.getByText("Until is before the time")).toBeVisible();
      await expect(add, `Until ${before} is refused`).toBeDisabled();
    }
    await until.fill("21:00");
    await expect(dialog.getByText("Until is before the time")).toHaveCount(0);
    await shot(page, testInfo, "calendar-until");
    await add.click();
    await expect(dialog).toBeHidden();
    await expect(page).toHaveURL(new RegExp(`/notes/Work/Calendar/${encodeURIComponent(title).replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\.md$`));

    // One entry, with its end beside its start, read back through the console.
    const listed = await aliceApi.console(`ls /Work/Calendar | rg -F "${folder}"`);
    expect(listed.text.trim().split("\n"), "one note for the entry").toEqual([`${title}.md`]);
    const written = await aliceApi.console(`cat "${entryPath(title)}"`);
    expect(written.isError, written.text).toBe(false);
    expect(written.text).toContain(`when: ${day}T12:00\nuntil: ${day}T21:00\n`);
    expect(written.text).toContain(`# ${title}`);

    // The week shows the entry with its end.
    await page.goto(weekUrl("Work", day));
    const entry = dayRegion(page, day).getByRole("listitem").filter({ hasText: title });
    await expect(entry).toHaveCount(1);
    await expect(entry).toContainText("12:00–21:00");
  } finally {
    await aliceApi.deleteNote(entryPath(title)).catch(() => undefined);
  }
});

test("calendar until #2: a time without Until writes no until @calendar", async ({ page, aliceApi, folder }) => {
  const day = addDays(today(), 15);
  const title = `Dentist ${folder}`;
  try {
    const dialog = await addOn(page, day);
    await dialog.getByLabel("Title").fill(title);
    await dialog.getByLabel("Time").fill("09:30");
    await expect(dialog.getByLabel("Until")).toBeVisible();
    await dialog.getByRole("button", { name: "Add" }).click();
    await expect(dialog).toBeHidden();

    await expect.poll(async () => (await aliceApi.console(`cat "${entryPath(title)}"`)).text).toContain(`when: ${day}T09:30\n`);
    expect((await aliceApi.console(`cat "${entryPath(title)}"`)).text).not.toContain("until:");
  } finally {
    await aliceApi.deleteNote(entryPath(title)).catch(() => undefined);
  }
});
