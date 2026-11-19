// SPDX-License-Identifier: AGPL-3.0-or-later
// The graph backend through `sh graph`, `links`, `backlinks` and `/api/graph` (docs/superpowers/specs/2026-11-16-graph-backend-design.md;
// PRs #224, #227, #246), on a small graph of each test's own below `/Work/<folder>/`. Every list over the vault is
// scoped to that folder, as other tests write to Work at the same time.
//
// Promises in scope, and the test that covers each:
// 1. `graph <note>`: a heading with the counts, one line per link `depth  source -> target` grouped by depth, depth 1
//    to 3 (default 1), both directions by default, `--out`/`--in`, `(embed)`, `Name (no note)` for a link to nothing;
//    attachments and links to nothing end a walk (§7.1, §4.3) → "graph: a note's neighbourhood …".
// 2. The hub cap: the best-connected neighbours first, then by path, and a line naming the count and the command
//    that goes on (§6.1, §3 decision 5.3) → "graph: --cap shows the best-connected neighbours …".
// 3. `graph --path`: the shortest paths (at most 5, at most 6 links) with each arrow in its link's direction, more of
//    the same length said; none: "not connected at all" or "farther than 6 steps"; `--out` as written (§6.8)
//    → "graph --path: …" and "graph --path: at most five paths …".
// 4. `--orphans`, `--hubs [-n N]`, `--unresolved` below a path, and `--tag` selecting notes (§6.4–6.6, the filter)
//    → "graph --orphans, --hubs and --unresolved …".
// 5. At most 50 `path` and 50 `tag` in a REST filter; anything more is a 400 (§7.2, amended 2026-11-18)
//    → "graph: the REST filter takes at most 50 paths and 50 tags".
// 6. `links`, `backlinks` and the graph read one index: after `mv` they all name the new path at once (§3 decisions 4,
//    anti-goals "two truths" and "a stale graph") → "graph: after mv, links, backlinks and the graph …".
// 7. The creation time: set on creation, kept through a write and `mv`; shown as the node's `created` (§5; `sh` shows
//    it nowhere, so `/api/graph/node`) → "graph: a note's creation time …".
// Left out: the graph over several vaults and a hidden discreet vault (§8): needs `@serial` and a discreet session,
// which this project has not; the ETag and `304` (§4.4, §7.2): xUnit covers it, and a vault others write to changes
// its version between two calls; the budget with a hub of 400 backlinks (§10): costly to seed; the time-lapse and
// the "at the latest" fill for notes from before (§5): no such notes on a fresh stack; the bundle query (§6.7): not
// exposed, Publish covers it.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import type { Mcp } from "../support/api";
import { fixturesDir } from "../support/env";
import { expect, test } from "./fixtures";

/** Names below `/Work/<folder>/` carrying the folder's random part, as links resolve vault-wide. */
function names(folder: string) {
  const id = folder.slice(-6);
  return {
    id,
    name: (title: string) => `${title} ${id}`,
    path: (title: string, under = "") => `/Work/${folder}/${under}${title} ${id}.md`,
    link: (title: string) => `[[${title} ${id}]]`,
  };
}

// What each test wrote, removed after it (there is no `rm -r`).
let written: string[] = [];

async function write(mcp: Mcp, path: string, content: string) {
  written.push(path);
  const answer = await mcp.call("write", { path, content });
  expect(answer.isError, answer.text).toBe(false);
  return /^version of .+: ([0-9a-f]{12})$/m.exec(answer.text)?.[1];
}

test.afterEach(async ({ mcp }) => {
  if (written.length > 0) await mcp.sh(`rm ${written.map((p) => `"${p}"`).join(" ")}`);
  written = [];
});

