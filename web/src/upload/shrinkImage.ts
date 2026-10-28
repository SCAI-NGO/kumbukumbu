// SPDX-License-Identifier: AGPL-3.0-or-later

/** What a shrunk image is encoded as: WebP where the browser can encode it, else JPEG. */
export type ShrinkType = "image/webp" | "image/jpeg";

/** The server's limits for an image (`ImageHeader`): refused here too, before the browser decodes anything. */
export const MAX_PIXELS = 50_000_000;
export const MAX_SIDE = 16_384;
/** How much of the file the header is looked for in, as the server does. */
const HEADER_BYTES = 256 * 1024;

/** First the quality is lowered (0.92 down to 0.5 in steps of 0.07), then the size, by 0.85 per round. */
const QUALITIES = [0.92, 0.85, 0.78, 0.71, 0.64, 0.57, 0.5];
const LOWEST = 0.5;
const SCALE = 0.85;
/** 0.85²⁰ is 4 %: an image that doesn't fit then won't fit at all. */
const MAX_ROUNDS = 20;

/** Why an image couldn't be shrunk, as a reason in the skipped list ("couldn't be shrunk …"). */
export class ShrinkError extends Error {}

/** A decoded image, drawn by `encode` at any size; `close` frees its pixels. */
export interface Decoded {
  image: CanvasImageSource;
  width: number;
  height: number;
  close(): void;
}

/** The browser's decoder and canvas, behind a seam so tests can stand in for them. */
export interface Canvas {
  decode(file: Blob): Promise<Decoded>;
  encode(image: CanvasImageSource, width: number, height: number, type: ShrinkType, quality: number): Promise<Blob>;
}

function draw(context: OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D, image: CanvasImageSource, width: number, height: number, type: ShrinkType) {
  // JPEG has no transparency: transparent pixels would turn black.
  if (type === "image/jpeg") {
    context.fillStyle = "#fff";
    context.fillRect(0, 0, width, height);
  }
  context.drawImage(image, 0, 0, width, height);
}

export const browserCanvas: Canvas = {
  async decode(file) {
    // The EXIF orientation is applied: the re-encoded file has no EXIF to carry it.
    const bitmap = await createImageBitmap(file, { imageOrientation: "from-image" });
    return { image: bitmap, width: bitmap.width, height: bitmap.height, close: () => bitmap.close() };
  },
  async encode(image, width, height, type, quality) {
    if (typeof OffscreenCanvas !== "undefined") {
      const canvas = new OffscreenCanvas(width, height);
      draw(canvas.getContext("2d")!, image, width, height, type);
      return canvas.convertToBlob({ type, quality });
    }
    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    draw(canvas.getContext("2d")!, image, width, height, type);
    try {
      return await new Promise<Blob>((ok, fail) => canvas.toBlob((blob) => (blob ? ok(blob) : fail(new Error("toBlob"))), type, quality));
    } finally {
      // Safari keeps a canvas's memory until it is sized down.
      canvas.width = canvas.height = 0;
    }
  },
};

let detected: ShrinkType | undefined;

/** WebP if this browser's canvas encodes it (Safari's doesn't), else JPEG. Asked once. */
export function shrinkType(): ShrinkType {
  if (!detected) {
    try {
      const canvas = document.createElement("canvas");
      canvas.width = canvas.height = 1;
      detected = canvas.toDataURL("image/webp").startsWith("data:image/webp") ? "image/webp" : "image/jpeg";
    } catch {
      detected = "image/jpeg";
    }
  }
  return detected;
}

/** The name a shrunk image is uploaded under: the same, with the extension of `type` unless it has one of it already. */
export function shrunkName(name: string, type: ShrinkType): string {
  const keeps = type === "image/webp" ? /\.webp$/i : /\.jpe?g$/i;
  if (keeps.test(name)) return name;
  const dot = name.lastIndexOf(".");
  return `${dot > 0 ? name.slice(0, dot) : name}${type === "image/webp" ? ".webp" : ".jpg"}`;
}

const u16be = (b: Uint8Array, i: number) => (b[i] << 8) | b[i + 1];
const u16le = (b: Uint8Array, i: number) => b[i] | (b[i + 1] << 8);
const u32be = (b: Uint8Array, i: number) => ((b[i] << 24) >>> 0) + ((b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);
const ascii = (b: Uint8Array, i: number, text: string) => Array.from(text).every((c, k) => b[i + k] === c.charCodeAt(0));

function jpegSize(b: Uint8Array): [number, number] | null {
  let pos = 2;
  while (pos < b.length) {
    if (b[pos] !== 0xff) return null;
    while (pos < b.length && b[pos] === 0xff) pos++;
    if (pos >= b.length) return null;
    const marker = b[pos++];
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd8)) continue;
    if (marker === 0xd9 || marker === 0xda || pos + 2 > b.length) return null;
    const length = u16be(b, pos);
    if (length < 2 || pos + length > b.length) return null;
    if (marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc) {
      return length < 7 ? null : [u16be(b, pos + 5), u16be(b, pos + 3)];
    }
    pos += length;
  }
  return null;
}

