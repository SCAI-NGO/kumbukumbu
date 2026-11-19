// SPDX-License-Identifier: AGPL-3.0-or-later
// The workspace's steps without a browser test yet (spec 2026-11-18-workspace-design.md, its "Tests" and its hard
// anti-goals). The existing specs beside this one stay as they are; every step of the spec is mapped here to the test
// that covers it, in the browser or in Vitest (web/src), or why it is left out of this run.
//
// Side panel
//   shown from 1280 px, not below ............................. panel #1, panel #5; on a phone: gaps #11
//   "Linked from" not at the bottom where the panel is ......... panel #1, panel #4 (collapsed too)
//   the outline from unsaved text in Write mode ................ panel #3
//   a click on a heading puts the caret on its line ............ panel #3
//   the current heading marked: Write mode ..................... panel #3
//   the current heading marked: Read mode, by the scroll ....... gaps #1
//   Ctrl+Shift+\ hides and shows it ............................ panel #4
//   the focus moved to the toggle when the panel held it ....... gaps #2
//   collapsed state survives a reload .......................... panel #4
//   the tab survives a reload .................................. gaps #2
// Link completion
//   [[Note#, [[Note#^, ](, ](/x.md#, paths with %20 ............ completion #1, #2, #3
//   [[# from the open note's live text ......................... gaps #3
//   no list for an unknown note ................................ gaps #3
//   heading text cleaned, ambiguous names take the first, one ranking for palette and list, linkQuery's cases:
//     Vitest (web/src/note/linkCompletion.test.ts, web/src/palette/*.test.ts): logic, no flow of its own
// Pane model
//   currentPath and mode follow the focus, at most one pane writes: Vitest (web/src/state/editor.test.ts); seen in
//     the browser through split #2, #4, #5
//   starting Write in the other pane saves first (hard: no text lost) ... gaps #4
//   a failed save keeps Write where it was with its text: Vitest (editor.test.ts, web/src/shell/split.test.tsx); a
//     save refused on purpose needs a server fault this stack can't make
//   closing a writing pane saves first: the right ✕ while the left writes, split #8; the left ✕ while the right
//     writes, the right taking its place ....................... gaps #5
// Where things open
//   after focusing the right pane, a tree click opens there .... split #2
//   a link opens in its own pane ............................... split #3
//   the palette in the focused pane ............................ gaps #6
//   Ctrl+Enter on a palette row in the right ................... split #3
//   Split view as a palette command ............................ gaps #6
//   Ctrl+Alt+← / → focus a pane, its label says so ............. gaps #6
//   the tree marks the other pane's note ("Open in the other pane") ... gaps #6
//   Open beside in a tree row's menu ........................... gaps #7
//   Ctrl+Alt+click on a link opens it in the right pane ........ gaps #7
// Click to write
//   a click on a word starts Write there, the caret at it ...... split #4
//   a drag selects and switches nothing ........................ split #4
//   a double or triple click selects and switches nothing ...... split #10
//   a link click follows the link .............................. split #4
// Live preview
//   Write left, the preview right, marked with the glasses ..... split #1
//   typing shows in the preview, without holding typing up ..... split #1, split #6
//   follows the caret until scrolled by hand ................... split #6
//   a click into the preview swaps the roles, caret where clicked ... split #5
//   a link in the preview opens there .......................... split #5
//   the preview writes nothing of the note (a task box) ........ gaps #8
// URL
//   ?beside= restores both panes ............................... split #7
//   below 1280 px only the focused pane shows: the left ........ split #9
//   below 1280 px the focused right pane, and the split back once wide ... gaps #9
// Discreet: hiding the discreet vaults closes a pane on one of their notes; ?beside= neutral: web/e2e/discreet/
//   (another task, @serial)
// Templates
//   placeholders with formats and the time zone, properties merged, the template unchanged, one undo ... templates #1
//   unknown placeholders kept, {{time}}, Insert template from the palette ... gaps #10
//   unreadable frontmatter inserts only the body with the hint .............. gaps #10
//   the empty state with New template: Vitest (web/src/note/MarkdownEditor.test.tsx); in the browser it needs a vault
//     without a Templates folder, and a vault of the test's own changes Alice's vault list, which smoke #1 and #8
//     assert exactly while this project runs
// Left to the builder, also Vitest only: a deleted note's pane closes (split.test.tsx); the dock under the focused
//   pane only (split.test.tsx).
// The desktop project is 1280 px wide, the breakpoint.
import type { Locator, Page } from "@playwright/test";
import { dismissInstallHint } from "../calendar/helpers";
import type { Api } from "../support/api";
import { expect, onPhone, shot, test } from "../support/test";

