// SPDX-License-Identifier: AGPL-3.0-or-later
// The command palette and favourites (spec 2026-11-18-command-palette-design.md; PRs #237, #248) and a note's actions
// (spec 2026-11-18-note-actions-design.md; PR #238). The hidden discreet vault's rows and favourites are
// e2e/discreet/'s.
//
// Promises in scope, and the test that covers each:
// - Ctrl+P opens the palette and closes it again, in Write mode too, focus back where it was ...... palette #1
// - the star in the top bar ("Favourites and commands (Ctrl+P)") opens it; Esc and, on the phone, ✕ close it .. palette #1
// - commands, vaults with Board/Calendar/Gallery, notes by name; words in any order, case ignored ... palette #2
// - the last row "Search notes for ‘…’" hands the text to search ................................. palette #2
// - the empty palette: favourites, then Recent (the notes opened lately); the hint without favourites .. palette #3, #4
// - a row's star and Ctrl+D make a favourite that stays on top while typing, also after a reload .. palette #3 (@serial)
// - a note's "Add to favourites" / "Remove from favourites" in its menu ........................... palette #4 (@serial)
// - the group Note: the menu's items, shown once something is typed, filtered by group and name
//   ("no pub" finds Publish…), no star, Ctrl+D does nothing; the row runs what the menu runs ...... palette #5
// - Duplicate: "Copy of …" beside it without the calendar's keys, then "… 2"; the original stays open,
//   the notice's Open goes to the copy ............................................................ palette #6
// - Shift+F10 opens the note's menu with the items that fit the mode, in Write mode at the caret ..... palette #7
// - the keyboard menu: Esc closes it, the focus and caret back where they were ..................... palette #8 (F-12)
// - the menu flips above where there is no room below (PointMenu), on screen whatever the point ..... palette #9 (F-13)
// - in Read mode the same list in the same place as the dock's ⋯ menu ............................... palette #10 (F-14)
// Left out: Cmd+P on a Mac (the projects are Linux Chromium; Vitest covers it); the 50-note cap and the ranking
// within a group (Vitest, and other tests' notes in Work would make counts fragile); stale favourites and the
// 409 retry (need a second device's write timed against the tree; xUnit and Vitest cover them); the context-menu key
// (Chromium in Playwright doesn't deliver a ContextMenu keydown the same way on every platform; Shift+F10 is the same handler).
import type { Locator, Page } from "@playwright/test";
import { encodePath, type Api } from "../support/api";
import { secrets } from "../support/env";
import { reservePassword } from "../support/session";
import { expect, onPhone, shot, test } from "../support/test";

const noteUrl = (path: string) => `/notes/${encodePath(path)}`;

