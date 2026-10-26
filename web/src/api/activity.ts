// SPDX-License-Identifier: AGPL-3.0-or-later
import { useInfiniteQuery } from "@tanstack/react-query";
import { api, call } from "./client";
import type { components } from "./schema";

export type ActivityRow = components["schemas"]["ActivityRowView"];
export type ActivityFile = components["schemas"]["ActivityFileView"];

/**
 * The vault's changesets, newest first, a page at a time (sessions only): the next page asks for the rows before the
 * last row's end, while the server says there are more.
 */
export const useActivity = (vault: string) =>
  useInfiniteQuery({
    queryKey: ["activity", vault],
    queryFn: ({ pageParam }) =>
      call(api.GET("/api/vaults/{vault}/activity", { params: { path: { vault }, query: pageParam ? { before: pageParam } : {} } })),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => (last.more && last.rows.length > 0 ? last.rows[last.rows.length - 1].to : undefined),
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
