// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { api, call } from "./client";
import type { NoteView } from "./notes";
import { inVault, vaultOf } from "./paths";
import type { components } from "./schema";

export type TrashEntry = components["schemas"]["TrashEntryView"];
export type CheckpointEntry = components["schemas"]["CheckpointEntryView"];
export type RestorePreview = components["schemas"]["RestorePreviewView"];
export type NoteVersion = components["schemas"]["NoteVersionView"];

const params = (path: string) => ({ path: { vault: vaultOf(path), path: inVault(path) } });

/** Every note of the vault may read differently now: its tree, notes, links, history and search are asked again. */
function vaultChanged(queryClient: QueryClient, vault: string) {
  const inIt = (k: unknown) => typeof k === "string" && (k === vault || k.startsWith(`/${vault}/`));
  for (const key of ["tree", "note", "links", "backlinks", "history", "trash", "checkpoints"]) {
    void queryClient.invalidateQueries({ queryKey: [key], predicate: (q) => q.queryKey.some(inIt) });
  }
  for (const key of ["vaults", "changes", "search"]) void queryClient.invalidateQueries({ queryKey: [key] });
}

/**
 * Restores a note (`restore <path> [version]`): an existing note goes back to `version` of its history, as a new
 * version of its own; a removed one comes back from the trash (the one with `version`, else the latest).
 */
export function useRestoreNote() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ path, version }: { path: string; version?: string }) =>
      call(api.POST("/api/restore/{vault}/{path}", { params: params(path), body: { version: version ?? null } })),
    onSuccess: (note: NoteView) => {
      queryClient.setQueryData(["note", note.path], note);
      vaultChanged(queryClient, vaultOf(note.path));
    },
  });
}

/**
 * The open note's version and last writer, without its text: asked every few seconds while the page is visible,
 * at once when it becomes visible again, and not at all while it is hidden (the query's focus handling).
 */
export const useVersionPoll = (path: string, enabled: boolean, interval = 5_000) =>
  useQuery({
    queryKey: ["version", path],
    queryFn: () => call(api.GET("/api/version/{vault}/{path}", { params: params(path) })),
    enabled,
    refetchInterval: interval,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: "always",
    retry: false,
  });

export const useTrash = (vault: string) =>
  useQuery({ queryKey: ["trash", vault], queryFn: () => call(api.GET("/api/vaults/{vault}/trash", { params: { path: { vault } } })) });

export const useCheckpoints = (vault: string) =>
  useQuery({ queryKey: ["checkpoints", vault], queryFn: () => call(api.GET("/api/vaults/{vault}/checkpoints", { params: { path: { vault } } })) });

/** Names the vault's state as it is now (`mkcp /vault name`). */
export function useCreateCheckpoint(vault: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => call(api.POST("/api/vaults/{vault}/checkpoints", { params: { path: { vault } }, body: { name } })),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ["checkpoints", vault] }),
  });
}

/** What restoring the checkpoint would change, and the plan that does exactly that; nothing is written yet. */
export const usePreviewCheckpoint = (vault: string, name: string) =>
  useQuery({
    // Its own key: what invalidates the list must not ask for another preview (and plan) behind the user.
    queryKey: ["checkpoint-preview", vault, name],
    queryFn: () => call(api.POST("/api/vaults/{vault}/checkpoints/{name}/preview", { params: { path: { vault, name } } })),
    // A preview holds a plan for an hour; it is made once per dialog, never again behind the user's back.
    staleTime: Infinity,
    gcTime: 0,
    refetchOnWindowFocus: false,
  });

export function useRestoreCheckpoint(vault: string, name: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (plan: string) => call(api.POST("/api/vaults/{vault}/checkpoints/{name}/restore", { params: { path: { vault, name } }, body: { plan } })),
    onSuccess: () => vaultChanged(queryClient, vault),
  });
}

/** Removes the checkpoint for good; the notes stay as they are. */
export function useRemoveCheckpoint(vault: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => call(api.DELETE("/api/vaults/{vault}/checkpoints/{name}", { params: { path: { vault, name } } })),
    onSettled: () => void queryClient.invalidateQueries({ queryKey: ["checkpoints", vault] }),
  });
}
