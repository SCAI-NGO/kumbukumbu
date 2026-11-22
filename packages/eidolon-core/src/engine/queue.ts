// SPDX-License-Identifier: AGPL-3.0-or-later
// One queue per vault: every sync operation runs here, one at a time (spec §6, rule 1).

interface Entry { job: () => Promise<void>; key: string | undefined; promise: Promise<void>; settle: (e?: unknown) => void; }

export class SerialQueue {
  private readonly waiting: Entry[] = [];
  private running = false;
  private idleWaiters: (() => void)[] = [];

  /** Runs jobs one at a time in order. A job with a key that is already waiting (not running) is not added again. */
  push(job: () => Promise<void>, key?: string): Promise<void> {
    if (key !== undefined) {
      const queued = this.waiting.find((e) => e.key === key);
      if (queued) return queued.promise;
    }
    let settle!: (e?: unknown) => void;
    const promise = new Promise<void>((resolve, reject) => {
      settle = (e?: unknown) => (e === undefined ? resolve() : reject(e instanceof Error ? e : new Error("The job failed.")));
    });
    // Callers that don't await must not cause an unhandled rejection.
    promise.catch(() => undefined);
    this.waiting.push({ job, key, promise, settle });
    void this.drain();
    return promise;
  }

  get busy(): boolean {
    return this.running || this.waiting.length > 0;
  }

  idle(): Promise<void> {
    if (!this.busy) return Promise.resolve();
    return new Promise((resolve) => this.idleWaiters.push(resolve));
  }

  private async drain(): Promise<void> {
    if (this.running) return;
    this.running = true;
    try {
      for (let entry = this.waiting.shift(); entry; entry = this.waiting.shift()) {
        try {
          await entry.job();
          entry.settle();
        } catch (e) {
          entry.settle(e ?? new Error("job failed"));
        }
      }
    } finally {
      this.running = false;
      const waiters = this.idleWaiters;
      this.idleWaiters = [];
      for (const w of waiters) w();
    }
  }
}