function webpSize(b: Uint8Array): [number, number] | null {
  if (ascii(b, 12, "VP8 ")) return b.length >= 30 && b[23] === 0x9d && b[24] === 0x01 && b[25] === 0x2a ? [u16le(b, 26) & 0x3fff, u16le(b, 28) & 0x3fff] : null;
  if (ascii(b, 12, "VP8L")) {
    if (b.length < 25 || b[20] !== 0x2f) return null;
    const bits = (b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24)) >>> 0;
    return [(bits & 0x3fff) + 1, ((bits >>> 14) & 0x3fff) + 1];
  }
  if (ascii(b, 12, "VP8X")) return b.length >= 30 ? [(b[24] | (b[25] << 8) | (b[26] << 16)) + 1, (b[27] | (b[28] << 8) | (b[29] << 16)) + 1] : null;
  return null;
}

/** An image's width and height from its header alone, as the server reads it (`ImageHeader`); null when unreadable. */
export function imageSize(b: Uint8Array): [number, number] | null {
  if (b.length >= 24 && b[0] === 0x89 && ascii(b, 1, "PNG") && ascii(b, 12, "IHDR")) return [u32be(b, 16), u32be(b, 20)];
  if (b.length >= 3 && b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return jpegSize(b);
  if (b.length >= 10 && (ascii(b, 0, "GIF87a") || ascii(b, 0, "GIF89a"))) return [u16le(b, 6), u16le(b, 8)];
  if (b.length >= 16 && ascii(b, 0, "RIFF") && ascii(b, 8, "WEBP")) return webpSize(b);
  return null;
}

const megabytes = (bytes: number) => `${Math.round(bytes / (1024 * 1024))} MB`;

/**
 * The image re-encoded small enough for `maxBytes`: as WebP where the browser can (else JPEG), at quality 0.92 down
 * to 0.5, then at 0.5 and 85 % of the size per round, at most 20 rounds. The result keeps the name, with the new
 * format's extension. An image over the server's limits (50 megapixels, 16,384 px on a side) is refused before the
 * browser decodes it: such a file can be small on disk and gigabytes as pixels. Rejects with a `ShrinkError`.
 */
export async function shrinkImage(file: File, maxBytes: number, canvas: Canvas = browserCanvas, preferred?: ShrinkType): Promise<File> {
  const size = imageSize(new Uint8Array(await file.slice(0, HEADER_BYTES).arrayBuffer()));
  if (!size || size[0] <= 0 || size[1] <= 0) throw new ShrinkError("can't be read as an image, so it can't be shrunk");
  if (size[0] > MAX_SIDE || size[1] > MAX_SIDE) throw new ShrinkError("too large to shrink: at most 16,384 px on each side");
  if (size[0] * size[1] > MAX_PIXELS) throw new ShrinkError("too large to shrink: at most 50 megapixels");

  let decoded: Decoded;
  try {
    decoded = await canvas.decode(file);
  } catch {
    throw new ShrinkError("this browser can't open it to shrink it");
  }
  try {
    const steps = [...QUALITIES.map((quality) => ({ scale: 1, quality })), ...Array.from({ length: MAX_ROUNDS }, (_, i) => ({ scale: SCALE ** (i + 1), quality: LOWEST }))];
    let type = preferred ?? shrinkType();
    for (let i = 0; i < steps.length; ) {
      const { scale, quality } = steps[i];
      const width = Math.max(1, Math.round(decoded.width * scale));
      const height = Math.max(1, Math.round(decoded.height * scale));
      let blob: Blob;
      try {
        blob = await canvas.encode(decoded.image, width, height, type, quality);
      } catch {
        throw new ShrinkError("this browser couldn't shrink it");
      }
      // A canvas that can't encode the type asked for gives PNG: JPEG, which every browser encodes, from the start.
      if (blob.type !== type) {
        if (type === "image/jpeg") throw new ShrinkError("this browser couldn't shrink it");
        type = "image/jpeg";
        i = 0;
        continue;
      }
      if (blob.size <= maxBytes) return new File([blob], shrunkName(file.name, type), { type, lastModified: file.lastModified });
      i++;
    }
    throw new ShrinkError(`couldn't be shrunk to ${megabytes(maxBytes)}`);
  } finally {
    decoded.close();
  }
}
