// SPDX-License-Identifier: AGPL-3.0-or-later
// The UI round's example (spec 2026-11-15-ui-round-0-5-0-design.md §3): Alice in Work on her phone (steps 1–4) and on
// the laptop (step 5), in the light and the dark scheme. @serial: it reads What's on's count and the flags in use
// across Work, and makes Work's board when Work has none.
import type { Locator, Page } from "@playwright/test";
import { randomBytes } from "node:crypto";
import type { Api } from "../support/api";
import { addDays, nowTime, today } from "../support/time";
import { cleanFolder, dismissInstallHint, entryLine, expect, note, noteActions, onPhone, switchVault, test, treePanel } from "../calendar/helpers";

/** The flags in the app's order, with the words their chips say (web/src/flags/api.ts, FlagIcon.tsx). */
const FLAGS = { red: "Red", green: "Green", blue: "Blue", purple: "Purple", yellow: "Yellow", white: "White", done: "Done", important: "Important" } as const;

type TreeFolder = { path: string; folders: TreeFolder[]; notes: { path: string; flag?: string | null }[] };

/** The flags some note of Work carries, in the app's order: the chips Find by name shows besides Any flag. */
async function flagsInUse(api: Api) {
  const tree = await api.json<{ root: TreeFolder }>("GET", "/api/vaults/Work/tree");
  const used = new Set<string>();
  const walk = (folder: TreeFolder) => {
    for (const n of folder.notes) if (n.flag) used.add(n.flag);
    folder.folders.forEach(walk);
  };
  walk(tree.root);
  return Object.entries(FLAGS).filter(([flag]) => used.has(flag));
}

type WhatsOn = { count: number; overdue: boolean };
/** The badge's words for a screen reader (web/src/calendar/Badge.tsx). */
const badgeLabel = ({ count, overdue }: WhatsOn) => (overdue ? `${count} on, ${count === 1 ? "overdue" : "some overdue"}` : `${count} on`);

/** "17:16" for a number of minutes after midnight. */
const clock = (minutes: number) => `${String(Math.floor(minutes / 60)).padStart(2, "0")}:${String(minutes % 60).padStart(2, "0")}`;

/** Each control's accessible name, in the order the row holds them. */
const namesIn = (row: Locator) => row.locator("button, a").evaluateAll((els) => els.map((e) => e.getAttribute("aria-label") ?? e.textContent?.trim() ?? ""));

/** Neither the row nor anything in it scrolls sideways. */
async function fitsItsWidth(box: Locator, what: string) {
  const { scroll, client } = await box.evaluate((e) => ({ scroll: e.scrollWidth, client: e.clientWidth }));
  expect(scroll, `${what} doesn't scroll sideways`).toBeLessThanOrEqual(client);
}

async function atLeast48(targets: Locator) {
  for (const target of await targets.all()) {
    const box = (await target.boundingBox())!;
    const name = (await target.getAttribute("aria-label")) ?? (await target.textContent());
    expect(box.width, `${name}: at least 48 px wide`).toBeGreaterThanOrEqual(48);
    expect(box.height, `${name}: at least 48 px high`).toBeGreaterThanOrEqual(48);
  }
}

