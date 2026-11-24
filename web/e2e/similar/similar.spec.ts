// SPDX-License-Identifier: AGPL-3.0-or-later
// "Similar notes" in the Links tab and the graph's suggestion card (docs/superpowers/specs/2026-11-22-link-prediction-
// design.md, #306). The API's answers without a page are web/e2e/mcp/similar-api.spec.ts.
//
// Each test in a vault of its own (`Similar-<random>`, plan review point 7): the suggestions are vault-wide, Add link
// and Not related change the notes, and a copy of a fixture in another vault of the same test run scores 1.00. Measured
// with arctic-m on the meaning fixtures: the diving notes 0.53–0.63 with each other, every other pair below 0.33, so a
// diving note's rows are the other two diving notes, and the cake has none.
//
// Promises in scope, and the test that covers each:
//  1. The section "Similar notes" below "Unlinked mentions", closed and not asked until opened, closed again on a
//     change of note (§3.1) → "similar #1" (desktop and phone)
//  2. A row: the title, the first line of the closest section dimmed below it, the folder; no score; a tap opens the
//     note at that line (§3.2) → "similar #1"; a short title readable in the side panel's row → "similar #10"
//  3. Up to five, one per note, same vault, not the archive, only notes, not linked either way, the floor (§3.5)
//     → "similar #1" (the two diving notes, nothing else of the vault), "similar #6" (the cake: none above the floor);
//     the rest without a page: similar-api #1–#5
//  4. Add link: two 44 px buttons labelled for screen readers; `- [[Other]]` in `## Related` at the end, read back; the
//     row leaves the list (§3.3, §3.4) → "similar #2"; the Related section's cases: similar-api #6–#8
//  5. Not related: `not-related:` written, the row "Hidden · <title>" with Undo in place, Undo takes the entry out, hidden on the
//     other note too (§3.6) → "similar #3"; the words → "similar #4"; in place → "similar #11"; for agents: similar-api #9
//  6. Only where the caller may change the note: a read member sees the rows without the answers (§3.3)
//     → "similar #5"; the API's refusal: similar-api #10
//  7. While the note is being written, a tap saves and leaves Write mode first (§3.3) → "similar #7"
//  8. The states (§3.7): nothing above the floor, "No similar notes that aren't linked." → "similar #6"; an error,
//     with Try again → "similar #6" (the request made to fail in the page); search by meaning off → "similar #9" (no
//     `@search`: it runs in the phase with search off, and fails on a stack with it on)
//  9. The graph's suggested links and their card with the same two answers, Undo after Not related (§2, §5)
//     → "similar #8"
// Left out: "preparing" (the model is loaded before the tests run, plan review point 5); "this note not indexed yet"
// in the page (a note is embedded within about a second of its write here, so the page can't be timed to see it; the
// API's answer is similar-api #11, its shape only: the state itself rests on Vitest and xUnit); offline ("needs a connection"): Task 7, with the offline helpers.
import type { Locator, Page } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { secrets } from "../support/env";
import { firstIndex, nextIndex, similarNotes, waitSimilarReady } from "../support/search";
import { reservePassword } from "../support/session";
import { shareWithBob, unshare } from "../support/share";
import { expect, shot, shotsDir070, test as base } from "../support/test";

/** A vault of Alice's for this test alone, with the meaning fixtures; deleted after it (its password check counted). */
const test = base.extend<{ vault: string }>({
  vault: [
    async ({ aliceApi, seedAs }, use) => {
      const vault = `Similar-${randomBytes(3).toString("hex")}`;
      await aliceApi.json("POST", "/api/vaults", { name: vault });
      try {
        await seedAs("tags/meaning", `/${vault}`);
        await use(vault);
      } finally {
        await reservePassword("alice");
        await aliceApi.json("POST", `/api/vaults/${encodeURIComponent(vault)}/delete`, { password: secrets().passwords.alice, confirm: vault });
      }
    },
    { timeout: 240_000 },
  ],
});

test.use({ shotsDir: shotsDir070 });

