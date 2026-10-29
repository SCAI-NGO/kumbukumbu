// SPDX-License-Identifier: AGPL-3.0-or-later

// Scales an image to a size target, never enlarging it. Skia applies the EXIF orientation when it
// decodes a JPEG, so sizes and crops are in the image's displayed pixels, as the server reports
// them. The header is checked before anything is decoded.

import { createCanvas, loadImage } from "@napi-rs/canvas";
import { type Rendered, encode } from "./encode.js";
import { MAX_IMAGE_PIXELS, PdfError } from "./pdfjs.js";
import { type Crop, type Target, fit, inside, pixels } from "./steps.js";

export const MAX_SIDE = 16_384;

export type RenderedImage = Rendered & { originalWidth: number; originalHeight: number };

export async function renderImage(img: Uint8Array, t: Target, crop?: Crop): Promise<RenderedImage> {
  const header = imageHeader(img);
  if (!header || header.width <= 0 || header.height <= 0) throw new PdfError("The file is not an image Kumbu² can read.", "not_image");
  if (header.width > MAX_SIDE || header.height > MAX_SIDE)
    throw new PdfError(`The image is too large: at most ${MAX_SIDE.toLocaleString("en-GB")} px on each side.`, "too_large");
  if (header.width * header.height > MAX_IMAGE_PIXELS)
    throw new PdfError(`The image is too large: at most ${MAX_IMAGE_PIXELS / 1_000_000} megapixels.`, "too_large");

  let image;
  try {
    // A buffer, never a string: loadImage would take a string for a path or URL.
    image = await loadImage(Buffer.from(img.buffer, img.byteOffset, img.byteLength));
  } catch {
    throw new PdfError("The file is not an image Kumbu² can read.", "not_image");
  }
  const { width: ow, height: oh } = image;
  const region = crop ?? { x: 0, y: 0, w: ow, h: oh };
  if (!inside(region, ow, oh)) throw new PdfError(`The region lies outside the image (${ow} × ${oh} px).`, "bad_crop");

  const { width, height } = pixels(region.w, region.h, fit(region.w, region.h, t, 1));
  const canvas = createCanvas(width, height);
  const ctx = canvas.getContext("2d");
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = "high";
  ctx.drawImage(image, region.x, region.y, region.w, region.h, 0, 0, width, height);
  return { ...(await encode(canvas, t.bytes, false)), originalWidth: ow, originalHeight: oh };
}

// The stored size from the header (PNG, JPEG, GIF, WebP), or null. Reads lengths and sizes only.
export function imageHeader(b: Uint8Array): { width: number; height: number } | null {
  const v = new DataView(b.buffer, b.byteOffset, b.byteLength);
  const ascii = (at: number, s: string) => at + s.length <= b.length && [...s].every((c, i) => b[at + i] === c.charCodeAt(0));
  const size = (width: number, height: number) => ({ width, height });

  if (ascii(0, "\x89PNG\r\n\x1a\n")) return b.length >= 24 && ascii(12, "IHDR") ? size(v.getUint32(16), v.getUint32(20)) : null;
  if (b.length >= 3 && b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return jpeg(b, v);
  if (ascii(0, "GIF87a") || ascii(0, "GIF89a")) return b.length >= 10 ? size(v.getUint16(6, true), v.getUint16(8, true)) : null;
  if (ascii(0, "RIFF") && ascii(8, "WEBP")) {
    if (ascii(12, "VP8 "))
      return b.length >= 30 && b[23] === 0x9d && b[24] === 0x01 && b[25] === 0x2a
        ? size(v.getUint16(26, true) & 0x3fff, v.getUint16(28, true) & 0x3fff)
        : null;
    if (ascii(12, "VP8L")) {
      if (b.length < 25 || b[20] !== 0x2f) return null;
      const bits = v.getUint32(21, true);
      return size((bits & 0x3fff) + 1, ((bits >>> 14) & 0x3fff) + 1);
    }
    if (ascii(12, "VP8X")) return b.length >= 30 ? size(1 + (b[24]! | (b[25]! << 8) | (b[26]! << 16)), 1 + (b[27]! | (b[28]! << 8) | (b[29]! << 16))) : null;
  }
  return null;
}

// Walks the segments by their lengths up to the frame header (SOFn).
function jpeg(b: Uint8Array, v: DataView): { width: number; height: number } | null {
  let pos = 2;
  while (pos < b.length) {
    if (b[pos] !== 0xff) return null;
    while (pos < b.length && b[pos] === 0xff) pos++;
    if (pos >= b.length) return null;
    const marker = b[pos++]!;
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd8)) continue; // no length
    if (marker === 0xd9 || marker === 0xda || pos + 2 > b.length) return null;
    const length = v.getUint16(pos);
    if (length < 2 || pos + length > b.length) return null;
    if (marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc)
      return length < 7 ? null : { width: v.getUint16(pos + 5), height: v.getUint16(pos + 3) };
    pos += length;
  }
  return null;
}
