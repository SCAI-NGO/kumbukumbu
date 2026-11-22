// SPDX-License-Identifier: AGPL-3.0-or-later
// One mode for every tab (offline mode design §3a: never two different numbers). The tab that holds the mode's lock
// runs the ModeMachine; the others send it what they see and show the state it posts. When that tab closes, another
// takes the lock, starts from the last state it heard and asks the tabs for their sockets again.
import type { Clock } from "../engine/ports";
import type { TabLocks } from "../store/ports";
import { initialMode, ModeMachine, type ModeInput, type ModeState } from "./mode";

export type ModeMessage =
  | { type: "input"; input: ModeInput }
  | { type: "state"; state: ModeState }
  /** A new tab wants the current state. */
  | { type: "ask" }
  /** A new lead tab wants every tab's socket. */
  | { type: "report" };

/** A BroadcastChannel: a message reaches every other tab, never its sender. */
export interface ModeChannel {
  post(message: ModeMessage): void;
  listen(fn: (message: ModeMessage) => void): () => void;
}

export interface ModePorts { locks: TabLocks; channel: ModeChannel; clock: Clock; }

const LOCK = "kumbu-mode";

export class SharedMode {
  private machine: ModeMachine | null = null;
  private last: ModeState = initialMode;
  private socketUp = false;
  private readonly held: { acquired: Promise<void>; release(): void };
  private readonly stopListening: () => void;
  private stopped = false;

  constructor(private readonly p: ModePorts, private readonly tab: string, private readonly onState: (s: ModeState) => void) {
    this.stopListening = p.channel.listen((m) => this.heard(m));
    this.held = p.locks.hold(LOCK);
    void this.held.acquired.then(() => this.lead());
    p.channel.post({ type: "ask" });
  }

  get state(): ModeState { return this.last; }

  /** What this tab saw; the lead tab decides. */
  send(input: ModeInput): void {
    if (this.stopped) return;
    if (input.type === "socket") this.socketUp = input.up;
    if (this.machine !== null) this.machine.apply(input);
    else this.p.channel.post({ type: "input", input });
  }

  /** The tab closes: its socket no longer counts, and the lock goes to another tab. */
  stop(): void {
    if (this.stopped) return;
    this.send({ type: "gone", tab: this.tab });
    this.stopped = true;
    this.machine?.stop();
    this.machine = null;
    this.stopListening();
    this.held.release();
  }

  private lead(): void {
    if (this.stopped) return;
    this.machine = new ModeMachine(this.p.clock, (s) => this.publish(s), this.last);
    this.machine.apply({ type: "socket", tab: this.tab, up: this.socketUp });
    this.p.channel.post({ type: "report" });
  }

  private publish(state: ModeState): void {
    this.last = state;
    this.p.channel.post({ type: "state", state });
    this.onState(state);
  }

  private heard(m: ModeMessage): void {
    if (this.stopped) return;
    if (m.type === "state") {
      if (this.machine !== null) return;
      this.last = m.state;
      this.onState(m.state);
    } else if (m.type === "input") {
      this.machine?.apply(m.input);
    } else if (m.type === "ask") {
      if (this.machine !== null) this.p.channel.post({ type: "state", state: this.last });
    } else if (this.machine === null) {
      this.p.channel.post({ type: "input", input: { type: "socket", tab: this.tab, up: this.socketUp } });
    }
  }
}
