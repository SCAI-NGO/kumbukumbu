// SPDX-License-Identifier: AGPL-3.0-or-later
// The console's completion, shortcuts, peek and schemes (specs 2026-11-18-console-autocomplete-design.md, PR #239;
// 2026-11-18-console-shortcuts-and-peek-design.md, PR #233).
//
// Promises in scope, and the test that covers each:
// - Tab: one match completes fully, a file with a trailing space, a folder with `/` → "console #1"
// - Tab: several complete to their common prefix, the next Tab lists them in one line → "console #1"
// - Tab: no match flashes the command line once (a class, removed again), no line in the output → "console #2"
// - Tab on an empty line does nothing → "console #2"
// - names with spaces are put in with backslash escapes, and the line runs as completed → "console #1"
// - relative paths (`..`), `cd` completes folders only, commands as the first word and after a pipe → "console #3"
// - phone: a row of chips for a path word, 48 px targets, a tap completes and keeps the focus; an empty word after a
//   command offers the working folder's entries; hidden when empty or nothing matches → "console #4" (@phone)
// - `l`, `la`, `lsa` are rewritten to `ls -l`, `ls -a`, `ls -la`, the → line shows what was sent; `ls -l` gives a
//   note's size and time → "console #5"
// - help names l, la, lsa and p; `help p` explains peek → "console #5"
// - `p <note>` renders the note under its path: headings, a working link (opens the note, closes the console), a
//   missing link marked without a create button, task boxes disabled; `#Section` renders that section only → "console #6"
// - `p` without an argument, on a folder, on a missing note: an error in words → "console #6"
// - Plain · Green · Amber, kept per device across a reload; errors say "Error:" in every scheme → "console #7" (@phone)
// Left out: `_archive/` offered only once `_` is typed (needs an archive in Work, which only `archive` makes, and the
// console can't write; covered by Vitest); the cache's 10 s refresh (timing, Vitest); a peeked discreet note
// collapsing on hide (discreet task, @serial); Mermaid as code and images in peek (the renderer's own tests).
import type { Locator, Page } from "@playwright/test";
import type { Api } from "../support/api";
import { cleanFolder } from "../calendar/helpers";
import { expect, onPhone, shot, test } from "../support/test";

const command = (page: Page) => page.getByRole("textbox", { name: "Command" });
const output = (page: Page) => page.getByRole("log", { name: "Console output" });

/** The notes the tests complete and peek at; the link target carries the folder, as wiki links resolve vault-wide. */
async function seed(api: Api, folder: string) {
  const base = `/Work/${folder}`;
  await api.putNote(`${base}/Alpha.md`, "# Alpha\n");
  await api.putNote(`${base}/Alpine.md`, "# Alpine\n");
  await api.putNote(`${base}/Meeting Notes.md`, "# Meeting\n\nThe agenda of the harbour board.\n");
  await api.putNote(`${base}/Summary.md`, "# Summary\n");
  await api.putNote(`${base}/Sub/Inner.md`, "# Inner\n");
  await api.putNote(`${base}/T-${folder}.md`, "# Target\n\nWhere the link leads.\n");
  await api.putNote(
    `${base}/Peek.md`,
    [
      "# Peek heading",
      "",
      `Intro with [[T-${folder}]] and [[Nowhere-${folder}]].`,
      "",
      "- [ ] a task box",
      "",
      "## Goals",
      "",
      "Goal text here.",
      "",
      "## Later",
      "",
      "Later text here.",
      "",
    ].join("\n"),
  );
  return base;
}

/** The console, open over the notes, waiting for a command. */
async function openConsole(page: Page) {
  await page.goto("/console");
  await expect(output(page)).toContainText("Kumbu² console, not a bash, and read-only.");
  await expect(command(page)).toBeEnabled();
}

/** Types a line and runs it; waits until the console takes the next one. */
async function runLine(page: Page, line: string) {
  await command(page).fill(line);
  await command(page).press("Enter");
  await expect(command(page)).toHaveValue("");
  await expect(command(page)).toBeEnabled();
  await expect(output(page)).toHaveAttribute("aria-busy", "false");
}

/**
 * Tab until the input holds `expected`: the first Tab after typing may come before the folder's listing is in, and a
 * Tab finds what is listed. A Tab that completes changes the line, so the loop ends there.
 */
