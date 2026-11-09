// SPDX-License-Identifier: AGPL-3.0-or-later
import { ImageData, createCanvas, loadImage } from "@napi-rs/canvas";
import { AnnotationMode, OPS } from "pdfjs-dist/legacy/build/pdf.mjs";
import { afterEach, describe, expect, it, vi } from "vitest";
import { encodeStored } from "../src/encode.js";
import {
  type ImageInfo,
  MAX_BODY_STORED,
  MAX_STORED_PIXELS,
  STORED_DPI,
  extractImage,
  imagesOf,
  listImages,
  quietly,
  renderRegion,
} from "../src/extract.js";
import { PdfError, openPdf } from "../src/pdfjs.js";
import { renderPage } from "../src/render.js";
import {
  A4,
  CORNER_COLOURS,
  CORNER_IMAGE,
  INLINES,
  INLINE_COLOUR,
  LARGE,
  MASKS,
  MASKS_COLOUR,
  MASK_COLOUR,
  PHOTO,
  REPEATS,
  RGB_PIXELS,
  SQUARE,
  bigImagePage,
  brokenImagePage,
  cornerImagePage,
  cornersPage,
  hostileSizesPage,
  hugeImagePage,
  inlineImagesPage,
  logoOnEveryPage,
  longImagePage,
  manyMasksPage,
  manyPlacementsPage,
  maskPage,
  offPageImagePage,
  oneBitPage,
  repeatedImagePage,
  rgbPage,
  transparentImagePage,
  truncatedMaskPage,
  twoImagesPage,
  vectorDrawingPage,
  widePage,
} from "./fixtures.js";

type Pixels = { width: number; height: number; at: (x: number, y: number) => number[] };

async function pixels(data: Uint8Array): Promise<Pixels> {
  const img = await loadImage(Buffer.from(data));
  const canvas = createCanvas(img.width, img.height);
  const ctx = canvas.getContext("2d");
  ctx.drawImage(img, 0, 0);
  const { data: rgba } = ctx.getImageData(0, 0, img.width, img.height);
  return { width: img.width, height: img.height, at: (x, y) => [...rgba.subarray((y * img.width + x) * 4, (y * img.width + x) * 4 + 4)] };
}

const PNG = [0x89, 0x50, 0x4e, 0x47];
const JPEG = [0xff, 0xd8, 0xff];
const starts = (data: Uint8Array, magic: number[]) => expect([...data.subarray(0, magic.length)]).toEqual(magic);

// A box in PDF user space as displayed on an unrotated A4 page.
const shown = (b: { x: number; y: number; width: number; height: number }) => ({ x: b.x, y: A4.height - b.y - b.height, w: b.width, h: b.height });

async function failure(p: Promise<unknown>): Promise<PdfError> {
  const e = await p.then(
    () => null,
    (e: unknown) => e,
  );
  expect(e).toBeInstanceOf(PdfError);
  return e as PdfError;
}

const near = (got: number[], want: readonly number[], slack = 3) => {
  expect(got.length).toBeGreaterThanOrEqual(want.length);
  want.forEach((w, i) => expect(Math.abs(got[i]! - w), `channel ${i} of ${got} vs ${want}`).toBeLessThanOrEqual(slack));
};

