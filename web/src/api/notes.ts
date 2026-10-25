// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, call } from "./client";
import { folderOf, inVault, untitledName, vaultOf } from "./paths";
import type { components } from "./schema";

export type TreeFolder = components["schemas"]["TreeFolder"];
export type TreeView = components["schemas"]["TreeView"];
export type NoteView = components["schemas"]["NoteView"];

/** `{ vault, path }` of the `/api/…/{vault}/{path}` endpoints for a note path (`/Work/plan.md`). */
const params = (path: string) => ({ path: { vault: vaultOf(path), path: inVault(path) } });

export const useVaults = () => useQuery({ queryKey: ["vaults"], queryFn: () => call(api.GET("/api/vaults")) });

export const useTree = (vault: string | null) =>
  useQuery({
    queryKey: ["tree", vault],
    queryFn: () => call(api.GET("/api/vaults/{vault}/tree", { params: { path: { vault: vault! } } })),
    enabled: vault !== null,
  });

/** The folder at `path` (`/Work/projects`) in a vault's tree, if it is there. */
export function findFolder(root: TreeFolder, path: string): TreeFolder | undefined {
  if (root.path === path) return root;
  return root.folders.map((f) => (path.startsWith(`${f.path}/`) || path === f.path ? findFolder(f, path) : undefined)).find(Boolean);
}

/** An empty note at `path`; one that is there already (its link was stale) is simply opened. */
async function createEmpty(path: string) {
  const { error, response } = await api.PUT("/api/notes/{vault}/{path}", { params: params(path), body: { content: "" } });
  if (!response.ok && response.status !== 409) await call(Promise.resolve({ error, response }));
  return path;
}

/** Creates the first free `Untitled` note in a folder (`/Work/projects`) and answers its path. */
export function useCreateUntitled() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (folder: string) => {
      const [vault, ...rest] = folder.split("/").slice(1);
      const tree = await queryClient.ensureQueryData({
        queryKey: ["tree", vault],
        queryFn: () => call(api.GET("/api/vaults/{vault}/tree", { params: { path: { vault } } })),
      });
      const name = untitledName(findFolder(tree.root, folder)?.notes.map((n) => n.name) ?? []);
      const path = [...rest, name].join("/");
      const note = await call(api.PUT("/api/notes/{vault}/{path}", { params: { path: { vault, path } }, body: { content: "" } }));
      return note.path;
    },
    onSettled: (_, __, folder) => {
      void queryClient.invalidateQueries({ queryKey: ["tree", folder.split("/")[1]] });
      void queryClient.invalidateQueries({ queryKey: ["vaults"] });
    },
  });
}

/** Creates an empty note at `path` (a missing link's target) and answers its path. */
export function useCreateNote() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: createEmpty,
    onSettled: (_, __, path) => {
      void queryClient.invalidateQueries({ queryKey: ["tree", vaultOf(path)] });
      void queryClient.invalidateQueries({ queryKey: ["vaults"] });
      // Links to it that were "not found" lead somewhere now.
      void queryClient.invalidateQueries({ queryKey: ["links"] });
    },
  });
}

export const useNote = (path: string) =>
  useQuery({ queryKey: ["note", path], queryFn: () => call(api.GET("/api/notes/{vault}/{path}", { params: params(path) })) });

/** The note's links as the server resolves them; asked again for every version, as its text decides them. */
export const useLinks = (path: string, version: string | undefined) =>
  useQuery({
    queryKey: ["links", path, version],
    queryFn: () => call(api.GET("/api/links/{vault}/{path}", { params: params(path) })),
    enabled: version !== undefined,
    placeholderData: (previous) => previous,
  });

export const useBacklinks = (path: string) =>
  useQuery({ queryKey: ["backlinks", path], queryFn: () => call(api.GET("/api/backlinks/{vault}/{path}", { params: params(path) })) });

/** Notes last written by one of the user's keys since `since` (session only): the agent banner. Not asked without a time. */
export const useChanges = (since: string | null) =>
  useQuery({
    queryKey: ["changes", since],
    queryFn: () => call(api.GET("/api/changes", { params: { query: { since: since! } } })),
    enabled: since !== null,
  });

export type HistoryEntry = components["schemas"]["HistoryEntryView"];

/** The note's versions, newest first; asked only while `enabled`. */
export const useHistory = (path: string, enabled: boolean) =>
  useQuery({ queryKey: ["history", path], queryFn: () => call(api.GET("/api/history/{vault}/{path}", { params: params(path) })), enabled });

/** One version of the note by its token (`?version=`); a version's text never changes, so it is kept as it is. */
export const useNoteVersion = (path: string, version: string | null) =>
  useQuery({
    queryKey: ["note", path, "version", version],
    queryFn: () => call(api.GET("/api/notes/{vault}/{path}", { params: { ...params(path), query: { version: version! } } })),
    enabled: version !== null,
    staleTime: Infinity,
  });

export type SaveResult = { saved: NoteView } | { conflict: { content: string; version: string } };

/**
 * Writes the note on top of `version`. A 409 is an answer, not a failure: the note changed elsewhere, and its
 * current text and version come back so the view can show them instead of overwriting them.
 */
export function useSaveNote(path: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ content, version }: { content: string; version: string }): Promise<SaveResult> => {
      const { data, error, response } = await api.PUT("/api/notes/{vault}/{path}", { params: params(path), body: { content, version } });
      if (response.status === 409 && error && "current" in error) return { conflict: error.current };
      return { saved: await call(Promise.resolve({ data, error, response })) };
    },
    onSuccess: (result) => {
      const now = "saved" in result ? result.saved : result.conflict;
      queryClient.setQueryData<NoteView>(["note", path], (old) => old && { ...old, ...now });
      // The banner counts the last write; after the user's own it no longer applies.
      if ("saved" in result) {
        void queryClient.invalidateQueries({ queryKey: ["changes"] });
        void queryClient.invalidateQueries({ queryKey: ["history", path] });
      }
    },
  });
}

/**
 * Deletes a note and answers where to go: the next note of its folder (else the one before), or null for none.
 * The neighbour is looked up first, while the tree still has the note.
 */
export function useDeleteNote() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (path: string) => {
      const vault = vaultOf(path);
      const tree = await queryClient.ensureQueryData({
        queryKey: ["tree", vault],
        queryFn: () => call(api.GET("/api/vaults/{vault}/tree", { params: { path: { vault } } })),
      });
      const siblings = findFolder(tree.root, folderOf(path))?.notes.map((n) => n.path) ?? [];
      const at = siblings.indexOf(path);
      const next = siblings[at + 1] ?? (at > 0 ? siblings[at - 1] : undefined) ?? null;
      await call(api.DELETE("/api/notes/{vault}/{path}", { params: params(path) }));
      return next;
    },
    onSuccess: (_, path) => {
      for (const key of ["note", "links", "backlinks", "history"]) queryClient.removeQueries({ queryKey: [key, path] });
      void queryClient.invalidateQueries({ queryKey: ["tree", vaultOf(path)] });
      void queryClient.invalidateQueries({ queryKey: ["vaults"] });
      // Links to it lead nowhere now, and it no longer links to anything.
      void queryClient.invalidateQueries({ queryKey: ["links"] });
      void queryClient.invalidateQueries({ queryKey: ["backlinks"] });
      void queryClient.invalidateQueries({ queryKey: ["changes"] });
    },
  });
}
