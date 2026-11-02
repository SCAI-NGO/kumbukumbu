// SPDX-License-Identifier: AGPL-3.0-or-later
import { createCanvas, loadImage } from "@napi-rs/canvas";
import { describe, expect, it } from "vitest";
import { encode } from "../src/encode.js";
import { renderPage } from "../src/render.js";
import { type Target, fit } from "../src/steps.js";
import {
  A4,
  CORNER_COLOURS,
  CORNER_SIDE,
  CROPPED_BOX,
  CROP_BOX,
  KB,
  LARGE,
  MEDIUM,
  ROTATED_BOX,
  SMALL,
  chartPage,
  cornersPage,
  cropBoxPage,
  noiseCanvas,
  photoPage,
  rotatedPage,
  threePages,
} from "./fixtures.js";

const within = (r: { width: number; height: number }, t: Target) => {
  expect(Math.max(r.width, r.height)).toBeLessThanOrEqual(t.longEdge);
  expect(r.width * r.height).toBeLessThanOrEqual(t.pixels);
};

async function pixel(data: Uint8Array, x: number, y: number) {
  const img = await loadImage(Buffer.from(data));
  const canvas = createCanvas(img.width, img.height);
  const ctx = canvas.getContext("2d");
  ctx.drawImage(img, 0, 0);
  return [...ctx.getImageData(x, y, 1, 1).data];
}

// Crops a margin of 20 pt around a box (displayed points) and finds its colour inside, white outside.
async function expectBox(pdf: Uint8Array, box: { x: number; y: number; w: number; h: number }, colour: number[], rotate = 0) {
  const margin = 20;
  const r = await renderPage(pdf, 1, MEDIUM, { x: box.x - margin, y: box.y - margin, w: box.w + 2 * margin, h: box.h + 2 * margin }, rotate);
  const scale = r.width / (box.w + 2 * margin);
  const at = (x: number, y: number) => pixel(r.data, Math.round(x * scale), Math.round(y * scale));
  const [red, green, blue] = await at(margin + box.w / 2, margin + box.h / 2);
  for (const [got, want] of [[red, colour[0]], [green, colour[1]], [blue, colour[2]]]) expect(Math.abs(got! - want!)).toBeLessThanOrEqual(3);
  // Just outside each edge of the box.
  for (const [x, y] of [[margin / 2, margin + box.h / 2], [margin + box.w + margin / 2, margin + box.h / 2], [margin + box.w / 2, margin / 2], [margin + box.w / 2, margin + box.h + margin / 2]])
    expect(await at(x!, y!)).toEqual([255, 255, 255, 255]);
}

type Box = { x: number; y: number; w: number; h: number };

// The conversion of help pdf (spec §1.5): a region on a page W × H not turned, in the turned page.
function turned({ x, y, w, h }: Box, W: number, H: number, rotate: number): Box {
  if (rotate === 90) return { x: H - y - h, y: x, w: h, h: w };
  if (rotate === 180) return { x: W - x - w, y: H - y - h, w, h };
  return { x: y, y: W - x - w, w: h, h: w };
}

const near = (got: number[], want: readonly number[]) => [0, 1, 2].every((i) => Math.abs(got[i]! - want[i]!) <= 3);

describe("fit", () => {
  it("takes the tightest of long edge, pixel budget and scale cap", () => {
    expect(fit(1000, 500, { longEdge: 500, pixels: 1e9, bytes: 1 }, 10)).toBeCloseTo(0.5);
    expect(fit(1000, 1000, { longEdge: 5000, pixels: 250_000, bytes: 1 }, 10)).toBeCloseTo(0.5);
    expect(fit(100, 50, MEDIUM, 1)).toBe(1);
  });
});

