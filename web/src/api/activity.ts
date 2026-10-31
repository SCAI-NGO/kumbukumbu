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

const pairedInfix = " – Obsidian ";

/**
 * The device of a key paired from Obsidian, named "<vault> – Obsidian <device>" ("Work – Obsidian Laptop" is
 * "Laptop", "Work – Obsidian My Obsidian Phone" is "My Obsidian Phone"); the whole label for any other name.
 */
export function deviceOf(writer: string): string {
  const at = writer.indexOf(pairedInfix);
  return at < 0 ? writer : writer.slice(at + pairedInfix.length);
}

/**
 * Who wrote, for a person: "You" (the server's kind `self`, never a name compared here), "You in Obsidian (Laptop)"
 * (the device the key was paired from), "Agent liz", or the writer as the server renders it (the server, another
 * user, or a key that no longer exists).
 */
export function writerText({ writer, writerKind }: Pick<ActivityRow, "writer" | "writerKind">): string {
  switch (writerKind) {
    case "self":
      return "You";
    case "device":
      return `You in Obsidian (${deviceOf(writer)})`;
    case "agent":
      return `Agent ${writer}`;
    default:
      return writer;
  }
}

const kinds = ["changed", "added", "renamed", "deleted"] as const;

/** "3 changed · 1 added · 2 deleted · 2 folders": the row's files by kind, only the kinds it has; folders are counted apart. */
export const countsText = (files: Pick<ActivityFile, "kind" | "fileKind">[]) => {
  const folders = files.filter((f) => f.fileKind === "folder").length;
  const others = files.filter((f) => f.fileKind !== "folder");
  return [
    ...kinds.map((kind) => `${others.filter((f) => f.kind === kind).length} ${kind}`).filter((t) => !t.startsWith("0 ")),
    ...(folders > 0 ? [`${folders} ${folders === 1 ? "folder" : "folders"}`] : []),
  ].join(" · ");
};