test("graph: a note's neighbourhood at depth 1 to 3, in both directions or one @graph", async ({ mcp, folder, aliceApi }) => {
  const n = names(folder);
  const png = `/Work/${folder}/pixel ${n.id}.png`;
  written.push(png);
  await aliceApi.putFile(png, readFileSync(join(fixturesDir, "tags/smoke/pixel.png")));
  // Hub → A, B, C and a ghost; B → Hub; A → Deep → a ghost; C embeds the image.
  await write(mcp, n.path("Hub"), `# Hub\n\n${n.link("A")} ${n.link("B")} ${n.link("C")} ${n.link("Ghost")}\n`);
  await write(mcp, n.path("A"), `# A\n\n${n.link("Deep")}\n`);
  await write(mcp, n.path("B"), `# B\n\n${n.link("Hub")}\n`);
  await write(mcp, n.path("C"), `# C\n\n![[pixel ${n.id}.png]]\n`);
  await write(mcp, n.path("Deep"), `# Deep\n\n${n.link("Far")}\n`);

  const p = n.path;
  const one = [
    `1  ${p("Hub")} -> ${p("A")}`,
    `1  ${p("Hub")} -> ${p("B")}`,
    `1  ${p("Hub")} -> ${p("C")}`,
    `1  ${p("Hub")} -> ${n.name("Ghost")} (no note)`,
    `1  ${p("B")} -> ${p("Hub")}`,
  ];
  expect(await mcp.sh(`graph "${p("Hub")}"`)).toEqual({
    text: [`${p("Hub")}, depth 1, both directions: 4 notes, 1 without a note`, ...one].join("\n"),
    isError: false,
  });
  const two = [`2  ${p("A")} -> ${p("Deep")}`, `2  ${p("C")} -> ${png} (embed)`];
  expect(await mcp.sh(`graph "${p("Hub")}" --depth 2`)).toEqual({
    text: [`${p("Hub")}, depth 2, both directions: 5 notes, 1 attachment, 1 without a note`, ...one, ...two].join("\n"),
    isError: false,
  });
  // Outgoing only, three deep: the walk ends at the image and at the links to nothing. B → Hub is shown as an edge
  // between two notes the walk returned (§6.1), though it wasn't walked.
  expect(await mcp.sh(`graph "${p("Hub")}" --depth 3 --out`)).toEqual({
    text: [
      `${p("Hub")}, depth 3, outgoing links only: 5 notes, 1 attachment, 2 without a note`,
      ...one,
      ...two,
      `3  ${p("Deep")} -> ${n.name("Far")} (no note)`,
    ].join("\n"),
    isError: false,
  });
  expect(await mcp.sh(`graph "${p("Hub")}" --in`)).toEqual({
    text: [`${p("Hub")}, depth 1, incoming links only: 2 notes`, `1  ${p("Hub")} -> ${p("B")}`, `1  ${p("B")} -> ${p("Hub")}`].join("\n"),
    isError: false,
  });
  expect(await mcp.sh(`graph "${p("Hub")}" --depth 4`)).toEqual({ text: "graph: --depth must be 1 to 3", isError: true });
  expect(await mcp.sh(`graph "${p("Hub")}" --out --in`)).toEqual({ text: "graph: --out and --in are exclusive: give one", isError: true });
});

test("graph: --cap shows the best-connected neighbours and the command that goes on @graph", async ({ mcp, folder }) => {
  const n = names(folder);
  const p = n.path;
  // Hub → A, B, C, D; A → B: A and B have two links each, C and D one.
  await write(mcp, p("Hub"), `# Hub\n\n${["A", "B", "C", "D"].map(n.link).join(" ")}\n`);
  await write(mcp, p("A"), `# A\n\n${n.link("B")}\n`);
  for (const title of ["B", "C", "D"]) await write(mcp, p(title), `# ${title}\n`);

  expect(await mcp.sh(`graph "${p("Hub")}" --cap 2`)).toEqual({
    text: [
      `${p("Hub")}, depth 1, both directions: 3 notes`,
      `1  ${p("Hub")} -> ${p("A")}`,
      `1  ${p("Hub")} -> ${p("B")}`,
      `1  ${p("A")} -> ${p("B")}`,
      `   +2 more linked from ${p("Hub")}: graph "${p("Hub")}" --out`,
    ].join("\n"),
    isError: false,
  });
  expect(await mcp.sh(`graph "${p("Hub")}" --cap 101`)).toEqual({ text: "graph: --cap must be 1 to 100", isError: true });
});