const noteUrl = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;

async function openNote(page: Page, path: string) {
  await page.goto(noteUrl(path));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
  const notNow = page.getByRole("button", { name: "Not now" });
  if (await notNow.isVisible()) await notNow.click();
}

const at = (vault: string) => ({
  decompression: `/${vault}/Diving/Decompression.md`,
  tables: `/${vault}/Diving/Dive tables.md`,
  computer: `/${vault}/Diving/Dive computer.md`,
  cake: `/${vault}/Baking/Chocolate cake.md`,
});

/** The section and its disclosure button. */
function similar(page: Page) {
  const section = page.getByRole("region", { name: "Similar notes" });
  return { section, button: section.getByRole("button", { name: "Similar notes", exact: true }), rows: section.getByRole("listitem") };
}

/** Opens the section, once the vault's suggestions are ready. */
async function openSimilar(page: Page) {
  const s = similar(page);
  await s.button.click();
  await expect(s.button).toHaveAttribute("aria-expanded", "true");
  return s;
}

const row = (rows: Locator, title: string) => rows.filter({ has: rows.page().getByRole("button", { name: new RegExp(`^${title}\\b`) }) });

test("similar #1: closed and not asked until opened; the two diving notes, each with its line and folder, no score; a tap opens it at that line @similar @search @phone", async ({ page, aliceApi, vault }, testInfo) => {
  test.setTimeout(300_000);
  const n = at(vault);
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);
  const expected = await similarNotes(aliceApi, n.decompression);
  expect(expected.notes.map((s) => s.path).sort(), "the API's answer").toEqual([n.computer, n.tables].sort());

  const asked: string[] = [];
  page.on("request", (r) => {
    if (new URL(r.url()).pathname.startsWith("/api/similar/")) asked.push(r.url());
  });
  await openNote(page, n.decompression);
  const { section, button, rows } = similar(page);
  await expect(button).toHaveAttribute("aria-expanded", "false");
  await expect(button).toHaveText("Similar notes");
  await expect(rows).toHaveCount(0);
  // Below Unlinked mentions.
  const mentions = page.getByRole("region", { name: "Unlinked mentions" });
  await expect(mentions).toBeVisible();
  expect((await section.boundingBox())!.y, "below Unlinked mentions").toBeGreaterThan((await mentions.boundingBox())!.y);
  // Once the page's requests have settled, none of them was the section's.
  await page.waitForLoadState("networkidle");
  expect(asked, "nothing asked while closed").toEqual([]);

  await openSimilar(page);
  await expect(rows).toHaveCount(2);
  expect(asked.length, "asked once opened").toBeGreaterThan(0);
  // Best first, as the server ranks them; each a mention row: title, its line, the folder, and nothing else.
  for (const [i, s] of expected.notes.entries()) {
    const title = s.path.slice(s.path.lastIndexOf("/") + 1).replace(/\.md$/, "");
    const open = rows.nth(i).getByRole("button", { name: new RegExp(`^${title}`) });
    await expect(open).toHaveText(`${title}${s.text}${vault} / Diving`);
    expect(await open.innerText(), "no score").not.toMatch(/\d[.,]\d/);
  }
  await section.scrollIntoViewIfNeeded();
  await shot(page, testInfo, "similar");

  const first = expected.notes[0];
  await rows.first().getByRole("button").first().click();
  await expect(page).toHaveURL(new RegExp(`${noteUrl(first.path).replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`));
  // The flash lasts 1.6 s (NoteView.tsx): its line read at once; the block NoteView aims at is the greatest line at
  // or above the suggestion's, outside embeds.
  await expect(page.locator("article .k-flash")).toHaveCount(1);
  const { lit, aimed } = await page.locator("article").evaluate((article, line) => {
    const lines = [...article.querySelectorAll<HTMLElement>("[data-line]")].filter((e) => !e.closest(".k-embed")).map((e) => Number(e.dataset.line));
    return { lit: Number(article.querySelector<HTMLElement>(".k-flash")?.dataset.line), aimed: Math.max(...lines.filter((l) => l <= line)) };
  }, first.line);
  expect(lit, `the block at line ${first.line} lit`).toBe(aimed);
  await expect(page.locator(`article [data-line="${aimed}"]`).first()).toBeInViewport();
  // Another note: the section closed again.
  await expect(similar(page).button).toHaveAttribute("aria-expanded", "false");
  await expect(similar(page).rows).toHaveCount(0);
});

