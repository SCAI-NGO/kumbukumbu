// SPDX-License-Identifier: AGPL-3.0-or-later

// A page's embedded images for storing as attachments: listed in painting order, each as pdf.js
// decoded it, turned and mirrored as the page shows it (in quarter turns), scaled to fit 8 MP and
// encoded anew; and regions rendered at 300 dpi. analyse.ts measures the same operators, but its
// walk stays its own so the analysis cannot change through this one. Images in hidden optional
// content and masks under a transparent fill are listed and stored too, as measureDrawing counts
// them: kept on purpose, so an ingest loses nothing.

import { createHash } from "node:crypto";
import { type Canvas, ImageData, createCanvas } from "@napi-rs/canvas";
import { AnnotationMode, OPS, Util, type PDFPageProxy } from "pdfjs-dist/legacy/build/pdf.mjs";
import { type Box, MIN_FIGURE, clip } from "./classify.js";
import { type Rendered, encodeStored } from "./encode.js";
import { MAX_SIDE } from "./image.js";
import { PdfError, openPdf } from "./pdfjs.js";
import { type RenderedPage, renderWith } from "./render.js";
import { type Crop, pixels } from "./steps.js";

export const MAX_STORED_PIXELS = 8_000_000;
export const STORED_DPI = 300;
/** The attachment limit (AttachmentFormat.MaxBytes). */
export const MAX_BODY_STORED = 15 * 1024 * 1024;
/** pdf.js's warning for an image it leaves out for exceeding maxImageSize. */
export const OVERSIZE_WARNING = "Image exceeded maximum allowed size and was removed.";
/** The placements a page's walk takes before it gives up. */
export const MAX_PLACEMENTS = 10_000;

export type ImageInfo = {
  n: number;
  /** Decoded pixels, turned as painted. */
  width: number;
  height: number;
  /** Its box in displayed points, clipped to the page. */
  x: number;
  y: number;
  w: number;
  h: number;
  small: boolean;
  /** Null for an image pdf.js could not decode. */
  hash: string | null;
  failed: boolean;
};
export type PageImages = { page: number; pageCount: number; width: number; height: number; over: number; images: ImageInfo[] };
/** originalWidth and originalHeight: the image's pixels before scaling, turned as painted. */
export type StoredImage = Rendered & { originalWidth: number; originalHeight: number };

// --- pdf.js's console ---

export type Warnings = { oversize: number };

const listening: Warnings[] = [];
let saved: Pick<Console, "log" | "warn" | "error" | "info"> | null = null;

/**
 * Runs `run` with console.log, warn, error and info swallowed, counting pdf.js's oversize warnings.
 * pdf.js warns through the console of the thread it runs in, and with warnings on it names details
 * of the file, so nothing of it may reach the log; the extraction job runs inside this.
 */
export async function quietly<T>(run: (warnings: Warnings) => Promise<T>): Promise<T> {
  const warnings: Warnings = { oversize: 0 };
  if (listening.length === 0) {
    saved = { log: console.log, warn: console.warn, error: console.error, info: console.info };
    const swallow = (...args: unknown[]) => {
      if (args.length === 1 && args[0] === `Warning: ${OVERSIZE_WARNING}`) for (const w of listening) w.oversize++;
    };
    console.log = console.warn = console.error = console.info = swallow;
  }
  listening.push(warnings);
  try {
    return await run(warnings);
  } finally {
    listening.splice(listening.indexOf(warnings), 1);
    if (listening.length === 0 && saved) {
      Object.assign(console, saved);
      saved = null;
    }
  }
}

// --- The walk ---

type Matrix = number[];
const IDENTITY: Matrix = [1, 0, 0, 1, 0, 0];

// An image as pdf.js hands it over: kind 1 (1-bit grey, packed rows, 1 white), 2 (RGB), 3 (RGBA);
// a mask has no kind, its packed bits paint where 0. A bitmap instead of data does not occur in Node.
type ImgData = { width: number; height: number; kind?: number; data: Uint8Array | Uint8ClampedArray | null; bitmap?: unknown };
type MaskArgs = { data: string | Uint8Array | null; width: number; height: number };

// Where an image's pixels come from: an object pdf.js resolves by id, data in the operator's
// arguments, or a piece of an atlas of inline images.
type Source =
  | { from: "object"; id: string; width?: number; height?: number }
  | { from: "inline"; img: ImgData }
  | { from: "atlas"; img: ImgData; x: number; y: number; w: number; h: number }
  | { from: "mask"; mask: MaskArgs | ImgData; fill: string };

type Placement = { source: Source; turn: Turn; box: Box };

