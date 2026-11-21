// SPDX-License-Identifier: AGPL-3.0-or-later

// Runs each job in a fresh worker thread with a heap limit and a time limit, so one hostile file
// can neither stall the others nor leave state behind for the next. Memory outside the heaps
// (decoded images, canvases) is bounded only by the container's limit, which kills the whole
// process, jobs under way included. A bounded queue holds the rest; past it a job is refused at once.
// A job that draws runs in a child process instead: Skia draws an SVG, and rasterises a PDF page or region
// inside its first encode, in one native call, which terminate() can't interrupt (a few filters, a fan of
// <use> or a page of translucent fills take minutes), and a process can be killed at its deadline.

import { fork } from "node:child_process";
import { fileURLToPath } from "node:url";
import { Worker } from "node:worker_threads";
import type { Crop, Target } from "./steps.js";
import { PdfError } from "./pdfjs.js";

export type Job =
  | { kind: "analyse"; bytes: Uint8Array; maxPages: number }
  | { kind: "page"; bytes: Uint8Array; page: number; target: Target; crop?: Crop; rotate: number }
  | { kind: "image"; bytes: Uint8Array; target: Target; crop?: Crop; rotate: number; frame?: number }
  | { kind: "svg"; bytes: Uint8Array; target: Target; size?: { width: number; height: number }; crop?: Crop; rotate: number }
  | { kind: "boxes"; bytes: Uint8Array; pages: number[] }
  | { kind: "list"; bytes: Uint8Array; page: number }
  | { kind: "extract"; bytes: Uint8Array; page: number; n: number }
  | { kind: "region"; bytes: Uint8Array; page: number; crop: Crop; rotate: number; format: "png" | "jpeg" };

// What a worker posts: its page count as soon as it knows it, then one answer.
export type WorkerMessage =
  | { type: "pages"; pages: number }
  | { type: "done"; value: unknown }
  | { type: "error"; code: string; message: string };

const FAILED = "The job failed.";

type Pending = { job: Job; timeoutMs: number; extend?: (pages: number) => number; resolve: (v: unknown) => void; reject: (e: unknown) => void; leave?: () => void };

/** A worker thread or, for a job that draws, a child process: the pool's one view of both. */
type Runner = {
  on(event: "message", f: (m: WorkerMessage) => void): void;
  on(event: "error" | "exit", f: () => void): void;
  post(job: Job, transfer: ArrayBuffer[]): void;
  stop(): Promise<unknown>;
};

const HEAP_MB = 384;

/** The jobs that draw with Skia, run in a child process; reading a PDF's text, boxes or images stays in a thread. */
const DRAWS: ReadonlySet<Job["kind"]> = new Set(["svg", "page", "region", "extract", "image"]);

function thread(entry: URL): Runner {
  const worker = new Worker(entry, { resourceLimits: { maxOldGenerationSizeMb: HEAP_MB } });
  return { on: (event: string, f: (...args: any[]) => void) => void worker.on(event, f), post: (job, transfer) => worker.postMessage(job, transfer), stop: () => worker.terminate() };
}

// SIGKILL ends it at once, inside a native call too; it prints nothing, as what it says may quote the file.
function child(entry: URL): Runner {
  const proc = fork(fileURLToPath(entry), [], { serialization: "advanced", execArgv: [`--max-old-space-size=${HEAP_MB}`], stdio: ["ignore", "ignore", "ignore", "ipc"] });
  const exited = new Promise((resolve) => proc.once("exit", resolve));
  return {
    on: (event: string, f: (...args: any[]) => void) => void proc.on(event, f),
    post: (job) => void proc.send(job),
    stop: () => (proc.exitCode === null && proc.signalCode === null && proc.kill("SIGKILL"), exited),
  };
}

const busy = () => new PdfError("The sidecar is busy; try again shortly.", "busy");
const closing = () => new PdfError("The sidecar is shutting down.", "failed");
const gone = () => new PdfError("The request was closed before its job began.", "gone");