async function openNote(page: Page, path: string) {
  const hint = page.getByRole("region", { name: "Install Kumbu² as an app" });
  await page.addLocatorHandler(hint, async () => {
    await hint.getByRole("button", { name: "Not now" }).click();
  });
  await page.goto(noteUrl(path));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

const palette = (page: Page) => page.getByRole("dialog", { name: "Favourites and commands" });
const field = (page: Page) => palette(page).getByRole("combobox", { name: "Find a command, vault or note" });
const group = (page: Page, name: string) => palette(page).getByRole("group", { name, exact: true });
const option = (page: Page, name: string) => palette(page).getByRole("option", { name, exact: true });
const starButton = (page: Page) => page.getByRole("banner").getByRole("button", { name: "Favourites and commands (Ctrl+P)" });

/** The rows' names as a screen reader reads them: name, then place. */
const labels = (rows: Locator) => rows.evaluateAll((all) => all.map((r) => r.getAttribute("aria-label")));

/** Opens the palette with Ctrl+P and waits for its field to have the focus. */
async function openPalette(page: Page) {
  await page.keyboard.press("Control+p");
  await expect(field(page)).toBeFocused();
}

type Favourites = { favourites: { kind: string; id?: string; vault?: string; path?: string; added: string }[]; version: string };
const favourites = (api: Api) => api.json<Favourites>("GET", "/api/account/favourites");
const keyOf = (f: { kind: string; id?: string; vault?: string; path?: string }) => (f.kind === "command" ? `command:${f.id}` : f.kind === "note" ? `note:${f.vault}/${f.path}` : `${f.kind}:${f.vault}`);

/** Takes the entries keyed `keys` off Alice's list, whatever the test got to: favourites are per account. */
async function removeFavourites(api: Api, keys: string[]) {
  const now = await favourites(api);
  const kept = now.favourites.filter((f) => !keys.includes(keyOf(f)));
  if (kept.length === now.favourites.length) return;
  await api.json("PUT", "/api/account/favourites", { favourites: kept.map(({ kind, id, vault, path }) => ({ kind, id, vault, path })), version: now.version });
}

// Every note a test here makes, by name in its folder.
const made = ["Harbour", "Harbour light", "Other", "Beacon", "Copy of Harbour", "Copy of Harbour 2"];

test.afterEach(async ({ aliceApi, folder }) => {
  for (const name of made) await aliceApi.deleteNote(`/Work/${folder}/${name}.md`).catch(() => undefined);
});

test("palette #1: Ctrl+P and the star open the palette, Ctrl+P, Esc and ✕ close it, in Write mode too @palette @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n\nThe lighthouse.\n");
  await openNote(page, path);

  await openPalette(page);
  await expect(palette(page)).toBeVisible();
  await page.keyboard.press("Control+p");
  await expect(palette(page)).toHaveCount(0);

  await starButton(page).click();
  await expect(field(page)).toBeFocused();
  await shot(page, testInfo, "palette");
  if (onPhone(testInfo)) {
    // A sheet from below, the field near the thumb: below the list.
    // Once it has slid up.
    await expect.poll(async () => Math.round(await palette(page).evaluate((d) => d.getBoundingClientRect().bottom)), "flush with the bottom").toBe(page.viewportSize()!.height);
    await expect(palette(page).getByRole("heading", { name: "Favourites and commands" })).toBeVisible();
    await palette(page).getByRole("button", { name: "Close" }).click();
  } else await page.keyboard.press("Escape");
  await expect(palette(page)).toHaveCount(0);
  await expect(starButton(page), "the focus back on the star").toBeFocused();

  // Write mode: Ctrl+P is the palette's there too, not the editor's or Print; Esc gives the editor the focus back.
  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  await source.focus();
  await openPalette(page);
  await page.keyboard.press("Escape");
  await expect(palette(page)).toHaveCount(0);
  await expect(source).toBeFocused();
  await expect(source).toHaveValue("# Harbour\n\nThe lighthouse.\n");
});

