// SPDX-License-Identifier: AGPL-3.0-or-later
// Windows over the notes (spec 2026-11-17-windows-over-the-notes-design.md; PR #232): Settings, Admin, the console,
// Recent and What's on open over the note, which stays where it was, dimmed behind. The discreet cases are
// e2e/discreet/'s; the console's schemes are e2e/console/'s.
//
// Promises in scope, and the test that covers each:
// - Settings, Admin, Console, Recent and What's on open over the note shown, the shell behind inert ... windows #1
// - ✕, Esc and Back close every window, back to the note ........................................... windows #1
// - a move inside a window (its navigation, Admin's entries) replaces the address: Back closes the
//   window, and Back after closing never reopens it ................................................ windows #2
// - a click beside a light window (Recent, What's on) closes it; beside Settings or the console nothing .. windows #3
// - an unsaved draft in Write mode survives a window ................................................ windows #4
// - old addresses lead to the right place (`/settings`, `#discreet-vaults`, `/access`, `/access/keys`,
//   `/keys`, `/skills`, `/skills/:tab`, `/settings#install`), with replace: Back skips the old address; a
//   deep link shows the window over the shell and closes to `/` (the note kept, or the vault list) .... windows #5
// - the navigation marks the current entry (aria-current="page"); the desktop title is "Settings" .... windows #2, #5
// - on the phone a sheet from a strip at the top to the bottom edge, the navigation behind a tab,
//   the title naming the entry ("Settings · Security"), choosing folds the navigation, and so do a tap on
//   the dimmed content, the arrow and Esc, without going anywhere ...................................... windows #6 (@phone)
// Left out: a secret on show locking the navigation (needs a key or recovery key revealed on screen, which this run
// never screenshots or asserts; Vitest covers the lock); the exact sizes and frames (look, not behaviour; the
// fixed size is asserted once in #3); the console's schemes (task 12's @console); reduced motion.
import type { Locator, Page } from "@playwright/test";
import { encodePath } from "../support/api";
import { expect, onPhone, shot, test } from "../support/test";

const noteUrl = (path: string) => `/notes/${encodePath(path)}`;
const escaped = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
/** The address of the note, at the end of the page's URL. */
const at = (path: string) => new RegExp(`${escaped(noteUrl(path))}$`);

async function openNote(page: Page, path: string) {
  const hint = page.getByRole("region", { name: "Install Kumbu² as an app" });
  await page.addLocatorHandler(hint, async () => {
    await hint.getByRole("button", { name: "Not now" }).click();
  });
  await page.goto(noteUrl(path));
  await expect(page.locator("article").getByRole("heading", { level: 1 }).first()).toBeVisible();
}

const win = (page: Page, name: string) => page.getByRole("dialog", { name, exact: true });

/** The tree's Account links: in the docked tree on the desktop, in the drawer on the phone. */
async function account(page: Page): Promise<Locator> {
  const drawer = page.getByRole("dialog", { name: "File tree" });
  if (await page.getByRole("button", { name: "Open file tree" }).isVisible()) {
    if (!(await drawer.isVisible())) await page.getByRole("button", { name: "Open file tree" }).click();
    return drawer.getByRole("navigation", { name: "Account" });
  }
  return page.getByRole("complementary", { name: "File tree" }).getByRole("navigation", { name: "Account" });
}

/** The note behind is still there, in the shell that is inert while the window is open. */
async function noteBehind(page: Page, title: string) {
  const heading = page.locator("article").getByRole("heading", { name: title, level: 1 }).first();
  await expect(heading).toBeAttached();
  expect(await heading.evaluate((h) => !!h.closest("[inert]")), "the shell behind the window is inert").toBe(true);
}

test.afterEach(async ({ aliceApi, folder }) => {
  for (const name of ["Harbour", "Beacon"]) await aliceApi.deleteNote(`/Work/${folder}/${name}.md`).catch(() => undefined);
});

