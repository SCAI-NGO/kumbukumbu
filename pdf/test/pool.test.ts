// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it } from "vitest";
import { Gate, type Job, Pool } from "../src/pool.js";

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