const encoded = (path: string) => path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/");
const noteUrl = (path: string) => `/notes/${encoded(path)}`;
const splitUrl = (left: string, right: string) => `${noteUrl(left)}?beside=${encoded(right)}`;

// Names of the test's own: wiki links resolve vault-wide, and the Templates folder is the vault's.
const names = (folder: string) => {
  const [alpha, beta] = ["Alpha", "Beta"].map((n) => `${n} ${folder}`);
  return {
    alpha,
    beta,
    alphaPath: `/Work/${folder}/${alpha}.md`,
    betaPath: `/Work/${folder}/${beta}.md`,
    longPath: `/Work/${folder}/Long.md`,
    template: `Odd ${folder}`,
    templatePath: `/Work/Templates/Odd ${folder}.md`,
  };
};
const alphaText = (beta: string) => `# Alpha\n\nThe quick brown fox jumps over the lazy dog.\n\nSee [[${beta}]] next.\n\n- [ ] a task of Alpha's\n`;
const betaText = (alpha: string) => `# Beta\n\nBeta has plain words, and links back to [[${alpha}]].\n`;

async function seed(api: Api, folder: string) {
  const n = names(folder);
  await api.putNote(n.alphaPath, alphaText(n.beta));
  await api.putNote(n.betaPath, betaText(n.alpha));
  return n;
}

async function open(page: Page, url: string) {
  await page.goto(url);
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

const pane = (page: Page, side: "left" | "right") => page.getByRole("region", { name: side === "left" ? /^Left pane/ : /^Right pane/ });
const source = (where: Page | Locator) => where.getByRole("textbox", { name: "Markdown source", exact: true });
const glasses = (where: Locator) => where.getByRole("img", { name: /^Live preview of / });
const sidePanel = (page: Page) => page.getByRole("complementary", { name: "Side panel" });
const panelToggle = (page: Page) => page.getByRole("button", { name: "Side panel" });
const tree = (page: Page) => page.getByRole("complementary", { name: "File tree" });
const treeRow = (page: Page, name: string) => tree(page).getByRole("button", { name: new RegExp(`^${name}`) });

/** Where on screen the `into`th character of `word` (the first such text in an article of `where`) is, a pixel in. */
async function pointAt(where: Locator, word: string, into = 1) {
  return where.evaluate(
    (root, [word, into]) => {
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode() as Text | null; node; node = walker.nextNode() as Text | null) {
        const at = node.data.indexOf(word as string);
        if (at < 0 || !node.parentElement?.closest("article")) continue;
        const range = document.createRange();
        range.setStart(node, at + (into as number));
        range.setEnd(node, at + (into as number) + 1);
        const box = range.getBoundingClientRect();
        return { x: box.left + 1, y: box.top + box.height / 2 };
      }
      throw new Error(`no ${word}`);
    },
    [word, into] as const,
  );
}

/** A plain click on `word` in a reading pane, once its note is rendered there: pointAt looks once, it doesn't wait. */
async function clickOn(page: Page, side: "left" | "right", word: string) {
  await expect(pane(page, side).locator("article").getByText(word, { exact: false }).first()).toBeVisible();
  const at = await pointAt(pane(page, side), word);
  await page.mouse.click(at.x, at.y);
  await expect(source(pane(page, side))).toBeFocused();
}

