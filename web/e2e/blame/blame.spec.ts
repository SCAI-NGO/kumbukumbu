// SPDX-License-Identifier: AGPL-3.0-or-later
// The "Who wrote what" window (docs/superpowers/specs/2026-11-18-blame-design.md §7; PR #253). Two writers: Alice in
// the web app (`Api.putNote`) and her agent (the `mcp` key); a section both wrote in reads "alice and others", so no
// second person is needed.
//
// Promises in scope, and the test that covers each:
//  1. Reached from the note's menu, "Who wrote what", right after "History" (§7) → "blame #1"
//  2. … and from the palette's Note group, in Read mode only (§7) → "blame #3", "blame #2" (in Write mode the menu
//     itself is gone, so the palette's Note group, which takes the menu's items, is where its absence shows)
//  3. A light window titled "Who wrote what", headed with the note's title, at /blame/<vault>/<path>; a click on the
//     dimmed notes closes it (§7) → "blame #1", "blame #3"
//  4. The legend counts sections by their main writer: "2 sections by alice, 1 by <agent>" (§7 item 1, §7.1)
//     → "blame #1"
//  5. The sections in note order, each with a gutter naming its writer; a section with a line by another writer is
//     "X and others" (§7 item 2, §7.1) → "blame #1"
//  6. The gutter opens the section's detail in place: every writer with her lines and newest time (§7 item 2)
//     → "blame #1"
//  7. Colour is never the only carrier: every gutter names its writer (§7 item 3) → "blame #1" (by name, not mark)
//  8. The gutter's time of the newest line, short ("just now"), the full time on hover and for screen readers (§7
//     item 2) → "blame #1"
//  9. A copy is a new note: Duplicate makes every line the copier's, the original keeps its record (§5.4)
//     → "blame #4"
// Left out: "from before <date>" and its sentence: not reachable on a fresh stack (every note is written by 0.6.0);
// the phone's gutter above the section is layout, seen in the phone's screenshot; a discreet note's /discreet address
// is Task 8's (@serial); the kind ("· Obsidian device") needs a device key.
import type { Page } from "@playwright/test";
import type { Api, Mcp } from "../support/api";
import { expect, onPhone, shot, test } from "../support/test";

const noteUrl = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;

async function openNote(page: Page, path: string) {
  await page.goto(noteUrl(path));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
  const notNow = page.getByRole("button", { name: "Not now" });
  if (await notNow.isVisible()) await notNow.click();
}

/** Alice's note in the web app, then her agent's write over it: a line of Protocol changed, a section added. */
async function protocol(api: Api, mcp: Mcp, path: string) {
  const created = await api.putNote(path, "# Protocol\n\nstep one\nstep two\n\n## Results\n\nfine\n");
  const written = await mcp.call("write", {
    path,
    version: created.version,
    content: "# Protocol\n\nstep one\nstep two, checked\n\n## Results\n\nfine\n\n## Agent notes\n\nfound it\n",
  });
  expect(written.isError, written.text).toBe(false);
  // The agent's name as the history shows it, the label the window must show.
  const history = await api.console(`history "${path}"`);
  return history.text.split("\n")[0].split(/ {2,}/)[2];
}

const blameWindow = (page: Page) => page.getByRole("dialog", { name: "Who wrote what" });

test.afterEach(async ({ aliceApi, folder }) => {
  for (const name of ["Protocol.md", "Copy of Protocol.md"]) await aliceApi.deleteNote(`/Work/${folder}/${name}`).catch(() => undefined);
});

