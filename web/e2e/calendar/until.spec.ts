// SPDX-License-Identifier: AGPL-3.0-or-later
// Until in a day's + (spec 2026-11-16-calendar-until-on-add-design.md, PR #225).
//
// Promises in scope, and the test that covers each:
// - Until appears only once a Time is given → "calendar until #1"
// - an end before or at the start is refused in the dialog, in plain words ("Until is before the time") → "calendar until #1"
// - "12:00 until 21:00" is written as one entry, `until: <day>T21:00` beside `when`, and the week shows its end
//   → "calendar until #1"
// - left empty, no `until` is written → "calendar until #2"
// - the server refuses at its boundary too: an until before the time, equal to it, without a time, malformed; nothing
//   is stored → "calendar until #3"
import type { Page } from "@playwright/test";
import { addDays, today, type Day } from "../support/time";
import { shot } from "../support/test";
import { dayHeading, expect, test, weekUrl } from "./helpers";

/** A day's + writes to the vault's Calendar folder, named after the title: the folder in the title keeps it the test's own. */
const entryPath = (title: string) => `/Work/Calendar/${title}.md`;

const dayRegion = (page: Page, day: Day) => page.getByRole("region", { name: new RegExp(`^${dayHeading(day)},`) });

/** The titles a test adds, deleted in afterEach: a `finally` doesn't run once the test's time is up, a hook does. */
let titles: string[] = [];

// On the phone the install hint can sit over the dialog's buttons: answered beforehand, as Not now leaves it.
test.beforeEach(async ({ page }) => {
  titles = [];
  await page.addInitScript(() => localStorage.setItem("kumbu.installHint", "dismissed"));
});

// Not there (the test failed before adding it): 404; any other answer is a clean-up that failed.
test.afterEach(async ({ aliceApi }) => {
  for (const title of titles) {
    const status = (await aliceApi.send("DELETE", `/api/notes/Work/Calendar/${encodeURIComponent(`${title}.md`)}`)).status();
    expect([204, 404], `delete ${entryPath(title)}`).toContain(status);
  }
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
  titles.push(title);
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
  await expect(page).toHaveURL(new RegExp(`/notes/Work/Calendar/${encodeURIComponent(title)}\\.md$`));

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
});

test("calendar until #2: a time without Until writes no until @calendar", async ({ page, aliceApi, folder }) => {
  const day = addDays(today(), 15);
  const title = `Dentist ${folder}`;
  titles.push(title);
  const dialog = await addOn(page, day);
  await dialog.getByLabel("Title").fill(title);
  await dialog.getByLabel("Time").fill("09:30");
  await expect(dialog.getByLabel("Until")).toBeVisible();
  await dialog.getByRole("button", { name: "Add" }).click();
  await expect(dialog).toBeHidden();

  await expect.poll(async () => (await aliceApi.console(`cat "${entryPath(title)}"`)).text).toContain(`when: ${day}T09:30\n`);
  expect((await aliceApi.console(`cat "${entryPath(title)}"`)).text).not.toContain("until:");
});

test("calendar until #3: the server refuses an until before or at the time, without one, or malformed, and stores nothing @calendar", async ({ aliceApi, folder }) => {
  const day = addDays(today(), 16);
  const title = `Refused ${folder}`;
  titles.push(title);
  for (const [body, error] of [
    [{ time: "12:00", until: "11:00" }, "until 11:00 is not after 12:00"],
    [{ time: "12:00", until: "12:00" }, "until 12:00 is not after 12:00"],
    [{ until: "17:00" }, "an until needs a time"],
    [{ time: "12:00", until: "9pm" }, "9pm is not a time; give 17:00"],
  ] as const) {
    const response = await aliceApi.send("POST", "/api/vaults/Work/calendar/entries", { data: { day, title, task: false, ...body } });
    expect(response.status(), JSON.stringify(body)).toBe(400);
    expect(await response.json(), JSON.stringify(body)).toEqual({ error });
  }
  // Nothing stored: neither the note nor a numbered one beside it (the Calendar folder may not exist yet at all).
  for (const name of [title, `${title} 2`]) {
    expect((await aliceApi.send("GET", `/api/notes/Work/Calendar/${encodeURIComponent(`${name}.md`)}`)).status(), `${name} not stored`).toBe(404);
  }
});
