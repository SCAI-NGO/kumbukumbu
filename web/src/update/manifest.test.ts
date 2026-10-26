// SPDX-License-Identifier: AGPL-3.0-or-later
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

const web = (path: string) => resolve(__dirname, "../..", path);

type Icon = { src: string; sizes: string; type: string; purpose?: string };
const manifest = () => JSON.parse(readFileSync(web("public/manifest.webmanifest"), "utf8")) as Record<string, unknown> & { icons: Icon[] };

/** A PNG's width and height, from its IHDR chunk (bytes 16–23). */
function pngSize(path: string) {
  const bytes = readFileSync(path);
  expect(bytes.subarray(0, 8)).toEqual(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

test("the manifest says what the spec says", () => {
  const m = manifest();
  expect(m.name).toBe("Kumbu²");
  expect(m.short_name).toBe("Kumbu²");
  expect(m.display).toBe("standalone");
  expect(m.start_url).toBe("/");
  expect(m.scope).toBe("/");
  expect(m.theme_color).toBe("#111113");
  expect(m.background_color).toBe("#111113");
});

test("every icon exists at its size", () => {
  const { icons } = manifest();
  expect(icons.length).toBeGreaterThanOrEqual(3);
  for (const icon of icons) {
    const file = web(`public${icon.src}`);
    expect(existsSync(file), icon.src).toBe(true);
    expect(icon.type).toBe("image/png");
    const [w, h] = icon.sizes.split("x").map(Number);
    expect(pngSize(file)).toEqual({ width: w, height: h });
  }
  expect(icons.filter((i) => i.purpose === "maskable")).toHaveLength(1);
  expect(icons.map((i) => i.sizes)).toEqual(expect.arrayContaining(["192x192", "512x512"]));
});

test("the Apple touch icon is 180 pixels", () => {
  expect(pngSize(web("public/apple-touch-icon.png"))).toEqual({ width: 180, height: 180 });
});

test("the icon source draws without fonts", () => {
  const svg = readFileSync(web("icons/k2.svg"), "utf8");
  expect(svg).not.toContain("<text");
  expect(svg).not.toContain("font");
  expect(readFileSync(web("public/favicon.svg"), "utf8")).toBe(svg);
});

test("the page links the manifest and the icons", () => {
  const html = readFileSync(web("index.html"), "utf8");
  for (const tag of [
    '<link rel="manifest" href="/manifest.webmanifest"',
    '<link rel="icon" href="/favicon.svg" type="image/svg+xml"',
    '<link rel="apple-touch-icon" href="/apple-touch-icon.png"',
    '<meta name="theme-color" content="#111113"',
    '<meta name="apple-mobile-web-app-capable" content="yes"',
    '<meta name="apple-mobile-web-app-title" content="Kumbu²"',
    '<meta name="apple-mobile-web-app-status-bar-style" content="black"',
    "<title>Kumbu²</title>",
  ]) {
    expect(html).toContain(tag);
  }
});
