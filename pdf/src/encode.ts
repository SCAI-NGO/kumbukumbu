// SPDX-License-Identifier: AGPL-3.0-or-later

// The encoding rule: PNG when it fits the byte target (crisp text and line art), else JPEG at the
// highest of these qualities that fits, else the last one as it is.

import type { Canvas } from "@napi-rs/canvas";

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
