// SPDX-License-Identifier: AGPL-3.0-or-later

// Page types from measured boxes. Pure and deterministic: the same boxes give the same answer, so
// a stored analysis stays valid until RULES_VERSION changes.
//
// The rules beyond the plan's thresholds, all in analyse.ts's measurement:
// - each subpath of a path gets its own box, so a table grid drawn as one path stays thin rules;
// - paths that only set a clip are ignored (they paint nothing);
// - fill-only paths in pure white are ignored as the paper (many generators paint a background);
// - annotations are not measured (form fields and stamps are not the page's content).
//
// A region may gain a labelled box beside its tight one (withLabels): by position only, so a raster
// figure gains nothing from what is drawn into its pixels.

// Displayed points, top-left origin; a text box also says how many characters it holds.
export type Box = { x: number; y: number; w: number; h: number; chars?: number };
export type PageMeasure = { width: number; height: number; textBoxes: Box[]; imageBoxes: Box[]; pathBoxes: Box[] };
export type Region = { kind: "image" | "drawing"; x: number; y: number; w: number; h: number; labelled?: Box };
export type PageType = "text" | "mixed" | "image";
export type Classified = {
  type: PageType;
  textCoverage: number;
  imageCoverage: number;
  vectorCoverage: number;
  images: number;
  regions: Region[];
};

export const RULES_VERSION = 2;

/** One image covering at least this share of the page makes it an image page. */
export const FULL_PAGE_IMAGE = 0.85;
/** Images and figures covering at least this share of the page make it a mixed page. */
export const MIXED = 0.25;
/** A path box with a side under this many points is a rule or a border, not a figure. */
export const THIN_PT = 2;
/** Path boxes grown by this many points that then overlap belong to one figure. */
export const MERGE_PT = 6;
/** Figures and images smaller than this share of the page are not regions. */
export const MIN_FIGURE = 0.01;
/** Coverage is sampled on a GRID × GRID raster over the page. */
export const GRID = 200;
/** Above this many path boxes clustering (quadratic) is skipped: they make one drawing. */
export const MAX_CLUSTERED = 20_000;
/** Text boxes of fewer characters may be a figure's labels; longer ones (body text, captions) stop its growth. */
export const LABEL_CHARS = 30;
/** A labelled box takes in labels, legend swatches and pieces of its chart that lie this close to it. */
export const LABEL_GAP_PT = 12;
/** Above this many text boxes, or as many path boxes that may be a chart's marks, a page's regions gain no labelled box. */
export const MAX_LABELLED = 5_000;

export function classify(page: PageMeasure): Classified {
  const { width, height } = page;
  const area = width * height;
  if (!(area > 0))
    return { type: "text", textCoverage: 0, imageCoverage: 0, vectorCoverage: 0, images: 0, regions: [] };

  // A text box keeps its count of characters.
  const within = (boxes: Box[]) =>
    boxes
      .map((b) => {
        const c = clip(b, width, height);
        return c && { ...b, ...c };
      })
      .filter((b): b is Box => b !== null);
  const text = within(page.textBoxes);
  const images = within(page.imageBoxes);
  const paths = within(page.pathBoxes).filter((b) => Math.min(b.w, b.h) >= THIN_PT);
  // A page of tens of thousands of path pieces (a map, outlined text) would take too long to
  // cluster; its pieces count as they are, and their bounding box is its one drawing.
  const many = paths.length > MAX_CLUSTERED;
  const big = (b: Box) => b.w * b.h >= MIN_FIGURE * area;
  const clustered = many ? [bounds(paths)] : cluster(paths);
  const figures = clustered.filter(big);
  const drawn = many ? paths : figures;

  const cover = (boxes: Box[]) => coverage(boxes, width, height);
  const imageCoverage = cover(images);
  const vectorCoverage = cover(drawn);
  const type: PageType = images.some((b) => (b.w * b.h) / area >= FULL_PAGE_IMAGE)
    ? "image"
    : cover([...images, ...drawn]) >= MIXED
      ? "mixed"
      : "text";

  const found: Found[] = [
    ...images.filter(big).map((box) => ({ kind: "image" as const, box, grows: (box.w * box.h) / area < FULL_PAGE_IMAGE })),
    ...figures.map((box) => ({ kind: "drawing" as const, box, grows: true })),
  ];
  // Marks: the small path boxes that fell under MIN_FIGURE (legend swatches) and the thin ones (a
  // chart's axes, gridlines and ticks, which lead to its labels).
  const marks = [...clustered.filter((b) => !big(b)), ...lines(page.pathBoxes, width, height)];
  const labelled = page.textBoxes.length > MAX_LABELLED || marks.length > MAX_LABELLED ? [] : withLabels(found, text, marks);
  const regions: Region[] = found
    .map((f, i) => region(f.kind, f.box, labelled[i], width, height))
    .sort((a, b) => a.y - b.y || a.x - b.x);

  return { type, textCoverage: cover(text), imageCoverage, vectorCoverage, images: images.length, regions };
}

