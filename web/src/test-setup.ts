import "@testing-library/jest-dom/vitest";

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
