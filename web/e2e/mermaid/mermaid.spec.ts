// SPDX-License-Identifier: AGPL-3.0-or-later
// Mermaid diagrams (the mermaid report's hand-test checklist 1-6, 11, 13, 14; spec 2026-11-12-mermaid-and-parity-design.md §2).
import type { BrowserContext, Page } from "@playwright/test";
import { Api } from "../support/api";
import { reserveReaderRead } from "../support/session";
import { onPhone, shot } from "../support/test";
import {
  article,
  cleanFolder,
  controllerLine,
  diagram,
  diaryRow,
  expect,
  expectLoaded,
  filler,
  lightFill,
  lit,
  mentions,
  openNote,
  pageScrollsSideways,
  publish,
  seedExample,
  svgOf,
  test,
  withdraw,
} from "./helpers";

// No failure screenshots in this file: #14's reader page shows the AI link and its token (a describe of its own
// can't turn them off).
test.use({ screenshot: "off" });

test.afterEach(async ({ aliceApi, folder }) => {
  await cleanFolder(aliceApi, folder);
});

/** Opens the reader page at the publish link; wrapped, as a failed navigation's error names the URL and its secret. */
async function openReader(reader: Page, link: string) {
  await reserveReaderRead();
  await reader.goto(link).catch(() => {
    throw new Error("the reader page did not open");
  });
  await expect(reader.getByRole("heading", { name: "Plan", level: 1 })).toBeVisible();
}

/** Plan's Mermaid source, as stored. */
const planSource = "```mermaid\nflowchart LR\n  Sensor --> Controller --> Pump\n```";

test("mermaid #1: the flowchart is a picture, the block id hidden @mermaid", async ({ page, seedAs, folder }, testInfo) => {
  const { plan } = await seedExample(seedAs, folder);
  await openNote(page, plan);

  const image = diagram(article(page));
  await expectLoaded(image);
  const svg = await svgOf(image);
  for (const node of ["Sensor", "Controller", "Pump"]) expect(svg, `the diagram draws ${node}`).toContain(node);
  expect(svg, "in the light theme's colours (Mermaid's default)").toMatch(lightFill);

  await expect(controllerLine(page)).toBeVisible();
  await expect(article(page).getByText("^controller")).toHaveCount(0);
  await shot(page, testInfo, "mermaid");
});

test("mermaid #2: Show source toggles the code with aria-expanded @mermaid", async ({ page, seedAs, folder }) => {
  const { plan } = await seedExample(seedAs, folder);
  await openNote(page, plan);
  await expectLoaded(diagram(article(page)));

  const code = article(page).getByText("Sensor --> Controller --> Pump");
  const show = article(page).getByRole("button", { name: "Show source" });
  await expect(code).toBeHidden();
  await expect(show).toHaveAttribute("aria-expanded", "false");

  await show.click();
  const hide = article(page).getByRole("button", { name: "Hide source" });
  await expect(hide).toHaveAttribute("aria-expanded", "true");
  await expect(code).toBeVisible();
  await expect(article(page).locator("pre").filter({ hasText: "Sensor --> Controller --> Pump" })).toHaveText(/^flowchart LR\n\s+Sensor --> Controller --> Pump\s*$/);

  await hide.click();
  await expect(show).toHaveAttribute("aria-expanded", "false");
  await expect(code).toBeHidden();
});

test("mermaid #3: the diagram is an img from a blob, no svg in the page @mermaid", async ({ page, seedAs, folder }) => {
  const { plan } = await seedExample(seedAs, folder);
  await openNote(page, plan);

  const image = diagram(article(page));
  await expectLoaded(image);
  expect(await image.evaluate((i) => i.tagName)).toBe("IMG");
  await expect(image).toHaveAttribute("src", /^blob:/);
  await expect(image).toHaveAttribute("alt", "Diagram (flowchart)");
  // No SVG of the diagram anywhere in the document, and nothing Mermaid measured in left behind.
  await expect(page.locator("svg", { hasText: "Sensor" })).toHaveCount(0);
  expect(await page.evaluate(() => document.querySelectorAll('[id^="kumbu-mermaid-"], [id^="dkumbu-mermaid-"]').length), "no Mermaid element in the page").toBe(0);
});