export function clip(b: Box, width: number, height: number): Box | null {
  const x0 = Math.max(0, b.x);
  const y0 = Math.max(0, b.y);
  const x1 = Math.min(width, b.x + b.w);
  const y1 = Math.min(height, b.y + b.h);
  return x1 > x0 && y1 > y0 ? { x: x0, y: y0, w: x1 - x0, h: y1 - y0 } : null;
}

function bounds(boxes: Box[]): Box {
  let [x0, y0, x1, y1] = [Infinity, Infinity, -Infinity, -Infinity];
  for (const b of boxes) {
    x0 = Math.min(x0, b.x);
    y0 = Math.min(y0, b.y);
    x1 = Math.max(x1, b.x + b.w);
    y1 = Math.max(y1, b.y + b.h);
  }
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}

// Merges boxes whose MERGE_PT-grown outlines overlap until nothing changes; the result holds the
// bounding box of each cluster.
function cluster(boxes: Box[]): Box[] {
  const out = boxes.map((b) => ({ ...b }));
  const near = (a: Box, b: Box) =>
    a.x - MERGE_PT < b.x + b.w + MERGE_PT &&
    b.x - MERGE_PT < a.x + a.w + MERGE_PT &&
    a.y - MERGE_PT < b.y + b.h + MERGE_PT &&
    b.y - MERGE_PT < a.y + a.h + MERGE_PT;
  let changed = true;
  while (changed) {
    changed = false;
    for (let i = 0; i < out.length; i++) {
      for (let j = i + 1; j < out.length; j++) {
        if (!near(out[i]!, out[j]!)) continue;
        const a = out[i]!;
        const b = out[j]!;
        const x = Math.min(a.x, b.x);
        const y = Math.min(a.y, b.y);
        out[i] = { x, y, w: Math.max(a.x + a.w, b.x + b.w) - x, h: Math.max(a.y + a.h, b.y + b.h) - y };
        out.splice(j, 1);
        j = i;
        changed = true;
      }
    }
  }
  return out;
}

// Share of grid cells whose centre lies in at least one box, rounded to 3 decimals.
function coverage(boxes: Box[], width: number, height: number): number {
  if (boxes.length === 0) return 0;
  const cw = width / GRID;
  const ch = height / GRID;
  const cells = new Uint8Array(GRID * GRID);
  // Cell i's centre (i + 0.5)·cw lies in [x, x + w) for i in [ceil(x/cw - 0.5), ceil((x+w)/cw - 0.5)).
  const span = (from: number, to: number, size: number): [number, number] => [
    Math.max(0, Math.ceil(from / size - 0.5)),
    Math.min(GRID, Math.ceil(to / size - 0.5)),
  ];
  let covered = 0;
  for (const b of boxes) {
    const [i0, i1] = span(b.x, b.x + b.w, cw);
    const [j0, j1] = span(b.y, b.y + b.h, ch);
    for (let j = j0; j < j1; j++)
      for (let i = i0; i < i1; i++) {
        const k = j * GRID + i;
        if (cells[k] === 0) {
          cells[k] = 1;
          covered++;
        }
      }
  }
  return round(covered / (GRID * GRID), 1000);
}

const round = (v: number, per: number) => Math.round(v * per) / per;

