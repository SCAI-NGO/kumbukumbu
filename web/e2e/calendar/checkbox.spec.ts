// SPDX-License-Identifier: AGPL-3.0-or-later
// The calendar line's tick is a real checkbox (fixes spec 2026-11-14-0-5-0-fixes-design.md §2.2).
import { addDays, today } from "../support/time";
import { cleanFolder, dismissInstallHint, entryLine, expect, note, notePage, shortDay, tagOf, test, weekUrl } from "./helpers";

/** The lines of `after` that `before` hasn't, and the reverse, each line counted as often as it occurs. */
function lineChanges(before: string, after: string) {
  const without = (from: string[], take: string[]) => {
    const rest = [...take];
    return from.filter((line) => {
      const i = rest.indexOf(line);
      if (i < 0) return true;
      rest.splice(i, 1);
      return false;
    });
  };
  const a = before.split("\n");
  const b = after.split("\n");
  return { added: without(b, a), removed: without(a, b) };
}

test.beforeEach(async ({ page }) => {
  await dismissInstallHint(page);
});

test.afterEach(async ({ aliceApi, folder }) => {
  await cleanFolder(aliceApi, folder);
});

test("fixes §2.2 #1: ☐ To do ticks the to-do closed, ☑ Done unticks it, nothing else in the note changes @calendar @phone", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Certificate.md`;
  const when = addDays(today(), 30);
  await aliceApi.putNote(path, note({ owner: "Alice", when, task: "open", upcoming: "7d" }, "# Certificate\n\nThe pump's certificate.\n"));
  const before = (await aliceApi.getNote(path)).content;

  await page.goto(notePage(path));
  const line = entryLine(page);
  const open = line.getByRole("checkbox", { name: "To do" });
  await expect(open).not.toBeChecked();
  const size = (await open.boundingBox())!;
  expect(size.width, "the box's target is 48 px wide").toBeGreaterThanOrEqual(48);
  expect(size.height, "the box's target is 48 px high").toBeGreaterThanOrEqual(48);

  // The day it is closed on, read once: midnight in Vienna may pass while the test runs.
  const closedOn = today();
  await open.click();
  const done = line.getByRole("checkbox", { name: "Done" });
  await expect(done).toBeChecked();
  await expect.poll(async () => (await aliceApi.getNote(path)).content).toMatch(/^task: closed$/m);
  expect(lineChanges(before, (await aliceApi.getNote(path)).content)).toEqual({ added: ["task: closed", `closed: ${closedOn}`], removed: ["task: open"] });

  await done.click();
  await expect(line.getByRole("checkbox", { name: "To do" })).not.toBeChecked();
  await expect.poll(async () => (await aliceApi.getNote(path)).content).toBe(before);
});

test("fixes §2.2 #2: the week's card shows ☑ for a closed to-do, and unticking reopens it @calendar @phone", async ({ page, aliceApi, folder }) => {
  const t = today();
  const tag = tagOf(folder);
  const path = `/Work/${folder}/Pay invoice.md`;
  await aliceApi.putNote(path, note({ when: t, task: "closed", closed: t, tags: `[${tag}]` }, "# Pay invoice\n"));

  await page.goto(weekUrl("Work", t, tag));
  const box = page.getByRole("checkbox", { name: "Done: Pay invoice" });
  await expect(box).toBeChecked();
  await box.click();
  await expect(page.getByRole("checkbox", { name: "To do: Pay invoice" })).not.toBeChecked();
  await expect.poll(async () => (await aliceApi.getNote(path)).content).toMatch(/^task: open$/m);
  expect((await aliceApi.getNote(path)).content).not.toMatch(/^closed:/m);
});

test("fixes §2.2 #3: the date keeps its dots, the controls follow without @calendar", async ({ page, aliceApi, folder }) => {
  const path = `/Work/${folder}/Call.md`;
  const when = addDays(today(), 3);
  await aliceApi.putNote(path, note({ when: `${when}T11:30`, task: "open" }, "# Call\n"));
  await page.goto(notePage(path));
  await expect(entryLine(page)).toHaveText(`📅 ${shortDay(when, true)} · 11:30 To do Later`);
});
