// SPDX-License-Identifier: AGPL-3.0-or-later
// The calendar's checklist (docs/superpowers/overnighter/2026-11-13-calendar-report.md) and the flags report's step 23,
// for the steps that stay inside the test's own folder. Each test narrows the views to a tag of its own where other
// tests' entries could show beside its own, and trashes its folder's notes at its end.
import type { Page } from "@playwright/test";
import type { Api } from "../support/api";
import { addDays, addMonths, isoWeek, monday, today } from "../support/time";
import {
  cleanFolder,
  dayAndMonth,
  dayHeading,
  dismissInstallHint,
  entryLine,
  expect,
  makeAgent,
  note,
  noteActions,
  noteEntryAnswered,
  notePage,
  onPhone,
  rangeText,
  shortDay,
  switchVault,
  tagOf,
  test,
  treePanel,
  weekTitle,
  weekUrl,
} from "./helpers";
import { shot } from "../support/test";

const EMPTY_WEEK = 'Nothing this week yet. Tap a day to add something, or give any note a day from its ⋯ menu. Your AI can do it too: "put this on Friday".';

test.beforeEach(async ({ page }) => {
  await dismissInstallHint(page);
});

test.afterEach(async ({ aliceApi, folder }) => {
  await cleanFolder(aliceApi, folder);
});

test("calendar #1: Settings → Calendar says the times are in Europe/Vienna, preset without asking @calendar", async ({ page, aliceApi }) => {
  // Preset by the app on the setup's first visit, from the browser's zone: nobody chose it.
  expect((await aliceApi.json<{ zone: string | null }>("GET", "/api/account/calendar")).zone).toBe("Europe/Vienna");
  await page.goto("/settings");
  const section = page.getByRole("region", { name: "Calendar" });
  await expect(section.getByText("Your calendar shows times in Europe/Vienna.", { exact: true })).toBeVisible();
  await expect(section.getByLabel("Your time zone")).toHaveValue("Europe/Vienna");
});

test("calendar #2: an empty week shows its vault above the week, seven days and the inviting line @calendar @phone", async ({ page, aliceApi }, testInfo) => {
  // Work gets dated notes from other tests at the same time; Welcome has none (checked here, before the screens).
  const t = today();
  const welcome = await aliceApi.json<{ weeks: { spanning: unknown[]; days: { entries: unknown[] }[] }[] }>("GET", "/api/vaults/Welcome/calendar");
  expect(welcome.weeks[0].spanning.length + welcome.weeks[0].days.reduce((n, d) => n + d.entries.length, 0), "Welcome has nothing this week").toBe(0);

  await page.goto("/");
  const panel = await treePanel(page, testInfo);
  await switchVault(page, panel, "Welcome");
  await panel.getByRole("button", { name: "Actions for Welcome", exact: true }).click();
  await page.getByRole("menuitem", { name: /^Calendar/ }).click();
  await expect(page).toHaveURL(/\/calendar\/Welcome$/);

  const banner = page.getByRole("banner");
  const title = banner.getByRole("heading", { level: 1 });
  await expect(title).toHaveText(weekTitle(t));
  const vault = banner.getByText("Welcome", { exact: true });
  await expect(vault).toBeVisible();
  expect((await vault.boundingBox())!.y, "the vault's name stands above the week").toBeLessThan((await title.boundingBox())!.y);

  const days = page.getByRole("main").getByRole("heading", { level: 2 });
  await expect(days).toHaveCount(7);
  for (let i = 0; i < 7; i++) await expect(days.nth(i)).toHaveText(dayHeading(addDays(monday(t), i)));
  await expect(page.getByText(EMPTY_WEEK, { exact: true })).toBeVisible();
  await shot(page, testInfo, "calendar");
});

