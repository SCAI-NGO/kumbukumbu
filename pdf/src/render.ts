// SPDX-License-Identifier: AGPL-3.0-or-later

// Renders a page, or a region of it, to a size target. Regions are in points of the page as
// displayed (CropBox, rotation, top-left origin), as the analysis reports them, and are rendered
// anew at the target's size so vector drawings stay sharp.

import { createCanvas } from "@napi-rs/canvas";
import { type Rendered, encode } from "./encode.js";
import { PdfError, openPdf } from "./pdfjs.js";
import { type Crop, MAX_PAGE_SCALE, type Target, fit, inside, pixels } from "./steps.js";

export type RenderedPage = Rendered & { pageWidth: number; pageHeight: number };

export async function renderPage(pdf: Uint8Array, page: number, t: Target, crop?: Crop): Promise<RenderedPage> {
  const doc = await openPdf(pdf);
  try {
    if (!Number.isInteger(page) || page < 1 || page > doc.numPages)
      throw new PdfError(`The PDF has no page ${page}; it has ${doc.numPages}.`, "no_such_page");
    const p = await doc.getPage(page);
    try {
      const shown = p.getViewport({ scale: 1 });
      const region = crop ?? { x: 0, y: 0, w: shown.width, h: shown.height };
      if (!inside(region, shown.width, shown.height))
        throw new PdfError(`The region lies outside the page (${round1(shown.width)} × ${round1(shown.height)} pt).`, "bad_crop");
      const scale = fit(region.w, region.h, t, MAX_PAGE_SCALE);
      const { width, height } = pixels(region.w, region.h, scale);
      // The viewport is moved so the region's top-left corner lands at the canvas origin.
      const viewport = p.getViewport({ scale, offsetX: -region.x * scale, offsetY: -region.y * scale });
      const canvas = createCanvas(width, height);
      await p.render({ canvas: canvas as never, canvasContext: canvas.getContext("2d") as never, viewport }).promise;
      return { ...(await encode(canvas, t.bytes, true)), pageWidth: round1(shown.width), pageHeight: round1(shown.height) };
    } finally {
      p.cleanup();
    }
  } finally {
    await doc.loadingTask.destroy();
  }
}

const round1 = (v: number) => Math.round(v * 10) / 10;
