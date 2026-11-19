// SPDX-License-Identifier: AGPL-3.0-or-later
// Discreet vaults in 0.6.0's new views (spec 2026-11-19-tests-0-6-0-design.md §2): with Home hidden again, nothing of
// it shows, and what hides it while a view is open takes it off the screen. Each test signs in a session of its own,
// enters the PIN at most twice and ends hidden (`endHidden` in `finally`). Home holds /Home/Zahnarzt.md
// (e2e/fixtures/Home/). The PIN is entered through the vault menu (`revealDiscreet`); "hidden elsewhere" is the call ✕
// makes, sent from outside the tab (as another device or a tab of the same session does), which reaches the tab through
// its sync socket.
//
// Promises in scope, and the test that covers each:
// Command palette (2026-11-18-command-palette-design.md, "Discreet vaults", anti-goal 1):
// 1. A hidden discreet vault's favourites are left out of the answer, with no hint and no count, and come back once the
//    vaults are shown → "palette: …" (the favourites answer read back, the empty palette as without favourites).
// 2. The palette's vaults and notes follow the discreet state → "palette: …" (Zahnarzt and Home's rows gone).
// 3. Hiding the vaults again closes an open palette or drops its rows of hidden vaults at once → "palette: hidden from
//    elsewhere while it is open, the palette closes …"; where the address goes then is "palette: hidden from elsewhere
//    … the address leaves the note (F-10)", fixme while F-10 stands.
// 4. Recent in the empty palette: a discreet note opened before is not offered once hidden → "palette: …".
// Workspace (2026-11-18-workspace-design.md, anti-goal 3, §3 "Discreet vaults"):
// 5. Hiding closes a pane whose note is in a discreet vault, both panes alike → "split view: …" (right, then left).
// 6. A discreet vault named in `?beside=` makes the address neutral (`/discreet`), opened from the palette or loaded as
//    an address → "split view: …".
// 7. The side panel follows the focused pane, so nothing of the closed pane stays in it → "split view: …". The spec
//    promises nothing more of the panel (a Work note can't link into Home: links resolve within a vault).
// Windows (2026-11-17-windows-over-the-notes-design.md, "A discreet vault hiding while a window is open"):
// 8. A window's content follows the discreet state → "windows: hidden with ✕ …" (What's on, Recent, Settings' Vaults and
//    Security). Hidden while a window is open, the window stays without Home → the same test, its second half (What's
//    on, hidden from elsewhere: the pill is out of reach under a window); ✕ then shows the vault list, and Recent and
//    Settings after it → "windows: hidden from elsewhere while a window is open … (F-10, F-11)", fixme while F-10 stands.
// Console (2026-11-18-console-autocomplete-design.md, "Data"; the console resets on hiding):
// 9. Completion's listings go with the reset: `cat /H` Tab completes nothing, the vaults listed by Tab or offered as
//    chips leave Home out → "console: …".
// Console peek (2026-11-18-console-shortcuts-and-peek-design.md): a peeked note of a discreet vault leaves nothing of
// itself on screen once hidden → "console: …" (`p /Home/Zahnarzt.md`, then neither its text nor its name).
// Note deep links (2026-11-17-note-deep-links-design.md, rule 4):
// 10. A link into a discreet vault not revealed leads to the one "Not here" page, word for word the page of a vault that
//     doesn't exist → "deep links: …"; shown, the same link opens the note under the neutral address.
// Left out: the peek's neutral line "(a note that's hidden again)": a render may come before the console's reset or not,
// a timing the spec leaves open, so the test asserts only that nothing of the note stays; the split view and the side
// panel on the phone (they exist from 1280 px only).
import type { BrowserContext, Locator, Page, TestInfo } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { guarded } from "../support/api";
import { baseURL } from "../support/env";
import { endHidden, hideDiscreet, revealDiscreet } from "../support/session";
import { expect, onPhone, shot, test } from "../support/test";
import { dismissInstallHint, makeAgent } from "../calendar/helpers";
import { addDays, today } from "../support/time";

const encoded = (path: string) => path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/");
const noteUrl = (path: string) => `/notes/${encoded(path)}`;
const splitUrl = (left: string, right: string) => `${noteUrl(left)}?beside=${encoded(right)}`;
const zahnarzt = "/Home/Zahnarzt.md";
const headers = { Origin: baseURL };

/** A Work note of the test's own (wiki links resolve vault-wide: its name carries the folder). */
const desk = (folder: string) => ({ name: `Desk ${folder}`, path: `/Work/${folder}/Desk ${folder}.md` });

test.afterEach(async ({ aliceApi, folder }) => {
  await aliceApi.deleteNote(desk(folder).path).catch(() => undefined);
});

