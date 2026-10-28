// SPDX-License-Identifier: AGPL-3.0-or-later

// The sidecar's HTTP interface: raw bodies in, JSON or an image out. Stateless; reachable only
// from the server on its own network. Logs method, route, status and duration, nothing else.

import { type IncomingMessage, type Server, type ServerResponse, createServer } from "node:http";
import { pathToFileURL } from "node:url";
import type { Analysis } from "./analyse.js";
import type { RenderedImage } from "./image.js";
import { PdfError } from "./pdfjs.js";
import { type Job, Pool } from "./pool.js";
import type { RenderedPage } from "./render.js";
import type { Crop, Target } from "./steps.js";

export const MAX_BODY = 16 * 1024 * 1024;
export const MAX_PAGES = 2000;
export const RENDER_MS = 10_000;
// An analysis gets 30 s plus 0.5 s per page, at most 20 min: pdf.js decodes a scan's image even to
// measure the page (about 0.3 s each), and an ordinary scanned book must not time out for good.
export const ANALYSIS_MS = 30_000;
export const analysisLimit = (pages: number) => Math.min(20 * 60_000, ANALYSIS_MS + 500 * pages);

const STATUS: Record<string, number> = {
  bad_request: 400,
  not_pdf: 400,
  not_image: 400,
  too_large: 413,
  no_such_page: 422,
  bad_crop: 422,
  encrypted: 422,
  busy: 503,
  timeout: 504,
};

class TooLarge extends Error {}

const bad = (message: string) => new PdfError(message, "bad_request");

export function createSidecar(pool: Pool): Server {
  return createServer((req, res) => {
    const started = Date.now();
    const path = (req.url ?? "/").split("?")[0];
    const route = req.method === "GET" && path === "/healthz" ? path : req.method === "POST" && Object.hasOwn(ROUTES, path!) ? path! : undefined;
    res.on("finish", () => console.log(`${req.method} ${route ?? "(unknown)"} ${res.statusCode} ${Date.now() - started}ms`));

    if (!route) return error(res, 404, "not_found", "There is no such route.");
    if (route === "/healthz") return json(res, 200, { status: "ok" });

    handle(route, req, pool).then(
      (send) => send(res),
      (e: unknown) => {
        if (e instanceof TooLarge) {
          // Stop reading: the rest of the body is not wanted.
          res.setHeader("Connection", "close");
          res.on("finish", () => req.destroy());
          return error(res, 413, "too_large", `The body is larger than ${MAX_BODY / 1024 / 1024} MiB.`);
        }
        if (e instanceof PdfError && Object.hasOwn(STATUS, e.code)) return error(res, STATUS[e.code]!, e.code, e.message);
        // Nothing of an unexpected error's text: it may quote the file.
        error(res, 500, "failed", "The sidecar could not process the request.");
      },
    );
  });
}

type Send = (res: ServerResponse) => void;

const ROUTES: Record<string, (q: URLSearchParams, body: Uint8Array, pool: Pool) => Promise<Send>> = {
  "/analyse": async (q, body, pool) => {
    const maxPages = q.has("maxPages") ? integer(q, "maxPages", 1, MAX_PAGES) : MAX_PAGES;
    const a = await pool.run<Analysis>({ kind: "analyse", bytes: body, maxPages }, ANALYSIS_MS, analysisLimit);
    return (res) => json(res, 200, a);
  },
  "/render/page": async (q, body, pool) => {
    const job: Job = { kind: "page", bytes: body, page: integer(q, "page", 1, Number.MAX_SAFE_INTEGER), target: target(q), crop: crop(q) };
    const r = await pool.run<RenderedPage>(job, RENDER_MS);
    return (res) => image(res, r, { "X-Kumbu-Page-Width": r.pageWidth, "X-Kumbu-Page-Height": r.pageHeight });
  },
  "/render/image": async (q, body, pool) => {
    const r = await pool.run<RenderedImage>({ kind: "image", bytes: body, target: target(q), crop: crop(q) }, RENDER_MS);
    return (res) => image(res, r, { "X-Kumbu-Original-Width": r.originalWidth, "X-Kumbu-Original-Height": r.originalHeight });
  },
};

async function handle(route: string, req: IncomingMessage, pool: Pool): Promise<Send> {
  const q = new URL(req.url ?? "/", "http://sidecar").searchParams;
  const body = await read(req);
  return ROUTES[route]!(q, body, pool);
}

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
    req.on("end", () => resolve(new Uint8Array(Buffer.concat(pieces))));
    req.on("error", reject);
  });
}

function integer(q: URLSearchParams, name: string, min: number, max: number): number {
  const text = q.get(name);
  const v = text !== null && /^\d{1,16}$/.test(text) ? Number(text) : NaN;
  if (!(v >= min && v <= max)) throw bad(`${name} must be a whole number from ${min} to ${max}.`);
  return v;
}

function target(q: URLSearchParams): Target {
  return {
    longEdge: integer(q, "longEdge", 1, 16_384),
    pixels: integer(q, "pixels", 1, 50_000_000),
    bytes: integer(q, "bytes", 1, MAX_BODY),
  };
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

function image(res: ServerResponse, r: { data: Uint8Array; mediaType: string; width: number; height: number }, extra: Record<string, number>) {
  res.writeHead(200, {
    "Content-Type": r.mediaType,
    "Content-Length": r.data.length,
    "X-Kumbu-Width": r.width,
    "X-Kumbu-Height": r.height,
    ...extra,
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
  const server = createSidecar(pool);
  const port = Number(process.env["PORT"] ?? 8090);
  server.listen(port, "0.0.0.0", () => console.log(`PDF sidecar listening on port ${port}`));
  const stop = () => server.close(() => void pool.close().then(() => process.exit(0)));
  process.on("SIGTERM", stop);
  process.on("SIGINT", stop);
}