async function tabTo(input: Locator, expected: string) {
  await expect(async () => {
    if ((await input.inputValue()) !== expected) await input.press("Tab");
    await expect(input).toHaveValue(expected, { timeout: 1_000 });
  }).toPass();
}

// On the phone the install hint sits under the console's window, where Not now can't be reached: answered beforehand,
// as Not now leaves it.
test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem("kumbu.installHint", "dismissed"));
});

test.afterEach(async ({ aliceApi, folder }) => {
  await cleanFolder(aliceApi, folder);
});

test("console #1: Tab completes a unique match fully, several to their common start, and the next Tab lists them @console", async ({ page, aliceApi, folder }, testInfo) => {
  const base = await seed(aliceApi, folder);
  await openConsole(page);
  const input = command(page);

  // One match: the file in full, with a space after it.
  await input.fill(`cat ${base}/Sum`);
  await tabTo(input, `cat ${base}/Summary.md `);

  // A folder: a trailing /, so typing goes on inside it.
  await input.fill(`ls ${base}/S`);
  await input.press("Tab");
  // Sub/ and Summary.md: the common start.
  await expect(input).toHaveValue(`ls ${base}/Su`);
  await input.fill(`ls ${base}/Sub`);
  await tabTo(input, `ls ${base}/Sub/`);

  // Two notes: the common start, then the list in the output, one line, nothing run.
  await input.fill(`cat ${base}/Al`);
  await tabTo(input, `cat ${base}/Alp`);
  await input.press("Tab");
  await expect(output(page)).toContainText(/Alpha\.md\s+Alpine\.md/);
  await expect(input, "listing runs nothing").toHaveValue(`cat ${base}/Alp`);

  // A name with a space: escaped, and the completed line runs.
  await input.fill(`cat ${base}/Mee`);
  await tabTo(input, `cat ${base}/Meeting\\ Notes.md `);
  await input.press("Enter");
  await expect(output(page)).toContainText("The agenda of the harbour board.");
  await expect(output(page)).not.toContainText("Error:");
  await shot(page, testInfo, "console");
});

test("console #2: Tab with no match flashes the command line once and writes nothing; on an empty line it does nothing @console", async ({ page, aliceApi, folder }) => {
  const base = await seed(aliceApi, folder);
  await openConsole(page);
  const input = command(page);
  const line = page.getByRole("form", { name: "Command line" });
  // The flash lasts about 200 ms: an observer records that the class came, and the test sees it go.
  const watchBell = () =>
    line.evaluate((form) => {
      const w = window as unknown as { rang: number };
      w.rang = 0;
      new MutationObserver(() => {
        if (form.classList.contains("k-bell")) w.rang++;
      }).observe(form, { attributes: true, attributeFilter: ["class"] });
    });
  const rang = () => page.evaluate(() => (window as unknown as { rang: number }).rang);

  // The folder's listing first, so that the Tab below finds nothing for certain and not for want of it.
  await input.fill(`cat ${base}/Al`);
  await tabTo(input, `cat ${base}/Alp`);
  const lines = await output(page).locator("[data-line]").count();

  await watchBell();
  await input.fill(`cat ${base}/Zz`);
  await input.press("Tab");
  await expect.poll(rang, "the command line flashed").toBe(1);
  await expect(line).not.toHaveClass(/k-bell/);
  await expect(input).toHaveValue(`cat ${base}/Zz`);
  expect(await output(page).locator("[data-line]").count(), "no line in the output").toBe(lines);

  // An empty line: nothing at all, and the focus stays in the input.
  await watchBell();
  await input.fill("");
  await input.press("Tab");
  await expect(input).toBeFocused();
  await expect(input).toHaveValue("");
  expect(await rang(), "an empty line doesn't flash").toBe(0);
});

