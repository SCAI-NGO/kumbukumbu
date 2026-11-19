// SPDX-License-Identifier: AGPL-3.0-or-later
// The split view (spec 2026-11-18-workspace-design.md §3; plan tasks 3b.3–3b.7): two panes from 1280 px, one editor
// at a time, Write following the click with the caret where it was, and the live preview of the note being written.
// The desktop project is 1280 px wide, the breakpoint.
import type { Locator, Page } from "@playwright/test";
import { dismissInstallHint } from "../calendar/helpers";
import type { Api } from "../support/api";
import { expect, onPhone, test } from "../support/test";

const encoded = (path: string) => path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/");
const noteUrl = (path: string) => `/notes/${encoded(path)}`;
const splitUrl = (left: string, right: string) => `${noteUrl(left)}?beside=${encoded(right)}`;

// Names of the test's own: wiki links resolve vault-wide, and other tests have their notes too.
const names = (folder: string) => {
  const [alpha, beta, gamma] = ["Alpha", "Beta", "Gamma"].map((n) => `${n} ${folder}`);
  return { alpha, beta, gamma, alphaPath: `/Work/${folder}/${alpha}.md`, betaPath: `/Work/${folder}/${beta}.md`, gammaPath: `/Work/${folder}/${gamma}.md` };
};
const alphaText = (beta: string) => `# Alpha\n\nThe quick brown fox jumps over the lazy dog.\n\nSee [[${beta}]] next.\n`;
const betaText = (gamma: string) => `# Beta\n\nBeta has **bold** words and a [[${gamma}]] link.\n`;

/** Alpha linking to Beta, Beta linking to Gamma. */
async function seed(api: Api, folder: string) {
  const n = names(folder);
  await api.putNote(n.alphaPath, alphaText(n.beta));
  await api.putNote(n.betaPath, betaText(n.gamma));
  await api.putNote(n.gammaPath, "# Gamma\n\nGamma's own text.\n");
  return n;
}

const pane = (page: Page, side: "left" | "right") => page.getByRole("region", { name: side === "left" ? /^Left pane/ : /^Right pane/ });
const source = (where: Page | Locator) => where.getByRole("textbox", { name: "Markdown source", exact: true });
const noteBar = (where: Locator) => where.getByRole("group", { name: "This note" });
const glasses = (where: Locator) => where.getByRole("img", { name: /^Live preview of / });