async function openNote(page: Page, path: string) {
  await page.goto(noteUrl(path));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

/** A fresh session of Alice's on her Work note: the install hint answered as a person would. */
async function start(freshSession: (user: "alice") => Promise<{ context: BrowserContext; page: Page }>, aliceApi: { putNote(p: string, c: string): Promise<unknown> }, folder: string) {
  const note = desk(folder);
  await aliceApi.putNote(note.path, "# Desk\n\nWork on the desk.\n");
  const session = await freshSession("alice");
  await dismissInstallHint(session.page);
  await openNote(session.page, note.path);
  return { ...session, note };
}

/** The PIN through the vault menu: on the phone in the tree's drawer, closed again afterwards (the pill sits under it). */
async function reveal(page: Page, testInfo: TestInfo) {
  if (onPhone(testInfo)) await page.getByRole("button", { name: "Open file tree" }).click();
  await revealDiscreet(page);
  // The PIN's dialog gone: Ctrl+P doesn't open the palette over a dialog.
  await expect(page.getByRole("dialog", { name: "More vaults" })).toHaveCount(0);
  const drawer = page.getByRole("dialog", { name: "File tree" });
  if (await drawer.isVisible()) await page.keyboard.press("Escape");
  await expect(drawer).toHaveCount(0);
}

const pill = (page: Page) => page.getByRole("group", { name: "Discreet vaults shown" });

/** Hidden from outside the tab (another device, another tab): the call ✕ makes; the tab hears it through its socket. */
async function hideElsewhere(page: Page) {
  await endHidden(page);
  await expect(pill(page)).toHaveCount(0);
}

const paletteDialog = (page: Page) => page.getByRole("dialog", { name: "Favourites and commands" });
const paletteField = (page: Page) => paletteDialog(page).getByRole("combobox", { name: "Find a command, vault or note" });

/** Ctrl+P, which opens it over a window too (the top bar's star is under the window then). */
async function openPalette(page: Page) {
  await page.keyboard.press("Control+p");
  await expect(paletteField(page)).toBeVisible();
}

/** A row of the palette, picked by what is typed and its accessible name (`<name>, <place>`). */
async function runFromPalette(page: Page, words: string, option: string) {
  await openPalette(page);
  await paletteField(page).fill(words);
  await paletteDialog(page).getByRole("option", { name: option, exact: true }).click();
  await expect(paletteDialog(page)).toHaveCount(0);
}

type FavouriteView = { kind: string; id?: string | null; vault?: string | null; path?: string | null };

/** The favourites as this session is answered them. */
async function favourites(context: BrowserContext) {
  const answer = await guarded(() => context.request.get("/api/account/favourites"), "GET favourites");
  expect(answer.status()).toBe(200);
  return (await answer.json()) as { favourites: FavouriteView[]; version: string };
}

/**
 * Takes Home's favourites off the list, for `finally`. Only a session that shows Home sees them (a write while it is
 * hidden keeps them), so this cleans up after a failure while Home was shown.
 */
async function dropHomeFavourites(context: BrowserContext) {
  const answer = await context.request.get("/api/account/favourites").catch(() => null);
  if (!answer?.ok()) return;
  const view = (await answer.json()) as { favourites: FavouriteView[]; version: string };
  const kept = view.favourites.filter((f) => f.vault !== "Home");
  if (kept.length === view.favourites.length) return;
  const input = kept.map(({ kind, id, vault, path }) => ({ kind, ...(id ? { id } : {}), ...(vault ? { vault } : {}), ...(path ? { path } : {}) }));
  await context.request.put("/api/account/favourites", { data: { favourites: input, version: view.version }, headers }).catch(() => undefined);
}

test("discreet-0-6-0 palette: Home's favourite and rows leave with the hide and come back with the PIN @palette @serial @phone", async ({ freshSession, aliceApi, folder }, testInfo) => {
  const { context, page, note } = await start(freshSession, aliceApi, folder);
  const option = (name: string) => paletteDialog(page).getByRole("option", { name, exact: true });
  const star = paletteDialog(page).getByRole("button", { name: /^(Add|Remove) Zahnarzt (to|from) favourites$/ });
  try {
    await reveal(page, testInfo);
    // A favourite of Home an earlier failed run left while hidden (only a session showing Home can take it off).
    await dropHomeFavourites(context);
    // Zahnarzt opened from the palette (so it is in Recent), then starred.
    await runFromPalette(page, "Zahnarzt", "Zahnarzt, Home");
    await expect(page.locator("article").getByRole("heading", { name: "Zahnarzt", level: 1 })).toBeVisible();
    await expect(page).toHaveURL(`${baseURL}/discreet`);
    await openPalette(page);
    await paletteField(page).fill("Zahnarzt");
    await paletteDialog(page).getByRole("button", { name: "Add Zahnarzt to favourites" }).click();
    await expect(star).toHaveAttribute("aria-pressed", "true");
    await expect.poll(async () => (await favourites(context)).favourites.some((f) => f.vault === "Home" && f.path === "Zahnarzt.md"), "the favourite is stored").toBe(true);
    // The empty palette: Zahnarzt among the favourites.
    await paletteField(page).fill("");
    await expect(paletteDialog(page).getByRole("group", { name: "Favourites" }).getByRole("option", { name: "Zahnarzt, Home" })).toBeVisible();

    // ✕ on the pill, the palette closed first: hidden from elsewhere (while it is open) is F-10's test, below, a race
    // that can put the note back in the address, and so in Recent.
    await page.keyboard.press("Escape");
    await expect(paletteDialog(page)).toHaveCount(0);
    await hideDiscreet(page);
    await expect(page).toHaveURL(`${baseURL}${noteUrl(note.path)}`);
    await expect(page.getByText("Zahnarzt")).toHaveCount(0);

    // Opened again: as if she had no favourite at all, Recent without Zahnarzt, and no row of Home's.
    await openPalette(page);
    await expect(paletteDialog(page).getByText("Star anything here to keep it on top.")).toBeVisible();
    await expect(paletteDialog(page).getByRole("group", { name: "Favourites" })).toHaveCount(0);
    await expect(paletteDialog(page).getByRole("group", { name: "Recent" }).getByRole("option", { name: `${note.name}, Work / ${folder}` })).toBeVisible();
    await expect(paletteDialog(page)).not.toContainText("Zahnarzt");
    await shot(page, testInfo, "discreet");
    await paletteField(page).fill("Zahnarzt");
    // Only the search row: no note, and no star to hint at one.
    await expect(paletteDialog(page).getByRole("option")).toHaveText(["Search notes for ‘Zahnarzt’"]);
    await paletteField(page).fill("Home");
    await expect(option("Search notes for ‘Home’")).toBeVisible();
    await expect(paletteDialog(page).getByRole("option", { name: /^(?!Search notes for).*Home/ })).toHaveCount(0);
    // The server's answer names nothing of Home, and no count of it either.
    const hidden = await favourites(context);
    expect(JSON.stringify(hidden), "no favourite of Home in the answer").not.toMatch(/Home|Zahnarzt/);
    await page.keyboard.press("Escape");
    await expect(paletteDialog(page)).toHaveCount(0);

    // Shown again: the favourite is back; removed, before the final hide.
    await reveal(page, testInfo);
    await openPalette(page);
    await expect(paletteDialog(page).getByRole("group", { name: "Favourites" }).getByRole("option", { name: "Zahnarzt, Home" })).toBeVisible();
    await paletteField(page).fill("Zahnarzt");
    await paletteDialog(page).getByRole("button", { name: "Remove Zahnarzt from favourites" }).click();
    await expect(star).toHaveAttribute("aria-pressed", "false");
    await expect.poll(async () => (await favourites(context)).favourites.some((f) => f.vault === "Home"), "the favourite is gone").toBe(false);
    await page.keyboard.press("Escape");
    await expect(paletteDialog(page)).toHaveCount(0);
    await hideDiscreet(page);
  } finally {
    await dropHomeFavourites(context);
    await endHidden(page);
  }
});

test("discreet-0-6-0 palette: hidden from elsewhere while it is open, the palette closes and offers nothing of Home @palette @serial @phone", async ({ freshSession, aliceApi, folder }, testInfo) => {
  const { page } = await start(freshSession, aliceApi, folder);
  try {
    await reveal(page, testInfo);
    await runFromPalette(page, "Zahnarzt", "Zahnarzt, Home");
    await expect(page.locator("article").getByRole("heading", { name: "Zahnarzt", level: 1 })).toBeVisible();
    await openPalette(page);
    await paletteField(page).fill("Zahnarzt");
    await expect(paletteDialog(page).getByRole("option", { name: "Zahnarzt, Home", exact: true })).toBeVisible();

    // The hide closes it in the same step as it forgets the vaults (hideAgain), whatever the address does after (F-10).
    await hideElsewhere(page);
    await expect(paletteDialog(page)).toHaveCount(0);
    await expect(page.getByRole("option", { name: /Zahnarzt/ })).toHaveCount(0);
  } finally {
    await endHidden(page);
  }
});

// F-10, a race, and not one of the open palette's. Hidden from elsewhere with the palette open (this test's steps): the
// address kept the Home note in 2 of 5 repeats on the phone, and in about 1 of 10 runs on the desktop. Hidden from
// elsewhere with the palette closed: 5 of 5 repeats on the phone were clean, then the next run on the phone kept the
// address too, and another put Zahnarzt back into the palette's Recent while hidden. Under a window: see the windows
// fixme test. Hidden with ✕ in the tab itself, it was never seen (the green tests). Evidence: every history.pushState/replaceState recorded with an init script shows exactly one
// write after the hide, `replaceState /notes/Home/Zahnarzt.md`, from React Router's history.replace (a
// `navigate(…, { replace: true })`), made once the discreet state was reset, so the neutral window let it through; the
// page then says "There is no such note" under that address. Likely places: web/src/discreet/hideAgain.ts:183-201 (the
// palette's overlay closed in the same step, reset() before navigate); a replace navigation to the current note as the
// palette closes (web/src/palette/Palette.tsx, the URL effects in web/src/shell/MobileShell.tsx around line 336); and
// web/src/discreet/neutralWindow.ts write(), which guards a replace without a URL only, not one naming a vault that was
// discreet a moment before.
test("discreet-0-6-0 palette: hidden from elsewhere (here with the palette open), the address leaves the note (F-10) @palette @serial @phone", async ({ freshSession, aliceApi, folder }, testInfo) => {
  test.fixme(true, "F-10 (High, bug, intermittent): hidden from elsewhere, palette open or not, the address bar can show /notes/Home/Zahnarzt.md in the clear; with the palette open about 2 in 5 phone runs, 1 in 10 desktop; one replaceState to /notes/Home/… after the reset");
  const { context, page, note } = await start(freshSession, aliceApi, folder);
  try {
    await reveal(page, testInfo);
    await dropHomeFavourites(context);
    await runFromPalette(page, "Zahnarzt", "Zahnarzt, Home");
    await expect(page.locator("article").getByRole("heading", { name: "Zahnarzt", level: 1 })).toBeVisible();
    await expect(page).toHaveURL(`${baseURL}/discreet`);
    await openPalette(page);
    await paletteField(page).fill("Zahnarzt");
    await paletteDialog(page).getByRole("button", { name: "Add Zahnarzt to favourites" }).click();
    await expect.poll(async () => (await favourites(context)).favourites.some((f) => f.vault === "Home" && f.path === "Zahnarzt.md"), "the favourite is stored").toBe(true);
    await paletteField(page).fill("");
    await expect(paletteDialog(page).getByRole("group", { name: "Favourites" }).getByRole("option", { name: "Zahnarzt, Home" })).toBeVisible();

    await hideElsewhere(page);
    await expect(page.getByRole("option", { name: /Zahnarzt/ })).toHaveCount(0);
    await expect(page, "F-10: the address names no discreet note").toHaveURL(`${baseURL}${noteUrl(note.path)}`);
    // When the race is lost, the note comes back into Recent as well (seen once in the palette's Recent while hidden).
    await openPalette(page);
    await expect(paletteDialog(page), "F-10: nothing of Zahnarzt in the palette").not.toContainText("Zahnarzt");
    await page.keyboard.press("Escape");

    // Shown again only to take the favourite off.
    await openNote(page, note.path);
    await reveal(page, testInfo);
    await dropHomeFavourites(context);
    await hideDiscreet(page);
  } finally {
    await dropHomeFavourites(context);
    await endHidden(page);
  }
});

const pane = (page: Page, side: "left" | "right") => page.getByRole("region", { name: side === "left" ? /^Left pane/ : /^Right pane/ });
const sidePanel = (page: Page) => page.getByRole("complementary", { name: "Side panel" });

/** The side panel's outline (the tab chosen first): its entries, the headings of the focused pane's note. */
async function outline(page: Page): Promise<Locator> {
  const tab = sidePanel(page).getByRole("tab", { name: "Outline" });
  if ((await tab.getAttribute("aria-selected")) !== "true") await tab.click();
  return sidePanel(page).getByRole("tabpanel", { name: "Outline" }).getByRole("button");
}

test("discreet-0-6-0 split view: a Home note in either pane leaves the address neutral and closes on the hide, the side panel with it @workspace @serial", async ({ freshSession, aliceApi, folder }) => {
  const { page, note } = await start(freshSession, aliceApi, folder);
  try {
    await reveal(page, test.info());
    // Zahnarzt in ?beside=, loaded as an address while shown: its pane shows it, and the address bar turns neutral.
    await page.goto(splitUrl(note.path, zahnarzt));
    await expect(pane(page, "right").getByRole("heading", { name: "Zahnarzt", level: 1 })).toBeVisible();
    await expect(pane(page, "left").getByRole("heading", { name: "Desk", level: 1 })).toBeVisible();
    await expect(page).toHaveURL(`${baseURL}/discreet`);

    // Zahnarzt beside the Work note (Ctrl+Enter in the palette): the address names neither pane.
    await openNote(page, note.path);
    await openPalette(page);
    await paletteField(page).fill("Zahnarzt");
    await expect(paletteDialog(page).getByRole("option", { name: "Zahnarzt, Home", exact: true })).toBeVisible();
    await page.keyboard.press("Control+Enter");
    await expect(pane(page, "right").getByRole("heading", { name: "Zahnarzt", level: 1 })).toBeVisible();
    await expect(pane(page, "left").getByRole("heading", { name: "Desk", level: 1 })).toBeVisible();
    await expect(page).toHaveURL(`${baseURL}/discreet`);
    // The side panel follows the focused pane, the right one.
    await expect(await outline(page)).toHaveText(["Zahnarzt"]);

    // ✕ on the pill: the right pane closes, the address is the Work note's alone, the panel shows its outline.
    await hideDiscreet(page);
    await expect(pane(page, "right")).toHaveCount(0);
    await expect(page).toHaveURL(`${baseURL}${noteUrl(note.path)}`);
    await expect(page.locator("article").getByRole("heading", { name: "Desk", level: 1 })).toBeVisible();
    await expect(await outline(page)).toHaveText(["Desk"]);
    await expect(page.getByText("Zahnarzt")).toHaveCount(0);

    // Named in ?beside= while hidden: its pane shows nothing of the note.
    await page.goto(splitUrl(note.path, zahnarzt));
    await expect(pane(page, "left").getByRole("heading", { name: "Desk", level: 1 })).toBeVisible();
    await expect(pane(page, "right")).toBeVisible();
    await expect(page.getByText("Check-up at the dentist.")).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "Zahnarzt", level: 1 })).toHaveCount(0);

    // Shown again, Zahnarzt in the left pane and the Work note beside it: the left one closes on the hide.
    await openNote(page, note.path);
    await reveal(page, test.info());
    await runFromPalette(page, "Zahnarzt", "Zahnarzt, Home");
    await expect(page.locator("article").getByRole("heading", { name: "Zahnarzt", level: 1 })).toBeVisible();
    await expect(page).toHaveURL(`${baseURL}/discreet`);
    await openPalette(page);
    await paletteField(page).fill(note.name);
    await expect(paletteDialog(page).getByRole("option", { name: `${note.name}, Work / ${folder}`, exact: true })).toBeVisible();
    await page.keyboard.press("Control+Enter");
    await expect(pane(page, "left").getByRole("heading", { name: "Zahnarzt", level: 1 })).toBeVisible();
    await expect(pane(page, "right").getByRole("heading", { name: "Desk", level: 1 })).toBeVisible();
    await expect(page).toHaveURL(`${baseURL}/discreet`);
    await hideDiscreet(page);
    await expect(pane(page, "right")).toHaveCount(0);
    await expect(page.locator("article").getByRole("heading", { name: "Desk", level: 1 })).toBeVisible();
    await expect(page).toHaveURL(`${baseURL}${noteUrl(note.path)}`);
    await expect(await outline(page)).toHaveText(["Desk"]);
    await expect(page.getByText("Zahnarzt")).toHaveCount(0);
  } finally {
    await endHidden(page);
  }
});

