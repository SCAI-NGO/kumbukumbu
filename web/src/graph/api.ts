// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery, useQueryClient } from "@tanstack/react-query";
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

// The ETag of each local graph's last answer: the server answers 304 while no link or name has changed, and the
// answer on screen stays. Only the tag lives here; the answer itself is the query cache's, so a query removed (as
// "hide again" removes a discreet vault's) leaves nothing behind and is asked for in full.
const etags = new Map<string, string>();

/**
 * The local graph of the note or attachment at `path` (from the root), `null` while none is open. Asked only while
 * `enabled` (the column is shown); the last answer stays meanwhile, so the column shows it at once when expanded.
 */
export function useLocalGraph(path: string | null, filters: GraphFilters, enabled = true) {
  const queryClient = useQueryClient();
  return useQuery<LocalGraphView>({
    queryKey: ["graph", "local", path, filters],
    enabled: enabled && path !== null,
    placeholderData: (previous) => previous,
    queryFn: async ({ queryKey }) => {
      const key = JSON.stringify(queryKey);
      const known = queryClient.getQueryData<LocalGraphView>(queryKey);
      const etag = known ? etags.get(key) : undefined;
      const result = await api.GET("/api/graph/local", {
        params: {
          query: { path: path!, depth: filters.depth, dir: "both", cap: capFor(filters.depth), days: filters.days, attachments: filters.attachments, ghosts: filters.ghosts },
        },
        headers: etag ? { "If-None-Match": etag } : {},
      });
      // call() throws on anything but 2xx, and a 304 is the answer we already have.
      if (result.response.status === 304 && etag) return known!;
      const data = await call(Promise.resolve(result));
      const fresh = result.response.headers.get("ETag");
      if (fresh) etags.set(key, fresh);
      else etags.delete(key);
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
 * missing one, and `"other-vault"` for two vaults, as links never cross them. Asked only while `enabled`.
 */
export function useGraphPaths(from: string | null, to: string | null, filters: GraphFilters, enabled = true) {
  return useQuery<GraphPathsView | "other-vault" | null>({
    queryKey: ["graph", "paths", from, to, filters],
    enabled,
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
