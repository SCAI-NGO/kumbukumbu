// SPDX-License-Identifier: AGPL-3.0-or-later

// The sidecar's HTTP interface: raw bodies in, JSON or an image out. Stateless; reachable only
// from the server on its own network. Logs method, route, status and duration, nothing else.

import { type IncomingMessage, type Server, type ServerResponse, createServer } from "node:http";
import { pathToFileURL } from "node:url";
import type { Analysis } from "./analyse.js";
import type { PageBoxes } from "./boxes.js";
import type { PageImages, StoredImage } from "./extract.js";
import type { RenderedImage } from "./image.js";
import { PdfError } from "./pdfjs.js";
import { Pool, type Reservation } from "./pool.js";
import type { RenderedPage } from "./render.js";
import type { Crop, Target } from "./steps.js";

export const MAX_BODY = 16 * 1024 * 1024;
export const MAX_PAGES = 2000;
export const MAX_BOX_PAGES = 50;
export const RENDER_MS = 10_000;
// An analysis gets 30 s plus 0.5 s per page, at most 20 min: pdf.js decodes a scan's image even to
// measure the page (about 0.3 s each), and an ordinary scanned book must not time out for good.
export const ANALYSIS_MS = 30_000;
export const analysisLimit = (pages: number) => Math.min(20 * 60_000, ANALYSIS_MS + 500 * pages);
// Extraction decodes a page's images at full size, so it runs in a pool of one worker with a short
// queue: the server's time-out covers a job waiting behind the queue.
export const EXTRACT_MS = 30_000;
export const EXTRACT_QUEUE = 2;

const STATUS: Record<string, number> = {
  bad_request: 400,
  not_pdf: 400,
  not_image: 400,
  too_large: 413,
  no_such_page: 422,
  no_such_frame: 422,
  no_such_image: 422,
  bad_crop: 422,
  encrypted: 422,
  busy: 503,
  timeout: 504,
};

class TooLarge extends Error {}

const bad = (message: string) => new PdfError(message, "bad_request");

export type Limits = { renderMs: number; analysisMs: number; extractMs: number };

/** extract runs the /images routes; by default the same pool as the rest. */
export function createSidecar(pool: Pool, limits: Partial<Limits> = {}, extract: Pool = pool): Server {
  const ms: Limits = { renderMs: RENDER_MS, analysisMs: ANALYSIS_MS, extractMs: EXTRACT_MS, ...limits };
  const server = createServer((req, res) => {
    const started = Date.now();
    const path = (req.url ?? "/").split("?")[0];
    const route = req.method === "GET" && path === "/healthz" ? path : req.method === "POST" && Object.hasOwn(ROUTES, path!) ? path! : undefined;
    res.on("finish", () => console.log(`${req.method} ${route ?? "(unknown)"} ${res.statusCode} ${Date.now() - started}ms`));

    if (!route) return error(res, 404, "not_found", "There is no such route.");
    if (route === "/healthz") return json(res, 200, { status: "ok" });

    handle(route, req, EXTRACTION.has(route) ? extract : pool, ms).then(
      (send) => send(res),
      (e: unknown) => {
        // Answered before the body is in (busy, too large, bad parameters): the rest is not wanted.
        if (!req.complete) {
          res.setHeader("Connection", "close");
          res.on("finish", () => req.destroy());
        }
        if (e instanceof TooLarge) return error(res, 413, "too_large", `The body is larger than ${MAX_BODY / 1024 / 1024} MiB.`);
        if (e instanceof PdfError && Object.hasOwn(STATUS, e.code)) return error(res, STATUS[e.code]!, e.code, e.message);
        // Nothing of an unexpected error's text: it may quote the file.
        error(res, 500, "failed", "The sidecar could not process the request.");
      },
    );
  });
  // Every connection can carry a job or wait for one, plus a margin for health checks; a 16 MiB
  // body over the compose network takes well under a minute.
  server.maxConnections = pool.capacity + (extract === pool ? 0 : extract.capacity) + 16;
  server.headersTimeout = 20_000;
  server.requestTimeout = 60_000;
  return server;
}

type Send = (res: ServerResponse) => void;
// A route checks its parameters first, then runs with the body in the place reserved for it.
type Route = (q: URLSearchParams, ms: Limits) => (body: Uint8Array, slot: Reservation) => Promise<Send>;

