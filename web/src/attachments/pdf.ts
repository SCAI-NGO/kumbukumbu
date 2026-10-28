// SPDX-License-Identifier: AGPL-3.0-or-later
import type { PDFDocumentLoadingTask, PDFPageProxy, PageViewport } from "pdfjs-dist/legacy/build/pdf.mjs";
// The worker as an asset of the build, served from the app's own origin, never a CDN.
import workerUrl from "pdfjs-dist/legacy/build/pdf.worker.min.mjs?url";

/**
 * How every PDF is opened (attachments design, "pdf.js"). A PDF is untrusted: nothing in it may run code.
 * - `isEvalSupported: false`: no code is compiled from a PDF. pdf.js 6 has dropped the option, as it no longer
 *   compiles PostScript functions with `eval` at all; it stays here, explicit, for any build that still reads it,
 *   and the Content-Security-Policy refuses `eval` whatever pdf.js asks.
 * - `useWasm: false`: no WebAssembly, which the CSP would need `'wasm-unsafe-eval'` for. JPEG 2000 and JBIG2 images
 *   (scans) are decoded by pdf.js's plain-script stand-ins from `wasmUrl` instead (vite.config.ts serves them);
 *   colours of embedded ICC profiles are approximated, without colour management.
 * - `enableXfa: false`: no XFA forms. There is no scripting either: that lives in pdf.js's viewer and annotation
 *   layer, and the app uses neither, only the page canvases and its own links (`pageLinks`).
 * - `maxImageSize`: no image over the 50 megapixels the server takes for an image file, against decompression bombs.
 * The legacy build: the modern one relies on JavaScript only the newest browsers have, which older phones lack.
 */
export const pdfOptions = {
  isEvalSupported: false,
  useWasm: false,
  wasmUrl: new URL(__PDF_DECODERS__, window.location.origin).href,
  enableXfa: false,
  maxImageSize: 50_000_000,
};

let pdfjs: Promise<typeof import("pdfjs-dist/legacy/build/pdf.mjs")> | undefined;

/** pdf.js itself, in a chunk of its own, loaded the first time a PDF is shown. */
function loadPdfJs() {
  pdfjs ??= import("pdfjs-dist/legacy/build/pdf.mjs").then((module) => {
    module.GlobalWorkerOptions.workerSrc = workerUrl;
    return module;
  });
  return pdfjs;
}

/** Opens a PDF from its bytes; `destroy()` on the task ends it, its worker included. */
export async function openPdf(data: ArrayBuffer): Promise<PDFDocumentLoadingTask> {
  const { getDocument } = await loadPdfJs();
  return getDocument({ data, ...pdfOptions });
}

/** A link on a page: where it goes, and its box in fractions of the page (0–1, from the top left). */
export interface PageLink {
  /** An address outside the PDF, opened in a new tab. */
  url?: string;
  /** A place inside the PDF (pdf.js's `dest`). */
  dest?: unknown;
  left: number;
  top: number;
  width: number;
  height: number;
}

/** Only these leave the PDF; pdf.js has already refused anything that isn't an absolute URL. */
const safeUrl = (url: unknown): url is string => typeof url === "string" && /^(https?:|mailto:)/i.test(url);

/**
 * The page's links (`Link` annotations) with their boxes. Nothing of pdf.js's annotation layer is used: no forms, no
 * actions, no scripts; a link to an address becomes a plain `<a target="_blank" rel="noopener">`, never navigating
 * the app, and a link inside the PDF scrolls to its page.
 */
export async function pageLinks(page: PDFPageProxy, viewport: PageViewport): Promise<PageLink[]> {
  const annotations: { subtype?: string; url?: unknown; dest?: unknown; rect?: number[] }[] = await page.getAnnotations({ intent: "display" });
  return annotations.flatMap((a) => {
    if (a.subtype !== "Link" || !a.rect) return [];
    const url = safeUrl(a.url) ? a.url : undefined;
    if (!url && !a.dest) return [];
    const [x1, y1] = viewport.convertToViewportPoint(a.rect[0], a.rect[1]);
    const [x2, y2] = viewport.convertToViewportPoint(a.rect[2], a.rect[3]);
    const left = Math.min(x1, x2) / viewport.width;
    const top = Math.min(y1, y2) / viewport.height;
    return [{ url, dest: url ? undefined : a.dest, left, top, width: Math.abs(x2 - x1) / viewport.width, height: Math.abs(y2 - y1) / viewport.height }];
  });
}