test("palette #2: commands, vaults and notes are found by words in any order, and the last row hands the text to search @palette", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Harbour light.md`;
  await aliceApi.putNote(path, "# Harbour light\n");
  await openNote(page, path);

  // A command by two words' starts, case ignored.
  await openPalette(page);
  await field(page).fill("SET vau");
  await expect(group(page, "Commands").getByRole("option").first()).toHaveAccessibleName("Vaults, Settings");
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/settings\/vaults$/);
  await expect(page.getByRole("dialog", { name: "Settings" })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog", { name: "Settings" })).toHaveCount(0);

  // A vault and its board, calendar and gallery.
  await openPalette(page);
  await field(page).fill("work");
  const vaults = group(page, "Vaults");
  for (const name of ["Work, Vault", "Board, Work", "Calendar, Work", "Gallery, Work"]) await expect(vaults.getByRole("option", { name, exact: true })).toBeVisible();
  await field(page).fill("board work");
  await option(page, "Board, Work").click();
  await expect(page).toHaveURL(/\/board\/Work$/);

  // A note by its name and its place, the words the other way round.
  await openNote(page, `/Work/${folder}/Harbour light.md`);
  await aliceApi.putNote(`/Work/${folder}/Other.md`, "# Other\n");
  await openPalette(page);
  await field(page).fill(`light ${folder} HARBOUR`);
  const notes = group(page, "Notes").getByRole("option");
  await expect(notes).toHaveCount(1);
  await expect(notes).toHaveAccessibleName(`Harbour light, Work / ${folder}`);
  // A word missing from both: no row.
  await field(page).fill(`${folder} harbour nowhere`);
  await expect(group(page, "Notes")).toHaveCount(0);

  // The last row, always there once something is typed: search with the text as its query.
  const words = `${folder} harbour nowhere`;
  const last = palette(page).getByRole("option").last();
  await expect(last).toHaveAccessibleName(`Search notes for ‘${words}’`);
  await last.click();
  await expect(palette(page)).toHaveCount(0);
  await expect(page.getByRole("dialog", { name: "Search" }).getByRole("searchbox", { name: "Search" })).toHaveValue(words);
});

test("palette #3: the star and Ctrl+D make favourites that stay on top, also after a reload, and go again @palette @serial", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Harbour.md`;
  const mine = ["command:settings.appearance", `note:Work/${folder}/Harbour.md`];
  await aliceApi.putNote(path, "# Harbour\n");
  expect((await favourites(aliceApi)).favourites, "no favourite left by another test").toEqual([]);
  try {
    await openNote(page, path);
    await openPalette(page);
    // Without favourites, one line in their place.
    await expect(palette(page).getByText("Star anything here to keep it on top.")).toBeVisible();
    await expect(group(page, "Favourites")).toHaveCount(0);

    // A command's star, shown on hover.
    await field(page).fill("appearance");
    const appearance = option(page, "Appearance, Settings");
    await appearance.hover();
    await appearance.getByRole("button", { name: "Add Appearance to favourites" }).click();
    await expect.poll(() => labels(group(page, "Favourites").getByRole("option"))).toEqual(["Appearance, Settings"]);
    await expect(group(page, "Favourites").getByRole("button", { name: "Remove Appearance from favourites" })).toHaveAttribute("aria-pressed", "true");

    // Ctrl+D on the highlighted row: the note, the first and only row of its folder.
    await field(page).fill(`${folder} harbour`);
    await expect(palette(page).getByRole("option").first()).toHaveAccessibleName(`Harbour, Work / ${folder}`);
    await expect(palette(page).getByRole("option").first()).toHaveAttribute("aria-selected", "true");
    await page.keyboard.press("Control+d");
    await expect.poll(() => labels(group(page, "Favourites").getByRole("option"))).toEqual([`Harbour, Work / ${folder}`]);

    // On top while typing: Appearance before About, which comes first alphabetically among the settings.
    await field(page).fill("settings");
    await expect(palette(page).getByRole("option").first()).toHaveAccessibleName("Appearance, Settings");
    await expect(group(page, "Commands").getByRole("option", { name: "About, Settings", exact: true })).toBeVisible();

    // Kept on the server, in the order added, and shown so after a reload.
    await expect.poll(async () => (await favourites(aliceApi)).favourites.map(keyOf)).toEqual(mine);
    await page.keyboard.press("Escape");
    await page.reload();
    await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
    await openPalette(page);
    await expect.poll(() => labels(group(page, "Favourites").getByRole("option"))).toEqual(["Appearance, Settings", `Harbour, Work / ${folder}`]);
    await expect(palette(page).getByText("Star anything here to keep it on top.")).toHaveCount(0);

    // Off again: Ctrl+D on the first, the star on the second.
    await page.keyboard.press("Control+d");
    await expect.poll(() => labels(group(page, "Favourites").getByRole("option"))).toEqual([`Harbour, Work / ${folder}`]);
    await group(page, "Favourites").getByRole("button", { name: "Remove Harbour from favourites" }).click();
    await expect(group(page, "Favourites")).toHaveCount(0);
    await expect(palette(page).getByText("Star anything here to keep it on top.")).toBeVisible();
    await expect.poll(async () => (await favourites(aliceApi)).favourites).toEqual([]);
  } finally {
    await removeFavourites(aliceApi, mine);
  }
});