test("mermaid #4: a scheme switch redraws the diagram @mermaid", async ({ page, seedAs, folder }) => {
  const { plan } = await seedExample(seedAs, folder);
  await openNote(page, plan);
  const image = diagram(article(page));
  await expectLoaded(image);
  const before = await image.getAttribute("src");
  expect(await svgOf(image), "light first").toMatch(lightFill);
  // A marker on the page itself: it is gone if the page is ever reloaded.
  await page.evaluate(() => ((window as unknown as { __mark: number }).__mark = 1));

  // The system turns dark with the note open: the saved session chose no scheme, so the app follows it (the
  // light default, Catppuccin Latte, becomes Kumbu's dark one) and the mounted diagram is drawn again.
  await expect(page.locator("html")).toHaveAttribute("data-scheme", "catppuccin-latte");
  await page.emulateMedia({ colorScheme: "dark" });
  await expect(page.locator("html")).toHaveAttribute("data-scheme", "kumbu");

  await expect(image).not.toHaveAttribute("src", before!);
  await expectLoaded(image);
  const svg = await svgOf(image);
  expect(svg, "drawn again with Mermaid's dark theme").not.toMatch(lightFill);
  expect(svg).toContain("Controller");
  expect(await page.evaluate(() => (window as unknown as { __mark?: number }).__mark), "no reload").toBe(1);
});

