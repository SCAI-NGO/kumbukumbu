// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useDiscreet } from "../discreet/state";
import { api, call, type RefusalCode } from "./client";
import { addToFirstColumn } from "./board";
import { isVaultBusy } from "./error-text";
import { folderOf, inVault, isAttachment, untitledName, vaultOf, type FileAt } from "./paths";
import { textOrder } from "./textOrder";
import type { components } from "./schema";

export type TreeFolder = components["schemas"]["TreeFolder"];
export type TreeView = components["schemas"]["TreeView"];
export type NoteView = components["schemas"]["NoteView"];
export type EmbeddingProvider = components["schemas"]["EmbeddingProviderView"];

export type VaultView = components["schemas"]["VaultView"];

/** What the caller may do in a vault, in rising order (collab design §3.3): a member's role, or the owner's own vault. */
export type Level = "read" | "write" | "maintain" | "owner";
const levels: readonly Level[] = ["read", "write", "maintain", "owner"];

/**
 * The caller's level in `vault`. An answer without one (an older server) is her own vault unless it can't be written;
 * a vault not in the list (yet) is read: no write action shows before its level is known.
 */
export function levelOf(vault: VaultView | undefined): Level {
  if (!vault) return "read";
  if (levels.includes(vault.level as Level)) return vault.level as Level;
  return vault.canWrite === false ? "read" : "owner";
}

export const atLeast = (level: Level, least: Level) => levels.indexOf(level) >= levels.indexOf(least);

/** The vault's structure (`/Work/Board/Board.md`, the board's columns), which takes `maintain` to change, as the server says. */
export const isStructural = (path: string) => inVault(path) === "Board/Board.md";

/** Whether the level lets the caller change `path` (a note, or a folder holding the board). */
export const mayChange = (level: Level, path: string) => atLeast(level, isStructural(path) || inVault(path) === "Board" ? "maintain" : "write");

/**
 * The sentence for a refusal's `code` (collab design §3.5, §9.3), in the server's words: the vault is closing, its
 * owner is deactivated, the caller's level is below `write`, or the board's structure wants `maintain`.
 */
export function refusalSentence(code: RefusalCode, vault: string): string {
  switch (code) {
    case "closing":
      return `${vault} is read-only: its owner deleted their account, and it is closing.`;
    case "owner-inactive":
      return `${vault} is read-only while its owner's account is deactivated.`;
    case "read-only":
      return `You can only read ${vault}.`;
    case "structural":
      return `Only those who may maintain ${vault} can change its board's columns.`;
  }
}

/** Why the caller can't change `path` in `vault`, from the vault list (`refusalSentence`), or null if she can. */
export function refusalOf(vault: VaultView | undefined, path: string): string | null {
  const level = levelOf(vault);
  if (mayChange(level, path)) return null;
  const name = vault?.name ?? vaultOf(path);
  const code: RefusalCode = vault?.closingAt ? "closing" : vault?.ownerInactive ? "owner-inactive" : !atLeast(level, "write") ? "read-only" : "structural";
  return refusalSentence(code, name);
}

/** Whether people other than the caller can write into `vault`: her own vault that she shares, or one shared with her that isn't read-only for everyone. */
export const othersWrite = (vault: VaultView) => (vault.owner == null ? vault.shared : !vault.closingAt && !vault.ownerInactive);

/** Her own vault that nobody else reaches. */
export const isPrivate = (vault: VaultView) => vault.owner == null && !vault.shared;

/** The caller's level in the vault named `vault`, from the vault list. */
export function useLevel(vault: string | null): Level {
  const vaults = useVaults();
  return levelOf(vaults.data?.find((v) => v.name === vault));
}

/**
 * Whether the caller may change `path` (a file, or with `vaultWide` anything in its vault, as an upload into it), from
 * the vault list. While the list is unknown (or couldn't be read) the controls stay: the server refuses below the level
 * either way, and they would flash out of sight on every page otherwise.
 */
export function useMayChange(path: string, vaultWide = false) {
  const vaults = useVaults();
  if (!vaults.data) return true;
  const level = levelOf(vaults.data.find((v) => v.name === vaultOf(path)));
  return vaultWide ? atLeast(level, "write") : mayChange(level, path);
}

