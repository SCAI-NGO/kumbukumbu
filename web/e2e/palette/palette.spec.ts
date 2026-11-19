// SPDX-License-Identifier: AGPL-3.0-or-later
// The command palette and favourites (spec 2026-11-18-command-palette-design.md; PRs #237, #248) and a note's actions
// (spec 2026-11-18-note-actions-design.md; PR #238). The hidden discreet vault's rows and favourites are
// e2e/discreet/'s.
//
// Promises in scope, and the test that covers each:
// - Ctrl+P opens the palette and closes it again, in Write mode too, focus back where it was ...... palette #1
// - the star in the top bar ("Favourites and commands (Ctrl+P)") opens it; Esc, a click outside and, on the
//   phone, ✕ close it; on the phone a sheet from below with the field under the list ............... palette #1
// - Ctrl+P doesn't open over a dialog; the arrow keys move the highlight ............................ palette #2
// - commands, vaults with Board/Calendar/Gallery, notes by name; words in any order, case ignored ... palette #2
// - the last row "Search notes for ‘…’" hands the text to search ................................. palette #2
// - the empty palette: favourites, then Recent (the notes opened lately, newest first); the hint
//   without favourites ............................................................................. palette #3, #4
//   (whether a starred note also stands in Recent is the builder's choice: not asserted)
// - a row's star and Ctrl+D make a favourite that stays on top while typing, also after a reload .. palette #3 (@serial)
// - a note's "Add to favourites" / "Remove from favourites" in its menu ........................... palette #4 (@serial)
// - the group Note: the menu's items, shown once something is typed, filtered by group and name
//   ("no pub" finds Publish…), no star, Ctrl+D does nothing; the row runs what the menu runs ...... palette #5
// - Duplicate: "Copy of …" beside it without the calendar's keys, then "… 2"; the original stays open,
//   the notice's Open goes to the copy ............................................................ palette #6
// - Duplicate in Write mode saves the unsaved text first, and the copy holds it ................... palette #11
// - Shift+F10 opens the note's menu with the items that fit the mode, in Write mode at the caret ..... palette #7
// - the keyboard menu: Esc closes it, the focus and caret back where they were ..................... palette #8 (F-12)
// - the menu flips above where there is no room below (PointMenu), on screen whatever the point ..... palette #9 (F-13)
// - in Read mode the same list in the same place as the dock's ⋯ menu ............................... palette #10 (F-14)
// Left out: Cmd+P on a Mac (the projects are Linux Chromium; Vitest covers it); the 50-note cap and the ranking
// within a group (Vitest, and other tests' notes in Work would make counts fragile); stale favourites and the
// 409 retry (need a second device's write timed against the tree; xUnit and Vitest cover them); the context-menu key
// (Chromium in Playwright doesn't deliver a ContextMenu keydown the same way on every platform; Shift+F10 is the same handler);
// Duplicate's "not for _archive/ notes, not without write" and a card keeping `column` and losing `rank` and `moved`
// (Vitest, duplicate.test.ts and useNoteActions; an archived note and a read-only vault need a set-up of their own).
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
const made = ["Harbour", "Harbour light", "Other", "Beacon", "Lantern", "Copy of Harbour", "Copy of Harbour 2"];

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
    // A sheet from below, once it has slid up, with the field near the thumb: below the list.
    await expect.poll(async () => Math.round(await palette(page).evaluate((d) => d.getBoundingClientRect().bottom)), "flush with the bottom").toBe(page.viewportSize()!.height);
    await expect(palette(page).getByRole("heading", { name: "Favourites and commands" })).toBeVisible();
    const list = (await palette(page).getByRole("listbox", { name: "Favourites and commands" }).boundingBox())!;
    expect((await field(page).boundingBox())!.y, "the field below the list").toBeGreaterThanOrEqual(list.y + list.height);
    await palette(page).getByRole("button", { name: "Close" }).click();
  } else await page.keyboard.press("Escape");
  await expect(palette(page)).toHaveCount(0);
  await expect(starButton(page), "the focus back on the star").toBeFocused();

  // A click outside it closes it too: beside the panel on the desktop, on the strip above the sheet on the phone.
  await openPalette(page);
  await page.mouse.click(10, 10);
  await expect(palette(page)).toHaveCount(0);

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

  // Not over a dialog: Rename or move's question keeps Ctrl+P.
  await page.getByRole("button", { name: "Note actions" }).click();
  await page.getByRole("menuitem", { name: "Rename or move" }).click();
  const rename = page.getByRole("dialog", { name: /^Rename or move/ });
  await expect(rename).toBeVisible();
  await page.keyboard.press("Control+p");
  await expect(rename).toBeVisible();
  await expect(palette(page)).toHaveCount(0);
  await page.keyboard.press("Escape");
  await expect(rename).toHaveCount(0);

  // A command by two words' starts, case ignored.
  await openPalette(page);
  await field(page).fill("SET vau");
  await expect(group(page, "Commands").getByRole("option").first()).toHaveAccessibleName("Vaults, Settings");
  // The arrows move the highlight, and back.
  const rows = palette(page).getByRole("option");
  await expect(rows.first()).toHaveAttribute("aria-selected", "true");
  await page.keyboard.press("ArrowDown");
  await expect(rows.nth(1)).toHaveAttribute("aria-selected", "true");
  await expect(rows.first()).toHaveAttribute("aria-selected", "false");
  await page.keyboard.press("ArrowUp");
  await expect(rows.first()).toHaveAttribute("aria-selected", "true");
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
  const lantern = `/Work/${folder}/Lantern.md`;
  const mine = [`note:Work/${folder}/Harbour.md`];
  await aliceApi.putNote(harbour, "# Harbour\n");
  await aliceApi.putNote(beacon, "# Beacon\n");
  await aliceApi.putNote(lantern, "# Lantern\n");
  expect((await favourites(aliceApi)).favourites, "no favourite left by another test").toEqual([]);
  try {
    // Recent is this tab's: Beacon by its address, then Lantern and Harbour from the palette, in the app.
    await openNote(page, beacon);
    for (const name of ["Lantern", "Harbour"]) {
      await openPalette(page);
      await field(page).fill(`${folder} ${name}`);
      await page.keyboard.press("Enter");
      await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/${name}\\.md$`));
      await expect(page.locator("article").getByRole("heading", { name, level: 1 })).toBeVisible();
    }
    await page.getByRole("button", { name: "Note actions" }).click();
    await page.getByRole("menuitem", { name: "Add to favourites" }).click();
    await expect.poll(async () => (await favourites(aliceApi)).favourites.map(keyOf)).toEqual(mine);

    // Empty: the favourite, then Recent with the notes opened lately, newest first. Whether the starred Harbour
    // also stands in Recent is the builder's choice; the others' order is the spec's.
    await openPalette(page);
    await expect.poll(() => labels(group(page, "Favourites").getByRole("option"))).toEqual([`Harbour, Work / ${folder}`]);
    const recent = async () => (await labels(group(page, "Recent").getByRole("option"))).filter((l) => l?.endsWith(`Work / ${folder}`) && !l.startsWith("Harbour,"));
    await expect.poll(recent).toEqual([`Lantern, Work / ${folder}`, `Beacon, Work / ${folder}`]);
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
    // Nothing starred: the open note's own item still offers to add it, and the server holds nothing of this folder.
    await field(page).fill("note fav");
    await expect(group(page, "Note").getByRole("option", { name: "Add to favourites, Harbour", exact: true })).toBeVisible();
    await expect(group(page, "Favourites")).toHaveCount(0);
    expect((await favourites(aliceApi)).favourites.filter((f) => (f.path ?? "").startsWith(`${folder}/`)), "no favourite of this folder").toEqual([]);
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

/** The menu's box as the page lays it out. */
const boxOf = (menu: Locator) => menu.evaluate((m) => {
  const r = m.getBoundingClientRect();
  return { top: r.top, bottom: r.bottom, left: r.left, right: r.right };
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
  const openAt = async (offset: number) => {
    await source.evaluate((area: HTMLTextAreaElement, at: number) => area.setSelectionRange(at, at), offset);
    await page.keyboard.press("Shift+F10");
    await expect(menu.getByRole("menuitem", { name: "Duplicate", exact: true })).toBeVisible();
    const top = (await boxOf(menu)).top;
    await page.mouse.click(640, 40);
    await expect(menu).toHaveCount(0);
    return top;
  };
  // On the first line, and on the third ("The lighthouse."): the menu follows the caret down two lines.
  const first = await openAt(5);
  const third = await openAt("# Harbour\n\nThe".length);
  const line = await source.evaluate((a) => parseFloat(getComputedStyle(a).lineHeight));
  const area = (await source.boundingBox())!;
  expect(first, "below the editor's top").toBeGreaterThanOrEqual(area.y);
  expect(third - first, "two lines further down").toBeGreaterThanOrEqual(2 * line - 4);
  expect(third - first).toBeLessThanOrEqual(2 * line + 4);

  // Write mode leaves out what acts on the saved text.
  await page.keyboard.press("Shift+F10");
  await expect(menu.getByRole("menuitem", { name: "Duplicate", exact: true })).toBeVisible();
  for (const name of ["Find in note", "History", "Publish…", "Add property…"]) await expect(menu.getByRole("menuitem", { name, exact: true })).toHaveCount(0);
  await page.mouse.click(640, 40);
  await expect(menu).toHaveCount(0);
});

test("palette #8: the keyboard menu takes the focus, and Esc closes it with the caret where it was @palette", async ({ page, aliceApi, folder }) => {
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
  const focusedText = () => page.evaluate(() => document.activeElement?.textContent ?? "");
  const first = await focusedText();
  await page.keyboard.press("ArrowDown");
  expect(await focusedText(), "F-12: ↓ moves the focus to the second item").not.toBe(first);
  await page.keyboard.press("Escape");
  await expect(menu, "F-12: Esc closes the menu").toHaveCount(0);
  await expect(source).toBeFocused();
  expect(await source.evaluate((a: HTMLTextAreaElement) => [a.selectionStart, a.selectionEnd]), "the caret where it was").toEqual([5, 5]);

  // Shift+Tab on the first item closes it too (the list doesn't wrap).
  await page.keyboard.press("Shift+F10");
  await expect(menu.getByRole("menuitem", { name: "Duplicate", exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.activeElement?.getAttribute("role")), "F-12: the focus on the first item again").toBe("menuitem");
  await page.keyboard.press("Shift+Tab");
  await expect(menu, "F-12: Shift+Tab on the first item closes the menu").toHaveCount(0);
});

test("palette #9: Shift+F10 straight after the note opens gives a menu that fits the window @palette", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n\nThe lighthouse.\n");
  // The favourites and the vault's publications held until the menu is open, as on a slow line: the menu opens with
  // the items known then, and the rest arrive after it.
  let release = () => {};
  const held = new Promise<void>((r) => (release = r));
  for (const url of ["**/api/account/favourites", "**/api/vaults/Work/publications"])
    await page.route(url, async (route) => {
      if (route.request().method() === "GET") await held;
      await route.continue();
    });
  await openNote(page, path);

  const menu = page.getByRole("menu", { name: "Note actions" });
  await page.keyboard.press("Shift+F10");
  await expect(menu).toBeVisible();
  await expect(menu.getByRole("menuitem", { name: "Add to favourites", exact: true }), "held: not yet in the menu").toHaveCount(0);
  release();
  // Every item there now.
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
  expect(await page.evaluate(() => document.activeElement?.getAttribute("role")), "F-12: the focus on a menu item in Read mode too").toBe("menuitem");
  const byKey = await boxOf(menu);
  expect({ left: Math.round(byKey.left), right: Math.round(byKey.right) }, "F-14: the same place as the dock's own menu").toEqual({ left: Math.round(byMouse.left), right: Math.round(byMouse.right) });
});

test("palette #11: Duplicate in Write mode saves the unsaved text first, and the copy holds it @palette", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n");
  await openNote(page, path);
  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  await source.focus();
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\nThe keeper winds the clockwork.");

  // From the keyboard menu, chosen with the mouse (its keys are F-12's).
  await page.keyboard.press("Shift+F10");
  const menu = page.getByRole("menu", { name: "Note actions" });
  await menu.getByRole("menuitem", { name: "Duplicate", exact: true }).click();
  await expect(page.getByRole("status", { name: "Copy created" })).toHaveText(/^Copy of Harbour\.md created/);
  expect((await aliceApi.getNote(`/Work/${folder}/Copy of Harbour.md`)).content, "the copy holds the typed line").toContain("The keeper winds the clockwork.");
  expect((await aliceApi.getNote(path)).content, "the original saved first").toContain("The keeper winds the clockwork.");
  // Still writing the original.
  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Harbour\\.md$`));
  await expect(page.getByRole("button", { name: "Done" })).toBeVisible();
});