test("mermaid #5: a broken diagram says it can't be drawn above its code @mermaid", async ({ page, aliceApi, seedAs, folder }) => {
  const { plan } = await seedExample(seedAs, folder);
  await openNote(page, plan);
  await expectLoaded(diagram(article(page)));

  // Write mode: a second block after what is there, then Done (which saves).
  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  await source.fill(`${await source.inputValue()}\n\`\`\`mermaid\nflowchart LR\n  A -->\n\`\`\`\n`);
  await page.getByRole("button", { name: "Done" }).click();
  await expect(page.getByRole("button", { name: "Done" })).toHaveCount(0);
  await expect.poll(async () => (await aliceApi.getNote(plan)).content).toContain("  A -->\n```");

  const said = article(page).getByRole("note").filter({ hasText: "This diagram can't be drawn" });
  await expect(said).toHaveText(/^This diagram can't be drawn: \S.*$/);
  // The code block right below the line, holding the broken source.
  const code = said.locator("xpath=following-sibling::pre[1]");
  await expect(code).toBeVisible();
  await expect(code).toHaveText(/^flowchart LR\n\s+A -->\s*$/);
  // The rest of the note as before.
  await expectLoaded(diagram(article(page)));
  await expect(controllerLine(page)).toBeVisible();
  await expect(page.getByRole("region", { name: "Embedded: Ruling › Decision" })).toBeVisible();
});

test("mermaid #6: images in diagrams aren't loaded @mermaid", async ({ page, aliceApi, seedAs, folder }) => {
  const { plan } = await seedExample(seedAs, folder);
  const stored = (await aliceApi.getNote(plan)).content;
  await aliceApi.putNote(plan, `${stored}\n\`\`\`mermaid\nstateDiagram-v2\n  [*] --> S\n  style S fill:url(/x)\n\`\`\`\n`);
  const asked: string[] = [];
  page.context().on("request", (r) => {
    if (new URL(r.url()).pathname === "/x") asked.push(r.url());
  });
  await openNote(page, plan);

  const said = article(page).getByRole("note").filter({ hasText: "This diagram can't be drawn" });
  await expect(said).toHaveText("This diagram can't be drawn: images in diagrams aren't loaded.");
  await expect(said.locator("xpath=following-sibling::pre[1]")).toHaveText(/^stateDiagram-v2\n\s+\[\*\] --> S\n\s+style S fill:url\(\/x\)\s*$/);
  // Plan's own diagram still draws (after the refused one, so Mermaid has had its turn), and nothing asked for /x.
  await expectLoaded(diagram(article(page)));
  expect(asked, "no request to /x").toEqual([]);
});

test("mermaid #11: at 375 px the diagram fits, mentions ≥ 48 px, a wide diagram scrolls in its frame @mermaid @phone", async ({ page, aliceApi, seedAs, folder }, testInfo) => {
  test.skip(!onPhone(testInfo), "The phone's step (375 px); the desktop has its own steps 1, 7 and 9.");
  const { plan, tasks, wide } = await seedExample(seedAs, folder);

  // 1: the diagram within the column, the page not scrolling sideways.
  await openNote(page, plan);
  const image = diagram(article(page));
  await expectLoaded(image);
  const width = await article(page).evaluate((a) => a.clientWidth);
  expect((await image.boundingBox())!.width, "the diagram fits the column").toBeLessThanOrEqual(width);
  expect(await pageScrollsSideways(page), "no sideways scroll").toBe(false);
  expect(await svgOf(image), "the phone's dark scheme draws a dark diagram").not.toMatch(lightFill);
  await expect(controllerLine(page)).toBeVisible();
  await expect(article(page).getByText("^controller")).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Embedded: Ruling › Decision" })).toBeVisible();
  await shot(page, testInfo, "mermaid");

  // 7: the block link from Tasks, to the block below filler paragraphs: the jump has to scroll to it.
  const stored = (await aliceApi.getNote(plan)).content;
  await aliceApi.putNote(plan, stored.replace("# Plan\n\n", `# Plan\n\n${filler}\n\n`));
  await openNote(page, tasks);
  await article(page).getByRole("link", { name: "Plan › ^controller" }).tap();
  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Pump/Plan\\.md$`));
  expect(new URL(page.url()).hash, "no hash in the address").toBe("");
  await expect(controllerLine(page)).toHaveClass(lit);
  await expect(controllerLine(page)).toBeInViewport();
  await expect(article(page).getByText("Filler paragraph 1 about", { exact: false }), "the jump scrolled").not.toBeInViewport();

  // 9: the mentions button, tall enough and reachable by keyboard; opened, the diary's line.
  const { section, button } = mentions(page);
  await expect(button).toHaveAttribute("aria-expanded", "false");
  await expect(button).toHaveText("Unlinked mentions");
  await expect(section.getByRole("listitem")).toHaveCount(0);
  expect((await button.boundingBox())!.height, "the mentions button is at least 48 px high").toBeGreaterThanOrEqual(48);
  // Reachable by keyboard: Tab from the button before it (Linked from's Tasks) gets there.
  await page.getByRole("region", { name: "Linked from" }).getByRole("button").last().focus();
  let reached = false;
  for (let i = 0; i < 5 && !reached; i++) {
    await page.keyboard.press("Tab");
    reached = await button.evaluate((b) => b === document.activeElement);
  }
  expect(reached, "Tab reaches the mentions button").toBe(true);
  await expect(button).toBeFocused();
  await button.tap();
  await expect(button).toHaveAttribute("aria-expanded", "true");
  const row = diaryRow(section, folder);
  await expect(row).toContainText("Talked about the plan with Bob.");
  await expect(row.locator("mark")).toHaveText(["plan"]);
  await expect(row).toContainText(`Work / ${folder} / Diary`);
  expect(await pageScrollsSideways(page), "no sideways scroll").toBe(false);
  await row.tap();
  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Diary/2026-11-12\\.md$`));
  await expect(article(page).getByText("Talked about the plan with Bob.")).toHaveClass(lit);

  // A wide diagram: Full size, and its image scrolls within its frame, not the page.
  await openNote(page, wide);
  const wideImage = article(page).getByRole("img", { name: "Diagram (flowchart)" });
  await expectLoaded(wideImage);
  const fullSize = article(page).getByRole("button", { name: "Full size" });
  await expect(fullSize).toBeVisible();
  await fullSize.tap();
  await expect(article(page).getByRole("button", { name: "Fit" })).toHaveAttribute("aria-pressed", "true");
  const frame = wideImage.locator("xpath=..");
  await expect.poll(() => frame.evaluate((f) => f.scrollWidth > f.clientWidth), "the image is wider than its frame").toBe(true);
  expect(await frame.evaluate((f) => getComputedStyle(f).overflowX)).toBe("auto");
  const scrolled = await frame.evaluate((f) => {
    f.scrollLeft = 120;
    return f.scrollLeft;
  });
  expect(scrolled, "the frame scrolls").toBeGreaterThan(0);
  expect(await pageScrollsSideways(page), "the page doesn't scroll sideways").toBe(false);
});

