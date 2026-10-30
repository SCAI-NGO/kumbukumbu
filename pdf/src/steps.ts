// SPDX-License-Identifier: AGPL-3.0-or-later

// The size a picture is delivered at. The steps themselves live in the server
// (src/Kumbu.Core/Attachments/SizeSteps.cs), which sends the target with every request.

export type Target = { longEdge: number; pixels: number; bytes: number };
export type Crop = { x: number; y: number; w: number; h: number };

// 600 dpi: a PDF point is 1/72 in.
export const MAX_PAGE_SCALE = 600 / 72;

export function fit(w: number, h: number, t: Target, maxScale: number): number {
  return Math.min(t.longEdge / Math.max(w, h), Math.sqrt(t.pixels / (w * h)), maxScale);
}

// Whole pixels within the limits: rounded down, with a little slack for a product such as
// 842 × (1568 / 842) that lands just below the integer it means.
export function pixels(w: number, h: number, scale: number): { width: number; height: number } {
  const side = (v: number) => Math.max(1, Math.floor(v * scale + 1e-6));
  return { width: side(w), height: side(h) };
}

export function inside(c: Crop, width: number, height: number): boolean {
  const slack = 1e-6;
  return c.x >= 0 && c.y >= 0 && c.w > 0 && c.h > 0 && c.x + c.w <= width + slack && c.y + c.h <= height + slack;
}
