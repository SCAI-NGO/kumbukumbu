// SPDX-License-Identifier: AGPL-3.0-or-later

// The pdf-lib part of the corpus of tools/attachment-probes/make_figure_pdfs.py, which calls it:
//   node eval-dist/eval/figures.js <out>/seed-N/pdf-lib --seed N [--turn <pdf>…]
// Draws the eight layouts and the controls of spec §4.5 with a manifest beside each, from the word
// lists the script wrote to <out>/seed-N/words.json; --turn sets /Rotate 90 on the first page of
// the other generators' PDFs given.

import { createCanvas } from "@napi-rs/canvas";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { pathToFileURL } from "node:url";
import {
  type PDFFont,
  PDFDocument,
  type PDFPage,
  StandardFonts,
  degrees,
  popGraphicsState,
  pushGraphicsState,
  rgb,
  setTextRenderingMode,
} from "pdf-lib";
import { noiseCanvas } from "../test/fixtures.js";
import { CONTROLS, type FigureManifest, LAYOUTS, type Manifest } from "./regions.js";

const W = 595;
const H = 842;
const COLOURS = [rgb(0.2, 0.4, 0.8), rgb(0.9, 0.5, 0.1)];

// mulberry32: small, seedable and the same on every machine.
function random(seed: number) {
  let s = seed >>> 0;
  const next = () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  const int = (lo: number, hi: number) => lo + Math.floor(next() * (hi - lo + 1));
  const pick = <T>(xs: readonly T[]) => xs[Math.floor(next() * xs.length)]!;
  const sample = <T>(xs: readonly T[], k: number) => {
    const pool = [...xs];
    for (let i = pool.length - 1; i > 0; i--) {
      const j = Math.floor(next() * (i + 1));
      [pool[i], pool[j]] = [pool[j]!, pool[i]!];
    }
    return pool.slice(0, k);
  };
  return { next, int, pick, sample };
}
type Random = ReturnType<typeof random>;

type Figure = FigureManifest & {
  categories: string[];
  series: string[];
  xTitle: string;
  yTitle: string;
  title: string | null;
  ticks: number[];
  values: number[][];
  kind: "bar" | "line";
};

type Box = { x: number; y: number; w: number; h: number }; // points from the top left

class Drawer {
  constructor(
    readonly page: PDFPage,
    readonly font: PDFFont,
    readonly rnd: Random,
    readonly body: readonly string[],
  ) {}

  text(s: string, x: number, y: number, size: number, rotate = 0) {
    // y is the baseline, from the top.
    this.page.drawText(s, { x, y: H - y, size, font: this.font, rotate: degrees(rotate) });
  }

  width(s: string, size: number) {
    return this.font.widthOfTextAtSize(s, size);
  }

  line(maxWidth: number, size = 10, prefix = "") {
    const words: string[] = [];
    for (;;) {
      const w = this.rnd.pick(this.body);
      if (this.width(prefix + [...words, w].join(" ") + ".", size) > maxWidth) break;
      words.push(w);
    }
    const s = words.join(" ");
    return prefix + s.charAt(0).toUpperCase() + s.slice(1) + ".";
  }

  paragraph(x: number, y: number, width: number, lines: number) {
    for (let i = 0; i < lines; i++, y += 14) this.text(this.line(width), x, y + 10, 10);
    return y;
  }

  rect(x: number, y: number, w: number, h: number, colour = COLOURS[0]!) {
    this.page.drawRectangle({ x, y: H - y - h, width: w, height: h, color: colour });
  }

  stroke(x0: number, y0: number, x1: number, y1: number, thickness = 1, colour = rgb(0, 0, 0)) {
    this.page.drawLine({ start: { x: x0, y: H - y0 }, end: { x: x1, y: H - y1 }, thickness, color: colour });
  }

