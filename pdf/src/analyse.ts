// SPDX-License-Identifier: AGPL-3.0-or-later

// Measures every page of a PDF (text layer, images, vector paths, size and rotation) and
// classifies it. Deterministic for a given pdf.js version and RULES_VERSION, which ANALYSER names.

import { AnnotationMode, OPS, Util, version, type PDFPageProxy } from "pdfjs-dist/legacy/build/pdf.mjs";
import type { TextItem } from "pdfjs-dist/types/src/display/api.js";
import { type Box, type Classified, type PageMeasure, RULES_VERSION, classify } from "./classify.js";
import { PdfError, openPdf } from "./pdfjs.js";

export type PageAnalysis = Classified & { n: number; width: number; height: number; rotation: number; chars: number; text: string };
export type Analysis = {
  analyser: string;
  pageCount: number;
  analysedPages: number;
  title: string | null;
  author: string | null;
  pages: PageAnalysis[];
};

export const ANALYSER = `pdfjs-dist@${version}+rules.${RULES_VERSION}`;

// onPages hears the number of pages to analyse as soon as it is known (the pool's time limit grows with it).
export async function analyse(bytes: Uint8Array, maxPages: number, onPages?: (pages: number) => void): Promise<Analysis> {
  if (typeof maxPages !== "number" || Number.isNaN(maxPages)) throw new PdfError("maxPages must be a number.", "bad_request");
  const limit = Math.max(1, Math.floor(maxPages));
  const doc = await openPdf(bytes);
  try {
    const pageCount = doc.numPages;
    const analysedPages = Math.min(pageCount, limit);
    onPages?.(analysedPages);
    const { info } = (await doc.getMetadata().catch(() => ({ info: {} }))) as { info: Record<string, unknown> };
    const pages: PageAnalysis[] = [];
    for (let n = 1; n <= analysedPages; n++) {
      const page = await doc.getPage(n);
      try {
        pages.push(await analysePage(page));
      } finally {
        page.cleanup();
      }
    }
    return { analyser: ANALYSER, pageCount, analysedPages, title: meta(info["Title"]), author: meta(info["Author"]), pages };
  } finally {
    await doc.loadingTask.destroy();
  }
}

function meta(value: unknown): string | null {
  return typeof value === "string" && value.trim() !== "" ? value.trim() : null;
}

type Matrix = number[];
const IDENTITY: Matrix = [1, 0, 0, 1, 0, 0];

async function analysePage(page: PDFPageProxy): Promise<PageAnalysis> {
  const viewport = page.getViewport({ scale: 1 });
  // From PDF user space to displayed points: the CropBox, rotation and top-left origin applied.
  const displayed = (m: Matrix, x0: number, y0: number, x1: number, y1: number): Box => {
    const [a, b, c, d, e, f] = Util.transform(viewport.transform, m) as [number, number, number, number, number, number];
    const xs = [a * x0 + c * y0, a * x1 + c * y0, a * x0 + c * y1, a * x1 + c * y1];
    const ys = [b * x0 + d * y0, b * x1 + d * y0, b * x0 + d * y1, b * x1 + d * y1];
    const x = Math.min(...xs) + e;
    const y = Math.min(...ys) + f;
    return { x, y, w: Math.max(...xs) + e - x, h: Math.max(...ys) + f - y };
  };

  const content = await page.getTextContent();
  let text = "";
  const textBoxes: Box[] = [];
  for (const item of content.items) {
    if (!("str" in item)) continue;
    const t = item as TextItem;
    text += t.str;
    if (t.hasEOL) text += "\n";
    if (t.str.trim() === "") continue;
    const [, , , , e, f] = t.transform as number[];
    textBoxes.push(displayed(IDENTITY, e!, f!, e! + t.width, f! + t.height));
  }
  text = text.trimEnd();

  // Annotations (form fields, stamps, comments) are left out: they are not the page's content.
  const ops = await page.getOperatorList({ annotationMode: AnnotationMode.DISABLE });
  const { imageBoxes, pathBoxes } = measureDrawing(ops.fnArray, ops.argsArray, displayed);

  const measure: PageMeasure = { width: viewport.width, height: viewport.height, textBoxes, imageBoxes, pathBoxes };
  return {
    n: page.pageNumber,
    width: round1(viewport.width),
    height: round1(viewport.height),
    rotation: viewport.rotation,
    // Characters other than white space, so a page of spaces and line ends counts as empty.
    chars: [...text.replace(/\s+/gu, "")].length,
    text,
    ...classify(measure),
  };
}

const round1 = (v: number) => Math.round(v * 10) / 10;

// pdf.js's path encoding inside constructPath (DrawOPS, not exported).
const MOVE_TO = 0;
const LINE_TO = 1;
const CURVE_TO = 2;
const QUADRATIC_CURVE_TO = 3;
const CLOSE_PATH = 4;
const FILL_ONLY = new Set<number>([OPS.fill, OPS.eoFill]);
const WHITE = "#ffffff";

