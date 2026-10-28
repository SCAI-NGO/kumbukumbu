// SPDX-License-Identifier: AGPL-3.0-or-later
import { createCanvas } from "@napi-rs/canvas";
import {
  PDFArray,
  PDFDocument,
  PDFHexString,
  PDFName,
  PDFPage,
  StandardFonts,
  concatTransformationMatrix,
  degrees,
  drawObject,
  popGraphicsState,
  pushGraphicsState,
  rgb,
  setTextRenderingMode,
} from "pdf-lib";
import type { Target } from "../src/steps.js";

export const A4 = { width: 595, height: 842 } as const;

// The size steps (src/Kumbu.Core/Attachments/SizeSteps.cs); the server sends them with each request.
export const KB = 1024;
export const SMALL: Target = { longEdge: 768, pixels: 400_000, bytes: 150 * KB };
export const MEDIUM: Target = { longEdge: 1568, pixels: 1_200_000, bytes: 400 * KB };
export const LARGE: Target = { longEdge: 2576, pixels: 3_700_000, bytes: 1024 * KB };

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

// The filled box of cropBoxPage(), in PDF user space; the CropBox starts at (50, 50).
export const CROPPED_BOX = { x: 100, y: 500, width: 100, height: 50 };
export const CROP_BOX = { x: 50, y: 50, width: 400, height: 600 };

export const cropBoxPage = () =>
  single((_, page) => {
    page.setCropBox(CROP_BOX.x, CROP_BOX.y, CROP_BOX.width, CROP_BOX.height);
    page.drawRectangle({ ...CROPPED_BOX, color: rgb(0.2, 0.6, 0.2) });
  });

// A whole-page image that declares 10,000 × 10,000 pixels in a few bytes of Flate: it would cost
// 100 MP of memory to decode.
export const hugeImagePage = () =>
  single(async (doc, page) => {
    const font = await doc.embedFont(StandardFonts.Helvetica);
    page.drawText("Text next to a huge image", { x: 50, y: 800, size: 12, font });
    const image = doc.context.flateStream(new Uint8Array(1000), {
      Type: "XObject",
      Subtype: "Image",
      Width: 10_000,
      Height: 10_000,
      ColorSpace: "DeviceGray",
      BitsPerComponent: 8,
    });
    const name = page.node.newXObject("Huge", doc.context.register(image));
    page.pushOperators(pushGraphicsState(), concatTransformationMatrix(A4.width, 0, 0, A4.height, 0, 0), drawObject(name), popGraphicsState());
  });

// pdf-lib cannot encrypt, so this adds a Standard security handler whose user password check fails
// for the empty password: pdf.js then asks for a password, as with any protected PDF.
export async function encryptedPdf() {
  const doc = await PDFDocument.create();
  doc.addPage([A4.width, A4.height]);
  const bytes32 = (seed: number) => PDFHexString.of(Array.from({ length: 32 }, (_, i) => ((i * 37 + seed) & 0xff).toString(16).padStart(2, "0")).join(""));
  const encrypt = doc.context.obj({ Filter: "Standard", V: 1, R: 2, Length: 40, P: -4 });
  encrypt.set(PDFName.of("O"), bytes32(1));
  encrypt.set(PDFName.of("U"), bytes32(2));
  doc.context.trailerInfo.Encrypt = doc.context.register(encrypt);
  const id = PDFArray.withContext(doc.context);
  id.push(bytes32(3));
  id.push(bytes32(3));
  doc.context.trailerInfo.ID = id;
  return doc.save({ useObjectStreams: false });
}

// A deterministic noise pattern in blocks (4 × 4 px by default): as hard to compress as a photograph.
export function noiseCanvas(width: number, height: number, block = 4) {
  const canvas = createCanvas(width, height);
  const ctx = canvas.getContext("2d");
  let seed = 12345;
  const next = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) % 256;
  for (let y = 0; y < height; y += block)
    for (let x = 0; x < width; x += block) {
      ctx.fillStyle = `rgb(${next()},${next()},${next()})`;
      ctx.fillRect(x, y, block, block);
    }
  return canvas;
}

export const photoPage = () =>
  single(async (doc, page) => {
    const photo = await doc.embedPng(noiseCanvas(600, 850).toBuffer("image/png"));
    page.drawImage(photo, { x: 0, y: 0, width: A4.width, height: A4.height });
  });