  // The plot area is b; labels, titles and legend go round it.
  chart(f: Figure, b: Box, legend: "right" | "below", slanted = false, turnedTitle = false) {
    const size = 8;
    const top = f.ticks.at(-1)!;
    const n = f.categories.length;
    const group = b.w / n;
    const yOf = (v: number) => b.y + b.h - (v / top) * b.h;
    for (let i = 0; i < n; i++) {
      const cx = b.x + group * (i + 0.5);
      if (f.kind === "bar")
        f.values.forEach((vals, s) => this.rect(cx - group * 0.35 + s * group * 0.35, yOf(vals[i]!), group * 0.33, b.y + b.h - yOf(vals[i]!), COLOURS[s]));
    }
    if (f.kind === "line")
      f.values.forEach((vals, s) => {
        for (let i = 0; i < n; i++) {
          const [x, y] = [b.x + group * (i + 0.5), yOf(vals[i]!)];
          this.page.drawCircle({ x, y: H - y, size: 2.5, color: COLOURS[s] });
          if (i + 1 < n) this.stroke(x, y, b.x + group * (i + 1.5), yOf(vals[i + 1]!), 1.5, COLOURS[s]);
        }
      });
    this.stroke(b.x, b.y, b.x, b.y + b.h);
    this.stroke(b.x, b.y + b.h, b.x + b.w, b.y + b.h);
    for (const t of f.ticks) {
      const s = `${f.token} ${t}`;
      this.stroke(b.x - 3, yOf(t), b.x, yOf(t), 0.5);
      this.text(s, b.x - 6 - this.width(s, size), yOf(t) + 3, size);
    }
    let below = b.y + b.h + 12;
    f.categories.forEach((c, i) => {
      const cx = b.x + group * (i + 0.5);
      if (!slanted) return this.text(c, cx - this.width(c, size) / 2, below, size);
      // Up to the right, ending under its tick.
      const tw = this.width(c, size);
      const a = (40 * Math.PI) / 180;
      this.text(c, cx - Math.cos(a) * tw, b.y + b.h + 6 + Math.sin(a) * tw, size, 40);
    });
    if (slanted) below = b.y + b.h + 6 + Math.max(...f.categories.map((c) => this.width(c, size))) * Math.sin((40 * Math.PI) / 180) + 4;
    const xt = below + 16;
    this.text(f.xTitle, b.x + (b.w - this.width(f.xTitle, 9)) / 2, xt, 9);
    if (turnedTitle) {
      const longest = Math.max(...f.ticks.map((t) => this.width(`${f.token} ${t}`, size)));
      this.text(f.yTitle, b.x - longest - 14, b.y + (b.h + this.width(f.yTitle, 9)) / 2, 9, 90);
    } else this.text(f.yTitle, b.x - 20, b.y - 8, 9);
    if (f.title) this.text(f.title, b.x + (b.w - this.width(f.title, 10)) / 2, b.y - 22, 10);
    if (legend === "right")
      f.series.forEach((s, i) => {
        const y = b.y + b.h / 2 - 10 + i * 14;
        this.rect(b.x + b.w + 12, y, 8, 8, COLOURS[i]);
        this.text(s, b.x + b.w + 24, y + 7, size);
      });
    else {
      let x = b.x + 10;
      f.series.forEach((s, i) => {
        this.rect(x, xt + 8, 8, 8, COLOURS[i]);
        this.text(s, x + 12, xt + 15, size);
        x += 12 + this.width(s, size) + 20;
      });
    }
  }
}

function newFigure(rnd: Random, labelWords: readonly string[], number: number, used: Set<string>, small = false): Figure {
  let token: string;
  do token = `zq${String.fromCharCode(65 + rnd.int(0, 25))}${rnd.int(0, 9)}`;
  while (used.has(token));
  used.add(token);
  const n = small ? rnd.int(3, 4) : rnd.int(4, 5);
  const words = rnd.sample(labelWords, n + 5).map((w) => `${token} ${w}`);
  const step = rnd.pick([5, 10, 20, 25]);
  const ticks = Array.from({ length: rnd.int(4, 5) }, (_, k) => k * step);
  const top = ticks.at(-1)!;
  const f = {
    figure: number,
    token,
    categories: words.slice(0, n),
    series: words.slice(n, n + 2),
    xTitle: words[n + 2]!,
    yTitle: words[n + 3]!,
    title: rnd.next() < 0.5 ? words[n + 4]! : null,
    ticks,
    values: [0, 1].map(() => Array.from({ length: n }, () => rnd.int(Math.max(1, Math.floor(top / 6)), top))),
    kind: rnd.next() < 0.5 ? ("bar" as const) : ("line" as const),
    labels: [] as string[],
    minTickLabels: ticks.length,
  };
  f.labels = [...f.categories, ...f.series, f.xTitle, f.yTitle, ...(f.title ? [f.title] : [])];
  return f;
}

const manifestOf = (f: Figure): FigureManifest => ({ figure: f.figure, token: f.token, labels: f.labels, minTickLabels: f.minTickLabels });

function png(width: number, height: number, colour: string) {
  const canvas = createCanvas(width, height);
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = colour;
  ctx.fillRect(0, 0, width, height);
  ctx.fillStyle = "#ffffff";
  ctx.fillRect(width / 4, height / 4, width / 2, height / 2);
  return canvas.toBuffer("image/png");
}