const rounded = (b: Box): Box => ({ x: round(b.x, 10), y: round(b.y, 10), w: round(b.w, 10), h: round(b.h, 10) });

function region(kind: Region["kind"], b: Box, labelled: Box | null | undefined, width: number, height: number): Region {
  const tight = rounded(b);
  const cut = labelled ? clip(labelled, width, height) : null;
  const wide = cut ? rounded(cut) : null;
  const same = !wide || (wide.x === tight.x && wide.y === tight.y && wide.w === tight.w && wide.h === tight.h);
  return same ? { kind, ...tight } : { kind, ...tight, labelled: wide };
}

type Found = { kind: Region["kind"]; box: Box; grows: boolean };
type Chart = { members: number[]; box: Box; grows: boolean; drawing: boolean; alive: boolean };
type Grown = { box: Box; pieces: Chart[] };

// The labelled box of each region (index as in found), or null where it gains none. Each region
// starts as a chart of its own and, round by round in a fixed order, takes in what lies within
// LABEL_GAP_PT of its box, nearest first: short text and marks (a chain, so a legend's second line
// follows its first, and an axis leads to its tick labels), and the drawing regions of other charts
// when the two reach each other or a mark joins them (a grouped bar chart falls apart into one
// region per group). Never what lies inside another region or closer to another chart, and never
// so that the box takes in a long text's centre or overlaps another chart's image, or a drawing too
// far from what brought it in. A full page's image (a scan's OCR text isn't its label) only stands
// in the way.
function withLabels(found: Found[], text: Box[], marks: Box[]): (Box | null)[] {
  const order = found.map((_, i) => i).sort((a, b) => byPosition(found[a]!.box, found[b]!.box) || found[a]!.kind.localeCompare(found[b]!.kind));
  const charts: Chart[] = order.map((i) => ({ members: [i], box: found[i]!.box, grows: found[i]!.grows, drawing: found[i]!.kind === "drawing", alive: true }));
  const label = (t: Box) => t.chars !== undefined && t.chars < LABEL_CHARS;
  const candidates = [...text.filter(label), ...marks].sort(byPosition);
  const blockers = text.filter((t) => !label(t)).map(centre);
  const taken = new Uint8Array(candidates.length);
  const reach = (chart: Chart, other: Chart) => Math.min(...other.members.map((m) => gap(chart.box, found[m]!.box)));
  // Nearness is measured to a chart's own regions, never to its grown box, so the chart processed
  // first doesn't win what lies nearer its neighbour.
  const tight = (chart: Chart) => chart.members.map((m) => found[m]!.box);
  const own = (chart: Chart, b: Box) => Math.min(...tight(chart).map((t) => gap(t, b)));
  const apart = (a: Chart, b: Chart) => Math.min(...tight(b).map((t) => own(a, t)));
  const others = (...not: Chart[]) => charts.filter((c) => c.alive && !not.includes(c));

  // The chart's box grown to next, with the drawings of other charts it would then overlap taken
  // in, each within LABEL_GAP_PT of what brought it in and no nearer a third chart, as one taken
  // in directly; null if it would overlap anything else or reach a blocker's centre.
  const grow = (chart: Chart, next: Box, by: Box[]): Grown | null => {
    const pieces: Chart[] = [];
    const near = [...by];
    for (;;) {
      const hit = others(chart, ...pieces).filter((o) => overlaps(next, o.box));
      if (hit.length === 0) break;
      const fits = (o: Chart) => {
        const d = Math.min(...near.map((b) => own(o, b)));
        return d <= LABEL_GAP_PT && !others(chart, ...pieces, ...hit).some((t) => apart(t, o) < d);
      };
      if (hit.some((o) => !chart.drawing || !o.drawing || !o.grows || !fits(o))) return null;
      pieces.push(...hit);
      near.push(...hit.flatMap(tight));
      next = hit.reduce((b, o) => union(b, o.box), next);
    }
    const now = [chart.box, ...pieces.map((p) => p.box)];
    return blockers.some((p) => holds(next, p) && !now.some((b) => holds(b, p))) ? null : { box: next, pieces };
  };
  const accept = (chart: Chart, grown: Grown) => {
    chart.box = grown.box;
    for (const piece of grown.pieces) {
      chart.members.push(...piece.members);
      piece.alive = false;
    }
  };

  const takeLabel = (chart: Chart, k: number): boolean => {
    const c = candidates[k]!;
    const d = gap(chart.box, c);
    const mid = centre(c);
    if (d > LABEL_GAP_PT) return false;
    if (found.some((f, i) => !chart.members.includes(i) && holds(f.box, mid))) return false;
    const mine = own(chart, c);
    if (others(chart).some((o) => own(o, c) < mine)) return false;
    const grown = grow(chart, union(chart.box, c), [c]);
    if (!grown) return false;
    accept(chart, grown);
    taken[k] = 1;
    return true;
  };
  const takeChart = (chart: Chart, piece: Chart): boolean => {
    const d = reach(chart, piece);
    if (!piece.alive || d > LABEL_GAP_PT || reach(piece, chart) > LABEL_GAP_PT) return false;
    if (others(chart, piece).some((o) => apart(o, piece) < apart(chart, piece))) return false;
    const grown = grow(chart, union(chart.box, piece.box), tight(piece));
    if (!grown) return false;
    accept(chart, grown);
    return true;
  };

  let changed = true;
  while (changed) {
    changed = false;
    for (const chart of charts) {
      if (!chart.alive || !chart.grows) continue;
      const near: { d: number; k: number; piece?: Chart }[] = [];
      candidates.forEach((c, k) => {
        if (taken[k] === 0 && gap(chart.box, c) <= LABEL_GAP_PT) near.push({ d: gap(chart.box, c), k });
      });
      if (chart.drawing)
        charts.forEach((piece, k) => {
          if (piece !== chart && piece.alive && piece.grows && piece.drawing && reach(chart, piece) <= LABEL_GAP_PT)
            near.push({ d: reach(chart, piece), k: candidates.length + k, piece });
        });
      near.sort((a, b) => a.d - b.d || a.k - b.k);
      for (const n of near) {
        if (n.piece ? takeChart(chart, n.piece) : taken[n.k] === 0 && takeLabel(chart, n.k)) changed = true;
      }
    }
  }
  const labelled: (Box | null)[] = found.map(() => null);
  for (const chart of charts)
    if (chart.alive && chart.grows) for (const m of chart.members) labelled[m] = chart.box;
  return labelled;
}