test("blame #1: Who wrote what in the note's menu shows each section's writer, and others, the legend and the detail @blame @phone", async ({ page, aliceApi, mcp, folder }, testInfo) => {
  const path = `/Work/${folder}/Protocol.md`;
  const agent = await protocol(aliceApi, mcp, path);
  expect(agent, "the agent's key, by its name").toMatch(/^e2e worker \d+ [0-9a-f]{6}$/);
  await openNote(page, path);

  await page.getByRole("button", { name: "Note actions" }).click();
  // Right after History.
  const items = (await page.getByRole("menuitem").allInnerTexts()).map((t) => t.trim());
  expect(items, "History is in the menu").toContain("History");
  expect(items.indexOf("Who wrote what"), items.join(" | ")).toBe(items.indexOf("History") + 1);
  await page.getByRole("menuitem", { name: "Who wrote what" }).click();
  await expect(page).toHaveURL(new RegExp(`/blame/Work/${folder}/Protocol\\.md$`));
  const blame = blameWindow(page);
  await expect(blame).toBeVisible();
  // The note's title: a heading on the desktop (not the section's h1), beside the window's title on the phone.
  if (onPhone(testInfo)) await expect(blame.getByText("· Protocol", { exact: true })).toBeVisible();
  else await expect(blame.getByRole("heading", { level: 3, name: "Protocol" })).toBeVisible();

  await expect(blame.getByRole("list", { name: "Writers" }).getByRole("listitem")).toHaveText(["2 sections by alice", `1 by ${agent}`]);

  // In note order; every gutter names its writer.
  const sections = blame.getByRole("list", { name: "Sections" }).getByRole("listitem");
  await expect(sections).toHaveCount(3);
  const gutters = blame.getByRole("list", { name: "Sections" }).getByRole("button");
  await expect(gutters.nth(0)).toHaveAccessibleName(/^alice and others\b/);
  await expect(gutters.nth(1)).toHaveAccessibleName(/^alice\b(?! and others)/);
  await expect(gutters.nth(2)).toHaveAccessibleName(new RegExp(`^${agent}\\b(?! and others)`));
  await expect(sections.nth(0).getByRole("heading", { name: "Protocol" })).toBeVisible();
  await expect(sections.nth(1).getByRole("heading", { name: "Results" })).toBeVisible();
  await expect(sections.nth(2).getByRole("heading", { name: "Agent notes" })).toBeVisible();
  await expect(sections.nth(2).getByText("found it")).toBeVisible();

  // The agent's gutter: its newest line's time, short, and in full on hover and for screen readers.
  const view = await aliceApi.json<{ sections: { counts: { at: string }[] }[] }>("GET", `/api/blame/Work/${folder}/Protocol.md`);
  const newest = view.sections[2].counts.map((c) => c.at).sort().at(-1)!;
  const full = await page.evaluate((iso) => new Date(iso).toLocaleString("en-GB", { dateStyle: "medium", timeStyle: "short" }), newest);
  const when = gutters.nth(2).locator("time");
  await expect(when).toHaveAttribute("datetime", newest);
  await expect(when).toHaveAttribute("title", full);
  await expect(when.locator("[aria-hidden=true]")).toHaveText(/^(just now|\d+ (seconds?|minutes?) ago)$/);
  await expect(gutters.nth(2)).toHaveAccessibleName(new RegExp(`^${agent}\\s*${full.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`));

  // The mixed section's detail, in place: each writer, her lines (blank ones not counted), her newest time.
  await gutters.nth(0).click();
  await expect(gutters.nth(0)).toHaveAttribute("aria-expanded", "true");
  const detail = blame.getByRole("list", { name: "Writers of Protocol" });
  await expect(detail.getByRole("listitem")).toHaveText([/^alice, 2 lines, \d\d:\d\d/, new RegExp(`^${agent}, 1 line, \\d\\d:\\d\\d`)]);
  await shot(page, testInfo, "blame");

  await gutters.nth(0).click();
  await expect(detail).toHaveCount(0);
  await blame.getByRole("button", { name: "Close" }).click();
  await expect(blame).toHaveCount(0);
  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Protocol\\.md$`));
});

test("blame #2: in Write mode the palette's Note group has no Who wrote what @blame @phone", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Protocol.md`;
  await aliceApi.putNote(path, "# Protocol\n\nstep one\n");
  await openNote(page, path);
  const find = page.getByRole("combobox", { name: "Find a command, vault or note" });
  const option = (name: string) => page.getByRole("listbox", { name: "Favourites and commands" }).getByRole("option", { name });

  // Read mode: the note's actions are there, placed with its title.
  await page.getByRole("button", { name: /^Favourites and commands/ }).click();
  await find.fill("who wrote");
  await expect(option("Who wrote what, Protocol")).toBeVisible();
  await page.keyboard.press("Escape");

  // Write mode: the window would show the server's state behind the editor's, so the palette offers the editor's command only.
  await page.getByRole("button", { name: "Write" }).click();
  await expect(page.getByRole("textbox", { name: "Markdown source", exact: true })).toBeVisible();
  await page.getByRole("button", { name: /^Favourites and commands/ }).click();
  await find.fill("template");
  await expect(option("Insert template, Protocol")).toBeVisible();
  await find.fill("who wrote");
  // The search row ends every list: once it shows the query, the list is the query's.
  await expect(option("Search notes for ‘who wrote’")).toBeVisible();
  await expect(option("Who wrote what, Protocol")).toHaveCount(0);
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: "Done" }).click();
});

test("blame #3: the palette's Note group opens Who wrote what @blame @phone", async ({ page, aliceApi, mcp, folder }) => {
  const path = `/Work/${folder}/Protocol.md`;
  const agent = await protocol(aliceApi, mcp, path);
  await openNote(page, path);

  await page.getByRole("button", { name: /^Favourites and commands/ }).click();
  await page.getByRole("combobox", { name: "Find a command, vault or note" }).fill("who wrote");
  const option = page.getByRole("group", { name: "Note" }).getByRole("option", { name: "Who wrote what, Protocol" });
  await option.click();
  await expect(page).toHaveURL(new RegExp(`/blame/Work/${folder}/Protocol\\.md$`));
  await expect(blameWindow(page).getByRole("list", { name: "Writers" }).getByRole("listitem")).toHaveText(["2 sections by alice", `1 by ${agent}`]);

  // A light window: a click on the dimmed notes (the top-left corner, outside the window on both viewports) closes it.
  await page.mouse.click(8, 8);
  await expect(blameWindow(page)).toHaveCount(0);
  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Protocol\\.md$`));
});

test("blame #4: Duplicate makes a new note, every line the copier's; the original keeps its writers @blame @phone", async ({ page, aliceApi, mcp, folder }) => {
  const path = `/Work/${folder}/Protocol.md`;
  const agent = await protocol(aliceApi, mcp, path);
  await openNote(page, path);

  await page.getByRole("button", { name: "Note actions" }).click();
  await page.getByRole("menuitem", { name: "Duplicate" }).click();
  await page.getByRole("status", { name: "Copy created" }).getByRole("button", { name: "Open" }).click();
  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Copy%20of%20Protocol\\.md$`));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();

  await page.getByRole("button", { name: "Note actions" }).click();
  await page.getByRole("menuitem", { name: "Who wrote what" }).click();
  const copy = blameWindow(page);
  await expect(copy.getByRole("list", { name: "Writers" }).getByRole("listitem")).toHaveText(["3 sections by alice"]);
  await expect(copy.getByRole("list", { name: "Sections" }).getByRole("button")).toHaveText([/^alice/, /^alice/, /^alice/]);
  await expect(copy.getByRole("list", { name: "Sections" }).getByRole("button", { name: /and others/ })).toHaveCount(0);

  const original = await aliceApi.json<{ writers: { display: string }[] }>("GET", `/api/blame/Work/${folder}/Protocol.md`);
  expect(original.writers.map((w) => w.display)).toEqual(["alice", agent]);
});