async function open(page: Page, url: string) {
  await page.goto(url);
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

/** Opens Alpha and starts the split view: Write on the left, the preview on the right. */
async function startSplit(page: Page, alphaPath: string) {
  await open(page, noteUrl(alphaPath));
  await page.getByRole("button", { name: "Split view" }).click();
  await expect(source(pane(page, "left"))).toBeFocused();
  await expect(glasses(pane(page, "right"))).toBeVisible();
}

/** Where on screen the `into`th character of `word` (the first such text in `where`) starts, a pixel in. */
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

/** Where the textarea's caret stands, and the selection's end. */
const caret = (area: Locator) => area.evaluate((a: HTMLTextAreaElement) => ({ start: a.selectionStart, end: a.selectionEnd, value: a.value }));

test.afterEach(async ({ aliceApi, folder }) => {
  const n = names(folder);
  for (const path of [n.alphaPath, n.betaPath, n.gammaPath, `/Work/${folder}/Long.md`]) await aliceApi.deleteNote(path).catch(() => undefined);
});

test("workspace split #1: Split view starts with Write left and the live preview right, which shows what is typed @workspace", async ({ page, aliceApi, folder }) => {
  const n = await seed(aliceApi, folder);
  await startSplit(page, n.alphaPath);
  await expect(page).toHaveURL(splitUrl(n.alphaPath, n.alphaPath));
  await expect(glasses(pane(page, "right"))).toHaveAccessibleName(`Live preview of ${n.alpha}`);
  await expect(glasses(pane(page, "left"))).toHaveCount(0);

  await page.keyboard.press("Control+End");
  await page.keyboard.type("\n\nTyped just now, not saved yet");
  await expect(pane(page, "right").locator("article").getByText("Typed just now, not saved yet")).toBeVisible();
});

test("workspace split #2: the right pane chosen by its note bar, a note from the tree opens there while the left keeps Write and its text @workspace", async ({ page, aliceApi, folder }) => {
  const n = await seed(aliceApi, folder);
  await startSplit(page, n.alphaPath);
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\n\nKept on the left");

  await noteBar(pane(page, "right")).getByText(n.alpha).click();
  await expect(pane(page, "right")).toHaveAttribute("aria-current", "true");
  await expect(source(pane(page, "right"))).toHaveCount(0);
  await page.getByRole("complementary", { name: "File tree" }).getByRole("button", { name: new RegExp(`^${n.beta}`) }).click();

  await expect(pane(page, "right").getByRole("heading", { name: "Beta" })).toBeVisible();
  await expect(page).toHaveURL(splitUrl(n.alphaPath, n.betaPath));
  await expect(source(pane(page, "left"))).toHaveValue(`${alphaText(n.beta)}\n\nKept on the left`);
  await expect(glasses(pane(page, "right"))).toHaveCount(0);
});

test("workspace split #3: a link opens in its own pane, and Ctrl+Enter in the palette in the right one @workspace", async ({ page, aliceApi, folder }) => {
  const n = await seed(aliceApi, folder);
  await open(page, splitUrl(n.alphaPath, n.betaPath));

  await pane(page, "right").getByRole("link", { name: n.gamma }).click();
  await expect(pane(page, "right").getByRole("heading", { name: "Gamma" })).toBeVisible();
  await expect(page).toHaveURL(splitUrl(n.alphaPath, n.gammaPath));
  await expect(pane(page, "left").getByRole("heading", { name: "Alpha" })).toBeVisible();

  await pane(page, "left").getByRole("link", { name: n.beta }).click();
  await expect(pane(page, "left").getByRole("heading", { name: "Beta" })).toBeVisible();
  await expect(page).toHaveURL(splitUrl(n.betaPath, n.gammaPath));

  // The left pane focused (the link was clicked there): Ctrl+Enter still opens in the right one, and focuses it.
  await page.keyboard.press("Control+p");
  await page.getByRole("combobox", { name: "Find a command, vault or note" }).fill(n.alpha);
  await expect(page.getByRole("option", { name: new RegExp(`^${n.alpha}`) }).first()).toBeVisible();
  await page.keyboard.press("Control+Enter");
  await expect(pane(page, "right").getByRole("heading", { name: "Alpha" })).toBeVisible();
  await expect(page).toHaveURL(splitUrl(n.betaPath, n.alphaPath));
  await expect(pane(page, "right")).toHaveAttribute("aria-current", "true");
});

test("workspace split #4: a click on a word in the reading pane starts Write there, the caret at that word; a drag selects and switches nothing @workspace", async ({ page, aliceApi, folder }) => {
  const n = await seed(aliceApi, folder);
  await open(page, splitUrl(n.alphaPath, n.betaPath));

  // A drag over "bold" in the right pane: selected, nothing switched.
  const from = await pointAt(pane(page, "right"), "bold", 0);
  const to = await pointAt(pane(page, "right"), "words", 0);
  await page.mouse.move(from.x - 1, from.y);
  await page.mouse.down();
  await page.mouse.move(to.x, to.y, { steps: 5 });
  await page.mouse.up();
  expect(await page.evaluate(() => String(window.getSelection()))).toContain("bold");
  await expect(source(page)).toHaveCount(0);

  // A click two characters into "words", after the bold one: Write mode there, the caret before its "r".
  const at = await pointAt(pane(page, "right"), "words", 2);
  await page.mouse.click(at.x, at.y);
  await expect(source(pane(page, "right"))).toBeFocused();
  const text = betaText(n.gamma);
  expect(await caret(source(pane(page, "right")))).toEqual({ start: text.indexOf("words") + 2, end: text.indexOf("words") + 2, value: text });

  // Then a click on the left pane's "brown": Write moves there, its caret at that word.
  const brown = await pointAt(pane(page, "left"), "brown", 1);
  await page.mouse.click(brown.x, brown.y);
  await expect(source(pane(page, "left"))).toBeFocused();
  await expect(source(pane(page, "right"))).toHaveCount(0);
  expect((await caret(source(pane(page, "left")))).start).toBe(alphaText(n.beta).indexOf("brown") + 1);

  // A link keeps its own click.
  await pane(page, "right").getByRole("link", { name: n.gamma }).click();
  await expect(pane(page, "right").getByRole("heading", { name: "Gamma" })).toBeVisible();
  await expect(source(pane(page, "right"))).toHaveCount(0);
});

test("workspace split #5: a click in the preview swaps the roles, the caret where clicked, and a link in it opens there @workspace", async ({ page, aliceApi, folder }) => {
  const n = await seed(aliceApi, folder);
  await startSplit(page, n.alphaPath);
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\n\nWritten on the left first");
  const preview = pane(page, "right").locator("article").getByText("Written on the left first");
  await expect(preview).toBeVisible();

  const at = await pointAt(pane(page, "right"), "left first", 0);
  await page.mouse.click(at.x, at.y);
  await expect(source(pane(page, "right"))).toBeFocused();
  await expect(source(pane(page, "left"))).toHaveCount(0);
  await expect(glasses(pane(page, "left"))).toBeVisible();
  const text = `${alphaText(n.beta)}\n\nWritten on the left first`;
  expect(await caret(source(pane(page, "right")))).toEqual({ start: text.indexOf("left first"), end: text.indexOf("left first"), value: text });

  // Typed on the right now, shown on the left.
  await page.keyboard.type("then ");
  await expect(pane(page, "left").locator("article").getByText("Written on the then left first")).toBeVisible();

  // A link in the preview opens in the preview's pane: the preview ends there.
  await pane(page, "left").getByRole("link", { name: n.beta }).click();
  await expect(pane(page, "left").getByRole("heading", { name: "Beta" })).toBeVisible();
  await expect(glasses(page.locator("main"))).toHaveCount(0);
  await expect(source(pane(page, "right"))).toHaveValue(text.replace("left first", "then left first"));
});

test("workspace split #6: the preview follows the caret, until scrolled by hand, and typing in a long note isn't held up by it @workspace", async ({ page, aliceApi, folder }) => {
  const long = `/Work/${folder}/Long.md`;
  const lines = Array.from({ length: 2000 }, (_, i) => (i % 20 === 0 ? `## Section ${i / 20}` : `Line ${i} of the long note, with **some** words in it.`));
  await aliceApi.putNote(long, `# Long\n\n${lines.join("\n\n")}\n`);
  await open(page, noteUrl(long));
  await page.getByRole("button", { name: "Split view" }).click();
  const area = source(pane(page, "left"));
  await expect(area).toBeFocused();
  const preview = pane(page, "right");
  // The note shown in the preview first: opening it takes its time, typing is what is measured.
  await expect(preview.locator("article").getByText(/^Line 1999 /)).toBeAttached();
  await page.evaluate(() => new Promise((done) => requestAnimationFrame(() => setTimeout(done, 300))));

  // Each key's wait between the browser taking it and the page handling it: what a busy main thread would show.
  await page.evaluate(() => {
    const waits: number[] = [];
    (window as unknown as { waits: number[] }).waits = waits;
    document.addEventListener("keydown", (e) => waits.push(performance.now() - e.timeStamp), true);
  });
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\n\nAt the very end", { delay: 40 });
  await expect(area).toHaveValue(/At the very end$/);
  const waits = await page.evaluate(() => (window as unknown as { waits: number[] }).waits);
  expect(Math.max(...waits), `each key handled at once (${waits.map(Math.round).join(", ")} ms)`).toBeLessThan(100);

  // The caret's block in view in the preview.
  await expect(preview.locator("article").getByText("At the very end")).toBeInViewport();

  // Scrolled by hand to the top, the preview stays there; the next keystroke brings the caret's block back.
  await preview.locator("article").hover();
  await page.mouse.wheel(0, -1_000_000);
  await expect(preview.getByRole("heading", { name: "Long" })).toBeInViewport();
  await page.waitForTimeout(500);
  await expect(preview.getByRole("heading", { name: "Long" })).toBeInViewport();
  await area.focus();
  await page.keyboard.type("!");
  await expect(preview.locator("article").getByText("At the very end!")).toBeInViewport();
});

test("workspace split #7: ?beside= keeps both panes over a reload @workspace", async ({ page, aliceApi, folder }) => {
  const n = await seed(aliceApi, folder);
  await open(page, splitUrl(n.alphaPath, n.betaPath));
  await page.reload();
  await expect(pane(page, "left").getByRole("heading", { name: "Alpha" })).toBeVisible();
  await expect(pane(page, "right").getByRole("heading", { name: "Beta" })).toBeVisible();
  await expect(page).toHaveURL(splitUrl(n.alphaPath, n.betaPath));
});

test("workspace split #8: Split view on while writing, and the right ✕ while the left writes, keep the typed text @workspace", async ({ page, aliceApi, folder }) => {
  const n = await seed(aliceApi, folder);
  await open(page, noteUrl(n.alphaPath));
  await page.getByRole("button", { name: "Write" }).click();
  const area = source(page);
  await area.focus();
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\n\nTyped before the split");

  await page.getByRole("button", { name: "Split view" }).click();
  await expect(glasses(pane(page, "right"))).toBeVisible();
  await expect(source(pane(page, "left"))).toHaveValue(`${alphaText(n.beta)}\n\nTyped before the split`);
  await source(pane(page, "left")).focus();
  await page.keyboard.type(", and after");

  await pane(page, "right").getByRole("button", { name: "Close this pane" }).click();
  await expect(page).toHaveURL(noteUrl(n.alphaPath));
  await expect(page.getByRole("region", { name: /^(Left|Right) pane/ })).toHaveCount(0);
  await expect(source(page)).toHaveValue(`${alphaText(n.beta)}\n\nTyped before the split, and after`);
  await source(page).focus();
  await page.keyboard.type(" and on");
  await expect(source(page)).toHaveValue(`${alphaText(n.beta)}\n\nTyped before the split, and after and on`);
});

test("workspace split #9: at 1024 px and on a phone nothing of the split shows, and a click on the text starts nothing @workspace @phone", async ({ page, aliceApi, folder }, testInfo) => {
  if (!onPhone(testInfo)) await page.setViewportSize({ width: 1024, height: 800 });
  else await dismissInstallHint(page);
  const n = await seed(aliceApi, folder);
  await page.goto(splitUrl(n.alphaPath, n.betaPath));
  await expect(page.getByRole("heading", { name: "Alpha" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Beta" })).toBeHidden();
  await expect(page.getByRole("button", { name: "Split view" })).toHaveCount(0);
  await expect(page.getByRole("region", { name: /^(Left|Right) pane/ })).toHaveCount(0);
  await expect(page.getByRole("img", { name: /^Live preview of / })).toHaveCount(0);

  await page.getByText("The quick brown fox").click();
  await page.waitForTimeout(300);
  await expect(source(page)).toHaveCount(0);
});
