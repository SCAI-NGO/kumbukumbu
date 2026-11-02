// SPDX-License-Identifier: AGPL-3.0-or-later
import type { TextItem } from "pdfjs-dist/types/src/display/api.js";
import type { PageViewport } from "pdfjs-dist/types/src/display/page_viewport.js";
import { describe, expect, it } from "vitest";
import { analyse } from "../src/analyse.js";
import { type PageBoxes, boxesOf, itemBox, pageText, textBoxes } from "../src/boxes.js";
import { openPdf } from "../src/pdfjs.js";
import {
  A4,
  CROPPED_LINES,
  CROP_BOX,
  ROTATED_TEXT,
  ROTATED_TEXT_AT,
  TEXT_SIZE,
  TURNED_AT,
  TURNED_TEXT,
  chartPage,
  cropBoxTextPage,
  emptyPage,
  encryptedPdf,
  rotatedPage,
  rotatedTextPage,
  scanPage,
  tablePage,
  textPage,
  threePages,
  trailingSpacesPage,
  turnedTextPage,
} from "./fixtures.js";

async function onlyPage(bytes: Uint8Array): Promise<PageBoxes> {
  const { pages } = await textBoxes(bytes, [1]);
  expect(pages).toHaveLength(1);
  return pages[0]!;
}

// The viewport of a fixture's first page, for boxesOf and itemBox on items made up here.
async function viewportOf(bytes: Uint8Array): Promise<PageViewport> {
  const doc = await openPdf(bytes);
  try {
    return (await doc.getPage(1)).getViewport({ scale: 1 });
  } finally {
    await doc.loadingTask.destroy();
  }
}

const item = (str: string, x: number, y: number, more: Partial<TextItem> = {}): TextItem => ({
  str,
  dir: "ltr",
  transform: [TEXT_SIZE, 0, 0, TEXT_SIZE, x, y],
  width: str.length * 6,
  height: TEXT_SIZE,
  fontName: "f",
  hasEOL: false,
  ...more,
});

const slice = (p: PageBoxes, i: number) => p.text.slice(p.items[i]!.at, p.items[i]!.end);

const FIXTURES = { textPage, chartPage, tablePage, threePages, rotatedPage, scanPage: () => scanPage(true), turnedTextPage, rotatedTextPage, cropBoxTextPage, trailingSpacesPage };

