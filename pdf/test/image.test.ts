// SPDX-License-Identifier: AGPL-3.0-or-later
import { GifEncoder, createCanvas, loadImage } from "@napi-rs/canvas";
import { GifReader } from "omggif";
import { describe, expect, it, vi } from "vitest";
import { MAX_FRAMES, frames, gifFrameCount, imageHeader, renderImage } from "../src/image.js";
import { type GifFrame, MEDIUM, SMALL, gifOf, gifWithFrameRect } from "./fixtures.js";

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
  it("delivers a transparent image on white, whichever format it ends up in, and says it was transparent", async () => {
    const canvas = createCanvas(300, 300);
    const ctx = canvas.getContext("2d");
    ctx.fillStyle = "#000000";
    ctx.fillRect(200, 200, 100, 100);
    const webp = await canvas.encode("webp", 100);

    for (const t of [MEDIUM, { ...MEDIUM, bytes: 1 }]) {
      const r = await renderImage(webp, t);
      expect(r.transparent).toBe(true);
      expect(r.mediaType).toBe(t.bytes === 1 ? "image/jpeg" : "image/png");
      const [red, green, blue, alpha] = await pixel(r.data, 50, 50);
      for (const c of [red, green, blue]) expect(c).toBeGreaterThan(245);
      expect(alpha).toBe(255);
      const [black] = await pixel(r.data, 250, 250);
      expect(black).toBeLessThan(10);
    }
  });

  it("calls an opaque image not transparent", async () => {
    const r = await renderImage(await solid(100, 100).encode("png"), MEDIUM);
    expect(r.transparent).toBe(false);
  });

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

  it("turns an image by 90, 180 and 270 degrees clockwise, exact to the pixel", async () => {
    // Six colours in a 3 × 2 image; source (x, y) has colours[y * 3 + x].
    const colours = [
      [255, 0, 0],
      [0, 255, 0],
      [0, 0, 255],
      [255, 255, 0],
      [0, 255, 255],
      [255, 0, 255],
    ];
    const canvas = createCanvas(3, 2);
    const ctx = canvas.getContext("2d");
    colours.forEach(([r, g, b], i) => {
      ctx.fillStyle = `rgb(${r},${g},${b})`;
      ctx.fillRect(i % 3, Math.floor(i / 3), 1, 1);
    });
    const png = canvas.toBuffer("image/png");
    // Where each output pixel comes from, after a clockwise turn of the 3 × 2 source.
    const from: Record<number, (x: number, y: number) => [number, number]> = {
      90: (x, y) => [y, 1 - x],
      180: (x, y) => [2 - x, 1 - y],
      270: (x, y) => [2 - y, x],
    };
    for (const rotate of [90, 180, 270]) {
      const r = await renderImage(png, SMALL, undefined, rotate);
      const [w, h] = rotate === 180 ? [3, 2] : [2, 3];
      expect([r.width, r.height], `${rotate}`).toEqual([w, h]);
      for (let y = 0; y < h; y++)
        for (let x = 0; x < w; x++) {
          const [sx, sy] = from[rotate]!(x, y);
          expect(await pixel(r.data, x, y), `${rotate}: ${x},${y}`).toEqual([...colours[sy * 3 + sx]!, 255]);
        }
    }
  });

  it("swaps the sides for 90 and 270, and keeps them for 180", async () => {
    const png = solid(4000, 3000).toBuffer("image/png");
    for (const [rotate, size] of [[90, [948, 1264]], [180, [1264, 948]], [270, [948, 1264]]] as const) {
      const r = await renderImage(png, MEDIUM, undefined, rotate);
      expect([r.width, r.height], `${rotate}`).toEqual(size);
    }
  });

  it("counts a crop in the turned image", async () => {
    // 300 × 200 with a red square of 50 px in the bottom-left corner.
    const canvas = solid(300, 200, "#ffffff");
    const ctx = canvas.getContext("2d");
    ctx.fillStyle = "#ff0000";
    ctx.fillRect(0, 150, 50, 50);
    const png = canvas.toBuffer("image/png");
    // Turned, the square lies at the top left (90), the top right (180) or the bottom right (270).
    for (const [rotate, crop, red, white] of [
      [90, { x: 0, y: 0, w: 100, h: 100 }, [25, 25], [75, 75]],
      [180, { x: 200, y: 0, w: 100, h: 100 }, [75, 25], [25, 75]],
      [270, { x: 100, y: 200, w: 100, h: 100 }, [75, 75], [25, 25]],
    ] as const) {
      const r = await renderImage(png, MEDIUM, crop, rotate);
      expect([r.width, r.height]).toEqual([100, 100]);
      expect(isRed(await pixel(r.data, red[0], red[1])), `${rotate}`).toBe(true);
      expect(await pixel(r.data, white[0], white[1]), `${rotate}`).toEqual([255, 255, 255, 255]);
    }
  });

  it("refuses a crop outside the turned image that fits the unturned one", async () => {
    const png = solid(300, 200).toBuffer("image/png");
    await expect(renderImage(png, MEDIUM, { x: 250, y: 0, w: 50, h: 50 }, 90)).rejects.toMatchObject({ code: "bad_crop" });
    await expect(renderImage(png, MEDIUM, { x: 0, y: 250, w: 50, h: 50 }, 270)).resolves.toMatchObject({ width: 50, height: 50 });
  });

  it("turns on top of the EXIF orientation", async () => {
    // As above: displayed 200 × 400 with the marker at the top left; a further quarter turn brings it
    // to the top right of 400 × 200.
    const canvas = solid(400, 200, "#ffffff");
    const ctx = canvas.getContext("2d");
    ctx.fillStyle = "#ff0000";
    ctx.fillRect(0, 150, 50, 50);
    const jpeg = withSegment(canvas.toBuffer("image/jpeg", 95), exif(6));

    const r = await renderImage(jpeg, MEDIUM, undefined, 90);
    expect([r.width, r.height]).toEqual([400, 200]);
    expect(isRed(await pixel(r.data, 390, 10))).toBe(true);
    expect(isRed(await pixel(r.data, 10, 10))).toBe(false);
  });

  it("reports the size before the turn as the original", async () => {
    const png = solid(4000, 3000).toBuffer("image/png");
    for (const rotate of [90, 180, 270]) {
      const r = await renderImage(png, MEDIUM, undefined, rotate);
      expect([r.originalWidth, r.originalHeight], `${rotate}`).toEqual([4000, 3000]);
    }
  });

  it("keeps an opaque image opaque when it is turned and scaled", async () => {
    // Skia doesn't blend an image's edges: a drawing off by a fraction of a pixel leaves a row or
    // column empty once the fraction reaches half an output pixel. At scale 1 (640 × 480 at small),
    // half a pixel of the image is half a pixel of the output.
    for (const [w, h] of [[1000, 700], [640, 480]]) {
      const png = solid(w!, h!).toBuffer("image/png");
      for (const rotate of [90, 180, 270]) {
        const r = await renderImage(png, SMALL, undefined, rotate);
        expect(r.transparent, `${w} × ${h}, ${rotate}`).toBe(false);
      }
    }
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

const [RED, BLUE, GREEN, BLACK] = [0xff0000, 0x0000ff, 0x00ff00, 0x000000];
const [red, blue, green, white] = [[255, 0, 0, 255], [0, 0, 255, 255], [0, 255, 0, 255], [255, 255, 255, 255]];

// A frame of one colour; the second palette entry is only there to make the palette's size two.
const fill = (colour: number, rect: { x?: number; y?: number; width: number; height: number }, more: Partial<GifFrame> = {}): GifFrame => ({
  ...rect,
  palette: [colour, BLACK],
  pixels: 0,
  ...more,
});

const frame = (gif: Uint8Array, n: number, t = SMALL) => renderImage(gif, t, undefined, 0, n);

// The bytes of gifWithFrameRect() without its global palette: the frame then has no palette at all.
function gifWithoutPalette() {
  const b = gifWithFrameRect(4, 4, 4, 4);
  return new Uint8Array([...b.subarray(0, 10), 0, ...b.subarray(11, 13), ...b.subarray(19)]);
}

const notOutside = { code: "not_image", message: "A frame of the GIF lies outside its canvas." };

describe("renderImage with a frame", () => {
  it("composes frame 2 over frame 1 with a partial rectangle", async () => {
    const gif = gifOf(10, 10, [fill(RED, { width: 10, height: 10 }), fill(BLUE, { x: 2, y: 2, width: 4, height: 4 })]);
    const r = await frame(gif, 2);
    expect([r.width, r.height]).toEqual([10, 10]);
    expect(await pixel(r.data, 3, 3)).toEqual(blue);
    expect(await pixel(r.data, 0, 0)).toEqual(red);
    expect(await pixel(r.data, 8, 8)).toEqual(red);
    expect(r.transparent).toBe(false);
  });

  it("clears a frame's rectangle for disposal 2", async () => {
    const gif = gifOf(10, 10, [
      fill(RED, { width: 10, height: 10 }),
      fill(BLUE, { x: 2, y: 2, width: 4, height: 4 }, { disposal: 2 }),
      fill(GREEN, { width: 2, height: 2 }),
    ]);
    const r = await frame(gif, 3);
    expect(await pixel(r.data, 3, 3)).toEqual(white);
    expect(await pixel(r.data, 8, 8)).toEqual(red);
    expect(await pixel(r.data, 1, 1)).toEqual(green);
    expect(r.transparent).toBe(true);
  });

  it("restores what lay under a frame for disposal 3", async () => {
    const gif = gifOf(10, 10, [
      fill(RED, { width: 10, height: 10 }),
      fill(BLUE, { x: 2, y: 2, width: 4, height: 4 }, { disposal: 3 }),
      fill(GREEN, { width: 2, height: 2 }),
    ]);
    const r = await frame(gif, 3);
    expect(await pixel(r.data, 3, 3)).toEqual(red);
    expect(await pixel(r.data, 1, 1)).toEqual(green);
    expect(await pixel(r.data, 8, 8)).toEqual(red);
  });

  it("lets what lies under show through a transparent index", async () => {
    // Frame 2 covers the canvas: blue on the left half, its transparent index on the right.
    const pixels = Array.from({ length: 100 }, (_, i) => (i % 10 < 5 ? 0 : 1));
    const gif = gifOf(10, 10, [fill(RED, { width: 10, height: 10 }), { width: 10, height: 10, palette: [BLUE, BLACK], pixels, transparent: 1 }]);
    const r = await frame(gif, 2);
    expect(await pixel(r.data, 2, 5)).toEqual(blue);
    expect(await pixel(r.data, 8, 5)).toEqual(red);
  });

  it("shows the last frame before its own disposal", async () => {
    const gif = gifOf(10, 10, [fill(RED, { width: 10, height: 10 }), fill(BLUE, { x: 2, y: 2, width: 4, height: 4 }, { disposal: 2 })]);
    const r = await frame(gif, 2);
    expect(await pixel(r.data, 3, 3)).toEqual(blue);
  });

  it("shows frame 1 through the composing path as the plain still shows it", async () => {
    // Four colours in stripes over the whole canvas, then a second frame that the still doesn't show.
    const pixels = Array.from({ length: 12 * 8 }, (_, i) => Math.floor((i % 12) / 3));
    const gif = gifOf(12, 8, [{ width: 12, height: 8, palette: [RED, GREEN, BLUE, BLACK], pixels }, fill(BLUE, { width: 12, height: 8 })]);
    const [still, first] = [await renderImage(gif, SMALL), await frame(gif, 1)];
    expect([first.width, first.height]).toEqual([still.width, still.height]);
    for (let y = 0; y < 8; y++) for (let x = 0; x < 12; x++) expect(await pixel(first.data, x, y), `${x},${y}`).toEqual(await pixel(still.data, x, y));
  });

  it("delivers transparency left by composing on white, and says so", async () => {
    // Frame 1 is cleared after it is shown, frame 2 covers a corner: the rest of the canvas is empty.
    const gif = gifOf(10, 10, [fill(RED, { x: 2, y: 2, width: 4, height: 4 }, { disposal: 2 }), fill(BLUE, { x: 6, y: 6, width: 2, height: 2 })]);
    const r = await frame(gif, 2);
    expect(r.transparent).toBe(true);
    expect(await pixel(r.data, 7, 7)).toEqual(blue);
    expect(await pixel(r.data, 3, 3)).toEqual(white);
    expect(await pixel(r.data, 0, 0)).toEqual(white);
  });

  it("turns and crops a frame", async () => {
    // 20 × 10 red; frame 2 puts a blue square of 5 px in the top-left corner, which a quarter turn
    // clockwise brings to the top right of 10 × 20.
    const gif = gifOf(20, 10, [fill(RED, { width: 20, height: 10 }), fill(BLUE, { width: 5, height: 5 })]);
    const whole = await renderImage(gif, SMALL, undefined, 90, 2);
    expect([whole.width, whole.height]).toEqual([10, 20]);
    expect(await pixel(whole.data, 7, 2)).toEqual(blue);
    expect(await pixel(whole.data, 2, 2)).toEqual(red);
    const corner = await renderImage(gif, SMALL, { x: 5, y: 0, w: 5, h: 5 }, 90, 2);
    expect([corner.width, corner.height]).toEqual([5, 5]);
    for (const [x, y] of [[0, 0], [4, 4]] as const) expect(await pixel(corner.data, x, y)).toEqual(blue);
    expect(corner.frame).toBe(2);
  });

  it("refuses a frame past the end with no_such_frame", async () => {
    const gif = gifOf(10, 10, [fill(RED, { width: 10, height: 10 }), fill(BLUE, { width: 10, height: 10 })]);
    await expect(frame(gif, 3)).rejects.toMatchObject({ code: "no_such_frame" });
  });

  it("refuses a frame rectangle past the canvas before decoding it", async () => {
    // Decoded, the frame would take 3.6 GB; a failed allocation would also be not_image, so the
    // sentence tells the check from it.
    await expect(frame(gifWithFrameRect(10, 10, 60_000, 60_000), 1)).rejects.toMatchObject(notOutside);
  });

  it("refuses a frame past the canvas's width", async () => {
    await expect(frame(gifWithFrameRect(10, 10, 6, 1, 5, 0), 1)).rejects.toMatchObject(notOutside);
  });

  it("refuses a frame past the canvas's height", async () => {
    await expect(frame(gifWithFrameRect(10, 10, 1, 6, 0, 5), 1)).rejects.toMatchObject(notOutside);
  });

  it("refuses to compose a GIF over 16 megapixels", async () => {
    // 5000 × 4000 is 20 MP: within the header limits of the plain path, which goes on to decode it
    // (Skia makes an empty canvas of the garbage after the header).
    const header = new Uint8Array([...new TextEncoder().encode("GIF89a"), 0x88, 0x13, 0xa0, 0x0f, 0, 0, 0, ...new Array(32).fill(0xee)]);
    await expect(frame(header, 1)).rejects.toMatchObject({ code: "too_large", message: "The GIF is too large for a frame: at most 16 megapixels." });
    await expect(renderImage(header, SMALL)).resolves.toMatchObject({ originalWidth: 5000, originalHeight: 4000 });
  });

  it("logs nothing while decoding a frame with a short stream", async () => {
    const log = vi.spyOn(console, "log");
    try {
      const r = await frame(gifWithFrameRect(4, 4, 4, 4), 1);
      expect([r.width, r.height, r.frame]).toEqual([4, 4, 1]);
      expect(log).not.toHaveBeenCalled();
      expect(console.log).toBe(log);
    } finally {
      log.mockRestore();
    }
  });

  it("refuses a frame with no palette", async () => {
    await expect(frame(gifWithoutPalette(), 1)).rejects.toMatchObject({ code: "not_image" });
  });

  it("refuses a frame of a file that is no GIF", async () => {
    await expect(frame(solid(10, 10).toBuffer("image/png"), 1)).rejects.toMatchObject({ code: "not_image" });
  });

  it("refuses a frame of a GIF with no image in it", async () => {
    // Cut after the header and the global palette: omggif counts no frames and doesn't throw.
    const gif = gifWithFrameRect(4, 4, 4, 4).subarray(0, 19);
    await expect(frame(gif, 1)).rejects.toMatchObject({ code: "not_image" });
    await expect(renderImage(gif, SMALL)).rejects.toMatchObject({ code: "not_image" });
  });

  it("refuses a frame of a truncated GIF", async () => {
    const gif = gifOf(10, 10, [fill(RED, { width: 10, height: 10 }), fill(BLUE, { width: 10, height: 10 })]);
    await expect(frame(gif.subarray(0, gif.length - 6), 1)).rejects.toMatchObject({ code: "not_image" });
  });

  it("frames counts 3 for a GIF of three, 1 for a still GIF, and nothing for a PNG", () => {
    const one = fill(RED, { width: 10, height: 10 });
    expect(frames(gifOf(10, 10, [one, one, one]))).toBe(3);
    expect(frames(gifOf(10, 10, [one]))).toBe(1);
    expect(frames(solid(10, 10).toBuffer("image/png"))).toBeUndefined();
  });

  it("frames gives nothing, and doesn't throw, for a truncated GIF", () => {
    const gif = gifOf(10, 10, [fill(RED, { width: 10, height: 10 }), fill(BLUE, { width: 10, height: 10 })]);
    expect(frames(gif.subarray(0, gif.length - 6))).toBeUndefined();
  });

  it("renderImage reports the frames of a plain GIF", async () => {
    const one = fill(RED, { width: 10, height: 10 });
    const r = await renderImage(gifOf(10, 10, [one, one, one]), SMALL);
    expect(r.frames).toBe(3);
    expect(r.frame).toBeUndefined();
    expect((await renderImage(solid(10, 10).toBuffer("image/png"), SMALL)).frames).toBeUndefined();
  });

  it("a plain GIF still comes through Skia", async () => {
    // A plain text extension before the frame: Skia skips it, omggif knows no such block and throws.
    const b = gifWithFrameRect(4, 4, 4, 4);
    const gif = new Uint8Array([...b.subarray(0, 19), 0x21, 0x01, 12, ...new Array(12).fill(0), 0, ...b.subarray(19)]);
    const r = await renderImage(gif, SMALL);
    expect([r.width, r.height]).toEqual([4, 4]);
    expect(r.frames).toBeUndefined();
  });
});

// A GIF of n frames of 1 × 1 px, written byte by byte: each a clear code, index 0 and an end code
// in 3-bit codes.
function gifOfTinyFrames(n: number) {
  const frame = [0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0];
  const b = new Uint8Array(19 + n * frame.length + 1);
  b.set([...new TextEncoder().encode("GIF89a"), 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xff, 0xff, 0xff]);
  for (let i = 0; i < n; i++) b.set(frame, 19 + i * frame.length);
  b[b.length - 1] = 0x3b;
  return b;
}

describe("the limit on frames", () => {
  it("counts a GIF of 10,000 frames, and composes its last", async () => {
    const gif = gifOfTinyFrames(10_000);
    expect(MAX_FRAMES).toBe(10_000);
    expect(gifFrameCount(gif)).toBe(10_000);
    expect(frames(gif)).toBe(10_000);
    const r = await frame(gif, 10_000);
    expect(r.frames).toBe(10_000);
  });

  it("gives no count for a GIF of 10,001 frames, and refuses a frame of it", async () => {
    const gif = gifOfTinyFrames(10_001);
    expect(gifFrameCount(gif)).toBeGreaterThan(MAX_FRAMES);
    expect(frames(gif)).toBeUndefined();
    await expect(frame(gif, 2)).rejects.toMatchObject({ code: "too_large", message: "The GIF has too many frames for a frame: at most 10,000." });
    expect((await renderImage(gif, SMALL)).frames).toBeUndefined();
  });

  it("stops counting once past the limit", () => {
    const gif = gifOfTinyFrames(30_000);
    expect(gifFrameCount(gif)).toBe(MAX_FRAMES + 1);
  });

  it("agrees with omggif on the fixtures", () => {
    const one = fill(RED, { width: 10, height: 10 });
    const fixtures = [
      gifOf(10, 10, [one]),
      gifOf(10, 10, [one, one, one]),
      gifOf(10, 10, [fill(RED, { x: 2, y: 2, width: 4, height: 4 }, { disposal: 2 }), fill(BLUE, { x: 6, y: 6, width: 2, height: 2 }, { disposal: 3 })]),
      gifWithFrameRect(4, 4, 4, 4),
      gifOfTinyFrames(25),
    ];
    for (const gif of fixtures) expect(gifFrameCount(gif)).toBe(new GifReader(gif).numFrames());
  });

  it("gives no count, and doesn't throw, for a truncated block or what is no GIF", () => {
    const gif = gifOf(10, 10, [fill(RED, { width: 10, height: 10 }), fill(BLUE, { width: 10, height: 10 })]);
    expect(gifFrameCount(gif.subarray(0, gif.length - 6))).toBeUndefined();
    expect(gifFrameCount(gifOfTinyFrames(3).subarray(0, 19 + 12))).toBeUndefined();
    expect(gifFrameCount(gif.subarray(0, 8))).toBeUndefined();
    expect(gifFrameCount(solid(10, 10).toBuffer("image/png"))).toBeUndefined();
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