describe("renderPage", () => {
  it("renders a chart page at medium as PNG within the step", async () => {
    const r = await renderPage(await chartPage(), 1, MEDIUM);
    expect(r.mediaType).toBe("image/png");
    expect(r.data.length).toBeLessThanOrEqual(MEDIUM.bytes);
    expect([r.pageWidth, r.pageHeight]).toEqual([A4.width, A4.height]);
    within(r, MEDIUM);
    // The pixel budget binds for A4 at medium: √(1.2e6 / (595 × 842)) ≈ 1.548.
    expect([r.width, r.height]).toEqual([920, 1303]);
    expect([...r.data.subarray(0, 4)]).toEqual([0x89, 0x50, 0x4e, 0x47]);
  });

  it("keeps a page within small", async () => {
    const r = await renderPage(await chartPage(), 1, SMALL);
    within(r, SMALL);
  });

  it("renders a region anew at its own aspect, offset to its top-left corner", async () => {
    const r = await renderPage(await chartPage(), 1, LARGE, { x: 70, y: 400, w: 460, h: 300 });
    within(r, LARGE);
    expect(r.width / r.height).toBeCloseTo(460 / 300, 2);
    const scale = r.width / 460;
    expect(scale).toBeLessThanOrEqual(600 / 72);
    // The first bar spans x 70…98 and, displayed, y 432…512: inside the region at x 0…28, y 32…112.
    const bar = await pixel(r.data, Math.round(14 * scale), Math.round(70 * scale));
    expect(bar[2]).toBeGreaterThan(150);
    expect(bar[0]).toBeLessThan(100);
    expect(await pixel(r.data, Math.round(14 * scale), Math.round(10 * scale))).toEqual([255, 255, 255, 255]);
  });

  it("crops a rotated page in displayed points", async () => {
    // Rotated by 90° clockwise: the box shows at x = PDF y, y = PDF x.
    const box = { x: ROTATED_BOX.y, y: ROTATED_BOX.x, w: ROTATED_BOX.height, h: ROTATED_BOX.width };
    await expectBox(await rotatedPage(), box, [204, 51, 51]);
  });

  it("crops a page from its CropBox, not its MediaBox", async () => {
    const box = {
      x: CROPPED_BOX.x - CROP_BOX.x,
      y: CROP_BOX.y + CROP_BOX.height - (CROPPED_BOX.y + CROPPED_BOX.height),
      w: CROPPED_BOX.width,
      h: CROPPED_BOX.height,
    };
    await expectBox(await cropBoxPage(), box, [51, 153, 51]);
  });

  it("caps the scale at 600 dpi for a tiny region", async () => {
    const r = await renderPage(await chartPage(), 1, LARGE, { x: 70, y: 400, w: 20, h: 10 });
    expect(r.width).toBe(Math.floor((20 * 600) / 72));
    expect(r.height).toBe(Math.floor((10 * 600) / 72));
  });

  it("refuses a region beyond the page", async () => {
    await expect(renderPage(await chartPage(), 1, MEDIUM, { x: 500, y: 700, w: 200, h: 200 })).rejects.toMatchObject({ code: "bad_crop" });
  });

  it("refuses a page the PDF does not have", async () => {
    await expect(renderPage(await threePages(), 9, MEDIUM)).rejects.toMatchObject({ code: "no_such_page" });
    await expect(renderPage(await threePages(), 0, MEDIUM)).rejects.toMatchObject({ code: "no_such_page" });
  });

  it("refuses what is not a PDF", async () => {
    await expect(renderPage(new TextEncoder().encode("not a pdf at all"), 1, MEDIUM)).rejects.toMatchObject({ code: "not_pdf" });
  });

  it("measures a rotated page as displayed", async () => {
    const r = await renderPage(await rotatedPage(), 1, SMALL);
    expect([r.pageWidth, r.pageHeight]).toEqual([A4.height, A4.width]);
    expect(r.width).toBeGreaterThan(r.height);
  });

  it("turns a page by each angle", async () => {
    const pdf = await cornersPage();
    for (const rotate of [90, 180, 270]) {
      const r = await renderPage(pdf, 1, SMALL, undefined, rotate);
      expect(r.width > r.height, `${rotate}`).toBe(rotate !== 180);
      const inset = 20;
      const corners = [[inset, inset], [r.width - inset, inset], [r.width - inset, r.height - inset], [inset, r.height - inset]];
      // A clockwise turn by k quarters brings the square of corner (i − k) mod 4 to corner i.
      for (const [i, [x, y]] of corners.entries()) {
        const want = CORNER_COLOURS[(i - rotate / 90 + 4) % 4]!;
        const got = await pixel(r.data, x!, y!);
        expect(near(got, want), `${rotate}: corner ${i} is ${got}`).toBe(true);
      }
    }
  });

  it("turns a page with its own /Rotate on top of it", async () => {
    // As displayed (rotated by 90°), on a page of 842 × 595.
    const box = { x: ROTATED_BOX.y, y: ROTATED_BOX.x, w: ROTATED_BOX.height, h: ROTATED_BOX.width };
    for (const rotate of [90, 180, 270]) await expectBox(await rotatedPage(), turned(box, A4.height, A4.width, rotate), [204, 51, 51], rotate);
  });

  it("turns a page with a CropBox", async () => {
    const box = {
      x: CROPPED_BOX.x - CROP_BOX.x,
      y: CROP_BOX.y + CROP_BOX.height - (CROPPED_BOX.y + CROPPED_BOX.height),
      w: CROPPED_BOX.width,
      h: CROPPED_BOX.height,
    };
    for (const rotate of [90, 180, 270])
      await expectBox(await cropBoxPage(), turned(box, CROP_BOX.width, CROP_BOX.height, rotate), [51, 153, 51], rotate);
  });

  it("counts a crop in the turned page", async () => {
    const pdf = await cornersPage();
    // The top left of the page turned by 90° is its bottom-left square, by 270° its top-right one.
    for (const [rotate, colour] of [[90, CORNER_COLOURS[3]], [270, CORNER_COLOURS[1]]] as const) {
      const r = await renderPage(pdf, 1, SMALL, { x: 0, y: 0, w: CORNER_SIDE, h: CORNER_SIDE }, rotate);
      expect(near(await pixel(r.data, Math.floor(r.width / 2), Math.floor(r.height / 2)), colour), `${rotate}`).toBe(true);
    }
    await expect(renderPage(pdf, 1, SMALL, { x: 700, y: 0, w: 100, h: 100 }, 90)).resolves.toMatchObject({ pageWidth: A4.width });
    await expect(renderPage(pdf, 1, SMALL, { x: 0, y: 700, w: 100, h: 100 }, 90)).rejects.toMatchObject({ code: "bad_crop" });
  });

  it("reports the page's size without the turn", async () => {
    for (const rotate of [90, 180, 270]) {
      const r = await renderPage(await chartPage(), 1, SMALL, undefined, rotate);
      expect([r.pageWidth, r.pageHeight], `${rotate}`).toEqual([A4.width, A4.height]);
      const own = await renderPage(await rotatedPage(), 1, SMALL, undefined, rotate);
      expect([own.pageWidth, own.pageHeight], `${rotate}`).toEqual([A4.height, A4.width]);
    }
  });

  it("the conversions in the help hit the same colour", async () => {
    const pdf = await cornersPage();
    const [W, H, s, extra] = [A4.width, A4.height, CORNER_SIDE, 20];
    // Each square with 20 pt more on its inner sides: the middle of the region lies in the square.
    const regions = [
      { x: 0, y: 0, w: s + extra, h: s + extra },
      { x: W - s - extra, y: 0, w: s + extra, h: s + extra },
      { x: W - s - extra, y: H - s - extra, w: s + extra, h: s + extra },
      { x: 0, y: H - s - extra, w: s + extra, h: s + extra },
    ];
    for (const rotate of [90, 180, 270])
      for (const [i, region] of regions.entries()) {
        const r = await renderPage(pdf, 1, SMALL, turned(region, W, H, rotate), rotate);
        const got = await pixel(r.data, Math.floor(r.width / 2), Math.floor(r.height / 2));
        expect(near(got, CORNER_COLOURS[i]!), `${rotate}: corner ${i} is ${got}`).toBe(true);
      }
  });

  it("a converted region with y = 0 fits a page of fractional size", async () => {
    const [w, h] = [595.28, 841.89];
    const pdf = await cornersPage([w, h]);
    const region = { x: 0, y: 0, w: CORNER_SIDE, h: CORNER_SIDE };
    for (const rotate of [90, 180, 270]) {
      const r = await renderPage(pdf, 1, SMALL, turned(region, Math.floor(w), Math.floor(h), rotate), rotate);
      expect(near(await pixel(r.data, Math.floor(r.width / 2), Math.floor(r.height / 2)), CORNER_COLOURS[0]), `${rotate}`).toBe(true);
    }
  });

  it("sends a photo-like page as JPEG within small", async () => {
    const r = await renderPage(await photoPage(), 1, SMALL);
    expect(r.mediaType).toBe("image/jpeg");
    expect(r.data.length).toBeLessThanOrEqual(SMALL.bytes);
    within(r, SMALL);
  });
});

