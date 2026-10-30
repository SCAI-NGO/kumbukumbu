// SPDX-License-Identifier: AGPL-3.0-or-later

// Runs each job in a fresh worker thread with a heap limit and a time limit, so one hostile file
// can neither stall the others nor leave state behind for the next. Memory outside the heaps
// (decoded images, canvases) is bounded only by the container's limit, which kills the whole
// process, jobs under way included. A bounded queue holds the rest; past it a job is refused at once.

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

const busy = () => new PdfError("The sidecar is busy; try again shortly.", "busy");
const closing = () => new PdfError("The sidecar is shutting down.", "failed");

/** A place in the pool held for a job whose input is still arriving. */
export type Reservation = {
  /** Runs the job in the reserved place (see Pool.run). */
  run<T>(job: Job, timeoutMs: number, extend?: (pages: number) => number): Promise<T>;
  /** Gives the place back unused; nothing once run. */
  release(): void;
};

export class Pool {
  private readonly workers: number;
  private readonly queue: number;
  private readonly entry: URL;
  private readonly waiting: Pending[] = [];
  private readonly running = new Set<Worker>();
  private reserved = 0;
  private closed = false;

  constructor(o: { workers: number; queue: number; entry?: URL }) {
    this.workers = o.workers;
    this.queue = o.queue;
    this.entry = o.entry ?? new URL("./worker.js", import.meta.url);
  }

  /** Jobs the pool holds at most: running, queued and reserved. */
  get capacity(): number {
    return this.workers + this.queue;
  }

  /** Jobs held now (including threads still ending after a time-out). */
  get load(): number {
    return this.running.size + this.waiting.length + this.reserved;
  }

  /** Live worker threads. */
  get threads(): number {
    return this.running.size;
  }

  /** Holds a place for a job, or throws busy: lets the server refuse before it reads a body. */
  reserve(): Reservation {
    if (this.closed) throw closing();
    if (this.load >= this.capacity) throw busy();
    this.reserved++;
    let held = true;
    const give = () => {
      if (!held) return false;
      held = false;
      this.reserved--;
      return true;
    };
    return {
      run: <T>(job: Job, timeoutMs: number, extend?: (pages: number) => number) => {
        if (!give()) return Promise.reject(new Error("A reservation runs one job."));
        return this.enqueue<T>(job, timeoutMs, extend);
      },
      release: () => {
        if (give()) this.pump();
      },
    };
  }

  /**
   * Runs a job within timeoutMs; extend, given the page count the worker reports, may lengthen that
   * limit. The job's bytes are transferred to the worker when they fill their own buffer, which
   * leaves the caller's array empty.
   */
  run<T>(job: Job, timeoutMs: number, extend?: (pages: number) => number): Promise<T> {
    try {
      return this.reserve().run<T>(job, timeoutMs, extend);
    } catch (e) {
      return Promise.reject(e);
    }
  }

  async close(): Promise<void> {
    this.closed = true;
    for (const p of this.waiting.splice(0)) p.reject(closing());
    await Promise.all([...this.running].map((w) => w.terminate()));
  }

  private enqueue<T>(job: Job, timeoutMs: number, extend?: (pages: number) => number): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      if (this.closed) return reject(closing());
      this.waiting.push({ job, timeoutMs, extend, resolve: resolve as (v: unknown) => void, reject });
      this.pump();
    });
  }

  private pump() {
    while (!this.closed && this.running.size < this.workers && this.waiting.length > 0) this.start(this.waiting.shift()!);
  }

  private start(p: Pending) {
    const worker = new Worker(this.entry, { resourceLimits: { maxOldGenerationSizeMb: 384 } });
    this.running.add(worker);
    const started = Date.now();
    let settled = false;
    const free = () => {
      if (this.running.delete(worker)) this.pump();
    };
    const settle = (f: () => void, timedOut = false) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      f();
      // Never reused. An answered job's slot is free at once; a timed-out thread may still be inside
      // a native drawing call (terminate() takes effect when it returns), so its slot is free only
      // when it has gone: live threads never exceed the workers.
      void worker.terminate();
      if (!timedOut) free();
    };
    const expire = () => settle(() => p.reject(new PdfError("The job took too long and was stopped.", "timeout")), true);
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
    worker.on("exit", () => {
      settle(() => p.reject(new PdfError(FAILED, "failed")));
      free();
    });
    const bytes = (p.job as { bytes?: Uint8Array }).bytes;
    const own = bytes && bytes.byteOffset === 0 && bytes.byteLength === bytes.buffer.byteLength && bytes.buffer instanceof ArrayBuffer;
    worker.postMessage(p.job, own ? [bytes.buffer as ArrayBuffer] : []);
  }
}
