// SPDX-License-Identifier: AGPL-3.0-or-later
// Days off and lighter days (fixes spec 2026-11-14-0-5-0-fixes-design.md §2.4). Until Alice chooses, the browser's
// locale says which days are off: those tests set the locale themselves. The test that chooses is @serial and returns
// to "not chosen" at its end, so no other test ever runs while a choice is stored.
import type { Locator, Page } from "@playwright/test";
import type { Api } from "../support/api";
import { addDays, isoWeek, monday, today, type Day } from "../support/time";
import { cleanFolder, dayHeading, dismissInstallHint, expect, monthTitle, note, tagOf, test, weekUrl } from "./helpers";

const WEEKDAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

/** Alice's stored week, null while she hasn't chosen. */
const storedDays = async (api: Api) => (await api.json<{ days?: string[] | null }>("GET", "/api/account/calendar")).days ?? null;

/** A day of the week view, or of the month list. */
const dayRegion = (page: Page, day: Day) => page.getByRole("region", { name: new RegExp(`^${dayHeading(day)},`) });
const background = (l: Locator) => l.evaluate((e) => getComputedStyle(e).backgroundColor);

/** This week of Work, narrowed to a tag nobody carries: seven empty days. */
async function emptyWeek(page: Page, folder: string) {
  const t = today();
  await page.goto(weekUrl("Work", t, tagOf(folder)));
  await expect(page.getByRole("region", { name: /^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday), / })).toHaveCount(7);
  return Array.from({ length: 7 }, (_, i) => dayRegion(page, addDays(monday(t), i)));
}

/** The weekdays, by name, whose day says Day off. */
async function daysOff(days: Locator[]) {
  const off: string[] = [];
  for (let i = 0; i < 7; i++) if ((await days[i].textContent())!.includes("Day off")) off.push(WEEKDAYS[i]);
  return off;
}

test.beforeEach(async ({ page }) => {
  await dismissInstallHint(page);
});

test.describe("en-GB", () => {
  test.use({ locale: "en-GB" });

  test("fixes §2.4 #1: until Alice chooses, en-GB shades Saturday and Sunday @calendar", async ({ page, aliceApi, folder }) => {
    expect(await storedDays(aliceApi), "Alice hasn't chosen her week").toBeNull();
    const days = await emptyWeek(page, folder);
    expect(await daysOff(days)).toEqual(["Saturday", "Sunday"]);
    const workday = await background(days[0]);
    expect(await background(days[5]), "Saturday shaded").not.toBe(workday);
    expect(await background(days[6]), "Sunday as Saturday").toBe(await background(days[5]));
    for (let i = 1; i < 5; i++) expect(await background(days[i]), WEEKDAYS[i]).toBe(workday);
  });
});

test.describe("he-IL", () => {
  test.use({ locale: "he-IL" });

  test("fixes §2.4 #2: in he-IL, Friday and Saturday are the days off until someone chooses @calendar", async ({ page, aliceApi, folder }) => {
    expect(await storedDays(aliceApi), "Alice hasn't chosen her week").toBeNull();
    const days = await emptyWeek(page, folder);
    expect(await daysOff(days)).toEqual(["Friday", "Saturday"]);
    const workday = await background(days[0]);
    expect(await background(days[4]), "Friday shaded").not.toBe(workday);
    expect(await background(days[6]), "Sunday a workday").toBe(workday);
  });

  test("fixes §2.4 #2: in he-IL, Settings shows Friday and Saturday as Day off @calendar", async ({ page, aliceApi }) => {
    expect(await storedDays(aliceApi), "Alice hasn't chosen her week").toBeNull();
    await page.goto("/settings");
    const section = page.getByRole("region", { name: "Calendar" });
    for (const name of WEEKDAYS) {
      const level = name === "Friday" || name === "Saturday" ? "Day off" : "Workday";
      await expect(section.getByRole("group", { name, exact: true }).getByRole("button", { name: level }), `${name}: ${level}`).toHaveAttribute("aria-pressed", "true");
    }
    await expect(section.getByRole("button", { name: "Use my browser's weekend" })).toHaveCount(0);
  });
});

