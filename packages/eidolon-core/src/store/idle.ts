// SPDX-License-Identifier: AGPL-3.0-or-later
// An unlocked copy ends like a session (offline mode design §3a): after the idle limit without use, `onIdle` runs,
// which writes unsaved editor text into the copy and locks it. A key in memory without a limit would leave a tab
// open forever, the laptop left open on a train being exactly what the sealing is for.
import type { Clock } from "../engine/ports";

export class IdleLock {
  private last: number;
  private timer: unknown = null;
  private done = false;

  constructor(private readonly clock: Clock, private readonly idleMs: number, private readonly onIdle: () => void) {
    this.last = clock.now();
    this.arm(idleMs);
  }

  /**
   * The person did something: the limit starts again. Cheap, so it can be called on every keystroke. Once the limit
   * has passed unseen (no timer runs while a laptop sleeps or a page is frozen), the first use locks instead.
   */
  touch(): void {
    if (this.check()) return;
    this.last = this.clock.now();
  }

  /** Locks now if the limit has passed: for a page that wakes up (visibilitychange, pageshow). True if it did. */
  check(): boolean {
    if (this.done) return true;
    if (this.clock.now() - this.last < this.idleMs) return false;
    this.fire();
    return true;
  }

  stop(): void {
    if (this.timer !== null) this.clock.clearTimeout(this.timer);
    this.timer = null;
  }

  private fire(): void {
    this.stop();
    this.done = true;
    this.onIdle();
  }

  private arm(ms: number): void {
    this.timer = this.clock.setTimeout(() => {
      const left = this.last + this.idleMs - this.clock.now();
      this.timer = null;
      if (left > 0) return this.arm(left);
      this.fire();
    }, ms);
  }
}