// A fill other than an RGB hex colour (a pattern, transparent) paints a mask black.
const HEX = /^#[0-9a-f]{6}$/i;

// Walks the operator list keeping the current transformation matrix and fill as pdf.js's canvas
// does, and gives each image placement on the page, in painting order.
function walk(fns: ArrayLike<number>, args: ArrayLike<unknown[] | null>, page: PDFPageProxy): Placement[] {
  const viewport = page.getViewport({ scale: 1 });
  const out: Placement[] = [];
  let ctm = IDENTITY;
  let fill = "#000000";
  const stack: { ctm: Matrix; fill: string }[] = [];
  let placed = 0;
  const place = (source: Source, m: Matrix) => {
    if (++placed > MAX_PLACEMENTS) throw new PdfError("The page paints more than 10,000 images.", "too_large");
    const shown = Util.transform(viewport.transform, m) as Matrix;
    const box = clip(unitBox(shown), viewport.width, viewport.height);
    if (box) out.push({ source, turn: turnOf(shown), box });
  };
  const mask = (m: MaskArgs | ImgData): Source => ({ from: "mask", mask: m, fill });

  for (let i = 0; i < fns.length; i++) {
    const a = args[i] ?? [];
    switch (fns[i]) {
      case OPS.save:
      case OPS.beginGroup:
        stack.push({ ctm, fill });
        break;
      case OPS.restore:
      case OPS.endGroup:
      case OPS.paintFormXObjectEnd: {
        const s = stack.pop();
        if (s) ({ ctm, fill } = s);
        break;
      }
      case OPS.transform:
        ctm = Util.transform(ctm, a as Matrix);
        break;
      case OPS.paintFormXObjectBegin:
        stack.push({ ctm, fill });
        if (Array.isArray(a[0]) || ArrayBuffer.isView(a[0])) ctm = Util.transform(ctm, Array.from(a[0] as ArrayLike<number>));
        break;
      case OPS.setFillRGBColor:
        fill = typeof a[0] === "string" && HEX.test(a[0]) ? a[0] : "#000000";
        break;
      case OPS.setFillTransparent:
      case OPS.setFillColorN:
        fill = "#000000";
        break;
      case OPS.paintImageXObject: {
        const [id, width, height] = a as [string, number, number];
        place({ from: "object", id, width, height }, ctm);
        break;
      }
      case OPS.paintImageXObjectRepeat: {
        const [id, scaleX, scaleY, positions] = a as [string, number, number, ArrayLike<number>];
        for (let p = 0; p + 1 < positions.length; p += 2)
          place({ from: "object", id }, Util.transform(ctm, [scaleX, 0, 0, scaleY, positions[p]!, positions[p + 1]!]));
        break;
      }
      case OPS.paintInlineImageXObject:
        place({ from: "inline", img: a[0] as ImgData }, ctm);
        break;
      case OPS.paintInlineImageXObjectGroup: {
        const [img, map] = a as [ImgData, { transform: Matrix; x: number; y: number; w: number; h: number }[]];
        for (const e of map) place({ from: "atlas", img, x: e.x, y: e.y, w: e.w, h: e.h }, Util.transform(ctm, e.transform));
        break;
      }
      case OPS.paintImageMaskXObject:
        place(mask(a[0] as MaskArgs), ctm);
        break;
      case OPS.paintImageMaskXObjectRepeat: {
        const [m, scaleX, skewX, skewY, scaleY, positions] = a as [MaskArgs, number, number, number, number, ArrayLike<number>];
        for (let p = 0; p + 1 < positions.length; p += 2)
          place(mask(m), Util.transform(ctm, [scaleX, skewX ?? 0, skewY ?? 0, scaleY, positions[p]!, positions[p + 1]!]));
        break;
      }
      case OPS.paintImageMaskXObjectGroup:
        for (const m of a[0] as (MaskArgs & { transform: Matrix })[]) place(mask(m), Util.transform(ctm, m.transform));
        break;
      // paintSolidColorImageMask fills a unit square: not an image.
    }
  }
  return out;
}

// The displayed box of the unit square under m.
function unitBox([a, b, c, d, e, f]: Matrix): Box {
  const xs = [0, a!, c!, a! + c!];
  const ys = [0, b!, d!, b! + d!];
  const x = Math.min(...xs) + e!;
  const y = Math.min(...ys) + f!;
  return { x, y, w: Math.max(...xs) + e! - x, h: Math.max(...ys) + f! - y };
}

// --- Turning ---

