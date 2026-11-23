// SPDX-License-Identifier: AGPL-3.0-or-later
// The graph out of preview in the browser (docs/superpowers/specs/2026-11-22-graph-out-of-preview-design.md, #305; the
// column's words from 2026-11-21-graph-view-design.md): edges by meaning, the foot's line, the Suggestions layer and its
// card. The endpoint without a page is web/e2e/mcp/graph-similarity.spec.ts.
//
// The drawing is SVG (GraphCanvas.tsx): a node is a `g` with role button, its label "<title>, N links in, M out" and
// its place in `transform`; an edge a `line` from one node's place to the other's, with its `stroke-width`. So an
// edge's width and length are read from the DOM, matching the line's ends to the two nodes' places. Only the order
// between a clearly close pair (two diving notes) and a clearly far one (a diving note and the cake) is asserted.
// Reduced motion, so the layout is at rest when drawn (graph view spec §5).
//
// The edges and the foot in a folder of each test's own below Work, every link naming its target with the folder (links
// resolve vault-wide); the scores are per link, so other tests' copies of the fixtures don't reach them. The
// suggestions are vault-wide, and a copy of a fixture scores 1.00: their test runs in a vault of this file's own, made
// before it and deleted after (plan review point 6). Measured with arctic-m on the meaning fixtures: the diving notes
// 0.53–0.63 with each other, every other pair below 0.33.
//
// Promises in scope, and the test that covers each:
//  1. Closer notes drawn thicker and shorter (§1 "In the view", graph view spec §5), in the dialog → "graph #1"
//     (desktop and phone); in the column from 1920 px → "graph #2"
//  2. The foot: nothing when every link between notes is measured → "graph #1", "graph #2"; "Similarity measured for
//     N of M links" while a note isn't embedded (one with no text to embed, which stays so: deterministic)
//     → "graph #3" (desktop and phone); "Similarity not measured: search by meaning is off" with the edges drawn
//     plainly → "graph #5" (no `@search`, `@since-0-7-0`: it runs in the phase with search off, and fails here)
//  3. The Suggestions switch shown in this build, off by default; its layer the vault's suggestions not in the graph;
//     the card with the other note's title, "Similar in meaning, not linked", Add link (written, read back, the note
//     then in the graph) and Not related (hidden, written, read back, Undo) (§2; link prediction spec §2)
//     → "graph #4"
// Left out: "… getting ready" (the model is loaded before the tests run, plan review point 5); a note read before it
// is embedded (embedded within about a second of its write here, so not reachable deterministically; the empty note
// stands for it); offline ("The graph needs a connection"): Task 7; the tooltip with a score: left out by the spec.
import type { Locator, Page } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { dismissInstallHint } from "../calendar/helpers";
import { Api, seed } from "../support/api";
import { fixturesDir, secrets } from "../support/env";
import { firstIndex, similarNotes, waitIndexed, waitSimilarReady } from "../support/search";
import { reservePassword } from "../support/session";
import { expect, fixtureTree, shot, shotsDir070, test } from "../support/test";

test.use({ shotsDir: shotsDir070 });

const noteUrl = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;

