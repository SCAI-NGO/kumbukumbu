// SPDX-License-Identifier: AGPL-3.0-or-later
// Embeds, block references and unlinked mentions (the mermaid report's hand-test checklist 1, 7-10, 12, 15-20; spec
// 2026-11-12-mermaid-and-parity-design.md §3-§5).
import type { Page } from "@playwright/test";
import { shot } from "../support/test";
import {
  article,
  cleanFolder,
  controllerLine,
  diaryRow,
  expect,
  lit,
  mentions,
  noteUrl,
  openNote,
  seedExample,
  test,
} from "./helpers";

test.afterEach(async ({ aliceApi, folder }) => {
  await cleanFolder(aliceApi, folder);
});

const embeddedHeader = "--- embedded notes: shown for reading, not part of this note's text (which ends above) ---";

/** The console's lines, without blank ones. */
const lines = (text: string) => text.split("\n").map((l) => l.trimEnd()).filter(Boolean);

/** From Tasks, the link to Plan's block. */
async function followBlockLink(page: Page, tasks: string, name = "Plan › ^controller") {
  await openNote(page, tasks);
  await article(page).getByRole("link", { name }).click();
}

test("mermaid #1: the embed frame shows Ruling › Decision @embeds", async ({ page, seedAs, folder }, testInfo) => {
  const { plan, ruling } = await seedExample(seedAs, folder);
  await openNote(page, plan);

  const frame = page.getByRole("region", { name: "Embedded: Ruling › Decision" });
  await expect(frame).toBeVisible();
  await expect(frame.getByRole("link", { name: "Ruling › Decision (open the note)" })).toHaveAttribute("href", noteUrl(ruling));
  await expect(frame.getByRole("heading", { name: "Decision" })).toBeVisible();
  await expect(frame.getByText("The board approves a new controller for the pump.")).toBeVisible();
  // Only the section: Ruling's title stays out.
  await expect(frame.getByRole("heading", { name: "Ruling" })).toHaveCount(0);
  await frame.scrollIntoViewIfNeeded();
  await shot(page, testInfo, "embeds");
});

test("mermaid #7: a block link opens the note at the block, highlighted, without a hash @blockrefs", async ({ page, seedAs, folder }, testInfo) => {
  const { tasks } = await seedExample(seedAs, folder);
  await followBlockLink(page, tasks);

  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Pump/Plan\\.md$`));
  expect(new URL(page.url()).hash, "no hash in the address").toBe("");
  const line = controllerLine(page);
  await expect(line).toHaveClass(lit);
  await expect(line).toBeInViewport();
  // A fade while lit, then gone: briefly.
  expect(await line.evaluate((e) => e.getAnimations().length), "the highlight fades").toBeGreaterThan(0);
  await shot(page, testInfo, "blockrefs");
  await expect(line).not.toHaveClass(lit);
});

test("mermaid #8: a missing block opens the note at the top with a sentence @blockrefs", async ({ page, aliceApi, seedAs, folder }) => {
  const { tasks } = await seedExample(seedAs, folder);
  await aliceApi.putNote(tasks, "- see [[Plan#^contoller]]\n");
  await followBlockLink(page, tasks, "Plan › ^contoller");

  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Pump/Plan\\.md$`));
  await expect(page.getByRole("status").filter({ hasText: "There is no block" })).toHaveText("There is no block ^contoller in this note.");
  await expect(page.getByRole("heading", { name: "Plan", level: 1 })).toBeInViewport();
  await expect(controllerLine(page)).not.toHaveClass(lit);
});