type Displayed = (m: Matrix, x0: number, y0: number, x1: number, y1: number) => Box;

// Walks the operator list keeping the current transformation matrix, as pdf.js's canvas does.
function measureDrawing(fns: number[], args: unknown[][], displayed: Displayed) {
  const imageBoxes: Box[] = [];
  const pathBoxes: Box[] = [];
  let ctm = IDENTITY;
  let fill: unknown = "#000000";
  const stack: { ctm: Matrix; fill: unknown }[] = [];
  const save = () => stack.push({ ctm, fill });
  const restore = () => {
    const s = stack.pop();
    if (s) ({ ctm, fill } = s);
  };
  const image = (m: Matrix) => imageBoxes.push(displayed(m, 0, 0, 1, 1));

  for (let i = 0; i < fns.length; i++) {
    const a = args[i] ?? [];
    switch (fns[i]) {
      case OPS.save:
      case OPS.beginGroup:
        save();
        break;
      case OPS.restore:
      case OPS.endGroup:
      case OPS.paintFormXObjectEnd:
        restore();
        break;
      case OPS.transform:
        ctm = Util.transform(ctm, a as Matrix);
        break;
      case OPS.paintFormXObjectBegin:
        save();
        if (Array.isArray(a[0]) || ArrayBuffer.isView(a[0])) ctm = Util.transform(ctm, Array.from(a[0] as ArrayLike<number>));
        break;
      case OPS.setFillRGBColor:
        fill = a[0];
        break;
      case OPS.setFillTransparent:
      case OPS.setFillColorN:
        fill = null;
        break;
      case OPS.constructPath: {
        const [op, data, minMax] = a as [number, [ArrayLike<number> | null] | undefined, ArrayLike<number> | null];
        // A path that only sets a clip paints nothing; a white fill is taken for the paper.
        if (op === OPS.endPath) break;
        if (FILL_ONLY.has(op) && typeof fill === "string" && fill.toLowerCase() === WHITE) break;
        for (const [x0, y0, x1, y1] of subpaths(data?.[0], minMax)) pathBoxes.push(displayed(ctm, x0, y0, x1, y1));
        break;
      }
      case OPS.paintImageXObject:
      case OPS.paintInlineImageXObject:
      case OPS.paintImageMaskXObject:
      case OPS.paintSolidColorImageMask:
        image(ctm);
        break;
      case OPS.paintImageXObjectRepeat: {
        const [, scaleX, scaleY, positions] = a as [unknown, number, number, ArrayLike<number>];
        for (let p = 0; p + 1 < positions.length; p += 2)
          image(Util.transform(ctm, [scaleX, 0, 0, scaleY, positions[p]!, positions[p + 1]!]));
        break;
      }
      case OPS.paintImageMaskXObjectRepeat: {
        const [, scaleX, skewX, skewY, scaleY, positions] = a as [unknown, number, number, number, number, ArrayLike<number>];
        for (let p = 0; p + 1 < positions.length; p += 2)
          image(Util.transform(ctm, [scaleX, skewX ?? 0, skewY ?? 0, scaleY, positions[p]!, positions[p + 1]!]));
        break;
      }
      case OPS.paintImageMaskXObjectGroup:
        for (const img of a[0] as { transform: Matrix }[]) image(Util.transform(ctm, img.transform));
        break;
      case OPS.paintInlineImageXObjectGroup:
        for (const entry of a[1] as { transform: Matrix }[]) image(Util.transform(ctm, entry.transform));
        break;
    }
  }
  return { imageBoxes, pathBoxes };
}

// One box per subpath in local coordinates (control points included), so a table grid drawn as one
// path still falls apart into thin rules. Falls back to the whole path's minMax for anything unknown.
function subpaths(path: ArrayLike<number> | null | undefined, minMax: ArrayLike<number> | null): number[][] {
  const whole = minMax && minMax.length === 4 ? [[minMax[0]!, minMax[1]!, minMax[2]!, minMax[3]!]] : [];
  if (!path) return whole;
  const out: number[][] = [];
  let box: number[] | null = null;
  const add = (x: number, y: number) => {
    if (!box) box = [x, y, x, y];
    else box = [Math.min(box[0]!, x), Math.min(box[1]!, y), Math.max(box[2]!, x), Math.max(box[3]!, y)];
  };
  const points = (from: number, count: number) => {
    for (let k = 0; k < count; k++) add(path[from + 2 * k]!, path[from + 2 * k + 1]!);
  };
  for (let i = 0; i < path.length; ) {
    switch (path[i++]) {
      case MOVE_TO:
        if (box) out.push(box);
        box = null;
        points(i, 1);
        i += 2;
        break;
      case LINE_TO:
        points(i, 1);
        i += 2;
        break;
      case CURVE_TO:
        points(i, 3);
        i += 6;
        break;
      case QUADRATIC_CURVE_TO:
        points(i, 2);
        i += 4;
        break;
      case CLOSE_PATH:
        break;
      default:
        return whole;
    }
  }
  if (box) out.push(box);
  return out;
}
