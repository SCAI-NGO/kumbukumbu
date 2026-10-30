// SPDX-License-Identifier: AGPL-3.0-or-later
import { GifEncoder, createCanvas, loadImage } from "@napi-rs/canvas";
import { describe, expect, it } from "vitest";
import { imageHeader, renderImage } from "../src/image.js";
import { MEDIUM, SMALL } from "./fixtures.js";

function solid(width: number, height: number, colour = "#3366cc") {
  const canvas = createCanvas(width, height);
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = colour;
  ctx.fillRect(0, 0, width, height);
  return canvas;
}

// An APP1 segment holding a big-endian TIFF block whose IFD0 has only the orientation tag.
function exif(orientation: number) {
  const tiff = [0x4d, 0x4d, 0, 0x2a, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, orientation, 0, 0, 0, 0, 0, 0];
  const payload = [...new TextEncoder().encode("Exif\0\0"), ...tiff];
  return [0xff, 0xe1, (payload.length + 2) >> 8, (payload.length + 2) & 0xff, ...payload];
}

const withSegment = (jpeg: Uint8Array, segment: number[]) => new Uint8Array([...jpeg.subarray(0, 2), ...segment, ...jpeg.subarray(2)]);

async function pixel(data: Uint8Array, x: number, y: number) {
  const img = await loadImage(Buffer.from(data));
  const canvas = createCanvas(img.width, img.height);
  const ctx = canvas.getContext("2d");
  ctx.drawImage(img, 0, 0);
  return [...ctx.getImageData(x, y, 1, 1).data];
}

const isRed = ([r, g, b]: number[]) => r! > 200 && g! < 60 && b! < 60;

describe("renderImage", () => {
  it("scales a large image to the pixel budget, keeping its aspect", async () => {
    const r = await renderImage(solid(4000, 3000).toBuffer("image/png"), MEDIUM);
    // √(1.2e6 / 1.2e7) ≈ 0.316: 1264 × 948.
    expect([r.width, r.height]).toEqual([1264, 948]);
    expect([r.originalWidth, r.originalHeight]).toEqual([4000, 3000]);
    expect(r.mediaType).toBe("image/png");
  });

  it("never enlarges", async () => {
    const r = await renderImage(solid(300, 200).toBuffer("image/png"), MEDIUM);
    expect([r.width, r.height]).toEqual([300, 200]);
  });

  it("applies the EXIF orientation, and crops in displayed coordinates", async () => {
    // Stored 400 × 200 with a red marker in the bottom-left corner; orientation 6 turns the image a
    // quarter clockwise, which brings that corner to the top left.
    const canvas = solid(400, 200, "#ffffff");
    const ctx = canvas.getContext("2d");
    ctx.fillStyle = "#ff0000";
    ctx.fillRect(0, 150, 50, 50);
    const jpeg = withSegment(canvas.toBuffer("image/jpeg", 95), exif(6));

    const whole = await renderImage(jpeg, MEDIUM);
    expect([whole.originalWidth, whole.originalHeight]).toEqual([200, 400]);
    expect([whole.width, whole.height]).toEqual([200, 400]);

    const top = await renderImage(jpeg, MEDIUM, { x: 0, y: 0, w: 200, h: 100 });
    expect([top.width, top.height]).toEqual([200, 100]);
    expect(isRed(await pixel(top.data, 10, 10))).toBe(true);
    expect(isRed(await pixel(top.data, 190, 10))).toBe(false);
  });

  it("refuses a crop beyond the image", async () => {
    await expect(renderImage(solid(300, 200).toBuffer("image/png"), MEDIUM, { x: 250, y: 0, w: 100, h: 100 })).rejects.toMatchObject({
      code: "bad_crop",
    });
  });

  it("delivers the first frame of an animated GIF", async () => {
    const gif = new GifEncoder(40, 30);
    for (const colour of ["#ff0000", "#0000ff"]) {
      const data = solid(40, 30, colour).getContext("2d").getImageData(0, 0, 40, 30).data;
      gif.addFrame(new Uint8Array(data.buffer, data.byteOffset, data.byteLength), 40, 30);
    }
    const r = await renderImage(gif.finish(), SMALL);
    expect([r.width, r.height]).toEqual([40, 30]);
    expect(isRed(await pixel(r.data, 20, 15))).toBe(true);
  });

  it("refuses a header over 50 MP or 16,384 px a side without decoding", async () => {
    const png = (w: number, h: number) => {
      const b = new Uint8Array(64);
      b.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52]);
      new DataView(b.buffer).setUint32(16, w);
      new DataView(b.buffer).setUint32(20, h);
      return b;
    };
    // The rest is garbage: decoding it would answer not_image.
    await expect(renderImage(png(20_000, 20_000), MEDIUM)).rejects.toMatchObject({ code: "too_large" });
    await expect(renderImage(png(16_385, 10), MEDIUM)).rejects.toMatchObject({ code: "too_large" });
    await expect(renderImage(png(10_000, 6_000), MEDIUM)).rejects.toMatchObject({ code: "too_large" });
    await expect(renderImage(png(100, 100), MEDIUM)).rejects.toMatchObject({ code: "not_image" });
  });

  it("refuses what is not an image", async () => {
    await expect(renderImage(new TextEncoder().encode("hello, not an image"), MEDIUM)).rejects.toMatchObject({ code: "not_image" });
    await expect(renderImage(new Uint8Array(), MEDIUM)).rejects.toMatchObject({ code: "not_image" });
  });
});