test("mermaid #9: Unlinked mentions lists the diary line with plan marked @mentions", async ({ page, seedAs, folder }, testInfo) => {
  const { plan } = await seedExample(seedAs, folder);
  await openNote(page, plan);

  const linked = page.getByRole("region", { name: "Linked from" });
  await expect(linked.getByRole("heading", { name: "Linked from · 1" })).toBeVisible();
  await expect(linked.getByRole("button", { name: new RegExp(`^Tasks\\s*Work / ${folder} / Pump$`) })).toBeVisible();

  // Closed, no count, and below Linked from.
  const { section, button } = mentions(page);
  await expect(button).toHaveAttribute("aria-expanded", "false");
  await expect(button).toHaveText("Unlinked mentions");
  await expect(section.getByRole("listitem")).toHaveCount(0);
  expect((await section.boundingBox())!.y, "below Linked from").toBeGreaterThan((await linked.boundingBox())!.y);

  await button.click();
  await expect(button).toHaveAttribute("aria-expanded", "true");
  const row = diaryRow(section, folder);
  await expect(row).toHaveCount(1);
  await expect(row).toContainText("2026-11-12");
  await expect(row).toContainText("Talked about the plan with Bob.");
  await expect(row.locator("mark")).toHaveText(["plan"]);
  await expect(row).toContainText(`Work / ${folder} / Diary`);
  // Nothing else of this folder: Tasks links Plan, and its link's text doesn't count.
  await expect(section.getByRole("listitem").filter({ hasText: `Work / ${folder} /` })).toHaveCount(1);
  await row.scrollIntoViewIfNeeded();
  await shot(page, testInfo, "mentions");

  await row.click();
  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Diary/2026-11-12\\.md$`));
  const said = article(page).getByText("Talked about the plan with Bob.");
  await expect(said).toHaveClass(lit);
  await expect(said).toBeInViewport();
  await expect(said).not.toHaveClass(lit);
});

test("mermaid #10: Write mode shows the block id and the source @blockrefs", async ({ page, aliceApi, seedAs, folder }) => {
  const { plan } = await seedExample(seedAs, folder);
  const stored = (await aliceApi.getNote(plan)).content;
  await openNote(page, plan);

  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  await expect(source).toHaveValue(stored);
  expect(stored).toContain("The pump needs a new controller. ^controller\n");
  expect(stored).toContain("```mermaid\nflowchart LR\n  Sensor --> Controller --> Pump\n```\n");
  await page.getByRole("button", { name: "Done" }).click();
  await expect(page.getByRole("button", { name: "Done" })).toHaveCount(0);
  expect((await aliceApi.getNote(plan)).content, "unchanged").toBe(stored);
});

test("mermaid #12: with reduced motion the block gets a static outline @blockrefs", async ({ page, seedAs, folder }) => {
  const { tasks } = await seedExample(seedAs, folder);
  await page.emulateMedia({ reducedMotion: "reduce" });
  await followBlockLink(page, tasks);

  await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Pump/Plan\\.md$`));
  const line = controllerLine(page);
  await expect(line).toHaveClass(lit);
  // Read in one go while it is lit: an outline, and no animation running.
  const look = await line.evaluate((e) => ({
    lit: e.classList.contains("k-flash"),
    outline: getComputedStyle(e).outlineStyle,
    width: getComputedStyle(e).outlineWidth,
    animations: e.getAnimations().length,
  }));
  expect(look.lit, "still lit while read").toBe(true);
  expect(look.outline).toBe("solid");
  expect(look.width).not.toBe("0px");
  expect(look.animations, "no fade").toBe(0);
  await expect(line).not.toHaveClass(lit);
  expect(await line.evaluate((e) => getComputedStyle(e).outlineStyle), "the outline goes with it").toBe("none");
});

test("mermaid #15: cat appends the embedded notes after the note as stored @embeds", async ({ aliceApi, seedAs, folder }) => {
  const { plan, ruling } = await seedExample(seedAs, folder);
  const stored = await aliceApi.getNote(plan);

  const view = await aliceApi.json<{ parts: { text: string | null }[]; isError: boolean; version?: { path: string; token: string } }>("POST", "/api/console", {
    call: "sh",
    command: `cat ${plan}`,
  });
  const text = view.parts.map((p) => p.text ?? "").join("");
  expect(view.isError, text).toBe(false);
  // The note byte for byte, a blank line, the header, then each embed's line and its text.
  expect(text.startsWith(`${stored.content}\n${embeddedHeader}\n`), "the note as stored, a blank line, the header").toBe(true);
  expect(text.slice(stored.content.length).trimEnd()).toBe(
    `\n${embeddedHeader}\n[line 10: ![[Ruling#Decision]] → ${ruling}#Decision]\n## Decision\n\nThe board approves a new controller for the pump.`,
  );
  expect(view.version, "the version token is Plan's").toEqual({ path: plan, token: stored.version });
});

test("mermaid #16: cat | wc -l counts the note only @embeds", async ({ aliceApi, seedAs, folder }) => {
  const { plan } = await seedExample(seedAs, folder);
  const stored = (await aliceApi.getNote(plan)).content;
  const counted = await aliceApi.console(`cat ${plan} | wc -l`);
  expect(counted.isError, counted.text).toBe(false);
  expect(stored.split("\n").length - 1, "Plan has ten lines").toBe(10);
  expect(counted.text.trim()).toBe("10");
});