const windowNamed = (page: Page, name: string) => page.getByRole("dialog", { name, exact: true });
const whatsOnItem = (page: Page, title: string) =>
  windowNamed(page, "What's on").getByRole("list").first().getByRole("listitem").filter({ has: page.getByRole("link", { name: title, exact: true }) });

/** An overdue to-do, set through this session. */
async function overdue(context: BrowserContext, path: string) {
  const vault = path.split("/")[1];
  const set = await guarded(() => context.request.post(`/api/vaults/${vault}/calendar/set`, { data: { path, when: addDays(today(), -1), task: true }, headers }), "POST calendar set");
  expect(set.status()).toBe(200);
}

/**
 * Zahnarzt and the Desk note overdue, through this session, which shows Home; and an agent for Home that takes
 * Zahnarzt's date off whatever happens (the Desk note goes in afterEach). The Desk note's entry says What's on has loaded.
 */
async function datedNotes(context: BrowserContext, deskPath: string) {
  const home = await makeAgent({ Home: "write" }, "discreet", context);
  await overdue(context, zahnarzt);
  await overdue(context, deskPath);
  return home;
}

/** What's on has answered: no "Loading …", and the Desk note's entry is there. Before every check of what it lacks. */
async function whatsOnLoaded(page: Page, deskName: string) {
  await expect(windowNamed(page, "What's on").getByText(/^Loading/)).toHaveCount(0);
  await expect(whatsOnItem(page, deskName)).toBeVisible();
}

