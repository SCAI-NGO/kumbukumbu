// SPDX-License-Identifier: AGPL-3.0-or-later
// The calendar's steps that look at a list across vaults or folders (What's on, its badge, the dates that can't be
// read), at something vault-wide (Today's day note in /Work/Calendar/, the board, the remembered view) or at
// discreet state: all @serial, so nothing else writes meanwhile. Each trashes its folder's notes at its end.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import type { BrowserContext, Page, TestInfo } from "@playwright/test";
import { Api, guarded } from "../support/api";
import { baseURL, fixturesDir } from "../support/env";
import { endHidden as hideThroughApi, hideDiscreet, revealDiscreet } from "../support/session";
import { addDays, addMonths, addYears, isoWeek, monday, nowTime, today } from "../support/time";
import {
  cleanFolder,
  dayAndMonth,
  dayHeading,
  dismissInstallHint,
  entryLine,
  expect,
  longDay,
  makeAgent,
  monthTitle,
  note,
  noteActions,
  noteEntryAnswered,
  notePage,
  onPhone,
  shortDay,
  shortWeekTitle,
  switchVault,
  tagOf,
  test,
  treePanel,
  warningColour,
  weekUrl,
} from "./helpers";


type Item = { path: string; vault: string; title: string; state: string };
type WhatsOn = { items: Item[]; count: number; overdue: boolean; vaults: { vault: string; count: number; overdue: boolean }[]; couldntRead: number };
const whatsOn = (api: Api) => api.json<WhatsOn>("GET", "/api/calendar/whats-on");

/** What's on's entries, by their titles. */
const whatsOnItems = (page: Page) => page.getByRole("main").getByRole("list").first().getByRole("listitem");
const itemOf = (page: Page, title: string) => whatsOnItems(page).filter({ has: page.getByRole("link", { name: title, exact: true }) });

/** The badge's words on What's on in the navigation: "3 on", "3 on, some overdue", nothing at zero. */
const badgeWords = (count: number, overdue: boolean) => (count <= 0 ? "" : overdue ? `${count} on, ${count === 1 ? "overdue" : "some overdue"}` : `${count} on`);
const named = (label: string, count: number, overdue: boolean) => new RegExp(`^${label}${count > 0 ? ` ${badgeWords(count, overdue)}` : ""}$`);

/** "17:16" for a number of minutes after midnight. */
const clock = (minutes: number) => `${String(Math.floor(minutes / 60)).padStart(2, "0")}:${String(minutes % 60).padStart(2, "0")}`;

test.beforeEach(async ({ page }) => {
  await dismissInstallHint(page);
});

test.afterEach(async ({ aliceApi, folder }) => {
  await cleanFolder(aliceApi, folder);
});

