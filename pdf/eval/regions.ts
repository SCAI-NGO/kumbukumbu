// SPDX-License-Identifier: AGPL-3.0-or-later

// The measure of spec §4.5: are a figure's regions, with labels where the analysis gives them, both
// complete and clean? Run on the corpus of tools/attachment-probes/make_figure_pdfs.py:
//   node eval-dist/eval/regions.js <out> --seeds 1-5 [--baseline] [--verbose]
// Identity comes from the text pdf.js gives: an item holding zq<Letter><digit> is its figure's, one
// made wholly of the body's words is body text, one starting "Figure N" is a caption.

import { existsSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { analyse } from "../src/analyse.js";
import { textBoxes } from "../src/boxes.js";
import { type Box, type PageType, type Region, RULES_VERSION } from "../src/classify.js";
import * as fixtures from "../test/fixtures.js";

export const LAYOUTS = [
  "legend-right",
  "legend-below",
  "side-by-side",
  "between-paragraphs",
  "two-columns",
  "long-caption",
  "turned-page",
  "turned-labels",
] as const;
export const CONTROLS = ["control-text", "control-table", "control-logo", "control-photos", "control-scan", "control-empty"] as const;
export const GENERATORS = ["matplotlib", "calc", "pdf-lib"] as const;

export type FigureManifest = { figure: number; token: string; labels: string[]; minTickLabels: number };
export type Manifest = { generator: string; layout: string; page: number; control?: boolean; figures: FigureManifest[] };

/** Share of an item's area that must lie in the judged box for "complete". */
export const COMPLETE_AREA = 0.9;
export const BARS = { overall: 0.9, generator: 0.8, layout: 0.7, figures: 60 } as const;
/** Page types and tight regions are held to those of rules 1, which the baseline must have been made by. */
export const BASELINE_RULES = 1;

// A region as the analysis gives it, with its box of labels once the rule has one.
export type JudgedRegion = Region & { labelled?: Box };
export type Item = Box & { text: string };
export type ItemKind = "token" | "words" | "caption" | "unidentified";
export type Classed = Item & { kind: ItemKind; token?: string };

export const FAILURES = [
  "no region",
  "drawing outside",
  "label outside",
  "label missing",
  "too few tick labels",
  "body text inside",
  "other figure inside",
] as const;
export type Failure = (typeof FAILURES)[number];
const CLEAN_FAILURES: readonly Failure[] = ["body text inside", "other figure inside"];

export type FigureResult = {
  figure: number;
  token: string;
  by: "labelled" | "tight" | "none";
  complete: boolean;
  clean: boolean;
  pass: boolean;
  failures: Failure[];
};

const TOKEN = /zq[A-Z][0-9]/;
const norm = (s: string) => s.replace(/\s+/gu, "");

function kindOf(text: string, body: ReadonlySet<string>): { kind: ItemKind; token?: string } {
  const token = TOKEN.exec(text)?.[0];
  if (token) return { kind: "token", token };
  if (/^\s*Figure \d+/u.test(text)) return { kind: "caption" };
  const words = text.toLowerCase().split(/[\s.,;:]+/u).filter(Boolean);
  if (words.length > 0 && words.every((w) => body.has(w))) return { kind: "words" };
  return { kind: "unidentified" };
}

// Two items next to each other: sharing half a band of y (one line of horizontal text) or of x
// (upright text, or a label wrapped onto a second line), at most a line's thickness apart.
export function neighbours(a: Box, b: Box): boolean {
  const ox = Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x);
  const oy = Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y);
  const gap = Math.max(Math.min(a.w, a.h), Math.min(b.w, b.h));
  return (oy >= 0.5 * Math.min(a.h, b.h) && -ox <= gap) || (ox >= 0.5 * Math.min(a.w, b.w) && -oy <= gap);
}

