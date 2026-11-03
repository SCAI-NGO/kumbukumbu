// SPDX-License-Identifier: AGPL-3.0-or-later

// Where a page's text lies: each text item's box in displayed points (CropBox, rotation, top-left
// origin), with its offsets into the page's text, the very text the analysis stores. Asked for when
// wanted, never stored.

import type { TextItem } from "pdfjs-dist/types/src/display/api.js";
import type { PageViewport } from "pdfjs-dist/types/src/display/page_viewport.js";
import { type Box, clip } from "./classify.js";
import { PdfError, openPdf } from "./pdfjs.js";

export type ItemBox = { at: number; end: number; x: number; y: number; w: number; h: number };
export type PageBoxes = { n: number; width: number; height: number; text: string; items: ItemBox[] };

/** A page of more items than this (outlined or hostile text) answers no boxes: no use for a region. */
export const MAX_ITEMS = 20_000;

// Items' str in order, a line end after hasEOL, trimmed at the end. A span for each item that is not
// white space, its end kept inside the trimmed text.
export function pageText(items: unknown[]): { text: string; spans: { item: TextItem; at: number; end: number }[] } {
  let text = "";
  const all: { item: TextItem; at: number; end: number }[] = [];
  for (const raw of items) {
    if (!(typeof raw === "object" && raw !== null && "str" in raw)) continue;
    const item = raw as TextItem;
    const at = text.length;
    text += item.str;
    if (item.str.trim() !== "") all.push({ item, at, end: text.length });
    if (item.hasEOL) text += "\n";
  }
  text = text.trimEnd();
  const spans = all.map((s) => ({ ...s, end: Math.min(s.end, text.length) })).filter((s) => s.end > s.at);
  return { text, spans };
}

// pdf.js gives width along the text's direction and height across it, so the corners come from the
// unit vectors of the item's transform, not from x and y.
export function itemBox(item: TextItem, viewport: PageViewport): Box | null {
  const [a, b, c, d, e, f] = item.transform as number[] as [number, number, number, number, number, number];
  const lu = Math.hypot(a, b);
  const lv = Math.hypot(c, d);
  if (!(lu > 0 && lv > 0)) return null;
  const [ux, uy] = [(a / lu) * item.width, (b / lu) * item.width];
  const [vx, vy] = [(c / lv) * item.height, (d / lv) * item.height];
  const corners = [
    [e, f],
    [e + ux, f + uy],
    [e + vx, f + vy],
    [e + ux + vx, f + uy + vy],
  ].map(([x, y]) => viewport.convertToViewportPoint(x!, y!) as [number, number]);
  const xs = corners.map(([x]) => x);
  const ys = corners.map(([, y]) => y);
  const x = Math.min(...xs);
  const y = Math.min(...ys);
  // Text may lie partly off the displayed page; only what lies on it counts.
  const box = clip({ x, y, w: Math.max(...xs) - x, h: Math.max(...ys) - y }, viewport.width, viewport.height);
  return box && { x: round1(box.x), y: round1(box.y), w: round1(box.w), h: round1(box.h) };
}

export function boxesOf(page: { n: number; width: number; height: number }, items: unknown[], viewport: PageViewport, maxItems = MAX_ITEMS): PageBoxes {
  const { text, spans } = pageText(items);
  const boxes: ItemBox[] = [];
  if (items.length <= maxItems)
    for (const { item, at, end } of spans) {
      const box = itemBox(item, viewport);
      if (box) boxes.push({ at, end, ...box });
    }
  return { n: page.n, width: page.width, height: page.height, text, items: boxes };
}

export async function textBoxes(bytes: Uint8Array, pages: number[]): Promise<{ pages: PageBoxes[] }> {
  const doc = await openPdf(bytes);
  try {
    const out: PageBoxes[] = [];
    for (const n of pages) {
      if (!Number.isInteger(n) || n < 1 || n > doc.numPages)
        throw new PdfError(`The PDF has no page ${n}; it has ${doc.numPages}.`, "no_such_page");
      const page = await doc.getPage(n);
      try {
        const viewport = page.getViewport({ scale: 1 });
        const content = await page.getTextContent();
        out.push(boxesOf({ n, width: round1(viewport.width), height: round1(viewport.height) }, content.items, viewport));
      } finally {
        page.cleanup();
      }
    }
    return { pages: out };
  } finally {
    await doc.loadingTask.destroy();
  }
}

const round1 = (v: number) => Math.round(v * 10) / 10;
