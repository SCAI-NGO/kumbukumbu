// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it } from "vitest";
import { type Job, Pool } from "../src/pool.js";

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
