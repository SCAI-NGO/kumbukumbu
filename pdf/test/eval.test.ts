// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { type FigureManifest, type Item, type JudgedRegion, classifyItems, judgeFigures } from "../eval/regions.js";

const body = new Set(["plain", "words", "here", "again"]);
const item = (text: string, x: number, y: number, w: number, h = 10): Item => ({ text, x, y, w, h });

// A chart's plot area at 100,100 200×150, its labels round it, body text and a caption further off.
const tight = { kind: "drawing" as const, x: 100, y: 100, w: 200, h: 150 };
const labelled = { x: 50, y: 80, w: 320, h: 200 };
const items: Item[] = [
  item("zqA3 0", 60, 240, 34),
  item("zqA3 10", 60, 100, 38),
  item("zqA3", 310, 150, 22), // "zqA3 north", split in two by the generator
  item("north", 334, 150, 26),
  item("zqA3 south", 310, 165, 50),
  item("Plain words here again.", 50, 400, 120),
  item("Figure 1. Plain words", 50, 300, 100),
  item("%%", 500, 500, 10),
];
const figure: FigureManifest = { figure: 1, token: "zqA3", labels: ["zqA3 north", "zqA3 south"], minTickLabels: 2 };
const judge = (its: Item[], regions: JudgedRegion[], figures = [figure]) => judgeFigures(its, regions, figures, body).figures;

describe("the eval of regions with labels", () => {
  it("judges a figure by token: complete and clean, and each way it can fail", () => {
    // Every item falls into one of the four classes; a fragment joins its label's token.
    const { items: classed } = classifyItems(items, body);
    expect(classed.map((c) => [c.text, c.kind, c.token])).toEqual([
      ["zqA3 0", "token", "zqA3"],
      ["zqA3 10", "token", "zqA3"],
      ["zqA3", "token", "zqA3"],
      ["north", "token", "zqA3"],
      ["zqA3 south", "token", "zqA3"],
      ["Plain words here again.", "words", undefined],
      ["Figure 1. Plain words", "caption", undefined],
      ["%%", "unidentified", undefined],
    ]);

    // With labels: complete and clean, the split label counted whole and not against "clean"; the
    // unidentified item counts for neither side.
    const passes = judgeFigures(items, [{ ...tight, labelled }], [figure], body);
    expect(passes.figures).toEqual([{ figure: 1, token: "zqA3", by: "labelled", complete: true, clean: true, pass: true, failures: [] }]);
    expect(passes.unidentified.map((u) => u.text)).toEqual(["%%"]);

    // The tight box alone leaves the labels out.
    expect(judge(items, [tight])[0]).toMatchObject({ by: "tight", complete: false, clean: true, failures: ["label outside"] });

    // A label at 85 % inside is outside; at 95 % it is in.
    const edge = (inside: number) => [...items.slice(0, 4), item("zqA3 south", 370 - 50 * inside, 165, 50)];
    expect(judge(edge(0.85), [{ ...tight, labelled }])[0]!.failures).toEqual(["label outside"]);
    expect(judge(edge(0.95), [{ ...tight, labelled }])[0]!.failures).toEqual([]);

    // A token that vanished: a label not found, and too few tick labels.
    const missing = judge(items.filter((i) => i.text !== "zqA3 south" && i.text !== "zqA3 10"), [{ ...tight, labelled }]);
    expect(missing[0]).toMatchObject({ complete: false, clean: true, failures: ["label missing", "too few tick labels"] });

    // A box that takes in a body line and another figure's label is not clean; a caption and an
    // unidentified item inside it count for neither side.
    const greedy = { x: 40, y: 80, w: 480, h: 440 };
    const other: FigureManifest = { figure: 2, token: "zqB7", labels: ["zqB7 east"], minTickLabels: 0 };
    const crowded = [...items, item("zqB7 east", 420, 120, 50), item("zqB7 1", 600, 600, 30)];
    expect(judge(crowded, [{ ...tight, labelled: greedy }], [figure, other])[0]).toMatchObject({
      complete: true,
      clean: false,
      failures: ["body text inside", "other figure inside"],
    });

    // A drawing in pieces: a second region within the figure's labels lies outside the box.
    const piece = { kind: "drawing" as const, x: 240, y: 180, w: 50, h: 70 };
    expect(judge(items, [{ ...tight, w: 120, labelled: { x: 50, y: 80, w: 180, h: 200 } }, piece])[0]!.failures).toContain("drawing outside");

    // No region at all, or only one beyond the figure's labels.
    expect(judge(items, [])[0]).toMatchObject({ by: "none", complete: false, failures: ["no region"] });
    const elsewhere = { kind: "drawing" as const, x: 100, y: 600, w: 200, h: 100 };
    expect(judge(items, [elsewhere])[0]).toMatchObject({ by: "none", complete: false });
    expect(judge(items, [elsewhere])[0]!.failures).toContain("no region");
  });

  it("gives each of two figures on a page the region nearest its labels", () => {
    // Figure A as above; figure B the same, 300 points lower.
    const b = (it: Item) => ({ ...it, text: it.text.replace("zqA3", "zqB7"), y: it.y + 300 });
    const two = [...items.slice(0, 5), ...items.slice(0, 5).map(b)];
    const lower = { ...tight, y: tight.y + 300, labelled: { ...labelled, y: labelled.y + 300 } };
    const figB: FigureManifest = { ...figure, figure: 2, token: "zqB7", labels: ["zqB7 north", "zqB7 south"] };
    // Listed lower first, so that order alone can't give the right answer.
    const results = judge(two, [lower, { ...tight, labelled }], [figure, figB]);
    expect(results.map((r) => [r.token, r.pass, r.failures])).toEqual([
      ["zqA3", true, []],
      ["zqB7", true, []],
    ]);
  });

  it("counts only a token followed by a number as a tick label", () => {
    const ticks = { ...figure, minTickLabels: 3 };
    // Two ticks and "zqA3 north": not three.
    expect(judge(items, [{ ...tight, labelled }], [ticks])[0]!.failures).toEqual(["too few tick labels"]);
    const { runs } = classifyItems(items, body);
    expect(runs.get("zqA3")).toContain("zqA3northzqA3south");
  });
});