async function palette(page: Page, query: string, row: RegExp) {
  await page.keyboard.press("Control+p");
  await page.getByRole("combobox", { name: "Find a command, vault or note" }).fill(query);
  await expect(page.getByRole("option", { name: row }).first()).toHaveAttribute("aria-selected", "true");
  await page.keyboard.press("Enter");
}

test.afterEach(async ({ aliceApi, folder }) => {
  const n = names(folder);
  for (const path of [n.alphaPath, n.betaPath, n.longPath, n.templatePath]) await aliceApi.deleteNote(path).catch(() => undefined);
});

test("workspace gaps #1: in Read mode the outline marks the last heading above the top of the visible text, following the scroll @workspace", async ({ page, aliceApi, folder }) => {
  const { longPath } = names(folder);
  const filler = (what: string) => Array.from({ length: 40 }, (_, i) => `Line ${i + 1} about ${what}.`).join("\n\n");
  await aliceApi.putNote(longPath, ["# Long", "", filler("the start"), "", "## Middle", "", filler("the middle"), "", "## End", "", filler("the end"), ""].join("\n"));
  await open(page, noteUrl(longPath));
  await sidePanel(page).getByRole("tab", { name: "Outline" }).click();
  const entry = (name: string) => sidePanel(page).getByRole("tabpanel", { name: "Outline" }).getByRole("button", { name });

  await entry("Middle").click();
  await expect(page.locator("article").getByRole("heading", { name: "Middle" })).toBeInViewport();
  await expect(entry("Middle")).toHaveAttribute("aria-current", "location");
  await expect(entry("Long")).not.toHaveAttribute("aria-current", "location");

  // Scrolled by hand to "Line 20 about the end": End is the last heading above the top.
  await page.locator("article").getByText("Line 20 about the end.").evaluate((p) => p.scrollIntoView({ block: "start" }));
  await expect(entry("End")).toHaveAttribute("aria-current", "location");
  await expect(entry("Middle")).not.toHaveAttribute("aria-current", "location");
});

test("workspace gaps #2: hiding the panel that holds the focus hands it to the toggle, and the tab chosen survives a reload @workspace", async ({ page, aliceApi, folder }) => {
  const { alphaPath } = await seed(aliceApi, folder);
  await open(page, noteUrl(alphaPath));
  const outlineTab = sidePanel(page).getByRole("tab", { name: "Outline" });
  await outlineTab.click();
  await expect(outlineTab).toBeFocused();

  await page.keyboard.press("Control+Shift+Backslash");
  await expect(sidePanel(page)).toHaveCount(0);
  await expect(panelToggle(page)).toBeFocused();
  await page.keyboard.press("Control+Shift+Backslash");
  await expect(sidePanel(page)).toBeVisible();

  await page.reload();
  await expect(sidePanel(page).getByRole("tab", { name: "Outline" })).toHaveAttribute("aria-selected", "true");
  await expect(sidePanel(page).getByRole("tabpanel", { name: "Outline" }).getByRole("button", { name: "Alpha" })).toBeVisible();
});

test("workspace gaps #3: [[# lists the open note's own headings from the unsaved text, and an unknown note gets no list @workspace", async ({ page, aliceApi, folder }) => {
  const { alphaPath } = await seed(aliceApi, folder);
  await open(page, noteUrl(alphaPath));
  await page.getByRole("button", { name: "Write" }).click();
  const area = source(page);
  await area.focus();
  await page.keyboard.press("Control+End");
  const before = alphaText(names(folder).beta);

  await page.keyboard.type("\n## Fresh heading\n\nBack to [[#");
  const headings = page.getByRole("listbox", { name: "Headings to link" }).getByRole("option");
  await expect(headings).toHaveText([/^Alpha/, /^Fresh heading/]);
  await page.keyboard.type("fr");
  await expect(headings).toHaveText([/^Fresh heading/]);
  await page.keyboard.press("Enter");
  await expect(area).toHaveValue(`${before}\n## Fresh heading\n\nBack to [[#Fresh heading]]`);

  // A note no path ends with: no headings to list, and no list.
  await page.keyboard.type(` and [[Nobody ${folder}#`);
  await expect(area).toHaveValue(new RegExp(`\\[\\[Nobody ${folder}#$`));
  await expect(page.getByRole("listbox", { name: /to link$/ })).toHaveCount(0);
  await expect(page.getByText("Loading…")).toHaveCount(0);
});

