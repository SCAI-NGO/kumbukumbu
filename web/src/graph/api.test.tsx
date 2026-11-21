// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { vaultHinted } from "../api/history";
import { fakeApi, json } from "../test/app";
import { capFor, useGraphPaths, useLocalGraph, type GraphFilters, type LocalGraphView } from "./api";

const filters: GraphFilters = { depth: 2, days: false, attachments: true, ghosts: true };
const graph: LocalGraphView = {
  nodes: [{ path: "/Work/A.md", title: "A", kind: "note", tags: [], archived: false, in: 0, out: 0, depth: 0 }],
  edges: [],
  leftOut: {},
};

function setup<T>(hook: () => T) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  return { queryClient, ...renderHook(hook, { wrapper }) };
}

afterEach(() => vi.unstubAllGlobals());

describe("capFor", () => {
  it("shrinks the hub cap as the walk deepens", () => {
    expect([1, 2, 3].map((d) => capFor(d as 1 | 2 | 3))).toEqual([20, 8, 5]);
  });
});

describe("useLocalGraph", () => {
  it("asks for the note's neighbourhood with the filters and the cap of its depth", async () => {
    const fetch = fakeApi({ "GET /api/graph/local": () => json(200, graph) });
    const { result } = setup(() => useLocalGraph("/Work/A.md", filters));
    await waitFor(() => expect(result.current.data).toEqual(graph));
    const url = new URL(fetch.mock.calls[0][0].url);
    expect(url.pathname + url.search).toBe("/api/graph/local?path=%2FWork%2FA.md&depth=2&dir=both&cap=8&days=false&attachments=true&ghosts=true");
  });

  it("asks nothing without a note", () => {
    const fetch = fakeApi({});
    setup(() => useLocalGraph(null, filters));
    expect(fetch).not.toHaveBeenCalled();
  });

  it("sends the last ETag and keeps the data when the server answers 304", async () => {
    let calls = 0;
    const fetch = fakeApi({
      "GET /api/graph/local": () => (++calls === 1 ? new Response(JSON.stringify(graph), { status: 200, headers: { "Content-Type": "application/json", ETag: '"v1"' } }) : new Response(null, { status: 304 })),
    });
    const { result } = setup(() => useLocalGraph("/Work/B.md", filters));
    await waitFor(() => expect(result.current.data).toEqual(graph));
    await act(() => result.current.refetch());
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(fetch.mock.calls[0][0].headers.get("If-None-Match")).toBeNull();
    expect(fetch.mock.calls[1][0].headers.get("If-None-Match")).toBe('"v1"');
    expect(result.current.data).toEqual(graph);
    expect(result.current.isError).toBe(false);
  });
});

describe("useGraphPaths", () => {
  it.each([
    ["the same note twice", "/Work/A.md", "/Work/A.md", null],
    ["no second note", "/Work/A.md", null, null],
    ["notes of two vaults", "/Work/A.md", "/Ideas/B.md", "other-vault"],
  ])("answers without a request for %s", async (_, from, to, expected) => {
    const fetch = fakeApi({});
    const { result } = setup(() => useGraphPaths(from, to, filters));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBe(expected);
    expect(fetch).not.toHaveBeenCalled();
  });
});

describe("vaultHinted", () => {
  it("asks the vault's local graphs again and no other vault's", () => {
    const { queryClient } = setup(() => null);
    queryClient.setQueryData(["graph", "local", "/Work/A.md", filters], graph);
    queryClient.setQueryData(["graph", "local", "/Ideas/B.md", filters], graph);
    vaultHinted(queryClient, "Work");
    expect(queryClient.getQueryState(["graph", "local", "/Work/A.md", filters])?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(["graph", "local", "/Ideas/B.md", filters])?.isInvalidated).toBe(false);
  });
});
