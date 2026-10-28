// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { ANALYSER, type PageAnalysis, analyse } from "../src/analyse.js";
import {
  A4,
  CHART_BARS,
  ROTATED_BOX,
  chartPage,
  emptyPage,
  logoPage,
  rotatedPage,
  scanPage,
  tablePage,
  textPage,
  threePages,
  whiteBackgroundPage,
} from "./fixtures.js";

async function onlyPage(bytes: Uint8Array): Promise<PageAnalysis> {
  const a = await analyse(bytes, 2000);
  expect(a.pageCount).toBe(1);
  expect(a.pages).toHaveLength(1);
  return a.pages[0]!;
}

describe("analyse", () => {
  it("names the analyser", () => {
    expect(ANALYSER).toBe("pdfjs-dist@6.4.299+rules.1");
  });

  it("reads a text page", async () => {
    const p = await onlyPage(await textPage());
    expect(p).toMatchObject({ n: 1, type: "text", width: A4.width, height: A4.height, rotation: 0, images: 0, regions: [] });
    expect(p.chars).toBeGreaterThan(1000);
    expect(p.text).toContain("Body text line 3 ");
    expect(p.text.split("\n").length).toBeGreaterThanOrEqual(40);
    expect(p.textCoverage).toBeGreaterThan(0.05);
  });

  it("finds the chart on a chart page", async () => {
    const p = await onlyPage(await chartPage());
    expect(p.type).toBe("mixed");
    expect(p.vectorCoverage).toBeGreaterThan(0.25);
    expect(p.vectorCoverage).toBeLessThan(0.5);
    expect(p.regions).toHaveLength(1);
    const [r] = p.regions;
    const left = Math.min(...CHART_BARS.map((b) => b.x));
    const right = Math.max(...CHART_BARS.map((b) => b.x + b.width));
    const top = A4.height - Math.max(...CHART_BARS.map((b) => b.y + b.height));
    const bottom = A4.height - Math.min(...CHART_BARS.map((b) => b.y));
    expect(r!.kind).toBe("drawing");
    expect(Math.abs(r!.x - left)).toBeLessThanOrEqual(10);
    expect(Math.abs(r!.y - top)).toBeLessThanOrEqual(10);
    expect(Math.abs(r!.x + r!.w - right)).toBeLessThanOrEqual(10);
    expect(Math.abs(r!.y + r!.h - bottom)).toBeLessThanOrEqual(10);
  });

  it("treats a small logo as a text page", async () => {
    const p = await onlyPage(await logoPage());
    expect(p.type).toBe("text");
    expect(p.images).toBe(1);
    expect(p.imageCoverage).toBeGreaterThan(0);
    expect(p.imageCoverage).toBeLessThan(0.01);
  });

  it("calls a scan an image page, with or without an invisible text layer", async () => {
    const bare = await onlyPage(await scanPage(false));
    expect(bare).toMatchObject({ type: "image", images: 1, chars: 0 });
    expect(bare.imageCoverage).toBe(1);
    const ocr = await onlyPage(await scanPage(true));
    expect(ocr.type).toBe("image");
    expect(ocr.chars).toBeGreaterThan(0);
    expect(ocr.text).toContain("Recognised line 2");
  });

  it("measures a rotated page as displayed", async () => {
    const p = await onlyPage(await rotatedPage());
    expect(p.rotation).toBe(90);
    expect(p.width).toBe(A4.height);
    expect(p.height).toBe(A4.width);
    expect(p.width).toBeGreaterThan(p.height);
    // Rotated by 90° clockwise: PDF y runs left to right, PDF x top to bottom.
    expect(p.regions).toEqual([
      { kind: "drawing", x: ROTATED_BOX.y, y: ROTATED_BOX.x, w: ROTATED_BOX.height, h: ROTATED_BOX.width },
    ]);
  });

  it("calls an empty page text", async () => {
    const p = await onlyPage(await emptyPage());
    expect(p).toMatchObject({ type: "text", chars: 0, text: "", textCoverage: 0, imageCoverage: 0, vectorCoverage: 0, images: 0 });
  });

  it("calls a table text", async () => {
    const p = await onlyPage(await tablePage());
    expect(p.type).toBe("text");
    expect(p.regions).toEqual([]);
    expect(p.text).toContain("Cell 3.2");
  });

  it("ignores a white page background", async () => {
    const p = await onlyPage(await whiteBackgroundPage());
    expect(p.type).toBe("text");
    expect(p.vectorCoverage).toBe(0);
  });

  it("stops at maxPages and reads the metadata", async () => {
    const a = await analyse(await threePages(), 2);
    expect(a).toMatchObject({ analyser: ANALYSER, pageCount: 3, analysedPages: 2, title: "Three pages", author: "A. Writer" });
    expect(a.pages.map((p) => [p.n, p.text])).toEqual([
      [1, "Page 1"],
      [2, "Page 2"],
    ]);
  });

  it("gives null metadata when there is none", async () => {
    const a = await analyse(await emptyPage(), 2000);
    expect(a.title).toBeNull();
    expect(a.author).toBeNull();
  });

  it("refuses bytes that are not a PDF", async () => {
    await expect(analyse(new TextEncoder().encode("this is not a PDF at all\n".repeat(40)), 2000)).rejects.toMatchObject({ code: "not_pdf" });
    await expect(analyse(new Uint8Array(0), 2000)).rejects.toMatchObject({ code: "not_pdf" });
  });
});