test("windows #1: Settings, Admin, the console, Recent and What's on open over the note, and ✕, Esc and Back close them @windows", async ({ page, aliceApi, folder }, testInfo) => {
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n\nThe lighthouse.\n");
  await openNote(page, path);

  // Each from where a person finds it, closed one of the three ways.
  const windows: { link: () => Promise<void>; title: string; url: RegExp; close: "✕" | "Esc" | "Back" }[] = [
    { link: async () => (await account(page)).getByRole("link", { name: "Settings" }).click(), title: "Settings", url: /\/settings\/appearance$/, close: "✕" },
    { link: async () => (await account(page)).getByRole("link", { name: "Admin" }).click(), title: "Admin", url: /\/admin\/users$/, close: "Esc" },
    { link: async () => (await account(page)).getByRole("link", { name: "Console" }).click(), title: "Console", url: /\/console$/, close: "Back" },
    { link: async () => (await account(page)).getByRole("link", { name: /^What's on/ }).click(), title: "What's on", url: /\/whats-on$/, close: "Esc" },
    {
      link: () => page.getByRole("complementary", { name: "File tree" }).getByRole("group", { name: "Tools for this vault" }).getByRole("link", { name: "Recent changes", exact: true }).click(),
      title: "Recent changes",
      url: /\/recent$/,
      close: "✕",
    },
  ];
  for (const w of windows) {
    await w.link();
    await expect(page).toHaveURL(w.url);
    await expect(win(page, w.title)).toBeVisible();
    await noteBehind(page, "Harbour");
    if (w.title === "Settings") {
      await expect(win(page, "Settings").getByRole("navigation", { name: "Settings" }).getByRole("link", { name: "Appearance" })).toHaveAttribute("aria-current", "page");
      await shot(page, testInfo, "windows");
    }
    if (w.close === "✕") await win(page, w.title).getByRole("button", { name: "Close" }).click();
    else if (w.close === "Esc") await page.keyboard.press("Escape");
    else await page.goBack();
    await expect(win(page, w.title), `${w.title} closed with ${w.close}`).toHaveCount(0);
    await expect(page).toHaveURL(at(path));
    // The note is live again.
    expect(await page.locator("article").first().evaluate((a) => !!a.closest("[inert]"))).toBe(false);
  }
});

test("windows #2: moving inside a window replaces its address, so Back closes it, and Back after closing never reopens it @windows", async ({ page, aliceApi, folder }) => {
  const beacon = `/Work/${folder}/Beacon.md`;
  const harbour = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(beacon, "# Beacon\n");
  await aliceApi.putNote(harbour, "# Harbour\n");
  await openNote(page, beacon);
  await openNote(page, harbour);

  // Settings: Appearance → Security → Access, then Back.
  await (await account(page)).getByRole("link", { name: "Settings" }).click();
  const nav = win(page, "Settings").getByRole("navigation", { name: "Settings" });
  await nav.getByRole("link", { name: "Security" }).click();
  await expect(page).toHaveURL(/\/settings\/security$/);
  await expect(nav.getByRole("link", { name: "Security" })).toHaveAttribute("aria-current", "page");
  await nav.getByRole("link", { name: "Access" }).click();
  await expect(page).toHaveURL(/\/settings\/access\/keys$/);
  // Access's own tabs replace too.
  await win(page, "Settings").getByRole("tab", { name: "Apps" }).click();
  await expect(page).toHaveURL(/\/settings\/access\/apps$/);
  await page.goBack();
  await expect(win(page, "Settings")).toHaveCount(0);
  await expect(page).toHaveURL(at(harbour));

  // Admin: Users → Invitations, then ✕; Back afterwards goes to the note before, not into the window.
  await (await account(page)).getByRole("link", { name: "Admin" }).click();
  await win(page, "Admin").getByRole("navigation", { name: "Admin" }).getByRole("link", { name: "Invitations" }).click();
  await expect(page).toHaveURL(/\/admin\/invitations$/);
  await win(page, "Admin").getByRole("button", { name: "Close" }).click();
  await expect(win(page, "Admin")).toHaveCount(0);
  await expect(page).toHaveURL(at(harbour));
  await page.goBack();
  await expect(page).toHaveURL(at(beacon));
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.locator("article").getByRole("heading", { name: "Beacon", level: 1 })).toBeVisible();
});

test("windows #3: a click beside Recent or What's on closes it, beside Settings or the console it does nothing @windows", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n");
  await openNote(page, path);

  for (const [link, title, closes] of [
    ["Settings", "Settings", false],
    ["Console", "Console", false],
    [/^What's on/, "What's on", true],
  ] as const) {
    await (await account(page)).getByRole("link", { name: link }).click();
    await expect(win(page, title)).toBeVisible();
    const box = (await win(page, title).boundingBox())!;
    expect(box.x, `${title} leaves room beside it`).toBeGreaterThan(20);
    // On the dimmed notes, left of the window.
    await page.mouse.click(10, 400);
    if (closes) {
      await expect(win(page, title), `a click beside ${title} closes it`).toHaveCount(0);
      await expect(page).toHaveURL(at(path));
    } else {
      await expect(win(page, title), `a click beside ${title} keeps it`).toBeVisible();
      // Fixed in size: Settings is 960 × 720, the console 1100 × 760, capped at the viewport less 32 px a side.
      const size = title === "Settings" ? { width: 960, height: 720 } : { width: 1100, height: 736 };
      expect({ width: Math.round(box.width), height: Math.round(box.height) }).toEqual(size);
      await page.keyboard.press("Escape");
      await expect(win(page, title)).toHaveCount(0);
    }
  }

  await page.getByRole("complementary", { name: "File tree" }).getByRole("group", { name: "Tools for this vault" }).getByRole("link", { name: "Recent changes", exact: true }).click();
  await expect(win(page, "Recent changes")).toBeVisible();
  await page.mouse.click(10, 400);
  await expect(win(page, "Recent changes")).toHaveCount(0);
  await expect(page).toHaveURL(at(path));
});

test("windows #4: an unsaved draft in Write mode survives a window @windows", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n");
  await openNote(page, path);

  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  await source.focus();
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\nThe keeper winds the clockwork.");
  const draft = "# Harbour\n\nThe keeper winds the clockwork.";

  await (await account(page)).getByRole("link", { name: "Settings" }).click();
  await expect(win(page, "Settings")).toBeVisible();
  await win(page, "Settings").getByRole("navigation", { name: "Settings" }).getByRole("link", { name: "Security" }).click();
  await expect(page).toHaveURL(/\/settings\/security$/);
  await page.keyboard.press("Escape");
  await expect(win(page, "Settings")).toHaveCount(0);

  await expect(page).toHaveURL(at(path));
  await expect(source).toHaveValue(draft);
  await expect(page.getByRole("button", { name: "Done" })).toBeVisible();
  await page.getByRole("button", { name: "Done" }).click();
  await expect(page.getByRole("button", { name: "Done" })).toHaveCount(0);
  await expect.poll(async () => (await aliceApi.getNote(path)).content).toContain("The keeper winds the clockwork.");
});

test("windows #5: old addresses lead to the window's new ones, and a deep link closes to the shell @windows", async ({ page, aliceApi, folder }) => {
  const moved: [string, RegExp, string][] = [
    ["/settings", /\/settings\/appearance$/, "Appearance"],
    ["/settings#discreet-vaults", /\/settings\/security#discreet-vaults$/, "Security"],
    ["/settings#install", /\/settings\/appearance#install$/, "Appearance"],
    ["/access", /\/settings\/access\/keys$/, "Access"],
    ["/access/keys", /\/settings\/access\/keys$/, "Access"],
    ["/keys", /\/settings\/access\/keys$/, "Access"],
    ["/skills", /\/settings\/skills\/installed$/, "Skills"],
    ["/skills/try", /\/settings\/skills\/try$/, "Skills"],
  ];
  for (const [old, now, entry] of moved) {
    await page.goto(old);
    await expect(page, `${old} leads on`).toHaveURL(now);
    await expect(win(page, "Settings")).toBeVisible();
    await expect(win(page, "Settings").getByRole("navigation", { name: "Settings" }).getByRole("link", { name: entry, exact: true })).toHaveAttribute("aria-current", "page");
  }
  // A deep link has nothing behind it in the app's history: ✕ goes to `/`, which shows the note kept or the vault list.
  await win(page, "Settings").getByRole("button", { name: "Close" }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect.poll(() => new URL(page.url()).pathname, "closed to / or the note it opens on").toMatch(/^\/$|^\/notes\//);
  await expect(page.getByRole("button", { name: "Favourites and commands (Ctrl+P)" })).toBeVisible();

  // With replace: Back from where an old address led goes to the page before it, not to the old address.
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n");
  await openNote(page, path);
  await page.goto("/keys");
  await expect(page).toHaveURL(/\/settings\/access\/keys$/);
  await page.goBack();
  await expect(page).toHaveURL(at(path));
  await expect(page.getByRole("dialog")).toHaveCount(0);
});

test("windows #6: on the phone a window is a sheet with its navigation behind a tab, and its title names the entry @windows @phone", async ({ page, aliceApi, folder }, testInfo) => {
  test.skip(!onPhone(testInfo), "the phone's sheet; the desktop's navigation is a column (windows #2)");
  const path = `/Work/${folder}/Harbour.md`;
  await aliceApi.putNote(path, "# Harbour\n");
  await openNote(page, path);

  await (await account(page)).getByRole("link", { name: "Settings" }).click();
  const sheet = win(page, "Settings");
  await expect(sheet).toBeVisible();
  await expect(sheet.getByRole("heading", { name: "Settings", level: 2 })).toBeVisible();
  await expect(sheet.getByText("· Appearance", { exact: true })).toBeVisible();
  await noteBehind(page, "Harbour");
  // From a strip at the top down to the bottom edge, once it has slid up.
  await expect.poll(async () => sheet.evaluate((d) => Math.round(d.getBoundingClientRect().bottom))).toBe(page.viewportSize()!.height);
  const top = await sheet.evaluate((d) => d.getBoundingClientRect().top);
  expect(top, "a strip of the note above, at least 32 px").toBeGreaterThanOrEqual(32);
  expect(top).toBeLessThan(80);

  // The navigation behind its tab.
  const tab = sheet.getByRole("button", { name: "Show navigation" });
  const nav = sheet.getByRole("navigation", { name: "Settings" });
  await expect(tab).toHaveAttribute("aria-expanded", "false");
  await expect(nav).toHaveCount(0);
  const target = (await tab.boundingBox())!;
  expect({ width: Math.round(target.width), height: Math.round(target.height) }, "a 48 × 72 px target").toEqual({ width: 48, height: 72 });
  await tab.click();
  await expect(tab).toHaveAttribute("aria-expanded", "true");
  await expect(nav.getByRole("link", { name: "Appearance" })).toHaveAttribute("aria-current", "page");
  await shot(page, testInfo, "windows");
  // Folded without going anywhere: by a tap on the dimmed content, by the arrow, by Esc (the window stays).
  for (const fold of ["tap", "arrow", "Esc"] as const) {
    if (fold === "tap") await page.mouse.click(355, 200);
    else if (fold === "arrow") await tab.click();
    else await page.keyboard.press("Escape");
    await expect(nav, `folded by ${fold}`).toHaveCount(0);
    await expect(tab).toHaveAttribute("aria-expanded", "false");
    await expect(page).toHaveURL(/\/settings\/appearance$/);
    await expect(sheet).toBeVisible();
    await tab.click();
    await expect(nav).toBeVisible();
  }
  await nav.getByRole("link", { name: "Security" }).click();
  await expect(page).toHaveURL(/\/settings\/security$/);
  await expect(nav).toHaveCount(0);
  await expect(tab).toHaveAttribute("aria-expanded", "false");
  await expect(sheet.getByText("· Security", { exact: true })).toBeVisible();

  // Back (the phone's back gesture) closes the whole sheet, not the entry.
  await page.goBack();
  await expect(sheet).toHaveCount(0);
  await expect(page).toHaveURL(at(path));

  // A light one closes with a tap on the strip of note above it.
  await (await account(page)).getByRole("link", { name: /^What's on/ }).click();
  await expect(win(page, "What's on")).toBeVisible();
  await page.mouse.click(200, 10);
  await expect(win(page, "What's on")).toHaveCount(0);
  await expect(page).toHaveURL(at(path));
});