test("calendar #4: Add to calendar… writes when, task and upcoming unquoted, every other line unchanged @calendar @phone", async ({ page, aliceApi, seedAs, folder }) => {
  const path = `/Work/${folder}/Certificate.md`;
  await seedAs("tags/calendar/Certificate.md", path);
  const before = (await aliceApi.getNote(path)).content;
  const when = addMonths(today(), 1);

  const answered = noteEntryAnswered(page);
  await page.goto(notePage(path));
  await expect(page.getByRole("heading", { level: 1, name: "Certificate" })).toBeVisible();
  await answered;
  await (await noteActions(page)).getByRole("menuitem", { name: "Add to calendar…" }).click();
  const dialog = page.getByRole("dialog", { name: "Add to calendar" });
  await dialog.getByRole("group", { name: "When" }).getByLabel("Day").fill(when);
  await dialog.getByRole("checkbox", { name: "To do" }).check();
  await dialog.getByRole("spinbutton", { name: "Show early: days before" }).fill("7");
  await dialog.getByRole("button", { name: "Save" }).click();
  await expect(dialog).toBeHidden();

  await expect(entryLine(page)).toHaveText(`📅 ${shortDay(when, true)} · To do · ✓ · Later`);
  // The keys after the existing ones, in the spec's order, unquoted; nothing else touched.
  expect((await aliceApi.getNote(path)).content).toBe(before.replace("owner: Alice\n---\n", `owner: Alice\nwhen: ${when}\ntask: open\nupcoming: 7d\n---\n`));
});