describe("listImages", () => {
  it("lists a page's images in painting order with boxes and sizes", async () => {
    const list = await listImages(await twoImagesPage(), 1);
    expect(list).toMatchObject({ page: 1, pageCount: 1, width: A4.width, height: A4.height, over: 0 });
    expect(list.images.map(({ n, width, height, x, y, w, h }) => ({ n, width, height, x, y, w, h }))).toEqual([
      { n: 1, width: PHOTO.pixels[0], height: PHOTO.pixels[1], ...shown(PHOTO) },
      { n: 2, width: SQUARE.pixels[0], height: SQUARE.pixels[1], ...shown(SQUARE) },
    ]);
    for (const image of list.images) {
      expect(image.hash).toMatch(/^[0-9a-f]{64}$/);
      expect(image.failed).toBe(false);
    }
  });

  it("marks images under 1 % of the page as small", async () => {
    const page2 = await listImages(await logoOnEveryPage(), 2);
    expect(page2.images.map((i) => i.small)).toEqual([false, true]);
    expect((await listImages(await twoImagesPage(), 1)).images.map((i) => i.small)).toEqual([false, false]);
  });

  it("gives the same image on every page the same hash, and another image another", async () => {
    const pdf = await logoOnEveryPage();
    const pages = await Promise.all([1, 2, 3].map((p) => listImages(pdf, p)));
    const logos = pages.map((p) => p.images.at(-1)!.hash);
    expect(new Set(logos).size).toBe(1);
    expect(pages[1]!.images[0]!.hash).not.toBe(logos[0]);
    const two = (await listImages(await twoImagesPage(), 1)).images;
    expect(two[0]!.hash).not.toBe(two[1]!.hash);
  });

  it("leaves out an image wholly off the page and clips one across its edge", async () => {
    const list = await listImages(await offPageImagePage(), 1);
    expect(list.images.map(({ n, x, y, w, h }) => ({ n, x, y, w, h }))).toEqual([
      { n: 1, x: 500, y: 0, w: 95, h: 142 },
      { n: 2, x: 100, y: 542, w: 200, h: 200 },
    ]);
  });

  it("counts an image mask but not a solid colour mask", async () => {
    const list = await listImages(await maskPage(), 1);
    expect(list.images.map(({ n, width, height }) => ({ n, width, height }))).toEqual([{ n: 1, width: 16, height: 16 }]);
  });

  it("lists an image pdf.js cannot decode as failed, without a hash", async () => {
    const list = await listImages(await brokenImagePage(), 1);
    expect(list.images).toEqual([{ n: 1, width: 50, height: 30, x: 100, y: 182, w: 100, h: 60, small: false, hash: null, failed: true }]);
    expect((await failure(extractImage(await brokenImagePage(), 1, 1))).code).toBe("failed");
  });

  it("lists an image declaring a width that is no whole positive number as failed, and the others", async () => {
    const pdf = await hostileSizesPage();
    const [good, ...bad] = (await listImages(pdf, 1)).images;
    expect(good).toMatchObject({ n: 1, width: 3, height: 2, failed: false });
    expect(bad).toHaveLength(2);
    for (const image of bad) {
      expect(image).toMatchObject({ hash: null, failed: true });
      for (const side of [image.width, image.height]) expect(Number.isInteger(side) && side >= 0, `${side}`).toBe(true);
      expect((await failure(extractImage(pdf, 1, image.n))).code).toBe("failed");
    }
  });

  it("lists every placement of one image, hashing it once, up to 10,000 and refuses more", async () => {
    const list = await listImages(await manyPlacementsPage(10_000), 1);
    expect(list.images).toHaveLength(10_000);
    expect(new Set(list.images.map((i) => i.hash)).size).toBe(1);
    const e = await failure(listImages(await manyPlacementsPage(10_001), 1));
    expect([e.code, e.message]).toEqual(["too_large", "The page paints more than 10,000 images."]);
  });

  it("counts an image over 50 MP that pdf.js leaves out", async () => {
    const list = await listImages(await hugeImagePage(), 1);
    expect(list.images).toEqual([]);
    expect(list.over).toBe(1);
  });

  it("refuses a page outside the PDF", async () => {
    expect((await failure(listImages(await twoImagesPage(), 2))).code).toBe("no_such_page");
    expect((await failure(listImages(await twoImagesPage(), 0))).code).toBe("no_such_page");
  });
});

