// SPDX-License-Identifier: AGPL-3.0-or-later
import { act } from "@testing-library/react";

/** A WebSocket that only records: the test opens it, sends frames into it, reads what it sent and drops it. */
export class FakeSocket {
  static all: FakeSocket[] = [];
  onopen: ((e: Event) => void) | null = null;
  onmessage: ((e: MessageEvent) => void) | null = null;
  onclose: ((e: CloseEvent) => void) | null = null;
  readyState = 0;
  closed = false;
  readonly sent: unknown[] = [];
  constructor(readonly url: string) {
    FakeSocket.all.push(this);
  }
  send(data: string) {
    this.sent.push(JSON.parse(data));
  }
  close() {
    this.closed = true;
    this.readyState = 3;
  }
  open() {
    this.readyState = 1;
    act(() => this.onopen?.(new Event("open")));
  }
  frame(body: unknown) {
    act(() => this.onmessage?.(new MessageEvent("message", { data: JSON.stringify(body) })));
  }
  drop(code = 1006) {
    this.readyState = 3;
    act(() => this.onclose?.(new CloseEvent("close", { code })));
  }
}
