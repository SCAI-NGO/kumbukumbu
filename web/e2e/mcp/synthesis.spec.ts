// SPDX-License-Identifier: AGPL-3.0-or-later
// Knowledge synthesis through MCP: `similar`, the filters `--outside` and `--without-tag` on `similar`, `semsearch` and
// `graph`, and the system skill `synthesize-knowledge` (docs/superpowers/specs/2026-11-22-knowledge-synthesis-design.md,
// #304). Each test works below `/Work/<folder>/` and scopes every `similar` to it, as other tests seed the same texts into
// Work (a copy scores 1.00); the default scope (every vault) is asked with notes on glassblowing, a topic no other test
// writes, and only those are asserted on. A vault of this file's own (made in `beforeAll`, deleted in `afterAll`) holds
// the notes of another vault and an archive; its notes are written through the web app's API, as the agent's key has
// Work only, and read through the console. Rankings are asserted only where they are clear by meaning (arctic-m on
// these texts: the dive texts score 0.5 and more with each other, a dive and a cake text about 0).
//
// Promises in scope, and the test that covers each:
// §3.1, §5 `similar <note> [scope]`: the heading `similar to <note>, below <scope>: N notes`, one line per hit
//    `N. path#Section  lines a-b  score s  distance` and the snippet below it, the footer
//    → "similar: one hit per note, its best section …"; headings with nothing below them don't count (a heading-only
//    note, indexed, is no hit) → the same test; the default scope is every vault, a source may lie outside the scope
//    → "similar: the default scope is every vault …"; the heading's words for it (§5: "below every vault")
//    → "similar: the heading of the default scope says 'below every vault'" (a finding).
// §3.2, §3.3 one hit per note, its best section (best chunk pair, not a mean), the source left out
//    → "similar: one hit per note, its best section …".
// §3.3 a source section uses only that section's chunks → "similar: a #section as the source …"; the first section
//    below a note's title as the source → "similar: the first section of a note as the source …" (a finding);
//    `--between` with a section as A → "similar: a #section as the source …".
// §3.4, §5 `--between a c`: by the lower score, both scores and both distances shown, the heading names both
//    → "similar --between: a note close to both …".
// §3.5 link distances `1 step`, `N steps`, `not connected`, `other vault`, links either way, never through attachments or
//    links to nothing, `--without-tag` (nested tags too) and the archive rule applying to the walk
//    → "similar: link distances …".
// §3.6 `--unlinked` leaves out distance 1 only → "similar --unlinked: …".
// §3.7 `--outside` (a folder or a note, repeatable, at most 50), `--without-tag` (an unknown tag no error), `-n N` (1 to 50)
//    → "similar: --outside, --without-tag and -n …"; `--archive` (the archive left out by default, a scope inside it
//    always taking it in) → "similar: the archive is left out …".
// §2 non-goal "PDF pages as similar hits: notes only for now" → "semsearch --outside and --without-tag …" (a PDF beside
//    the notes is never a `similar` hit).
// §6 `semsearch --outside P` (notes and PDF pages), `semsearch --without-tag T` (PDF pages stay)
//    → "semsearch --outside and --without-tag …".
// §6 `graph --without-tag T` in every mode (local, --path, --orphans, --hubs, --unresolved), the ends of `--path` kept → "graph --without-tag: …" (no search needed).
// §7, §3.9 `synthesize-knowledge` a system skill: in the handshake's list and `skills ls`, read with `skills cat`; its
//    two modes, the commands of each, `--without-tag synthesis/hypothesis`, at most five hypotheses, why each could be
//    wrong, one report note (`#synthesis/hypothesis`, `source: synthesized`, a review date, `[!inferred]`, B links to
//    their sections), A and C never changed → "synthesize-knowledge: …" (no search needed).
// §2 anti-goal "nothing sent to an embedding provider": the footer says so → "similar: one hit per note …".
// Left out:
// §3.8 a source not indexed yet ("… isn't indexed yet; try again in a while, or semsearch for its topic meanwhile") and
//    the pending notes counted in the footer: a note is embedded within a second or two of its write on this stack, so
//    asking at once reaches the error only by chance (the first-section finding above shows the message's form).
// §3.8, §2 other-model notes counted in the footer, never compared: needs a vault on another provider, `@tensorx`.
// §4 `VectorsAsync` and the cost: server internals, xUnit (`SimilarTests`). §3.9's hand-test list: not in the product.
import { randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { Api, seed, type Mcp } from "../support/api";
import { fixturesDir, secrets } from "../support/env";
import { nextIndex, waitIndexed } from "../support/search";
import { reservePassword } from "../support/session";
import { fixtureTree } from "../support/test";
import { expect, test } from "./fixtures";

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

// Two sections of about 200 words each, so each is a chunk of its own (Chunker.cs merges up to 250 words).
const cakeLong = `Cream the soft butter with the caster sugar until it is pale and fluffy, which takes a good five minutes with a wooden spoon or two with a mixer, then beat in the eggs one by one so that the batter doesn't curdle.

Sift the flour together with the cocoa powder, the baking powder and a pinch of salt, and fold it into the butter and eggs in three parts, alternating with a little milk, until the batter is smooth and falls thickly from the spoon.

Pour the batter into a round tin lined with baking paper and bake it in the middle of the oven at 180 degrees for about forty minutes, until a skewer pushed into the centre comes out clean and the top springs back when pressed lightly.

Let the cake cool in its tin for ten minutes, then turn it out onto a rack. Once it is cold, melt dark chocolate with a little cream over a pan of hot water and pour the icing over the top, letting it run down the sides.`;
const diveLong = `On a deep dive the body of the diver takes up nitrogen from the breathing gas, and the deeper and longer the dive, the more nitrogen dissolves into the blood and the tissues under the growing pressure of the water above.

Ascending too fast lets that dissolved nitrogen come out of solution as bubbles in the tissues and the blood, which is decompression sickness: pain in the joints, numbness, and in the worst cases paralysis or death. The bends, as divers call it, was first seen in the workers of caissons and bridge foundations.

So the diver ascends slowly, at no more than nine metres a minute, and makes decompression stops at set depths, waiting at each stop until enough nitrogen has left the tissues to go on up safely. A safety stop of three minutes at five metres ends almost every recreational dive, even one within the no-decompression limits.

Dive tables and dive computers model the nitrogen loading of several tissue compartments, each with its own half-time, and tell the diver how long the stops must be and at which depths they lie.`;
const tables =
  "A dive table gives the no-decompression limit for each depth: how long a diver can stay down and still ascend without decompression stops, because the nitrogen taken up stays below the limit.\n\nFor a repetitive dive the table adds the residual nitrogen of the first dive, reduced by the surface interval.";
const cake =
  "Cream the soft butter with the sugar until pale, beat in the eggs one by one, then fold in the flour, the cocoa powder and a pinch of salt.\n\nPour the batter into a lined tin and bake it in the oven at 180 degrees for about forty minutes, until a skewer comes out clean.";
/** A note of two sections: a cake under its title, then the dive (lines 13-21). */
const twoSections = (title: string, cakeText = cakeLong, diveText = diveLong) => `# ${title}\n\n## Ovens\n\n${cakeText}\n\n## Nitrogen\n\n${diveText}\n`;

/** Names below `/Work/<folder>/` carrying the folder's random part, as links resolve vault-wide. */
function names(folder: string) {
  const id = folder.slice(-6);
  return {
    id,
    path: (title: string) => `/Work/${folder}/${title} ${id}.md`,
    link: (title: string) => `[[${title} ${id}]]`,
  };
}

// What each test wrote in Work, removed after it (there is no `rm -r`), and in the file's vault.
let written: string[] = [];
let writtenThere: string[] = [];

async function write(mcp: Mcp, path: string, content: string) {
  written.push(path);
  const answer = await mcp.call("write", { path, content });
  expect(answer.isError, answer.text).toBe(false);
}

async function writeThere(api: Api, path: string, content: string) {
  writtenThere.push(path);
  await api.putNote(path, content);
}

type Hit = { rank: number; path: string; address: string; lines: string; score: number; scores?: [number, number]; distance: string; snippet: string };

/** `similar`'s answer: the heading, the hits with their snippets, the footer. */
function parse(text: string) {
  const all = text.split("\n");
  const [heading, footer] = [all[0], all[all.length - 1]];
  const hits: Hit[] = [];
  for (let i = 1; i < all.length - 1; i += 2) {
    const m = /^(\d+)\. (.+?\.md)(#.*?)?  lines (\d+-\d+)  score (-?\d\.\d\d)(?: \((-?\d\.\d\d) to A, (-?\d\.\d\d) to C\))?  (.+)$/.exec(all[i]);
    if (!m) throw new Error(`not a hit line: ${all[i]}`);
    hits.push({
      rank: Number(m[1]),
      path: m[2],
      address: m[3] ?? "",
      lines: m[4],
      score: Number(m[5]),
      scores: m[6] ? [Number(m[6]), Number(m[7])] : undefined,
      distance: m[8],
      snippet: all[i + 1],
    });
  }
  return { heading, hits, footer };
}

async function similar(run: (command: string) => Promise<{ text: string; isError: boolean }>, args: string) {
  const answer = await run(`similar ${args}`);
  expect(answer.isError, answer.text).toBe(false);
  return parse(answer.text);
}

const distances = (hits: Hit[]) => Object.fromEntries(hits.map((h) => [h.path, h.distance]));
const footer = '(by the stored embeddings, nothing sent to an embedding provider; distance counts links either way; cat "path#Section" reads one)';

// waitIndexed's bound for the first index on a fresh stack (180 s) needs room within the test.
test.describe.configure({ timeout: 240_000 });

// The file's own vault: another vault for `other vault`, and an archive no other test writes to.
let vault = "";

test.beforeAll(async () => {
  test.setTimeout(240_000);
  const api = await Api.as("alice");
  try {
    vault = `Synthesis-${randomBytes(3).toString("hex")}`;
    await api.json("POST", "/api/vaults", { name: vault });
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

test.afterEach(async ({ mcp, aliceApi }) => {
  if (written.length > 0) await mcp.sh(`rm ${written.map((p) => `"${p}"`).join(" ")}`);
  for (const path of writtenThere) await aliceApi.deleteNote(path);
  written = [];
  writtenThere = [];
});

test("similar: one hit per note, its best section, the source left out, the heading and the footer @synthesis @search", async ({ mcp, folder, aliceApi }) => {
  const n = names(folder);
  const [source, two, table, cakeNote] = [n.path("Decompression"), n.path("Two"), n.path("Tables"), n.path("Cake")];
  await write(mcp, source, `# Decompression\n\n${diveLong}\n`);
  // A cake under its title, then a dive: close to the source by its second section only.
  await write(mcp, two, twoSections(`Two ${n.id}`, cakeLong, diveLong.replace("first seen", "once seen")));
  await write(mcp, table, `# Tables\n\n${tables}\n`);
  await write(mcp, cakeNote, `# Cake\n\n${cake}\n`);
  // Headings only, on the source's topic: indexed (it answers as a source), never a hit.
  await write(mcp, n.path("Hollow"), "# Decompression stops and nitrogen\n\n## The ascent rate of divers\n");
  await waitIndexed(aliceApi, written);

  const { heading, hits, footer: last } = await similar(mcp.sh.bind(mcp), `"${source}" /Work/${folder}`);
  expect(heading).toBe(`similar to ${source}, below /Work/${folder}: 3 notes`);
  // Each note once, the source not at all; the two-section note by its dive section, the best of its pair.
  expect(hits.map((h) => `${h.rank}. ${h.path}${h.address}  lines ${h.lines}  ${h.distance}`)).toEqual([
    `1. ${two}#Two ${n.id}#Nitrogen  lines 13-21  not connected`,
    `2. ${table}#Tables  lines 1-5  not connected`,
    `3. ${cakeNote}#Cake  lines 1-5  not connected`,
  ]);
  expect(hits.map((h) => h.score), "the best first").toEqual([...hits.map((h) => h.score)].sort((a, b) => b - a));
  // The snippet is the start of the hit's section, cut with an ellipsis.
  expect(hits[0].snippet).toMatch(/^ {3}On a deep dive the body of the diver takes up nitrogen .+…$/);
  expect(hits[1].snippet).toMatch(/^ {3}A dive table gives the no-decompression limit .+…$/);
  expect(last).toBe(footer);
  // `cat "path#Section"`, as the footer says, reads the hit's section.
  const section = await mcp.sh(`cat "${two}#Two ${n.id}#Nitrogen"`);
  expect(section.isError, section.text).toBe(false);
  // The section, then cat's version and link lines.
  expect(section.text.replace(/\n\nversion of [^\n]+\n\nlink to [^\n]+$/, "")).toBe(`## Nitrogen\n\n${diveLong.replace("first seen", "once seen")}`);
});

test("similar: a #section as the source uses only that section @synthesis @search", async ({ mcp, folder, aliceApi }) => {
  const n = names(folder);
  const [source, table, cakeNote] = [n.path("Source"), n.path("Tables"), n.path("Cake")];
  await write(mcp, source, twoSections(`Source ${n.id}`));
  await write(mcp, table, `# Tables\n\n${tables}\n`);
  await write(mcp, cakeNote, `# Cake\n\n${cake}\n`);
  await waitIndexed(aliceApi, written);

  // The whole note: the cake text is the closest to its cake.
  const whole = await similar(mcp.sh.bind(mcp), `"${source}" /Work/${folder}`);
  expect(whole.hits.map((h) => h.path).sort()).toEqual([cakeNote, table].sort());
  // Its dive section only: the dive table first, the cake last.
  const section = await similar(mcp.sh.bind(mcp), `"${source}#Nitrogen" /Work/${folder}`);
  expect(section.heading).toBe(`similar to ${source}#Nitrogen, below /Work/${folder}: 2 notes`);
  expect(section.hits.map((h) => h.path)).toEqual([table, cakeNote]);
  expect(section.footer).toBe(footer);
  // A section as A of --between.
  const between = await similar(mcp.sh.bind(mcp), `--between "${source}#Nitrogen" "${table}" /Work/${folder}`);
  expect(between.heading).toBe(`between A = ${source}#Nitrogen and C = ${table}, below /Work/${folder}: 1 note`);
  expect(between.hits.map((h) => h.path)).toEqual([cakeNote]);
});

test("similar: the first section of a note as the source is ranked by its text @synthesis @search", async ({ mcp, folder, aliceApi }) => {
  const n = names(folder);
  const [source, table, cakeNote] = [n.path("Source"), n.path("Tables"), n.path("Cake")];
  await write(mcp, source, twoSections(`Source ${n.id}`));
  await write(mcp, table, `# Tables\n\n${tables}\n`);
  await write(mcp, cakeNote, `# Cake\n\n${cake}\n`);
  // The note is indexed: its second section answers (the test above).
  await waitIndexed(aliceApi, written);
  const second = await mcp.sh(`similar "${source}#Nitrogen" /Work/${folder}`);
  expect(second.isError, second.text).toBe(false);

  const first = await mcp.sh(`similar "${source}#Ovens" /Work/${folder}`);
  expect(first, "M-01: the note's first section, ## Ovens, as the source").toEqual({ text: expect.stringMatching(/^similar to /), isError: false });
  expect(parse(first.text).hits.map((h) => h.path)).toEqual([cakeNote, table]);
});

test("similar --between: a note close to both ranks above one close to only one; both scores and distances @synthesis @search", async ({ mcp, folder, aliceApi }) => {
  const n = names(folder);
  const [a, c, mix, hub, dive, bread] = ["Tables", "Cake", "Mix", "Hub", "Dive", "Bread"].map(n.path);
  // A → Hub → Mix: Mix is two steps from A and not connected to C.
  await write(mcp, a, `# Tables\n\n${tables} See ${n.link("Hub")}.\n`);
  await write(mcp, c, `# Cake\n\n${cake}\n`);
  await write(mcp, hub, `# Hub\n\nTo read later: ${n.link("Mix")}.\n`);
  // One chunk holding both: the cake baked on the dive boat between two dives.
  await write(mcp, mix, `# Mix\n\n${cake.replace("pinch", "spoon")}\n\n${tables.replace("first dive", "morning dive")}\n`);
  await write(mcp, dive, `# Dive\n\nOn a deep dive the body takes up nitrogen under pressure; ascending too fast lets it form bubbles, so the diver makes decompression stops.\n`);
  await write(mcp, bread, `# Bread\n\nFeed the sourdough starter the evening before, mix it with flour, water and salt, let the dough rise and bake it in a hot oven.\n`);
  await waitIndexed(aliceApi, written);

  const { heading, hits, footer: last } = await similar(mcp.sh.bind(mcp), `--between "${a}" "${c}" /Work/${folder}`);
  expect(heading).toBe(`between A = ${a} and C = ${c}, below /Work/${folder}: 4 notes`);
  expect(hits[0].path, "the note close to both first").toBe(mix);
  expect(hits.map((h) => h.path).sort()).toEqual([bread, dive, hub, mix].sort());
  expect(hits[0].distance).toBe("2 steps from A, not connected to C");
  expect(distances(hits)).toMatchObject({ [hub]: "1 step from A, not connected to C", [dive]: "not connected from A, not connected to C" });
  // Each hit's score is the lower of its two.
  for (const hit of hits) expect(hit.score, `${hit.path}: the lower of ${hit.scores}`).toBe(Math.min(...hit.scores!));
  expect(last).toBe(footer);
  expect(await mcp.sh(`similar --between "${a}"`)).toEqual({
    text: "similar: --between needs two notes: similar --between /Work/A.md /Work/C.md",
    isError: true,
  });
});

/**
 * S → A → B; X → S; S → H (tagged synthesis/hypothesis) → D; S and F embed one image; S and G link one missing note;
 * C stands alone; S → Arch (in the archive) → T. And O in the file's vault.
 */
async function linkGraph(mcp: Mcp, api: Api, folder: string) {
  const n = names(folder);
  const p = n.path;
  const png = `/Work/${folder}/pixel ${n.id}.png`;
  const arch = `/Work/_archive/${folder}/Arch ${n.id}.md`;
  written.push(png);
  await api.putFile(png, readFileSync(join(fixturesDir, "tags/smoke/pixel.png")));
  const say = (what: string) => `The note about ${what}.`;
  await write(mcp, p("S"), `# S\n\n${say("the start")} ${n.link("A")} ${n.link("H")} ${n.link("Arch")} ${n.link("Nowhere")}\n\n![[pixel ${n.id}.png]]\n`);
  await write(mcp, p("A"), `# A\n\n${say("apples")} ${n.link("B")}\n`);
  await write(mcp, p("B"), `# B\n\n${say("bees")}\n`);
  await write(mcp, p("X"), `# X\n\n${say("x-rays")} ${n.link("S")}\n`);
  await write(mcp, p("H"), `---\ntags: [synthesis/hypothesis]\nsource: synthesized\n---\n# H\n\n${say("a hypothesis")} ${n.link("D")}\n`);
  await write(mcp, p("D"), `# D\n\n${say("dunes")}\n`);
  await write(mcp, p("F"), `# F\n\n${say("frames")}\n\n![[pixel ${n.id}.png]]\n`);
  await write(mcp, p("G"), `# G\n\n${say("ghosts")} ${n.link("Nowhere")}\n`);
  await write(mcp, p("C"), `# C\n\n${say("canals")}\n`);
  await write(mcp, arch, `# Arch\n\n${say("the archive")} ${n.link("T")}\n`);
  await write(mcp, p("T"), `# T\n\n${say("towers")}\n`);
  const other = `/${vault}/${folder}/O ${n.id}.md`;
  await writeThere(api, other, `# O\n\n${say("oceans")}\n`);
  await waitIndexed(api, [...written.filter((w) => w.endsWith(".md") && w !== arch), other]);
  return { p, other };
}

test("similar: link distances, either way, never through attachments, missing notes, tagged or archived notes @synthesis @search", async ({ mcp, folder, aliceApi }) => {
  const { p, other } = await linkGraph(mcp, aliceApi, folder);
  const scope = `/Work/${folder}`;

  const plain = await similar(mcp.sh.bind(mcp), `"${p("S")}" ${scope}`);
  expect(plain.heading).toBe(`similar to ${p("S")}, below ${scope}: 9 notes`);
  expect(distances(plain.hits)).toEqual({
    [p("A")]: "1 step",
    [p("X")]: "1 step",
    [p("H")]: "1 step",
    [p("B")]: "2 steps",
    [p("D")]: "2 steps",
    [p("F")]: "not connected",
    [p("G")]: "not connected",
    [p("C")]: "not connected",
    [p("T")]: "not connected",
  });
  // The hypothesis isn't walked through (nor shown), with its tag or a parent of it.
  for (const tag of ["synthesis/hypothesis", "synthesis"]) {
    const without = await similar(mcp.sh.bind(mcp), `"${p("S")}" ${scope} --without-tag ${tag}`);
    expect(without.hits.map((h) => h.path), `--without-tag ${tag}`).not.toContain(p("H"));
    expect(distances(without.hits), `--without-tag ${tag}`).toMatchObject({ [p("D")]: "not connected", [p("B")]: "2 steps" });
  }
  // The archived note is walked through with --archive only.
  const archive = await similar(mcp.sh.bind(mcp), `"${p("S")}" ${scope} --archive`);
  expect(distances(archive.hits)).toMatchObject({ [p("T")]: "2 steps" });
  // Links never cross vaults.
  const there = await similar(aliceApi.console.bind(aliceApi), `"${p("S")}" /${vault}/${folder}`);
  expect(there.heading).toBe(`similar to ${p("S")}, below /${vault}/${folder}: 1 note`);
  expect(distances(there.hits)).toEqual({ [other]: "other vault" });
});

test("similar --unlinked: leaves out the notes one link away either way, keeps those two away @synthesis @search", async ({ mcp, folder, aliceApi }) => {
  const { p } = await linkGraph(mcp, aliceApi, folder);
  const { heading, hits } = await similar(mcp.sh.bind(mcp), `"${p("S")}" /Work/${folder} --unlinked`);
  expect(heading).toBe(`similar to ${p("S")}, below /Work/${folder}: 6 notes`);
  expect(distances(hits)).toEqual({
    [p("B")]: "2 steps",
    [p("D")]: "2 steps",
    [p("F")]: "not connected",
    [p("G")]: "not connected",
    [p("C")]: "not connected",
    [p("T")]: "not connected",
  });
});

test("similar: --outside, --without-tag and -n leave out what they say @synthesis @search", async ({ mcp, folder, aliceApi }) => {
  const base = `/Work/${folder}`;
  const files = fixtureTree("tags/meaning", base);
  written.push(...files.map((f) => f.path));
  await seed(aliceApi, files);
  await waitIndexed(aliceApi, written);
  const at = (name: string) => `${base}/${name}.md`;
  const source = at("Diving/Decompression");
  const run = mcp.sh.bind(mcp);

  const plain = await similar(run, `"${source}" ${base}`);
  expect(plain.hits.slice(0, 2).map((h) => h.path).sort(), "the dive notes first").toEqual([at("Diving/Dive computer"), at("Diving/Dive tables")]);
  expect(plain.hits).toHaveLength(5);

  const outside = await similar(run, `"${source}" ${base} --outside ${base}/Diving`);
  expect(outside.heading).toBe(`similar to ${source}, below ${base}: 3 notes`);
  expect(outside.hits.map((h) => h.path).sort()).toEqual([at("Baking/Chocolate cake"), at("Baking/Sourdough bread"), at("Garden/Tomatoes")]);
  const twice = await similar(run, `"${source}" ${base} --outside ${base}/Diving --outside ${base}/Garden`);
  expect(twice.hits.map((h) => h.path).sort()).toEqual([at("Baking/Chocolate cake"), at("Baking/Sourdough bread")]);

  // A note's path leaves out that note.
  const note = await similar(run, `"${source}" ${base} --outside "${at("Diving/Dive tables")}"`);
  expect(note.hits.map((h) => h.path).sort()).toEqual(plain.hits.map((h) => h.path).filter((path) => path !== at("Diving/Dive tables")).sort());

  // A tag no note carries leaves out nothing, and is no error.
  const unknown = await similar(run, `"${source}" ${base} --without-tag nosuch-${folder.slice(-6)}`);
  expect(unknown.hits.map((h) => h.path)).toEqual(plain.hits.map((h) => h.path));

  const two = await similar(run, `"${source}" ${base} -n 2`);
  expect(two.heading).toBe(`similar to ${source}, below ${base}: 2 notes`);
  expect(two.hits.map((h) => h.path)).toEqual(plain.hits.slice(0, 2).map((h) => h.path));
  expect(await run(`similar "${source}" ${base} -n 51`)).toEqual({ text: "similar: -n must be 1 to 50", isError: true });
  const many = Array.from({ length: 51 }, (_, i) => `--outside ${base}/x${i}`).join(" ");
  expect(await run(`similar "${source}" ${base} ${many}`)).toEqual({
    text: "similar: --outside and --without-tag can each be given at most 50 times",
    isError: true,
  });
});

test("similar: the archive is left out unless asked for or inside the scope @synthesis @search", async ({ mcp, folder, aliceApi }) => {
  const n = names(folder);
  const source = n.path("Source");
  const [plain, old] = [`/${vault}/${folder}/Plain.md`, `/${vault}/_archive/${folder}/Old.md`];
  await write(mcp, source, `# Source\n\n${diveLong}\n`);
  await writeThere(aliceApi, plain, `# Plain\n\n${tables}\n`);
  await writeThere(aliceApi, old, `# Old\n\n${diveLong.replace("first seen", "once seen")}\n`);
  await waitIndexed(aliceApi, [source, plain, old]);
  const run = aliceApi.console.bind(aliceApi);

  const paths = async (args: string) => (await similar(run, args)).hits.map((h) => h.path);
  expect(await paths(`"${source}" /${vault}`), "the vault without its archive").toEqual([plain]);
  expect((await paths(`"${source}" /${vault} --archive`)).sort(), "--archive").toEqual([old, plain].sort());
  expect(await paths(`"${source}" /${vault}/_archive/${folder}`), "a scope inside the archive").toEqual([old]);
});

test("similar: the default scope is every vault, and the source may lie outside the scope @synthesis @search", async ({ mcp, folder, aliceApi }) => {
  // Glassblowing: no other test writes about it, so these notes are the only close ones in any vault.
  const n = names(folder);
  const [glass, gaffer] = [n.path("Glass"), n.path("Gaffer")];
  const furnace = `/${vault}/${folder}/Furnace.md`;
  await write(mcp, glass, "# Glass\n\nThe glassblower gathers molten glass on the end of a blowpipe from the furnace and blows a bubble into it, turning the pipe all the time.\n");
  await write(mcp, gaffer, "# Gaffer\n\nThe gaffer shapes the gather of hot glass on the marver and reheats it in the glory hole before blowing it out into a goblet.\n");
  await writeThere(aliceApi, furnace, "# Furnace\n\nThe glass furnace melts sand, soda ash and lime into molten glass; the glassblower takes a gather from it with the blowpipe.\n");
  await waitIndexed(aliceApi, [glass, gaffer, furnace]);

  const { heading, hits } = await similar(aliceApi.console.bind(aliceApi), `"${glass}" -n 20`);
  expect(heading).toMatch(new RegExp(`^similar to ${escape(glass)}, (?:in|below) every vault: \\d+ notes?$`));
  expect(hits.map((h) => h.path)).not.toContain(glass);
  expect(distances(hits)).toMatchObject({ [gaffer]: "not connected", [furnace]: "other vault" });
  // A scope that doesn't hold the source.
  const there = await similar(aliceApi.console.bind(aliceApi), `"${glass}" /${vault}/${folder}`);
  expect(there.heading).toBe(`similar to ${glass}, below /${vault}/${folder}: 1 note`);
  expect(distances(there.hits)).toEqual({ [furnace]: "other vault" });
});

test("similar: the heading of the default scope says 'below every vault' @synthesis @search", async ({ mcp, folder, aliceApi }) => {
  const glass = names(folder).path("Glass");
  await write(mcp, glass, "# Glass\n\nThe glassblower gathers molten glass on the end of a blowpipe and blows a bubble into it.\n");
  await waitIndexed(aliceApi, [glass]);
  const { heading } = await similar(mcp.sh.bind(mcp), `"${glass}"`);
  expect(heading, "M-02: the default scope's heading").toMatch(new RegExp(`^similar to ${escape(glass)}, below every vault: \\d+ notes?$`));
});

test("semsearch --outside and --without-tag: notes and PDF pages below a path go, tagged notes go and PDF pages stay @synthesis @search", async ({ mcp, folder, aliceApi }) => {
  const base = `/Work/${folder}`;
  const files = fixtureTree("tags/meaning", base);
  written.push(...files.map((f) => f.path));
  await seed(aliceApi, files);
  const pdf = `${base}/Manual/manual.pdf`;
  const tagged = `${base}/Tagged.md`;
  written.push(pdf);
  await aliceApi.putFile(pdf, readFileSync(join(fixturesDir, "tags/recent/Pump/manual.pdf")));
  await write(mcp, tagged, "---\ntags: [hypothesis/dive]\n---\n# Tagged\n\nDecompression stops let the nitrogen leave the tissues before the diver ascends further.\n");
  await waitIndexed(aliceApi, written.filter((w) => w.endsWith(".md")));
  const hitPaths = async (args: string) => {
    const answer = await mcp.sh(`semsearch --meaning -l -n 20 nitrogen and decompression stops ${base} ${args}`);
    expect(answer.isError, answer.text).toBe(false);
    return answer.text.split("\n").flatMap((l) => /^\d+\. (.+?\.(?:md|pdf))(?:#| p\d)/.exec(l)?.[1] ?? []);
  };
  // The PDF's pages are indexed after its upload: until semsearch ranks them.
  await expect.poll(async () => (await hitPaths("")).includes(pdf), { message: "the PDF's pages are ranked", timeout: nextIndex, intervals: [1000] }).toBe(true);

  const all = await hitPaths("");
  const dive = files.map((f) => f.path).filter((path) => path.startsWith(`${base}/Diving/`));
  expect(all).toEqual(expect.arrayContaining([...dive, tagged, pdf]));
  const outside = await hitPaths(`--outside ${base}/Diving`);
  expect(outside.filter((path) => path.startsWith(`${base}/Diving/`)), "--outside: the folder's notes").toEqual([]);
  expect(outside).toEqual(expect.arrayContaining([tagged, pdf, `${base}/Garden/Tomatoes.md`]));
  expect(await hitPaths(`--outside ${base}/Manual`), "--outside: the folder's PDF pages").not.toContain(pdf);
  const without = await hitPaths("--without-tag hypothesis");
  expect(without, "--without-tag: the tagged note (by a parent of its tag)").not.toContain(tagged);
  expect(without, "--without-tag: PDF pages stay").toEqual(expect.arrayContaining([...dive, pdf]));

  // similar finds notes only (§2): the PDF beside them is never a hit.
  const near = await similar(mcp.sh.bind(mcp), `"${base}/Diving/Decompression.md" ${base}`);
  expect(near.footer).toBe(footer);
  expect(near.hits.map((h) => h.path)).not.toContain(pdf);
  expect(near.hits).toHaveLength(6);
});

test("graph --without-tag: a tagged note leaves the local graph, the paths and the lists; the ends of --path stay @synthesis", async ({ mcp, folder }) => {
  const n = names(folder);
  const p = n.path;
  // S → H (tagged) → T, S → B; Lone is an orphan with the tag.
  await write(mcp, p("S"), `# S\n\n${n.link("H")} ${n.link("B")}\n`);
  await write(mcp, p("H"), `---\ntags: [synthesis/hypothesis]\n---\n# H\n\n${n.link("T")} ${n.link("Gone")}\n`);
  await write(mcp, p("B"), "# B\n");
  await write(mcp, p("T"), "# T\n");
  await write(mcp, p("Lone"), "---\ntags: [synthesis/hypothesis]\n---\n# Lone\n");

  expect(await mcp.sh(`graph "${p("S")}"`)).toEqual({
    text: [`${p("S")}, depth 1, both directions: 3 notes`, `1  ${p("S")} -> ${p("B")}`, `1  ${p("S")} -> ${p("H")}`].join("\n"),
    isError: false,
  });
  expect(await mcp.sh(`graph "${p("S")}" --without-tag synthesis`)).toEqual({
    text: [`${p("S")}, depth 1, both directions: 2 notes`, `1  ${p("S")} -> ${p("B")}`].join("\n"),
    isError: false,
  });
  expect(await mcp.sh(`graph --path "${p("S")}" "${p("T")}"`)).toEqual({
    text: `${p("S")} to ${p("T")}, 2 steps, in either direction: 1 path\n${p("S")} -> ${p("H")} -> ${p("T")}`,
    isError: false,
  });
  expect(await mcp.sh(`graph --path "${p("S")}" "${p("T")}" --without-tag synthesis/hypothesis`)).toEqual({
    text: `${p("S")} and ${p("T")} are not connected at all (no path of any length)`,
    isError: false,
  });
  // An end that carries the tag stays, as with --tag.
  expect(await mcp.sh(`graph --path "${p("H")}" "${p("T")}" --without-tag synthesis/hypothesis`)).toEqual({
    text: `${p("H")} to ${p("T")}, 1 step, in either direction: 1 path\n${p("H")} -> ${p("T")}`,
    isError: false,
  });
  expect(await mcp.sh(`graph --orphans /Work/${folder}`)).toEqual({ text: p("Lone"), isError: false });
  expect(await mcp.sh(`graph --orphans /Work/${folder} --without-tag synthesis`)).toEqual({ text: "(no notes without links)", isError: false });
  expect(await mcp.sh(`graph --hubs /Work/${folder}`)).toEqual({
    text: [`3  ${p("H")}  (1 in, 2 out)`, `2  ${p("S")}  (0 in, 2 out)`, `1  ${p("B")}  (1 in, 0 out)`, `1  ${p("T")}  (1 in, 0 out)`, `0  ${p("Lone")}  (0 in, 0 out)`].join("\n"),
    isError: false,
  });
  // The tagged notes leave the list, and their links leave the counts, as --unresolved counts kept notes only (M-05,
  // decided: the knowledge-synthesis spec §6, ADR 0028).
  expect(await mcp.sh(`graph --hubs /Work/${folder} --without-tag synthesis`)).toEqual({
    text: [`1  ${p("B")}  (1 in, 0 out)`, `1  ${p("S")}  (0 in, 1 out)`, `0  ${p("T")}  (0 in, 0 out)`].join("\n"),
    isError: false,
  });
  expect(await mcp.sh(`graph --unresolved /Work/${folder}`)).toEqual({ text: `Gone ${n.id}, linked from 1 note:\n  ${p("H")}:6`, isError: false });
  expect(await mcp.sh(`graph --unresolved /Work/${folder} --without-tag synthesis`)).toEqual({ text: "(no unresolved links)", isError: false });
});

test("synthesize-knowledge: a system skill in the handshake and skills ls, read with skills cat @synthesis", async ({ mcp }) => {
  const head = "synthesize-knowledge (system): Find what the user's notes know together but never say";
  const { instructions = "" } = await mcp.initialize();
  expect(instructions.split("\n").some((l) => l.startsWith(`- ${head}`)), "listed in the handshake").toBe(true);
  const list = await mcp.sh("skills ls");
  expect(list.isError, list.text).toBe(false);
  expect(list.text.split("\n").filter((l) => l.startsWith("synthesize-knowledge "))).toEqual([expect.stringMatching(new RegExp(`^${escape(head)}`))]);

  const skill = await mcp.sh("skills cat synthesize-knowledge");
  expect(skill.isError, skill.text).toBe(false);
  expect(await mcp.sh("skills cat system:synthesize-knowledge")).toEqual(skill);
  const text = skill.text;
  expect(text).toMatch(/^---\nname: synthesize-knowledge\ndescription: /);
  // The two modes, and the commands of each.
  expect(text).toContain("Two modes. **Bridge**");
  expect(text).toContain("**Explore**");
  expect(text).toContain("Bridge: `similar --between <A> <C> --without-tag synthesis/hypothesis`");
  expect(text).toContain("`graph --path <A> <C> --without-tag synthesis/hypothesis`");
  expect(text).toContain("Explore: `similar <A> --unlinked --outside <A's silo> --without-tag synthesis/hypothesis`");
  expect(text).toContain("`semsearch --outside <A's silo> --without-tag synthesis/hypothesis <B term>`");
  // At most five, each with why it could be wrong; one report note, tagged and dated; the inference marked; A and C kept.
  expect(text).toContain("Keep at most five hypotheses");
  expect(text).toContain("say why it could be wrong");
  expect(text).toContain("Write one report note");
  expect(text).toContain("`source: synthesized` and `tags: [synthesis/hypothesis]`");
  expect(text).toContain("a review date two weeks out");
  expect(text).toContain("> [!inferred]");
  expect(text).toContain("each B link as a link to the section it stands in");
  expect(text).toContain("Never change A or C and never add links to them");
});