const ROUTES: Record<string, Route> = {
  "/analyse": (q, ms) => {
    const maxPages = q.has("maxPages") ? integer(q, "maxPages", 1, MAX_PAGES) : MAX_PAGES;
    return async (body, slot) => {
      const a = await slot.run<Analysis>({ kind: "analyse", bytes: body, maxPages }, ms.analysisMs, analysisLimit);
      return (res) => json(res, 200, a);
    };
  },
  "/render/page": (q, ms) => {
    const [page, t, c, rotate] = [integer(q, "page", 1, Number.MAX_SAFE_INTEGER), target(q), crop(q), turn(q)];
    return async (body, slot) => {
      const r = await slot.run<RenderedPage>({ kind: "page", bytes: body, page, target: t, crop: c, rotate }, ms.renderMs);
      return (res) => image(res, r, { "X-Kumbu-Page-Width": r.pageWidth, "X-Kumbu-Page-Height": r.pageHeight });
    };
  },
  "/render/image": (q, ms) => {
    const [t, c, rotate] = [target(q), crop(q), turn(q)];
    const frame = q.has("frame") ? integer(q, "frame", 1, 1_000_000) : undefined;
    return async (body, slot) => {
      const r = await slot.run<RenderedImage>({ kind: "image", bytes: body, target: t, crop: c, rotate, frame }, ms.renderMs);
      return (res) =>
        image(res, r, {
          "X-Kumbu-Original-Width": r.originalWidth,
          "X-Kumbu-Original-Height": r.originalHeight,
          "X-Kumbu-Transparent": r.transparent ? 1 : 0,
          // Like X-Kumbu-Rotate, the frame it composed lets the server tell an older sidecar.
          ...(r.frame !== undefined ? { "X-Kumbu-Frame": r.frame } : {}),
          ...(r.frames !== undefined ? { "X-Kumbu-Frames": r.frames } : {}),
        });
    };
  },
  "/text-boxes": (q, ms) => {
    const pages = pageList(q, "pages", MAX_BOX_PAGES);
    return async (body, slot) => {
      const b = await slot.run<{ pages: PageBoxes[] }>({ kind: "boxes", bytes: body, pages }, ms.renderMs);
      return (res) => json(res, 200, b);
    };
  },
  "/images/list": (q, ms) => {
    const page = integer(q, "page", 1, Number.MAX_SAFE_INTEGER);
    return async (body, slot) => {
      const list = await slot.run<PageImages>({ kind: "list", bytes: body, page }, ms.extractMs);
      return (res) => json(res, 200, list);
    };
  },
  "/images/image": (q, ms) => {
    const [page, n] = [integer(q, "page", 1, Number.MAX_SAFE_INTEGER), integer(q, "n", 1, Number.MAX_SAFE_INTEGER)];
    return async (body, slot) => {
      const r = await slot.run<StoredImage>({ kind: "extract", bytes: body, page, n }, ms.extractMs);
      return (res) => image(res, { ...r, rotate: 0 }, { "X-Kumbu-Original-Width": r.originalWidth, "X-Kumbu-Original-Height": r.originalHeight });
    };
  },
  "/images/region": (q, ms) => {
    const [page, c, rotate, format] = [integer(q, "page", 1, Number.MAX_SAFE_INTEGER), crop(q), turn(q), q.get("format")];
    if (!c) throw bad("crop is required.");
    if (format !== "png" && format !== "jpeg") throw bad("format must be png or jpeg.");
    return async (body, slot) => {
      const r = await slot.run<RenderedPage>({ kind: "region", bytes: body, page, crop: c, rotate, format }, ms.extractMs);
      return (res) => image(res, r, { "X-Kumbu-Page-Width": r.pageWidth, "X-Kumbu-Page-Height": r.pageHeight });
    };
  },
};

const EXTRACTION = new Set(["/images/list", "/images/image", "/images/region"]);

// The place in the pool is taken before the body is read, so a full pool answers busy at once and
// bodies in memory never outnumber the jobs the pool holds.
async function handle(route: string, req: IncomingMessage, pool: Pool, ms: Limits): Promise<Send> {
  const q = new URL(req.url ?? "/", "http://sidecar").searchParams;
  const run = ROUTES[route]!(q, ms);
  const slot = pool.reserve();
  let body: Uint8Array;
  try {
    body = await read(req);
  } catch (e) {
    slot.release();
    throw e;
  }
  return run(body, slot);
}

