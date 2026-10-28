// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { api, call } from "./client";
import { folderOf, inVault, isAttachment, untitledName, vaultOf } from "./paths";
import type { components } from "./schema";

export type TreeFolder = components["schemas"]["TreeFolder"];
export type TreeView = components["schemas"]["TreeView"];
export type NoteView = components["schemas"]["NoteView"];
export type EmbeddingProvider = components["schemas"]["EmbeddingProviderView"];

/** `{ vault, path }` of the `/api/…/{vault}/{path}` endpoints for a note path (`/Work/plan.md`). */
const params = (path: string) => ({ path: { vault: vaultOf(path), path: inVault(path) } });

/** The vaults; while one is being re-encrypted, asked again every few seconds, so that it shows as done by itself. */
export const useVaults = () =>
  useQuery({
    queryKey: ["vaults"],
    queryFn: () => call(api.GET("/api/vaults")),
    refetchInterval: (query) => (query.state.data?.some((v) => v.pending) ? 5000 : false),
  });

/** The server's embedding providers, the internal model first. */
export const useEmbeddingProviders = () =>
  useQuery({ queryKey: ["embeddings"], queryFn: () => call(api.GET("/api/embeddings")), staleTime: 60_000 });

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

/**
 * The note as the server has it. Not read again by itself when the page comes back into view or online: that would
 * replace the text under the reader's eye. A change made elsewhere is shown instead (`useChangedElsewhere`), and
 * taken when the user says so. `refetchOnMount: false` for a view next to the note (its history), which must not
 * read it again either.
 */
export const useNote = (path: string, { refetchOnMount = true }: { refetchOnMount?: boolean } = {}) =>
  useQuery({
    queryKey: ["note", path],
    queryFn: () => call(api.GET("/api/notes/{vault}/{path}", { params: params(path) })),
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchOnMount,
  });

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

/** Browsers refuse keepalive requests above 64 KiB (counted over all of them in flight); this leaves room. */
const KEEPALIVE_LIMIT = 60_000;

export type SaveResult = { saved: NoteView } | { conflict: { content: string; version: string } };

/**
 * Writes the note on top of `version`. A 409 is an answer, not a failure: the note changed elsewhere, and its
 * current text and version come back so the view can show them instead of overwriting them. `keepalive` (the page
 * is going away) is honoured up to the browsers' limit; a larger note goes as a normal request and may not arrive.
 */
export function useSaveNote(path: string) {
  const queryClient = useQueryClient();
  return useMutation({
    // Known to the change bar: while a save is under way, the version it brings is no change made elsewhere.
    mutationKey: ["save", path],
    mutationFn: async ({ content, version, keepalive = false }: { content: string; version: string; keepalive?: boolean }): Promise<SaveResult> => {
      const body = { content, version };
      const small = new TextEncoder().encode(JSON.stringify(body)).length <= KEEPALIVE_LIMIT;
      const { data, error, response } = await api.PUT("/api/notes/{vault}/{path}", { params: params(path), body, keepalive: keepalive && small });
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
 * Moves (or renames) a note, or a folder with every note below it (`from` ending in `/`), within its vault. The
 * server rewrites the links the move would break, in one batch with the move, and says so in `message`.
 */
export function useMove() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ from, to }: { from: string; to: string }) => call(api.POST("/api/move", { body: { from, to, keepLinks: false } })),
    onSuccess: (_, { from }) => {
      const gone = (key: readonly unknown[]) => typeof key[1] === "string" && (from.endsWith("/") ? key[1].startsWith(from) : key[1] === from);
      for (const key of ["note", "links", "backlinks", "history"]) queryClient.removeQueries({ queryKey: [key], predicate: (q) => gone(q.queryKey) });
      // Rewritten links changed other notes' text: every note, link and backlink is asked again.
      for (const key of ["tree", "vaults", "note", "links", "backlinks", "changes", "search"]) void queryClient.invalidateQueries({ queryKey: [key] });
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
      // The tree's `notes` hold the folder's attachments too; the way on is to a note.
      const siblings = findFolder(tree.root, folderOf(path))?.notes.filter((n) => n.path === path || !isAttachment(n.path)).map((n) => n.path) ?? [];
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

/**
 * An attachment's bytes (`path` within `vault`, at `version` if given) as an object URL for an `img` or pdf.js:
 * fetched with the session, as the server serves files to signed-in users only. Undefined while they load, or if
 * they can't be read; the URL is revoked when the component unmounts or asks for another file.
 */
export function useAttachmentUrl(vault: string, path: string, version?: string): string | undefined {
  const [url, setUrl] = useState<string>();
  useEffect(() => {
    const controller = new AbortController();
    let made: string | undefined;
    setUrl(undefined);
    api
      .GET("/api/files/{vault}/{path}", {
        params: { path: { vault, path }, query: version ? { version } : {} },
        parseAs: "blob",
        signal: controller.signal,
      })
      .then(({ data, response }) => {
        if (!response.ok || !data || controller.signal.aborted) return;
        made = URL.createObjectURL(data);
        setUrl(made);
      })
      .catch(() => {}); // Aborted, or the network failed: no URL.
    return () => {
      controller.abort();
      if (made) URL.revokeObjectURL(made);
    };
  }, [vault, path, version]);
  return url;
}