describe("extractImage", () => {
  it("delivers an embedded image at its own size", async () => {
    const pdf = await twoImagesPage();
    const photo = await extractImage(pdf, 1, 1);
    expect(photo).toMatchObject({ mediaType: "image/png", width: 400, height: 300, originalWidth: 400, originalHeight: 300 });
    starts(photo.data, PNG);
    const square = await extractImage(pdf, 1, 2);
    expect(square).toMatchObject({ width: 200, height: 200 });
    near((await pixels(square.data)).at(100, 100), [0xc0, 0x40, 0x20], 6);
  });

  for (const [rotation, flip] of [[0, false], [90, false], [180, false], [270, false], [0, true], [90, true], [180, true], [270, true]] as const)
    it(`turns an image as painted: ${rotation}°${flip ? ", mirrored" : ""}`, async () => {
      const pdf = await cornerImagePage(rotation, flip);
      const [box] = (await listImages(pdf, 1)).images as [ImageInfo];
      const image = await extractImage(pdf, 1, 1);
      const sideways = rotation === 90 || rotation === 270;
      const [w, h] = sideways ? [CORNER_IMAGE.height, CORNER_IMAGE.width] : [CORNER_IMAGE.width, CORNER_IMAGE.height];
      expect([image.width, image.height, box.width, box.height]).toEqual([w, h, w, h]);
      const page = await renderPage(pdf, 1, LARGE);
      const [shownPage, stored] = [await pixels(page.data), await pixels(image.data)];
      const scale = page.width / A4.width;
      // Each corner of the stored image has the colour of the same corner as the page shows it.
      for (const [fx, fy] of [[0.15, 0.15], [0.85, 0.15], [0.85, 0.85], [0.15, 0.85]] as const) {
        const want = shownPage.at(Math.floor((box.x + fx * box.w) * scale), Math.floor((box.y + fy * box.h) * scale));
        near(stored.at(Math.floor(fx * w), Math.floor(fy * h)), want, 10);
      }
    });

  it("draws an image mask in its fill colour on transparent", async () => {
    const mask = await pixels((await extractImage(await maskPage(), 1, 1)).data);
    expect([mask.width, mask.height]).toEqual([16, 16]);
    expect(mask.at(2, 8)).toEqual([...MASK_COLOUR, 255]);
    expect(mask.at(12, 8)[3]).toBe(0);
  });

  it("paints the rows a mask's data lacks as nothing", async () => {
    const list = await listImages(await truncatedMaskPage(), 1);
    expect(list.images).toMatchObject([{ width: 16, height: 16, failed: false }]);
    const mask = await pixels((await extractImage(await truncatedMaskPage(), 1, 1)).data);
    expect([mask.width, mask.height]).toEqual([16, 16]);
    expect(mask.at(8, 3)).toEqual([...MASK_COLOUR, 255]);
    expect(mask.at(8, 12)[3]).toBe(0);
  });

  it("keeps an image's alpha", async () => {
    const image = await extractImage(await transparentImagePage(), 1, 1);
    expect(image.mediaType).toBe("image/png");
    const p = await pixels(image.data);
    expect(p.at(2, 2)[3]).toBe(0);
    expect(p.at(32, 32)).toEqual([0, 0, 255, 255]);
  });

  it("reads 1-bit grey and raw RGB pixels exactly", async () => {
    const bits = await pixels((await extractImage(await oneBitPage(), 1, 1)).data);
    expect([bits.width, bits.height]).toEqual([10, 4]);
    for (let y = 0; y < 4; y++)
      for (let x = 0; x < 10; x++) expect(bits.at(x, y)).toEqual((x + y) % 2 === 0 ? [255, 255, 255, 255] : [0, 0, 0, 255]);
    const rgb = await pixels((await extractImage(await rgbPage(), 1, 1)).data);
    expect([rgb.width, rgb.height]).toEqual([3, 2]);
    RGB_PIXELS.forEach((c, i) => expect(rgb.at(i % 3, Math.floor(i / 3))).toEqual([...c, 255]));
  });

  it("numbers every placement of an image placed again and again", async () => {
    const pdf = await repeatedImagePage();
    const list = await listImages(pdf, 1);
    expect(list.images.map((i) => [i.n, i.x])).toEqual(Array.from({ length: REPEATS }, (_, i) => [i + 1, 50 + i * 60]));
    expect(new Set(list.images.map((i) => i.hash)).size).toBe(1);
    const last = await pixels((await extractImage(pdf, 1, REPEATS)).data);
    RGB_PIXELS.forEach((c, i) => expect(last.at(i % 3, Math.floor(i / 3))).toEqual([...c, 255]));
  });

  it("numbers many masks and many inline images in order, each in its colour", async () => {
    const masks = await manyMasksPage("growing");
    const list = await listImages(masks, 1);
    expect(list.images.map((i) => [i.n, i.x])).toEqual(Array.from({ length: MASKS }, (_, i) => [i + 1, 20 + i * 45]));
    const mask = await pixels((await extractImage(masks, 1, MASKS)).data);
    expect(mask.at(1, 3)).toEqual([...MASKS_COLOUR, 255]);
    expect(mask.at(6, 3)[3]).toBe(0);

    const inline = await inlineImagesPage();
    const inlines = await listImages(inline, 1);
    expect(inlines.images.map((i) => [i.n, i.x, i.width, i.height])).toEqual(Array.from({ length: INLINES }, (_, i) => [i + 1, 20 + i * 45, 4, 4]));
    expect(new Set(inlines.images.map((i) => i.hash)).size).toBe(INLINES);
    for (const n of [1, 7, INLINES]) {
      const p = await pixels((await extractImage(inline, 1, n)).data);
      expect([p.width, p.height]).toEqual([4, 4]);
      expect(p.at(0, 0)).toEqual([...INLINE_COLOUR(n - 1), 255]);
      expect(p.at(3, 3)).toEqual([...INLINE_COLOUR(n - 1), 255]);
    }
  });

  it("scales an image over 8 MP down, keeping its aspect", async () => {
    const image = await extractImage(await bigImagePage(), 1, 1);
    expect(image.width * image.height).toBeLessThanOrEqual(MAX_STORED_PIXELS);
    expect(image.width * image.height).toBeGreaterThan(MAX_STORED_PIXELS * 0.99);
    expect(Math.abs(image.width / image.height - 4 / 3)).toBeLessThan(0.002);
    expect([image.originalWidth, image.originalHeight]).toEqual([4000, 3000]);
    const header = await pixels(image.data);
    expect([header.width, header.height]).toEqual([image.width, image.height]);
  });

  it("scales an image longer than 16,384 px on a side down", async () => {
    const image = await extractImage(await longImagePage(), 1, 1);
    expect([image.width, image.height, image.originalWidth, image.originalHeight]).toEqual([16_384, 40, 20_000, 50]);
  });

  it("refuses an image past the page's last and a page outside the PDF", async () => {
    const pdf = await twoImagesPage();
    expect((await failure(extractImage(pdf, 1, 3))).code).toBe("no_such_image");
    expect((await failure(extractImage(pdf, 1, 0))).code).toBe("no_such_image");
    expect((await failure(extractImage(pdf, 2, 1))).code).toBe("no_such_page");
  });
});