// Where a pixel moves: the displayed step of one pixel to the right (u) and one down (v) in the
// image, each one of ±x, ±y. [p, q, r, s]: x' = p·u + q·v, y' = r·u + s·v.
type Turn = readonly [number, number, number, number];
const TURNS: Turn[] = [
  [1, 0, 0, 1],
  [0, -1, 1, 0],
  [-1, 0, 0, -1],
  [0, 1, -1, 0],
  [-1, 0, 0, 1],
  [1, 0, 0, -1],
  [0, 1, 1, 0],
  [0, -1, -1, 0],
];

// The quarter turn (and mirroring) nearest to the displayed matrix. pdf.js paints an image's first
// row at the top of its unit square, so a step down the image is -(c, d).
function turnOf([a, b, c, d]: Matrix): Turn {
  const unit = (x: number, y: number) => {
    const l = Math.hypot(x, y) || 1;
    return [x / l, y / l] as const;
  };
  const [ux, uy] = unit(a!, b!);
  const [vx, vy] = unit(-c!, -d!);
  let best = TURNS[0]!;
  let score = -Infinity;
  for (const t of TURNS) {
    const s = t[0] * ux + t[1] * vx + t[2] * uy + t[3] * vy;
    if (s > score + 1e-9) [best, score] = [t, s];
  }
  return best;
}

const sideways = (t: Turn) => t[0] === 0;
const turned = (t: Turn, width: number, height: number) => (sideways(t) ? { width: height, height: width } : { width, height });

// --- Pixels ---

// A rectangle of pdf.js's image data: rows of `stride` bytes; kind as ImgData, 0 for a mask.
type Raster = { kind: number; data: Uint8Array | Uint8ClampedArray; stride: number; x0: number; y0: number; width: number; height: number; fill?: string };

function objectOf(page: PDFPageProxy, id: string): Promise<ImgData | null> {
  const objs = id.startsWith("g_") ? page.commonObjs : page.objs;
  // Decoded images arrive after the operator list; the callback form waits for them.
  return new Promise((resolve) => objs.get(id, (data: ImgData | null) => resolve(data ?? null)));
}

const positive = (v: unknown): v is number => Number.isInteger(v) && (v as number) > 0;

const strideOf = (kind: number, width: number) => (kind === 2 ? width * 3 : kind === 3 ? width * 4 : (width + 7) >> 3);

async function rasterOf(page: PDFPageProxy, source: Source): Promise<Raster | null> {
  const whole = (img: ImgData | null, kind = img?.kind, fill?: string): Raster | null => {
    if (!img) return null;
    if (!img.data && img.bitmap) return fromBitmap(img);
    if (!img.data || !(kind === 0 || kind === 1 || kind === 2 || kind === 3)) return null;
    if (!positive(img.width) || !positive(img.height)) return null;
    const stride = strideOf(kind, img.width);
    let data = img.data;
    if (data.length < stride * img.height) {
      if (kind !== 0) return null;
      // A mask short of data paints nothing where it lacks it, as pdf.js pads it.
      data = new Uint8Array(stride * img.height).fill(0xff);
      data.set(img.data);
    }
    return { kind, data, stride, x0: 0, y0: 0, width: img.width, height: img.height, fill };
  };
  switch (source.from) {
    case "object":
      return whole(await objectOf(page, source.id));
    case "inline":
      return whole(source.img);
    case "atlas": {
      const atlas = whole(source.img);
      if (!atlas || atlas.kind !== 3) return null;
      return { ...atlas, x0: source.x, y0: source.y, width: source.w, height: source.h };
    }
    case "mask": {
      const m = source.mask;
      const img = typeof m.data === "string" ? await objectOf(page, m.data) : (m as ImgData);
      // pdf.js hands a mask over as an RGBA bitmap only where it has OffscreenCanvas.
      if (img && !img.data && img.bitmap) return fromBitmap(img);
      return whole(img, 0, source.fill);
    }
  }
}

function fromBitmap(img: ImgData): Raster {
  const canvas = createCanvas(img.width, img.height);
  const ctx = canvas.getContext("2d");
  ctx.drawImage(img.bitmap as never, 0, 0);
  const { data } = ctx.getImageData(0, 0, img.width, img.height);
  return { kind: 3, data, stride: img.width * 4, x0: 0, y0: 0, width: img.width, height: img.height };
}

// The hash of pdf.js's data as it is, with what makes it look as it does: kind, size, turn and a
// mask's colour. Nothing is converted, so listing a page costs no copy of its images.
function hashOf(r: Raster, t: Turn): string {
  const h = createHash("sha256");
  h.update(JSON.stringify([r.kind, r.width, r.height, t, r.fill ?? null]));
  const bpp = r.kind === 2 ? 3 : r.kind === 3 ? 4 : 0;
  for (let y = 0; y < r.height; y++) {
    const start = (r.y0 + y) * r.stride + r.x0 * bpp;
    h.update(r.data.subarray(start, bpp ? start + r.width * bpp : start + r.stride));
  }
  return h.digest("hex");
}

