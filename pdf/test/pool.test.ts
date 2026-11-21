// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it } from "vitest";
import { Gate, type Job, Pool } from "../src/pool.js";
import { MEDIUM, translucentPage } from "./fixtures.js";

const stub = new URL("./workers/stub.mjs", import.meta.url);
// The stub worker understands job kinds of its own.
const job = (j: Record<string, unknown>) => j as unknown as Job;

let pool: Pool | undefined;
afterEach(async () => {
  await pool?.close();
  pool = undefined;
});

describe("Pool", () => {
  it("runs a job in a worker and answers its value", async () => {
    pool = new Pool({ workers: 1, queue: 0, entry: stub });
    expect(await pool.run(job({ kind: "echo", value: 42 }), 5_000)).toBe(42);
  });

  it("rejects a job that never ends with timeout, and the next job succeeds", async () => {
    pool = new Pool({ workers: 1, queue: 1, entry: stub });
    const started = Date.now();
    await expect(pool.run(job({ kind: "hang" }), 300)).rejects.toMatchObject({ code: "timeout" });
    expect(Date.now() - started).toBeGreaterThanOrEqual(290);
    expect(await pool.run(job({ kind: "echo", value: "next" }), 5_000)).toBe("next");
  });

  it("answers busy past workers plus queue", async () => {
    pool = new Pool({ workers: 1, queue: 2, entry: stub });
    const running = [1, 2, 3].map(() => pool!.run(job({ kind: "hang" }), 500));
    await expect(pool.run(job({ kind: "echo", value: 1 }), 500)).rejects.toMatchObject({ code: "busy" });
    for (const r of running) await expect(r).rejects.toMatchObject({ code: "timeout" });
  });

  it("counts a reservation against workers plus queue until it runs or is released", async () => {
    pool = new Pool({ workers: 1, queue: 0, entry: stub });
    const reservation = pool.reserve();
    expect(() => pool!.reserve()).toThrowError(expect.objectContaining({ code: "busy" }));
    await expect(pool.run(job({ kind: "echo", value: 1 }), 500)).rejects.toMatchObject({ code: "busy" });
    reservation.release();
    expect(await pool.reserve().run(job({ kind: "echo", value: 2 }), 5_000)).toBe(2);
  });

  it("drops a waiting job whose signal aborts, and frees its place at once", async () => {
    pool = new Pool({ workers: 1, queue: 1, entry: stub });
    const hung = pool.run(job({ kind: "hang" }), 1_000);
    const leaving = new AbortController();
    const waiting = pool.reserve(leaving.signal).run(job({ kind: "echo", value: 1 }), 5_000);
    expect(pool.load).toBe(2);
    leaving.abort();
    await expect(waiting).rejects.toMatchObject({ code: "gone" });
    expect(pool.load).toBe(1);
    await expect(hung).rejects.toMatchObject({ code: "timeout" });
  });

  it("drops a job whose signal aborted before it was queued", async () => {
    pool = new Pool({ workers: 1, queue: 0, entry: stub });
    await expect(pool.run(job({ kind: "echo", value: 1 }), 5_000, undefined, AbortSignal.abort())).rejects.toMatchObject({ code: "gone" });
    expect(pool.load).toBe(0);
  });

  it("keeps a timed-out worker's slot until its thread has gone", async () => {
    pool = new Pool({ workers: 1, queue: 1, entry: stub });
    await expect(pool.run(job({ kind: "hang" }), 100)).rejects.toMatchObject({ code: "timeout" });
    expect(pool.threads).toBeLessThanOrEqual(1);
    expect(await pool.run(job({ kind: "echo", value: "after" }), 5_000)).toBe("after");
  });

  it("transfers the job's bytes to the worker", async () => {
    pool = new Pool({ workers: 1, queue: 0, entry: stub });
    const bytes = new Uint8Array(1024);
    await pool.run(job({ kind: "echo", value: 1, bytes }), 5_000);
    expect(bytes.byteLength).toBe(0);
  });

  it("extends the deadline by the page count a worker reports", async () => {
    pool = new Pool({ workers: 1, queue: 0, entry: stub });
    const extend = (pages: number) => 300 + pages * 100;
    expect(await pool.run(job({ kind: "slow", pages: 5, ms: 600 }), 300, extend)).toBe("slow");
    await expect(pool.run(job({ kind: "slow", pages: 1, ms: 600 }), 300, extend)).rejects.toMatchObject({ code: "timeout" });
  });

  it("passes a worker's error code on, and hides what a crash says", async () => {
    pool = new Pool({ workers: 1, queue: 0, entry: stub });
    await expect(pool.run(job({ kind: "fail" }), 5_000)).rejects.toMatchObject({ code: "not_pdf", message: "The file is not a PDF." });
    const crash = pool.run(job({ kind: "crash" }), 5_000);
    await expect(crash).rejects.toMatchObject({ code: "failed" });
    await expect(crash).rejects.not.toMatchObject({ message: expect.stringContaining("secret") });
  });

  it("rejects queued jobs when closed", async () => {
    pool = new Pool({ workers: 1, queue: 1, entry: stub });
    const first = pool.run(job({ kind: "hang" }), 10_000);
    const second = pool.run(job({ kind: "echo", value: 1 }), 10_000);
    const outcomes = Promise.allSettled([first, second]);
    await pool.close();
    for (const o of await outcomes) expect(o).toMatchObject({ status: "rejected", reason: { code: "failed" } });
  });
});