/** Steps 1–4, at 375 px with touch. */
async function onThePhone(page: Page, api: Api, folder: string) {
  const [hour, minute] = nowTime().split(":").map(Number);
  const now = hour * 60 + minute;
  test.skip(now >= 23 * 60, "within an hour of midnight in Vienna, later today would cross into another day");
  const t = today();

  // Two notes carry red and blue; Dentist is later today. The board, if Work has none, only for this test.
  await api.putNote(`/Work/${folder}/Valve.md`, note({ tags: "[flag/red]" }, "# Valve\n"));
  await api.putNote(`/Work/${folder}/Pump.md`, note({ tags: "[flag/blue]" }, "# Pump\n"));
  const dentist = `/Work/${folder}/Dentist.md`;
  await api.putNote(dentist, note({ when: `${t}T${clock(now + 30)}` }, "# Dentist\n"));
  const boardMade = (await api.send("GET", "/api/notes/Work/Board/Board.md")).status() === 404;
  if (boardMade) await api.json("POST", "/api/vaults/Work/board");
  const title = `Standup ${randomBytes(3).toString("hex")}`;
  let standup: string | undefined;
  try {
    const flags = await flagsInUse(api);
    expect(flags.map(([f]) => f), "red and blue are in use").toEqual(expect.arrayContaining(["red", "blue"]));

    // 1. The drawer: + New note, Calendar, Board, Find and ⋯ in one row; Upload, Sort and Recent changes at the top of ⋯.
    await page.goto("/");
    const panel = await treePanel(page, test.info());
    await switchVault(page, panel, "Work");
    const tools = panel.getByRole("group", { name: "Tools for this vault" });
    await expect.poll(() => namesIn(tools)).toEqual(["New note", "Calendar", "Board", "Find in this vault", "Actions for Work"]);
    await fitsItsWidth(tools, "the toolbar");
    await atLeast48(tools.locator("button, a"));

    await tools.getByRole("button", { name: "Actions for Work" }).click();
    const menu = page.getByRole("menu", { name: "Actions for Work" });
    const items = menu.getByRole("menuitem");
    await expect(items.nth(0)).toHaveAccessibleName("Upload");
    await expect(items.nth(1)).toHaveAccessibleName(/^Sorted by (name|last changed)\. Sort by (last changed|name)$/);
    await expect(items.nth(2)).toHaveAccessibleName("Recent changes");
    await page.keyboard.press("Escape");
    await expect(menu).toBeHidden();

    // Find: the chips of the flags in use only, wrapping rather than scrolling.
    await tools.getByRole("button", { name: "Find in this vault" }).click();
    const chips = panel.getByRole("group", { name: "Find by flag" });
    await expect.poll(() => namesIn(chips)).toEqual(["Any flag", ...flags.map(([, label]) => label)]);
    await fitsItsWidth(chips, "the flag chips");
    await panel.getByRole("button", { name: "Close the search" }).click();

    // 2. + New note, the block typed below the title, Done: the hint, and Move to properties.
    await tools.getByRole("button", { name: "New note" }).click();
    const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
    await expect(source).toBeVisible();
    const typed = `# ${title}\n---\naliases: [daily]\n---\n`;
    await source.fill(typed);
    await page.getByRole("button", { name: "Done" }).click();
    await expect(page.getByRole("button", { name: "Done" })).toHaveCount(0);
    // The title names the note once it is written.
    await expect(page).toHaveURL(new RegExp(`/notes/Work/${encodeURIComponent(title)}\\.md$`));
    standup = `/Work/${title}.md`;
    expect((await api.getNote(standup)).content).toBe(typed);

    const article = page.locator("article");
    const hint = article.getByRole("note").filter({ hasText: "Properties go at the very top of a note." });
    await expect(hint).toBeVisible();
    expect((await hint.boundingBox())!.y, "the hint stands above the rule").toBeLessThan((await article.locator("hr").first().boundingBox())!.y);
    await hint.getByRole("button", { name: "Move to properties" }).click();
    await expect.poll(async () => (await api.getNote(standup!)).content).toBe(`---\naliases: [daily]\n---\n# ${title}\n`);
    await expect(hint).toHaveCount(0);
    const box = article.getByLabel("Properties", { exact: true });
    await expect(box).toContainText("aliases");

    // 3. + Add property: owner, Alice, as the frontmatter's last line; nothing else changes.
    await article.getByRole("button", { name: "Add property" }).click();
    const dialog = page.getByRole("dialog", { name: "Add property" });
    await dialog.getByLabel("Name").fill("owner");
    await dialog.getByLabel("Value").fill("Alice");
    await dialog.getByRole("button", { name: "Add" }).click();
    await expect(dialog).toBeHidden();
    await expect.poll(async () => (await api.getNote(standup!)).content).toBe(`---\naliases: [daily]\nowner: Alice\n---\n# ${title}\n`);
    await expect(box).toContainText("owner");

    // 4. The dock's Calendar with What's on's count, Dentist, ‹ What's on, Make it a to-do, and back.
    const whatsOn = await api.json<WhatsOn>("GET", "/api/calendar/whats-on");
    expect(whatsOn.count, "Dentist is on").toBeGreaterThan(0);
    const calendar = page.getByRole("navigation", { name: "Vault actions" }).getByRole("button", { name: "Calendar", exact: true });
    await expect(calendar).toHaveAccessibleDescription(badgeLabel(whatsOn));
    await expect(calendar).toContainText(whatsOn.count > 99 ? "99+" : String(whatsOn.count));
    await atLeast48(calendar);
    await calendar.click();
    await expect(page).toHaveURL(/\/whats-on$/);
    await expect(page.getByRole("heading", { level: 1, name: "What's on" })).toBeVisible();

    await page.getByRole("main").getByRole("link", { name: "Dentist", exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Dentist\\.md$`));
    const back = page.getByRole("navigation", { name: "Back" }).getByRole("link", { name: "Back to What's on" });
    await expect(back).toHaveText("What's on");
    await expect(entryLine(page)).toBeVisible();
    expect((await back.boundingBox())!.y, "‹ What's on stands above the calendar line").toBeLessThan((await entryLine(page).boundingBox())!.y);
    await atLeast48(back);

    await (await noteActions(page)).getByRole("menuitem", { name: "Make it a to-do" }).click();
    await expect(entryLine(page).getByRole("checkbox", { name: "To do" })).toHaveAttribute("aria-checked", "false");
    await expect.poll(async () => (await api.getNote(dentist)).content).toMatch(/^task: open$/m);

    await back.click();
    await expect(page).toHaveURL(/\/whats-on$/);
    await expect(page.getByRole("heading", { level: 1, name: "What's on" })).toBeVisible();
  } finally {
    if (standup) await api.deleteNote(standup).catch(() => {});
    if (boardMade) await api.deleteNote("/Work/Board/Board.md").catch(() => {});
  }
}

/** Step 5, at 1280 px: the folder's name opens the calendar, its chevron the folder. */
async function onTheLaptop(page: Page, api: Api) {
  // A day note long past, so that the folder has a note to show and What's on stays as it is.
  const day = addDays(today(), -400 - randomBytes(1)[0]);
  const path = `/Work/Calendar/${day}.md`;
  await api.putNote(path, `# ${day}\n`);
  try {
    await page.goto("/");
    const sidebar = await treePanel(page, test.info());
    await switchVault(page, sidebar, "Work");
    await sidebar.getByRole("button", { name: "Calendar, open the calendar", exact: true }).click();
    await expect(page).toHaveURL(/\/calendar\/Work(\?.*)?$/);
    await expect(page.getByRole("banner").getByText("Work", { exact: true })).toBeVisible();

    // The calendar has the page to itself; back in the notes, the chevron opens the folder and nothing else.
    await page.goBack();
    await expect(sidebar).toBeVisible();
    const chevron = sidebar.getByRole("button", { name: "Show the folder Calendar", exact: true });
    await expect(chevron).toHaveAttribute("aria-expanded", "false");
    await chevron.click();
    await expect(sidebar.getByRole("button", { name: "Hide the folder Calendar", exact: true })).toHaveAttribute("aria-expanded", "true");
    await expect(sidebar.getByRole("button", { name: new RegExp(`^${day}\\b`) })).toBeVisible();
    await expect(page).not.toHaveURL(/\/calendar\//);
  } finally {
    await api.deleteNote(path).catch(() => {});
  }
}

test.beforeEach(async ({ page }) => {
  await dismissInstallHint(page);
});

test.afterEach(async ({ aliceApi, folder }) => {
  await cleanFolder(aliceApi, folder);
});

for (const scheme of ["light", "dark"] as const) {
  test(`ui round §3, ${scheme}: the toolbar, properties moved and added, the dock's Calendar and the way back, the Calendar folder @properties @serial @phone`, async ({ page, aliceApi, folder }, testInfo) => {
    await page.emulateMedia({ colorScheme: scheme });
    if (onPhone(testInfo)) await onThePhone(page, aliceApi, folder);
    else await onTheLaptop(page, aliceApi);
  });
}