const centre = (b: Box) => ({ x: b.x + b.w / 2, y: b.y + b.h / 2 });
const holds = (b: Box, p: { x: number; y: number }) => p.x >= b.x && p.x <= b.x + b.w && p.y >= b.y && p.y <= b.y + b.h;
function shareIn(item: Box, box: Box): number {
  const area = item.w * item.h;
  if (!(area > 0)) return holds(box, centre(item)) ? 1 : 0;
  const w = Math.min(item.x + item.w, box.x + box.w) - Math.max(item.x, box.x);
  const h = Math.min(item.y + item.h, box.y + box.h) - Math.max(item.y, box.y);
  return w > 0 && h > 0 ? (w * h) / area : 0;
}
function bounds(boxes: Box[]): Box {
  const x = Math.min(...boxes.map((b) => b.x));
  const y = Math.min(...boxes.map((b) => b.y));
  return { x, y, w: Math.max(...boxes.map((b) => b.x + b.w)) - x, h: Math.max(...boxes.map((b) => b.y + b.h)) - y };
}
function distance(p: { x: number; y: number }, b: Box): number {
  const dx = Math.max(b.x - p.x, 0, p.x - (b.x + b.w));
  const dy = Math.max(b.y - p.y, 0, p.y - (b.y + b.h));
  return Math.hypot(dx, dy);
}

// Classes every item, then gives an unidentified fragment the token of the nearest token item it
// joins on a line: "zqA3" and "north" drawn apart are both zqA3's. Also returns, per token, the
// joined text of each run of fragments that holds it, white space removed, to match labels on.
export function classifyItems(items: Item[], body: ReadonlySet<string>): { items: Classed[]; runs: Map<string, string[]> } {
  const classed: Classed[] = items.map((it) => ({ ...it, ...kindOf(it.text, body) }));
  const joinable = classed.map((c, i) => (c.kind === "token" || c.kind === "unidentified" ? i : -1)).filter((i) => i >= 0);
  const parent = new Map<number, number>(joinable.map((i) => [i, i]));
  const find = (i: number): number => {
    while (parent.get(i) !== i) i = parent.get(i)!;
    return i;
  };
  for (let a = 0; a < joinable.length; a++)
    for (let b = a + 1; b < joinable.length; b++) {
      const [i, j] = [joinable[a]!, joinable[b]!];
      if (neighbours(classed[i]!, classed[j]!)) parent.set(find(i), find(j));
    }
  const groups = new Map<number, number[]>();
  for (const i of joinable) groups.set(find(i), [...(groups.get(find(i)) ?? []), i]);

  const runs = new Map<string, string[]>();
  for (const members of groups.values()) {
    const tokens = members.filter((i) => classed[i]!.kind === "token");
    if (tokens.length === 0) continue;
    for (const i of members) {
      const c = classed[i]!;
      if (c.kind !== "unidentified") continue;
      const p = centre(c);
      const nearest = tokens.reduce((best, t) => (distance(p, classed[t]!) < distance(p, classed[best]!) ? t : best));
      classed[i] = { ...c, kind: "token", token: classed[nearest]!.token };
    }
    const joined = norm(members.map((i) => classed[i]!.text).join(""));
    for (const token of new Set(tokens.map((t) => classed[t]!.token!))) runs.set(token, [...(runs.get(token) ?? []), joined]);
  }
  return { items: classed, runs };
}

const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");