/** The badge on What's on in the navigation, on either viewport: the sidebar's link, or the drawer's on a phone. */
async function whatsOnLink(page: Page, testInfo: TestInfo) {
  await page.goto("/");
  const panel = await treePanel(page, testInfo);
  return { panel, link: panel.getByRole("navigation", { name: "Account" }).getByRole("link", { name: /^What's on/ }) };
}

test("calendar #3: Today makes the day note in /Work/Calendar/ with its heading, the caret below it and its header line @calendar @serial", async ({ page, aliceApi }) => {
  const t = today();
  const path = `/Work/Calendar/${t}.md`;
  expect((await aliceApi.send("GET", `/api/notes/Work/Calendar/${t}.md`)).status(), "no day note of today yet").toBe(404);
  try {
    await page.goto(weekUrl("Work", t));
    await page.getByRole("button", { name: "+ Today", exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/notes/Work/Calendar/${t}\\.md$`));
    const editor = page.getByLabel("Markdown source", { exact: true });
    await expect(editor).toHaveValue(new RegExp(`^# ${longDay(t)}\\n`));
    await expect(editor).toBeFocused();
    const caretLine = await editor.evaluate((area: HTMLTextAreaElement) => area.value.slice(0, area.selectionStart).split("\n").length - 1);
    expect(caretLine, "the caret is on the line below the heading").toBe(1);
    await expect(entryLine(page)).toContainText(`This is the day note of ${dayAndMonth(t, true)} (by its name)`);
    expect((await aliceApi.getNote(path)).content).toMatch(new RegExp(`^# ${longDay(t)}\\n`));
  } finally {
    await aliceApi.deleteNote(path).catch(() => {});
  }
});

test("calendar #5: in discreet Home, Add to calendar… with a time asks nothing about folders and leaves the note where it is @calendar @serial", async ({ freshSession }, testInfo) => {
  const t = today();
  const day = addDays(t, 6);
  const original = withoutCr(readFileSync(join(fixturesDir, "Home", "Zahnarzt.md"), "utf8"));
  const { context, page } = await freshSession("alice");
  await dismissInstallHint(page);
  let home: Awaited<ReturnType<typeof makeAgent>> | undefined;
  try {
    await revealDiscreet(page);
    // An agent for Home, made while this session shows it, puts the note right at the end whatever happened.
    home = await makeAgent({ Home: "write" }, "home", context);
    const panel = await treePanel(page, testInfo);
    await switchVault(page, panel, "Home");
    const answered = noteEntryAnswered(page);
    await panel.getByRole("button", { name: "Zahnarzt", exact: true }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Zahnarzt" })).toBeVisible();
    await answered;

    await (await noteActions(page)).getByRole("menuitem", { name: "Add to calendar…" }).click();
    const dialog = page.getByRole("dialog", { name: "Add to calendar" });
    await expect(dialog.getByRole("group", { name: "When" })).toBeVisible();
    await expect(dialog).not.toContainText(/folder/i);
    await expect(dialog.getByRole("combobox")).toHaveCount(0);
    await dialog.getByRole("group", { name: "When" }).getByLabel("Day").fill(day);
    await dialog.getByRole("group", { name: "When" }).getByLabel("Time").fill("14:00");
    await dialog.getByRole("button", { name: "Save" }).click();
    await expect(dialog).toBeHidden();

    await expect(entryLine(page)).toHaveText(`📅 ${shortDay(day, true)} · 14:00`);
    const written = await guarded(() => context.request.get("/api/notes/Home/Zahnarzt.md"), "GET Home's note");
    expect(written.status(), "the note stays at /Home/Zahnarzt.md").toBe(200);
    expect(((await written.json()) as { content: string }).content).toBe(original.replace("tags: [health]\n", `tags: [health]\nwhen: ${day}T14:00\n`));
    const tree = JSON.stringify(await (await guarded(() => context.request.get("/api/vaults/Home/tree"), "GET Home's tree")).json());
    expect(tree, "no Calendar folder was made").not.toContain("/Home/Calendar");

    await (await noteActions(page)).getByRole("menuitem", { name: "Remove from calendar" }).click();
    await expect(page.getByText("Removed from the calendar.")).toBeVisible();
    await expect(entryLine(page)).toHaveCount(0);
    expect(((await (await guarded(() => context.request.get("/api/notes/Home/Zahnarzt.md"), "GET Home's note")).json()) as { content: string }).content).toBe(original);
  } finally {
    await endHidden(page, home);
  }
});

/** The end of a discreet step, whatever happened: Home's note out of the calendar, the key revoked, Home hidden. */
async function endHidden(page: Page, home: Awaited<ReturnType<typeof makeAgent>> | undefined) {
  try {
    try {
      if (home) await home.mcp.sh("calendar unset /Home/Zahnarzt.md");
    } finally {
      await home?.dispose();
    }
  } finally {
    try {
      await page.goto("/");
      await expect(page.getByRole("button", { name: /^Switch vault|^Open file tree$/ }).first()).toBeVisible();
      if (await page.getByRole("button", { name: "Hide discreet vaults" }).count()) await hideDiscreet(page);
    } finally {
      // Hidden even when the page didn't render: the call ✕ makes.
      await hideThroughApi(page);
    }
  }
}

const withoutCr = (text: string) => text.replace(/\r\n/g, "\n");

test("calendar #7: What's on lists the open tasks with vault, icon and a calm span, and the badge counts them everywhere @calendar @serial @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const t = today();
  await aliceApi.putNote(`/Work/${folder}/Renew certificate.md`, note({ when: addDays(t, 7), task: "open", upcoming: "7d" }, "# Renew certificate\n"));
  await aliceApi.putNote(`/Work/${folder}/Pay invoice.md`, note({ when: addDays(t, -1), task: "open" }, "# Pay invoice\n"));
  const before = await whatsOn(aliceApi);
  const others = before.items.filter((i) => i.state === "overdue" && !i.path.startsWith(`/Work/${folder}/`)).map((i) => i.path);
  expect(others, "nothing else overdue in the vaults the session shows").toEqual([]);
  const work = before.vaults.find((v) => v.vault === "Work")!;

  // Midnight in Vienna may pass after the seeding: the spans count from the day the page is read on.
  const shown = today();
  const daysFrom = (a: string, b: string) => Math.round((Date.parse(b) - Date.parse(a)) / 86_400_000);
  const ahead = daysFrom(shown, addDays(t, 7));
  const behind = daysFrom(addDays(t, -1), shown);
  const overdueSpan = `overdue · ${behind} day${behind === 1 ? "" : "s"}`;
  await page.goto("/whats-on");
  const renew = itemOf(page, "Renew certificate");
  const pay = itemOf(page, "Pay invoice");
  await expect(renew).toContainText("Work");
  await expect(renew).toContainText(`upcoming · in ${ahead} days`);
  await expect(pay).toContainText("Work");
  await expect(pay).toContainText(overdueSpan);
  for (const item of [renew, pay]) await expect(item.locator("svg").first()).toBeVisible();
  // The warning colour on the words, calmly: no red box around the entry.
  const overdueWords = pay.getByText(overdueSpan);
  expect(await overdueWords.evaluate((e) => getComputedStyle(e).color)).toBe(await warningColour(page));
  const box = (l: typeof pay) => l.evaluate((e) => { const s = getComputedStyle(e); return [s.backgroundColor, s.borderTopColor, s.borderTopWidth].join(" "); });
  expect(await box(pay), "the overdue entry's box is the same as any other's").toBe(await box(renew));
  const count = await whatsOnItems(page).count();
  expect(count).toBe(before.count);

  // The badge: on What's on, on Work's Calendar, beside Work in the switcher, and on the phone dock's Calendar.
  await page.goto("/");
  if (onPhone(testInfo)) {
    await expect(page.getByRole("navigation", { name: "Vault actions" }).getByRole("button", { name: "Calendar" })).toHaveAccessibleDescription(badgeWords(count, true));
    await expect(page.getByRole("button", { name: "Open file tree" })).not.toHaveAttribute("aria-describedby");
  }
  const { panel, link } = await whatsOnLink(page, testInfo);
  await expect(link).toHaveAccessibleName(named("What's on", count, true));
  const badge = link.getByText(badgeWords(count, true)).locator("..");
  expect(await badge.evaluate((e) => getComputedStyle(e).backgroundColor), "warning-coloured while something is overdue").toBe(await warningColour(page));
  await switchVault(page, panel, "Work");
  await panel.getByRole("button", { name: "Actions for Work", exact: true }).click();
  await expect(page.getByRole("menuitem", { name: /^Calendar/ })).toHaveAccessibleName(named("Calendar", work.count, true));
  await page.keyboard.press("Escape");
  await panel.getByRole("button", { name: /^Switch vault/ }).click();
  await expect(page.getByRole("dialog", { name: "Vaults" }).getByRole("button", { name: /^Work\b/ })).toHaveAccessibleName(named("Work", work.count, true));
  await page.keyboard.press("Escape");

  // Closing the overdue one: the badge counts one less and turns calm.
  await page.goto("/whats-on");
  await pay.getByRole("checkbox", { name: "To do: Pay invoice" }).click();
  await expect(pay).toHaveCount(0);
  await expect(whatsOnItems(page)).toHaveCount(count - 1);
  const after = await whatsOnLink(page, testInfo);
  await expect(after.link).toHaveAccessibleName(named("What's on", count - 1, false));
  if (count - 1 > 0) {
    const calm = after.link.getByText(badgeWords(count - 1, false), { exact: true }).locator("..");
    expect(await calm.evaluate((e) => getComputedStyle(e).backgroundColor), "calm once nothing is overdue").not.toBe(await warningColour(page));
  }
});

test("calendar #9: the month is a stack of weeks, the grid marks weekends and days with entries, and a reload keeps both @calendar @serial", async ({ page, aliceApi, folder }) => {
  const t = today();
  const tag = tagOf(folder);
  const month = t.slice(0, 7);
  await aliceApi.putNote(`/Work/${folder}/Today's entry.md`, note({ when: t, tags: `[${tag}]` }, "# Today's entry\n"));
  const mondays: string[] = [];
  for (let m = monday(`${month}-01`); m.slice(0, 7) <= month; m = addDays(m, 7)) mondays.push(m);
  try {
    await page.goto(weekUrl("Work", t, tag));
    await page.getByRole("group", { name: "View" }).getByRole("button", { name: "Month" }).click();
    await expect(page).toHaveURL(new RegExp(`month=${month}`));
    await expect(page.getByRole("banner").getByRole("heading", { level: 1 })).toHaveText(monthTitle(t));

    const checkMonth = async () => {
      const weeks = page.getByRole("main").getByRole("heading", { level: 2 });
      await expect(weeks).toHaveText(mondays.map((m) => (m === monday(t) ? `${shortWeekTitle(m)}this week` : shortWeekTitle(m))));
      for (const m of mondays) {
        const week = page.getByRole("region", { name: new RegExp(`^${shortWeekTitle(m)}`) });
        if (m === monday(t)) {
          await expect(week.getByText("this week", { exact: true })).toBeVisible();
          // Empty days left out: only today, which has the entry.
          await expect(week.getByRole("heading", { level: 3 })).toHaveText([shortDay(t)]);
          await expect(week.getByRole("link", { name: "Today's entry" })).toBeVisible();
        } else {
          await expect(week.getByText("nothing yet", { exact: true })).toBeVisible();
          await expect(week.getByRole("heading", { level: 3 })).toHaveCount(0);
        }
      }
    };
    await checkMonth();

    await page.getByRole("button", { name: "Show grid" }).click();
    const grid = page.getByRole("table");
    const checkGrid = async () => {
      await expect(grid.locator("caption")).toHaveText(monthTitle(t));
      await expect(grid.getByRole("rowheader")).toHaveText(mondays.map((m) => String(isoWeek(m).week)));
      // A week without today and without the entry, so neither marks a cell.
      const quiet = mondays.findIndex((m) => m !== monday(t));
      const backgrounds = await grid.locator("tbody tr").nth(quiet).locator("td").evaluateAll((cells) => cells.map((c) => getComputedStyle(c).backgroundColor));
      expect(backgrounds.length).toBe(7);
      expect(new Set(backgrounds.slice(0, 5)).size, "the weekdays alike").toBe(1);
      expect(backgrounds[5], "Saturday apart from the weekdays").not.toBe(backgrounds[0]);
      expect(backgrounds[6], "Sunday as Saturday").toBe(backgrounds[5]);
      const date = Number(t.slice(8));
      await expect(grid.getByRole("button", { name: new RegExp(`^${date}\\s*,(?: (?:Day off|Lighter day),)? has entries$`) })).toHaveAttribute("aria-current", "date");
      await expect(grid.getByRole("button", { name: /has entries/ })).toHaveCount(1);
    };
    await checkGrid();
    await expect(page.getByRole("button", { name: "Show grid" })).toHaveAttribute("aria-pressed", "true");

    // The calendar without a range in its address opens as it was left: the month, with the grid.
    await page.goto(`/calendar/Work?tag=${tag}`);
    await expect(page).toHaveURL(new RegExp(`month=${month}`));
    await checkMonth();
    await checkGrid();
    await page.reload();
    await checkMonth();
    await checkGrid();
  } finally {
    // Alice's view of Work's calendar is hers, for every test: back to the week without the grid.
    await aliceApi.json("PUT", "/api/vaults/Work/calendar/view", { view: "week", grid: false });
  }
});


test("calendar #11: when: tomorrow is under Dates I couldn't read with Fix…, What's on leads there, and nothing says problem @calendar @serial", async ({ page, aliceApi, folder }) => {
  const t = today();
  const path = `/Work/${folder}/Tomorrow.md`;
  await aliceApi.putNote(path, "---\nwhen: tomorrow\n---\n# Tomorrow\n");
  expect((await whatsOn(aliceApi)).couldntRead, "this is the only date that can't be read").toBe(1);

  await page.goto(weekUrl("Work", t));
  const unread = page.getByRole("region", { name: "Dates I couldn't read" });
  const item = unread.getByRole("listitem").filter({ hasText: "Tomorrow" });
  await expect(item).toHaveText(/^Tomorrow — when: tomorrow – I need a date like \d{4}-\d{2}-\d{2}\s*Fix…$/);
  await expect(item.locator("code")).toHaveText("when: tomorrow");
  await expect(page.locator("body")).not.toContainText(/problem/i);
  await item.getByRole("button", { name: "Fix… Tomorrow" }).click();
  const dialog = page.getByRole("dialog", { name: "Edit date" });
  await expect(dialog.getByRole("group", { name: "When" }).getByLabel("Day")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();

  await page.goto("/whats-on");
  const line = page.getByRole("link", { name: "1 date I couldn't read", exact: true });
  await expect(line).toBeVisible();
  await expect(page.locator("body")).not.toContainText(/problem/i);
  await line.click();
  await expect(page).toHaveURL(/\/calendar\/Work#couldnt-read$/);
  await expect(page.getByRole("heading", { name: "Dates I couldn't read" })).toBeInViewport();
  await expect(page.getByRole("region", { name: "Dates I couldn't read" }).getByRole("listitem").filter({ hasText: "Tomorrow" })).toBeVisible();
  await expect(page.locator("body")).not.toContainText(/problem/i);
});

test("calendar #12: the tag filter kumbu keeps #kumbu and #kumbu/sub in the week, the month and What's on @calendar @serial", async ({ page, seedAs, folder }) => {
  const t = today();
  await seedAs("tags/calendar/Tags", `/Work/${folder}/Tags`);
  const kept = ["Kumbu", "Kumbu sub"];
  const titles = (page: Page) => page.getByRole("main").getByRole("listitem").getByRole("link").allTextContents();
  const filter = async () => {
    await page.getByRole("search", { name: "Tag filter" }).getByLabel("Tag").fill("kumbu");
    await page.getByRole("search", { name: "Tag filter" }).getByRole("button", { name: "Filter" }).click();
    await expect(page).toHaveURL(/[?&]tag=kumbu(&|$)/);
    await expect(page.getByRole("button", { name: "#kumbu (show everything)", exact: true })).toBeVisible();
  };

  await page.goto(weekUrl("Work", t));
  for (const title of ["Untagged", "Kumbunot", ...kept]) await expect(page.getByRole("link", { name: title, exact: true })).toBeVisible();
  await filter();
  await expect(page.getByRole("link", { name: "Kumbu sub", exact: true })).toBeVisible();
  expect((await titles(page)).sort(), "the week").toEqual(kept);

  await page.goto(`/calendar/Work?month=${t.slice(0, 7)}&tag=kumbu`);
  await expect(page.getByRole("banner").getByRole("heading", { level: 1 })).toHaveText(monthTitle(t));
  await expect(page.getByRole("link", { name: "Kumbu sub", exact: true })).toBeVisible();
  expect((await titles(page)).filter((l) => !/^Week \d+/.test(l)).sort(), "the month").toEqual(kept);

  await page.goto("/whats-on");
  await expect(page.getByRole("link", { name: "Untagged", exact: true })).toBeVisible();
  await filter();
  await expect(page.getByRole("link", { name: "Kumbu sub", exact: true })).toBeVisible();
  expect((await titles(page)).sort(), "What's on").toEqual(kept);
});

test("calendar #15: a hidden discreet vault leaves no trace in What's on, the badge, the switcher or its calendar's address @calendar @serial", async ({ freshSession, aliceApi }, testInfo) => {
  const t = today();
  const { context, page } = await freshSession("alice");
  await dismissInstallHint(page);
  const headers = { Origin: baseURL };
  const before = await whatsOn(aliceApi);
  expect(before.overdue, "nothing overdue in the vaults the shared session shows").toBe(false);
  let home: Awaited<ReturnType<typeof makeAgent>> | undefined;
  try {
    await revealDiscreet(page);
    home = await makeAgent({ Home: "write" }, "home", context);
    // Home gets an overdue to-do through this session, which shows it.
    const set = await guarded(() => context.request.post("/api/vaults/Home/calendar/set", { data: { path: "/Home/Zahnarzt.md", when: addDays(t, -1), task: true }, headers }), "POST calendar set");
    expect(set.status()).toBe(200);

    // Shown: its calendar's address is /discreet, and What's on has its entry.
    let panel = await treePanel(page, testInfo);
    await switchVault(page, panel, "Home");
    await panel.getByRole("button", { name: "Actions for Home", exact: true }).click();
    await page.getByRole("menuitem", { name: /^Calendar/ }).click();
    await expect(page.getByRole("banner").getByText("Home", { exact: true })).toBeVisible();
    await expect(page).toHaveURL(`${baseURL}/discreet`);
    await page.getByRole("link", { name: "Back to the notes" }).click();
    panel = await treePanel(page, testInfo);
    await panel.getByRole("navigation", { name: "Account" }).getByRole("link", { name: /^What's on/ }).click();
    await expect(itemOf(page, "Zahnarzt")).toContainText("Home");
    await expect(itemOf(page, "Zahnarzt")).toContainText("overdue · 1 day");

    await page.getByRole("link", { name: "Back to the notes" }).click();
    await hideDiscreet(page);

    // Hidden: nothing of Home in What's on, the badge's count and colour, or the switcher.
    await page.goto("/whats-on");
    await expect(page.getByRole("heading", { level: 1, name: "What's on" })).toBeVisible();
    await expect(page.getByRole("main")).not.toContainText("Zahnarzt");
    await expect(page.getByRole("main")).not.toContainText("Home");
    const hidden = await whatsOn(await apiOf(context));
    expect([hidden.count, hidden.overdue, hidden.vaults.map((v) => v.vault)]).toEqual([before.count, false, before.vaults.map((v) => v.vault)]);
    const { panel: shell, link } = await whatsOnLink(page, testInfo);
    await expect(link).toHaveAccessibleName(named("What's on", before.count, false));
    if (before.count > 0) {
      const badge = link.getByText(badgeWords(before.count, false), { exact: true }).locator("..");
      expect(await badge.evaluate((e) => getComputedStyle(e).backgroundColor), "the badge's colour tells nothing of Home's overdue entry").not.toBe(await warningColour(page));
    }
    await shell.getByRole("button", { name: /^Switch vault/ }).click();
    const vaults = page.getByRole("dialog", { name: "Vaults" });
    await expect(vaults.getByRole("button", { name: /^Work\b/ })).toBeVisible();
    await expect(vaults.getByRole("button", { name: /^Home\b/ })).toHaveCount(0);
    await page.keyboard.press("Escape");

    // Its calendar answers as a vault that doesn't exist, on the screen and in the API.
    const answer = async (vault: string) => {
      await page.goto(`/calendar/${vault}`);
      const text = (await page.getByRole("main").or(page.locator("p").filter({ hasText: "There is no vault" })).first().textContent())!;
      const api = await guarded(() => context.request.get(`/api/vaults/${vault}/calendar`), "GET a vault's calendar");
      return { text: text.replaceAll(vault, "<vault>"), status: api.status(), body: (await api.text()).replaceAll(vault, "<vault>") };
    };
    expect(await answer("Home")).toEqual(await answer("Nowhere"));

    // Shown again with the PIN: Home's entry is back.
    await page.goto("/");
    await revealDiscreet(page);
    panel = await treePanel(page, testInfo);
    await panel.getByRole("navigation", { name: "Account" }).getByRole("link", { name: /^What's on/ }).click();
    await expect(itemOf(page, "Zahnarzt")).toContainText("Home");
    const unset = await guarded(() => context.request.post("/api/vaults/Home/calendar/unset", { data: { path: "/Home/Zahnarzt.md" }, headers }), "POST calendar unset");
    expect(unset.status()).toBe(200);
    await page.getByRole("link", { name: "Back to the notes" }).click();
    await hideDiscreet(page);
  } finally {
    await endHidden(page, home);
  }
});

/** An Api over a session's own request context, for reading what that session is shown. */
const apiOf = (context: BrowserContext) => Api.from(context.request, "alice");

test("calendar #16: a board card shows its column, Move to Done only with one archivable column, and Later moves only its due @calendar @serial", async ({ page, aliceApi, mcp, seedAs }) => {
  const t = today();
  const due = addDays(t, 6);
  const card = "/Work/Board/Pump check.md";
  expect((await aliceApi.send("GET", "/api/notes/Work/Board/Board.md")).status(), "Work has no board yet").toBe(404);
  await seedAs("tags/calendar/Board", "/Work/Board");
  try {
    const before = (await aliceApi.getNote(card)).content;
    // In the calendar: the week of its due date, in that day's column, as a card.
    await page.goto(weekUrl("Work", due));
    const dueColumn = page.getByRole("region", { name: new RegExp(`^${dayHeading(due)},`) });
    const entry = dueColumn.getByRole("listitem").filter({ has: page.getByRole("link", { name: "Pump check", exact: true }) });
    await expect(entry).toBeVisible();
    await expect(entry).toContainText("Card");

    await page.goto(notePage(card));
    const line = entryLine(page);
    await expect(line).toHaveText(`📅 ${shortDay(due, true)} · In progress Move to Done Later`);
    await expect(line.getByRole("button", { name: "Move to Done" })).toBeVisible();

    const close = await mcp.sh(`calendar close "${card}"`);
    expect(close.isError).toBe(true);
    expect(close.text).toMatch(/board tool/);

    await line.getByRole("button", { name: "Later: Pump check" }).click();
    const menu = page.getByRole("menu", { name: "Later: Pump check" });
    await expect(menu.getByRole("group")).toHaveCount(0);
    await menu.getByRole("menuitem", { name: "+1 day" }).click();
    await expect(page.getByRole("status", { name: "Moved" })).toHaveText(`Moved to ${shortDay(addDays(due, 1), true)} · Undo`);
    // Only its due: the column and the rank lines as they were.
    expect((await aliceApi.getNote(card)).content).toBe(before.replace(`due: ${due}`, `due: ${addDays(due, 1)}`));

    await aliceApi.putNote("/Work/Board/Board.md", readFileSync(join(fixturesDir, "tags", "calendar", "Board2", "Board.md"), "utf8"));
    await page.reload();
    await expect(line).toHaveText(`📅 ${shortDay(addDays(due, 1), true)} · In progress Open the board Later`);
    await expect(line.getByRole("button", { name: /^Move to/ })).toHaveCount(0);
  } finally {
    await aliceApi.deleteNote(card).catch(() => {});
    await aliceApi.deleteNote("/Work/Board/Board.md").catch(() => {});
  }
});

test("flags #20: Today's day note isn't counted by What's on and the badge until it is a to-do @calendar @serial", async ({ page, aliceApi }, testInfo) => {
  const t = today();
  const path = `/Work/Calendar/${t}.md`;
  expect((await aliceApi.send("GET", `/api/notes/Work/Calendar/${t}.md`)).status(), "no day note of today yet").toBe(404);
  const before = await whatsOn(aliceApi);
  try {
    await page.goto(weekUrl("Work", t));
    await page.getByRole("button", { name: "+ Today", exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/notes/Work/Calendar/${t}\\.md$`));
    await expect(page.getByLabel("Markdown source", { exact: true })).toHaveValue(new RegExp(`^# ${longDay(t)}`));

    await page.goto("/whats-on");
    await expect(page.getByRole("heading", { level: 1, name: "What's on" })).toBeVisible();
    await expect(page.getByRole("link", { name: t, exact: true })).toHaveCount(0);
    expect((await whatsOn(aliceApi)).count).toBe(before.count);
    await expect((await whatsOnLink(page, testInfo)).link).toHaveAccessibleName(named("What's on", before.count, before.overdue));

    await aliceApi.putNote(path, `---\ntask: open\n---\n${(await aliceApi.getNote(path)).content}`);
    await page.goto("/whats-on");
    await expect(page.getByRole("link", { name: t, exact: true })).toBeVisible();
    expect((await whatsOn(aliceApi)).count).toBe(before.count + 1);
    await expect((await whatsOnLink(page, testInfo)).link).toHaveAccessibleName(named("What's on", before.count + 1, before.overdue));
  } finally {
    await aliceApi.deleteNote(path).catch(() => {});
  }
});

test("flags #21: a timed entry reads today · HH:MM before its time and ongoing · until HH:MM within its window @calendar @serial", async ({ page, aliceApi, folder }) => {
  const [hour, minute] = nowTime().split(":").map(Number);
  const now = hour * 60 + minute;
  test.skip(now < 120 || now >= 22 * 60, "within two hours of midnight in Vienna the times would cross into another day");
  const t = today();
  const tag = tagOf(folder);
  await aliceApi.putNote(`/Work/${folder}/Later today.md`, note({ when: `${t}T${clock(now + 60)}`, until: `${t}T${clock(now + 120)}`, tags: `[${tag}]` }, "# Later today\n"));
  await aliceApi.putNote(`/Work/${folder}/Right now.md`, note({ when: `${t}T${clock(now - 30)}`, until: `${t}T${clock(now + 30)}`, tags: `[${tag}]` }, "# Right now\n"));

  await page.goto(`/whats-on?tag=${tag}`);
  await expect(itemOf(page, "Later today")).toContainText(`today · ${clock(now + 60)}`);
  await expect(itemOf(page, "Right now")).toContainText(`ongoing · until ${clock(now + 30)}`);
});

test("flags #22: a to-do overdue by more than a year says years @calendar @serial", async ({ page, aliceApi, folder }) => {
  const t = today();
  const tag = tagOf(folder);
  await aliceApi.putNote(`/Work/${folder}/Thirteen months.md`, note({ when: addMonths(t, -13), task: "open", tags: `[${tag}]` }, "# Thirteen months\n"));
  await aliceApi.putNote(`/Work/${folder}/Three years.md`, note({ when: addYears(t, -3), task: "open", tags: `[${tag}]` }, "# Three years\n"));

  await page.goto(`/whats-on?tag=${tag}`);
  await expect(itemOf(page, "Thirteen months")).toContainText("overdue · 1 year");
  await expect(itemOf(page, "Three years")).toContainText("overdue · 3 years");
});