test("graph --path: the shortest path, and 'farther than 6 steps' or 'not connected at all' @graph", async ({ mcp, folder }) => {
  const n = names(folder);
  const p = n.path;
  // A chain N0 → N1 → … → N7, and an island.
  for (let i = 0; i < 8; i++) await write(mcp, p(`N${i}`), `# N${i}\n\n${i < 7 ? n.link(`N${i + 1}`) : "The end."}\n`);
  await write(mcp, p("Island"), "# Island\n");

  const chain = (to: number) => Array.from({ length: to + 1 }, (_, i) => p(`N${i}`)).join(" -> ");
  expect(await mcp.sh(`graph --path "${p("N0")}" "${p("N6")}" --out`)).toEqual({
    text: `${p("N0")} to ${p("N6")}, 6 steps, following links as written: 1 path\n${chain(6)}`,
    isError: false,
  });
  // Walked backwards, each arrow keeps its link's own direction.
  expect(await mcp.sh(`graph --path "${p("N2")}" "${p("N0")}"`)).toEqual({
    text: `${p("N2")} to ${p("N0")}, 2 steps, in either direction: 1 path\n${p("N2")} <- ${p("N1")} <- ${p("N0")}`,
    isError: false,
  });
  expect(await mcp.sh(`graph --path "${p("N0")}" "${p("N7")}"`)).toEqual({
    text: `${p("N0")} and ${p("N7")} are farther than 6 steps apart`,
    isError: false,
  });
  expect(await mcp.sh(`graph --path "${p("N6")}" "${p("N0")}" --out`)).toEqual({
    text: `no path from ${p("N6")} to ${p("N0")} following links as written`,
    isError: false,
  });
  expect(await mcp.sh(`graph --path "${p("N0")}" "${p("Island")}"`)).toEqual({
    text: `${p("N0")} and ${p("Island")} are not connected at all (no path of any length)`,
    isError: false,
  });
  expect(await mcp.sh(`graph --path "${p("N0")}" "${p("N1")}" --hubs`)).toEqual({
    text: "graph: --path, --orphans, --hubs and --unresolved are exclusive: give one",
    isError: true,
  });
});

test("graph --path: at most five paths, and says more of the same length exist @graph", async ({ mcp, folder }) => {
  const n = names(folder);
  const p = n.path;
  // From to To through six middles: six paths of two steps.
  const middles = Array.from({ length: 6 }, (_, i) => `M${i}`);
  await write(mcp, p("From"), `# From\n\n${middles.map(n.link).join(" ")}\n`);
  for (const m of middles) await write(mcp, p(m), `# ${m}\n\n${n.link("To")}\n`);
  await write(mcp, p("To"), "# To\n");

  const answer = await mcp.sh(`graph --path "${p("From")}" "${p("To")}" --out`);
  expect(answer.isError, answer.text).toBe(false);
  const [heading, ...paths] = answer.text.split("\n");
  expect(heading).toBe(`${p("From")} to ${p("To")}, 2 steps, following links as written: 5 paths shown, more of the same length exist`);
  expect(paths).toHaveLength(5);
  for (const path of paths) expect(path).toMatch(new RegExp(`^${escape(p("From"))} -> /Work/${folder}/M[0-5] ${n.id}\\.md -> ${escape(p("To"))}$`));
  expect(new Set(paths).size, "five different paths").toBe(5);
});

