// SPDX-License-Identifier: AGPL-3.0-or-later
import { createCanvas } from "@napi-rs/canvas";
import {
  PDFDocument,
  PDFPage,
  StandardFonts,
  degrees,
  popGraphicsState,
  pushGraphicsState,
  rgb,
  setTextRenderingMode,
} from "pdf-lib";

export const A4 = { width: 595, height: 842 } as const;

// Every bar of chartPage(), in PDF user space (bottom-left origin).
export const CHART_BARS = Array.from({ length: 12 }, (_, i) => ({ x: 70 + i * 36, y: 330, width: 28, height: 80 + i * 30 }));

// The filled box of rotatedPage(), in PDF user space of its portrait MediaBox.
export const ROTATED_BOX = { x: 100, y: 600, width: 200, height: 100 };

function png(width: number, height: number, colour: string): Uint8Array {
  const canvas = createCanvas(width, height);
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = colour;
  ctx.fillRect(0, 0, width, height);
  ctx.fillStyle = "#ffffff";
  ctx.fillRect(width / 4, height / 4, width / 2, height / 2);
  return canvas.toBuffer("image/png");
}

async function single(draw: (doc: PDFDocument, page: PDFPage) => Promise<void> | void, size: [number, number] = [A4.width, A4.height]) {
  const doc = await PDFDocument.create();
  await draw(doc, doc.addPage(size));
  return doc.save();
}

async function bodyText(doc: PDFDocument, page: PDFPage, lines: number) {
  const font = await doc.embedFont(StandardFonts.Helvetica);
  for (let i = 0; i < lines; i++)
    page.drawText(`Body text line ${i} with a few more words to fill the line.`, { x: 50, y: 800 - i * 18, size: 11, font });
}

export const textPage = () => single((doc, page) => bodyText(doc, page, 40));

export const chartPage = () =>
  single(async (doc, page) => {
    const font = await doc.embedFont(StandardFonts.Helvetica);
    page.drawText("A page with a chart", { x: 50, y: 790, size: 14, font });
    for (const bar of CHART_BARS) page.drawRectangle({ ...bar, color: rgb(0.2, 0.4, 0.8) });
    page.drawLine({ start: { x: 60, y: 325 }, end: { x: 510, y: 325 }, thickness: 1 });
  });

export const logoPage = () =>
  single(async (doc, page) => {
    await bodyText(doc, page, 30);
    const logo = await doc.embedPng(png(80, 80, "#2060c0"));
    page.drawImage(logo, { x: 500, y: 780, width: 40, height: 40 });
  });

export const scanPage = (withInvisibleText: boolean) =>
  single(async (doc, page) => {
    const scan = await doc.embedPng(png(119, 168, "#d0d0d0"));
    page.drawImage(scan, { x: 0, y: 0, width: A4.width, height: A4.height });
    if (withInvisibleText) {
      const font = await doc.embedFont(StandardFonts.Helvetica);
      page.pushOperators(pushGraphicsState(), setTextRenderingMode(3));
      for (let i = 0; i < 5; i++) page.drawText(`Recognised line ${i}`, { x: 50, y: 800 - i * 18, size: 11, font });
      page.pushOperators(popGraphicsState());
    }
  });

export const rotatedPage = () =>
  single((_, page) => {
    page.setRotation(degrees(90));
    page.drawRectangle({ ...ROTATED_BOX, color: rgb(0.8, 0.2, 0.2) });
  });

export const emptyPage = () => single(() => {});

// The grid is one path of many subpaths, as generators often draw tables: its bounding box covers
// the whole table, but every rule in it is thin.
export const tablePage = () =>
  single(async (doc, page) => {
    const font = await doc.embedFont(StandardFonts.Helvetica);
    const [left, top, cols, rows, cw, rh] = [50, 780, 4, 20, 120, 24];
    let grid = "";
    for (let r = 0; r <= rows; r++) grid += `M 0 ${r * rh} H ${cols * cw} `;
    for (let c = 0; c <= cols; c++) grid += `M ${c * cw} 0 V ${rows * rh} `;
    page.drawSvgPath(grid, { x: left, y: top, borderWidth: 0.5, borderColor: rgb(0, 0, 0) });
    for (let r = 0; r < rows; r++)
      for (let c = 0; c < cols; c++)
        page.drawText(`Cell ${r}.${c}`, { x: left + c * cw + 6, y: top - (r + 1) * rh + 8, size: 10, font });
  });

// Body text on a page-sized white rectangle, as some generators paint the paper.
export const whiteBackgroundPage = () =>
  single(async (doc, page) => {
    page.drawRectangle({ x: 0, y: 0, width: A4.width, height: A4.height, color: rgb(1, 1, 1) });
    await bodyText(doc, page, 40);
  });

export async function threePages() {
  const doc = await PDFDocument.create();
  const font = await doc.embedFont(StandardFonts.Helvetica);
  for (let n = 1; n <= 3; n++) doc.addPage([A4.width, A4.height]).drawText(`Page ${n}`, { x: 50, y: 800, size: 12, font });
  doc.setTitle("Three pages");
  doc.setAuthor("A. Writer");
  return doc.save();
}