test("workspace gaps #4: Write moving to the other pane saves what was typed first, and nothing typed is lost @workspace", async ({ page, aliceApi, folder }) => {
  const n = await seed(aliceApi, folder);
  await open(page, splitUrl(n.alphaPath, n.betaPath));

  await clickOn(page, "left", "brown");
  await page.keyboard.press("Control+End");
  await page.keyboard.type("Typed on the left.\n");
  await clickOn(page, "right", "plain");
  await expect(source(pane(page, "left"))).toHaveCount(0);
  const alphaNow = `${alphaText(n.beta)}Typed on the left.\n`;
  expect((await aliceApi.getNote(n.alphaPath)).content, "saved as Done saves").toBe(alphaNow);
  await expect(pane(page, "left").locator("article").getByText("Typed on the left.")).toBeVisible();

  // And back: the right's typing saved as the left takes Write again.
  await page.keyboard.press("Control+End");
  await page.keyboard.type("Typed on the right.\n");
  await clickOn(page, "left", "quick");
  await expect(source(pane(page, "right"))).toHaveCount(0);
  expect((await aliceApi.getNote(n.betaPath)).content).toBe(`${betaText(n.alpha)}Typed on the right.\n`);
  await expect(source(pane(page, "left"))).toHaveValue(alphaNow);
});

test("workspace gaps #5: the left pane's ✕ while the right one writes saves the right one's text, and it takes the left's place @workspace", async ({ page, aliceApi, folder }) => {
  const n = await seed(aliceApi, folder);
  await open(page, splitUrl(n.alphaPath, n.betaPath));
  await clickOn(page, "right", "plain");
  await page.keyboard.press("Control+End");
  await page.keyboard.type("Typed before the left closed.\n");

  await pane(page, "left").getByRole("button", { name: "Close this pane" }).click();
  await expect(page.getByRole("region", { name: /^(Left|Right) pane/ })).toHaveCount(0);
  await expect(page).toHaveURL(noteUrl(n.betaPath));
  const typed = `${betaText(n.alpha)}Typed before the left closed.\n`;
  await expect.poll(async () => (await aliceApi.getNote(n.betaPath)).content).toBe(typed);
  // Saved as Done saves: the pane reads now, showing what was typed.
  await expect(source(page)).toHaveCount(0);
  await expect(page.locator("article").getByText("Typed before the left closed.")).toBeVisible();
});