/** Shown: Zahnarzt open, and the windows over it (Settings' Vaults, Recent, What's on) name Home. */
async function windowsShowHome(page: Page) {
  await runFromPalette(page, "Zahnarzt", "Zahnarzt, Home");
  await expect(page.locator("article").getByRole("heading", { name: "Zahnarzt", level: 1 })).toBeVisible();
  await runFromPalette(page, "settings vaults", "Vaults, Settings");
  await expect(windowNamed(page, "Settings").getByRole("button", { name: "Delete Home" })).toBeVisible();
  await runFromPalette(page, "open recent", "Recent, Open");
  await windowNamed(page, "Recent changes").getByRole("searchbox", { name: "Find by name" }).fill("Zahnarzt");
  await expect(windowNamed(page, "Recent changes").getByRole("list", { name: "Recent changes" }).getByRole("listitem").filter({ hasText: "Zahnarzt" })).toHaveCount(1);
  await runFromPalette(page, "what's on", "What's on, Open");
  await expect(whatsOnItem(page, "Zahnarzt")).toContainText("Home");
}

/** Hidden: Recent changes and Settings' Vaults and Security name nothing of Home; ends on Settings. */
async function windowsWithoutHome(page: Page) {
  await runFromPalette(page, "open recent", "Recent, Open");
  const recent = windowNamed(page, "Recent changes");
  await expect(recent.getByRole("group", { name: "Vaults" }).getByRole("button", { name: "Work" })).toBeVisible();
  await expect(recent.getByRole("group", { name: "Vaults" }).getByRole("button", { name: "Home" })).toHaveCount(0);
  await recent.getByRole("searchbox", { name: "Find by name" }).fill("Zahnarzt");
  await expect(recent.getByRole("status")).toHaveText("Nothing matches that.");

  await runFromPalette(page, "settings vaults", "Vaults, Settings");
  const settings = windowNamed(page, "Settings");
  await expect(settings.getByRole("button", { name: "Delete Work" })).toBeVisible();
  await expect(settings.getByRole("button", { name: "Delete Home" })).toHaveCount(0);
  await expect(settings).not.toContainText("Home");
  await runFromPalette(page, "settings security", "Security, Settings");
  await expect(settings.getByRole("heading", { name: "Discreet vaults" })).toBeVisible();
  await expect(settings.getByRole("switch", { name: "Discreet: Home" })).toHaveCount(0);
  await expect(settings).not.toContainText("Home");
  return settings;
}

