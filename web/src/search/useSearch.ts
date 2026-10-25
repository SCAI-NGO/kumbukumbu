// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { api, call } from "../api/client";
import type { components } from "../api/schema";
import type { SearchMode } from "../state/editor";

export type SearchView = components["schemas"]["SearchView"];
export type SearchResult = components["schemas"]["SearchResultView"];

export const DEBOUNCE_MS = 200;
const RESULTS = 20;

/**
 * Searches all of the user's vaults (no `path`) once the query has rested for 200 ms. The result belongs to the key
 * (query, mode): a newer query never shows an older one's answer, and TanStack Query aborts the request of the
 * key it left through `signal`.
 */
export function useSearch(query: string, mode: SearchMode) {
  const trimmed = query.trim();
  const [settled, setSettled] = useState(trimmed);
  useEffect(() => {
    if (trimmed === settled) return;
    const timer = setTimeout(() => setSettled(trimmed), DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [trimmed, settled]);

  const result = useQuery({
    queryKey: ["search", settled, mode],
    queryFn: ({ signal }) => call(api.GET("/api/search", { params: { query: { q: settled, mode, n: RESULTS } }, signal })),
    enabled: settled !== "",
  });

  return {
    /** Nothing to search for. */
    idle: trimmed === "",
    /** The query is still being typed, or its answer is on its way. */
    waiting: trimmed !== "" && (trimmed !== settled || result.isPending),
    data: trimmed === settled ? result.data : undefined,
    error: trimmed === settled && result.isError ? result.error : null,
  };
}
