// SPDX-License-Identifier: AGPL-3.0-or-later
import { PDFDocument, StandardFonts, rgb } from "pdf-lib";
import { describe, expect, it } from "vitest";
import { analyse } from "../src/analyse.js";
import { type Box, LABEL_GAP_PT, type PageMeasure, type Region, classify } from "../src/classify.js";
import { A4, CHART_BARS, chartPage, logoPage, photoPage, scanPage, tablePage, textPage, whiteBackgroundPage } from "./fixtures.js";

const W = 600;
const H = 800;
const page = (m: Partial<PageMeasure>): PageMeasure => ({ width: W, height: H, textBoxes: [], imageBoxes: [], pathBoxes: [], ...m });
const box = (x: number, y: number, w: number, h: number): Box => ({ x, y, w, h });
const text = (x: number, y: number, w: number, h: number, chars: number): Box => ({ x, y, w, h, chars });
const tight = (regions: Region[]) => regions.map(({ kind, x, y, w, h }) => ({ kind, x, y, w, h }));

// chartPage's bars with a title just under them, as an axis title would lie.
async function labelledChartPage() {
  const doc = await PDFDocument.create();
  const page = doc.addPage([A4.width, A4.height]);
  const font = await doc.embedFont(StandardFonts.Helvetica);
  for (const bar of CHART_BARS) page.drawRectangle({ ...bar, color: rgb(0.2, 0.4, 0.8) });
  page.drawText("Sales by month", { x: 220, y: 318, size: 10, font });
  return doc.save();
}

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

  it("gives a page with very many path pieces one drawing quickly", () => {
    // 50,000 boxes of 2 × 2 pt, 200 across and 250 down in steps of 2.4 pt.
    const pieces = Array.from({ length: 50_000 }, (_, i) => box(50 + (i % 200) * 2.4, 100 + Math.floor(i / 200) * 2.4, 2, 2));
    const started = performance.now();
    const c = classify(page({ pathBoxes: pieces }));
    expect(performance.now() - started).toBeLessThan(500);
    expect(c.type).toBe("mixed");
    expect(c.vectorCoverage).toBeGreaterThan(0.3);
    expect(c.vectorCoverage).toBeLessThan(0.6);
    expect(c.regions).toEqual([{ kind: "drawing", x: 50, y: 100, w: 479.6, h: 599.6 }]);
  });

  it("calls an empty page text with nothing covered", () => {
    expect(classify(page({}))).toEqual({ type: "text", textCoverage: 0, imageCoverage: 0, vectorCoverage: 0, images: 0, regions: [] });
  });
});

