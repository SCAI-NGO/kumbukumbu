// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { api, call } from "./client";
import type { components } from "./schema";

export type TagCount = components["schemas"]["TagCountView"];
export type TagLine = components["schemas"]["TagLineView"];

/** The tags of every vault the user has, most notes first: the search sheet's tag list. */
export const useTags = (enabled = true) =>
  useQuery({
    queryKey: ["tags"],
    queryFn: ({ signal }) => call(api.GET("/api/tags", { signal })),
    enabled,
  });

/** The lines where a tag (or one nested below it) stands, by path; an unknown tag answers 404 with a sentence. */
export const useTagNotes = (tag: string | null) =>
  useQuery({
    queryKey: ["tags", "notes", tag],
    queryFn: ({ signal }) => call(api.GET("/api/tags/notes", { params: { query: { tag: tag! } }, signal })),
    enabled: tag !== null,
  });