test("workspace gaps #6: Split view from the palette; Ctrl+Alt+→ focuses the right pane, the palette opens there and the tree marks both notes @workspace", async ({ page, aliceApi, folder }, testInfo) => {
  const n = await seed(aliceApi, folder);
  await open(page, noteUrl(n.alphaPath));
  await palette(page, "Split view", /^Split view/);
  await expect(source(pane(page, "left"))).toBeFocused();
  await expect(glasses(pane(page, "right"))).toBeVisible();
  await expect(page).toHaveURL(splitUrl(n.alphaPath, n.alphaPath));

  await page.keyboard.press("Control+Alt+ArrowRight");
  await expect(page.getByRole("region", { name: "Right pane, focused" })).toHaveAttribute("aria-current", "true");
  await expect(page.getByRole("region", { name: "Left pane", exact: true })).not.toHaveAttribute("aria-current", "true");
  await palette(page, n.beta, new RegExp(`^${n.beta}`));
  await expect(pane(page, "right").getByRole("heading", { name: "Beta" })).toBeVisible();
  await expect(page).toHaveURL(splitUrl(n.alphaPath, n.betaPath));
  // The left keeps its Write mode and its note.
  await expect(source(pane(page, "left"))).toHaveValue(alphaText(n.beta));

  // The focused pane's note is the tree's current one; the other's is marked more quietly, and said to be there.
  await expect(treeRow(page, n.beta)).toHaveAttribute("aria-current", "page");
  await expect(treeRow(page, n.alpha)).not.toHaveAttribute("aria-current", "page");
  await expect(treeRow(page, n.alpha)).toHaveAccessibleDescription("Open in the other pane");
  await shot(page, testInfo, "workspace");

  // Ctrl+Alt+← back: the focus into the left pane's editor, the marks swapped.
  await page.keyboard.press("Control+Alt+ArrowLeft");
  await expect(page.getByRole("region", { name: "Left pane, focused" })).toBeVisible();
  await expect(source(pane(page, "left"))).toBeFocused();
  await expect(treeRow(page, n.alpha)).toHaveAttribute("aria-current", "page");
  await expect(treeRow(page, n.beta)).toHaveAccessibleDescription("Open in the other pane");
});

test("workspace gaps #7: Open beside in a tree row's menu and Ctrl+Alt+click on a link open the note in the right pane, focused @workspace", async ({ page, context, aliceApi, folder }) => {
  const n = await seed(aliceApi, folder);
  await open(page, noteUrl(n.alphaPath));

  await tree(page).getByRole("button", { name: `Actions for ${n.beta}`, exact: true }).click();
  await page.getByRole("menuitem", { name: "Open beside" }).click();
  await expect(page).toHaveURL(splitUrl(n.alphaPath, n.betaPath));
  await expect(page.getByRole("region", { name: "Right pane, focused" }).getByRole("heading", { name: "Beta" })).toBeVisible();
  await expect(pane(page, "left").getByRole("heading", { name: "Alpha" })).toBeVisible();

  await pane(page, "right").getByRole("button", { name: "Close this pane" }).click();
  await expect(page).toHaveURL(noteUrl(n.alphaPath));
  await expect(page.getByRole("region", { name: /^(Left|Right) pane/ })).toHaveCount(0);

  // The page's click, not the browser's: no new tab, no new window.
  await page.locator("article").getByRole("link", { name: n.beta }).click({ modifiers: ["Control", "Alt"] });
  await expect(page).toHaveURL(splitUrl(n.alphaPath, n.betaPath));
  await expect(page.getByRole("region", { name: "Right pane, focused" }).getByRole("heading", { name: "Beta" })).toBeVisible();
  await expect(pane(page, "left").getByRole("heading", { name: "Alpha" })).toBeVisible();
  expect(context.pages(), "no other tab").toHaveLength(1);
});