describe("Gate", () => {
  let other: Pool | undefined;
  afterEach(async () => {
    await other?.close();
    other = undefined;
  });

  // Samples the live threads of both pools until done settles, and answers the most seen at once.
  async function peak(pools: Pool[], done: Promise<unknown>): Promise<number> {
    let most = 0;
    let over = false;
    void done.finally(() => (over = true));
    while (!over) {
      most = Math.max(most, pools.reduce((n, p) => n + p.threads, 0));
      await new Promise((r) => setTimeout(r, 5));
    }
    return most;
  }

  it("keeps the jobs running across two pools to its limit, and starts the third when one ends", async () => {
    const gate = new Gate(2);
    pool = new Pool({ workers: 2, queue: 4, entry: stub, gate });
    other = new Pool({ workers: 1, queue: 2, entry: stub, gate });
    const started = Date.now();
    const renders = [1, 2].map(() => pool!.run(job({ kind: "slow", pages: 1, ms: 400 }), 5_000));
    const extraction = other.run<number>(job({ kind: "echo", value: 3 }), 5_000).then((v) => [v, Date.now() - started] as const);
    const all = Promise.all([...renders, extraction]);
    expect(await peak([pool, other], all)).toBe(2);
    const [value, after] = (await all)[2] as readonly [number, number];
    expect(value).toBe(3);
    expect(after).toBeGreaterThanOrEqual(390);
  });

  it("holds a render back while an extraction and a render run", async () => {
    const gate = new Gate(2);
    pool = new Pool({ workers: 2, queue: 4, entry: stub, gate });
    other = new Pool({ workers: 1, queue: 2, entry: stub, gate });
    const started = Date.now();
    const extraction = other.run(job({ kind: "slow", pages: 1, ms: 400 }), 5_000);
    const render = pool.run(job({ kind: "slow", pages: 1, ms: 400 }), 5_000);
    const third = pool.run(job({ kind: "echo", value: 3 }), 5_000).then(() => Date.now() - started);
    const all = Promise.all([extraction, render, third]);
    expect(await peak([pool, other], all)).toBe(2);
    expect((await all)[2]).toBeGreaterThanOrEqual(390);
  });

  it("still answers busy past a pool's workers plus queue while the gate holds its jobs back", async () => {
    const gate = new Gate(2);
    pool = new Pool({ workers: 2, queue: 0, entry: stub, gate });
    other = new Pool({ workers: 1, queue: 1, entry: stub, gate });
    const renders = [1, 2].map(() => pool!.run(job({ kind: "hang" }), 300));
    const waiting = [1, 2].map(() => other!.run(job({ kind: "echo", value: 1 }), 5_000));
    await expect(other.run(job({ kind: "echo", value: 1 }), 5_000)).rejects.toMatchObject({ code: "busy" });
    for (const r of renders) await expect(r).rejects.toMatchObject({ code: "timeout" });
    for (const w of waiting) expect(await w).toBe(1);
  });
});

// Skia draws an SVG in one native call that terminate() can't interrupt: a few filters take minutes.
describe("an SVG", () => {
  const worker = new URL("../dist/worker.js", import.meta.url);
  const svg = (body: string) => new TextEncoder().encode(`<svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">${body}</svg>`);
  const heavy = svg(
    '<filter id="f" x="-10" y="-10" width="30" height="30"><feGaussianBlur stdDeviation="50"/><feMorphology radius="20"/></filter>' +
      '<rect width="200" height="200" filter="url(#f)"/>'.repeat(50),
  );
  const target = { longEdge: 1568, pixels: 1_200_000, bytes: 409_600 };

  it("that draws past its deadline is stopped then, and its slot is free for the next job at once", async () => {
    pool = new Pool({ workers: 1, queue: 1, entry: worker });
    const started = Date.now();
    await expect(pool.run(job({ kind: "svg", bytes: heavy, target, rotate: 0 }), 500)).rejects.toMatchObject({ code: "timeout" });
    const next = await pool.run<{ width: number }>(job({ kind: "svg", bytes: svg('<rect width="10" height="10"/>'), target, rotate: 0 }), 5_000);
    expect(next.width).toBe(1095);
    expect(Date.now() - started).toBeLessThan(4_000);
    expect(pool.threads).toBe(0);
  });
});

// Skia rasterises a PDF page in one native call too, inside the first encode: terminate() waits for it.
describe("a PDF page slow to draw", () => {
  const worker = new URL("../dist/worker.js", import.meta.url);
  const jobs: [string, (bytes: Uint8Array) => Job][] = [
    ["a page", (bytes) => ({ kind: "page", bytes, page: 1, target: MEDIUM, rotate: 0 })],
    ["a region", (bytes) => ({ kind: "region", bytes, page: 1, crop: { x: 0, y: 0, w: 595, h: 842 }, rotate: 0, format: "png" })],
  ];

  it.each(jobs)("as %s is stopped at its deadline, and its slot is free at once", async (_, make) => {
    pool = new Pool({ workers: 1, queue: 1, entry: worker });
    const slow = await translucentPage(100_000);
    const started = Date.now();
    await expect(pool.run(make(slow), 2_000)).rejects.toMatchObject({ code: "timeout" });
    while (pool.threads > 0 && Date.now() - started < 25_000) await new Promise((r) => setTimeout(r, 25));
    expect(Date.now() - started).toBeLessThan(3_000);
    const quick = await pool.run<{ width: number }>(make(await translucentPage(10)), 10_000);
    expect(quick.width).toBeGreaterThan(0);
  }, 30_000);
});