/**
 * Who shares `vault` with the caller, as she is shown it: the owner's display name (free text, quoted by the server)
 * with her username beside it, which tells two owners of one display name apart.
 */
export const ownerLabel = (vault: Pick<VaultView, "owner" | "ownerUsername">) =>
  vault.ownerUsername ? `${vault.owner ?? ""} (${vault.ownerUsername})` : (vault.owner ?? "");

/** What the server names an owner whose account is gone (a closing vault): no display name is left to show. */
export const goneOwner = "the owner";

/** The owner as a sentence begins with her: her quoted display name, or "The owner" once her account is gone. */
export const ownerAtStart = (vault: Pick<VaultView, "owner">) => (vault.owner === goneOwner ? "The owner" : (vault.owner ?? ""));

/**
 * The caller's own vaults, and those shared with her grouped by owner: by the owner's username, never by her display
 * name, which anyone may choose; in text order of what is shown.
 */
export function groupVaults(vaults: VaultView[]) {
  const own = vaults.filter((v) => v.owner == null);
  const byOwner = new Map<string, VaultView[]>();
  for (const v of vaults) {
    if (v.owner == null) continue;
    const key = v.ownerUsername ?? `display:${v.owner}`;
    byOwner.set(key, [...(byOwner.get(key) ?? []), v]);
  }
  const shared = [...byOwner]
    .map(([key, list]) => ({ key, owner: ownerLabel(list[0]), vaults: list }))
    .sort((a, b) => textOrder(a.owner, b.owner));
  return { own, shared };
}

/** `{ vault, path }` of the `/api/…/{vault}/{path}` endpoints for a note path (`/Work/plan.md`). */
const params = (path: string) => ({ path: { vault: vaultOf(path), path: inVault(path) } });

/** The vaults; while one is being re-encrypted, asked again every few seconds, so that it shows as done by itself. */
export const useVaults = () =>
  useQuery({
    queryKey: ["vaults"],
    queryFn: async () => {
      const vaults = await call(api.GET("/api/vaults"));
      // The discreet names follow the list wherever it is read: it carries them only while the mode is on.
      useDiscreet.getState().setFromVaults(vaults);
      return vaults;
    },
    refetchInterval: (query) => (query.state.data?.some((v) => v.pending) ? 5000 : false),
  });

/** The server's embedding providers, the internal model first. */
export const useEmbeddingProviders = () =>
  useQuery({ queryKey: ["embeddings"], queryFn: () => call(api.GET("/api/embeddings")), staleTime: 60_000 });

/** Asked again every few seconds while the vault is being re-encrypted: its "try again in a moment" comes true by itself. */
const whileBusy = (query: { state: { error: unknown } }) => (isVaultBusy(query.state.error) ? 3000 : false);

export const useTree = (vault: string | null) =>
  useQuery({
    queryKey: ["tree", vault],
    queryFn: () => call(api.GET("/api/vaults/{vault}/tree", { params: { path: { vault: vault! } } })),
    enabled: vault !== null,
    refetchInterval: whileBusy,
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
      const at = findFolder(tree.root, folder);
      const name = untitledName(at?.notes.map((n) => n.name) ?? []);
      // In the board's folder a note is a card: one made as a plain note would only be a problem on the board.
      if (folder === `/${vault}/Board` && at?.notes.some((n) => n.name === "Board.md")) return addToFirstColumn(queryClient, vault, name.slice(0, -".md".length));
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
    refetchInterval: whileBusy,
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
    mutationFn: ({ from, to }: { from: string; to: string }) => call(api.POST("/api/move", { body: { from, to, keepLinks: false, exact: false } })),
    onSuccess: (_, { from }) => {
      const gone = (key: readonly unknown[]) => typeof key[1] === "string" && (from.endsWith("/") ? key[1].startsWith(from) : key[1] === from);
      for (const key of ["note", "links", "backlinks", "history"]) queryClient.removeQueries({ queryKey: [key], predicate: (q) => gone(q.queryKey) });
      // Rewritten links changed other notes' text: every note, link and backlink is asked again.
      // Marks and publications move with their notes and folders.
      for (const key of ["tree", "vaults", "note", "links", "backlinks", "changes", "search", "publications", "publication"]) void queryClient.invalidateQueries({ queryKey: [key] });
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
      // Its publication is withdrawn: the publications and the marks are asked again.
      for (const key of ["publications", "publication"]) void queryClient.invalidateQueries({ queryKey: [key] });
    },
  });
}

