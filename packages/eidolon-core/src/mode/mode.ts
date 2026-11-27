// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline mode (offline mode design §3, §3a "The bar and the mark"): when the app reads from the copy on this
// device, and when it may go back to the server. Offline only after a failed request with the socket down
// (`navigator.onLine` is at most a hint, which the host may pass as a failure); online only once the socket is back
// and every change made offline has uploaded, so the app never shows the server's older text right after reconnecting.
// A mode, once entered, holds at least HOLD_MS: a connection that switches more often is broken anyway, and the bar
// should not mirror its noise. The hold delays the mode only; the count of changes waiting follows at once.
import type { CameBack, Clock } from "../engine/ports";

export const HOLD_MS = 15_000;

/**
 * A conflict copy the engines made: Kumbu²'s version kept the path, this device's went beside it. With `came`, a note
 * that came back after a trash met an edit, without a copy: `copy` is the note itself.
 */
export interface Conflict { vault: string; original: string; copy: string; came?: CameBack; }

export interface ModeState {
  /** The app reads from the copy on this device: while away, and after it until the uploads are through. */
  offline: boolean;
  /**
   * online; offline: away; uploading: back, changes still uploading; signIn: back, but the session ended with
   * changes waiting, which upload once the person signs in again.
   */
  phase: "online" | "offline" | "uploading" | "signIn";
  /** Changes made here that Kumbu² doesn't have yet, across the copy's vaults. */
  waiting: number;
  /** Back online after uploading this many changes: the done line, until the host or the person puts it away. */
  done: { uploaded: number } | null;
  /** Conflict copies made since the person last looked: they stay until seen (§3a). */
  conflicts: Conflict[];
}

export type ModeInput =
  /** A request couldn't reach Kumbu² (no network, a 502/503/504 from a gateway on the way, not Kumbu²'s own busy answer). */
  | { type: "failed" }
  /** A request reached Kumbu² again after a failure. */
  | { type: "reached" }
  /** A tab's sync socket opened or closed. */
  | { type: "socket"; tab: string; up: boolean }
  /** A tab closed. */
  | { type: "gone"; tab: string }
  | { type: "waiting"; count: number }
  /** A request answered 401: Kumbu² is reachable, the session isn't. */
  | { type: "sessionLost" }
  | { type: "signedIn" }
  | { type: "conflict"; conflict: Conflict }
  /** The done line's time is up, or the person dismissed it, or opened the conflicts. */
  | { type: "seen" }
  /**
   * The copy locked or went, or the session ended or changed hands: what the mode knows of that person (the conflict
   * copies' paths, the done line, the count waiting) is not the next person's to see.
   */
  | { type: "forget" };

export const initialMode: ModeState = { offline: false, phase: "online", waiting: 0, done: null, conflicts: [] };

export class ModeMachine {
  private state: ModeState;
  private readonly sockets = new Set<string>();
  /** A request failed and none has reached Kumbu² since. */
  private failing = false;
  private sessionLost = false;
  /** When the mode last switched; a start counts as none, so an app opened offline is offline at once. */
  private since = -Infinity;
  /** The most changes waiting while offline: what the done line says uploaded. */
  private peak = 0;
  private timer: unknown = null;

  constructor(private readonly clock: Clock, private readonly onState: (s: ModeState) => void, from: ModeState = initialMode) {
    this.state = from;
    this.failing = from.offline;
    this.peak = from.waiting;
  }

  get current(): ModeState { return this.state; }

  apply(input: ModeInput): void {
    switch (input.type) {
      case "failed": this.failing = true; break;
      case "reached": this.failing = false; break;
      case "socket":
        if (input.up) {
          this.sockets.add(input.tab);
          this.failing = false;
        } else {
          this.sockets.delete(input.tab);
        }
        break;
      case "gone": this.sockets.delete(input.tab); break;
      case "waiting":
        this.state = { ...this.state, waiting: input.count };
        if (this.state.offline) this.peak = Math.max(this.peak, input.count);
        break;
      case "sessionLost": this.sessionLost = true; break;
      case "signedIn": this.sessionLost = false; break;
      case "conflict": this.state = { ...this.state, conflicts: [...this.state.conflicts, input.conflict] }; break;
      case "seen": this.state = { ...this.state, done: null, conflicts: [] }; break;
      case "forget":
        this.state = { ...this.state, waiting: 0, done: null, conflicts: [] };
        this.peak = 0;
        break;
    }
    this.evaluate();
  }

  stop(): void {
    if (this.timer !== null) this.clock.clearTimeout(this.timer);
    this.timer = null;
  }

  private evaluate(): void {
    const up = this.sockets.size > 0;
    // A 401 came from Kumbu², so it is reachable; with nothing waiting the host's usual sign-in takes over.
    const reachable = up || this.sessionLost;
    const want = this.state.offline ? !(reachable && this.state.waiting === 0) : this.failing && !up;
    if (want !== this.state.offline) {
      const left = this.since + HOLD_MS - this.clock.now();
      if (left > 0) {
        if (this.timer === null) this.timer = this.clock.setTimeout(() => { this.timer = null; this.evaluate(); }, left);
      } else {
        this.since = this.clock.now();
        if (want) {
          this.peak = this.state.waiting;
          this.state = { ...this.state, offline: true, done: null };
        } else {
          this.state = { ...this.state, offline: false, done: this.peak > 0 ? { uploaded: this.peak } : null };
          this.peak = 0;
        }
      }
    }
    const phase: ModeState["phase"] = !this.state.offline ? "online"
      : this.sessionLost && this.state.waiting > 0 ? "signIn"
      : reachable ? "uploading" : "offline";
    this.state = { ...this.state, phase };
    this.onState(this.state);
  }
}
