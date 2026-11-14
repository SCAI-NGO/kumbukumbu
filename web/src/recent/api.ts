// SPDX-License-Identifier: AGPL-3.0-or-later
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { api, call } from "../api/client";
import type { FlagFilter } from "../shell/treeFilter";

// Recent changes' JSON, as `RecentApi` answers it. Kept here until the generated schema has it: then each becomes
// `components["schemas"][…]` and `untyped` below goes.

/**
 * A note or attachment as it is now: `title` (a note's name without `.md`, an attachment's whole name), `folder` where
 * it lies with its vault (`Work/Pump`), `writer` and `writerKind` its last writer as Activity shows them.
 */
export type RecentItem = {
  path: string;
  vault: string;
  title: string;
  folder: string;
  kind: "note" | "image" | "pdf" | "text";
  flag: string | null;
  writer: string;
  writerKind: string | null;
  writtenAt: string;
};
/** The newest first; `more` when there are further ones; `vaults` every vault the session shows, whatever is skipped. */
export type RecentView = { items: RecentItem[]; more: boolean; vaults: string[] };

/** The words (each must be in the path with its vault), a flag chip, the vaults switched off, and how many. */
export type RecentFilter = { words: string; flag: FlagFilter | null; skip: string[]; limit: number };

type Init = { params: { query: Record<string, string | string[]> } };
const untyped = api as unknown as { GET<T>(url: string, init: Init): Promise<{ data?: T; error?: unknown; response: Response }> };

/**
 * Recent changes over every vault the session shows (spec §4); a hidden discreet vault is never in the answer. While
 * the filter changes, the rows shown stay until the next ones come, rather than blinking to Loading.
 */
export function useRecent({ words, flag, skip, limit }: RecentFilter) {
  const query: Record<string, string | string[]> = { limit: String(limit) };
  if (words.trim()) query.words = words.trim();
  if (flag) query.flag = flag;
  if (skip.length) query.skip = skip;
  return useQuery({
    queryKey: ["recent", words.trim(), flag, skip, limit],
    queryFn: () => call(untyped.GET<RecentView>("/api/recent", { params: { query } })),
    placeholderData: keepPreviousData,
    retry: false,
  });
}