async function layoutPdf(layout: string, rnd: Random, words: { body: string[]; label: string[] }) {
  const doc = await PDFDocument.create();
  const page = doc.addPage([W, H]);
  const d = new Drawer(page, await doc.embedFont(StandardFonts.Helvetica), rnd, words.body);
  const used = new Set<string>();
  const fig = (n: number, small = false) => newFigure(rnd, words.label, n, used, small);
  const figures: Figure[] = [];
  switch (layout) {
    case "legend-right":
    case "turned-page":
    case "turned-labels": {
      const f = fig(1);
      d.paragraph(56, 60, 480, 5);
      d.chart(f, { x: 100, y: 220, w: 330, h: 280 }, "right", layout === "turned-labels", layout === "turned-labels");
      d.paragraph(56, 650, 480, 6);
      figures.push(f);
      if (layout === "turned-page") page.setRotation(degrees(90));
      break;
    }
    case "legend-below": {
      const f = fig(1);
      d.paragraph(56, 60, 480, 5);
      d.chart(f, { x: 100, y: 220, w: 400, h: 270 }, "below");
      d.paragraph(56, 650, 480, 6);
      figures.push(f);
      break;
    }
    case "side-by-side": {
      figures.push(fig(1, true), fig(2, true));
      d.paragraph(56, 60, 480, 6);
      // About 11 % of the page each, labels included, as in the playbook.
      d.chart(figures[0]!, { x: 95, y: 270, w: 190, h: 182 }, "below");
      d.chart(figures[1]!, { x: 365, y: 270, w: 190, h: 182 }, "below");
      d.paragraph(56, 560, 480, 8);
      break;
    }
    case "between-paragraphs": {
      const f = fig(1);
      const y = d.paragraph(56, 60, 480, 12);
      const top = y + (f.title ? 36 : 22);
      d.chart(f, { x: 100, y: top, w: 330, h: 240 }, "right");
      d.paragraph(56, top + 240 + 40, 480, 12);
      figures.push(f);
      break;
    }
    case "two-columns": {
      const f = fig(1, true);
      d.paragraph(56, 60, 220, 50);
      d.chart(f, { x: 350, y: 300, w: 180, h: 190 }, "below");
      figures.push(f);
      break;
    }
    case "long-caption": {
      const f = fig(1);
      d.paragraph(56, 60, 480, 4);
      d.chart(f, { x: 100, y: 190, w: 330, h: 280 }, "right");
      let y = 190 + 280 + 46;
      for (let i = 0; i < 4; i++, y += 14) d.text(i === 0 ? d.line(480, 10, "Figure 1. ") : d.line(480), 56, y + 10, 10);
      d.paragraph(56, y + 30, 480, 8);
      figures.push(f);
      break;
    }
    case "control-text":
      d.paragraph(56, 60, 480, 48);
      break;
    case "control-table": {
      d.paragraph(56, 40, 480, 1);
      const [left, top, cols, rows, cw, rh] = [56, 70, 4, 24, 120, 24];
      let grid = "";
      for (let r = 0; r <= rows; r++) grid += `M 0 ${r * rh} H ${cols * cw} `;
      for (let c = 0; c <= cols; c++) grid += `M ${c * cw} 0 V ${rows * rh} `;
      page.drawSvgPath(grid, { x: left, y: H - top, borderWidth: 0.5, borderColor: rgb(0, 0, 0) });
      for (let r = 0; r < rows; r++)
        for (let c = 0; c < cols; c++) d.text(rnd.pick(words.body), left + c * cw + 6, top + (r + 1) * rh - 8, 10);
      break;
    }
    case "control-logo": {
      d.paragraph(56, 60, 420, 40);
      const logo = await doc.embedPng(png(80, 80, "#2060c0"));
      page.drawImage(logo, { x: 495, y: H - 100, width: 40, height: 40 });
      break;
    }
    case "control-photos": {
      d.paragraph(56, 40, 480, 1);
      for (let i = 0; i < 4; i++) {
        const photo = await doc.embedPng(noiseCanvas(240, 320, 4 + i).toBuffer("image/png"));
        page.drawImage(photo, { x: 56 + (i % 2) * 250, y: H - 70 - 360 - Math.floor(i / 2) * 370, width: 235, height: 350 });
      }
      break;
    }
    case "control-scan": {
      const scan = await doc.embedPng(png(119, 168, "#d0d0d0"));
      page.drawImage(scan, { x: 0, y: 0, width: W, height: H });
      page.pushOperators(pushGraphicsState(), setTextRenderingMode(3));
      d.paragraph(56, 60, 480, 30);
      page.pushOperators(popGraphicsState());
      break;
    }
    case "control-empty":
      break;
  }
  return { bytes: await doc.save(), figures };
}

async function turn(path: string) {
  const doc = await PDFDocument.load(readFileSync(path));
  doc.getPage(0).setRotation(degrees(90));
  writeFileSync(path, await doc.save());
}

async function main(argv: string[]) {
  const out = argv[0];
  const at = argv.indexOf("--seed");
  const seed = Number(argv[at + 1]);
  if (!out || at < 0 || !Number.isInteger(seed)) throw new Error("usage: figures.js <out>/seed-N/pdf-lib --seed N [--turn <pdf>…]");
  const t = argv.indexOf("--turn");
  for (const path of t < 0 ? [] : argv.slice(t + 1)) await turn(path);

  const words = JSON.parse(readFileSync(join(dirname(out), "words.json"), "utf8")) as { body: string[]; label: string[] };
  const rnd = random(seed * 1000 + 3);
  mkdirSync(out, { recursive: true });
  for (const layout of [...LAYOUTS, ...CONTROLS]) {
    const { bytes, figures } = await layoutPdf(layout, rnd, words);
    const base = join(out, `${layout}-1`);
    writeFileSync(`${base}.pdf`, bytes);
    const manifest: Manifest = { generator: "pdf-lib", layout, page: 1, ...(layout.startsWith("control-") ? { control: true } : {}), figures: figures.map(manifestOf) };
    writeFileSync(`${base}.json`, JSON.stringify(manifest, null, 2) + "\n");
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main(process.argv.slice(2));