test("palette #4: a note's menu adds it to the favourites and removes it, and the empty palette lists it above Recent @palette @serial", async ({ page, aliceApi, folder }) => {
  const harbour = `/Work/${folder}/Harbour.md`;
  const beacon = `/Work/${folder}/Beacon.md`;
  const mine = [`note:Work/${folder}/Harbour.md`];
  await aliceApi.putNote(harbour, "# Harbour\n");
  await aliceApi.putNote(beacon, "# Beacon\n");
  expect((await favourites(aliceApi)).favourites, "no favourite left by another test").toEqual([]);
  try {
    // Recent is this tab's: Beacon by its address, then Harbour from the palette, in the app.
    await openNote(page, beacon);
    await openPalette(page);
    await field(page).fill(`${folder} harbour`);
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Harbour\\.md$`));
    await expect(page.locator("article").getByRole("heading", { name: "Harbour", level: 1 })).toBeVisible();
    await page.getByRole("button", { name: "Note actions" }).click();
    await page.getByRole("menuitem", { name: "Add to favourites" }).click();
    await expect.poll(async () => (await favourites(aliceApi)).favourites.map(keyOf)).toEqual(mine);

    // Empty: the favourite, then Recent with the notes opened lately, newest first, the favourite not twice.
    await openPalette(page);
    await expect.poll(() => labels(group(page, "Favourites").getByRole("option"))).toEqual([`Harbour, Work / ${folder}`]);
    const recent = group(page, "Recent").getByRole("option");
    await expect(recent.first()).toHaveAccessibleName(`Beacon, Work / ${folder}`);
    await expect(group(page, "Recent").getByRole("option", { name: `Harbour, Work / ${folder}`, exact: true })).toHaveCount(0);
    // No Note group in the empty palette.
    await expect(group(page, "Note")).toHaveCount(0);
    await page.keyboard.press("Escape");

    await page.getByRole("button", { name: "Note actions" }).click();
    await page.getByRole("menuitem", { name: "Remove from favourites" }).click();
    await expect.poll(async () => (await favourites(aliceApi)).favourites).toEqual([]);
    await page.getByRole("button", { name: "Note actions" }).click();
    await expect(page.getByRole("menuitem", { name: "Add to favourites" })).toBeVisible();
    await page.keyboard.press("Escape");
  } finally {
    await removeFavourites(aliceApi, mine);
  }
});

/** The owner's password if the publish dialog asks for it (only when the session's window has closed). */
async function givePassword(dialog: Locator, submit: Locator) {
  const password = dialog.getByLabel("Your password", { exact: true });
  await expect(async () => {
    if (await password.isVisible()) {
      await reservePassword("alice");
      await password.fill(secrets().passwords.alice);
    }
    await expect(submit).toBeEnabled({ timeout: 1_000 });
  }).toPass({ timeout: 10_000 });
}

test("palette #5: with a note open the group Note offers its menu's items: no star, Ctrl+D does nothing, and Publish… publishes @palette", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n");
  try {
    await openNote(page, path);
    await openPalette(page);
    await field(page).fill("no pub");
    const note = group(page, "Note");
    // Placed with the note's title; found by its group and name, not by its place.
    await expect(note.getByRole("option").first()).toHaveAccessibleName("Publish…, Harbour");
    await expect(note.getByRole("button")).toHaveCount(0);
    await page.keyboard.press("Control+d");
    await expect(group(page, "Favourites")).toHaveCount(0);
    await field(page).fill("harbour");
    await expect(group(page, "Note")).toHaveCount(0);

    // The rest of the menu is there too, with the menu's labels.
    await field(page).fill("note");
    for (const name of ["Find in note", "History", "Duplicate", "Rename or move", "Delete note", "Add to favourites"]) await expect(group(page, "Note").getByRole("option", { name: `${name}, Harbour`, exact: true })).toBeVisible();

    await field(page).fill("no pub");
    await page.keyboard.press("Enter");
    await expect(palette(page)).toHaveCount(0);
    const dialog = page.getByRole("dialog", { name: "Publish this note" });
    await expect(dialog).toBeVisible();
    const submit = dialog.getByRole("button", { name: "Publish", exact: true });
    await givePassword(dialog, submit);
    await submit.click();
    // Not an assertion on the link itself: it would print it.
    await expect(dialog.getByRole("button", { name: "Copy link" })).toBeVisible();
    await dialog.getByRole("button", { name: "Done" }).click();
    await expect(dialog).toHaveCount(0);
    const listed = await aliceApi.console(`publish ls /Work/${folder}`);
    expect(listed.isError).toBe(false);
    expect(listed.text).toContain(`/Work/${folder}/Harbour.md`);
  } finally {
    await aliceApi.send("DELETE", `/api/publications/${encodePath(path)}`).catch(() => undefined);
  }
  const after = await aliceApi.console(`publish ls /Work/${folder}`);
  expect(after.text, "withdrawn again").not.toContain(`/Work/${folder}/Harbour.md`);
});

test("palette #6: Duplicate makes Copy of … beside the note without its date, the original stays open, and Open goes to the copy @palette", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Harbour.md`;
  const text = "---\ntags: [harbour]\nwhen: 2026-12-01 10:00\nuntil: 11:00\nkeeper: Ada\n---\n# Harbour\n\nThe lighthouse.\n";
  const copyText = "---\ntags: [harbour]\nkeeper: Ada\n---\n# Harbour\n\nThe lighthouse.\n";
  await aliceApi.putNote(path, text);
  await openNote(page, path);

  await page.getByRole("button", { name: "Note actions" }).click();
  await page.getByRole("menuitem", { name: "Duplicate" }).click();
  const notice = page.getByRole("status", { name: "Copy created" });
  await expect(notice).toHaveText(/^Copy of Harbour\.md created/);
  await expect(page).toHaveURL(new RegExp(`${noteUrl(path).replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`));
  expect((await aliceApi.getNote(`/Work/${folder}/Copy of Harbour.md`)).content, "the text without the calendar's keys").toBe(copyText);
  expect((await aliceApi.getNote(path)).content, "the original as it was").toBe(text);

  // Again: the name is taken, so the next number. Through the palette this time.
  await openPalette(page);
  await field(page).fill("note dupl");
  await page.keyboard.press("Enter");
  await expect(notice).toHaveText(/^Copy of Harbour 2\.md created/);
  expect((await aliceApi.getNote(`/Work/${folder}/Copy of Harbour 2.md`)).content).toBe(copyText);

  await notice.getByRole("button", { name: "Open" }).click();
  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Copy%20of%20Harbour%202\\.md$`));
  await expect(page.locator("article").getByRole("heading", { name: "Harbour", level: 1 })).toBeVisible();
  await expect(notice).toHaveCount(0);
});

test("palette #7: Shift+F10 opens the note's menu, with Read mode's items in Read mode and at the caret with Write mode's in Write mode @palette", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n\nThe lighthouse.\n");
  await openNote(page, path);

  const menu = page.getByRole("menu", { name: "Note actions" });
  await page.keyboard.press("Shift+F10");
  await expect(menu).toBeVisible();
  for (const name of ["Find in note", "History", "Duplicate"]) await expect(menu.getByRole("menuitem", { name, exact: true })).toBeAttached();
  // Closed by a click beside it (Esc is palette #8's).
  await page.mouse.click(640, 40);
  await expect(menu).toHaveCount(0);

  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  await source.focus();
  await source.evaluate((area: HTMLTextAreaElement) => area.setSelectionRange(5, 5));
  await page.keyboard.press("Shift+F10");
  await expect(menu).toBeVisible();
  // Write mode leaves out what acts on the saved text.
  await expect(menu.getByRole("menuitem", { name: "Duplicate", exact: true })).toBeVisible();
  for (const name of ["Find in note", "History", "Publish…", "Add property…"]) await expect(menu.getByRole("menuitem", { name, exact: true })).toHaveCount(0);
  // At the caret: its top just below the first line of the editor.
  const area = (await source.boundingBox())!;
  const box = (await menu.boundingBox())!;
  expect(box.y, "below the editor's top").toBeGreaterThanOrEqual(area.y);
  expect(box.y, "at the first line's caret").toBeLessThan(area.y + 80);
  await page.mouse.click(640, 40);
  await expect(menu).toHaveCount(0);
});

test("palette #8: the keyboard menu takes the focus, and Esc closes it with the caret where it was @palette", async ({ page, aliceApi, folder }) => {
  test.fail(true, "F-12 (Medium, bug): the keyboard menu never takes the focus (it is focused while still hidden for its placement), so Esc, the arrows and Tab don't reach it and Esc leaves it open");
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n\nThe lighthouse.\n");
  await openNote(page, path);
  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  await source.focus();
  await source.evaluate((area: HTMLTextAreaElement) => area.setSelectionRange(5, 5));

  const menu = page.getByRole("menu", { name: "Note actions" });
  await page.keyboard.press("Shift+F10");
  await expect(menu.getByRole("menuitem", { name: "Duplicate", exact: true })).toBeVisible();
  const focused = await page.evaluate(() => document.activeElement?.getAttribute("role") ?? document.activeElement?.tagName ?? "");
  expect(focused, "F-12: the focus on the menu's first item").toBe("menuitem");
  await page.keyboard.press("Escape");
  await expect(menu, "F-12: Esc closes the menu").toHaveCount(0);
  await expect(source).toBeFocused();
  expect(await source.evaluate((a: HTMLTextAreaElement) => [a.selectionStart, a.selectionEnd]), "the caret where it was").toEqual([5, 5]);
});

/** The menu's box as the page lays it out. */
const boxOf = (menu: Locator) => menu.evaluate((m) => {
  const r = m.getBoundingClientRect();
  return { top: r.top, bottom: r.bottom, left: r.left, right: r.right };
});

test("palette #9: Shift+F10 straight after the note opens gives a menu that fits the window @palette", async ({ page, aliceApi, folder }) => {
  test.fail(true, "F-13 (Medium, bug): the keyboard menu is placed for the items known when it opens and isn't placed again when the rest arrive, so its lower items run off the window");
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n\nThe lighthouse.\n");
  await openNote(page, path);

  const menu = page.getByRole("menu", { name: "Note actions" });
  await page.keyboard.press("Shift+F10");
  await expect(menu).toBeVisible();
  // Every item there: the favourites and the publishing marks answer after the note is shown.
  await expect(menu.getByRole("menuitem", { name: "Delete note", exact: true })).toBeAttached();
  await expect(menu.getByRole("menuitem", { name: "Add to favourites", exact: true })).toBeAttached();
  const box = await boxOf(menu);
  expect(box.bottom, `F-13: the menu ends within the window (${page.viewportSize()!.height} px)`).toBeLessThanOrEqual(page.viewportSize()!.height);
});

test("palette #10: in Read mode Shift+F10 opens the menu where the dock's ⋯ opens it @palette", async ({ page, aliceApi, folder }) => {
  test.fail(true, "F-14 (Low, bug): in Read mode the keyboard menu opens right of the dock's ⋯ where there is room, while the dock's own menu ends at the button");
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n\nThe lighthouse.\n");
  await openNote(page, path);

  const menu = page.getByRole("menu", { name: "Note actions" });
  await page.getByRole("button", { name: "Note actions" }).click();
  await expect(menu.getByRole("menuitem", { name: "Delete note", exact: true })).toBeVisible();
  const byMouse = await boxOf(menu);
  await page.keyboard.press("Escape");
  await expect(menu).toHaveCount(0);

  await page.keyboard.press("Shift+F10");
  await expect(menu.getByRole("menuitem", { name: "Delete note", exact: true })).toBeVisible();
  const byKey = await boxOf(menu);
  expect({ left: Math.round(byKey.left), right: Math.round(byKey.right) }, "F-14: the same place as the dock's own menu").toEqual({ left: Math.round(byMouse.left), right: Math.round(byMouse.right) });
});
