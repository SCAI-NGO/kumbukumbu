// SPDX-License-Identifier: AGPL-3.0-or-later
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { api, call } from "./client";
import { inVault, vaultOf } from "./paths";
import type { components } from "./schema";

export type ActivityRow = components["schemas"]["ActivityRowView"];
export type ActivityFile = components["schemas"]["ActivityFileView"];

/**
 * The vault's changesets, newest first, a page at a time (sessions only): the next page asks for the rows after the
 * last one shown (its end and its key, as rows can end at the same instant), while the server says there are more.
 */
export const useActivity = (vault: string) =>
  useInfiniteQuery({
    queryKey: ["activity", vault],
    queryFn: ({ pageParam }) => call(api.GET("/api/vaults/{vault}/activity", { params: { path: { vault }, query: pageParam ?? {} } })),
    initialPageParam: null as { before: string; key: string } | null,
    getNextPageParam: (last) => {
      const end = last.rows.at(-1);
      return last.more && end ? { before: end.to, key: end.key } : undefined;
    },
  });

/**
 * A note's text now, read once each time a comparison asks for it (not again on refocus or reconnect), and kept
 * apart from the open note's cache: the open note never changes under the reader, and a comparison opened later
 * never shows an old "now".
 */
export const useNoteNow = (path: string, enabled: boolean) =>
  useQuery({
    queryKey: ["activity", "now", path],
    queryFn: () => call(api.GET("/api/notes/{vault}/{path}", { params: { path: { vault: vaultOf(path), path: inVault(path) } } })),
    enabled,
    gcTime: 0,
    staleTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });

/**
 * Who wrote, for a person: "You", "You in Obsidian (Laptop)" (the device key's name after "Obsidian "), "Agent liz",
 * or the writer's name as it is (the server, or a key that no longer exists).
 */
export function writerText({ writer, writerKind }: Pick<ActivityRow, "writer" | "writerKind">): string {
  switch (writerKind) {
    case "user":
      return "You";
    case "device": {
      const at = writer.lastIndexOf("Obsidian ");
      return `You in Obsidian (${at >= 0 ? writer.slice(at + "Obsidian ".length) : writer})`;
    }
    case "agent":
      return `Agent ${writer}`;
    default:
      return writer;
  }
}

const kinds = ["changed", "added", "renamed", "deleted"] as const;

/** "3 changed · 1 added · 2 deleted": the row's files by kind, only the kinds it has. */
export const countsText = (files: Pick<ActivityFile, "kind">[]) =>
  kinds
    .map((kind) => [kind, files.filter((f) => f.kind === kind).length] as const)
    .filter(([, n]) => n > 0)
    .map(([kind, n]) => `${n} ${kind}`)
    .join(" · ");