// pdf.js's getOperatorList never optimises (its NullOptimizer), but rendering does, so the walker
// knows the optimised forms too. These rewrite a page's operator list as the optimiser would.
describe("optimised operator lists", () => {
  type Ops = { fnArray: number[]; argsArray: unknown[][] };

  // The runs of save, transform, paint, restore (and the paint's dependency before the first).
  function quads(ops: Ops, paint: number) {
    const found: { transform: number[]; args: unknown[] }[] = [];
    const fnArray: number[] = [];
    const argsArray: unknown[][] = [];
    for (let i = 0; i < ops.fnArray.length; i++) {
      const [s, t, p, r] = ops.fnArray.slice(i, i + 4);
      if (s === OPS.save && t === OPS.transform && p === paint && r === OPS.restore) {
        found.push({ transform: ops.argsArray[i + 1] as number[], args: ops.argsArray[i + 2]! });
        i += 3;
      } else if (!(ops.fnArray[i] === OPS.save && ops.fnArray[i + 1] === OPS.transform && ops.fnArray[i + 2] === OPS.dependency)) {
        fnArray.push(ops.fnArray[i]!);
        argsArray.push(ops.argsArray[i] ?? []);
      } else {
        // save, transform, dependency, paint, restore: keep the dependency, take the rest as a quad.
        fnArray.push(OPS.dependency);
        argsArray.push(ops.argsArray[i + 2]!);
        found.push({ transform: ops.argsArray[i + 1] as number[], args: ops.argsArray[i + 3]! });
        i += 4;
      }
    }
    return { found, fnArray, argsArray };
  }

  async function both(pdf: Uint8Array, rewrite: (ops: Ops) => Ops) {
    const doc = await openPdf(pdf);
    try {
      const page = await doc.getPage(1);
      const ops = (await page.getOperatorList({ annotationMode: AnnotationMode.DISABLE })) as unknown as Ops;
      const plain = await imagesOf(page, ops);
      const optimised = await imagesOf(page, rewrite(ops));
      return { plain, optimised };
    } finally {
      await doc.loadingTask.destroy();
    }
  }

  it("reads paintImageXObjectRepeat", async () => {
    const { plain, optimised } = await both(await repeatedImagePage(), (ops) => {
      const { found, fnArray, argsArray } = quads(ops, OPS.paintImageXObject);
      const [a, , , d] = found[0]!.transform;
      return {
        fnArray: [...fnArray, OPS.paintImageXObjectRepeat],
        argsArray: [...argsArray, [found[0]!.args[0], a, d, new Float32Array(found.flatMap((f) => [f.transform[4]!, f.transform[5]!]))]],
      };
    });
    expect(plain).toHaveLength(REPEATS);
    expect(optimised).toEqual(plain);
  });

  it("reads paintImageMaskXObjectRepeat and paintImageMaskXObjectGroup", async () => {
    const repeat = await both(await manyMasksPage("same"), (ops) => {
      const { found, fnArray, argsArray } = quads(ops, OPS.paintImageMaskXObject);
      const [a, b, c, d] = found[0]!.transform;
      return {
        fnArray: [...fnArray, OPS.paintImageMaskXObjectRepeat],
        argsArray: [...argsArray, [(found[0]!.args as unknown[])[0], a, b, c, d, new Float32Array(found.flatMap((f) => [f.transform[4]!, f.transform[5]!]))]],
      };
    });
    expect(repeat.plain).toHaveLength(MASKS);
    expect(repeat.optimised).toEqual(repeat.plain);

    const group = await both(await manyMasksPage("growing"), (ops) => {
      const { found, fnArray, argsArray } = quads(ops, OPS.paintImageMaskXObject);
      const images = found.map((f) => ({ ...(f.args[0] as object), transform: f.transform }));
      return { fnArray: [...fnArray, OPS.paintImageMaskXObjectGroup], argsArray: [...argsArray, [images]] };
    });
    expect(group.plain).toHaveLength(MASKS);
    expect(group.optimised).toEqual(group.plain);
  });

  it("cuts inline images out of their atlas (paintInlineImageXObjectGroup)", async () => {
    const { plain, optimised } = await both(await inlineImagesPage(), (ops) => {
      const { found, fnArray, argsArray } = quads(ops, OPS.paintInlineImageXObject);
      // As pdf.js packs them: one row, a pixel of padding around each, the padding filled with noise here.
      const imgs = found.map((f) => f.args[0] as { width: number; height: number; data: Uint8Array });
      const width = imgs.reduce((x, i) => x + i.width + 2, 1) + 1;
      const height = Math.max(...imgs.map((i) => i.height)) + 2;
      const atlas = new Uint8Array(width * height * 4).map((_, i) => (i * 97) & 0xff);
      let x = 1;
      const map = imgs.map((img, q) => {
        for (let row = 0; row < img.height; row++)
          atlas.set(img.data.subarray(row * img.width * 4, (row + 1) * img.width * 4), ((1 + row) * width + x) * 4);
        const entry = { transform: found[q]!.transform, x, y: 1, w: img.width, h: img.height };
        x += img.width + 2;
        return entry;
      });
      return {
        fnArray: [...fnArray, OPS.paintInlineImageXObjectGroup],
        argsArray: [...argsArray, [{ width, height, kind: 3, data: atlas }, map]],
      };
    });
    expect(plain).toHaveLength(INLINES);
    // The hashes differ (an atlas is hashed as its pieces); boxes, sizes and the pixels do not.
    expect(optimised.map(({ hash: _, ...rest }) => rest)).toEqual(plain.map(({ hash: _, ...rest }) => rest));
    expect(new Set(optimised.map((i) => i.hash)).size).toBe(INLINES);
  });
});