// The raster's pixels, as they are (not turned), on a canvas of their size. They go over in strips
// of 16 rows, as pdf.js's putBinaryImageData does, so the RGBA never exists twice in full.
const STRIP = 16;

function canvasOf(r: Raster): Canvas {
  const canvas = createCanvas(r.width, r.height);
  const ctx = canvas.getContext("2d");
  const fill = r.fill ? [1, 3, 5].map((i) => parseInt(r.fill!.slice(i, i + 2), 16)) : [0, 0, 0];
  const { data } = r;
  let strip = new ImageData(r.width, Math.min(STRIP, r.height));
  for (let top = 0; top < r.height; top += STRIP) {
    const rows = Math.min(STRIP, r.height - top);
    if (rows !== strip.height) strip = new ImageData(r.width, rows);
    const out = strip.data;
    // A mask's strip starts transparent each time; the other kinds set every byte.
    if (r.kind === 0) out.fill(0);
    for (let v = 0; v < rows; v++) {
      const row = (r.y0 + top + v) * r.stride;
      for (let u = 0; u < r.width; u++) {
        const at = (v * r.width + u) * 4;
        const x = r.x0 + u;
        switch (r.kind) {
          case 0:
          case 1: {
            const bit = (data[row + (x >> 3)]! >> (7 - (x & 7))) & 1;
            if (r.kind === 1) {
              const c = bit ? 255 : 0;
              out[at] = out[at + 1] = out[at + 2] = c;
              out[at + 3] = 255;
            } else if (bit === 0) {
              out[at] = fill[0]!;
              out[at + 1] = fill[1]!;
              out[at + 2] = fill[2]!;
              out[at + 3] = 255;
            }
            break;
          }
          case 2: {
            const from = row + x * 3;
            out[at] = data[from]!;
            out[at + 1] = data[from + 1]!;
            out[at + 2] = data[from + 2]!;
            out[at + 3] = 255;
            break;
          }
          default:
            out.set(data.subarray(row + x * 4, row + x * 4 + 4), at);
        }
      }
    }
    ctx.putImageData(strip, 0, top);
  }
  return canvas;
}

// The source turned and scaled onto a canvas of width × height in one drawImage. A source pixel
// (u, v) lands at x' = p·u + q·v, y' = r·u + s·v, from the far edge where a step goes back.
function turnOnto(source: Canvas, t: Turn, width: number, height: number): Canvas {
  const [p, q, r, s] = t;
  const shown = turned(t, source.width, source.height);
  const [kx, ky] = [width / shown.width, height / shown.height];
  const canvas = createCanvas(width, height);
  const ctx = canvas.getContext("2d");
  if (kx < 1 || ky < 1) {
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = "high";
  } else ctx.imageSmoothingEnabled = false;
  ctx.setTransform(p * kx, r * ky, q * kx, s * ky, p < 0 || q < 0 ? width : 0, r < 0 || s < 0 ? height : 0);
  ctx.drawImage(source, 0, 0);
  return canvas;
}

// --- The routes' work ---

async function onPage<T>(pdf: Uint8Array, page: number, warnings: boolean, run: (p: PDFPageProxy, pageCount: number) => Promise<T>): Promise<T> {
  const doc = await openPdf(pdf, { warnings });
  try {
    if (!Number.isInteger(page) || page < 1 || page > doc.numPages)
      throw new PdfError(`The PDF has no page ${page}; it has ${doc.numPages}.`, "no_such_page");
    const p = await doc.getPage(page);
    try {
      return await run(p, doc.numPages);
    } finally {
      p.cleanup();
    }
  } finally {
    await doc.loadingTask.destroy();
  }
}

async function placements(p: PDFPageProxy): Promise<Placement[]> {
  // Annotations (form fields, stamps, comments) are left out, as in the analysis.
  const ops = await p.getOperatorList({ annotationMode: AnnotationMode.DISABLE });
  return walk(ops.fnArray, ops.argsArray, p);
}

const round1 = (v: number) => Math.round(v * 10) / 10;

// The size pdf.js declares for an image it could not decode.
function declared(source: Source): { width: number; height: number } {
  const side = (v: unknown) => (positive(v) ? v : 0);
  const sides = (width: unknown, height: unknown) => ({ width: side(width), height: side(height) });
  if (source.from === "object") return sides(source.width, source.height);
  if (source.from === "atlas") return sides(source.w, source.h);
  if (source.from === "mask") return sides(source.mask.width, source.mask.height);
  return sides(source.img.width, source.img.height);
}

