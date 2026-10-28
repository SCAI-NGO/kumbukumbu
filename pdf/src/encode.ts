// SPDX-License-Identifier: AGPL-3.0-or-later

// The encoding rule: PNG when it fits the byte target (crisp text and line art), else JPEG at the
// highest of these qualities that fits, else the last one as it is.

import { type Canvas, createCanvas } from "@napi-rs/canvas";

export type Rendered = { data: Uint8Array; mediaType: "image/png" | "image/jpeg"; width: number; height: number };

const QUALITIES = [85, 80, 75, 70, 65, 60];

export async function encode(canvas: Canvas, bytes: number, opaque: boolean): Promise<Rendered> {
  const { width, height } = canvas;
  const png = await canvas.encode("png");
  if (png.length <= bytes) return { data: png, mediaType: "image/png", width, height };

  // JPEG has no transparency: what shows through is white, as on paper.
  let source = canvas;
  if (!opaque) {
    source = createCanvas(width, height);
    const ctx = source.getContext("2d");
    ctx.fillStyle = "#ffffff";
    ctx.fillRect(0, 0, width, height);
    ctx.drawImage(canvas, 0, 0);
  }
  let jpeg = png;
  for (const quality of QUALITIES) {
    jpeg = await source.encode("jpeg", quality);
    if (jpeg.length <= bytes) break;
  }
  return { data: jpeg, mediaType: "image/jpeg", width, height };
}