describe("encode", () => {
  it("chooses PNG when it fits", async () => {
    const canvas = createCanvas(400, 300);
    const ctx = canvas.getContext("2d");
    ctx.fillStyle = "#fff";
    ctx.fillRect(0, 0, 400, 300);
    ctx.fillStyle = "#000";
    ctx.fillRect(50, 50, 100, 10);
    const r = await encode(canvas, 150 * KB);
    expect(r).toMatchObject({ mediaType: "image/png", width: 400, height: 300 });
  });

  it("chooses JPEG when the PNG does not fit, at the highest quality that does", async () => {
    const canvas = noiseCanvas(500, 400, 1);
    const q85 = await canvas.encode("jpeg", 85);
    const r = await encode(canvas, q85.length);
    expect(r.mediaType).toBe("image/jpeg");
    expect(r.data.length).toBe(q85.length);
    const q75 = await canvas.encode("jpeg", 75);
    expect((await encode(canvas, q75.length + 1)).data.length).toBe(q75.length);
  });

  it("sends the quality 60 JPEG when nothing fits", async () => {
    const canvas = noiseCanvas(500, 400, 1);
    const r = await encode(canvas, 1000);
    expect(r.mediaType).toBe("image/jpeg");
    expect(r.data.length).toBe((await canvas.encode("jpeg", 60)).length);
  });

});
