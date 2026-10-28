// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from "vitest";
import { imageSize, shrinkImage, shrunkName, type Canvas, type ShrinkType } from "./shrinkImage";

const MB = 1024 * 1024;

/** A PNG's signature and IHDR chunk: all a header read needs. */
function pngHeader(width: number, height: number): Uint8Array<ArrayBuffer> {
  const b = new Uint8Array(33);
  b.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52]);
  new DataView(b.buffer).setUint32(16, width);
  new DataView(b.buffer).setUint32(20, height);
  return b;
}

const png = (name: string, width: number, height: number) => new File([pngHeader(width, height)], name, { type: "image/png" });

/**
 * A canvas whose encoded size is `bytesPerPixel(quality)` per pixel; `encodes` the types it can encode (others come
 * out as PNG, as a real canvas does).
 */
function fakeCanvas(width: number, height: number, bytesPerPixel: (quality: number) => number, encodes: string[] = ["image/webp", "image/jpeg"]) {
  const close = vi.fn();
  const calls: { width: number; height: number; type: string; quality: number }[] = [];
  const canvas: Canvas = {
    decode: vi.fn(async () => ({ image: {} as CanvasImageSource, width, height, close })),
    encode: vi.fn(async (_image, w, h, type: ShrinkType, quality) => {
      calls.push({ width: w, height: h, type, quality });
      const out = encodes.includes(type) ? type : "image/png";
      // A blob that only says how large it is: the sizes here go far past what a test should allocate.
      const blob = new Blob([], { type: out });
      Object.defineProperty(blob, "size", { value: Math.round(w * h * bytesPerPixel(quality)) });
      return blob;
    }),
  };
  return { canvas, close, calls };
}