test("console #3: relative paths with .., cd folders only, commands first and after a pipe @console", async ({ page, aliceApi, folder }) => {
  const base = await seed(aliceApi, folder);
  await openConsole(page);
  const input = command(page);

  // Commands: as the first word, and as the first word after a pipe.
  await input.fill("hel");
  await tabTo(input, "help ");
  await input.fill("ls | w");
  await tabTo(input, "ls | wc ");

  await runLine(page, `cd ${base}/Sub`);
  await expect(page.getByRole("form", { name: "Command line" })).toContainText(`${base}/Sub`);
  // From Sub/, .. is the test's folder.
  await input.fill("cat ../Al");
  await tabTo(input, "cat ../Alp");
  await input.fill("cat ../Mee");
  await tabTo(input, "cat ../Meeting\\ Notes.md ");

  // cd offers folders only: Sub/ where cat would stop at Su (Sub/ and Summary.md).
  await input.fill("cd ../S");
  await tabTo(input, "cd ../Sub/");
  await input.fill("cat ../S");
  await input.press("Tab");
  await expect(input).toHaveValue("cat ../Su");
});

test("console #4: on the phone a row of suggestions completes on a tap and keeps the keyboard up @console @phone", async ({ page, aliceApi, folder }, testInfo) => {
  test.skip(!onPhone(testInfo), "the suggestion row is the phone's; the desktop has Tab");
  const base = await seed(aliceApi, folder);
  await openConsole(page);
  const input = command(page);
  const chips = page.getByRole("group", { name: "Suggestions" });

  await input.fill(`cat ${base}/Al`);
  await expect(chips.getByRole("button")).toHaveText(["Alpha.md", "Alpine.md"]);
  for (const chip of await chips.getByRole("button").all()) expect((await chip.boundingBox())!.height, "a 48 px target").toBeGreaterThanOrEqual(48);
  await chips.getByRole("button", { name: "Alpine.md" }).click();
  await expect(input).toHaveValue(`cat ${base}/Alpine.md `);
  await expect(input, "the keyboard stays up").toBeFocused();

  // An empty word after a command: the working folder's entries; a folder's tap goes on inside it.
  await runLine(page, `cd ${base}`);
  await input.fill("cat ");
  await expect(chips.getByRole("button", { name: "Sub/" })).toBeVisible();
  await expect(chips.getByRole("button", { name: "Meeting Notes.md" })).toBeVisible();
  await shot(page, testInfo, "console");
  await chips.getByRole("button", { name: "Sub/" }).click();
  await expect(input).toHaveValue("cat Sub/");
  await expect(chips.getByRole("button")).toHaveText(["Inner.md"]);

  // Nothing matches, or nothing typed: no row.
  await input.fill("cat Zz");
  await expect(chips).toHaveCount(0);
  await input.fill("");
  await expect(chips).toHaveCount(0);
});

test("console #5: l, la and lsa are written out as ls -l, ls -a and ls -la, and help names them and p @console", async ({ page, aliceApi, folder }) => {
  const base = await seed(aliceApi, folder);
  await openConsole(page);
  const log = output(page);

  await runLine(page, `l ${base}`);
  await expect(log).toContainText(`→ ls -l ${base}`);
  // A note's size in bytes ("# Alpha\n" is 8) and the time of its version, in UTC.
  await expect(log).toContainText(/Alpha\.md\s+8 B\s+\d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC/);
  await expect(log).toContainText(/Sub\/\s+\d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC/);

  await runLine(page, "clear");
  await runLine(page, `la ${base} | wc -l`);
  await expect(log).toContainText(`→ ls -a ${base} | wc -l`);
  // Alpha, Alpine, Meeting Notes, Peek, Sub/, Summary, T-…: seven entries.
  await expect(log.locator("[data-line]").last()).toHaveText("7");

  await runLine(page, "clear");
  await runLine(page, `lsa ${base}/Sub`);
  await expect(log).toContainText(`→ ls -la ${base}/Sub`);
  await expect(log).toContainText(/Inner\.md\s+8 B\s+\d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC/);

  // Only the first word: in a later stage `l` stays as typed, for the shell to refuse.
  await runLine(page, "clear");
  await runLine(page, `ls ${base} | l`);
  await expect(log).not.toContainText("ls -l");

  await runLine(page, "clear");
  await runLine(page, "help");
  await expect(log).toContainText("l, la and lsa for ls -l, ls -a and ls -la; p <note> shows a note as it reads in the app (help p).");
  await runLine(page, "clear");
  await runLine(page, "help p");
  await expect(log).toContainText("Peek: show a note as it reads in the app");
  await expect(log).not.toContainText("Error:");
});