// The body in a buffer of its own (not Node's shared pool), so it can be transferred to the worker.
function read(req: IncomingMessage): Promise<Uint8Array> {
  const declared = Number(req.headers["content-length"]);
  if (declared > MAX_BODY) return Promise.reject(new TooLarge());
  return new Promise((resolve, reject) => {
    const pieces: Buffer[] = [];
    let size = 0;
    req.on("data", (piece: Buffer) => {
      size += piece.length;
      if (size > MAX_BODY) {
        req.pause();
        req.removeAllListeners("data");
        reject(new TooLarge());
      } else pieces.push(piece);
    });
    req.on("end", () => {
      const body = new Uint8Array(new ArrayBuffer(size));
      let at = 0;
      for (const piece of pieces) {
        body.set(piece, at);
        at += piece.length;
      }
      resolve(body);
    });
    req.on("error", reject);
  });
}

function integer(q: URLSearchParams, name: string, min: number, max: number): number {
  const text = q.get(name);
  const v = text !== null && /^\d{1,16}$/.test(text) ? Number(text) : NaN;
  if (!(v >= min && v <= max)) throw bad(`${name} must be a whole number from ${min} to ${max}.`);
  return v;
}

// Whole numbers as integer takes them, at most max, each above the one before.
function pageList(q: URLSearchParams, name: string, max: number): number[] {
  const parts = (q.get(name) ?? "").split(",");
  const pages = parts.map((p) => (/^\d{1,16}$/.test(p) ? Number(p) : NaN));
  const ok = parts.length <= max && pages.every((v, i) => v >= 1 && v <= Number.MAX_SAFE_INTEGER && (i === 0 || v > pages[i - 1]!));
  if (!ok) throw bad(`${name} must be 1 to ${max} whole numbers, ascending and separated by commas.`);
  return pages;
}

function target(q: URLSearchParams): Target {
  return {
    longEdge: integer(q, "longEdge", 1, 16_384),
    pixels: integer(q, "pixels", 1, 50_000_000),
    bytes: integer(q, "bytes", 1, MAX_BODY),
  };
}

// Clockwise degrees; absent is no turn.
function turn(q: URLSearchParams): number {
  const text = q.get("rotate");
  if (text === null) return 0;
  if (text !== "90" && text !== "180" && text !== "270") throw bad("rotate must be 90, 180 or 270.");
  return Number(text);
}

function crop(q: URLSearchParams): Crop | undefined {
  const text = q.get("crop");
  if (text === null) return undefined;
  const parts = text.split(",").map((p) => (/^\d+(\.\d+)?$/.test(p.trim()) ? Number(p) : NaN));
  const [x, y, w, h] = parts;
  if (parts.length !== 4 || !parts.every(Number.isFinite) || !(w! > 0) || !(h! > 0))
    throw bad("crop must be x,y,w,h: four numbers, width and height above zero.");
  return { x: x!, y: y!, w: w!, h: h! };
}

// Says the turn it made, so the server can tell an older sidecar that ignored rotate.
function image(res: ServerResponse, r: { data: Uint8Array; mediaType: string; width: number; height: number; rotate: number }, extra: Record<string, number>) {
  res.writeHead(200, {
    "Content-Type": r.mediaType,
    "Content-Length": r.data.length,
    "X-Kumbu-Width": r.width,
    "X-Kumbu-Height": r.height,
    ...extra,
    ...(r.rotate !== 0 ? { "X-Kumbu-Rotate": r.rotate } : {}),
  });
  res.end(r.data);
}

function json(res: ServerResponse, status: number, value: unknown) {
  const body = Buffer.from(JSON.stringify(value));
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Content-Length": body.length });
  res.end(body);
}

function error(res: ServerResponse, status: number, code: string, message: string) {
  json(res, status, { error: message, code });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const pool = new Pool({ workers: 2, queue: 16 });
  const extract = new Pool({ workers: 1, queue: EXTRACT_QUEUE });
  const server = createSidecar(pool, {}, extract);
  const port = Number(process.env["PORT"] ?? 8090);
  server.listen(port, "0.0.0.0", () => console.log(`PDF sidecar listening on port ${port}`));
  const stop = () => server.close(() => void Promise.all([pool.close(), extract.close()]).then(() => process.exit(0)));
  process.on("SIGTERM", stop);
  process.on("SIGINT", stop);
}