test("graph --orphans, --hubs and --unresolved list what lies below the folder @graph", async ({ mcp, folder }) => {
  const n = names(folder);
  const p = n.path;
  const tag = `e2e-graph-${n.id}`;
  // Hub → A, B, Anna (none); A → Anna, Bert (none); B → Hub; Lonely (tagged) and Quiet link to nothing.
  await write(mcp, p("Hub"), `# Hub\n\n${n.link("A")} ${n.link("B")}\n\n${n.link("Anna")}\n`);
  await write(mcp, p("A"), `# A\n\n${n.link("Anna")} and ${n.link("Bert")}\n`);
  await write(mcp, p("B"), `# B\n\n${n.link("Hub")}\n`);
  await write(mcp, p("Lonely"), `# Lonely\n\nNo links. #${tag}\n`);
  await write(mcp, p("Quiet"), "# Quiet\n\nNo links either.\n");
  const scope = `/Work/${folder}`;

  expect(await mcp.sh(`graph --orphans ${scope}`)).toEqual({ text: [p("Lonely"), p("Quiet")].join("\n"), isError: false });
  expect(await mcp.sh(`graph --orphans ${scope} --tag ${tag}`)).toEqual({ text: p("Lonely"), isError: false });

  expect(await mcp.sh(`graph --hubs ${scope}`)).toEqual({
    text: [
      `4  ${p("Hub")}  (1 in, 3 out)`,
      `3  ${p("A")}  (1 in, 2 out)`,
      `2  ${p("B")}  (1 in, 1 out)`,
      `0  ${p("Lonely")}  (0 in, 0 out)`,
      `0  ${p("Quiet")}  (0 in, 0 out)`,
    ].join("\n"),
    isError: false,
  });
  expect(await mcp.sh(`graph --hubs ${scope} -n 2`)).toEqual({
    text: [`4  ${p("Hub")}  (1 in, 3 out)`, `3  ${p("A")}  (1 in, 2 out)`].join("\n"),
    isError: false,
  });

  // By how many notes link to each, with the notes and lines.
  expect(await mcp.sh(`graph --unresolved ${scope}`)).toEqual({
    text: [
      `${n.name("Anna")}, linked from 2 notes:`,
      `  ${p("A")}:3`,
      `  ${p("Hub")}:5`,
      `${n.name("Bert")}, linked from 1 note:`,
      `  ${p("A")}:3`,
    ].join("\n"),
    isError: false,
  });
  expect(await mcp.sh(`graph --unresolved ${scope} --tag ${tag}`)).toEqual({ text: "(no unresolved links)", isError: false });
  expect(await mcp.sh(`graph --orphans ${scope} --depth 2`)).toEqual({ text: "graph: --depth applies to a local graph only", isError: true });
});

test("graph: the REST filter takes at most 50 paths and 50 tags @graph", async ({ aliceApi, folder }) => {
  const repeat = (name: string, count: number, value: (i: number) => string) =>
    Array.from({ length: count }, (_, i) => `${name}=${encodeURIComponent(value(i))}`).join("&");
  const tags = (count: number) => repeat("tag", count, (i) => `e2e-none-${i}`);
  const paths = (count: number) => repeat("path", count, (i) => `/Work/${folder}/p${i}`);

  for (const [query, refusal] of [
    [tags(51), "The filter takes at most 50 tags; this named 51."],
    [paths(51), "The filter takes at most 50 paths; this named 51."],
  ]) {
    const refused = await aliceApi.send("GET", `/api/graph?vault=Work&${query}`);
    expect(refused.status()).toBe(400);
    expect(await refused.json()).toEqual({ error: refusal });
  }

  // Fifty of each are taken: no note has fifty tags of nobody's, and nothing lies below those folders.
  for (const query of [tags(50), paths(50)]) {
    const taken = await aliceApi.send("GET", `/api/graph?vault=Work&${query}`);
    expect(taken.status()).toBe(200);
    expect(await taken.json()).toEqual({ nodes: [], edges: [] });
  }
  const local = await aliceApi.send("GET", `/api/graph/paths?from=/Work/a.md&to=/Work/b.md&${tags(51)}`);
  expect(local.status()).toBe(400);
  expect(await local.json()).toEqual({ error: "The filter takes at most 50 tags; this named 51." });
});

