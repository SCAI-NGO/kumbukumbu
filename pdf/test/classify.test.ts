// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { type Box, type PageMeasure, classify } from "../src/classify.js";

const W = 600;
const H = 800;
const page = (m: Partial<PageMeasure>): PageMeasure => ({ width: W, height: H, textBoxes: [], imageBoxes: [], pathBoxes: [], ...m });
const box = (x: number, y: number, w: number, h: number): Box => ({ x, y, w, h });

describe("classify", () => {
  it("calls a page with one image over 85 % of it an image page", () => {
    const c = classify(page({ imageBoxes: [box(0, 0, W, 720)], textBoxes: [box(50, 750, 300, 12)] }));
    expect(c.type).toBe("image");
    expect(c.imageCoverage).toBeCloseTo(0.9, 3);
    expect(c.images).toBe(1);
    expect(c.regions).toEqual([{ kind: "image", x: 0, y: 0, w: W, h: 720 }]);
  });

  it("calls a page with two figures of 15 % each mixed", () => {
    const c = classify(page({ pathBoxes: [box(0, 0, W, 120), box(0, 400, W, 120)] }));
    expect(c.type).toBe("mixed");
    expect(c.vectorCoverage).toBeCloseTo(0.3, 2);
    expect(c.imageCoverage).toBe(0);
    expect(c.regions.map((r) => r.kind)).toEqual(["drawing", "drawing"]);
  });

  it("counts overlapping boxes once", () => {
    const overlapping = [box(0, 0, 300, 400), box(150, 0, 300, 400)];
    const images = classify(page({ imageBoxes: overlapping }));
    expect(images.imageCoverage).toBeCloseTo(0.375, 3);
    expect(images.images).toBe(2);
    expect(images.type).toBe("mixed");
    const paths = classify(page({ pathBoxes: overlapping }));
    expect(paths.vectorCoverage).toBeCloseTo(0.375, 3);
    expect(paths.regions).toEqual([{ kind: "drawing", x: 0, y: 0, w: 450, h: 400 }]);
  });

  it("merges boxes a few points apart into one figure", () => {
    const bars = Array.from({ length: 10 }, (_, i) => box(50 + i * 40, 300, 30, 200));
    const c = classify(page({ pathBoxes: bars }));
    expect(c.regions).toEqual([{ kind: "drawing", x: 50, y: 300, w: 390, h: 200 }]);
  });

  it("ignores thin rules", () => {
    const rules = Array.from({ length: 30 }, (_, i) => box(40, 40 + i * 24, 520, 0.5));
    const c = classify(page({ pathBoxes: [...rules, box(40, 40, 0.5, 720)], textBoxes: [box(50, 50, 200, 10)] }));
    expect(c.type).toBe("text");
    expect(c.vectorCoverage).toBe(0);
    expect(c.regions).toEqual([]);
  });

  it("counts small images without listing them", () => {
    const c = classify(page({ imageBoxes: [box(500, 20, 40, 40)] }));
    expect(c.type).toBe("text");
    expect(c.images).toBe(1);
    expect(c.imageCoverage).toBeLessThan(0.01);
    expect(c.regions).toEqual([]);
  });

  it("sorts regions top to bottom, then left to right, rounded to 0.1 pt", () => {
    const c = classify(page({ imageBoxes: [box(300, 100, 200, 100), box(20.04, 100, 200, 100)], pathBoxes: [box(20, 20.06, 400, 50)] }));
    expect(c.regions).toEqual([
      { kind: "drawing", x: 20, y: 20.1, w: 400, h: 50 },
      { kind: "image", x: 20, y: 100, w: 200, h: 100 },
      { kind: "image", x: 300, y: 100, w: 200, h: 100 },
    ]);
  });

  it("keeps boxes within the page", () => {
    const c = classify(page({ imageBoxes: [box(-100, -100, W + 200, H + 200)] }));
    expect(c.type).toBe("image");
    expect(c.imageCoverage).toBe(1);
    expect(c.regions).toEqual([{ kind: "image", x: 0, y: 0, w: W, h: H }]);
  });

  it("calls an empty page text with nothing covered", () => {
    expect(classify(page({}))).toEqual({ type: "text", textCoverage: 0, imageCoverage: 0, vectorCoverage: 0, images: 0, regions: [] });
  });
});