async function openNote(page: Page, path: string) {
  await dismissInstallHint(page);
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.goto(noteUrl(path));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

/** Below 1920 px the Graph button opens the dialog (graph view spec §12); its title is the note's. */
async function openDialog(page: Page, title: string) {
  await page.getByRole("button", { name: "Graph", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: title });
  await expect(dialog).toBeVisible();
  return dialog;
}

// What each test wrote below Work, removed after it (there is no `rm -r`).
let written: string[] = [];

test.afterEach(async ({ mcp }) => {
  if (written.length > 0) await mcp.sh(`rm ${written.map((p) => `"${p}"`).join(" ")}`);
  written = [];
});

/**
 * The meaning fixtures below `/Work/<folder>/`: Dive tables links Decompression, the cake, an image and a note that
 * doesn't exist; Tomatoes links Decompression and an empty note.
 */
async function graphIn(api: Api, folder: string) {
  const id = folder.slice(-6);
  const root = `/Work/${folder}`;
  const n = {
    tables: `${root}/Diving/Dive tables.md`,
    decompression: `${root}/Diving/Decompression.md`,
    cake: `${root}/Baking/Chocolate cake.md`,
    tomatoes: `${root}/Garden/Tomatoes.md`,
    empty: `${root}/Empty.md`,
    png: `${root}/plan ${id}.png`,
    ghost: `Nobody ${id}`,
  };
  const files = fixtureTree("tags/meaning", root);
  written.push(...files.map((f) => f.path), n.png, n.empty);
  await seed(api, files);
  await api.putFile(n.png, readFileSync(join(fixturesDir, "tags/smoke/pixel.png")));
  await api.putNote(n.empty, "");
  const tables = await api.getNote(n.tables);
  await api.putNote(n.tables, `${tables.content.trimEnd()}\n\nSee [[${folder}/Diving/Decompression]] and [[${folder}/Baking/Chocolate cake]]; the plan ![[${folder}/plan ${id}.png]]; ask [[${n.ghost}]].\n`);
  const tomatoes = await api.getNote(n.tomatoes);
  await api.putNote(n.tomatoes, `${tomatoes.content.trimEnd()}\n\nUnrelated: [[${folder}/Diving/Decompression]] and [[${folder}/Empty]].\n`);
  return n;
}

type Edge = { width: number; length: number };

/**
 * The edge from the node titled `centre` to each node titled in `others`, as drawn: its `stroke-width`, and the
 * distance between the two nodes' places (the line's ends). Null for an edge not (yet) drawn.
 */
function edgesOf(drawing: Locator, centre: string, others: string[]): Promise<(Edge | null)[]> {
  return drawing.evaluate(
    (svg, { centre, others }) => {
      const place = (title: string) => {
        const node = [...svg.querySelectorAll('g[role="button"]')].find((g) => g.getAttribute("aria-label")?.startsWith(`${title}, `));
        const m = /translate\(([-\d.e]+)[ ,]+([-\d.e]+)\)/.exec(node?.getAttribute("transform") ?? "");
        return m ? { x: Number(m[1]), y: Number(m[2]) } : null;
      };
      const near = (x: string | null, y: string | null, p: { x: number; y: number }) => Math.hypot(Number(x) - p.x, Number(y) - p.y) < 0.01;
      const c = place(centre);
      return others.map((title) => {
        const p = place(title);
        if (!c || !p) return null;
        const line = [...svg.querySelectorAll("line")].find(
          (l) =>
            (near(l.getAttribute("x1"), l.getAttribute("y1"), c) && near(l.getAttribute("x2"), l.getAttribute("y2"), p)) ||
            (near(l.getAttribute("x1"), l.getAttribute("y1"), p) && near(l.getAttribute("x2"), l.getAttribute("y2"), c)),
        );
        return line ? { width: Number(line.getAttribute("stroke-width")), length: Math.hypot(p.x - c.x, p.y - c.y) } : null;
      });
    },
    { centre, others },
  );
}

/**
 * Until Dive tables' edges to Decompression and to the cake are drawn with widths of their own (the placeholder draws
 * both alike), then the close pair thicker and shorter than the far one.
 */
async function expectCloserThickerAndShorter(drawing: Locator) {
  await expect
    .poll(async () => {
      const [close, far] = await edgesOf(drawing, "Dive tables", ["Decompression", "Chocolate cake"]);
      return close && far ? (close.width === far.width ? "alike" : "measured") : "not drawn";
    }, { message: "the edges carry their measured similarity", timeout: 15_000 })
    .toBe("measured");
  const [close, far] = (await edgesOf(drawing, "Dive tables", ["Decompression", "Chocolate cake"])) as Edge[];
  expect(close.width, "Decompression's edge is thicker than the cake's").toBeGreaterThan(far.width);
  expect(close.length, "Decompression sits nearer Dive tables than the cake").toBeLessThan(far.length);
}

test("graph #1: in the dialog, the diving note drawn thicker and nearer than the cake; no similarity line with every link measured @graph @search @phone", async ({ page, aliceApi, folder }, testInfo) => {
  test.setTimeout(240_000);
  const n = await graphIn(aliceApi, folder);
  await waitIndexed(aliceApi, [n.tables, n.decompression, n.cake], firstIndex);
  await openNote(page, n.tables);
  const dialog = await openDialog(page, "Dive tables");
  const drawing = dialog.getByRole("group", { name: "Graph of Dive tables" });
  // The image and the ghost are drawn, with links of their own.
  await expect(drawing.getByRole("button", { name: `plan ${folder.slice(-6)}.png, 1 link in, 0 out` })).toHaveCount(1);
  await expect(drawing.getByRole("button", { name: `${n.ghost}, no note yet` })).toHaveCount(1);
  await expectCloserThickerAndShorter(drawing);
  // Both links between notes measured: the image's and the ghost's have no score and need no line.
  await expect(dialog.getByText(/^Similarity/)).toHaveCount(0);
  await shot(page, testInfo, "graph");
});

test.describe("at 1920 px", () => {
  test.use({ viewport: { width: 1920, height: 1080 } });

  test("graph #2: in the column, the diving note drawn thicker and nearer than the cake; no similarity line @graph @search", async ({ page, aliceApi, folder }) => {
    test.setTimeout(240_000);
    const n = await graphIn(aliceApi, folder);
    await waitIndexed(aliceApi, [n.tables, n.decompression, n.cake], firstIndex);
    await openNote(page, n.tables);
    const column = page.getByRole("complementary", { name: "Graph" });
    const toggle = page.getByRole("button", { name: "Graph", exact: true });
    if ((await toggle.getAttribute("aria-expanded")) === "false") await toggle.click();
    await expect(toggle).toHaveAttribute("aria-expanded", "true");
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expectCloserThickerAndShorter(column.getByRole("group", { name: "Graph of Dive tables" }));
    await expect(column.getByText(/^Similarity/)).toHaveCount(0);
  });
});

test("graph #3: the foot says 'Similarity measured for 1 of 2 links' while a linked note has nothing embedded @graph @search @phone", async ({ page, aliceApi, folder }) => {
  test.setTimeout(240_000);
  const n = await graphIn(aliceApi, folder);
  await waitIndexed(aliceApi, [n.tomatoes, n.decompression], firstIndex);
  await openNote(page, n.tomatoes);
  const dialog = await openDialog(page, "Tomatoes");
  await expect(dialog.getByRole("group", { name: "Graph of Tomatoes" }).getByRole("button", { name: /^Empty, / })).toHaveCount(1);
  await expect(dialog.getByText(/^Similarity/)).toHaveText("Similarity measured for 1 of 2 links");
});

test("graph #5: with search by meaning off, the foot says so and the edges are drawn alike @graph @since-0-7-0 @phone", async ({ page, aliceApi, folder }) => {
  const n = await graphIn(aliceApi, folder);
  await openNote(page, n.tables);
  const dialog = await openDialog(page, "Dive tables");
  // The spec's words; the reason is the search's own ("… is off on this server", ISearchIndex.cs).
  await expect(dialog.getByText(/^Similarity/)).toHaveText(/^Similarity not measured: search by meaning is off\b/);
  const [close, far] = await edgesOf(dialog.getByRole("group", { name: "Graph of Dive tables" }), "Dive tables", ["Decompression", "Chocolate cake"]);
  expect(close, "Decompression's edge drawn").not.toBeNull();
  expect(close!.width, "unmeasured edges are drawn alike").toBe(far!.width);
});

test.describe("suggestions", () => {
  // The suggestions are vault-wide: a vault of this file's own, with the meaning fixtures and no links.
  let vault = "";
  test.beforeAll(async () => {
    vault = `Graph-${randomBytes(3).toString("hex")}`;
    const api = await Api.as("alice");
    try {
      await api.json("POST", "/api/vaults", { name: vault });
      await seed(api, fixtureTree("tags/meaning", `/${vault}`));
    } finally {
      await api.dispose();
    }
  });
  test.afterAll(async () => {
    test.setTimeout(240_000);
    if (!vault) return;
    const api = await Api.as("alice");
    try {
      await reservePassword("alice");
      await api.json("POST", `/api/vaults/${encodeURIComponent(vault)}/delete`, { password: secrets().passwords.alice, confirm: vault });
    } finally {
      await api.dispose();
    }
  });

  test("graph #4: the Suggestions switch, the vault's suggestions as dashed links, the card's Add link and Not related with Undo @graph @search", async ({ page, aliceApi }) => {
    test.setTimeout(240_000);
    const computer = `/${vault}/Diving/Dive computer.md`;
    await waitSimilarReady(aliceApi, computer, firstIndex);
    const expected = (await similarNotes(aliceApi, computer)).notes.map((s) => s.path.slice(s.path.lastIndexOf("/") + 1).replace(/\.md$/, ""));
    expect(expected.sort(), "the server's suggestions: the two other diving notes").toEqual(["Decompression", "Dive tables"]);

    await openNote(page, computer);
    const dialog = await openDialog(page, "Dive computer");
    const drawing = dialog.getByRole("group", { name: "Graph of Dive computer" });
    const switched = dialog.getByRole("button", { name: "Suggestions", exact: true });
    await expect(switched, "off by default").toHaveAttribute("aria-pressed", "false");
    const suggested = drawing.getByRole("button", { name: /^Suggested link: / });
    await expect(suggested).toHaveCount(0);
    await switched.click();
    await expect(switched).toHaveAttribute("aria-pressed", "true");
    await expect(suggested).toHaveCount(2);
    expect((await suggested.evaluateAll((els) => els.map((e) => e.getAttribute("aria-label")))).sort()).toEqual(["Suggested link: Decompression", "Suggested link: Dive tables"]);
    // Dashed, from the note in the middle.
    expect(await drawing.locator('line[stroke-dasharray="4 4"]').count(), "a dashed edge to each").toBe(2);

    // Add link: the card closes, the link is written under Related, and Dive tables is a neighbour, no longer suggested.
    const before = (await aliceApi.getNote(computer)).content;
    await drawing.getByRole("button", { name: "Suggested link: Dive tables", exact: true }).click();
    const card = dialog.getByRole("dialog", { name: "Dive tables" });
    await expect(card).toContainText("Similar in meaning, not linked");
    await card.getByRole("button", { name: "Add link" }).click();
    await expect(card).toHaveCount(0);
    await expect.poll(async () => (await aliceApi.getNote(computer)).content, { message: "the link read back" }).toBe(`${before.trimEnd()}\n\n## Related\n\n- [[Dive tables]]\n`);
    await expect(drawing.getByRole("button", { name: /^Dive tables, / })).toHaveCount(1);
    await expect(suggested).toHaveCount(1);

    // Not related: hidden in place, written to the note's properties, and Undo takes it out again.
    await drawing.getByRole("button", { name: "Suggested link: Decompression", exact: true }).click();
    const other = dialog.getByRole("dialog", { name: "Decompression" });
    await other.getByRole("button", { name: "Not related" }).click();
    await expect(other).toContainText("Hidden: not suggested again");
    await expect.poll(async () => (await aliceApi.getNote(computer)).content, { message: "not-related read back" }).toMatch(/^not-related: \["Diving\/Decompression"\]$/m);
    await other.getByRole("button", { name: "Undo" }).click();
    await expect(other).toHaveCount(0);
    await expect.poll(async () => (await aliceApi.getNote(computer)).content, { message: "Undo read back" }).not.toContain("not-related");
    await expect(drawing.getByRole("button", { name: "Suggested link: Decompression", exact: true })).toHaveCount(1);
  });
});
