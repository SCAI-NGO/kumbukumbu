// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import "fake-indexeddb/auto";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "../api/client";
import { nameVaults, OfflineSource, setOfflineSource } from "../offline/source";
import { offlineCopy } from "../offline/testCopy";
import { setOfflineWrites } from "../offline/writing";
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

describe("useSearch offline", () => {
  afterEach(() => {
    setOfflineSource(null);
    setOfflineWrites(null);
  });

  async function offline() {
    const { mirror, vaults } = await offlineCopy();
    await nameVaults(mirror, [{ id: "v-work", name: "Work" }, { id: "v-team", name: "Team" }]);
    await vaults.Work!.files().writeIf("Pump.md", null, "# Pump\nSee the list.\n");
    await vaults.Work!.files().writeIf("log.md", null, "# Monday\nThe pump was serviced. #maintenance/pumps\n");
    await vaults.Team!.files().writeIf("_archive/old.md", null, "An old pump.\n");
    setOfflineSource(new OfflineSource(mirror));
    const get = vi.spyOn(api, "GET");
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>{children}</QueryClientProvider>
    );
    return { get, wrapper };
  }

  it("answers by words from the copy, whatever the mode, and says so", async () => {
    const { get, wrapper } = await offline();
    const { result } = renderHook(() => useSearch("pump", "meaning"), { wrapper });
    await waitFor(() => expect(result.current.data).toBeDefined());
    expect(result.current.data).toEqual({
      results: [
        { path: "/Work/Pump.md", address: "#Pump", startLine: 1, endLine: 2, snippet: "See the list.", score: null, reason: "title" },
        { path: "/Work/log.md", address: "#Monday", startLine: 1, endLine: 2, snippet: "The pump was serviced. #maintenance/pumps", score: null, reason: "exact" },
      ],
      notIndexed: 0,
      notice: "You're offline: search finds words only. Search by meaning is back when you are.",
    });
    expect(get).not.toHaveBeenCalled();
  });

  it("keeps to a tag and leaves the archive out unless asked, as the server does", async () => {
    const { wrapper } = await offline();
    const tagged = renderHook(() => useSearch("pump", "hybrid", "maintenance"), { wrapper });
    await waitFor(() => expect(tagged.result.current.data).toBeDefined());
    expect(tagged.result.current.data!.results.map((r) => r.path)).toEqual(["/Work/log.md"]);

    const archived = renderHook(() => useSearch("old", "exact", null, true), { wrapper });
    await waitFor(() => expect(archived.result.current.data).toBeDefined());
    expect(archived.result.current.data!.results.map((r) => r.path)).toEqual(["/Team/_archive/old.md"]);

    const unknown = renderHook(() => useSearch("pump", "exact", "nothing"), { wrapper });
    await waitFor(() => expect(unknown.result.current.error).toMatchObject({ status: 404, message: "No note carries #nothing." }));
  });
});