export function judgeFigures(items: Item[], regions: JudgedRegion[], figures: FigureManifest[], body: ReadonlySet<string>) {
  const { items: classed, runs } = classifyItems(items, body);
  const results: FigureResult[] = figures.map((f) => {
    const failures = new Set<Failure>();
    const mine = classed.filter((c) => c.kind === "token" && c.token === f.token);
    const texts = runs.get(f.token) ?? [];
    if (f.labels.some((l) => !texts.some((t) => t.includes(norm(l))))) failures.add("label missing");
    const tick = new RegExp(`${escape(f.token)}[-−]?\\d`, "gu");
    const ticks = texts.reduce((n, t) => n + (t.match(tick)?.length ?? 0), 0);
    if (ticks < f.minTickLabels) failures.add("too few tick labels");

    let by: FigureResult["by"] = "none";
    if (mine.length === 0 || regions.length === 0) failures.add("no region");
    else {
      // The figure's region is the one nearest to the middle of its labels.
      const cs = mine.map(centre);
      const mid = { x: cs.reduce((s, c) => s + c.x, 0) / cs.length, y: cs.reduce((s, c) => s + c.y, 0) / cs.length };
      const region = regions.reduce((best, r) => (distance(mid, r) < distance(mid, best) ? r : best));
      const span = bounds(mine);
      // A region that doesn't even reach the span of the figure's labels is someone else's.
      if (shareIn(region, span) === 0) failures.add("no region");
      const judged = region.labelled ?? region;
      by = failures.has("no region") ? "none" : region.labelled ? "labelled" : "tight";
      // Its drawing: that region, and any other whose centre lies within the figure's labels, as a
      // chart that falls apart into pieces does.
      const drawing = regions.filter((r) => r === region || holds(span, centre(r)));
      if (drawing.some((r) => shareIn(r, judged) < COMPLETE_AREA)) failures.add("drawing outside");
      if (mine.some((c) => shareIn(c, judged) < COMPLETE_AREA)) failures.add("label outside");
      for (const c of classed) {
        if (!holds(judged, centre(c))) continue;
        if (c.kind === "words") failures.add("body text inside");
        if (c.kind === "token" && c.token !== f.token) failures.add("other figure inside");
      }
    }
    const list = FAILURES.filter((k) => failures.has(k));
    const clean = !list.some((k) => CLEAN_FAILURES.includes(k));
    const complete = !list.some((k) => !CLEAN_FAILURES.includes(k));
    return { figure: f.figure, token: f.token, by, complete, clean, pass: complete && clean, failures: list };
  });
  const unidentified = classed.filter((c) => c.kind === "unidentified");
  return { figures: results, unidentified };
}

// Text a box of labels takes in beyond its tight region: on a page with no labels, a body line.
export function bodyLinesInLabelled(items: Item[], regions: JudgedRegion[]): number {
  return items.filter((it) => regions.some((r) => r.labelled && holds(r.labelled, centre(it)) && !holds(r, centre(it)))).length;
}

// ---------------------------------------------------------------- running it

type Measured = { type: PageType; regions: JudgedRegion[]; items: Item[] };

async function measure(bytes: Uint8Array, page: number): Promise<Measured> {
  const a = await analyse(bytes, page);
  const p = a.pages[page - 1]!;
  const boxes = (await textBoxes(bytes, [page])).pages[0]!;
  const items = boxes.items.map((i) => ({ text: boxes.text.slice(i.at, i.end), x: i.x, y: i.y, w: i.w, h: i.h }));
  return { type: p.type, regions: p.regions as JudgedRegion[], items };
}

const FIXTURES: Record<string, () => Promise<Uint8Array>> = {
  textPage: fixtures.textPage,
  chartPage: fixtures.chartPage,
  logoPage: fixtures.logoPage,
  scanPage: () => fixtures.scanPage(false),
  scanPageWithText: () => fixtures.scanPage(true),
  tablePage: fixtures.tablePage,
  whiteBackgroundPage: fixtures.whiteBackgroundPage,
  photoPage: fixtures.photoPage,
  rotatedPage: fixtures.rotatedPage,
  cropBoxPage: fixtures.cropBoxPage,
  emptyPage: fixtures.emptyPage,
  turnedTextPage: fixtures.turnedTextPage,
  rotatedTextPage: fixtures.rotatedTextPage,
};

type Baseline = { rules: number; seeds: number[]; pages: Record<string, { type: PageType; regions: Region[]; control?: boolean }> };
const tight = (rs: JudgedRegion[]): Region[] => rs.map(({ kind, x, y, w, h }) => ({ kind, x, y, w, h }));