// What tells one source from another within a page's walk: the object pdf.js resolves, or the
// inline data itself, with what changes how it looks.
function keyOf(source: Source, turn: Turn): [object | string, string] {
  switch (source.from) {
    case "object":
      return [`o:${source.id}`, `${turn}`];
    case "inline":
      return [source.img, `${turn}`];
    case "atlas":
      return [source.img, `${turn}|${source.x},${source.y},${source.w},${source.h}`];
    case "mask": {
      const m = source.mask;
      return [typeof m.data === "string" ? `m:${m.data}` : m, `${turn}|${source.fill}`];
    }
  }
}

/**
 * The images of the placements. getOperatorList has decoded every image of the page into
 * page.objs, where they stay until the page's cleanup; this only reads them, and hashes each
 * source once however often it is placed.
 */
export async function imagesOf(p: PDFPageProxy, ops: { fnArray: ArrayLike<number>; argsArray: ArrayLike<unknown[] | null> }): Promise<ImageInfo[]> {
  const viewport = p.getViewport({ scale: 1 });
  const area = viewport.width * viewport.height;
  type Seen = { width: number; height: number; hash: string | null; failed: boolean };
  const seen = new Map<object | string, Map<string, Seen>>();
  const images: ImageInfo[] = [];
  for (const [i, { source, turn, box }] of walk(ops.fnArray, ops.argsArray, p).entries()) {
    const [what, how] = keyOf(source, turn);
    const known = seen.get(what) ?? new Map<string, Seen>();
    seen.set(what, known);
    let found = known.get(how);
    if (!found) {
      const raster = await rasterOf(p, source);
      const { width, height } = raster ?? declared(source);
      found = { ...turned(turn, width, height), hash: raster ? hashOf(raster, turn) : null, failed: !raster };
      known.set(how, found);
    }
    images.push({
      n: i + 1,
      width: found.width,
      height: found.height,
      x: round1(box.x),
      y: round1(box.y),
      w: round1(box.w),
      h: round1(box.h),
      small: box.w * box.h < MIN_FIGURE * area,
      hash: found.hash,
      failed: found.failed,
    });
  }
  return images;
}

export function listImages(pdf: Uint8Array, page: number): Promise<PageImages> {
  return quietly((warnings) =>
    onPage(pdf, page, true, async (p, pageCount) => {
      const viewport = p.getViewport({ scale: 1 });
      const before = warnings.oversize;
      const ops = await p.getOperatorList({ annotationMode: AnnotationMode.DISABLE });
      const over = warnings.oversize - before;
      const images = await imagesOf(p, ops);
      return { page, pageCount, width: round1(viewport.width), height: round1(viewport.height), over, images };
    }),
  );
}

export function extractImage(pdf: Uint8Array, page: number, n: number): Promise<StoredImage> {
  return quietly(() =>
    onPage(pdf, page, false, async (p) => {
      const all = await placements(p);
      if (!Number.isInteger(n) || n < 1 || n > all.length)
        throw new PdfError(`Page ${page} has no image ${n}; it has ${all.length}.`, "no_such_image");
      const { source, turn } = all[n - 1]!;
      all.length = 0;
      let raster = await rasterOf(p, source);
      if (!raster) throw new PdfError("The image could not be decoded.", "failed");
      const { width: ow, height: oh } = turned(turn, raster.width, raster.height);
      const scale = Math.min(1, Math.sqrt(MAX_STORED_PIXELS / (ow * oh)), MAX_SIDE / Math.max(ow, oh));
      const { width, height } = scale < 1 ? pixels(ow, oh, scale) : { width: ow, height: oh };
      let unturned: Canvas | null = canvasOf(raster);
      raster = null;
      const canvas = turnOnto(unturned, turn, width, height);
      unturned = null;
      return { ...(await encodeStored(canvas, MAX_BODY_STORED)), originalWidth: ow, originalHeight: oh };
    }),
  );
}

/** A region at 300 dpi, at most 8 MP and 16,384 px on a side, encoded only as asked. */
export function renderRegion(pdf: Uint8Array, page: number, crop: Crop, rotate: number, format: "png" | "jpeg"): Promise<RenderedPage> {
  return quietly(() =>
    renderWith(
      pdf,
      page,
      crop,
      rotate,
      (w, h) => Math.min(STORED_DPI / 72, Math.sqrt(MAX_STORED_PIXELS / (w * h)), MAX_SIDE / Math.max(w, h)),
      (canvas) => encodeStored(canvas, MAX_BODY_STORED, format),
    ),
  );
}
