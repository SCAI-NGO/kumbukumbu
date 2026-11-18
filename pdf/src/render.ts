// SPDX-License-Identifier: AGPL-3.0-or-later

// Renders a page, or a region of it, to a size target. Regions are in points of the page as
// displayed (CropBox, rotation, top-left origin), as the analysis reports them, and are rendered
// anew at the target's size so vector drawings stay sharp. A turn (clockwise, in degrees) comes on
// top of the page's own rotation; a region then counts in the turned page, while the page's size
// is reported without the turn.

import { type Canvas, createCanvas } from "@napi-rs/canvas";
import type { PDFPageProxy } from "pdfjs-dist/legacy/build/pdf.mjs";
import type { PageViewport } from "pdfjs-dist/types/src/display/page_viewport.js";
import { type Rendered, encode, encodeStored } from "./encode.js";
import { MAX_BODY_STORED, MAX_STORED_PIXELS, STORED_DPI, nativeScale, quietly } from "./extract.js";
import { MAX_SIDE } from "./image.js";
import { PdfError, openPdf } from "./pdfjs.js";
import { type Crop, MAX_PAGE_SCALE, type Target, fit, inside, pixels } from "./steps.js";

export type RenderedPage = Rendered & { pageWidth: number; pageHeight: number; rotate: number };

// A crop that lies within a picture is rendered no finer than the picture's own pixels (the client
// test of 2026-11-03 saw a photo enlarged sevenfold into visible steps); text and drawings up to 600 dpi.
export async function renderPage(pdf: Uint8Array, page: number, t: Target, crop?: Crop, rotate = 0): Promise<RenderedPage> {
  return renderWith(
    pdf,
    page,
    crop,
    rotate,
    async (region, p, shown) => {
      const own = crop ? await nativeScale(p, shown, region) : null;
      return fit(region.w, region.h, t, Math.min(MAX_PAGE_SCALE, own ?? Infinity));
    },
    (canvas) => encode(canvas, t.bytes),
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
      ({ w, h }) => Math.min(STORED_DPI / 72, Math.sqrt(MAX_STORED_PIXELS / (w * h)), MAX_SIDE / Math.max(w, h)),
      (canvas) => encodeStored(canvas, MAX_BODY_STORED, format),
    ),
  );
}

// The scale for a region in the points of the page as shown, and the encoding of what was rendered.
async function renderWith(
  pdf: Uint8Array,
  page: number,
  crop: Crop | undefined,
  rotate: number,
  scaleFor: (region: Crop, p: PDFPageProxy, shown: PageViewport) => number | Promise<number>,
  encodeAs: (canvas: Canvas) => Promise<Rendered>,
): Promise<RenderedPage> {
  const doc = await openPdf(pdf);
  try {
    if (!Number.isInteger(page) || page < 1 || page > doc.numPages)
      throw new PdfError(`The PDF has no page ${page}; it has ${doc.numPages}.`, "no_such_page");
    const p = await doc.getPage(page);
    try {
      const rotation = (p.rotate + rotate) % 360;
      const shown = p.getViewport({ scale: 1, rotation });
      const region = crop ?? { x: 0, y: 0, w: shown.width, h: shown.height };
      if (!inside(region, shown.width, shown.height))
        throw new PdfError(`The region lies outside the page (${round1(shown.width)} × ${round1(shown.height)} pt).`, "bad_crop");
      const scale = await scaleFor(region, p, shown);
      const { width, height } = pixels(region.w, region.h, scale);
      // The viewport is moved so the region's top-left corner lands at the canvas origin.
      const viewport = p.getViewport({ scale, rotation, offsetX: -region.x * scale, offsetY: -region.y * scale });
      const canvas = createCanvas(width, height);
      await p.render({ canvas: canvas as never, canvasContext: canvas.getContext("2d") as never, viewport }).promise;
      const own = p.getViewport({ scale: 1 });
      return { ...(await encodeAs(canvas)), pageWidth: round1(own.width), pageHeight: round1(own.height), rotate };
    } finally {
      p.cleanup();
    }
  } finally {
    await doc.loadingTask.destroy();
  }
}

const round1 = (v: number) => Math.round(v * 10) / 10;