test("mermaid #17: cat of a block prints it with its marker; a wrong id is an error @blockrefs", async ({ aliceApi, seedAs, folder }) => {
  const { plan } = await seedExample(seedAs, folder);
  const block = await aliceApi.console(`cat "${plan}#^controller"`);
  expect(block.isError, block.text).toBe(false);
  expect(block.text.trimEnd()).toBe("The pump needs a new controller. ^controller\nBREAK");

  const wrong = await aliceApi.console(`cat "${plan}#^contoller"`);
  expect(wrong.isError).toBe(true);
  expect(wrong.text.trimEnd()).toMatch(new RegExp(`^cat: ${plan.replace(/[.^]/g, "\\$&")}#\\^contoller: no such block \\(.+\\)$`));
  expect(wrong.text.trimEnd()).toBe(`cat: ${plan}#^contoller: no such block (a block id is written as ^contoller at the end of a paragraph, item or heading)`);
});

test("mermaid #18: links resolves the block link and reports a missing block among the broken @blockrefs", async ({ aliceApi, seedAs, folder }) => {
  const { plan, tasks } = await seedExample(seedAs, folder);
  const found = await aliceApi.console(`links ${tasks}`);
  expect(found.isError, found.text).toBe(false);
  expect(lines(found.text)).toEqual([`1: [[Plan#^controller]] → ${plan}#^controller`]);
  // Before the typo the folder has no broken link.
  const clean = await aliceApi.console(`links /Work/${folder}`);
  expect(lines(clean.text).filter((l) => l.startsWith(tasks)), clean.text).toEqual([]);

  await aliceApi.putNote(tasks, "- see [[Plan#^contoller]]\n");
  const missing = await aliceApi.console(`links ${tasks}`);
  expect(missing.isError, missing.text).toBe(false);
  expect(lines(missing.text)).toEqual([`1: [[Plan#^contoller]] → not found: no block ^contoller in ${plan}`]);
  const broken = await aliceApi.console(`links /Work/${folder}`);
  expect(broken.isError, broken.text).toBe(false);
  expect(lines(broken.text)).toContain(`${tasks}:1: [[Plan#^contoller]] → not found: no block ^contoller in ${plan}`);
});

test("mermaid #19: backlinks --unlinked lists the diary line, backlinks as before @mentions", async ({ aliceApi, seedAs, folder }) => {
  const { plan, tasks, diary } = await seedExample(seedAs, folder);
  const mine = (text: string) => lines(text).filter((l) => l.startsWith(`/Work/${folder}/`));

  const unlinked = await aliceApi.console(`backlinks --unlinked ${plan}`);
  expect(unlinked.isError, unlinked.text).toBe(false);
  // Vault-wide: this folder's line, as path:line:text.
  expect(mine(unlinked.text)).toEqual([`${diary}:1:Talked about the plan with Bob.`]);

  const linked = await aliceApi.console(`backlinks ${plan}`);
  expect(linked.isError, linked.text).toBe(false);
  expect(mine(linked.text)).toEqual([`${tasks}:1:- see [[Plan#^controller]]`]);
});

test("mermaid #20: mv keeps the block link @blockrefs", async ({ aliceApi, mcp, seedAs, folder }) => {
  const { plan, tasks } = await seedExample(seedAs, folder);
  const moved = `/Work/${folder}/Old/Plan.md`;
  try {
    const away = await mcp.sh(`mv ${plan} ${moved}`);
    expect(away.isError, away.text).toBe(false);
    const after = (await aliceApi.getNote(tasks)).content;
    // The path form is rewritten (Plan isn't beside Tasks any more), the block id stays.
    expect(after).toMatch(/\[\[[^\]]*Old\/Plan#\^controller\]\]/);
    const shown = await aliceApi.console(`cat ${tasks}`);
    expect(shown.text).toContain("#^controller]]");
    const resolved = await aliceApi.console(`links ${tasks}`);
    expect(lines(resolved.text)).toEqual([expect.stringMatching(new RegExp(`^1: \\[\\[[^\\]]*#\\^controller\\]\\] → ${moved.replace(/[.^]/g, "\\$&")}#\\^controller$`))]);
  } finally {
    const back = await mcp.sh(`mv ${moved} ${plan}`);
    expect(back.isError, back.text).toBe(false);
  }
  const home = await aliceApi.console(`links ${tasks}`);
  expect(lines(home.text), "back in Pump, the link still leads to the block").toEqual([expect.stringMatching(new RegExp(`#\\^controller\\]\\] → ${plan.replace(/[.^]/g, "\\$&")}#\\^controller$`))]);
});