describe("renderRegion", () => {
  it("renders a region at 300 dpi as PNG", async () => {
    const crop = { x: 50, y: 400, w: 200, h: 100 };
    const r = await renderRegion(await vectorDrawingPage(), 1, crop, 0, "png");
    expect(r).toMatchObject({ mediaType: "image/png", width: Math.floor((200 * STORED_DPI) / 72), height: Math.floor((100 * STORED_DPI) / 72) });
    expect(r).toMatchObject({ pageWidth: A4.width, pageHeight: A4.height, rotate: 0 });
    starts(r.data, PNG);
  });

  it("caps a region at 8 MP and 16,384 px on a side", async () => {
    const whole = await renderRegion(await vectorDrawingPage(), 1, { x: 0, y: 0, w: A4.width, h: A4.height }, 0, "png");
    expect(whole.width * whole.height).toBeLessThanOrEqual(MAX_STORED_PIXELS);
    expect(whole.width * whole.height).toBeGreaterThan(MAX_STORED_PIXELS * 0.99);
    const wide = await renderRegion(await widePage(), 1, { x: 0, y: 0, w: 4000, h: 100 }, 0, "png");
    expect(wide.width).toBe(16_384);
  });

  it("renders as JPEG when asked, turned", async () => {
    const r = await renderRegion(await cornersPage(), 1, { x: 0, y: 0, w: 100, h: 200 }, 90, "jpeg");
    const [width, height] = [Math.floor((100 * STORED_DPI) / 72), Math.floor((200 * STORED_DPI) / 72)];
    expect(r).toMatchObject({ mediaType: "image/jpeg", rotate: 90, width, height });
    starts(r.data, JPEG);
    // Turned by 90°, the page's bottom-left square comes to its top left, and white is below it.
    const p = await pixels(r.data);
    near(p.at(width / 2, height / 4), CORNER_COLOURS[3], 8);
    near(p.at(width / 2, (height * 3) / 4), [255, 255, 255], 8);
  });

  it("refuses a region outside the page and a page outside the PDF", async () => {
    const pdf = await vectorDrawingPage();
    expect((await failure(renderRegion(pdf, 1, { x: 500, y: 0, w: 200, h: 100 }, 0, "png"))).code).toBe("bad_crop");
    expect((await failure(renderRegion(pdf, 2, { x: 0, y: 0, w: 10, h: 10 }, 0, "png"))).code).toBe("no_such_page");
  });
});

