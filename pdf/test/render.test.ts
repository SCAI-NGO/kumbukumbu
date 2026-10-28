// SPDX-License-Identifier: AGPL-3.0-or-later
import { createCanvas, loadImage } from "@napi-rs/canvas";
import { describe, expect, it } from "vitest";
import { encode } from "../src/encode.js";
import { renderPage } from "../src/render.js";
import { type Target, fit } from "../src/steps.js";
import { A4, KB, LARGE, MEDIUM, SMALL, chartPage, noiseCanvas, photoPage, rotatedPage, threePages } from "./fixtures.js";

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
    const r = await encode(canvas, 150 * KB, true);
    expect(r).toMatchObject({ mediaType: "image/png", width: 400, height: 300 });
  });

  it("chooses JPEG when the PNG does not fit, at the highest quality that does", async () => {
    const canvas = noiseCanvas(500, 400, 1);
    const q85 = await canvas.encode("jpeg", 85);
    const r = await encode(canvas, q85.length, true);
    expect(r.mediaType).toBe("image/jpeg");
    expect(r.data.length).toBe(q85.length);
    const q75 = await canvas.encode("jpeg", 75);
    expect((await encode(canvas, q75.length + 1, true)).data.length).toBe(q75.length);
  });

  it("sends the quality 60 JPEG when nothing fits", async () => {
    const canvas = noiseCanvas(500, 400, 1);
    const r = await encode(canvas, 1000, true);
    expect(r.mediaType).toBe("image/jpeg");
    expect(r.data.length).toBe((await canvas.encode("jpeg", 60)).length);
  });

  it("draws a transparent image on white for JPEG", async () => {
    const canvas = noiseCanvas(300, 300);
    canvas.getContext("2d").clearRect(0, 0, 100, 100);
    const r = await encode(canvas, 1000, false);
    expect(r.mediaType).toBe("image/jpeg");
    const [red, green, blue] = await pixel(r.data, 50, 50);
    for (const c of [red, green, blue]) expect(c).toBeGreaterThan(245);
  });
});