test("workspace gaps #8: a task box in the live preview ticks nothing: the note is the editor's @workspace", async ({ page, aliceApi, folder }) => {
  const n = await seed(aliceApi, folder);
  await open(page, noteUrl(n.alphaPath));
  await page.getByRole("button", { name: "Split view" }).click();
  await expect(glasses(pane(page, "right"))).toBeVisible();

  const box = pane(page, "right").locator("article").getByRole("checkbox", { name: /a task of Alpha's/ });
  await expect(box).not.toBeChecked();
  await box.click({ force: true });
  await expect(box).not.toBeChecked();
  await expect(source(pane(page, "left"))).toHaveValue(alphaText(n.beta));
  await expect(glasses(pane(page, "right"))).toBeVisible();
  expect((await aliceApi.getNote(n.alphaPath)).content).toBe(alphaText(n.beta));
});

test("workspace gaps #9: below 1280 px the focused right pane is the one shown, and the split comes back once wide @workspace", async ({ page, aliceApi, folder }) => {
  const n = await seed(aliceApi, folder);
  await open(page, splitUrl(n.alphaPath, n.betaPath));
  await page.keyboard.press("Control+Alt+ArrowRight");
  await expect(page.getByRole("region", { name: "Right pane, focused" })).toBeVisible();

  await page.setViewportSize({ width: 1024, height: 800 });
  await expect(page.getByRole("heading", { name: "Beta" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Alpha" })).toBeHidden();
  await expect(page.getByRole("region", { name: /^(Left|Right) pane/ })).toHaveCount(0);
  await expect(page).toHaveURL(splitUrl(n.alphaPath, n.betaPath));

  await page.setViewportSize({ width: 1280, height: 800 });
  await expect(pane(page, "left").getByRole("heading", { name: "Alpha" })).toBeVisible();
  await expect(pane(page, "right").getByRole("heading", { name: "Beta" })).toBeVisible();
  await expect(pane(page, "right")).toHaveAttribute("aria-current", "true");
});

test("workspace gaps #10: Insert template from the palette keeps unknown placeholders, fills {{time}}, and only the body goes into a note whose properties can't be read @workspace", async ({ page, aliceApi, folder }) => {
  const n = names(folder);
  await aliceApi.putNote(n.templatePath, "---\ntags: [meeting]\n---\n## {{title}} at {{time}}\n\n{{weather}} and <% tp.file.title %> stay.\n");
  const noteText = "---\nnested:\n  key: v\n---\n# Weekly\n";
  await aliceApi.putNote(n.alphaPath, noteText);
  await open(page, noteUrl(n.alphaPath));
  await page.getByRole("button", { name: "Write" }).click();
  const area = source(page);
  await area.focus();
  await page.keyboard.press("Control+End");

  // The time in Vienna, before and after: a minute may turn meanwhile.
  const now = () => new Intl.DateTimeFormat("en-GB", { timeZone: "Europe/Vienna", hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).format(new Date());
  const before = now();
  await palette(page, "Insert template", /^Insert template/);
  await expect(page.getByRole("combobox", { name: "Find a template" })).toBeFocused();
  await page.keyboard.type(n.template);
  await expect(page.getByRole("listbox", { name: "Templates" }).getByRole("option")).toHaveText([n.template]);
  await page.keyboard.press("Enter");
  const after = now();

  const body = (time: string) => `## ${n.alpha} at ${time}\n\n{{weather}} and <% tp.file.title %> stay.\n`;
  await expect(area).toHaveValue(new RegExp(`^${noteText.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}## `));
  const value = await area.inputValue();
  expect([`${noteText}${body(before)}`, `${noteText}${body(after)}`], "the body only, its unknown placeholders as written").toContain(value);
  await expect(page.getByRole("status", { name: "Template" })).toHaveText("The template's properties weren't added: this note's properties can't be read.");
  expect((await aliceApi.getNote(n.templatePath)).content).toContain("{{time}}");
});

test("workspace gaps #11: on a phone there is no side panel and no split; Linked from stays at the note's bottom @workspace @phone", async ({ page, aliceApi, folder }, testInfo) => {
  test.skip(!onPhone(testInfo), "the phone's; at 1024 px on a desktop: panel #5 and split #9");
  await dismissInstallHint(page);
  const n = await seed(aliceApi, folder);
  await page.goto(noteUrl(n.alphaPath));
  await expect(page.getByRole("heading", { name: "Alpha" })).toBeVisible();

  const linked = page.getByRole("region", { name: "Linked from" });
  await expect(linked.getByRole("heading", { name: "Linked from · 1" })).toBeVisible();
  await expect(sidePanel(page)).toHaveCount(0);
  await expect(panelToggle(page)).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Split view" })).toHaveCount(0);
  const article = (await page.locator("article").first().boundingBox())!;
  expect((await linked.boundingBox())!.y, "below the note's text").toBeGreaterThan(article.y + article.height - 1);
  await shot(page, testInfo, "workspace");
});
