// SPDX-License-Identifier: AGPL-3.0-or-later
// `GET /api/graph/similarity`, the scores of a local graph's links between two notes (docs/superpowers/specs/
// 2026-11-22-graph-out-of-preview-design.md §1, #305), without a page. The graph column's drawing and foot are
// web/e2e/graph/graph.spec.ts; 0.6.0's graph tests over MCP (web/e2e/mcp/graph.spec.ts) stay as they are.
//
// Each test in a folder of its own below Work: the scores are per link of the local graph, so other tests' copies of
// the same fixtures elsewhere in Work don't reach them, and every link names its target with the folder, as links
// resolve vault-wide. Measured with arctic-m on the meaning fixtures: the diving notes 0.53–0.63 with each other, every
// other pair below 0.33.
//
// Promises in scope, and the test that covers each:
//  1. The parameters of `/local`; one pair per link between two notes, keyed by the two paths; attachments and ghosts
//     have none; `null` for a note not embedded (one with no text to embed: deterministic, where a fresh note is
//     embedded within about a second); `state` `ready` → "graph-similarity #1"
//  2. The score: a diving pair above a diving note and the cake (order of a clear case only, never a value)
//     → "graph-similarity #2"
//  3. A read member gets the scores of a vault they have → "graph-similarity #3"
//  4. 404 for a vault the caller hasn't: Bob (a read member of Work) for Alice's own vault, answered as for a vault
//     that doesn't exist; 400 for bad parameters, as `/local` answers them → "graph-similarity #4" (no `@search`,
//     `@since-0-7-0`: both come before any vector is read, GraphApi.cs:145, so the phase with search off is enough)
//  5. `off` with a `reason` and no pairs when search by meaning is off → "graph-similarity #5" (no `@search`, and
//     `@since-0-7-0`: it runs in the phase with search off, and fails on a stack with it on)
// Left out: `preparing` (the model is loaded before the tests run, plan review point 5); a note embedded by another
// model (needs a provider switch, Task 9's); scores for a member who hasn't consented to the vault's provider (§1, "No
// text leaves the server"): Work uses the internal model, so no consent is asked here; it belongs to `@tensorx`; no
// `ETag` and the `changed` nudge: the web app's (Vitest, api.test.tsx).
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { Api, seed } from "../support/api";
import { fixturesDir } from "../support/env";
import { firstIndex, nextIndex, waitIndexed } from "../support/search";
import { fixtureTree } from "../support/test";
import { expect, test } from "./fixtures";

type Pair = { from: string; to: string; score: number | null };
type Similarity = { state: "ready" | "preparing" | "off"; pairs: Pair[]; reason: string | null };
type Local = { nodes: { path: string | null; title: string; kind: string }[]; edges: [number, number, number][] };

// What each test wrote, removed after it (there is no `rm -r`).
let written: string[] = [];

test.afterEach(async ({ mcp }) => {
  if (written.length > 0) await mcp.sh(`rm ${written.map((p) => `"${p}"`).join(" ")}`);
  written = [];
});

/**
 * The meaning fixtures below `/Work/<folder>/`, with Dive tables linking Decompression, the cake, an empty note, an
 * image and a note that doesn't exist, and Decompression linking Dive computer (depth 2).
 */