test.describe("en-GB, chosen", () => {
  // The first tap stores the week as shown, which is this locale's weekend.
  test.use({ locale: "en-GB" });

  test("fixes §2.4 #3: a tap saves the week at once; days off and lighter days shade the week, the month list and the grid, in light and dark; Use my browser's weekend undoes it @calendar @serial @phone", async ({ page, aliceApi, folder }) => {
    expect(await storedDays(aliceApi), "Alice hasn't chosen her week").toBeNull();
    const t = today();
    const tag = tagOf(folder);
    // A week wholly in this month, so the month shows the same days.
    const mon = monday(`${t.slice(0, 7)}-10`);
    const [tue, fri] = [addDays(mon, 1), addDays(mon, 4)];
    // The month list leaves empty days out: an entry on each day it compares.
    for (const [day, title] of [[mon, "Monday entry"], [tue, "Tuesday entry"], [fri, "Friday entry"]]) {
      await aliceApi.putNote(`/Work/${folder}/${title}.md`, note({ when: day, tags: `[${tag}]` }, `# ${title}\n`));
    }
    try {
      await page.goto("/settings");
      const section = page.getByRole("region", { name: "Calendar" });
      const row = (name: string) => section.getByRole("group", { name, exact: true });
      await row("Monday").getByRole("button", { name: "Day off" }).click();
      // The first tap stores all seven days as shown: Saturday and Sunday off, from en-GB.
      await expect.poll(() => storedDays(aliceApi)).toEqual(["off", "workday", "workday", "workday", "workday", "off", "off"]);
      await row("Friday").getByRole("button", { name: "Lighter day" }).click();
      await expect.poll(() => storedDays(aliceApi)).toEqual(["off", "workday", "workday", "workday", "lighter", "off", "off"]);
      await expect(row("Monday").getByRole("button", { name: "Day off" })).toHaveAttribute("aria-pressed", "true");
      await expect(row("Friday").getByRole("button", { name: "Lighter day" })).toHaveAttribute("aria-pressed", "true");

      /** Monday, Tuesday and Friday: three shades, Monday's darkest apart from a workday's, Friday's another. */
      const threeShades = async (where: string, monday: Locator, tuesday: Locator, friday: Locator) => {
        const [m, tu, f] = [await background(monday), await background(tuesday), await background(friday)];
        expect(m, `${where}: Monday is shaded`).not.toBe(tu);
        expect(f, `${where}: Friday is shaded`).not.toBe(tu);
        expect(f, `${where}: Friday lighter than Monday`).not.toBe(m);
      };
      /** The grid's cells of the week, Monday to Sunday. */
      const gridWeek = () =>
        page
          .getByRole("table", { name: monthTitle(mon) })
          .locator("tbody tr")
          .filter({ has: page.getByRole("rowheader", { name: String(isoWeek(mon).week), exact: true }) })
          .locator("td");

      for (const scheme of ["dark", "light"] as const) {
        await page.emulateMedia({ colorScheme: scheme });
        await page.goto(weekUrl("Work", mon, tag));
        await expect.poll(() => page.evaluate(() => matchMedia("(prefers-color-scheme: dark)").matches)).toBe(scheme === "dark");
        const [m, tu, f] = [dayRegion(page, mon), dayRegion(page, tue), dayRegion(page, fri)];
        await expect(m).toContainText("Day off");
        await expect(f).toContainText("Lighter day");
        await expect(tu).not.toContainText(/Day off|Lighter day/);
        // A day off stays usable: its entry shows, and + adds to it.
        await expect(m.getByRole("link", { name: "Monday entry" })).toBeVisible();
        await threeShades(`${scheme} week`, m, tu, f);
        await m.getByRole("button", { name: `Add something on ${dayHeading(mon)}` }).click();
        const dialog = page.getByRole("dialog", { name: `Add something on ${dayHeading(mon)}` });
        await expect(dialog).toBeVisible();
        await dialog.getByRole("button", { name: "Cancel" }).click();
        await expect(dialog).toBeHidden();

        const grid = page.getByRole("button", { name: "Show grid" });
        if ((await grid.getAttribute("aria-pressed")) !== "true") await grid.click();
        await expect(gridWeek()).toHaveCount(7);
        await threeShades(`${scheme} grid`, gridWeek().nth(0), gridWeek().nth(1), gridWeek().nth(4));

        await page.goto(`/calendar/Work?month=${mon.slice(0, 7)}&tag=${tag}`);
        const [mm, mtu, mf] = [dayRegion(page, mon), dayRegion(page, tue), dayRegion(page, fri)];
        await expect(mm.getByRole("link", { name: "Monday entry" })).toBeVisible();
        await expect(mm).toContainText("Day off");
        await expect(mf).toContainText("Lighter day");
        await threeShades(`${scheme} month`, mm, mtu, mf);
        await threeShades(`${scheme} month grid`, gridWeek().nth(0), gridWeek().nth(1), gridWeek().nth(4));
      }

      await page.goto("/settings");
      await section.getByRole("button", { name: "Use my browser's weekend" }).click();
      await expect.poll(() => storedDays(aliceApi)).toBeNull();
      await expect(section.getByRole("button", { name: "Use my browser's weekend" })).toHaveCount(0);
      await expect(row("Monday").getByRole("button", { name: "Workday" })).toHaveAttribute("aria-pressed", "true");
    } finally {
      await aliceApi.json("PUT", "/api/account/calendar", { days: null });
      // Alice's view of Work's calendar is hers, for every test: back to the week without the grid.
      await aliceApi.json("PUT", "/api/vaults/Work/calendar/view", { view: "week", grid: false });
      await cleanFolder(aliceApi, folder);
    }
  });
});
