// SPDX-License-Identifier: AGPL-3.0-or-later

// Scales an image to a size target, never enlarging it. Skia applies the EXIF orientation when it
// decodes a JPEG, so sizes and crops are in the image's displayed pixels, as the server reports
// them; a turn (clockwise, in degrees) comes on top of that, and a crop counts in the turned image.
// A frame of a GIF is composed first, as it looks when played. The header is checked before
// anything is decoded.

import { type Canvas, type Image, ImageData, createCanvas, loadImage } from "@napi-rs/canvas";
import { GifReader } from "omggif";
import { type Rendered, encode } from "./encode.js";
import { MAX_IMAGE_PIXELS, PdfError } from "./pdfjs.js";
import { type Crop, type Target, fit, inside, pixels } from "./steps.js";

export const MAX_SIDE = 16_384;
// Composing holds the canvas several times over (buffer, copy, index stream, canvas), so a frame
// has a lower cap than the plain path.
export const MAX_FRAME_PIXELS = 4_000_000;
// omggif lists every frame as an object when it opens a file, and composing makes garbage for each
// frame before the last: a 16 MiB GIF of some 600,000 tiny frames took the sidecar past its memory
// limit at any canvas size. So frames are counted first, without omggif, and more is refused.
export const MAX_FRAMES = 10_000;

export type RenderedImage = Rendered & {
  originalWidth: number;
  originalHeight: number;
  transparent: boolean;
  rotate: number;
  frame?: number;
  frames?: number;
};

const notImage = () => new PdfError("The file is not an image Kumbu² can read.", "not_image");

// From the image's pixels to the turned frame: whole-number matrices, so a turn at scale 1 moves
// pixels and blends none.
function turn(rotate: number, ow: number, oh: number): [number, number, number, number, number, number] {
  if (rotate === 90) return [0, 1, -1, 0, oh, 0];
  if (rotate === 180) return [-1, 0, 0, -1, ow, oh];
  return [0, -1, 1, 0, 0, ow];
}

export async function renderImage(img: Uint8Array, t: Target, crop?: Crop, rotate = 0, frame?: number): Promise<RenderedImage> {
  const header = imageHeader(img);
  if (!header || header.width <= 0 || header.height <= 0) throw notImage();
  if (header.width > MAX_SIDE || header.height > MAX_SIDE)
    throw new PdfError(`The image is too large: at most ${MAX_SIDE.toLocaleString("en-GB")} px on each side.`, "too_large");
  if (header.width * header.height > MAX_IMAGE_PIXELS)
    throw new PdfError(`The image is too large: at most ${MAX_IMAGE_PIXELS / 1_000_000} megapixels.`, "too_large");

  let image: Image | Canvas;
  let count: number | undefined;
  if (frame === undefined) {
    try {
      // A buffer, never a string: loadImage would take a string for a path or URL.
      image = await loadImage(Buffer.from(img.buffer, img.byteOffset, img.byteLength));
    } catch {
      throw notImage();
    }
    count = frames(img);
  } else {
    if (!isGif(img)) throw notImage();
    if (header.width * header.height > MAX_FRAME_PIXELS)
      throw new PdfError(`The GIF is too large for a frame: at most ${MAX_FRAME_PIXELS / 1_000_000} megapixels.`, "too_large");
    const walked = gifFrameCount(img);
    if (walked === undefined) throw notImage();
    if (walked > MAX_FRAMES)
      throw new PdfError(`The GIF has too many frames for a frame: at most ${MAX_FRAMES.toLocaleString("en-GB")}.`, "too_large");
    let reader: GifReader;
    try {
      reader = new GifReader(img);
    } catch {
      throw notImage();
    }
    count = reader.numFrames();
    // A GIF cut before its first image reads as one of no frames: no frame of it is past the end.
    if (count === 0) throw notImage();
    if (frame > count) throw new PdfError(`The GIF has no frame ${frame}.`, "no_such_frame");
    image = composeFrame(reader, frame);
  }
  const { width: ow, height: oh } = image;
  const [tw, th] = rotate === 90 || rotate === 270 ? [oh, ow] : [ow, oh];
  const region = crop ?? { x: 0, y: 0, w: tw, h: th };
  if (!inside(region, tw, th)) throw new PdfError(`The region lies outside the image (${tw} × ${th} px).`, "bad_crop");

  const { width, height } = pixels(region.w, region.h, fit(region.w, region.h, t, 1));
  const canvas = createCanvas(width, height);
  const ctx = canvas.getContext("2d");
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = "high";
  if (rotate === 0) ctx.drawImage(image, region.x, region.y, region.w, region.h, 0, 0, width, height);
  else {
    ctx.scale(width / region.w, height / region.h);
    ctx.translate(-region.x, -region.y);
    ctx.transform(...turn(rotate, ow, oh));
    ctx.drawImage(image, 0, 0);
  }
  const transparent = hasTransparency(canvas);
  return {
    ...(await encode(transparent ? onWhite(canvas) : canvas, t.bytes)),
    originalWidth: ow,
    originalHeight: oh,
    transparent,
    rotate,
    ...(frame !== undefined ? { frame } : {}),
    ...(count !== undefined ? { frames: count } : {}),
  };
}