describe("shrinkImage", () => {
  it("lowers the quality first, and keeps the name with the new extension", async () => {
    // 4000 × 3000 at 2 bytes per pixel times the quality: 22 MB at 0.92, 14.6 MB at 0.64.
    const { canvas, close, calls } = fakeCanvas(4000, 3000, (q) => q * 2);
    const out = await shrinkImage(png("Holiday shot.png", 4000, 3000), 15 * MB, canvas, "image/webp");

    expect(calls.map((c) => c.quality)).toEqual([0.92, 0.85, 0.78, 0.71, 0.64]);
    expect(calls.every((c) => c.width === 4000 && c.height === 3000 && c.type === "image/webp")).toBe(true);
    expect(out.name).toBe("Holiday shot.webp");
    expect(out.type).toBe("image/webp");
    expect(out.size).toBeLessThanOrEqual(15 * MB);
    expect(close).toHaveBeenCalledOnce();
  });

  it("then lowers the size by 0.85 per round at quality 0.5", async () => {
    // At 0.5 it is still 2 bytes per pixel: 24 MB, 17.3 MB, 12.5 MB.
    const { canvas, calls } = fakeCanvas(4000, 3000, () => 2);
    const out = await shrinkImage(png("big.png", 4000, 3000), 15 * MB, canvas, "image/jpeg");
    expect(calls.slice(7).map((c) => [c.width, c.height, c.quality])).toEqual([
      [3400, 2550, 0.5],
      [2890, 2167, 0.5],
    ]);
    expect(out.name).toBe("big.jpg");
  });

  it("keeps a JPEG's extension when it stays JPEG", async () => {
    const { canvas } = fakeCanvas(10, 10, () => 1);
    expect((await shrinkImage(new File([pngHeader(10, 10)], "photo.JPEG"), 15 * MB, canvas, "image/jpeg")).name).toBe("photo.JPEG");
  });

  it("falls back to JPEG when the canvas can't encode WebP", async () => {
    const { canvas, calls } = fakeCanvas(10, 10, () => 1, ["image/jpeg"]);
    const out = await shrinkImage(png("a.png", 10, 10), 15 * MB, canvas, "image/webp");
    expect(calls.map((c) => c.type)).toEqual(["image/webp", "image/jpeg"]);
    expect(out.name).toBe("a.jpg");
    expect(out.type).toBe("image/jpeg");
  });

  it("gives up after a bounded number of rounds, saying so", async () => {
    const { canvas, close, calls } = fakeCanvas(4000, 3000, () => 1e9);
    await expect(shrinkImage(png("noise.png", 4000, 3000), 15 * MB, canvas, "image/webp")).rejects.toThrow("couldn't be shrunk to 15 MB");
    expect(calls).toHaveLength(27);
    expect(close).toHaveBeenCalledOnce();
  });

  it("refuses an image over 50 megapixels or 16,384 px on a side before decoding it", async () => {
    const { canvas } = fakeCanvas(1, 1, () => 1);
    await expect(shrinkImage(png("bomb.png", 50_000, 50_000), 15 * MB, canvas)).rejects.toThrow("at most 16,384 px on each side");
    await expect(shrinkImage(png("wide.png", 10_000, 6_000), 15 * MB, canvas)).rejects.toThrow("at most 50 megapixels");
    await expect(shrinkImage(new File(["not an image"], "x.png"), 15 * MB, canvas)).rejects.toThrow("can't be read as an image");
    expect(canvas.decode).not.toHaveBeenCalled();
  });

  it("reads a JPEG's size past 300 KB of metadata before its frame header", async () => {
    const { canvas } = fakeCanvas(4000, 3000, () => 1);
    const parts: Uint8Array<ArrayBuffer>[] = [new Uint8Array([0xff, 0xd8])];
    for (let i = 0; i < 5; i++) {
      const segment = new Uint8Array(4 + 0xfffd);
      segment.set([0xff, 0xe2, 0xff, 0xff]);
      parts.push(segment);
    }
    // A frame header claiming 20,000 × 3,000: refused, which only a read past the first 256 KB can tell.
    parts.push(new Uint8Array([0xff, 0xc0, 0, 11, 8, 0x0b, 0xb8, 0x4e, 0x20, 1, 1, 0x11, 0, 0xff, 0xd9]));
    const file = new File(parts, "photo.jpg", { type: "image/jpeg" });
    expect(file.size).toBeGreaterThan(300 * 1024);
    await expect(shrinkImage(file, 15 * MB, canvas)).rejects.toThrow("at most 16,384 px on each side");
    expect(canvas.decode).not.toHaveBeenCalled();
  });

  it("says so when the browser can't decode the image", async () => {
    const { canvas } = fakeCanvas(1, 1, () => 1);
    canvas.decode = vi.fn(async () => {
      throw new Error("InvalidStateError");
    });
    await expect(shrinkImage(png("a.png", 10, 10), 15 * MB, canvas)).rejects.toThrow("this browser can't open it to shrink it");
  });
});

describe("shrunkName", () => {
  it("changes the extension only when the format changes", () => {
    expect(shrunkName("a.png", "image/webp")).toBe("a.webp");
    expect(shrunkName("a.gif", "image/jpeg")).toBe("a.jpg");
    expect(shrunkName("a.jpg", "image/jpeg")).toBe("a.jpg");
    expect(shrunkName("a.webp", "image/webp")).toBe("a.webp");
    expect(shrunkName("a.jpeg", "image/webp")).toBe("a.webp");
  });
});

describe("imageSize", () => {
  it("reads PNG, JPEG, GIF and WebP headers", () => {
    expect(imageSize(pngHeader(1440, 3120))).toEqual([1440, 3120]);
    const jpeg = new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0, 4, 0, 0, 0xff, 0xc0, 0, 11, 8, 0x0c, 0x30, 0x05, 0xa0, 3, 1, 0x22, 0]);
    expect(imageSize(jpeg)).toEqual([1440, 3120]);
    expect(imageSize(new Uint8Array([...new TextEncoder().encode("GIF89a"), 0xa0, 0x05, 0x30, 0x0c]))).toEqual([1440, 3120]);
    const webp = new Uint8Array(30);
    webp.set(new TextEncoder().encode("RIFF"), 0);
    webp.set(new TextEncoder().encode("WEBPVP8X"), 8);
    webp.set([0x9f, 0x05, 0x00, 0x2f, 0x0c, 0x00], 24);
    expect(imageSize(webp)).toEqual([1440, 3120]);
    expect(imageSize(new TextEncoder().encode("%PDF-1.7"))).toBeNull();
  });
});
