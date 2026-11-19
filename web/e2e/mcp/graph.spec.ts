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
// 7. The creation time: set on creation, kept through a write, `mv`, `rm` and `restore`; shown as the node's `created`
//    (§5; `sh` shows it nowhere, so `/api/graph/node`) → "graph: a note's creation time …"; kept through `archive`
//    → "graph: archive keeps a note's creation time" (finding M-03).
// 8. The filter on a local graph: `--no-days`, `--no-attachments`, `--no-ghosts`, `--archive` (archived notes hidden
//    by default), `--tag`; the start note always shown; day notes a kind of their own (§6's filter, §4.3)
//    → "graph: the filter flags on a local graph …".
// 9. `(alias)` and `(ambiguous)`: a link found through an alias, and one edge to each of several notes that match
//    (§4.3) → "graph: a link by an alias and an ambiguous link are marked".
// 10. Ghost identity: a bare name is one ghost whatever its case, a name with a folder a ghost of its own, and once the
//    note exists the ghost is gone and its edges lead to the note (§4.3) → "graph: links to a missing note …".
// 11. Rights: a key without the vault sees nothing of it, answered as for a path that doesn't exist (§8)
//    → "graph: a key without the vault is answered as if it weren't there".
// Left out: the graph over several vaults and a hidden discreet vault (§8): needs `@serial` and a discreet session,
// which this project has not; the ETag and `304` (§4.4, §7.2): xUnit covers it, and a vault others write to changes
// its version between two calls; the budget with a hub of 400 backlinks (§10): `GraphCommandTests.Hub_at_depth_three_stays_in_budget`
// (tests/Kumbu.Core.Tests/Sh/GraphCommandTests.cs:106); the time-lapse and
// the "at the latest" fill for notes from before (§5): no such notes on a fresh stack; the bundle query (§6.7): not
// exposed, Publish covers it.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import type { Api, Mcp } from "../support/api";
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
  // B → Hub and Hub → B both show: the local graph returns the edges between the nodes it returned (§6.1), so the
  // direction picks the walk, not the edges listed.
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
  // Hub → A, B, C, D, E; X → C, D, E: C, D and E have two links each, A and B one. The cap of 2 takes two of the
  // better connected, not the first by path, and among the three tied ones C and D by path.
  await write(mcp, p("Hub"), `# Hub\n\n${["A", "B", "C", "D", "E"].map(n.link).join(" ")}\n`);
  await write(mcp, p("X"), `# X\n\n${["C", "D", "E"].map(n.link).join(" ")}\n`);
  for (const title of ["A", "B", "C", "D", "E"]) await write(mcp, p(title), `# ${title}\n`);

  expect(await mcp.sh(`graph "${p("Hub")}" --cap 2`)).toEqual({
    text: [
      `${p("Hub")}, depth 1, both directions: 3 notes`,
      `1  ${p("Hub")} -> ${p("C")}`,
      `1  ${p("Hub")} -> ${p("D")}`,
      `   +3 more linked from ${p("Hub")}: graph "${p("Hub")}" --out`,
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
  expect(old.text).toMatch(
    new RegExp(`^graph: ${escape(p("A"))}: No such file or directory; it was moved to ${escape(renamed)} on \\d{4}-\\d\\d-\\d\\d \\d\\d:\\d\\d UTC$`),
  );
});

type Detail = { node: { path: string; kind: string; archived: boolean; in: number; out: number }; changed: string; created: string; createdAtLeast: boolean; firstParagraph: string | null };
const nodeOf = (api: Api, path: string) => api.json<Detail>("GET", `/api/graph/node?path=${encodeURIComponent(path)}`);

test("graph: a note's creation time is set when it is made and kept through a write, mv, rm and restore @graph", async ({ mcp, folder, aliceApi }) => {
  const n = names(folder);
  const node = (path: string) => nodeOf(aliceApi, path);

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

  for (const command of [`rm "${n.path("Moved")}"`, `restore "${n.path("Moved")}"`]) {
    const done = await mcp.sh(command);
    expect(done.isError, done.text).toBe(false);
  }
  const restored = await node(n.path("Moved"));
  expect({ created: restored.created, createdAtLeast: restored.createdAtLeast }, "restore from the trash keeps it").toEqual({ created: made.created, createdAtLeast: false });
});

test("graph: archive keeps a note's creation time @graph", async ({ mcp, folder, aliceApi }) => {
  test.fail(true, "M-03 (Medium, bug): archive keeps the creation time's value but marks it 'at the latest'");
  const n = names(folder);
  await write(mcp, n.path("Life"), "# Life\n");
  const made = await nodeOf(aliceApi, n.path("Life"));
  expect(made.createdAtLeast).toBe(false);

  const archived = await mcp.sh(`archive "${n.path("Life")}"`);
  expect(archived.isError, archived.text).toBe(false);
  const to = /^moved .+ to (\/Work\/_archive\/.+\.md); no links needed rewriting$/m.exec(archived.text)?.[1];
  expect(to, archived.text).toBeDefined();
  written = [to!];
  const there = await nodeOf(aliceApi, to!);
  expect(there.node.archived).toBe(true);
  expect(there.created, "archive keeps the creation time").toBe(made.created);
  expect(there.createdAtLeast, "M-03: archive keeps the creation time as recorded, not as 'at the latest' (§5)").toBe(false);
});

test("graph: the filter flags on a local graph, the start note always shown, day notes their own kind @graph", async ({ mcp, folder, aliceApi }) => {
  const n = names(folder);
  const p = n.path;
  const tag = `e2e-graph-${n.id}`;
  const png = `/Work/${folder}/pixel ${n.id}.png`;
  const day = `/Work/${folder}/2026-11-19.md`;
  written.push(png);
  await aliceApi.putFile(png, readFileSync(join(fixturesDir, "tags/smoke/pixel.png")));
  // Start → Tagged, Plain, the image, a ghost, a day note (by its folder, as day notes share names) and Old; the day
  // note links back.
  await write(mcp, day, `# Day\n\n${n.link("Start")}\n`);
  await write(mcp, p("Start"), `# Start\n\n${n.link("Tagged")} ${n.link("Plain")} ![[pixel ${n.id}.png]] ${n.link("Nobody")} [[${folder}/2026-11-19]] ${n.link("Old")}\n`);
  await write(mcp, p("Tagged"), `# Tagged\n\n#${tag}\n`);
  await write(mcp, p("Plain"), "# Plain\n");
  await write(mcp, p("Old"), "# Old\n");

  const line = (to: string, mark = "") => `1  ${p("Start")} -> ${to}${mark}`;
  const back = `1  ${day} -> ${p("Start")}`;
  const graph = (flags: string) => mcp.sh(`graph "${p("Start")}"${flags}`);
  const all = [line(day), line(p("Old")), line(png, " (embed)"), line(p("Plain")), line(p("Tagged")), line(n.name("Nobody"), " (no note)"), back];
  expect(await graph("")).toEqual({
    text: [`${p("Start")}, depth 1, both directions: 5 notes, 1 attachment, 1 without a note`, ...all].join("\n"),
    isError: false,
  });
  expect(await graph(" --no-days")).toEqual({
    text: [`${p("Start")}, depth 1, both directions: 4 notes, 1 attachment, 1 without a note`, ...all.filter((l) => !l.includes(day))].join("\n"),
    isError: false,
  });
  expect(await graph(" --no-attachments --no-ghosts")).toEqual({
    text: [`${p("Start")}, depth 1, both directions: 5 notes`, line(day), line(p("Old")), line(p("Plain")), line(p("Tagged")), back].join("\n"),
    isError: false,
  });
  // Only notes with the tag, the start note always: it hasn't the tag.
  expect(await graph(` --tag ${tag}`)).toEqual({ text: [`${p("Start")}, depth 1, both directions: 2 notes`, line(p("Tagged"))].join("\n"), isError: false });
  expect(await mcp.sh(`graph "${p("Plain")}" --tag ${tag}`)).toEqual({ text: `${p("Plain")}, depth 1, both directions: 1 note\n(no links shown)`, isError: false });

  // The kinds, as the REST local graph gives them.
  const local = await aliceApi.json<{ nodes: { path: string | null; title: string; kind: string }[] }>("GET", `/api/graph/local?path=${encodeURIComponent(p("Start"))}`);
  expect(Object.fromEntries(local.nodes.map((x) => [x.path ?? x.title, x.kind]))).toEqual({
    [p("Start")]: "note",
    [day]: "day",
    [p("Old")]: "note",
    [png]: "attachment",
    [p("Plain")]: "note",
    [p("Tagged")]: "note",
    [n.name("Nobody")]: "ghost",
  });

  // Archived, Old leaves the graph unless --archive asks for it; the link followed it into the archive.
  const archived = await mcp.sh(`archive "${p("Old")}"`);
  const old = /^moved .+ to (\/Work\/_archive\/.+\.md); rewrote 1 link in /m.exec(archived.text)?.[1];
  expect(old, archived.text).toBeDefined();
  written = written.map((w) => (w === p("Old") ? old! : w));
  expect(await graph("")).toEqual({
    text: [`${p("Start")}, depth 1, both directions: 4 notes, 1 attachment, 1 without a note`, ...all.filter((l) => !l.includes(p("Old")))].join("\n"),
    isError: false,
  });
  expect(await graph(" --archive")).toEqual({
    text: [`${p("Start")}, depth 1, both directions: 5 notes, 1 attachment, 1 without a note`, line(old!), ...all.filter((l) => !l.includes(p("Old")))].join("\n"),
    isError: false,
  });
});

test("graph: a link by an alias and an ambiguous link are marked @graph", async ({ mcp, folder }) => {
  const n = names(folder);
  const p = n.path;
  await write(mcp, p("Nick"), `---\naliases: [${n.name("Nickname")}]\n---\n# Nick\n`);
  // Two notes of one name in two folders, neither preferred from here: one edge to each.
  await write(mcp, p("Twin", "S1/"), "# Twin\n");
  await write(mcp, p("Twin", "S2/"), "# Twin\n");
  await write(mcp, p("Marks"), `# Marks\n\n${n.link("Nickname")} and ${n.link("Twin")}\n`);
  expect(await mcp.sh(`graph "${p("Marks")}"`)).toEqual({
    text: [
      `${p("Marks")}, depth 1, both directions: 4 notes`,
      `1  ${p("Marks")} -> ${p("Nick")} (alias)`,
      `1  ${p("Marks")} -> ${p("Twin", "S1/")} (ambiguous)`,
      `1  ${p("Marks")} -> ${p("Twin", "S2/")} (ambiguous)`,
    ].join("\n"),
    isError: false,
  });
});

test("graph: links to a missing note meet in one ghost whatever their case, and lead to the note once it exists @graph", async ({ mcp, folder }) => {
  const n = names(folder);
  const p = n.path;
  const scope = `/Work/${folder}`;
  // G1 → Anna, G2 → anna and Sub/Anna: one ghost "Anna" (its title from the first link in path order), and one for
  // the name with a folder, which means something else.
  await write(mcp, p("G1"), `# G1\n\n[[Anna ${n.id}]]\n`);
  await write(mcp, p("G2"), `# G2\n\n[[anna ${n.id}]]\n\n[[Sub/Anna ${n.id}]]\n`);
  const subAnna = [`Sub/Anna ${n.id}, linked from 1 note:`, `  ${p("G2")}:5`];
  expect(await mcp.sh(`graph --unresolved ${scope}`)).toEqual({
    text: [`Anna ${n.id}, linked from 2 notes:`, `  ${p("G1")}:3`, `  ${p("G2")}:3`, ...subAnna].join("\n"),
    isError: false,
  });

  await write(mcp, p("Anna"), "# Anna\n");
  expect(await mcp.sh(`graph --unresolved ${scope}`)).toEqual({ text: subAnna.join("\n"), isError: false });
  expect(await mcp.sh(`graph "${p("Anna")}"`)).toEqual({
    text: [`${p("Anna")}, depth 1, both directions: 3 notes`, `1  ${p("G1")} -> ${p("Anna")}`, `1  ${p("G2")} -> ${p("Anna")}`].join("\n"),
    isError: false,
  });
});

test("graph: a key without the vault is answered as if it weren't there @graph", async ({ mcp, aliceApi }) => {
  // Alice reads Welcome in the web app; the agent's key has Work only.
  const note = "/Welcome/Start here.md";
  const hers = await aliceApi.console(`graph "${note}"`);
  expect(hers.isError, hers.text).toBe(false);
  expect(hers.text.split("\n")[0]).toMatch(new RegExp(`^${escape(note)}, depth 1, both directions: `));

  expect(await mcp.sh(`graph "${note}"`)).toEqual({ text: `graph: ${note}: No such file or directory`, isError: true });
  expect(await mcp.sh("graph --orphans /Welcome")).toEqual({ text: "graph: /Welcome: No such file or directory", isError: true });
  expect(await mcp.sh(`graph --path "${note}" "/Welcome/Skills.md"`)).toEqual({ text: `graph: ${note}: No such file or directory`, isError: true });
});

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