const startsWith = (b: Uint8Array, at: number, s: string) => at + s.length <= b.length && [...s].every((c, i) => b[at + i] === c.charCodeAt(0));
const isGif = (b: Uint8Array) => startsWith(b, 0, "GIF87a") || startsWith(b, 0, "GIF89a");

// How many frames a GIF has, or undefined for another format and a GIF omggif can't read (which
// Skia may still draw).
export function frames(bytes: Uint8Array): number | undefined {
  const walked = gifFrameCount(bytes);
  if (walked === undefined || walked > MAX_FRAMES) return undefined;
  try {
    const n = new GifReader(bytes).numFrames();
    return n > 0 ? n : undefined;
  } catch {
    return undefined;
  }
}

// The frames of a GIF counted by walking its blocks, allocating nothing per frame, and stopping at
// MAX_FRAMES + 1; undefined for another format and for blocks that run past the end. Extensions
// are skipped whatever their label: omggif, which knows only some, still decides what it can read.
export function gifFrameCount(b: Uint8Array): number | undefined {
  if (!isGif(b) || b.length < 13) return undefined;
  const palette = (flags: number) => (flags & 0x80 ? 3 << ((flags & 7) + 1) : 0);
  let p = 13 + palette(b[10]!);
  const subBlocks = () => {
    while (p < b.length && b[p] !== 0) p += b[p]! + 1;
    if (p >= b.length) return false;
    p++;
    return true;
  };
  let n = 0;
  while (p < b.length) {
    const block = b[p++];
    if (block === 0x3b) break;
    if (block === 0x21) {
      p++;
      if (!subBlocks()) return undefined;
    } else if (block === 0x2c) {
      if (p + 9 > b.length) return undefined;
      p += 9 + palette(b[p + 8]!) + 1;
      if (!subBlocks()) return undefined;
      if (++n > MAX_FRAMES) break;
    } else return undefined;
  }
  return n;
}

// Frames 1 to n (counting from 1) on a transparent canvas, each but the last disposed of as it
// asks. omggif checks no frame against the canvas and allocates a frame's own rectangle, up to
// 65,535 × 65,535 from a few bytes, so that is checked before each frame is decoded; and it reads a
// frame without a palette from the file's header, so such a frame is refused.
function composeFrame(reader: GifReader, n: number): Canvas {
  const { width, height } = reader;
  const buffer = new Uint8ClampedArray(width * height * 4);
  let saved: Uint8ClampedArray | undefined;
  // omggif warns with console.log once per decoded frame on a short stream, which a 16 MiB GIF of
  // tiny frames could turn into over a million lines; the sidecar logs nothing about a file.
  const log = console.log;
  console.log = () => {};
  try {
    for (let i = 0; i < n; i++) {
      const f = reader.frameInfo(i);
      if (f.x + f.width > width || f.y + f.height > height) throw new PdfError("A frame of the GIF lies outside its canvas.", "not_image");
      if (f.palette_offset === null) throw notImage();
      const last = i === n - 1;
      if (!last && f.disposal === 3) (saved ??= new Uint8ClampedArray(buffer.length)).set(buffer);
      reader.decodeAndBlitFrameRGBA(i, buffer);
      if (last) break;
      if (f.disposal === 2) for (let y = f.y; y < f.y + f.height; y++) buffer.fill(0, (y * width + f.x) * 4, (y * width + f.x + f.width) * 4);
      else if (f.disposal === 3) buffer.set(saved!);
    }
  } catch (e) {
    throw e instanceof PdfError ? e : notImage();
  } finally {
    console.log = log;
  }
  const canvas = createCanvas(width, height);
  // ImageData wraps the buffer without a copy.
  canvas.getContext("2d").putImageData(new ImageData(buffer, width, height), 0, 0);
  return canvas;
}

function hasTransparency(canvas: Canvas): boolean {
  const { data } = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height);
  for (let alpha = 3; alpha < data.length; alpha += 4) if (data[alpha]! < 255) return true;
  return false;
}

// Clients show transparency as they like, some on black, where a black shape vanishes. On white,
// as on paper, every client sees the same picture; the server's text says what became of it.
function onWhite(canvas: Canvas): Canvas {
  const flat = createCanvas(canvas.width, canvas.height);
  const ctx = flat.getContext("2d");
  ctx.fillStyle = "#ffffff";
  ctx.fillRect(0, 0, flat.width, flat.height);
  ctx.drawImage(canvas, 0, 0);
  return flat;
}

// The stored size from the header (PNG, JPEG, GIF, WebP), or null. Reads lengths and sizes only.
export function imageHeader(b: Uint8Array): { width: number; height: number } | null {
  const v = new DataView(b.buffer, b.byteOffset, b.byteLength);
  const ascii = (at: number, s: string) => startsWith(b, at, s);
  const size = (width: number, height: number) => ({ width, height });

  if (ascii(0, "\x89PNG\r\n\x1a\n")) return b.length >= 24 && ascii(12, "IHDR") ? size(v.getUint32(16), v.getUint32(20)) : null;
  if (b.length >= 3 && b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return jpeg(b, v);
  if (isGif(b)) return b.length >= 10 ? size(v.getUint16(6, true), v.getUint16(8, true)) : null;
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