describe("text boxes", () => {
  it("gives a horizontal item its box in displayed points", async () => {
    const p = await onlyPage(await textPage());
    expect(p).toMatchObject({ n: 1, width: A4.width, height: A4.height });
    expect(slice(p, 0)).toBe("Body text line 0 with a few more words to fill the line.");
    // Drawn at (50, 800) in 11 pt Helvetica; displayed from the top.
    expect(p.items[0]).toEqual({ at: 0, end: 56, x: 50, y: A4.height - 800 - 11, w: 254.9, h: 11 });
  });

  it("gives text drawn at 90 degrees a tall, narrow box", async () => {
    const p = await onlyPage(await turnedTextPage());
    expect(p.items).toHaveLength(1);
    expect(slice(p, 0)).toBe(TURNED_TEXT);
    // It runs upwards from TURNED_AT; its height lies to the left of the baseline.
    const width = 92; // Helvetica's width of TURNED_TEXT at 12 pt
    expect(p.items[0]).toEqual({ at: 0, end: TURNED_TEXT.length, x: TURNED_AT.x - TEXT_SIZE, y: A4.height - TURNED_AT.y - width, w: TEXT_SIZE, h: width });
  });

  it("counts boxes on a /Rotate page as displayed", async () => {
    const p = await onlyPage(await rotatedTextPage());
    expect(p).toMatchObject({ width: A4.height, height: A4.width });
    expect(slice(p, 0)).toBe(ROTATED_TEXT);
    // Rotated by 90° clockwise: PDF y runs left to right, PDF x top to bottom.
    expect(p.items[0]).toMatchObject({ x: ROTATED_TEXT_AT.y, y: ROTATED_TEXT_AT.x, w: TEXT_SIZE, h: 135.4 });
  });

  it("clips a box to the CropBox, and leaves out an item wholly outside", async () => {
    const p = await onlyPage(await cropBoxTextPage());
    expect(p).toMatchObject({ width: CROP_BOX.width, height: CROP_BOX.height });
    const top = CROP_BOX.y + CROP_BOX.height;
    const [inside, across] = CROPPED_LINES;
    expect(slice(p, 0)).toBe(inside.text);
    expect(p.items[0]).toMatchObject({ x: inside.x - CROP_BOX.x, y: top - inside.y - TEXT_SIZE, w: 78.7, h: TEXT_SIZE });
    // pdf.js drops the glyphs wholly left of the CropBox; the first one it keeps still starts 5.3 pt left of it.
    expect(slice(p, 1)).toBe("ss the left edge");
    expect(p.items[1]).toMatchObject({ x: 0, y: top - across.y - TEXT_SIZE, w: 76.1, h: TEXT_SIZE });
    expect(p.items).toHaveLength(2);

    const viewport = await viewportOf(await cropBoxTextPage());
    const outside = CROPPED_LINES[2];
    expect(itemBox(item(outside.text, outside.x, outside.y), viewport)).toBeNull();
    const [left, right] = [item("Left", -200, 300), item("Right", CROP_BOX.x + CROP_BOX.width + 1, 300)];
    expect(itemBox(left, viewport)).toBeNull();
    expect(itemBox(right, viewport)).toBeNull();
    const b = boxesOf({ n: 1, width: 400, height: 600 }, [item("Kept", 100, 400), left, item("Over", 30, 300, { width: 40 })], viewport);
    expect(b.items).toEqual([
      { at: 0, end: 4, x: 50, y: 238, w: 24, h: 12 },
      { at: 8, end: 12, x: 0, y: 338, w: 20, h: 12 },
    ]);
  });

  it("gives no number below zero on any fixture", async () => {
    for (const [name, make] of Object.entries(FIXTURES)) {
      const bytes = await make();
      const pageCount = (await analyse(bytes, 2000)).pageCount;
      const { pages } = await textBoxes(bytes, Array.from({ length: pageCount }, (_, i) => i + 1));
      for (const p of pages)
        for (const box of p.items)
          for (const v of Object.values(box)) {
            expect(Number.isFinite(v), name).toBe(true);
            expect(v, name).toBeGreaterThanOrEqual(0);
          }
    }
  });

  it("answers the very text analyse stores", async () => {
    for (const make of [textPage, chartPage, tablePage, threePages, rotatedPage, () => scanPage(true)]) {
      const bytes = await make();
      const a = await analyse(bytes, 2000);
      const { pages } = await textBoxes(bytes, a.pages.map((p) => p.n));
      expect(pages.map((p) => p.n)).toEqual(a.pages.map((p) => p.n));
      pages.forEach((p, i) => expect(p.text).toBe(a.pages[i]!.text));
    }
    // pdf.js leaves no white space at the end of these pages; items that end in it must lose it too,
    // as analyse's text (pageText's) does.
    const viewport = await viewportOf(await emptyPage());
    const ending = [item("Total", 50, 780, { hasEOL: true }), item("  ", 50, 760, { hasEOL: true })];
    expect(pageText(ending).text).toBe("Total");
    expect(boxesOf({ n: 1, width: A4.width, height: A4.height }, ending, viewport).text).toBe(pageText(ending).text);
  });

  it("keeps offsets inside the trimmed text, ascending", async () => {
    const ascending = (p: PageBoxes) => {
      let end = 0;
      for (const box of p.items) {
        expect(box.at).toBeGreaterThanOrEqual(end);
        expect(box.end).toBeGreaterThan(box.at);
        expect(box.end).toBeLessThanOrEqual(p.text.length);
        end = box.end;
      }
    };
    const real = await onlyPage(await trailingSpacesPage());
    expect(real.text.endsWith("Total")).toBe(true);
    ascending(real);

    // pdf.js drops a drawn line's trailing spaces itself; an item that keeps them (another
    // producer, a font that maps a glyph to white space) must still end inside the text.
    const viewport = await viewportOf(await emptyPage());
    const p = boxesOf({ n: 1, width: A4.width, height: A4.height }, [item("Item one", 50, 800, { hasEOL: true }), item("Total   ", 50, 780)], viewport);
    expect(p.text).toBe("Item one\nTotal");
    expect(p.items.map((b) => [b.at, b.end])).toEqual([[0, 8], [9, 14]]);
    ascending(p);
    expect(pageText([item("Total   ", 50, 780)]).spans.map((s) => [s.at, s.end])).toEqual([[0, 5]]);
  });

  it("has no entry for an item of white space", async () => {
    const real = await onlyPage(await trailingSpacesPage());
    expect(real.text).toBe("Item one 12\nTotal");
    expect(real.items.map((_, i) => slice(real, i))).toEqual(["Item one", "12", "Total"]);

    const viewport = await viewportOf(await emptyPage());
    const p = boxesOf({ n: 1, width: A4.width, height: A4.height }, [item("A", 50, 800), item("   ", 60, 800), item(" \t", 80, 800), item("B", 90, 800)], viewport);
    expect(p.text).toBe("A    \tB");
    expect(p.items.map((b) => [b.at, b.end])).toEqual([[0, 1], [6, 7]]);
  });

  it("has no box for an item with a zero vector", async () => {
    const viewport = await viewportOf(await emptyPage());
    expect(itemBox(item("Gone", 0, 0, { transform: [0, 0, 0, 0, 0, 0] }), viewport)).toBeNull();
    expect(itemBox(item("Flat", 50, 800, { transform: [0, 0, 0, 12, 50, 800] }), viewport)).toBeNull();
    expect(itemBox(item("Thin", 50, 800, { transform: [12, 0, 0, 0, 50, 800] }), viewport)).toBeNull();
    const p = boxesOf({ n: 1, width: A4.width, height: A4.height }, [item("Gone", 0, 0, { transform: [0, 0, 0, 0, 0, 0] }), item("Kept", 50, 800)], viewport);
    expect(p.items).toEqual([{ at: 4, end: 8, x: 50, y: 30, w: 24, h: 12 }]);
  });

  it("answers items: [] above the limit of items, with the text", async () => {
    const viewport = await viewportOf(await emptyPage());
    const three = [item("One", 50, 800), item("Two", 50, 780), item("Six", 50, 760)];
    const p = boxesOf({ n: 4, width: A4.width, height: A4.height }, three, viewport, 2);
    expect(p).toEqual({ n: 4, width: A4.width, height: A4.height, text: "OneTwoSix", items: [] });
    expect(boxesOf({ n: 4, width: A4.width, height: A4.height }, three, viewport, 3).items).toHaveLength(3);
  });

  it("refuses a page the PDF lacks", async () => {
    await expect(textBoxes(await threePages(), [1, 4])).rejects.toMatchObject({ code: "no_such_page" });
  });

  it("refuses what is not a PDF", async () => {
    await expect(textBoxes(new TextEncoder().encode("garbage"), [1])).rejects.toMatchObject({ code: "not_pdf" });
  });

  it("refuses an encrypted PDF", async () => {
    await expect(textBoxes(await encryptedPdf(), [1])).rejects.toMatchObject({ code: "encrypted" });
  });
});
