// SPDX-License-Identifier: AGPL-3.0-or-later
import { expect, test } from "vitest";
import { SerialQueue } from "../src/engine/queue";

const tick = () => new Promise((r) => setTimeout(r, 0));

test("jobs run strictly one after another", async () => {
  const q = new SerialQueue();
  const log: string[] = [];
  let running = 0;
  const job = (name: string) => async () => {
    running++;
    expect(running).toBe(1);
    log.push(`start ${name}`);
    await tick();
    log.push(`end ${name}`);
    running--;
  };
  void q.push(job("a"));
  void q.push(job("b"));
  await q.push(job("c"));
  expect(log).toEqual(["start a", "end a", "start b", "end b", "start c", "end c"]);
});

test("a keyed job waiting is not added twice", async () => {
  const q = new SerialQueue();
  let release!: () => void;
  const runs: string[] = [];
  void q.push(() => new Promise<void>((r) => { release = r; }));
  const first = q.push(async () => { runs.push("check"); }, "check");
  const second = q.push(async () => { runs.push("check again"); }, "check");
  expect(second).toBe(first);
  release();
  await q.idle();
  expect(runs).toEqual(["check"]);
});

test("a running keyed job does not block adding a new one with the same key", async () => {
  const q = new SerialQueue();
  let release!: () => void;
  const runs: number[] = [];
  const first = q.push(() => new Promise<void>((r) => { runs.push(1); release = r; }), "check");
  await tick();
  const second = q.push(async () => { runs.push(2); }, "check");
  expect(second).not.toBe(first);
  release();
  await q.idle();
  expect(runs).toEqual([1, 2]);
});

test("a failing job rejects its own promise and the queue goes on", async () => {
  const q = new SerialQueue();
  const failed = q.push(async () => { throw new Error("boom"); });
  const next = q.push(async () => {});
  await expect(failed).rejects.toThrow("boom");
  await expect(next).resolves.toBeUndefined();
});

test("idle waits for every job, also ones added while running", async () => {
  const q = new SerialQueue();
  const runs: number[] = [];
  void q.push(async () => { runs.push(1); void q.push(async () => { runs.push(2); }); });
  await q.idle();
  expect(runs).toEqual([1, 2]);
  expect(q.busy).toBe(false);
});
