// SPDX-License-Identifier: AGPL-3.0-or-later

// The one place that opens PDFs: every analysis and render goes through openPdf, so the hardening
// below applies to each of them.

import { createRequire } from "node:module";
import { dirname, join } from "node:path";
import { getDocument, type PDFDocumentProxy } from "pdfjs-dist/legacy/build/pdf.mjs";

const root = dirname(createRequire(import.meta.url).resolve("pdfjs-dist/package.json"));
// pdf.js in Node reads these with fs; the paths must end in a separator.
const asset = (folder: string) => join(root, folder) + "/";

export const MAX_IMAGE_PIXELS = 50_000_000;

export class PdfError extends Error {
  constructor(
    message: string,
    readonly code: string,
  ) {
    super(message);
    this.name = "PdfError";
  }
}

export async function openPdf(bytes: Uint8Array): Promise<PDFDocumentProxy> {
  const task = getDocument({
    // pdf.js may take over the buffer it is given; the caller keeps its own.
    data: bytes.slice(),
    // No isEvalSupported: pdf.js 6 no longer compiles fonts with eval, and the option is gone. No
    // scripting either: that lives in pdf.js's viewer, which is not loaded.
    enableXfa: false,
    disableFontFace: true,
    useSystemFonts: false,
    stopAtErrors: false,
    verbosity: 0,
    standardFontDataUrl: asset("standard_fonts"),
    cMapUrl: asset("cmaps"),
    cMapPacked: true,
    wasmUrl: asset("wasm"),
    iccUrl: asset("iccs"),
    // The server's own limit (ImageHeader.MaxPixels). A few bytes of Flate can declare an image
    // of gigabytes, decoded outside the V8 heap; with stopAtErrors off pdf.js drops such an
    // image's paint operation instead, so the page measures as if the image were not there.
    maxImageSize: MAX_IMAGE_PIXELS,
  });
  try {
    return await task.promise;
  } catch (e) {
    await task.destroy();
    throw openError(e);
  }
}

export function openError(e: unknown): unknown {
  if (!(e instanceof Error)) return e;
  if (e.name === "InvalidPDFException") return new PdfError("The file is not a PDF.", "not_pdf");
  if (e.name === "PasswordException") return new PdfError("The PDF is protected by a password.", "encrypted");
  return e;
}
