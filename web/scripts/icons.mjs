// SPDX-License-Identifier: AGPL-3.0-or-later
// Makes every icon under public/ from the one source, icons/k2.svg: `pnpm -C web icons`. To replace the logo,
// replace that SVG (shapes as paths, never text; the glyph in a <g id="glyph">) and run this again.
import { Resvg } from "@resvg/resvg-js";
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const web = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const source = resolve(web, "icons/k2.svg");
const svg = readFileSync(source, "utf8");

const glyph = svg.match(/<g id="glyph"[\s\S]*?<\/g>/)?.[0];
if (!glyph) throw new Error(`${source} has no <g id="glyph">`);
const background = svg.match(/<rect id="background"[^>]*fill="([^"]+)"/)?.[1];
if (!background) throw new Error(`${source} has no <rect id="background" fill="…">`);

/**
 * The glyph on a square without rounded corners, scaled about the centre: for the maskable icon (whose mask may
 * cut anything outside the central 80 % circle) and for iOS, which wants an opaque icon and rounds it itself.
 */
const fullBleed = (scale) =>
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512">` +
  `<rect width="512" height="512" fill="${background}"/>` +
  `<g transform="translate(256 256) scale(${scale}) translate(-256 -256)">${glyph}</g></svg>`;

function png(file, content, size) {
  const out = resolve(web, "public", file);
  mkdirSync(dirname(out), { recursive: true });
  writeFileSync(out, new Resvg(content, { fitTo: { mode: "width", value: size } }).render().asPng());
  console.log(`${file} (${size}×${size})`);
}

// The source's glyph fills about 70 % of the square; the maskable one keeps it inside the central 60 %.
png("icons/icon-192.png", svg, 192);
png("icons/icon-512.png", svg, 512);
png("icons/icon-maskable-512.png", fullBleed(60 / 70), 512);
png("apple-touch-icon.png", fullBleed(1), 180);
copyFileSync(source, resolve(web, "public/favicon.svg"));
console.log("favicon.svg");
