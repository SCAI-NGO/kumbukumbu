// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { api, call } from "../api/client";
import { vaultOf } from "../api/paths";
import type { components } from "../api/schema";

export type GraphNodeView = components["schemas"]["GraphNodeView"];
export type LocalGraphView = components["schemas"]["LocalGraphView"];
export type GraphNodeDetailView = components["schemas"]["GraphNodeDetailView"];
export type GraphPathsView = components["schemas"]["GraphPathsView"];

/** What the graph shows: how far the walk goes, and which kinds of node besides notes. */
export type GraphFilters = { depth: 1 | 2 | 3; days: boolean; attachments: boolean; ghosts: boolean };

/** The hub cap of a depth: the deeper the walk, the fewer neighbours each node keeps, so the column stays readable. */
export const capFor = (depth: 1 | 2 | 3): number => ({ 1: 20, 2: 8, 3: 5 })[depth];

// The last answer of each local graph with its ETag: the server answers 304 while no link or name has changed,
// and the answer on screen stays. Kept per query key, beyond the query's own life, so a graph visited again costs a 304.
const answers = new Map<string, { etag: string; data: LocalGraphView }>();

/** The local graph of the note or attachment at `path` (from the root), `null` while none is open. */
export function useLocalGraph(path: string | null, filters: GraphFilters) {
  return useQuery<LocalGraphView>({
    queryKey: ["graph", "local", path, filters],
    enabled: path !== null,
    placeholderData: (previous) => previous,
    queryFn: async ({ queryKey }) => {
      const key = JSON.stringify(queryKey);
      const known = answers.get(key);
      const result = await api.GET("/api/graph/local", {
        params: {
          query: { path: path!, depth: filters.depth, dir: "both", cap: capFor(filters.depth), days: filters.days, attachments: filters.attachments, ghosts: filters.ghosts },
        },
        headers: known ? { "If-None-Match": known.etag } : {},
      });
      // call() throws on anything but 2xx, and a 304 is the answer we already have.
      if (result.response.status === 304 && known) return known.data;
      const data = await call(Promise.resolve(result));
      const etag = result.response.headers.get("ETag");
      if (etag) answers.set(key, { etag, data });
      return data;
    },
  });
}

/** The times and first paragraph of one node, for its card; asked only while `enabled` (the card is open). */
export function useGraphNode(path: string | null, enabled: boolean) {
  return useQuery({
    queryKey: ["graph", "node", path],
    enabled: enabled && path !== null,
    staleTime: 60_000,
    queryFn: () => call(api.GET("/api/graph/node", { params: { query: { path: path! } } })),
  });
}

/**
 * The shortest paths between two notes. No request where the answer is known: none for the same note twice or a
 * missing one, and `"other-vault"` for two vaults, as links never cross them.
 */
export function useGraphPaths(from: string | null, to: string | null, filters: GraphFilters) {
  return useQuery<GraphPathsView | "other-vault" | null>({
    queryKey: ["graph", "paths", from, to, filters],
    queryFn: () => {
      if (from === null || to === null || from === to) return null;
      if (vaultOf(from) !== vaultOf(to)) return "other-vault";
      return call(
        api.GET("/api/graph/paths", {
          params: { query: { from, to, dir: "both", days: filters.days, attachments: filters.attachments, ghosts: filters.ghosts } },
        }),
      );
    },
  });
}