test("mermaid #13: the reader page shows the diagram as code, no block id, no CSP violation @mermaid @serial", async ({ page, browser, aliceApi, seedAs, folder, projectContextOptions }) => {
  const { plan } = await seedExample(seedAs, folder);
  let context: BrowserContext | undefined;
  try {
    await openNote(page, plan);
    const link = await publish(page);

    context = await browser.newContext(projectContextOptions);
    const reader = await context.newPage();
    const messages: string[] = [];
    reader.on("console", (m) => messages.push(m.text()));
    // Violations the console may not name.
    await reader.addInitScript(() => {
      const w = window as unknown as { __violations: string[] };
      w.__violations = [];
      document.addEventListener("securitypolicyviolation", (e) => w.__violations.push(`${e.violatedDirective} ${e.blockedURI}`));
    });
    await openReader(reader, link);

    // As code (spec §2.3, the report's deviations): the source in a code block, no picture.
    const code = reader.locator("pre").filter({ hasText: "Sensor --> Controller --> Pump" });
    await expect(code).toBeVisible();
    await expect(code).toHaveText(/^flowchart LR\n\s+Sensor --> Controller --> Pump\s*$/);
    await expect(reader.getByRole("img")).toHaveCount(0);
    await expect(reader.getByText("The pump needs a new controller.", { exact: true })).toBeVisible();
    await expect(reader.getByText("^controller")).toHaveCount(0);

    expect(messages.filter((m) => m.includes("Content Security Policy")), "no CSP violation in the console").toEqual([]);
    expect(await reader.evaluate(() => (window as unknown as { __violations: string[] }).__violations), "no CSP violation").toEqual([]);
  } finally {
    await context?.close();
    await withdraw(aliceApi, plan);
  }
});

test("mermaid #14: Copy as Markdown and the AI link keep the source and the block id @mermaid @serial", async ({ page, browser, aliceApi, seedAs, folder, projectContextOptions }) => {
  const { plan } = await seedExample(seedAs, folder);
  let context: BrowserContext | undefined;
  const anonymous = await Api.anonymous();
  try {
    await openNote(page, plan);
    const link = await publish(page);

    context = await browser.newContext({ ...projectContextOptions, permissions: ["clipboard-read", "clipboard-write"] });
    const reader = await context.newPage();
    await openReader(reader, link);

    await reader.getByRole("button", { name: "Copy as Markdown" }).click();
    await expect(reader.getByText("Copied", { exact: true })).toBeVisible();
    const copied = await reader.evaluate(() => navigator.clipboard.readText());
    expect(copied.includes(planSource), "Copy as Markdown holds the Mermaid source as written").toBe(true);
    expect(copied.includes("The pump needs a new controller. ^controller"), "Copy as Markdown keeps ^controller").toBe(true);

    await reserveReaderRead();
    await reader.getByRole("button", { name: "Link for your AI" }).click();
    const field = reader.getByRole("textbox", { name: "Link for your AI" });
    await expect(field).toHaveValue(/\S/);
    const aiLink = await field.inputValue();
    expect(/^http:\/\/localhost:8080\/s\/t\/[A-Za-z0-9_-]+$/.test(aiLink), "the AI link's shape").toBe(true);
    // Wrapped: a failed request's error names the URL, its token with it.
    await reserveReaderRead();
    const answer = await anonymous.send("GET", aiLink).catch(() => {
      throw new Error("the AI link could not be fetched");
    });
    expect(answer.status(), "the AI link answers").toBe(200);
    const body = await answer.text();
    expect(body.includes(planSource), "the AI link holds the Mermaid source as written").toBe(true);
    expect(body.includes("The pump needs a new controller. ^controller"), "the AI link keeps ^controller").toBe(true);
  } finally {
    await context?.close();
    await anonymous.dispose();
    await withdraw(aliceApi, plan);
  }
});