test("calendar #6: ✓ closes at once, Undo reopens, and the reason is kept with the day it was closed @calendar @phone", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Certificate.md`;
  const when = addDays(today(), 30);
  await aliceApi.putNote(path, note({ tags: "[pump]", when, task: "open", upcoming: "7d" }, "# Certificate\n\nThe pump's certificate.\n"));

  await page.goto(notePage(path));
  const line = entryLine(page);
  await expect(line).toHaveText(`📅 ${shortDay(when, true)} · To do · ✓ · Later`);

  await line.getByRole("button", { name: "Done: Certificate" }).click();
  const toast = page.getByRole("status", { name: "Closed" });
  await expect(toast).toHaveText("Closed · Undo · Add a reason");
  await toast.getByRole("button", { name: "Undo" }).click();
  await expect(toast).toBeHidden();
  await expect(line).toHaveText(`📅 ${shortDay(when, true)} · To do · ✓ · Later`);
  expect((await aliceApi.getNote(path)).content).toMatch(/^task: open$/m);

  await line.getByRole("button", { name: "Done: Certificate" }).click();
  await expect(toast).toHaveText("Closed · Undo · Add a reason");
  await toast.getByRole("button", { name: "Add a reason" }).click();
  const reason = toast.getByRole("textbox", { name: "Reason" });
  await expect(reason).toBeVisible();
  await expect(toast.getByRole("button", { name: "Save" })).toBeVisible();
  await reason.fill("renewed until 2027");
  await toast.getByRole("button", { name: "Save" }).click();
  await expect(toast).toBeHidden();

  await expect(line).toHaveText(`📅 ${shortDay(when, true)} · Closed ${dayAndMonth(today())} · renewed until 2027 · Reopen · Edit reason`);
  const content = (await aliceApi.getNote(path)).content;
  expect(content).toMatch(/^task: closed$/m);
  expect(content).toMatch(new RegExp(`^closed: ${today()}$`, "m"));
  expect(content).toMatch(/^reason: renewed until 2027$/m);
});

test("calendar #8: Later +1 day counts from the later of today and the date, and a task with until offers Postpone and More time @calendar @phone", async ({ page, aliceApi, folder }) => {
  const t = today();
  const overdue = `/Work/${folder}/Overdue.md`;
  const future = `/Work/${folder}/Future.md`;
  const window = `/Work/${folder}/Window task.md`;
  await aliceApi.putNote(overdue, note({ when: addDays(t, -3), task: "open" }, "# Overdue\n"));
  await aliceApi.putNote(future, note({ when: addDays(t, 5), task: "open" }, "# Future\n"));
  await aliceApi.putNote(window, note({ when: addDays(t, 2), until: addDays(t, 4), task: "open" }, "# Window task\n"));
  const moved = page.getByRole("status", { name: "Moved" });

  // Overdue: from today, so it moves to tomorrow, not to two days ago.
  await page.goto(notePage(overdue));
  await entryLine(page).getByRole("button", { name: "Later: Overdue" }).click();
  await page.getByRole("menuitem", { name: "+1 day" }).click();
  await expect(moved).toHaveText(`Moved to ${shortDay(addDays(t, 1), true)} · Undo`);
  expect((await aliceApi.getNote(overdue)).content).toMatch(new RegExp(`^when: ${addDays(t, 1)}$`, "m"));

  // Upcoming: a day past its own date; Undo puts it back.
  await page.goto(notePage(future));
  await entryLine(page).getByRole("button", { name: "Later: Future" }).click();
  await page.getByRole("menuitem", { name: "+1 day" }).click();
  await expect(moved).toHaveText(`Moved to ${shortDay(addDays(t, 6), true)} · Undo`);
  expect((await aliceApi.getNote(future)).content).toMatch(new RegExp(`^when: ${addDays(t, 6)}$`, "m"));
  await moved.getByRole("button", { name: "Undo" }).click();
  await expect(moved).toBeHidden();
  await expect.poll(async () => (await aliceApi.getNote(future)).content).toMatch(new RegExp(`^when: ${addDays(t, 5)}$`, "m"));

  // With until: Postpone and More time, each with its three choices.
  await page.goto(notePage(window));
  await entryLine(page).getByRole("button", { name: "Later: Window task" }).click();
  const menu = page.getByRole("menu", { name: "Later: Window task" });
  for (const heading of ["Postpone", "More time"]) {
    const group = menu.getByRole("group", { name: heading });
    await expect(group.getByRole("menuitem")).toHaveText(["+1 day", "+1 week", "Pick a date…"]);
  }
});

test("calendar #10: a window across weeks stands on top of each week it touches, its days with Also ongoing (1) collapsed @calendar @phone", async ({ page, aliceApi, folder }) => {
  const t = today();
  const tag = tagOf(folder);
  const first = addDays(monday(t), 4);
  const last = addDays(monday(t), 9);
  await aliceApi.putNote(`/Work/${folder}/Window.md`, note({ when: first, until: last, tags: `[${tag}]` }, "# Window\n\nThe pump is serviced.\n"));

  await page.goto(weekUrl("Work", t, tag));
  for (const week of [monday(t), addDays(monday(t), 7)]) {
    await expect(page.getByRole("banner").getByRole("heading", { level: 1 })).toHaveText(weekTitle(week));
    const top = page.getByRole("list", { name: "More than a day" });
    await expect(top.getByRole("listitem")).toHaveCount(1);
    await expect(top.getByRole("link", { name: "Window" })).toBeVisible();
    await expect(top).toContainText(rangeText(first, last));
    for (let i = 0; i < 7; i++) {
      const day = addDays(week, i);
      const region = page.getByRole("region", { name: new RegExp(`^${dayHeading(day)},`) });
      const also = region.locator("details");
      if (day >= first && day <= last) {
        await expect(also.locator("summary")).toHaveText("Also ongoing (1)");
        await expect(also, `${day} collapsed`).not.toHaveAttribute("open");
        await expect(also.getByRole("link", { name: "Window" })).toBeHidden();
      } else {
        await expect(also, `${day} has no Also ongoing`).toHaveCount(0);
      }
    }
    if (week === monday(t)) await page.getByRole("link", { name: "Next week" }).click();
  }
});

/** The controls on the page under 48 px high, by name. */
const smallControls = (page: Page) =>
  page.locator("button, a[href], input, summary, select").evaluateAll((controls) =>
    controls
      .filter((c) => {
        const r = c.getBoundingClientRect();
        return r.width > 1 && r.height > 1 && getComputedStyle(c).visibility !== "hidden";
      })
      .map((c) => ({ name: c.getAttribute("aria-label") ?? c.textContent?.trim() ?? c.tagName, height: c.getBoundingClientRect().height }))
      .filter((c) => c.height < 48),
  );

/**
 * This week of Work at 375 px with a long-titled to-do, narrowed to a tag of the test's own so that other tests'
 * entries add no days, lines or controls of theirs; `check` runs in dark, then in light.
 */
async function phoneWeek(page: Page, api: Api, folder: string, check: (scheme: "dark" | "light") => Promise<void>) {
  const t = today();
  const tag = tagOf(folder);
  await api.putNote(`/Work/${folder}/${LONG_TITLE}.md`, note({ when: t, task: "open", tags: `[${tag}]` }, `# ${LONG_TITLE}\n`));
  await page.goto(weekUrl("Work", t, tag));
  await expect(page.getByRole("link", { name: LONG_TITLE })).toBeVisible();
  for (const scheme of ["dark", "light"] as const) {
    await page.emulateMedia({ colorScheme: scheme });
    await expect.poll(() => page.evaluate(() => matchMedia("(prefers-color-scheme: dark)").matches)).toBe(scheme === "dark");
    await check(scheme);
  }
}

const LONG_TITLE = "A to-do with a rather long title that has to wrap on a phone of 375 pixels";
/** F-01: these two are 44 px; the second #13 test holds the finding, the first one asserts every other control. */
const FOUND_SMALL = ["Today", "This week"];