// Thin path boxes on the page, kept even with no thickness (an axis drawn as a line), which clip drops.
function lines(boxes: Box[], width: number, height: number): Box[] {
  const out: Box[] = [];
  for (const b of boxes) {
    if (!(Math.min(b.w, b.h) < THIN_PT)) continue;
    const [x0, y0] = [Math.max(0, b.x), Math.max(0, b.y)];
    const [x1, y1] = [Math.min(width, b.x + b.w), Math.min(height, b.y + b.h)];
    if (x1 >= x0 && y1 >= y0 && (x1 > x0 || y1 > y0)) out.push({ x: x0, y: y0, w: x1 - x0, h: y1 - y0 });
  }
  return out;
}

const byPosition = (a: Box, b: Box) => a.y - b.y || a.x - b.x || a.w - b.w || a.h - b.h || (a.chars ?? -1) - (b.chars ?? -1);
const centre = (b: Box) => ({ x: b.x + b.w / 2, y: b.y + b.h / 2 });
const holds = (b: Box, p: { x: number; y: number }) => p.x >= b.x && p.x <= b.x + b.w && p.y >= b.y && p.y <= b.y + b.h;
const overlaps = (a: Box, b: Box) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;

function gap(a: Box, b: Box): number {
  const dx = Math.max(0, b.x - (a.x + a.w), a.x - (b.x + b.w));
  const dy = Math.max(0, b.y - (a.y + a.h), a.y - (b.y + b.h));
  return Math.hypot(dx, dy);
}

function union(a: Box, b: Box): Box {
  const x = Math.min(a.x, b.x);
  const y = Math.min(a.y, b.y);
  return { x, y, w: Math.max(a.x + a.w, b.x + b.w) - x, h: Math.max(a.y + a.h, b.y + b.h) - y };
}