async function graphIn(api: Api, folder: string) {
  const id = folder.slice(-6);
  const root = `/Work/${folder}`;
  const n = {
    tables: `${root}/Diving/Dive tables.md`,
    decompression: `${root}/Diving/Decompression.md`,
    computer: `${root}/Diving/Dive computer.md`,
    cake: `${root}/Baking/Chocolate cake.md`,
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
  await api.putNote(
    n.tables,
    `${tables.content.trimEnd()}\n\nSee [[${folder}/Diving/Decompression]], [[${folder}/Baking/Chocolate cake]] and [[${folder}/Empty]]; the plan ![[${folder}/plan ${id}.png]]; ask [[${n.ghost}]].\n`,
  );
  const decompression = await api.getNote(n.decompression);
  await api.putNote(n.decompression, `${decompression.content.trimEnd()}\n\nThe computer: [[${folder}/Diving/Dive computer]].\n`);
  return n;
}

/** The first note within the first index's bound (the model may still load), the rest within the next one's. */
async function waitAll(api: Api, [first, ...rest]: string[]) {
  await waitIndexed(api, [first], firstIndex);
  await waitIndexed(api, rest, nextIndex);
}

const query = (params: Record<string, string | number>) => new URLSearchParams(Object.entries(params).map(([k, v]) => [k, String(v)])).toString();
const similarityOf = (api: Api, params: Record<string, string | number>) => api.json<Similarity>("GET", `/api/graph/similarity?${query(params)}`);
/** A pair's two paths, in a fixed order, so a pair matches whichever way its link runs. */
const key = (a: string, b: string) => [a, b].sort().join(" ↔ ");

/** The links of `/local`'s answer between two notes (a day note is a note), each pair once. */
const noteLinks = (local: Local) => {
  const isNote = (i: number) => ["note", "day"].includes(local.nodes[i].kind) && local.nodes[i].path !== null;
  return [...new Set(local.edges.filter(([a, b]) => a !== b && isNote(a) && isNote(b)).map(([a, b]) => key(local.nodes[a].path!, local.nodes[b].path!)))].sort();
};

test("graph-similarity #1: one pair per link between two notes, keyed by paths, none for the image or the ghost, null for a note with nothing embedded @graph @search", async ({ aliceApi, folder }) => {
  test.setTimeout(240_000);
  const n = await graphIn(aliceApi, folder);
  await waitAll(aliceApi, [n.tables, n.decompression, n.computer, n.cake]);

  const asked: Record<string, string | number>[] = [{ path: n.tables }, { path: n.tables, depth: 2 }, { path: n.decompression, dir: "out", depth: 2 }, { path: n.tables, attachments: "false", ghosts: "false" }];
  for (const params of asked) {
    const local = await aliceApi.json<Local>("GET", `/api/graph/local?${query(params)}`);
    const answer = await similarityOf(aliceApi, params);
    expect(answer.state, `${query(params)}: state`).toBe("ready");
    expect(answer.reason, `${query(params)}: reason`).toBeNull();
    const pairs = answer.pairs.map((p) => key(p.from, p.to));
    expect(pairs.length, `${query(params)}: each pair once`).toBe(new Set(pairs).size);
    expect(pairs.sort(), `${query(params)}: the links of /local between two notes`).toEqual(noteLinks(local));
  }

  // Depth 1: the three notes Dive tables links; not the image, not the ghost, though /local draws both.
  const local = await aliceApi.json<Local>("GET", `/api/graph/local?${query({ path: n.tables })}`);
  expect(local.nodes.map((x) => x.kind).sort(), "the local graph holds the image and the ghost").toEqual(["attachment", "ghost", "note", "note", "note", "note"]);
  const one = await similarityOf(aliceApi, { path: n.tables });
  const scores = new Map(one.pairs.map((p) => [key(p.from, p.to), p.score]));
  expect([...scores.keys()].sort()).toEqual([key(n.tables, n.cake), key(n.tables, n.decompression), key(n.tables, n.empty)].sort());
  expect(scores.get(key(n.tables, n.empty)), "the empty note has nothing embedded").toBeNull();
  for (const other of [n.decompression, n.cake]) {
    const s = scores.get(key(n.tables, other));
    expect(typeof s, `${other}: measured`).toBe("number");
    expect(s!, `${other}: within [0, 1]`).toBeGreaterThanOrEqual(0);
    expect(s!, `${other}: within [0, 1]`).toBeLessThanOrEqual(1);
  }
  // Depth 2 adds Decompression → Dive computer, measured.
  const two = await similarityOf(aliceApi, { path: n.tables, depth: 2 });
  expect(two.pairs.find((p) => key(p.from, p.to) === key(n.decompression, n.computer))?.score, "depth 2: Decompression and Dive computer").toEqual(expect.any(Number));
});

test("graph-similarity #2: two diving notes score above a diving note and the cake @graph @search", async ({ aliceApi, folder }) => {
  test.setTimeout(240_000);
  const n = await graphIn(aliceApi, folder);
  await waitAll(aliceApi, [n.tables, n.decompression, n.computer, n.cake]);
  const answer = await similarityOf(aliceApi, { path: n.tables, depth: 2 });
  const score = (a: string, b: string) => answer.pairs.find((p) => key(p.from, p.to) === key(a, b))?.score ?? null;
  const diving = score(n.tables, n.decompression);
  const cake = score(n.tables, n.cake);
  expect(diving, "Dive tables and Decompression measured").not.toBeNull();
  expect(cake, "Dive tables and the cake measured").not.toBeNull();
  expect(diving!, "Dive tables is closer to Decompression than to the cake").toBeGreaterThan(cake!);
  expect(score(n.decompression, n.computer)!, "Decompression is closer to Dive computer than Dive tables is to the cake").toBeGreaterThan(cake!);
});

test("graph-similarity #3: Bob, a read member of Work, gets the same scores as Alice @graph @search", async ({ aliceApi, bobApi, folder }) => {
  test.setTimeout(240_000);
  const n = await graphIn(aliceApi, folder);
  await waitAll(aliceApi, [n.tables, n.decompression, n.cake]);
  const alice = await similarityOf(aliceApi, { path: n.tables });
  const bob = await similarityOf(bobApi, { path: n.tables });
  expect(bob.state).toBe("ready");
  expect(bob.pairs.map((p) => [key(p.from, p.to), p.score]).sort(), "Bob gets Alice's scores").toEqual(alice.pairs.map((p) => [key(p.from, p.to), p.score]).sort());
});

test("graph-similarity #4: a vault Bob hasn't answers 404 as one that doesn't exist; bad parameters 400, as /local answers them @graph @since-0-7-0", async ({ aliceApi, bobApi, folder }) => {
  const n = await graphIn(aliceApi, folder);
  // Alice's own vault (Home), and one that doesn't exist, with the same note's name.
  const nowhere = `Nowhere-${folder.slice(-6)}`;
  const home = await bobApi.send("GET", `/api/graph/similarity?${query({ path: "/Home/Zahnarzt.md" })}`);
  const none = await bobApi.send("GET", `/api/graph/similarity?${query({ path: `/${nowhere}/Zahnarzt.md` })}`);
  expect(home.status(), "Bob, Alice's Home").toBe(404);
  expect(none.status(), "Bob, a vault that doesn't exist").toBe(404);
  expect((await home.text()).replaceAll("Home", "<vault>"), "the same answer as for a vault that doesn't exist").toBe((await none.text()).replaceAll(nowhere, "<vault>"));
  const local = await bobApi.send("GET", `/api/graph/local?${query({ path: "/Home/Zahnarzt.md" })}`);
  expect(local.status(), "/local, Bob, Alice's Home").toBe(404);

  const bad: Record<string, string | number>[] = [
    {},
    { path: "Work/plan.md" },
    { path: "/Work" },
    { path: n.tables, depth: 0 },
    { path: n.tables, depth: 4 },
    { path: n.tables, cap: 0 },
    { path: n.tables, cap: 101 },
    { path: n.tables, dir: "sideways" },
    { path: n.tables, depth: "two" },
    { path: n.tables, attachments: "maybe" },
  ];
  for (const params of bad) {
    const local = await aliceApi.send("GET", `/api/graph/local?${query(params)}`);
    const similarity = await aliceApi.send("GET", `/api/graph/similarity?${query(params)}`);
    expect(local.status(), `/local?${query(params)}`).toBe(400);
    expect(similarity.status(), `/similarity?${query(params)}`).toBe(400);
    expect(await similarity.text(), `/similarity?${query(params)}: the same words as /local`).toBe(await local.text());
  }
  // A note that doesn't exist, and a folder, are 404 as for /local.
  for (const path of [`/Work/${folder}/Nothing here.md`, `/Work/${folder}`]) {
    const local = await aliceApi.send("GET", `/api/graph/local?${query({ path })}`);
    const similarity = await aliceApi.send("GET", `/api/graph/similarity?${query({ path })}`);
    expect([similarity.status(), await similarity.text()], path).toEqual([local.status(), await local.text()]);
    expect(similarity.status(), path).toBe(404);
  }
});

test("graph-similarity #5: with search by meaning off, state off with the reason and no pairs @graph @since-0-7-0", async ({ aliceApi, folder }) => {
  test.info().annotations.push({
    type: "finding",
    description:
      "M-06: with search by meaning off on the server, /api/graph/similarity answers state ready with every pair null, not off with a reason (spec §1): SearchIndex is always registered (Program.cs:107), so EncryptedNoteStore's off branch (around :1018) is never reached and VectorsAsync answers Ready without chunks",
  });
  test.fail();
  const n = await graphIn(aliceApi, folder);
  const answer = await similarityOf(aliceApi, { path: n.tables });
  expect(answer.state, "M-06: state off with search by meaning off").toBe("off");
  expect(answer.pairs, "M-06: no pairs with search by meaning off").toEqual([]);
  expect(typeof answer.reason === "string" && answer.reason.length > 0, "M-06: a reason with search by meaning off").toBe(true);
  // In the words the search already uses (spec §1); the exact tail is not the spec's to settle.
  expect(answer.reason, "the reason's words").toMatch(/^search by meaning is off\b/);
});
