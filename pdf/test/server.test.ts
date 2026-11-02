// SPDX-License-Identifier: AGPL-3.0-or-later
import { request } from "node:http";
import type { AddressInfo } from "node:net";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { Pool } from "../src/pool.js";
import { MAX_BODY, createSidecar } from "../src/server.js";
import { chartPage, encryptedPdf, gifOf, threePages } from "./fixtures.js";

// The built worker (vitest's global setup builds it).
const entry = new URL("../dist/worker.js", import.meta.url);
const pool = new Pool({ workers: 2, queue: 4, entry });
const server = createSidecar(pool);
let base = "";

beforeAll(async () => {
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

afterAll(async () => {
  await new Promise((resolve) => server.close(resolve));
  await pool.close();
});

const post = (path: string, body: Uint8Array) => fetch(base + path, { method: "POST", body });
const medium = "longEdge=1568&pixels=1200000&bytes=409600";

describe("server", () => {
  it("answers /healthz", async () => {
    const r = await fetch(base + "/healthz");
    expect(r.status).toBe(200);
  });

  it("analyses a PDF", async () => {
    const r = await post("/analyse?maxPages=2000", await chartPage());
    expect(r.status).toBe(200);
    expect(r.headers.get("content-type")).toContain("application/json");
    const a = (await r.json()) as { pageCount: number; pages: { type: string }[] };
    expect(a.pageCount).toBe(1);
    expect(a.pages[0]!.type).toBe("mixed");
  });

  it("answers not_pdf for garbage", async () => {
    const r = await post("/analyse", new TextEncoder().encode("garbage"));
    expect(r.status).toBe(400);
    expect(await r.json()).toMatchObject({ code: "not_pdf", error: expect.any(String) });
  });

  it("answers encrypted with 422", async () => {
    const r = await post("/analyse", await encryptedPdf());
    expect(r.status).toBe(422);
    expect(await r.json()).toMatchObject({ code: "encrypted" });
  });

  it("renders a page with its sizes in headers", async () => {
    const r = await post(`/render/page?page=1&${medium}`, await chartPage());
    expect(r.status).toBe(200);
    expect(r.headers.get("content-type")).toBe("image/png");
    expect(r.headers.get("x-kumbu-width")).toBe("920");
    expect(r.headers.get("x-kumbu-height")).toBe("1303");
    expect(r.headers.get("x-kumbu-page-width")).toBe("595");
    expect(r.headers.get("x-kumbu-page-height")).toBe("842");
    const body = new Uint8Array(await r.arrayBuffer());
    expect([...body.subarray(0, 4)]).toEqual([0x89, 0x50, 0x4e, 0x47]);
    expect(r.headers.get("content-length")).toBe(String(body.length));
  });

  it("renders a region", async () => {
    const r = await post(`/render/page?page=1&${medium}&crop=70,400,460,300`, await chartPage());
    expect(r.status).toBe(200);
    expect(Number(r.headers.get("x-kumbu-width")) / Number(r.headers.get("x-kumbu-height"))).toBeCloseTo(460 / 300, 1);
  });

  it("renders an image with its original size in headers", async () => {
    const { createCanvas } = await import("@napi-rs/canvas");
    const png = createCanvas(4000, 3000).toBuffer("image/png");
    const r = await post(`/render/image?${medium}`, png);
    expect(r.status).toBe(200);
    expect(r.headers.get("x-kumbu-width")).toBe("1264");
    expect(r.headers.get("x-kumbu-original-width")).toBe("4000");
    expect(r.headers.get("x-kumbu-original-height")).toBe("3000");
    expect(r.headers.get("x-kumbu-transparent")).toBe("1");
  });

  it("takes rotate on both render routes and answers X-Kumbu-Rotate", async () => {
    const page = await post(`/render/page?page=1&${medium}&rotate=90`, await chartPage());
    expect(page.status).toBe(200);
    expect(page.headers.get("x-kumbu-rotate")).toBe("90");
    expect([page.headers.get("x-kumbu-width"), page.headers.get("x-kumbu-height")]).toEqual(["1303", "920"]);
    expect([page.headers.get("x-kumbu-page-width"), page.headers.get("x-kumbu-page-height")]).toEqual(["595", "842"]);

    const { createCanvas } = await import("@napi-rs/canvas");
    const png = createCanvas(4000, 3000).toBuffer("image/png");
    const image = await post(`/render/image?${medium}&rotate=270`, png);
    expect(image.status).toBe(200);
    expect(image.headers.get("x-kumbu-rotate")).toBe("270");
    expect([image.headers.get("x-kumbu-width"), image.headers.get("x-kumbu-height")]).toEqual(["948", "1264"]);
    expect([image.headers.get("x-kumbu-original-width"), image.headers.get("x-kumbu-original-height")]).toEqual(["4000", "3000"]);

    const half = await post(`/render/page?page=1&${medium}&rotate=180`, await chartPage());
    expect(half.headers.get("x-kumbu-rotate")).toBe("180");
  });

  it("answers no X-Kumbu-Rotate without a turn", async () => {
    const page = await post(`/render/page?page=1&${medium}`, await chartPage());
    expect(page.status).toBe(200);
    expect(page.headers.has("x-kumbu-rotate")).toBe(false);
    const { createCanvas } = await import("@napi-rs/canvas");
    const image = await post(`/render/image?${medium}`, createCanvas(40, 30).toBuffer("image/png"));
    expect(image.status).toBe(200);
    expect(image.headers.has("x-kumbu-rotate")).toBe(false);
  });

  it("refuses rotate other than 90, 180 or 270", async () => {
    const pdf = await chartPage();
    const { createCanvas } = await import("@napi-rs/canvas");
    const png = createCanvas(40, 30).toBuffer("image/png");
    for (const rotate of ["0", "45", "-90", "90.0", "abc"])
      for (const [route, body] of [[`/render/page?page=1&${medium}`, pdf], [`/render/image?${medium}`, png]] as const) {
        const r = await post(`${route}&rotate=${encodeURIComponent(rotate)}`, body);
        expect(r.status, `${route} ${rotate}`).toBe(400);
        expect(await r.json()).toEqual({ code: "bad_request", error: "rotate must be 90, 180 or 270." });
      }
  });

  const threeFrames = () => gifOf(10, 10, [0xff0000, 0x00ff00, 0x0000ff].map((colour) => ({ width: 10, height: 10, palette: [colour, 0], pixels: 0 })));

  it("takes frame and answers X-Kumbu-Frame and X-Kumbu-Frames", async () => {
    const r = await post(`/render/image?${medium}&frame=2`, threeFrames());
    expect(r.status).toBe(200);
    expect([r.headers.get("x-kumbu-frame"), r.headers.get("x-kumbu-frames")]).toEqual(["2", "3"]);
    const still = await post(`/render/image?${medium}`, threeFrames());
    expect(still.status).toBe(200);
    expect(still.headers.has("x-kumbu-frame")).toBe(false);
    expect(still.headers.get("x-kumbu-frames")).toBe("3");
  });

  it("answers no_such_frame with 422", async () => {
    const r = await post(`/render/image?${medium}&frame=4`, threeFrames());
    expect(r.status).toBe(422);
    expect(await r.json()).toMatchObject({ code: "no_such_frame" });
  });

  it("refuses frame 0, 1000001 and 1-3", async () => {
    for (const frame of ["0", "1000001", "1-3"]) {
      const r = await post(`/render/image?${medium}&frame=${frame}`, threeFrames());
      expect(r.status, frame).toBe(400);
      expect(await r.json()).toEqual({ code: "bad_request", error: "frame must be a whole number from 1 to 1000000." });
    }
  });

  it("answers no X-Kumbu-Frames for a PNG", async () => {
    const { createCanvas } = await import("@napi-rs/canvas");
    const r = await post(`/render/image?${medium}`, createCanvas(40, 30).toBuffer("image/png"));
    expect(r.status).toBe(200);
    expect(r.headers.has("x-kumbu-frames")).toBe(false);
    expect(r.headers.has("x-kumbu-frame")).toBe(false);
  });

  it("maps errors to their status", async () => {
    const three = await threePages();
    expect((await post(`/render/page?page=9&${medium}`, three)).status).toBe(422);
    const crop = await post(`/render/page?page=1&${medium}&crop=500,700,200,200`, three);
    expect(crop.status).toBe(422);
    expect(await crop.json()).toMatchObject({ code: "bad_crop" });
    const image = await post(`/render/image?${medium}`, new TextEncoder().encode("garbage"));
    expect(image.status).toBe(400);
    expect(await image.json()).toMatchObject({ code: "not_image" });
  });

  it("refuses bad parameters", async () => {
    const pdf = await chartPage();
    for (const q of ["page=1", `page=x&${medium}`, `page=1&longEdge=0&pixels=1&bytes=1`, `page=1&${medium}&crop=1,2,3`, `page=1&${medium}&crop=1,2,-3,4`]) {
      const r = await post(`/render/page?${q}`, pdf);
      expect(r.status, q).toBe(400);
      expect(await r.json()).toMatchObject({ code: "bad_request" });
    }
    expect((await post("/analyse?maxPages=none", pdf)).status).toBe(400);
  });

  it("refuses a body over 16 MiB with 413", async () => {
    const big = new Uint8Array(16 * 1024 * 1024 + 1);
    const status = await new Promise<number>((resolve, reject) => {
      const req = request(base + "/analyse", { method: "POST", headers: { "content-length": big.length } }, (res) => {
        res.resume();
        resolve(res.statusCode!);
      });
      req.on("error", reject);
      req.end(big);
    });
    expect(status).toBe(413);
  });

  it("refuses a chunked body over 16 MiB with 413", async () => {
    // Exactly one byte over, and then nothing more until the answer: the server has read all that was
    // sent when it closes. With bytes still unread, closing resets the connection (an RST), and the
    // client may lose the answer it was sent (#103).
    const status = await new Promise<number>((resolve, reject) => {
      const req = request(base + "/analyse", { method: "POST" }, (res) => {
        res.resume();
        resolve(res.statusCode!);
      });
      req.on("error", reject);
      req.write(new Uint8Array(MAX_BODY + 1));
      setTimeout(() => reject(new Error("no answer")), 10_000);
    });
    expect(status).toBe(413);
  });

  it("answers 404 for an unknown route", async () => {
    const r = await fetch(base + "/nothing");
    expect(r.status).toBe(404);
    expect((await post("/healthz", new Uint8Array(1))).status).toBe(404);
  });
});

describe("server limits", () => {
  const stub = new URL("./workers/stub.mjs", import.meta.url);
  // A queue of one: a timed-out thread keeps its slot until it has gone, and the next job waits for it.
  const limited = new Pool({ workers: 1, queue: 1, entry: stub });
  const sidecar = createSidecar(limited, { analysisMs: 300 });
  let url = "";

  beforeAll(async () => {
    await new Promise<void>((resolve) => sidecar.listen(0, "127.0.0.1", resolve));
    url = `http://127.0.0.1:${(sidecar.address() as AddressInfo).port}`;
  });

  afterAll(async () => {
    await new Promise((resolve) => sidecar.close(resolve));
    await limited.close();
  });

  it("answers 504 for a job past its limit, and then serves the next", async () => {
    const r = await fetch(url + "/analyse", { method: "POST", body: new Uint8Array(10) });
    expect(r.status).toBe(504);
    expect(await r.json()).toMatchObject({ code: "timeout" });
    expect((await fetch(url + "/healthz")).status).toBe(200);
    const page = await fetch(`${url}/render/page?page=1&${medium}`, { method: "POST", body: new Uint8Array(10) });
    expect(page.status).toBe(200);
    expect(page.headers.get("x-kumbu-page-width")).toBe("1");
  });

  it("answers 503 at once when the pool is full, without waiting for the body", async () => {
    const full = [1, 2].map(() => fetch(url + "/analyse", { method: "POST", body: new Uint8Array(10) }));
    while (limited.load < 2) await new Promise((resolve) => setTimeout(resolve, 10));
    // Headers and a first piece of a large body, the rest never sent.
    const status = await new Promise<number>((resolve, reject) => {
      const req = request(url + "/analyse", { method: "POST", headers: { "content-length": 8 * 1024 * 1024 } }, (res) => {
        res.resume();
        resolve(res.statusCode!);
      });
      req.on("error", () => {});
      req.write(new Uint8Array(64 * 1024));
      setTimeout(() => reject(new Error("no answer while the body was pending")), 2_000);
    });
    expect(status).toBe(503);
    for (const r of await Promise.all(full)) expect(r.status).toBe(504);
  });
});