test("calendar #13: at 375 px the week stacks, wraps and doesn't scroll sideways, in light and dark @calendar @phone", async ({ page, aliceApi, folder }, testInfo) => {
  test.skip(!onPhone(testInfo), "the phone's layout; the desktop has seven columns");
  const schemes: string[] = [];
  await phoneWeek(page, aliceApi, folder, async (scheme) => {
    // The app follows the system's scheme when it changes; the dark one is taken first, the light one once it differs.
    const shown = () => page.evaluate(() => `${document.documentElement.dataset.scheme} ${getComputedStyle(document.querySelector("main")!).color}`);
    if (scheme === "light") await expect.poll(shown, "the scheme followed the system").not.toBe(schemes[0]);
    schemes.push(await shown());

    const boxes = await page.getByRole("region", { name: /^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday), / }).evaluateAll((sections) => sections.map((s) => s.getBoundingClientRect()).map((r) => ({ x: r.x, y: r.y })));
    expect(boxes.length, scheme).toBe(7);
    for (let i = 1; i < 7; i++) {
      expect(boxes[i].x, `${scheme}: day ${i} under the one before`).toBe(boxes[0].x);
      expect(boxes[i].y, `${scheme}: day ${i} under the one before`).toBeGreaterThan(boxes[i - 1].y);
    }
    const sideways = await page.evaluate(() => [document.documentElement.scrollWidth - innerWidth, ...[...document.querySelectorAll("main")].map((m) => m.scrollWidth - m.clientWidth)]);
    expect(sideways.every((d) => d <= 0), `${scheme}: no sideways scroll (${sideways})`).toBe(true);

    const link = (await page.getByRole("link", { name: LONG_TITLE }).boundingBox())!;
    expect(link.height, `${scheme}: the long title wraps onto more lines`).toBeGreaterThan(60);
    expect(link.x + link.width, `${scheme}: the long title stays within the screen`).toBeLessThanOrEqual(375);

    expect((await smallControls(page)).filter((c) => !FOUND_SMALL.includes(c.name)), `${scheme}: no other control under 48 px`).toEqual([]);
  });
});

test("calendar #13: every control is at least 48 px high @calendar @phone", async ({ page, aliceApi, folder }, testInfo) => {
  test.fail(onPhone(testInfo), "F-01 Today and This week are 44 px high, under the 48 px of spec §7.11");
  test.info().annotations.push({ type: "finding", description: "F-01" });
  test.skip(!onPhone(testInfo), "the phone's layout; the desktop has seven columns");
  const small: { scheme: string; name: string; height: number }[] = [];
  await phoneWeek(page, aliceApi, folder, async (scheme) => {
    // The week is in, with the two controls the finding is about.
    await expect(page.getByRole("region", { name: /^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday), / })).toHaveCount(7);
    for (const name of FOUND_SMALL) await expect(page.getByRole("button", { name, exact: true }), `${scheme}: ${name}`).toBeVisible();
    small.push(...(await smallControls(page)).map((c) => ({ scheme, ...c })));
  });
  expect(small, "F-01: every control is at least 48 px high").toEqual([]);
});

test("calendar #14: Bob sees Work's entries in his zone, without ✓, Later, +, Today, This week, Fix… or the note's calendar items @calendar @phone", async ({ bobPage, bobApi, aliceApi, folder }) => {
  const t = today();
  const tag = tagOf(folder);
  const task = `/Work/${folder}/Bob task.md`;
  await aliceApi.putNote(task, note({ when: t, task: "open", tags: `[${tag}]` }, "# Bob task\n"));
  await aliceApi.putNote(`/Work/${folder}/Call London.md`, note({ when: `${addDays(t, 1)}T14:00`, tz: "Europe/London", tags: `[${tag}]` }, "# Call London\n"));
  await aliceApi.putNote(`/Work/${folder}/Someday.md`, note({ when: "someday", tags: `[${tag}]` }, "# Someday\n"));
  await dismissInstallHint(bobPage);

  // His zone: the calendar's today and zone are his (preset from his browser, Europe/Vienna, as Alice's).
  const view = await bobApi.json<{ today: string; zone: string; canWrite: boolean }>("GET", "/api/vaults/Work/calendar");
  expect([view.zone, view.today, view.canWrite]).toEqual(["Europe/Vienna", t, false]);

  await bobPage.goto(weekUrl("Work", t, tag));
  await expect(bobPage.getByRole("link", { name: "Bob task" })).toBeVisible();
  await expect(bobPage.getByRole("link", { name: "Call London" })).toBeVisible();
  // A time pinned to another zone than his shows that zone.
  await expect(bobPage.getByText("14:00 (Europe/London)")).toBeVisible();
  await expect(bobPage.getByRole("heading", { name: "Dates I couldn't read" })).toBeVisible();
  for (const name of [/^Done:/, /^Later/, /^Add something on/, /^Today$/, /^This week$/, /^Fix…/]) await expect(bobPage.getByRole("button", { name }), String(name)).toHaveCount(0);

  await bobPage.goto(notePage(task));
  await expect(entryLine(bobPage)).toHaveText(`📅 ${shortDay(t, true)} · To do`);
  const menu = await noteActions(bobPage);
  await expect(menu.getByRole("menuitem").first()).toBeVisible();
  for (const name of ["Add to calendar…", "Edit date…", "Remove from calendar"]) await expect(menu.getByRole("menuitem", { name }), name).toHaveCount(0);

  const refused = await bobApi.context.post("/api/vaults/Work/calendar/close", { data: { path: task } });
  expect(refused.status()).toBe(403);
  expect((await aliceApi.getNote(task)).content).toMatch(/^task: open$/m);
});