test("console #6: p renders a note with working links, a section on its own, and says what is wrong in words @console", async ({ page, aliceApi, folder }) => {
  const base = await seed(aliceApi, folder);
  await openConsole(page);
  const log = output(page);

  await runLine(page, `cd ${base}`);
  await runLine(page, "p Peek.md");
  // Under its path, rendered as in the app.
  await expect(log).toContainText(`${base}/Peek.md`);
  await expect(log.getByRole("heading", { name: "Peek heading", level: 1 })).toBeVisible();
  await expect(log.getByRole("heading", { name: "Goals", level: 2 })).toBeVisible();
  await expect(log.getByRole("checkbox")).toBeDisabled();
  // A missing link is only marked: the console never writes.
  await expect(log).toContainText(`Nowhere-${folder} (missing)`);
  await expect(log.getByRole("button", { name: /^Create the note/ })).toHaveCount(0);

  // A section: its heading and text, nothing of the next one.
  await runLine(page, "clear");
  await runLine(page, "p Peek.md#Goals");
  await expect(log).toContainText(`${base}/Peek.md#Goals`);
  await expect(log.getByRole("heading", { name: "Goals" })).toBeVisible();
  await expect(log).toContainText("Goal text here.");
  await expect(log).not.toContainText("Later text here.");
  await expect(log.getByRole("heading", { name: "Peek heading" })).toHaveCount(0);

  // Errors in words.
  await runLine(page, "clear");
  await runLine(page, "p");
  await expect(log).toContainText("Error: p: missing note");
  await runLine(page, "p Sub");
  await expect(log).toContainText(`Error: p: ${base}/Sub: not a note`);
  await runLine(page, "p Absent.md");
  await expect(log.locator("[data-line]").last()).toHaveText(/^! Error: p: \S/);

  // A link opens the note and closes the console.
  await runLine(page, "clear");
  await runLine(page, "p Peek.md");
  await log.getByRole("link", { name: `T-${folder}` }).click();
  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/T-${folder}\\.md$`));
  await expect(log).toHaveCount(0);
  await expect(page.locator("article").getByText("Where the link leads.")).toBeVisible();
});

test("console #7: Plain, Green and Amber, kept on this device across a reload, and errors say Error: in each @console @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const base = await seed(aliceApi, folder);
  await openConsole(page);
  const phone = onPhone(testInfo);
  const colour = () => output(page).evaluate((e) => getComputedStyle(e).color);
  const plain = await colour();
  /** Desktop: three buttons, the chosen one pressed; phone: one button that cycles and names the scheme. */
  const choose = async (scheme: "Plain" | "Green" | "Amber") => {
    if (phone) {
      const cycle = page.getByRole("button", { name: /^Console scheme: / });
      await expect(async () => {
        if ((await cycle.textContent()) !== `Console scheme: ${scheme}`) await cycle.click();
        await expect(cycle).toHaveText(`Console scheme: ${scheme}`, { timeout: 1_000 });
      }).toPass();
    } else {
      await page.getByRole("group", { name: "Console scheme" }).getByRole("button", { name: scheme }).click();
    }
  };
  const shows = async (scheme: "Plain" | "Green" | "Amber") => {
    if (phone) await expect(page.getByRole("button", { name: /^Console scheme: / })).toHaveText(`Console scheme: ${scheme}`);
    else await expect(page.getByRole("group", { name: "Console scheme" }).getByRole("button", { name: scheme })).toHaveAttribute("aria-pressed", "true");
  };

  for (const [scheme, rgb] of [
    ["Green", "rgb(51, 255, 102)"],
    ["Amber", "rgb(255, 176, 0)"],
  ] as const) {
    await choose(scheme);
    await shows(scheme);
    await expect.poll(colour, `${scheme} is phosphor`).toBe(rgb);
    await runLine(page, `cat ${base}/Absent.md`);
    await expect(output(page).locator("[data-line]").last()).toContainText("Error:");
    await page.reload();
    await expect(command(page)).toBeEnabled();
    await shows(scheme);
    await expect.poll(colour, `${scheme} after the reload`).toBe(rgb);
  }

  await choose("Plain");
  await shows("Plain");
  await expect.poll(colour).toBe(plain);
  await runLine(page, `cat ${base}/Absent.md`);
  await expect(output(page).locator("[data-line]").last()).toContainText("Error:");
  await page.reload();
  await expect(command(page)).toBeEnabled();
  await shows("Plain");
});
