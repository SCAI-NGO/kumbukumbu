// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMemo } from "react";
import { titleOf } from "../api/paths";
import { useSimilarNotes } from "../api/similar";
import type { Similarity } from "./similarity";

export type Suggestion = { path: string; title: string; similarity: Similarity };

/**
 * The notes `path` might link to and the graph doesn't show: the server's suggestions (link prediction spec §4), the
 * same as the Links tab's "Similar notes", asked only while `enabled` (the layer switched on).
 */
export function useSuggestedLinks(path: string, shown: ReadonlySet<string>, enabled: boolean): Suggestion[] {
  const similar = useSimilarNotes(path, enabled);
  return useMemo(
    () =>
      enabled
        ? (similar.data?.notes ?? [])
            .filter((n) => n.path !== path && !shown.has(n.path))
            .map((n) => ({ path: n.path, title: titleOf(n.path), similarity: { score: n.score, measured: true } }))
        : [],
    [enabled, similar.data, path, shown],
  );
}
