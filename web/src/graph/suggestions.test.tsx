// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import { useSuggestedLinks } from "./suggestions";

afterEach(() => vi.unstubAllGlobals());

const notes = [
  { path: "/Work/b.md", line: 1, text: "B", score: 0.8 },
  { path: "/Work/sub/c.md", line: 4, text: "C", score: 0.6 },
];

function hook(shown: Set<string>, enabled: boolean) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return renderHook(() => useSuggestedLinks("/Work/a.md", shown, enabled), {
    wrapper: ({ children }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
  });
}

describe("useSuggestedLinks", () => {
  it("asks nothing while the layer is off", () => {
    const fetch = fakeApi({ "GET /api/similar/Work/a.md": () => json(200, { state: "ready", notes, pending: 0 }) });
    expect(hook(new Set(), false).result.current).toEqual([]);
    expect(fetch).not.toHaveBeenCalled();
  });

  it("offers the server's suggestions the graph doesn't show, measured", async () => {
    fakeApi({ "GET /api/similar/Work/a.md": () => json(200, { state: "ready", notes, pending: 0 }) });
    const { result } = hook(new Set(["/Work/b.md"]), true);
    await waitFor(() => expect(result.current).toEqual([{ path: "/Work/sub/c.md", title: "c", similarity: { score: 0.6, measured: true } }]));
  });
});
