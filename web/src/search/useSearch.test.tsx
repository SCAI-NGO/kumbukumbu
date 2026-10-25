// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "../api/client";
import { useSearch } from "./useSearch";

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("useSearch", () => {
  it("passes an AbortSignal and aborts the request of a query it moved on from", async () => {
    vi.useFakeTimers();
    const signals: AbortSignal[] = [];
    // jsdom's AbortSignal doesn't reach a Request in this environment, so the call itself is observed.
    vi.spyOn(api, "GET").mockImplementation(((_: string, init: { signal: AbortSignal }) => {
      signals.push(init.signal);
      return new Promise(() => {});
    }) as never);
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>{children}</QueryClientProvider>
    );
    const { rerender, result } = renderHook(({ q }) => useSearch(q, "hybrid"), { wrapper, initialProps: { q: "old" } });
    expect(result.current.waiting).toBe(true);
    await act(() => vi.advanceTimersByTimeAsync(200));
    expect(signals).toHaveLength(1);
    expect(signals[0].aborted).toBe(false);

    rerender({ q: "new" });
    await act(() => vi.advanceTimersByTimeAsync(200));
    expect(signals).toHaveLength(2);
    expect(signals[0].aborted).toBe(true);
    expect(signals[1].aborted).toBe(false);
  });
});
