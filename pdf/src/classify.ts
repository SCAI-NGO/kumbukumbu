// SPDX-License-Identifier: AGPL-3.0-or-later

// Page types from measured boxes. Pure and deterministic: the same boxes give the same answer, so
// a stored analysis stays valid until RULES_VERSION changes.

export type Box = { x: number; y: number; w: number; h: number }; // displayed points, top-left origin
export type PageMeasure = { width: number; height: number; textBoxes: Box[]; imageBoxes: Box[]; pathBoxes: Box[] };
export type Region = { kind: "image" | "drawing"; x: number; y: number; w: number; h: number };
export type PageType = "text" | "mixed" | "image";
export type Classified = {
  type: PageType;
  textCoverage: number;
  imageCoverage: number;
  vectorCoverage: number;
  images: number;
  regions: Region[];
};

export const RULES_VERSION = 1;

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

export function classify(page: PageMeasure): Classified {
  const { width, height } = page;
  const area = width * height;
  if (!(area > 0))
    return { type: "text", textCoverage: 0, imageCoverage: 0, vectorCoverage: 0, images: 0, regions: [] };

  const within = (boxes: Box[]) => boxes.map((b) => clip(b, width, height)).filter((b): b is Box => b !== null);
  const text = within(page.textBoxes);
  const images = within(page.imageBoxes);
  const paths = within(page.pathBoxes).filter((b) => Math.min(b.w, b.h) >= THIN_PT);
  const figures = cluster(paths).filter((b) => b.w * b.h >= MIN_FIGURE * area);

  const cover = (boxes: Box[]) => coverage(boxes, width, height);
  const imageCoverage = cover(images);
  const vectorCoverage = cover(figures);
  const type: PageType = images.some((b) => (b.w * b.h) / area >= FULL_PAGE_IMAGE)
    ? "image"
    : cover([...images, ...figures]) >= MIXED
      ? "mixed"
      : "text";

  const regions: Region[] = [
    ...images.filter((b) => b.w * b.h >= MIN_FIGURE * area).map((b) => region("image", b)),
    ...figures.map((b) => region("drawing", b)),
  ].sort((a, b) => a.y - b.y || a.x - b.x);

  return { type, textCoverage: cover(text), imageCoverage, vectorCoverage, images: images.length, regions };
}

function clip(b: Box, width: number, height: number): Box | null {
  const x0 = Math.max(0, b.x);
  const y0 = Math.max(0, b.y);
  const x1 = Math.min(width, b.x + b.w);
  const y1 = Math.min(height, b.y + b.h);
  return x1 > x0 && y1 > y0 ? { x: x0, y: y0, w: x1 - x0, h: y1 - y0 } : null;
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

const region = (kind: Region["kind"], b: Box): Region => ({
  kind,
  x: round(b.x, 10),
  y: round(b.y, 10),
  w: round(b.w, 10),
  h: round(b.h, 10),
});
