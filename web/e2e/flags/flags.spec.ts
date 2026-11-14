// SPDX-License-Identifier: AGPL-3.0-or-later
// Flags (the flags report's hand-test checklist, steps 1-9; spec 2026-11-14-flags-explorer-photo-design.md §2).
import type { Locator, Page, Request, TestInfo } from "@playwright/test";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { encodePath, withDates } from "../support/api";
import { e2eDir } from "../support/env";
import { expect, onPhone, shot, test } from "../support/test";
import { today } from "../support/time";

const url = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;

/** Opens a note by its address; on the phone the install hint is answered with Not now, as a person would. */
async function openNote(page: Page, path: string) {
  const hint = page.getByRole("region", { name: "Install Kumbu² as an app" });
  await page.addLocatorHandler(hint, async () => {
    await hint.getByRole("button", { name: "Not now" }).click();
  });
  await page.goto(url(path));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

/** The note's ⋯ menu, opened. */
async function noteMenu(page: Page) {
  await page.getByRole("button", { name: "Note actions" }).click();
  const menu = page.getByRole("menu", { name: "Note actions" });
  await expect(menu).toBeVisible();
  return menu;
}

/** ⋯ → Flag: the flag menu (a sheet titled Flag). */
async function openFlagMenu(page: Page) {
  await (await noteMenu(page)).getByRole("menuitem", { name: "Flag" }).click();
  const sheet = page.getByRole("dialog", { name: "Flag" });
  await expect(sheet).toBeVisible();
  return sheet;
}

/** The flag's own buttons in the menu, not its Close. */
const choices = (sheet: Locator) => sheet.getByRole("button", { name: /^(?!Close)/ });

/** The note's header flag. */
const headerFlag = (page: Page) => page.getByRole("group", { name: "Flag" });

/** The one message after a write. */
const message = (page: Page) => page.getByRole("status", { name: "Flag changed" });

/** The tree's row of the open note: docked on the desktop, in the drawer (opened and closed here) on the phone. */
async function inTree<T>(page: Page, testInfo: TestInfo, check: (row: Locator) => Promise<T>) {
  if (!onPhone(testInfo)) {
    await page.getByRole("button", { name: "Show this note in the file tree" }).click();
    return check(page.getByRole("complementary", { name: "File tree" }).locator('[aria-current="page"]'));
  }
  await page.getByRole("button", { name: "Open file tree" }).click();
  const drawer = page.getByRole("dialog", { name: "File tree" });
  await expect(drawer).toBeVisible();
  const done = await check(drawer.locator('[aria-current="page"]'));
  await page.keyboard.press("Escape");
  await expect(drawer).toHaveCount(0);
  return done;
}

/** The lines of a note that differ from another's, by position (both have the same number of lines). */
const changedLines = (before: string, after: string) => {
  const a = before.split("\n");
  const b = after.split("\n");
  expect(b.length, "same number of lines").toBe(a.length);
  return b.flatMap((line, i) => (line === a[i] ? [] : [{ before: a[i], after: line }]));
};

/** The note's text with the calendar keys of an open to-do for today. */
const openTodo = () => withDates(`---\ntags: [pump]\nwhen: {{today}}\ntask: open\n---\n# Certificate\n\nThe pump's certificate.\n`);

/** Seeds the Certificate fixture, for a test that works on it. */
async function seedCertificate(seedAs: (from: string, to: string) => Promise<string[]>, folder: string) {
  const path = `/Work/${folder}/Certificate.md`;
  await seedAs("tags/flags/Certificate.md", path);
  return path;
}

test("flags #1: the flag menu offers eight flags and No flag, the white one outlined @flags @phone", async ({ page, seedAs, folder }, testInfo) => {
  const path = await seedCertificate(seedAs, folder);
  await openNote(page, path);
  const sheet = await openFlagMenu(page);

  const names = ["Red", "Green", "Blue", "Purple", "Yellow", "White", "Done", "Important", "No flag"];
  await expect(choices(sheet)).toHaveText(names);
  // Each with its icon, drawn: a flag filled in its colour, the white one outlined; Done and Important are marks.
  for (const name of names) await expect(choices(sheet).filter({ hasText: new RegExp(`^${name}$`) }).locator("svg")).toHaveCount(1);
  const paint = (name: string) =>
    choices(sheet)
      .filter({ hasText: new RegExp(`^${name}$`) })
      .locator("svg")
      .evaluate((svg) => {
        const style = getComputedStyle(svg);
        return { fill: style.fill, stroke: style.stroke };
      });
  const white = await paint("White");
  expect(white.fill, "the white flag has no fill").toBe("none");
  expect(white.stroke, "the white flag is drawn by its stroke").not.toBe("none");
  for (const name of ["Red", "Green", "Blue", "Purple", "Yellow"]) expect((await paint(name)).fill, `${name} is filled`).not.toBe("none");
  await shot(page, testInfo, "flags");
});

test("flags #2: Red shows in the header and the tree, and only tags changed @flags", async ({ page, aliceApi, seedAs, folder }, testInfo) => {
  const path = await seedCertificate(seedAs, folder);
  const before = await aliceApi.getNote(path);
  await openNote(page, path);
  const sheet = await openFlagMenu(page);
  await sheet.getByRole("button", { name: "Red", exact: true }).click();

  await expect(headerFlag(page).getByRole("img", { name: "Red flag" })).toBeVisible();
  await expect(headerFlag(page)).toContainText("Red flag");
  await expect(message(page)).toHaveText(/^Flagged Red\s*·\s*Undo$/);
  await inTree(page, testInfo, async (row) => {
    await expect(row).toContainText("Certificate");
    await expect(row.getByRole("img", { name: "Red flag" })).toBeVisible();
  });

  const after = await aliceApi.getNote(path);
  const changed = changedLines(before.content, after.content);
  expect(changed, "one line changed").toHaveLength(1);
  const tagsOf = (line: string) => new Set(/^tags:\s*\[(.*)\]\s*$/.exec(line)?.[1].split(",").map((t) => t.trim()).filter(Boolean));
  expect(tagsOf(changed[0].before), "the tags before").toEqual(new Set(["pump"]));
  expect(tagsOf(changed[0].after), "the tags after").toEqual(new Set(["pump", "flag/red"]));
});

test("flags #3: a single message for Blue, and Undo puts Red back @flags", async ({ page, aliceApi, seedAs, folder }) => {
  const path = await seedCertificate(seedAs, folder);
  await openNote(page, path);
  await (await openFlagMenu(page)).getByRole("button", { name: "Red", exact: true }).click();
  await expect(message(page)).toHaveText(/^Flagged Red\s*·\s*Undo$/);

  await headerFlag(page).getByRole("button").click();
  await page.getByRole("dialog", { name: "Flag" }).getByRole("button", { name: "Blue", exact: true }).click();
  await expect(headerFlag(page).getByRole("img", { name: "Blue flag" })).toBeVisible();
  await expect(message(page)).toHaveCount(1);
  await expect(message(page)).toHaveText(/^Flagged Blue\s*·\s*Undo$/);
  expect((await aliceApi.getNote(path)).content).toContain("flag/blue");

  await message(page).getByRole("button", { name: "Undo" }).click();
  await expect(headerFlag(page).getByRole("img", { name: "Red flag" })).toBeVisible();
  await expect(message(page)).toHaveCount(0);
  const content = (await aliceApi.getNote(path)).content;
  expect(content).toContain("flag/red");
  expect(content).not.toContain("flag/blue");
});

test("flags #4: No flag restores the note byte for byte @flags", async ({ page, aliceApi, seedAs, folder }) => {
  const path = await seedCertificate(seedAs, folder);
  const before = await aliceApi.getNote(path);
  await openNote(page, path);
  await (await openFlagMenu(page)).getByRole("button", { name: "Red", exact: true }).click();
  await expect(headerFlag(page)).toBeVisible();
  expect((await aliceApi.getNote(path)).content).not.toBe(before.content);

  await headerFlag(page).getByRole("button").click();
  await page.getByRole("dialog", { name: "Flag" }).getByRole("button", { name: "No flag" }).click();
  await expect(message(page)).toHaveText(/^Flag removed\s*·\s*Undo$/);
  await expect(headerFlag(page)).toHaveCount(0);
  expect((await aliceApi.getNote(path)).content).toBe(before.content);
});

test("flags #5: Done asks whether to mark it done in the calendar @flags @phone", async ({ page, aliceApi, seedAs, folder }, testInfo) => {
  const path = await seedCertificate(seedAs, folder);
  try {
    await aliceApi.putNote(path, openTodo());
    await openNote(page, path);
    await (await openFlagMenu(page)).getByRole("button", { name: "Done", exact: true }).click();

    const question = page.getByRole("alertdialog");
    await expect(question).toContainText("Also mark it done in the calendar?");
    const markDone = question.getByRole("button", { name: "Mark done" });
    const justFlag = question.getByRole("button", { name: "Just the flag" });
    for (const button of [markDone, justFlag]) {
      await expect(button).toBeVisible();
      await expect(button).toBeEnabled();
      if (onPhone(testInfo)) {
        await expect(button).toBeInViewport({ ratio: 1 });
        await button.tap({ trial: true });
      }
    }
  } finally {
    await aliceApi.deleteNote(path).catch(() => {});
  }
});

test("flags #6: Mark done writes flag, task and closed in one version, Undo gives both back @flags", async ({ page, aliceApi, mcp, seedAs, folder }) => {
  const path = await seedCertificate(seedAs, folder);
  try {
    // By the agent, not by Alice's own session: writes of one writer close together join one version (a changeset), which would hide the new one.
    const set = await mcp.sh(`calendar set "${path}" --when ${today()} --task`);
    expect(set.isError, set.text).toBe(false);
    const open = await aliceApi.getNote(path);
    expect(open.content).toContain("task: open");
    const versions = (await aliceApi.history(path)).length;
    await openNote(page, path);
    await (await openFlagMenu(page)).getByRole("button", { name: "Done", exact: true }).click();
    // Writes are recorded while Mark done is tapped: history alone can't tell one write from two (a writer's writes in a changeset merge).
    const writes: { method: string; url: string }[] = [];
    const record = (r: Request) => {
      if (r.url().includes("/api/") && r.method() !== "GET") writes.push({ method: r.method(), url: new URL(r.url()).pathname });
    };
    page.on("request", record);
    await page.getByRole("alertdialog").getByRole("button", { name: "Mark done" }).click();
    await expect(message(page)).toHaveText(/^Ticked and marked done\s*·\s*Undo$/);
    page.off("request", record);
    expect(writes, "exactly one write").toHaveLength(1);
    expect(writes[0].method).toBe("POST");
    expect(writes[0].url).toBe("/api/vaults/Work/flag");
    // (The body is not visible to the browser tooling; the one write is shown to carry flag, task and closed by the note below.)

    const closed = (await aliceApi.getNote(path)).content;
    expect(closed).toContain("flag/done");
    expect(closed).toContain("task: closed");
    expect(closed).toContain(`closed: ${today()}`);
    await expect.poll(async () => (await aliceApi.history(path)).length, "one version more").toBe(versions + 1);
    // Newest first: the current version, then the one before it, read by its token.
    const history = (await aliceApi.history(path)) as { version: string; current: boolean }[];
    expect(history[0].current).toBe(true);
    const previous = await aliceApi.json<{ content: string }>("GET", `/api/notes/${encodePath(path)}?version=${encodeURIComponent(history[1].version)}`);
    expect(previous.content, "the version before is the open note").toBe(open.content);
    await expect(page.getByRole("group", { name: "In the calendar" })).toContainText(/closed/i);

    await message(page).getByRole("button", { name: "Undo" }).click();
    await expect(message(page)).toHaveCount(0);
    await expect.poll(async () => (await aliceApi.getNote(path)).content).toBe(open.content);
    await expect(page.getByRole("group", { name: "In the calendar" })).not.toContainText(/closed/i);
  } finally {
    await aliceApi.deleteNote(path).catch(() => {});
  }
});

test("flags #7: a flag written in the text can't be changed from the menu @flags", async ({ page, seedAs, folder }) => {
  const path = `/Work/${folder}/Important.md`;
  await seedAs("tags/flags/Important.md", path);
  await openNote(page, path);
  const sheet = await openFlagMenu(page);

  await expect(sheet).toContainText("This flag is written in the note's text (#flag/important); change it there.");
  await expect(choices(sheet)).toHaveCount(0);
});

test("flags #8: Bob sees the flag but can't set one @flags @phone", async ({ bobPage, aliceApi, seedAs, folder }, testInfo) => {
  const path = await seedCertificate(seedAs, folder);
  const seeded = await aliceApi.getNote(path);
  await aliceApi.putNote(path, seeded.content.replace("tags: [pump]", "tags: [pump, flag/red]"));
  await openNote(bobPage, path);

  await expect(headerFlag(bobPage).getByRole("img", { name: "Red flag" })).toBeVisible();
  await expect(headerFlag(bobPage).getByRole("button")).toHaveCount(0);
  await inTree(bobPage, testInfo, async (row) => {
    await expect(row.getByRole("img", { name: "Red flag" })).toBeVisible();
  });

  const menu = await noteMenu(bobPage);
  await expect(menu.getByRole("menuitem").first()).toBeVisible();
  await expect(menu.getByRole("menuitem", { name: "Flag" })).toHaveCount(0);
  await expect(menu.getByRole("menuitem", { name: /photo/i })).toHaveCount(0);
  await bobPage.keyboard.press("Escape");
  await expect(menu).toHaveCount(0);

  // The tree's menus: the note's has items, none of them Flag or a photo; the folder has no actions at all for a reader.
  const tree = onPhone(testInfo) ? await (async () => {
    await bobPage.getByRole("button", { name: "Open file tree" }).click();
    return bobPage.getByRole("dialog", { name: "File tree" });
  })() : bobPage.getByRole("complementary", { name: "File tree" });
  const row = tree.locator('[aria-current="page"]');
  await expect(row).toBeVisible();
  const rowActions = row.locator("..").getByRole("button", { name: "Actions for Certificate" });
  await (onPhone(testInfo) ? rowActions.tap() : rowActions.click());
  const rowMenu = bobPage.getByRole("menu", { name: "Actions for Certificate" });
  await expect(rowMenu.getByRole("menuitem").first()).toBeVisible();
  await expect(rowMenu.getByRole("menuitem", { name: "Flag" })).toHaveCount(0);
  await expect(rowMenu.getByRole("menuitem", { name: /photo/i })).toHaveCount(0);
  await bobPage.keyboard.press("Escape");
  await expect(tree.getByRole("button", { name: `Actions for ${folder}` })).toHaveCount(0);
  await expect(bobPage.getByText(/Take photo/)).toHaveCount(0);
});

test("flags #9: the agent's tags flag and rg --tag flag/red list the flagged note @flags", async ({ aliceApi, seedAs, folder }) => {
  const path = await seedCertificate(seedAs, folder);
  const seeded = await aliceApi.getNote(path);
  await aliceApi.putNote(path, seeded.content.replace("tags: [pump]", "tags: [pump, flag/red]"));

  const flagged = await aliceApi.console(`tags flag /Work/${folder}`);
  expect(flagged.isError, flagged.text).toBe(false);
  expect(flagged.text).toContain(`${path}:`);
  const red = await aliceApi.console(`rg --tag flag/red -l "" /Work/${folder}`);
  expect(red.isError, red.text).toBe(false);
  expect(red.text).toContain(`${path}`);

  // The eight flags as the app has them (web/src/flags/api.ts): help names each by its tag.
  const source = readFileSync(join(e2eDir, "..", "src", "flags", "api.ts"), "utf8");
  const flags = [...(/FLAGS = \[([^\]]*)\]/.exec(source)?.[1] ?? "").matchAll(/"(\w+)"/g)].map((m) => m[1]);
  expect(flags, "the app's eight flags").toHaveLength(8);
  const help = await aliceApi.console("help tags");
  expect(help.isError, help.text).toBe(false);
  for (const flag of flags) expect(help.text, `help tags names flag/${flag}`).toContain(`flag/${flag}`);
});