export function parseSeeds(s: string): number[] {
  const out: number[] = [];
  for (const part of s.split(",")) {
    const m = /^(\d+)(?:-(\d+))?$/u.exec(part.trim());
    if (!m) throw new Error(`Not a seed list: ${s}`);
    const [a, b] = [Number(m[1]), Number(m[2] ?? m[1])];
    for (let n = a; n <= b; n++) out.push(n);
  }
  return out;
}

type Row = { figures: number; pass: number; complete: number; clean: number };
const row = (): Row => ({ figures: 0, pass: 0, complete: 0, clean: 0 });
const pct = (n: number, d: number) => (d === 0 ? "-" : `${((100 * n) / d).toFixed(1)} %`);

function table(title: string, rows: [string, Row][]): string[] {
  const out = [`${title.padEnd(20)}${"figures".padStart(8)}${"pass".padStart(7)}${"%".padStart(9)}${"complete".padStart(10)}${"clean".padStart(8)}`];
  for (const [name, r] of rows)
    out.push(`${name.padEnd(20)}${String(r.figures).padStart(8)}${String(r.pass).padStart(7)}${pct(r.pass, r.figures).padStart(9)}${String(r.complete).padStart(10)}${String(r.clean).padStart(8)}`);
  return out;
}

async function main(argv: string[]) {
  const out = argv[0];
  const at = argv.indexOf("--seeds");
  if (!out || out.startsWith("--") || at < 0 || !argv[at + 1]) {
    console.error("usage: regions.js <out> --seeds 1-5 [--baseline [--force]] [--verbose]");
    process.exit(2);
  }
  const seeds = parseSeeds(argv[at + 1]!);
  const writeBaseline = argv.includes("--baseline");
  const verbose = argv.includes("--verbose");
  const baselinePath = join(out, "baseline.json");
  if (writeBaseline && existsSync(baselinePath) && !argv.includes("--force")) {
    console.error(`${baselinePath} exists; add --force to write it again.`);
    process.exit(2);
  }

  const cache = new Map<string, Measured>();
  const measured = async (key: string, bytes: () => Promise<Uint8Array> | Uint8Array, page = 1) => {
    if (!cache.has(key)) cache.set(key, await measure(await bytes(), page));
    return cache.get(key)!;
  };

  const byGenerator = new Map<string, Row>(GENERATORS.map((g) => [g, row()]));
  const byLayout = new Map<string, Row>(LAYOUTS.map((l) => [l, row()]));
  const all = row();
  const failures = new Map<Failure, Map<string, number>>(FAILURES.map((k) => [k, new Map()]));
  const unidentified = new Map<string, number>(GENERATORS.map((g) => [g, 0]));
  const judgedBy = { labelled: 0, tight: 0, none: 0 };
  let controls = 0;
  let bodyLines = 0;
  const controlKeys = new Set<string>();
  const lines: string[] = [];

  for (const seed of seeds) {
    const dir = join(out, `seed-${seed}`);
    if (!existsSync(dir)) {
      console.error(`No ${dir}: generate it with tools/attachment-probes/make_figure_pdfs.py first.`);
      process.exit(2);
    }
    const words = JSON.parse(readFileSync(join(dir, "words.json"), "utf8")) as { body: string[] };
    const body = new Set(words.body);
    for (const generator of GENERATORS) {
      const gdir = join(dir, generator);
      if (!existsSync(gdir)) continue;
      for (const file of readdirSync(gdir).filter((f) => f.endsWith(".json")).sort()) {
        const manifest = JSON.parse(readFileSync(join(gdir, file), "utf8")) as Manifest;
        const pdf = join(gdir, file.replace(/\.json$/u, ".pdf"));
        const key = `seed-${seed}/${generator}/${file.replace(/\.json$/u, ".pdf")}`;
        const m = await measured(key, () => new Uint8Array(readFileSync(pdf)), manifest.page);
        if (manifest.control) {
          controls++;
          controlKeys.add(key);
          const n = bodyLinesInLabelled(m.items, m.regions);
          bodyLines += n;
          if (n > 0 && verbose) lines.push(`${key}: ${n} body line(s) in a with-labels region`);
          continue;
        }
        const judged = judgeFigures(m.items, m.regions, manifest.figures, body);
        unidentified.set(generator, (unidentified.get(generator) ?? 0) + judged.unidentified.length);
        if (verbose && judged.unidentified.length > 0)
          lines.push(`${key}: unidentified ${judged.unidentified.map((u) => JSON.stringify(u.text)).join(", ")}`);
        for (const r of judged.figures) {
          judgedBy[r.by]++;
          for (const target of [all, byGenerator.get(generator)!, byLayout.get(manifest.layout) ?? row()]) {
            target.figures++;
            if (r.pass) target.pass++;
            if (r.complete) target.complete++;
            if (r.clean) target.clean++;
          }
          for (const k of r.failures) failures.get(k)!.set(generator, (failures.get(k)!.get(generator) ?? 0) + 1);
          if (verbose && !r.pass) lines.push(`${key} figure ${r.figure} (${r.token}, by ${r.by}): ${r.failures.join(", ")}`);
        }
      }
    }
  }

  // Page types and tight regions against rules 1, on the baseline's seeds and the fixtures.
  const current = async (key: string): Promise<Measured | null> => {
    if (key.startsWith("fixture:")) return measured(key, FIXTURES[key.slice(8)]!);
    const path = join(out, key);
    return existsSync(path) ? measured(key, () => new Uint8Array(readFileSync(path))) : null;
  };
  if (writeBaseline) {
    const keys = [...cache.keys(), ...Object.keys(FIXTURES).map((f) => `fixture:${f}`)];
    const pages: Baseline["pages"] = {};
    for (const key of keys) {
      const m = (await current(key))!;
      pages[key] = { type: m.type, regions: tight(m.regions), ...(controlKeys.has(key) ? { control: true } : {}) };
    }
    writeFileSync(baselinePath, JSON.stringify({ rules: RULES_VERSION, seeds, pages } satisfies Baseline, null, 1) + "\n");
  }
  let compared = 0;
  let typeDiffs = 0;
  let regionDiffs = 0;
  let missing = 0;
  let baseSeeds: number[] = [];
  let baseRules: number | undefined;
  let baseControls = 0;
  const hasBaseline = existsSync(baselinePath);
  if (hasBaseline) {
    const base = JSON.parse(readFileSync(baselinePath, "utf8")) as Baseline;
    baseSeeds = base.seeds;
    baseRules = base.rules;
    for (const [key, want] of Object.entries(base.pages)) {
      const m = await current(key);
      if (!m) {
        missing++;
        continue;
      }
      compared++;
      if (m.type !== want.type) typeDiffs++;
      if (JSON.stringify(tight(m.regions)) !== JSON.stringify(want.regions)) regionDiffs++;
      if (verbose && (m.type !== want.type || JSON.stringify(tight(m.regions)) !== JSON.stringify(want.regions)))
        lines.push(`${key}: differs from the baseline`);
      // The baseline's controls are checked on every run, held-out seeds or not; once each.
      if (key.startsWith("fixture:") || (want.control && !controlKeys.has(key))) {
        if (want.control) baseControls++;
        const n = bodyLinesInLabelled(m.items, m.regions);
        bodyLines += n;
        if (n > 0 && verbose) lines.push(`${key}: ${n} body line(s) in a with-labels region`);
      }
    }
  }

  const share = (r: Row) => (r.figures === 0 ? 0 : r.pass / r.figures);
  const gens = GENERATORS.filter((g) => byGenerator.get(g)!.figures > 0);
  const lays = LAYOUTS.filter((l) => byLayout.get(l)!.figures > 0);
  const bars = [
    { name: `at least ${BARS.figures} figures`, ok: all.figures >= BARS.figures, value: `${all.figures}` },
    { name: `overall ≥ ${BARS.overall * 100} %`, ok: share(all) >= BARS.overall, value: pct(all.pass, all.figures) },
    {
      name: `each generator ≥ ${BARS.generator * 100} %`,
      ok: gens.length === GENERATORS.length && gens.every((g) => share(byGenerator.get(g)!) >= BARS.generator),
      value: gens.map((g) => `${g} ${pct(byGenerator.get(g)!.pass, byGenerator.get(g)!.figures)}`).join(", "),
    },
    {
      name: `each layout ≥ ${BARS.layout * 100} %`,
      ok: lays.length === LAYOUTS.length && lays.every((l) => share(byLayout.get(l)!) >= BARS.layout),
      value: `lowest ${lays.map((l) => pct(byLayout.get(l)!.pass, byLayout.get(l)!.figures)).sort((a, b) => parseFloat(a) - parseFloat(b))[0] ?? "-"}`,
    },
    {
      name: "page types and tight regions as the baseline",
      ok: hasBaseline && baseRules === BASELINE_RULES && typeDiffs === 0 && regionDiffs === 0 && missing === 0,
      value: !hasBaseline
        ? "no baseline"
        : baseRules !== BASELINE_RULES
          ? `the baseline was made by rules ${baseRules ?? "unknown"}, not ${BASELINE_RULES}`
          : `${typeDiffs} type(s), ${regionDiffs} region list(s) differ, ${missing} missing`,
    },
    { name: "no body line in a with-labels region", ok: bodyLines === 0, value: `${bodyLines}` },
  ];

  const p = (s = "") => console.log(s);
  p(`Regions with labels, seeds ${argv[at + 1]}: ${all.figures} figures, judged by labelled box ${judgedBy.labelled}, by tight box ${judgedBy.tight}, with no region ${judgedBy.none}`);
  p();
  for (const l of table("generator", [...gens.map((g) => [g, byGenerator.get(g)!] as [string, Row]), ["all", all]])) p(l);
  p();
  for (const l of table("layout", lays.map((l) => [l, byLayout.get(l)!] as [string, Row]))) p(l);
  p();
  p(`${"failures by kind".padEnd(22)}${GENERATORS.map((g) => g.padStart(12)).join("")}${"all".padStart(8)}`);
  for (const k of FAILURES) {
    const per = failures.get(k)!;
    const n = [...per.values()].reduce((s, v) => s + v, 0);
    p(`${k.padEnd(22)}${GENERATORS.map((g) => String(per.get(g) ?? 0).padStart(12)).join("")}${String(n).padStart(8)}`);
  }
  const u = [...unidentified.values()].reduce((s, v) => s + v, 0);
  p(`${"unidentified items".padEnd(22)}${GENERATORS.map((g) => String(unidentified.get(g)).padStart(12)).join("")}${String(u).padStart(8)}`);
  p("(figures per kind; unidentified in text items, counted for neither side)");
  p();
  p(`Controls checked for body lines: ${controls} of the judged seeds, ${baseControls} more of the baseline's; fixtures: ${Object.keys(FIXTURES).length}`);
  p(
    hasBaseline
      ? `Baseline (rules ${baseRules ?? "unknown"}, seeds ${baseSeeds.join(",")} and the fixtures${writeBaseline ? ", written now" : ""}): ${compared} pages compared`
      : "Baseline: none (run with --baseline to write one)",
  );
  p();
  for (const b of bars) p(`${b.ok ? "met    " : "not met"}  ${b.name}: ${b.value}`);
  const shortOf = [
    all.complete < all.figures ? `complete ${pct(all.complete, all.figures)}` : null,
    all.clean < all.figures ? `clean ${pct(all.clean, all.figures)}` : null,
  ].filter(Boolean);
  p();
  p(`Verdict: ${bars.every((b) => b.ok) ? "ships" : "fails"}${shortOf.length ? ` (${shortOf.join(", ")} of figures)` : ""}`);
  if (verbose && lines.length > 0) {
    p();
    for (const l of lines) p(l);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main(process.argv.slice(2));
