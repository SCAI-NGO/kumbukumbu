// SPDX-License-Identifier: AGPL-3.0-or-later
// Note templates (spec 2026-11-18-workspace-design.md §4; plan task 4.4): a note under Templates/ inserted at the
// caret, its placeholders filled in the user's zone (the e2e user's is Europe/Vienna) and its properties merged.
import { expect, test } from "../support/test";

const noteUrl = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;

// The template of the test's own: the Templates folder is the vault's, shared with every other test.
const names = (folder: string) => ({ template: `Meeting ${folder}`, templatePath: `/Work/Templates/Meeting ${folder}.md`, notePath: `/Work/${folder}/Weekly.md` });

const templateText = "---\ntags: [meeting]\nattendees:\n  - Ada\ncreated: {{date}}\n---\n## {{title}} on {{date:dddd, D MMMM YYYY}}\n\n- Agenda\n";
const noteText = "---\ntags: [work]\nstatus: open\n---\n# Weekly\n\nend\n";

/** Today in Vienna, as `YYYY-MM-DD` and as `dddd, D MMMM YYYY`. */
function today() {
  const parts = Object.fromEntries(
    new Intl.DateTimeFormat("en-US", { timeZone: "Europe/Vienna", year: "numeric", month: "2-digit", day: "2-digit", weekday: "long" })
      .formatToParts(new Date())
      .map((p) => [p.type, p.value]),
  );
  const month = new Intl.DateTimeFormat("en-US", { timeZone: "Europe/Vienna", month: "long" }).format(new Date());
  return { iso: `${parts.year}-${parts.month}-${parts.day}`, long: `${parts.weekday}, ${Number(parts.day)} ${month} ${parts.year}` };
}

test.afterEach(async ({ aliceApi, folder }) => {
  const { templatePath, notePath } = names(folder);
  for (const path of [templatePath, notePath]) await aliceApi.deleteNote(path).catch(() => undefined);
});

test("workspace templates #1: a template goes in at the caret, its placeholders filled and its properties merged @workspace", async ({ page, aliceApi, folder }) => {
  const { template, templatePath, notePath } = names(folder);
  await aliceApi.putNote(templatePath, templateText);
  await aliceApi.putNote(notePath, noteText);

  await page.goto(noteUrl(notePath));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  // The caret before `end`.
  const caret = noteText.indexOf("end");
  await source.evaluate((area: HTMLTextAreaElement, at) => area.setSelectionRange(at, at), caret);

  await page.getByRole("button", { name: "Insert template" }).click();
  await expect(page.getByRole("combobox", { name: "Find a template" })).toBeFocused();
  await page.keyboard.type(template);
  await expect(page.getByRole("listbox", { name: "Templates" }).getByRole("option")).toHaveText([template]);
  await page.keyboard.press("Enter");

  const { iso, long } = today();
  const body = `## Weekly on ${long}\n\n- Agenda\n`;
  const expected = `---\ntags: [work, meeting]\nstatus: open\nattendees:\n  - Ada\ncreated: ${iso}\n---\n# Weekly\n\n${body}end\n`;
  await expect(source).toHaveValue(expected);
  await expect(source).toBeFocused();
  expect(await source.evaluate((area: HTMLTextAreaElement) => [area.selectionStart, area.selectionEnd])).toEqual([expected.indexOf("end\n"), expected.indexOf("end\n")]);

  // One undo step takes it all back.
  await page.keyboard.press("Control+z");
  await expect(source).toHaveValue(noteText);
  await page.keyboard.press("Control+Shift+z");
  await expect(source).toHaveValue(expected);

  await page.getByRole("button", { name: "Done" }).click();
  await expect(page.getByRole("button", { name: "Done" })).toHaveCount(0);
  expect((await aliceApi.getNote(notePath)).content).toBe(expected);
  // The template keeps its raw placeholders.
  expect((await aliceApi.getNote(templatePath)).content).toBe(templateText);
});
