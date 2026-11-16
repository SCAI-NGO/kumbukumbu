// SPDX-License-Identifier: AGPL-3.0-or-later
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createCanvas, loadImage } from "@napi-rs/canvas";
import { describe, expect, it } from "vitest";
import { renderSvg } from "../src/image.js";
import { MEDIUM, SMALL } from "./fixtures.js";

const svg = (body: string, attributes = "") =>
  new TextEncoder().encode(`<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" ${attributes}>${body}</svg>`);

async function pixel(data: Uint8Array, x: number, y: number) {
  const img = await loadImage(Buffer.from(data));
  const canvas = createCanvas(img.width, img.height);
  const ctx = canvas.getContext("2d");
  ctx.drawImage(img, 0, 0);
  return [...ctx.getImageData(x, y, 1, 1).data];
}

const isRed = ([r, g, b]: number[]) => r! > 200 && g! < 60 && b! < 60;
const isBlue = ([r, g, b]: number[]) => r! < 60 && g! < 60 && b! > 200;
const isWhite = ([r, g, b]: number[]) => r! > 245 && g! > 245 && b! > 245;

// Red on the left half, blue on the right, in a 200 × 100 box.
const halves = svg('<rect width="100" height="100" fill="red"/><rect x="100" width="100" height="100" fill="blue"/>', 'viewBox="0 0 200 100"');

describe("renderSvg", () => {
  it("draws at the size step, enlarged as well as reduced, keeping the stored aspect", async () => {
    const r = await renderSvg(halves, MEDIUM, { width: 200, height: 100 });
    expect([r.width, r.height]).toEqual([1549, 774]);
    expect([r.originalWidth, r.originalHeight]).toEqual([200, 100]);
    expect(isRed(await pixel(r.data, 10, 400))).toBe(true);
    expect(isBlue(await pixel(r.data, 1500, 400))).toBe(true);
  });

  it("costs nothing for a file claiming a huge canvas", async () => {
    const huge = svg('<rect width="10" height="10" fill="red"/>', 'width="100000" height="100000" viewBox="0 0 10 10"');
    const started = performance.now();
    const r = await renderSvg(huge, SMALL, { width: 100000, height: 100000 });
    expect(performance.now() - started).toBeLessThan(2000);
    expect([r.width, r.height]).toEqual([632, 632]);
    expect(isRed(await pixel(r.data, 300, 300))).toBe(true);
  });

  it("draws a file without a size as if square", async () => {
    const r = await renderSvg(svg('<rect width="300" height="300" fill="red"/>'), SMALL);
    expect(r.width).toBe(r.height);
    expect([r.originalWidth, r.originalHeight]).toEqual([300, 300]);
  });

  it("crops in the stored size and turns", async () => {
    const right = await renderSvg(halves, SMALL, { width: 200, height: 100 }, { x: 100, y: 0, w: 100, h: 100 });
    expect(right.width).toBe(right.height);
    expect(isBlue(await pixel(right.data, 5, 5))).toBe(true);

    const turned = await renderSvg(halves, SMALL, { width: 200, height: 100 }, undefined, 90);
    expect(turned.height).toBeGreaterThan(turned.width);
    expect(isRed(await pixel(turned.data, 5, 5))).toBe(true);
    expect(isBlue(await pixel(turned.data, 5, turned.height - 5))).toBe(true);

    await expect(renderSvg(halves, SMALL, { width: 200, height: 100 }, { x: 150, y: 0, w: 100, h: 100 })).rejects.toMatchObject({ code: "bad_crop" });
  });

  it("shows transparency on white", async () => {
    const r = await renderSvg(svg('<rect width="5" height="5" fill="red"/>', 'width="10" height="10"'), SMALL, { width: 10, height: 10 });
    expect(r.transparent).toBe(true);
    expect(isWhite(await pixel(r.data, r.width - 5, r.height - 5))).toBe(true);
  });

  it("refuses what is no SVG", async () => {
    await expect(renderSvg(new TextEncoder().encode("hello"), SMALL)).rejects.toMatchObject({ code: "not_image" });
    const png = createCanvas(10, 10).toBuffer("image/png");
    await expect(renderSvg(png, SMALL, { width: 10, height: 10 })).rejects.toMatchObject({ code: "not_image" });
  });

  // An external reference resolved on the server would be SSRF; Skia resolves none.
  it("fetches nothing an SVG refers to, and still renders it", async () => {
    const seen: string[] = [];
    const red = createCanvas(10, 10);
    red.getContext("2d").fillStyle = "#ff0000";
    red.getContext("2d").fillRect(0, 0, 10, 10);
    const server = createServer((req, res) => {
      seen.push(req.url ?? "");
      res.writeHead(200, { "Content-Type": req.url?.endsWith(".css") ? "text/css" : "image/png" });
      res.end(req.url?.endsWith(".css") ? "rect { fill: red; }" : red.toBuffer("image/png"));
    });
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    const dir = mkdtempSync(join(tmpdir(), "kumbu-svg-"));
    try {
      const local = join(dir, "red.png");
      writeFileSync(local, red.toBuffer("image/png"));
      const origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
      const file = svg(
        `<style>@import url("${origin}/style.css");</style>` +
          `<image href="${origin}/a.png" x="0" y="0" width="100" height="100"/>` +
          `<image xlink:href="file://${local}" x="100" y="0" width="100" height="100"/>` +
          `<rect x="0" y="100" width="200" height="100" fill="blue"/>`,
        'width="200" height="200"',
      );

      const r = await renderSvg(file, SMALL, { width: 200, height: 200 });

      expect(isBlue(await pixel(r.data, 10, r.height - 10))).toBe(true);
      expect(isWhite(await pixel(r.data, 10, 10))).toBe(true);
      expect(isWhite(await pixel(r.data, r.width - 10, 10))).toBe(true);
      await new Promise((resolve) => setTimeout(resolve, 200));
      expect(seen).toEqual([]);
    } finally {
      rmSync(dir, { recursive: true, force: true });
      await new Promise((resolve) => server.close(resolve));
    }
  });
});