/**
 * An attachment's bytes (`path` within `vault`) at `at`: today's, an earlier version's or a checkpoint's; with the
 * session (the server serves files to signed-in users and API keys only, so never by a plain URL).
 */
export function fetchFile<As extends "blob" | "arrayBuffer">(vault: string, path: string, at: FileAt, parseAs: As, signal: AbortSignal) {
  return at.checkpoint
    ? api.GET("/api/vaults/{vault}/checkpoints/{name}/files/{path}", { params: { path: { vault, name: at.checkpoint, path } }, parseAs, signal })
    : api.GET("/api/files/{vault}/{path}", { params: { path: { vault, path }, query: at.version ? { version: at.version } : {} }, parseAs, signal });
}

/**
 * An attachment's bytes (`path` within `vault`, at `version` if given, or as `checkpoint` held it) as an object URL
 * for an `img` or pdf.js. Undefined while they load, null if they can't be read; the URL is revoked when the
 * component unmounts or asks for another file. With `turn`, the fetch waits for one and ends it once the bytes are in
 * (the thumbnails' limit on fetches at once).
 */
export function useAttachmentUrl(
  vault: string,
  path: string,
  version?: string,
  checkpoint?: string,
  turn?: (signal: AbortSignal) => Promise<() => void>,
): string | null | undefined {
  const key = `${vault}\n${path}\n${version ?? ""}\n${checkpoint ?? ""}`;
  // Kept with the file it was made for, and dropped when its fetch ends: after a change of file (also back to one
  // shown before) the old URL, revoked by then, is never answered.
  const [made, setMade] = useState<{ key: string; url: string | null }>();
  useEffect(() => {
    const controller = new AbortController();
    let url: string | undefined;
    let endTurn: (() => void) | undefined;
    (turn ? turn(controller.signal) : Promise.resolve(() => {}))
      .then((end) => {
        endTurn = end;
        return fetchFile(vault, path, { version, checkpoint }, "blob", controller.signal);
      })
      .then(({ data, response }) => {
        if (controller.signal.aborted) return;
        if (!response.ok || !data) return setMade({ key, url: null });
        url = URL.createObjectURL(data as Blob);
        setMade({ key, url });
      })
      // The network failed: no URL. Aborted: another file, or none, is wanted now.
      .catch(() => !controller.signal.aborted && setMade({ key, url: null }))
      .finally(() => endTurn?.());
    return () => {
      controller.abort();
      endTurn?.();
      if (url) URL.revokeObjectURL(url);
      setMade(undefined);
    };
  }, [key, vault, path, version, checkpoint, turn]);
  return made?.key === key ? made.url : undefined;
}

/** An attachment as it is, without its bytes (`GET /api/version`): size, dimensions, version and last write. */
export const fileInfoQuery = (path: string) => ({
  queryKey: ["version", path],
  queryFn: () => call(api.GET("/api/version/{vault}/{path}", { params: params(path) })),
});

export const useFileInfo = (path: string, enabled = true) => useQuery({ ...fileInfoQuery(path), enabled });

/**
 * Moves an attachment to the vault's trash, only if it is still at `version` (else a 409: it changed meanwhile).
 * The notes that link to it are told apart by the dialog before; their links lead nowhere afterwards.
 */
export function useDeleteFile() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ path, version }: { path: string; version: string }) =>
      call(api.DELETE("/api/files/{vault}/{path}", { params: { ...params(path), query: { version } } })),
    onSuccess: (_, { path }) => {
      queryClient.removeQueries({ queryKey: ["version", path] });
      // Its mark, and whatever the publications say of it, are asked again.
      for (const key of ["tree", "vaults", "trash", "links", "backlinks", "activity", "history", "publications", "publication"])
        void queryClient.invalidateQueries({ queryKey: [key] });
    },
  });
}
