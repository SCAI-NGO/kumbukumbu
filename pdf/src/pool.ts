// SPDX-License-Identifier: AGPL-3.0-or-later

// Runs each job in a fresh worker thread with a heap limit and a time limit, so one hostile file
// can neither stall the others nor leave state behind for the next. A bounded queue holds the
// rest; past it a job is refused at once.

import { Worker } from "node:worker_threads";
import type { Crop, Target } from "./steps.js";
import { PdfError } from "./pdfjs.js";

export type Job =
  | { kind: "analyse"; bytes: Uint8Array; maxPages: number }
  | { kind: "page"; bytes: Uint8Array; page: number; target: Target; crop?: Crop }
  | { kind: "image"; bytes: Uint8Array; target: Target; crop?: Crop };

// What a worker posts: its page count as soon as it knows it, then one answer.
export type WorkerMessage =
  | { type: "pages"; pages: number }
  | { type: "done"; value: unknown }
  | { type: "error"; code: string; message: string };

const FAILED = "The job failed.";

type Pending = { job: Job; timeoutMs: number; extend?: (pages: number) => number; resolve: (v: unknown) => void; reject: (e: unknown) => void };

export class Pool {
  private readonly workers: number;
  private readonly queue: number;
  private readonly entry: URL;
  private readonly waiting: Pending[] = [];
  private readonly running = new Set<Worker>();
  private closed = false;

  constructor(o: { workers: number; queue: number; entry?: URL }) {
    this.workers = o.workers;
    this.queue = o.queue;
    this.entry = o.entry ?? new URL("./worker.js", import.meta.url);
  }

  /** Runs a job within timeoutMs; extend, given the page count the worker reports, may lengthen that limit. */
  run<T>(job: Job, timeoutMs: number, extend?: (pages: number) => number): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      if (this.closed) return reject(new PdfError("The sidecar is shutting down.", "failed"));
      const pending: Pending = { job, timeoutMs, extend, resolve: resolve as (v: unknown) => void, reject };
      if (this.running.size < this.workers) this.start(pending);
      else if (this.waiting.length < this.queue) this.waiting.push(pending);
      else reject(new PdfError("The sidecar is busy; try again shortly.", "busy"));
    });
  }

  async close(): Promise<void> {
    this.closed = true;
    for (const p of this.waiting.splice(0)) p.reject(new PdfError("The sidecar is shutting down.", "failed"));
    await Promise.all([...this.running].map((w) => w.terminate()));
  }

  private start(p: Pending) {
    const worker = new Worker(this.entry, { resourceLimits: { maxOldGenerationSizeMb: 512 } });
    this.running.add(worker);
    const started = Date.now();
    let settled = false;
    const settle = (f: () => void) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      f();
      // Never reused. terminate() stops a thread at its next instruction (a native drawing call
      // finishes first), so the slot goes to the next job straight away.
      void worker.terminate();
      this.running.delete(worker);
      const next = this.waiting.shift();
      if (next && !this.closed) this.start(next);
    };
    const expire = () =>
      settle(() => p.reject(new PdfError("The job took too long and was stopped.", "timeout")));
    let timer = setTimeout(expire, p.timeoutMs);

    worker.on("message", (m: WorkerMessage) => {
      if (m.type === "pages") {
        if (!p.extend || settled) return;
        clearTimeout(timer);
        const limit = Math.max(p.timeoutMs, p.extend(m.pages));
        timer = setTimeout(expire, Math.max(0, started + limit - Date.now()));
      } else if (m.type === "done") settle(() => p.resolve(m.value));
      else settle(() => p.reject(new PdfError(m.message, m.code)));
    });
    // A crash or a heap over its limit; what it says may quote the file, so it is not passed on.
    worker.on("error", () => settle(() => p.reject(new PdfError(FAILED, "failed"))));
    worker.on("exit", () => settle(() => p.reject(new PdfError(FAILED, "failed"))));
    worker.postMessage(p.job);
  }
}
