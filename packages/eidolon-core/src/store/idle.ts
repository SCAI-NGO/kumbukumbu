// SPDX-License-Identifier: AGPL-3.0-or-later
// An unlocked copy ends like a session (offline mode design §3a): after the idle limit without use, `onIdle` runs,
// which writes unsaved editor text into the copy and locks it. A key in memory without a limit would leave a tab
// open forever, the laptop left open on a train being exactly what the sealing is for.
import type { Clock } from "../engine/ports";

export class IdleLock {
  private last: number;
  private timer: unknown = null;

  constructor(private readonly clock: Clock, private readonly idleMs: number, private readonly onIdle: () => void) {
    this.last = clock.now();
    this.arm(idleMs);
  }

  /** The person did something: the limit starts again. Cheap, so it can be called on every keystroke. */
  touch(): void {
    this.last = this.clock.now();
  }

  stop(): void {
    if (this.timer !== null) this.clock.clearTimeout(this.timer);
    this.timer = null;
  }

  private arm(ms: number): void {
    this.timer = this.clock.setTimeout(() => {
      const left = this.last + this.idleMs - this.clock.now();
      if (left > 0) return this.arm(left);
      this.timer = null;
      this.onIdle();
    }, ms);
  }
}
