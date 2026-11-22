import "@testing-library/jest-dom/vitest";
import { afterEach } from "vitest";
import { resetOfflineMode } from "./offline/mode";
import { startOver } from "./shell/lastNote";

// React Router builds a Request with an AbortSignal for every navigation. Under jsdom that signal is jsdom's,
// which Node's own Request refuses; tests never abort a request, so the signal is dropped.
const NodeRequest = globalThis.Request;
globalThis.Request = class extends NodeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    super(input, init?.signal ? { ...init, signal: undefined } : init);
  }
};

// Node 25+ has its own localStorage and sessionStorage globals, which shadow jsdom's (and lack `length` and
// `key` without --localstorage-file); the tests mean the browser's.
const { window: dom } = (globalThis as unknown as { jsdom: { window: Window } }).jsdom;
for (const name of ["localStorage", "sessionStorage"] as const) {
  Object.defineProperty(globalThis, name, { value: dom[name], configurable: true, writable: true });
}

// jsdom's WebSocket would try to reach a server: without one, the shell's sync socket stays down and the version
// poll runs, as in a browser that lost its socket. Tests of the socket stub a fake with vi.stubGlobal, which their
// vi.unstubAllGlobals() takes away again.
Object.defineProperty(globalThis, "WebSocket", { value: undefined, configurable: true, writable: true });

// The last note a test opened would open on start in the next one, and each test is a new page load.
afterEach(() => {
  startOver();
  // A request a test left unanswered would leave the next test offline.
  resetOfflineMode();
  for (const key of Object.keys(localStorage)) if (key.startsWith("kumbu.lastNote.")) localStorage.removeItem(key);
});