describe("regions with labels", () => {
  const chart = box(100, 200, 300, 200);

  it("a legend right of a chart joins its region", () => {
    const c = classify(
      page({
        pathBoxes: [chart, box(420, 246, 8, 8)],
        textBoxes: [text(410, 230, 50, 10, 6), text(432, 245, 40, 10, 10), text(50, 600, 400, 12, 70)],
      }),
    );
    expect(c.regions).toEqual([{ kind: "drawing", x: 100, y: 200, w: 300, h: 200, labelled: { x: 100, y: 200, w: 372, h: 200 } }]);
  });

  it("a chain follows a legend's second line", () => {
    const c = classify(page({ pathBoxes: [chart], textBoxes: [text(150, 406, 60, 10, 9), text(150, 420, 60, 10, 9), text(150, 434, 60, 10, 9)] }));
    expect(c.regions).toEqual([{ kind: "drawing", x: 100, y: 200, w: 300, h: 200, labelled: { x: 100, y: 200, w: 300, h: 244 } }]);
  });

  it("a label further than LABEL_GAP_PT stays out", () => {
    const c = classify(page({ pathBoxes: [chart], textBoxes: [text(150, 401 + LABEL_GAP_PT, 60, 10, 9)] }));
    expect(c.regions).toEqual([{ kind: "drawing", x: 100, y: 200, w: 300, h: 200 }]);
  });

  it("a long caption stops the growth", () => {
    // The label is within reach, but the box taking it in would hold the caption's centre.
    const c = classify(page({ pathBoxes: [chart], textBoxes: [text(100, 404, 300, 10, 60), text(60, 405, 30, 10, 6), text(150, 186, 60, 10, 8)] }));
    expect(c.regions).toEqual([{ kind: "drawing", x: 100, y: 200, w: 300, h: 200, labelled: { x: 100, y: 186, w: 300, h: 214 } }]);
  });

  it("a label closer to another figure stays with that figure", () => {
    // Two separate charts side by side; the label between them is in reach of both, nearer the right one.
    const c = classify(
      page({
        pathBoxes: [box(50, 200, 200, 200), box(330, 200, 200, 200)],
        textBoxes: [text(261, 300, 64, 10, 12), text(306, 220, 20, 10, 3)],
      }),
    );
    expect(c.regions).toEqual([
      { kind: "drawing", x: 50, y: 200, w: 200, h: 200 },
      { kind: "drawing", x: 330, y: 200, w: 200, h: 200, labelled: { x: 261, y: 200, w: 269, h: 200 } },
    ]);
  });

  it("two pieces of one chart share one labelled box", () => {
    // A grouped bar chart that falls apart into one region per group, each with its category label.
    const c = classify(
      page({
        pathBoxes: [box(100, 200, 100, 200), box(220, 200, 100, 200)],
        textBoxes: [text(92, 404, 116, 10, 10), text(212, 404, 116, 10, 10), text(80, 260, 10, 80, 14), text(330, 250, 40, 10, 8)],
      }),
    );
    const labelled = { x: 80, y: 200, w: 290, h: 214 };
    expect(c.regions).toEqual([
      { kind: "drawing", x: 100, y: 200, w: 100, h: 200, labelled },
      { kind: "drawing", x: 220, y: 200, w: 100, h: 200, labelled },
    ]);
  });

  it("a chart's axis lines and ticks lead to its labels and tie its pieces together", () => {
    // Bars further apart than LABEL_GAP_PT, the axes as lines of no thickness, tick labels beyond the axes.
    const G = LABEL_GAP_PT;
    const bars = [box(100, 200, 40, 200), box(150 + G, 200, 40, 200), box(200 + 2 * G, 200, 40, 200)];
    const axes = [box(90, 200, 0, 200), box(90, 400, 150 + 2 * G, 0), box(120, 400, 0, 4), box(170 + G, 400, 0, 4), box(220 + 2 * G, 400, 0, 4)];
    const c = classify(
      page({
        pathBoxes: [...bars, ...axes],
        textBoxes: [
          text(60, 195, 26, 10, 5),
          text(60, 395, 26, 10, 5),
          text(105, 408, 30, 10, 6),
          text(155 + G, 408, 30, 10, 6),
          text(205 + 2 * G, 408, 30, 10, 6),
          text(60, 440, 400, 10, 70),
        ],
      }),
    );
    const labelled = { x: 60, y: 195, w: 180 + 2 * G, h: 223 };
    expect(c.regions).toEqual(bars.map((b) => ({ kind: "drawing", ...b, labelled })));
  });

  it("a label inside another region stays there", () => {
    const c = classify(page({ pathBoxes: [chart], imageBoxes: [box(100, 404, 300, 100)], textBoxes: [text(110, 406, 40, 10, 6), text(150, 186, 60, 10, 8)] }));
    expect(c.regions).toEqual([
      { kind: "drawing", x: 100, y: 200, w: 300, h: 200, labelled: { x: 100, y: 186, w: 300, h: 214 } },
      { kind: "image", x: 100, y: 404, w: 300, h: 100 },
    ]);
  });

  it("a label goes to the nearer chart, whichever grows first", () => {
    // The left chart takes the first label and so grows to 2 pt from the second, which lies 10 pt from
    // the right chart's own region and further than LABEL_GAP_PT from the left one's.
    const G = LABEL_GAP_PT;
    const c = classify(
      page({
        pathBoxes: [box(50, 200, 200, 200), box(264 + 2 * G, 200, 200, 200)],
        textBoxes: [text(252, 300, G, 10, 4), text(254 + G, 300, G, 10, 4)],
      }),
    );
    expect(c.regions).toEqual([
      { kind: "drawing", x: 50, y: 200, w: 200, h: 200, labelled: { x: 50, y: 200, w: 202 + G, h: 200 } },
      { kind: "drawing", x: 264 + 2 * G, y: 200, w: 200, h: 200, labelled: { x: 254 + G, y: 200, w: 210 + G, h: 200 } },
    ]);
  });

  it("a chart taken in on the way is held to the same nearness as one taken in directly", () => {
    // The box taking in the label would overlap the middle chart, which lies nearer the image on the
    // right than the label does: the left chart doesn't take the label, nor the middle one with it.
    const G = LABEL_GAP_PT;
    const c = classify(
      page({
        pathBoxes: [box(50, 200, 100, 200), box(155 + G, 208, 60, 100)],
        imageBoxes: [box(221 + G, 208, 100, 100)],
        textBoxes: [text(152, 190, G + 20, 8, 6)],
      }),
    );
    expect(c.regions).toEqual([
      { kind: "drawing", x: 50, y: 200, w: 100, h: 200 },
      { kind: "drawing", x: 155 + G, y: 208, w: 60, h: 100 },
      { kind: "image", x: 221 + G, y: 208, w: 100, h: 100 },
    ]);
  });

  it("two charts with their own axes further apart than LABEL_GAP_PT stay apart", () => {
    // Known limit: one long rule touching both would join them.
    const G = LABEL_GAP_PT;
    const c = classify(
      page({
        pathBoxes: [
          box(60, 200, 150, 200),
          box(55, 200, 0, 200),
          box(55, 400, 155, 0),
          box(216 + G, 200, 150, 200),
          box(211 + G, 200, 0, 200),
          box(211 + G, 400, 155, 0),
        ],
      }),
    );
    expect(c.regions).toEqual([
      { kind: "drawing", x: 60, y: 200, w: 150, h: 200, labelled: { x: 55, y: 200, w: 155, h: 200 } },
      { kind: "drawing", x: 216 + G, y: 200, w: 150, h: 200, labelled: { x: 211 + G, y: 200, w: 155, h: 200 } },
    ]);
  });

  it("a full page's image gains nothing", () => {
    const c = classify(page({ imageBoxes: [box(0, 0, W, 780)], textBoxes: [text(100, 784, 40, 10, 8)] }));
    expect(c.regions).toEqual([{ kind: "image", x: 0, y: 0, w: W, h: 780 }]);
  });

  it("above 5,000 text boxes nothing grows", () => {
    const filler = (n: number) => Array.from({ length: n }, (_, i) => text(5 + (i % 100) * 5.9, 560 + Math.floor(i / 100) * 4.8, 4, 3, 1));
    // The label alone would be taken in (as in the chain above); the filler lies far from the chart.
    const above = classify(page({ pathBoxes: [chart], textBoxes: [text(150, 406, 60, 10, 9), ...filler(5_000)] }));
    expect(above.regions).toEqual([{ kind: "drawing", x: 100, y: 200, w: 300, h: 200 }]);
  });

  it("at 5,000 text boxes labels still grow", () => {
    const filler = (n: number) => Array.from({ length: n }, (_, i) => text(5 + (i % 100) * 5.9, 560 + Math.floor(i / 100) * 4.8, 4, 3, 1));
    const at = classify(page({ pathBoxes: [chart], textBoxes: [text(150, 406, 60, 10, 9), ...filler(4_999)] }));
    expect(at.regions).toEqual([{ kind: "drawing", x: 100, y: 200, w: 300, h: 200, labelled: { x: 100, y: 200, w: 300, h: 216 } }]);
  });

  it("a real PDF's chart takes in its title, its count of characters reaching the rule", async () => {
    const p = (await analyse(await labelledChartPage(), 1)).pages[0]!;
    const r = p.regions[0]!;
    // The title's line lies about 6 to 18 pt under the bars.
    expect({ x: r.labelled?.x, y: r.labelled?.y, w: r.labelled?.w }).toEqual({ x: r.x, y: r.y, w: r.w });
    expect(r.labelled!.y + r.labelled!.h).toBeGreaterThan(r.y + r.h + 6);
    expect(r.labelled!.y + r.labelled!.h).toBeLessThan(r.y + r.h + 18);
  });

  it("the same boxes give the same answer, in any order", () => {
    const pathBoxes = [box(50, 100, 200, 200), box(330, 100, 200, 200), box(100, 450, 100, 200), box(220, 450, 100, 200), box(540, 150, 8, 8), box(90, 650, 240, 0)];
    const textBoxes = [
      text(261, 200, 64, 10, 12),
      text(306, 120, 20, 10, 3),
      text(552, 148, 30, 10, 6),
      text(92, 654, 116, 10, 10),
      text(212, 654, 116, 10, 10),
      text(80, 500, 10, 80, 14),
      text(100, 670, 300, 10, 60),
      text(150, 306, 60, 10, 9),
      text(150, 320, 60, 10, 9),
    ];
    const expected = classify(page({ pathBoxes, textBoxes }));
    for (let turn = 1; turn < textBoxes.length; turn++) {
      const rotate = <T>(list: T[], by: number) => [...list.slice(by % list.length), ...list.slice(0, by % list.length)];
      expect(classify(page({ pathBoxes: rotate(pathBoxes, turn).reverse(), textBoxes: rotate(textBoxes, turn) }))).toEqual(expected);
    }
  });

  it("no fixture changes its page type or tight region", async () => {
    const whole = [{ kind: "image", x: 0, y: 0, w: A4.width, h: A4.height }];
    const chartRegion = [{ kind: "drawing", x: 70, y: 102, w: 424, h: 410 }];
    const expected: [string, () => Promise<Uint8Array>, string, unknown[]][] = [
      ["textPage", textPage, "text", []],
      ["chartPage", chartPage, "mixed", chartRegion],
      ["labelledChartPage", labelledChartPage, "mixed", chartRegion],
      ["logoPage", logoPage, "text", []],
      ["scanPage", () => scanPage(false), "image", whole],
      ["scanPage with text", () => scanPage(true), "image", whole],
      ["tablePage", tablePage, "text", []],
      ["whiteBackgroundPage", whiteBackgroundPage, "text", []],
      ["photoPage", photoPage, "image", whole],
    ];
    for (const [name, make, type, regions] of expected) {
      const p = (await analyse(await make(), 1)).pages[0]!;
      expect({ name, type: p.type, regions: tight(p.regions) }).toEqual({ name, type, regions });
    }
  });
});