test("calendar #17: an agent reads and writes the calendar through sh, and the console only reads @calendar", async ({ aliceApi, mcp, folder, seedAs }) => {
  const t = today();
  const plan = `/Work/${folder}/Plan.md`;
  const dayNote = `/Work/${folder}/Calendar/${t}.md`;
  await seedAs("tags/calendar/Plan.md", plan);
  await seedAs("tags/calendar/Tags/Kumbu.md", `/Work/${folder}/Kumbu.md`);
  await aliceApi.putNote(`/Work/${folder}/Due.md`, note({ when: addDays(t, -2), task: "open" }, "# Due\n"));
  await aliceApi.putNote(dayNote, `# ${t}\n`);
  await aliceApi.putNote(`/Work/${folder}/Someday.md`, note({ when: "someday" }, "# Someday\n"));
  const version = /version of .+: [0-9a-f]+/;

  const notifications = await mcp.sh("calendar notifications");
  expect(notifications.isError).toBe(false);
  const due = notifications.text.split("\n").filter((l) => l.includes(`/Work/${folder}/Due.md`));
  expect(due, "one line for Due.md").toHaveLength(1);
  expect(due[0]).toContain("overdue · 2 days");
  // At least this folder's Someday.md; other tests' dates may be counted too.
  expect(notifications.text).toMatch(/^Problems: (1 date|\d+ dates) can't be read \((\/\S+ \d+, )*\/Work \d+(, \/\S+ \d+)*\); calendar \/\S+ lists (it|them)$/m);

  const week = await mcp.sh("calendar /Work");
  expect(week.isError).toBe(false);
  expect(week.text.split("\n")[0]).toBe(`${weekTitle(t)} · /Work`);
  for (let i = 0; i < 7; i++) expect(week.text).toContain(shortDay(addDays(monday(t), i), true));

  const tagged = await mcp.sh(`calendar /Work --week ${isoWeek(t).week} --tag kumbu`);
  expect(tagged.isError).toBe(false);
  expect(tagged.text).toContain(`/Work/${folder}/Kumbu.md`);
  expect(tagged.text).not.toContain(`/Work/${folder}/Due.md`);

  const month = t.slice(0, 7);
  const weeks = await mcp.sh(`calendar /Work --month ${month}`);
  expect(weeks.isError).toBe(false);
  for (let day = `${month}-01`; day.startsWith(month); day = addDays(day, 7)) expect(weeks.text).toContain(`Week ${isoWeek(day).week}`);

  const when = addDays(t, 8);
  const set = await mcp.sh(`calendar set "${plan}" --when ${when} --task --upcoming 7d`);
  expect(set.text).toMatch(version);
  expect((await aliceApi.getNote(plan)).content).toBe(`---\nwhen: ${when}\ntask: open\nupcoming: 7d\n---\n# Plan\n\nWhat the week is for.\n`);

  const close = await mcp.sh(`calendar close "${plan}" --reason "done"`);
  expect(close.text).toMatch(version);
  // Keys new to the note go after the ones it has (spec §2.2).
  expect((await aliceApi.getNote(plan)).content).toBe(`---\nwhen: ${when}\ntask: closed\nupcoming: 7d\nclosed: ${t}\nreason: done\n---\n# Plan\n\nWhat the week is for.\n`);

  const reopen = await mcp.sh(`calendar reopen "${plan}"`);
  expect(reopen.text).toMatch(version);
  expect((await aliceApi.getNote(plan)).content).not.toMatch(/^(closed|reason):/m);

  const moved = addDays(t, 15);
  const move = await mcp.sh(`calendar move "${plan}" ${moved}`);
  expect(move.text).toMatch(version);
  expect((await aliceApi.getNote(plan)).content).toMatch(new RegExp(`^when: ${moved}$`, "m"));

  const unset = await mcp.sh(`calendar unset "${plan}"`);
  expect(unset.text).toMatch(version);
  expect((await aliceApi.getNote(plan)).content).not.toMatch(/^(when|task|upcoming):/m);

  const byName = await mcp.sh(`calendar unset "${dayNote}"`);
  expect(byName.isError).toBe(true);
  expect(byName.text).toContain(`${dayNote} is the day note of ${dayAndMonth(t, true)} by its name; rename it`);

  // A checkpoint takes maintain: an agent of its own for it, revoked at the end.
  const keeper = await makeAgent({ Work: "maintain" }, "checkpoint");
  const cp = `e2e-${folder.slice(-6)}`;
  try {
    expect((await keeper.mcp.sh(`mkcp "/Work/${folder}/" ${cp}`)).isError).toBe(false);
    const listed = await mcp.sh("lscp /Work");
    const name = listed.text.match(new RegExp(`\\b${cp}\\b`))?.[0];
    expect(name, "lscp lists the checkpoint").toBe(cp);
    const read = await mcp.sh(`calendar /Work --checkpoint ${name}`);
    expect(read.isError).toBe(false);
    expect(read.text).toContain(weekTitle(t));
    const refused = await mcp.sh(`calendar close "${plan}" --checkpoint ${name}`);
    expect(refused.isError).toBe(true);
    expect(refused.text).toMatch(/checkpoint can't be changed/);
  } finally {
    const removed = await keeper.mcp.sh(`rmcp /Work ${cp}`);
    await keeper.dispose();
    expect(removed.isError, "rmcp removes the checkpoint").toBe(false);
  }

  const reads = await aliceApi.console("calendar /Work");
  expect(reads.isError).toBe(false);
  expect(reads.text.split("\n")[0]).toBe(`${weekTitle(t)} · /Work`);
  for (const command of [`calendar close "${plan}"`, `calendar -- close "${plan}"`]) {
    const refused = await aliceApi.console(command);
    expect(refused.isError, command).toBe(true);
    expect(refused.text, command).toContain("not in this console, which only reads");
  }
});

test("flags #23: When: alone is in the calendar; When: and when: together can't be read, and Fix… keeps When: @calendar @phone", async ({ page, aliceApi, folder }) => {
  const t = today();
  const tag = tagOf(folder);
  const path = `/Work/${folder}/Capital.md`;
  await aliceApi.putNote(path, `---\nWhen: ${t}\ntags: [${tag}]\n---\n# Capital\n`);
  await page.goto(weekUrl("Work", t, tag));
  const day = page.getByRole("region", { name: new RegExp(`^${dayHeading(t)},`) });
  await expect(day.getByRole("link", { name: "Capital" })).toBeVisible();

  await aliceApi.putNote(path, `---\nWhen: ${t}\nwhen: ${addDays(t, 1)}\ntags: [${tag}]\n---\n# Capital\n`);
  await page.reload();
  const unread = page.getByRole("region", { name: "Dates I couldn't read" });
  const item = unread.getByRole("listitem").filter({ hasText: "Capital" });
  await expect(item).toBeVisible();
  await expect(day.getByRole("link", { name: "Capital" })).toHaveCount(0);

  await item.getByRole("button", { name: "Fix… Capital" }).click();
  const dialog = page.getByRole("dialog", { name: "Edit date" });
  const dayField = dialog.getByRole("group", { name: "When" }).getByLabel("Day");
  await dayField.fill(addDays(t, 2));
  await dialog.getByRole("button", { name: "Save" }).click();
  await expect(dialog).toBeHidden();

  const content = (await aliceApi.getNote(path)).content;
  expect(content).toMatch(new RegExp(`^When: ${addDays(t, 2)}$`, "m"));
  expect(content).not.toMatch(/^when:/m);
  await expect(unread).toBeHidden();
  await page.goto(weekUrl("Work", addDays(t, 2), tag));
  await expect(page.getByRole("region", { name: new RegExp(`^${dayHeading(addDays(t, 2))},`) }).getByRole("link", { name: "Capital" })).toBeVisible();
});