/** The end of a windows test, whatever happened: Zahnarzt's date off, the key revoked, Home hidden. */
async function endWindows(page: Page, home: Awaited<ReturnType<typeof makeAgent>> | undefined) {
  try {
    if (home) await home.mcp.sh(`calendar unset ${zahnarzt}`);
  } finally {
    await home?.dispose();
    await endHidden(page);
  }
}

test("discreet-0-6-0 windows: hidden with ✕, What's on, Recent and Settings show nothing of Home; hidden from elsewhere under What's on, it stays without Home @windows @serial @phone", async ({ freshSession, aliceApi, folder }, testInfo) => {
  const { context, page, note } = await start(freshSession, aliceApi, folder);
  let home: Awaited<ReturnType<typeof makeAgent>> | undefined;
  try {
    await reveal(page, testInfo);
    home = await datedNotes(context, note.path);
    await windowsShowHome(page);

    // ✕ on the pill, from the Work note (under a window the pill can't be reached; hidden while one is open is F-10's test).
    await openNote(page, note.path);
    await hideDiscreet(page);
    await runFromPalette(page, "what's on", "What's on, Open");
    await expect(windowNamed(page, "What's on")).toBeVisible();
    await whatsOnLoaded(page, note.name);
    await expect(windowNamed(page, "What's on")).not.toContainText("Zahnarzt");
    await expect(windowNamed(page, "What's on")).not.toContainText("Home");
    const settings = await windowsWithoutHome(page);

    // ✕ goes back one window at a time (each was opened from the one before), and from the first to the note behind.
    await settings.getByRole("button", { name: "Close" }).click();
    await expect(page).toHaveURL(`${baseURL}/settings/vaults`);
    await expect(settings.getByRole("button", { name: "Delete Home" })).toHaveCount(0);
    await settings.getByRole("button", { name: "Close" }).click();
    await expect(windowNamed(page, "Recent changes")).toBeVisible();
    await windowNamed(page, "Recent changes").getByRole("button", { name: "Close" }).click();
    await expect(windowNamed(page, "What's on")).toBeVisible();
    await whatsOnLoaded(page, note.name);
    await expect(windowNamed(page, "What's on")).not.toContainText("Home");
    await windowNamed(page, "What's on").getByRole("button", { name: "Close" }).click();
    await expect(page.getByRole("dialog", { name: /^(Settings|Recent changes|What's on)$/ })).toHaveCount(0);
    await expect(page).toHaveURL(`${baseURL}${noteUrl(note.path)}`);
    await expect(page.locator("article").getByRole("heading", { name: "Desk", level: 1 })).toBeVisible();
    await expect(page.getByText("Zahnarzt")).toHaveCount(0);

    // Shown again, What's on open over Zahnarzt, hidden from elsewhere: the window stays, without Home. Where the shell
    // behind it goes, and ✕, are the fixme test's (F-10).
    await reveal(page, testInfo);
    await runFromPalette(page, "Zahnarzt", "Zahnarzt, Home");
    await expect(page.locator("article").getByRole("heading", { name: "Zahnarzt", level: 1 })).toBeVisible();
    await runFromPalette(page, "what's on", "What's on, Open");
    await expect(whatsOnItem(page, "Zahnarzt")).toContainText("Home");
    await hideElsewhere(page);
    await expect(windowNamed(page, "What's on")).toBeVisible();
    await whatsOnLoaded(page, note.name);
    await expect(whatsOnItem(page, "Zahnarzt")).toHaveCount(0);
    await expect(windowNamed(page, "What's on")).not.toContainText("Zahnarzt");
    await expect(windowNamed(page, "What's on")).not.toContainText("Home");
  } finally {
    await endWindows(page, home);
  }
});

// F-10 under a window: once (desktop, 1 in 4 runs of this test), after a hide from elsewhere the shell behind the
// windows was on Home's note in the clear ("Switch vault: Home", the tree headed Home, "There is no vault 'Home'"), and
// ✕ went back one history entry, to the Settings window before, rather than replacing it with the location behind as a
// hide under a window makes it do. F-11 is reached only by this path (the pill is out of reach under a window), so it
// waits here too.
test("discreet-0-6-0 windows: hidden from elsewhere while a window is open over a Home note, the window stays without Home, and ✕ shows the vault list (F-10, F-11) @windows @serial @phone", async ({ freshSession, aliceApi, folder }, testInfo) => {
  test.fixme(true, "F-10 (High, bug, intermittent): hidden from elsewhere under a window, the shell behind can go back to Home's note in the clear; then F-11 (Low, unclear): ✕ on a window over a note hidden again shows the last ordinary note, not the vault list the windows spec names");
  const { context, page, note } = await start(freshSession, aliceApi, folder);
  let home: Awaited<ReturnType<typeof makeAgent>> | undefined;
  try {
    await reveal(page, testInfo);
    home = await datedNotes(context, note.path);
    await windowsShowHome(page);

    // Hidden elsewhere: What's on stays open, without Home.
    await hideElsewhere(page);
    await expect(windowNamed(page, "What's on")).toBeVisible();
    await whatsOnLoaded(page, note.name);
    await expect(whatsOnItem(page, "Zahnarzt")).toHaveCount(0);
    await expect(windowNamed(page, "What's on")).not.toContainText("Zahnarzt");
    await expect(windowNamed(page, "What's on")).not.toContainText("Home");
    const settings = await windowsWithoutHome(page);

    // ✕: nothing of Home (F-10), then the spec's vault list (F-11).
    await settings.getByRole("button", { name: "Close" }).click();
    await expect(windowNamed(page, "Settings")).toHaveCount(0);
    await expect(page, "F-10: the address names no discreet note").not.toHaveURL(/\/discreet$|\/Home\//);
    await expect(page.getByText("Zahnarzt"), "F-10: nothing of Zahnarzt").toHaveCount(0);
    await expect(page.getByText("Home", { exact: true }), "F-10: nothing of Home").toHaveCount(0);
    await expect(page.getByRole("heading", { name: "Your vaults" }), "F-11: ✕ on a window over a note hidden again shows the vault list").toBeVisible();
    await expect(page).toHaveURL(`${baseURL}/`);
    await expect(page.getByRole("list", { name: "Your vaults" }).getByRole("listitem")).toHaveText([/^Skills/, /^Welcome/, /^Work/]);
  } finally {
    await endWindows(page, home);
  }
});

test("discreet-0-6-0 console: completion and a peeked note forget Home with the hide, by Tab and by the phone's chips @console @serial @phone", async ({ freshSession, aliceApi, folder }, testInfo) => {
  const { page } = await start(freshSession, aliceApi, folder);
  const command = page.getByRole("textbox", { name: "Command" });
  const output = page.getByRole("log", { name: "Console output" });
  const chips = page.getByRole("group", { name: "Suggestions" });
  /** Tab on the desktop until the matches are listed in the output (the second Tab lists them). */
  const listed = async (line: string, expected: RegExp) => {
    await command.fill(line);
    await expect(async () => {
      await command.press("Tab");
      await expect(output).toContainText(expected, { timeout: 1_000 });
    }).toPass();
  };
  try {
    await reveal(page, testInfo);
    await runFromPalette(page, "console", "Console, Open");
    await expect(command).toBeFocused();
    if (onPhone(testInfo)) {
      await command.fill("cat /");
      await expect(chips.getByRole("button", { name: "Home/", exact: true })).toBeVisible();
    } else {
      await command.fill("cat /H");
      await command.press("Tab");
      await expect(command).toHaveValue("cat /Home/");
      await listed("cat /", /Home\/\s+Skills\/\s+Welcome\/\s+Work\//);
    }
    await command.fill("ls");
    await command.press("Enter");
    await expect(output).toContainText("Home");
    await command.fill(`p ${zahnarzt}`);
    await command.press("Enter");
    await expect(output).toContainText("Check-up at the dentist.");

    // Hidden elsewhere, with the listings still fresh (they are kept 10 s): the console starts again, without Home.
    await hideElsewhere(page);
    await expect(output).not.toContainText("Home");
    // Nothing of the peeked note stays (its neutral line, "(a note that's hidden again)", is left to the timing).
    await expect(output).not.toContainText("Check-up");
    await expect(output).not.toContainText("Zahnarzt");
    if (onPhone(testInfo)) {
      await command.fill("cat /");
      await expect(chips.getByRole("button", { name: "Work/", exact: true })).toBeVisible();
      await expect(chips.getByRole("button", { name: "Home/", exact: true })).toHaveCount(0);
    } else {
      // `cat /H` matches nothing: the line flashes (seen by an observer, as the flash lasts 200 ms) and stays as typed.
      await command.fill("cat /H");
      await page.getByRole("form", { name: "Command line" }).evaluate((form) => {
        const seen = window as unknown as { __bell?: boolean };
        seen.__bell = false;
        new MutationObserver(() => (seen.__bell ||= form.classList.contains("k-bell"))).observe(form, { attributes: true, attributeFilter: ["class"] });
      });
      await command.press("Tab");
      await expect.poll(() => page.evaluate(() => (window as unknown as { __bell?: boolean }).__bell), "no match for /H").toBe(true);
      await expect(command).toHaveValue("cat /H");
      await listed("cat /", /Skills\/\s+Welcome\/\s+Work\//);
      await expect(output).not.toContainText("Home");
    }
  } finally {
    await endHidden(page);
  }
});

test("discreet-0-6-0 deep links: a link into hidden Home leads to the same Not here as a vault that doesn't exist; shown, it opens the note @deeplinks @serial @phone", async ({ freshSession, aliceApi, folder }, testInfo) => {
  const { context, page } = await start(freshSession, aliceApi, folder);
  try {
    await reveal(page, testInfo);
    const vaults = (await (await guarded(() => context.request.get("/api/vaults"), "GET vaults")).json()) as { id: string; name: string }[];
    const homeId = vaults.find((v) => v.name === "Home")?.id;
    expect(homeId, "Home is listed while shown").toMatch(/^[0-9a-f]{32}$/);
    const link = `${baseURL}/n/${homeId}/Zahnarzt.md`;

    // Shown: the link opens the note, under the neutral address.
    await page.goto(link);
    await expect(page.locator("article").getByRole("heading", { name: "Zahnarzt", level: 1 })).toBeVisible();
    await expect(page).toHaveURL(`${baseURL}/discreet`);
    await hideDiscreet(page);
    await expect(page.getByText("Zahnarzt")).toHaveCount(0);

    // Hidden: Not here, the link left as it is, and the page word for word that of a vault that doesn't exist.
    const notHere = async (url: string) => {
      await page.goto(url);
      await expect(page.getByRole("heading", { name: "Not here", level: 1 })).toBeVisible();
      await expect(page.getByText("This link leads to a note that isn't here: it was moved or removed, or it's in a vault you can't see right now.")).toBeVisible();
      expect(page.url(), "the link stays the link").toBe(url);
      return page.locator("body").innerText();
    };
    const hidden = await notHere(link);
    expect(hidden, "nothing of the note").not.toContain("Check-up");
    expect(hidden).toBe(await notHere(`${baseURL}/n/${randomBytes(16).toString("hex")}/Zahnarzt.md`));
    await notHere(link);
    await expect(page.getByRole("button", { name: "Search for it" })).toBeVisible();
  } finally {
    await endHidden(page);
  }
});