describe("imageHeader", () => {
  it("reads PNG, JPEG, GIF and WebP sizes", async () => {
    const c = solid(321, 123);
    expect(imageHeader(c.toBuffer("image/png"))).toEqual({ width: 321, height: 123 });
    expect(imageHeader(c.toBuffer("image/jpeg"))).toEqual({ width: 321, height: 123 });
    expect(imageHeader(await c.encode("webp", 80))).toEqual({ width: 321, height: 123 });
    const gif = new GifEncoder(17, 9);
    gif.addFrame(new Uint8Array(17 * 9 * 4), 17, 9);
    expect(imageHeader(gif.finish())).toEqual({ width: 17, height: 9 });
  });

  it("finds a JPEG's frame header after a large segment", () => {
    const big = [0xff, 0xe2, 0xff, 0xff, ...new Array(0xfffd).fill(0)];
    expect(imageHeader(withSegment(solid(64, 32).toBuffer("image/jpeg"), big))).toEqual({ width: 64, height: 32 });
  });

  it("reads the stored size of VP8 and VP8L WebP", () => {
    const riff = (chunk: string, body: number[]) =>
      new Uint8Array([...new TextEncoder().encode("RIFF"), 0, 0, 0, 0, ...new TextEncoder().encode("WEBP" + chunk), 0, 0, 0, 0, ...body]);
    // VP8: frame tag, start code, 14-bit width and height (little-endian).
    expect(imageHeader(riff("VP8 ", [0, 0, 0, 0x9d, 0x01, 0x2a, 0x40, 0x01, 0xf0, 0x00]))).toEqual({ width: 320, height: 240 });
    // VP8L: signature, then 14-bit width − 1 and height − 1.
    const bits = (320 - 1) | ((240 - 1) << 14);
    expect(imageHeader(riff("VP8L", [0x2f, bits & 0xff, (bits >> 8) & 0xff, (bits >> 16) & 0xff, (bits >> 24) & 0xff]))).toEqual({
      width: 320,
      height: 240,
    });
  });

  it("answers null for anything else or a cut-off header", () => {
    expect(imageHeader(new TextEncoder().encode("GIF89"))).toBeNull();
    expect(imageHeader(new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0]))).toBeNull();
    expect(imageHeader(new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0x00]))).toBeNull();
    expect(imageHeader(new TextEncoder().encode("%PDF-1.7"))).toBeNull();
  });
});