test("similar #2: Add link writes - [[Dive tables]] under Related at the end, and the row goes @similar @search", async ({ page, aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  const before = (await aliceApi.getNote(n.decompression)).content;
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);
  await openNote(page, n.decompression);
  const { rows } = await openSimilar(page);
  await expect(rows).toHaveCount(2);

  const add = page.getByRole("button", { name: "Add link to Dive tables", exact: true });
  const box = (await add.boundingBox())!;
  expect([box.width, box.height], "a 44 px target").toEqual([44, 44]);
  await add.click();
  await expect(rows).toHaveCount(1);
  await expect(row(rows, "Dive computer")).toHaveCount(1);
  await expect(page.locator("article").getByRole("heading", { name: "Related", level: 2 })).toBeVisible();
  expect((await aliceApi.getNote(n.decompression)).content).toBe(`${before.trimEnd()}\n\n## Related\n\n- [[Dive tables]]\n`);
});

test("similar #3: Not related hides the row with Undo and keeps the path in not-related; Undo takes it back; the other note hides it too @similar @search", async ({ page, aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  const before = (await aliceApi.getNote(n.decompression)).content;
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);
  await openNote(page, n.decompression);
  const { rows } = await openSimilar(page);
  await expect(rows).toHaveCount(2);

  await page.getByRole("button", { name: "Not related: Dive tables", exact: true }).click();
  const hidden = rows.filter({ hasText: /^Hidden/ });
  await expect(hidden).toHaveCount(1);
  await expect(hidden.getByRole("button", { name: "Undo" })).toBeVisible();
  await expect(rows, "still two rows: the answer in place").toHaveCount(2);
  const written = (await aliceApi.getNote(n.decompression)).content;
  expect(written).toMatch(/^---\nnot-related: \["?Diving\/Dive tables"?\]\n---\n/);
  expect(written.replace(/^---\n[^]*?\n---\n/, ""), "the text as it was").toBe(before);

  await hidden.getByRole("button", { name: "Undo" }).click();
  await expect(rows.filter({ hasText: /^Hidden/ })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Not related: Dive tables", exact: true })).toBeVisible();
  await expect.poll(async () => (await aliceApi.getNote(n.decompression)).content, { message: "the entry and its frontmatter gone" }).toBe(before);

  // Hidden again, then seen from the other note: Decompression isn't offered there.
  await page.getByRole("button", { name: "Not related: Dive tables", exact: true }).click();
  await expect(rows.filter({ hasText: /^Hidden/ })).toHaveCount(1);
  await waitSimilarReady(aliceApi, n.tables, nextIndex);
  await openNote(page, n.tables);
  const other = await openSimilar(page);
  await expect(other.rows).toHaveCount(1);
  await expect(row(other.rows, "Dive computer")).toHaveCount(1);
});

test("similar #4: a row answered Not related reads Hidden · its title, with Undo @similar @search", async ({ page, aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);
  await openNote(page, n.decompression);
  const { rows } = await openSimilar(page);
  await page.getByRole("button", { name: "Not related: Dive tables", exact: true }).click();
  const hidden = rows.filter({ hasText: /^Hidden/ });
  await expect(hidden.getByRole("button", { name: "Undo" })).toBeVisible();
  // The row says what was hidden (Chris, 2026-11-24; §3.6).
  await expect(hidden, "F-01: the row reads Hidden · Dive tables, then Undo (§3.6)").toHaveText("Hidden · Dive tablesUndo");
});

test("similar #5: a read member sees the suggestions without Add link and Not related @similar @search", async ({ page, bobPage, aliceApi, seedAs }) => {
  // The first index, plus shareWithBob and unshare each waiting up to 61 s per password check on the ledger (up to three for the invitation, one for the deletion).
  test.setTimeout(480_000);
  const share = await shareWithBob("Similar", "read");
  try {
    const n = at(share.vault);
    await seedAs("tags/meaning", `/${share.vault}`);
    await waitSimilarReady(aliceApi, n.decompression, firstIndex);

    await openNote(bobPage, n.decompression);
    const bob = await openSimilar(bobPage);
    await expect(bob.rows).toHaveCount(2);
    await expect(row(bob.rows, "Dive tables")).toHaveCount(1);
    await expect(row(bob.rows, "Dive computer")).toHaveCount(1);
    await expect(bob.section.getByRole("button", { name: /^(Add link to|Not related:) / })).toHaveCount(0);

    // Alice, the owner, has them.
    await openNote(page, n.decompression);
    const alice = await openSimilar(page);
    await expect(alice.section.getByRole("button", { name: /^(Add link to|Not related:) / })).toHaveCount(4);
  } finally {
    await unshare(share);
  }
});

test("similar #6: the cake has nothing above the floor; a failed request says so and Try again asks again @similar @search", async ({ page, aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  await waitSimilarReady(aliceApi, n.cake, firstIndex);
  await openNote(page, n.cake);
  const cake = await openSimilar(page);
  await expect(cake.section.getByText("No similar notes that aren't linked.", { exact: true })).toBeVisible();
  await expect(cake.rows).toHaveCount(0);

  // The request fails, as a dropped connection would; the query's retries run out first.
  await page.route("**/api/similar/**", (route) => route.abort());
  await openNote(page, n.decompression);
  const s = await openSimilar(page);
  await expect(s.section.getByText("Couldn't look for similar notes.")).toBeVisible({ timeout: 30_000 });
  await page.unroute("**/api/similar/**");
  await s.section.getByRole("button", { name: "Try again" }).click();
  await expect(s.rows).toHaveCount(2);
  await expect(s.section.getByText("Couldn't look for similar notes.")).toHaveCount(0);
});

test("similar #7: in Write mode Add link saves the text and leaves Write first @similar @search", async ({ page, aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  const before = (await aliceApi.getNote(n.decompression)).content;
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);
  await openNote(page, n.decompression);
  const { rows } = await openSimilar(page);
  await expect(rows).toHaveCount(2);

  await page.getByRole("button", { name: "Write" }).click();
  const source = page.getByRole("textbox", { name: "Markdown source", exact: true });
  await expect(source).toBeVisible();
  const edited = `${before.trimEnd()}\n\nA safety stop at five metres ends every dive.\n`;
  await source.fill(edited);
  await page.getByRole("button", { name: "Add link to Dive tables", exact: true }).click();

  await expect(page.getByRole("button", { name: "Done" }), "Write mode left").toHaveCount(0);
  await expect(page.getByRole("button", { name: "Write" })).toBeVisible();
  await expect
    .poll(async () => (await aliceApi.getNote(n.decompression)).content, { message: "the typed text saved, then the link added" })
    .toBe(`${edited.trimEnd()}\n\n## Related\n\n- [[Dive tables]]\n`);
  await expect(page.locator("article")).toContainText("A safety stop at five metres ends every dive.");
});

test("similar #8: the graph's suggested links, their card's Add link, and Not related with Undo @similar @search", async ({ page, aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  const before = (await aliceApi.getNote(n.decompression)).content;
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);
  await openNote(page, n.decompression);
  await page.getByRole("button", { name: "Graph", exact: true }).click();
  const suggestions = page.getByRole("button", { name: "Suggestions", exact: true });
  await expect(suggestions).toHaveAttribute("aria-pressed", "false");
  await suggestions.click();
  await expect(suggestions).toHaveAttribute("aria-pressed", "true");
  const suggested = page.getByRole("button", { name: /^Suggested link: / });
  await expect(suggested).toHaveCount(2);
  await expect(page.getByRole("button", { name: "Suggested link: Dive tables", exact: true })).toHaveCount(1);
  await expect(page.getByRole("button", { name: "Suggested link: Dive computer", exact: true })).toHaveCount(1);

  await page.getByRole("button", { name: "Suggested link: Dive tables", exact: true }).click();
  const card = page.getByRole("dialog", { name: "Dive tables" });
  await expect(card).toContainText("Similar in meaning, not linked");
  await card.getByRole("button", { name: "Add link" }).click();
  await expect(card).toHaveCount(0);
  await expect.poll(async () => (await aliceApi.getNote(n.decompression)).content).toBe(`${before.trimEnd()}\n\n## Related\n\n- [[Dive tables]]\n`);
  await expect(page.getByRole("button", { name: /^Suggested link: / }), "Dive tables is linked now").toHaveCount(1);

  await page.getByRole("button", { name: "Suggested link: Dive computer", exact: true }).click();
  const other = page.getByRole("dialog", { name: "Dive computer" });
  await other.getByRole("button", { name: "Not related" }).click();
  await expect(other).toContainText("Hidden: not suggested again");
  const written = (await aliceApi.getNote(n.decompression)).content;
  expect(written).toMatch(/^---\nnot-related: \["?Diving\/Dive computer"?\]\n---\n/);
  await other.getByRole("button", { name: "Undo" }).click();
  await expect(other).toHaveCount(0);
  await expect
    .poll(async () => (await aliceApi.getNote(n.decompression)).content, { message: "Undo took the entry out" })
    .toBe(`${before.trimEnd()}\n\n## Related\n\n- [[Dive tables]]\n`);
});

test("similar #10: in the side panel a row shows a short title in full @similar @search", async ({ page, aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);
  await openNote(page, n.decompression);
  const { rows } = await openSimilar(page);
  await expect(rows).toHaveCount(2);
  const title = row(rows, "Dive tables").getByText("Dive tables", { exact: true });
  const cut = await title.evaluate((e) => e.scrollWidth > e.clientWidth);
  expect(cut, "F-02: the title Dive tables is shown in full (§3.2)").toBe(false);
});

test("similar #11: a row answered Not related stays where it stood @similar @search", async ({ page, aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);
  const [best] = (await similarNotes(aliceApi, n.decompression)).notes;
  const title = best.path.slice(best.path.lastIndexOf("/") + 1).replace(/\.md$/, "");
  await openNote(page, n.decompression);
  const { rows } = await openSimilar(page);
  await expect(rows).toHaveCount(2);
  await expect(rows.first().getByRole("button").first()).toHaveAccessibleName(new RegExp(`^${title}`));

  await page.getByRole("button", { name: `Not related: ${title}`, exact: true }).click();
  await expect(rows.filter({ hasText: /^Hidden/ })).toHaveCount(1);
  await expect(rows.first(), "F-03: the hidden row stays first, in place (§3.6)").toHaveText(/^Hidden/);
});

test("similar #9: with search by meaning off, the section says so @similar", async ({ page, aliceApi, folder }) => {
  test.info().annotations.push({
    type: "finding",
    description:
      "F-09: with search by meaning off on the server, Similar notes doesn't say \"Similar notes need search by meaning, which is off for this vault.\" (§3.7): the server answers unindexed (M-07)",
  });
  const path = `/Work/${folder}/Decompression.md`;
  await aliceApi.putNote(path, "# Decompression stops\n\nThe diver ascends slowly.\n");
  try {
    await openNote(page, path);
    const s = await openSimilar(page);
    await expect(s.section.getByText("Similar notes need search by meaning, which is off for this vault.", { exact: true }), "F-09: the section says search by meaning is off").toBeVisible();
    await expect(s.section.getByRole("button", { name: "Try again" })).toHaveCount(0);
    await expect(s.rows).toHaveCount(0);
  } finally {
    await aliceApi.deleteNote(path);
  }
});