/** A place in the pool held for a job whose input is still arriving. */
export type Reservation = {
  /** Runs the job in the reserved place (see Pool.run). */
  run<T>(job: Job, timeoutMs: number, extend?: (pages: number) => number): Promise<T>;
  /** Gives the place back unused; nothing once run. */
  release(): void;
};

/**
 * Places for running jobs shared by several pools, so that together they run at most limit jobs at a
 * time: the container's memory is budgeted for that many. A freed place is offered to the pools in turn.
 */
export class Gate {
  private used = 0;
  private next = 0;
  private readonly pools: (() => void)[] = [];

  constructor(readonly limit: number) {}

  listen(pump: () => void) {
    this.pools.push(pump);
  }

  take(): boolean {
    if (this.used >= this.limit) return false;
    this.used++;
    return true;
  }

  give() {
    this.used--;
    const n = this.pools.length;
    const first = this.next;
    this.next = (first + 1) % n;
    for (let i = 0; i < n && this.used < this.limit; i++) this.pools[(first + i) % n]!();
  }
}

export class Pool {
  private readonly workers: number;
  private readonly queue: number;
  private readonly entry: URL;
  private readonly gate: Gate | undefined;
  private readonly waiting: Pending[] = [];
  private readonly running = new Set<Runner>();
  private reserved = 0;
  private closed = false;

  /** gate, shared with other pools, limits the jobs they run together; a job waits in its queue for a place. */
  constructor(o: { workers: number; queue: number; entry?: URL; gate?: Gate }) {
    this.workers = o.workers;
    this.queue = o.queue;
    this.entry = o.entry ?? new URL("./worker.js", import.meta.url);
    this.gate = o.gate;
    this.gate?.listen(() => this.pump());
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

  /**
   * Holds a place for a job, or throws busy: lets the server refuse before it reads a body. A job run
   * in it is dropped while it waits once signal aborts (its client has gone), rejected with gone.
   */
  reserve(signal?: AbortSignal): Reservation {
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
        return this.enqueue<T>(job, timeoutMs, extend, signal);
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
  run<T>(job: Job, timeoutMs: number, extend?: (pages: number) => number, signal?: AbortSignal): Promise<T> {
    try {
      return this.reserve(signal).run<T>(job, timeoutMs, extend);
    } catch (e) {
      return Promise.reject(e);
    }
  }

  async close(): Promise<void> {
    this.closed = true;
    for (const p of this.waiting.splice(0)) p.reject(closing());
    await Promise.all([...this.running].map((w) => w.stop()));
  }

  private enqueue<T>(job: Job, timeoutMs: number, extend?: (pages: number) => number, signal?: AbortSignal): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      if (this.closed) return reject(closing());
      if (signal?.aborted) return reject(gone());
      const p: Pending = { job, timeoutMs, extend, resolve: resolve as (v: unknown) => void, reject };
      if (signal) {
        const drop = () => {
          const at = this.waiting.indexOf(p);
          if (at < 0) return;
          this.waiting.splice(at, 1);
          reject(gone());
        };
        signal.addEventListener("abort", drop, { once: true });
        p.leave = () => signal.removeEventListener("abort", drop);
      }
      this.waiting.push(p);
      this.pump();
    });
  }

  private pump() {
    while (!this.closed && this.running.size < this.workers && this.waiting.length > 0 && (this.gate?.take() ?? true)) this.start(this.waiting.shift()!);
  }

  private start(p: Pending) {
    p.leave?.();
    const worker = DRAWS.has(p.job.kind) ? child(this.entry) : thread(this.entry);
    this.running.add(worker);
    const started = Date.now();
    let settled = false;
    const free = () => {
      if (!this.running.delete(worker)) return;
      this.gate?.give();
      this.pump();
    };
    const settle = (f: () => void, timedOut = false) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      f();
      // Never reused. An answered job's slot is free at once; a timed-out thread may still be inside
      // a native drawing call (terminate() takes effect when it returns), so its slot is free only
      // when it has gone: live threads never exceed the workers. A child process goes at once.
      void worker.stop();
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
    worker.post(p.job, own ? [bytes.buffer as ArrayBuffer] : []);
  }
}