test("graph: after mv, links, backlinks and the graph name the new path at once @graph", async ({ mcp, folder }) => {
  const n = names(folder);
  const p = n.path;
  await write(mcp, p("Hub"), `# Hub\n\n${n.link("A")} and ${n.link("Ghost")}\n`);
  await write(mcp, p("A"), `# A\n\n${n.link("Hub")}\n`);
  const renamed = p("Renamed", "Sub/");

  const moved = await mcp.sh(`mv "${p("A")}" "${renamed}"`);
  expect(moved.isError, moved.text).toBe(false);
  expect(moved.text.split("\n")[0]).toBe(`moved ${p("A")} to ${renamed}; rewrote 1 link in ${p("Hub")}`);
  written = written.map((w) => (w === p("A") ? renamed : w));

  const links = await mcp.sh(`links "${p("Hub")}"`);
  expect(links.isError, links.text).toBe(false);
  expect(links.text.split("\n\n")[0]).toBe(
    [`3: ${n.link("Renamed")} → ${renamed}`, `3: ${n.link("Ghost")} → not found`].join("\n"),
  );
  const backlinks = await mcp.sh(`backlinks "${renamed}"`);
  expect(backlinks.isError, backlinks.text).toBe(false);
  expect(backlinks.text.split("\n\n")[0]).toBe(`${p("Hub")}:3:${n.link("Renamed")} and ${n.link("Ghost")}`);

  expect(await mcp.sh(`graph "${renamed}"`)).toEqual({
    text: [`${renamed}, depth 1, both directions: 2 notes`, `1  ${renamed} -> ${p("Hub")}`, `1  ${p("Hub")} -> ${renamed}`].join("\n"),
    isError: false,
  });
  // The old path is in no list any more.
  expect((await mcp.sh(`graph --hubs /Work/${folder}`)).text).toBe([`3  ${p("Hub")}  (1 in, 2 out)`, `2  ${renamed}  (1 in, 1 out)`].join("\n"));
  const old = await mcp.sh(`graph "${p("A")}"`);
  expect(old.isError, old.text).toBe(true);
});

test("graph: a note's creation time is set when it is made and kept through a write and mv @graph", async ({ mcp, folder, aliceApi }) => {
  const n = names(folder);
  type Detail = { node: { path: string; kind: string; in: number; out: number }; changed: string; created: string; createdAtLeast: boolean; firstParagraph: string | null };
  const node = async (path: string) => aliceApi.json<Detail>("GET", `/api/graph/node?path=${encodeURIComponent(path)}`);

  const version = await write(mcp, n.path("Plan"), "# Plan\n\nThe first paragraph.\n");
  const made = await node(n.path("Plan"));
  expect(made).toEqual({
    node: { path: n.path("Plan"), title: n.name("Plan"), kind: "note", tags: [], archived: false, in: 0, out: 0 },
    changed: made.created,
    created: made.created,
    createdAtLeast: false,
    firstParagraph: "The first paragraph.",
  });

  const edited = await mcp.call("edit", { path: n.path("Plan"), version, old_text: "first", new_text: "edited" });
  expect(edited.isError, edited.text).toBe(false);
  const after = await node(n.path("Plan"));
  expect(after.created, "the creation time stays").toBe(made.created);
  expect(Date.parse(after.changed), "the last change moves on").toBeGreaterThan(Date.parse(made.changed));
  expect(after.firstParagraph).toBe("The edited paragraph.");

  const moved = await mcp.sh(`mv "${n.path("Plan")}" "${n.path("Moved")}"`);
  expect(moved.isError, moved.text).toBe(false);
  written = [n.path("Moved")];
  const there = await node(n.path("Moved"));
  expect(there.created, "mv keeps the creation time").toBe(made.created);
  expect(there.createdAtLeast).toBe(false);
});

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