describe("encodeStored", () => {
  // A transparent canvas with an opaque black square in the middle.
  const canvas = (side: number) => {
    const c = createCanvas(side, side);
    const ctx = c.getContext("2d");
    ctx.fillStyle = "#000000";
    ctx.fillRect(side / 4, side / 4, side / 2, side / 2);
    return c;
  };

  it("keeps PNG with its alpha when it fits", async () => {
    const r = await encodeStored(canvas(64), MAX_BODY_STORED);
    expect(r.mediaType).toBe("image/png");
    expect((await pixels(r.data)).at(1, 1)[3]).toBe(0);
  });

  it("falls back to JPEG on white", async () => {
    const c = noisy(512);
    const r = await encodeStored(c, (await c.encode("png")).length - 1);
    expect(r.mediaType).toBe("image/jpeg");
    starts(r.data, JPEG);
    const p = await pixels(r.data);
    near(p.at(2, 2), [255, 255, 255], 8);
    near(p.at(256, 256), [32, 32, 32], 40);
  });

  it("encodes as asked, and refuses what does not fit", async () => {
    expect((await encodeStored(canvas(64), MAX_BODY_STORED, "jpeg")).mediaType).toBe("image/jpeg");
    const c = noisy(512);
    expect((await failure(encodeStored(c, (await c.encode("png")).length - 1, "png"))).code).toBe("too_large");
    expect((await failure(encodeStored(noisy(512), 1024))).code).toBe("too_large");
    expect((await failure(encodeStored(noisy(512), 1024, "jpeg"))).code).toBe("too_large");
  });

  // Noise of single pixels, dark, as large as a PNG gets, with a transparent square at the top left.
  function noisy(side: number) {
    const c = createCanvas(side, side);
    const rgba = new Uint8ClampedArray(side * side * 4);
    let seed = 7;
    for (let i = 0; i < rgba.length; i += 4) {
      const y = Math.floor(i / 4 / side);
      const x = (i / 4) % side;
      if (x < 32 && y < 32) continue;
      for (let k = 0; k < 3; k++) rgba[i + k] = (seed = (seed * 1103515245 + 12345) & 0x7fffffff) % 64;
      rgba[i + 3] = 255;
    }
    c.getContext("2d").putImageData(new ImageData(rgba, side, side), 0, 0);
    return c;
  }
});

describe("pdf.js's console", () => {
  afterEach(() => vi.restoreAllMocks());

  it("reaches no console, and counts only the oversize warning", async () => {
    const spies = (["log", "warn", "error", "info"] as const).map((m) => vi.spyOn(console, m).mockImplementation(() => {}));
    expect((await listImages(await hugeImagePage(), 1)).over).toBe(1);
    expect((await listImages(await brokenImagePage(), 1)).over).toBe(0);
    await failure(extractImage(await brokenImagePage(), 1, 1));
    await renderRegion(await hugeImagePage(), 1, { x: 0, y: 0, w: 100, h: 100 }, 0, "png");
    const counted = await quietly(async (warnings) => {
      console.warn("Warning: Image exceeded maximum allowed size and was removed.");
      console.warn("Warning: Image exceeded maximum allowed size and was removed. Or not.");
      console.log("anything");
      console.error("Warning: Image exceeded maximum allowed size and was removed.");
      return warnings.oversize;
    });
    expect(counted).toBe(2);
    for (const spy of spies) expect(spy).not.toHaveBeenCalled();
    // And the console is back afterwards.
    console.warn("after");
    expect(spies[1]).toHaveBeenCalledWith("after");
  });
});
