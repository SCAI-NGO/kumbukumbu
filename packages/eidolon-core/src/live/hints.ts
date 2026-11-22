// SPDX-License-Identifier: AGPL-3.0-or-later
// The "vault changed" hints over a WebSocket, with backoff, a polling fallback and the hourly full check (spec §6).
import { AuthError, OfflineError, type Clock, type ConnectionMode } from "../engine/ports";
import { CLOSE_KEY_REVOKED, type HintMessage } from "../api/types";
import { trimServer } from "../api/client";

export interface SocketLike {
  onopen: (() => void) | null;
  onmessage: ((e: { data: string }) => void) | null;
  onclose: ((e: { code: number }) => void) | null;
  onerror: (() => void) | null;
  close(): void;
}

export interface HintOptions {
  ticket: () => Promise<string>;
  wsUrl: (ticket: string) => string;
  open: (url: string) => SocketLike;
  vault: string;
  clock: Clock;
  random: () => number;
  check: (full: boolean) => void;
  onMode: (m: ConnectionMode) => void;
}

const FIRST_DELAY_MS = 1000;
const MAX_DELAY_MS = 5 * 60_000;
const POLL_AFTER_FAILURES = 3;
const POLL_MS = 2 * 60_000;
const FULL_CHECK_MS = 60 * 60_000;

const STABLE_MS = 30_000;

/** https://host → wss://host/api/ws?ticket=…, http → ws (any case: the URL rule accepts "HTTPS://"). */
export function wsUrlFor(server: string, ticket: string): string {
  const url = new URL(trimServer(server));
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  url.pathname = `${url.pathname.replace(/\/+$/, "")}/api/ws`;
  url.search = `?ticket=${encodeURIComponent(ticket)}`;
  return url.toString();
}

export class HintConnection {
  private socket: SocketLike | null = null;
  private failures = 0;
  private stopped = true;
  private reconnectTimer: unknown = null;
  private pollTimer: unknown = null;
  private hourlyTimer: unknown = null;
  private stableTimer: unknown = null;

  constructor(private readonly o: HintOptions) {}

  start(): void {
    if (!this.stopped) return;
    this.stopped = false;
    this.scheduleHourly();
    void this.connect();
  }

  stop(): void {
    this.stopped = true;
    const socket = this.socket;
    this.socket = null;
    socket?.close();
    for (const t of [this.reconnectTimer, this.pollTimer, this.hourlyTimer, this.stableTimer]) if (t !== null) this.o.clock.clearTimeout(t);
    this.reconnectTimer = this.pollTimer = this.hourlyTimer = this.stableTimer = null;
  }

  private async connect(): Promise<void> {
    this.reconnectTimer = null;
    if (this.stopped) return;
    let ticket: string;
    try {
      ticket = await this.o.ticket();
    } catch (e) {
      if (this.stopped) return;
      if (e instanceof AuthError) {
        this.authFailed();
        return;
      }
      this.failed(e instanceof OfflineError);
      return;
    }
    if (this.stopped) return;

    let socket: SocketLike;
    try {
      socket = this.o.open(this.o.wsUrl(ticket));
    } catch {
      this.failed(false);
      return;
    }
    this.socket = socket;
    socket.onopen = () => {
      if (socket !== this.socket) return;
      // Only a connection that stays up resets the backoff, so one that closes at once can't loop fast.
      this.stableTimer = this.o.clock.setTimeout(() => {
        this.stableTimer = null;
        this.failures = 0;
      }, STABLE_MS);
      this.stopPolling();
      this.o.onMode("live");
      // Whatever changed while there was no connection.
      this.o.check(false);
    };
    socket.onmessage = (e) => {
      if (socket !== this.socket) return;
      let message: Partial<HintMessage>;
      try {
        message = JSON.parse(e.data) as Partial<HintMessage>;
      } catch {
        return;
      }
      if (message.type === "changed" && message.vault === this.o.vault) this.o.check(false);
    };
    socket.onerror = () => undefined;
    socket.onclose = (e) => {
      if (socket !== this.socket) return;
      this.socket = null;
      if (this.stableTimer !== null) this.o.clock.clearTimeout(this.stableTimer);
      this.stableTimer = null;
      if (this.stopped) return;
      if (e.code === CLOSE_KEY_REVOKED) {
        this.authFailed();
        return;
      }
      this.failed(false);
    };
  }

  /**
   * The engine's own next call meets the same 401 and reports it (and the plugin stops the hints). If that call
   * succeeds instead, the 401 was passing: try again at the polling pace.
   */
  private authFailed(): void {
    this.o.check(false);
    this.reconnectTimer = this.o.clock.setTimeout(() => void this.connect(), POLL_MS);
  }

  private failed(offline: boolean): void {
    this.failures++;
    if (this.failures >= POLL_AFTER_FAILURES) this.startPolling();
    if (offline) this.o.onMode("offline");
    else if (this.failures >= POLL_AFTER_FAILURES) this.o.onMode("polling");
    const delay = Math.min(MAX_DELAY_MS, FIRST_DELAY_MS * 2 ** (this.failures - 1));
    // Equal jitter: spread out, but never sooner than half the delay.
    this.reconnectTimer = this.o.clock.setTimeout(() => void this.connect(), delay / 2 + (this.o.random() * delay) / 2);
  }

  private startPolling(): void {
    if (this.pollTimer !== null) return;
    const poll = () => {
      this.pollTimer = this.o.clock.setTimeout(() => {
        this.o.check(false);
        poll();
      }, POLL_MS);
    };
    poll();
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) this.o.clock.clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }

  private scheduleHourly(): void {
    this.hourlyTimer = this.o.clock.setTimeout(() => {
      this.o.check(true);
      this.scheduleHourly();
    }, FULL_CHECK_MS);
  }
}
