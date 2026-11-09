// SPDX-License-Identifier: AGPL-3.0-or-later

// The encoding rule: PNG when it fits the byte target (crisp text and line art), else JPEG at the
// highest of these qualities that fits, else the last one as it is.

import { type Canvas, createCanvas } from "@napi-rs/canvas";
import { PdfError } from "./pdfjs.js";

export type Rendered = { data: Uint8Array; mediaType: "image/png" | "image/jpeg"; width: number; height: number };

const QUALITIES = [85, 80, 75, 70, 65, 60];

export async function encode(canvas: Canvas, bytes: number): Promise<Rendered> {
  const { width, height } = canvas;
  const png = await canvas.encode("png");
  if (png.length <= bytes) return { data: png, mediaType: "image/png", width, height };

  let jpeg = png;
  for (const quality of QUALITIES) {
    jpeg = await canvas.encode("jpeg", quality);
    if (jpeg.length <= bytes) break;
  }
  return { data: jpeg, mediaType: "image/jpeg", width, height };
}

// For a stored image (an attachment): PNG if it fits, else JPEG at the highest of these qualities
// that fits, else refused; or only the format asked for. Skia writes a transparent pixel as black in
// a JPEG, so a JPEG is flattened onto white first, as on paper.
const STORED_QUALITIES = [90, 85, 80, 75, 70, 65, 60];

export async function encodeStored(canvas: Canvas, bytes: number, format?: "png" | "jpeg"): Promise<Rendered> {
  const { width, height } = canvas;
  const limit = `${Math.round((bytes / (1024 * 1024)) * 10) / 10} MB`;
  if (format !== "jpeg") {
    const png = await canvas.encode("png");
    if (png.length <= bytes) return { data: png, mediaType: "image/png", width, height };
    if (format === "png") throw new PdfError(`The image is over ${limit} as PNG.`, "too_large");
  }
  const flat = onWhite(canvas);
  for (const quality of STORED_QUALITIES) {
    const jpeg = await flat.encode("jpeg", quality);
    if (jpeg.length <= bytes) return { data: jpeg, mediaType: "image/jpeg", width, height };
  }
  throw new PdfError(`The image is over ${limit} even as JPEG at quality 60.`, "too_large");
}

// Clients show transparency as they like, some on black, where a black shape vanishes. On white,
// as on paper, every client sees the same picture.
export function onWhite(canvas: Canvas): Canvas {
  const flat = createCanvas(canvas.width, canvas.height);
  const ctx = flat.getContext("2d");
  ctx.fillStyle = "#ffffff";
  ctx.fillRect(0, 0, flat.width, flat.height);
  ctx.drawImage(canvas, 0, 0);
  return flat;
}
