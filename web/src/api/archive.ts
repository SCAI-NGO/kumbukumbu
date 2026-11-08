// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { api, call, HttpError } from "./client";
import { findFolder, type TreeView } from "./notes";
import { folderOf, inVault, isAttachment, isMarker, vaultOf } from "./paths";
import type { components } from "./schema";
import { textOrder } from "./textOrder";

export type ArchiveItem = components["schemas"]["ArchiveItem"];
export type ArchiveView = components["schemas"]["ArchiveView"];

/** The vault's archive item by item, as the server orders them: stamped items newest first, then any other file. */
export const useArchive = (vault: string) =>
  useQuery({
    queryKey: ["archive", vault],
    queryFn: () => call(api.GET("/api/vaults/{vault}/archive", { params: { path: { vault } } })),
  });

/**
 * The notes and attachments below an archived folder (`/Work/_archive/Manual 2026-11-08 213005`), in text order of
 * their paths, folder markers left out: from the vault's versions, as the tree leaves the archive out.
 */
export const useArchivedFiles = (folder: string, enabled: boolean) =>
  useQuery({
    queryKey: ["archive", vaultOf(folder), "files", folder],
    queryFn: async () => {
      const vault = vaultOf(folder);
      const answer = await call(api.GET("/api/vaults/{vault}/versions", { params: { path: { vault }, query: { attachments: "1", text: "1" } } }));
      return answer.notes
        .map((n) => n.path)
        .filter((p) => p.startsWith(`${folder}/`) && !isMarker(p))
        .sort(textOrder);
    },
    enabled,
  });

/** What changes with an item restored or deleted: the vault's tree and archive, and the account's storage. */
function archiveChanged(queryClient: QueryClient, vault: string) {
  for (const key of [["tree", vault], ["archive", vault], ["account", "storage"], ["vaults"], ["trash", vault], ["links"], ["backlinks"]])
    void queryClient.invalidateQueries({ queryKey: key });
}

/** Whether a note, attachment or folder is at `path` in the tree (as the server's paths compare: without case). */
export function takenIn(tree: TreeView, path: string) {
  const there = findFolder(tree.root, folderOf(path));
  const same = (p: string) => p.toLowerCase() === path.toLowerCase();
  return !!there && (there.folders.some((f) => same(f.path)) || there.notes.some((n) => same(n.path)));
}

/** A restore done, or the target taken: by what the tree shows, or by the server's 409 (taken meanwhile), its sentence in `message`. */
export type RestoreResult = { moved: string } | { taken: string; message?: string };

/**
 * Puts an archived item back at `to` (its original path, or another name the user chose), through `/api/move` as any
 * move: a folder moves with everything in it, and links that the move would break are rewritten. The target is
 * checked first in the vault's tree, asked anew: `mv` onto a folder would put the item inside it, not in its place.
 */
export function useRestoreArchived(vault: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ item, to }: { item: ArchiveItem; to: string }): Promise<RestoreResult> => {
      const tree = await queryClient.fetchQuery({
        queryKey: ["tree", vault],
        queryFn: () => call(api.GET("/api/vaults/{vault}/tree", { params: { path: { vault } } })),
        staleTime: 0,
      });
      if (takenIn(tree, to)) return { taken: to };
      try {
        await call(api.POST("/api/move", { body: item.folder ? { from: `${item.path}/`, to: `${to}/`, keepLinks: false } : { from: item.path, to, keepLinks: false } }));
      } catch (e) {
        if (e instanceof HttpError && e.status === 409) return { taken: to, message: e.message };
        throw e;
      }
      return { moved: to };
    },
    onSuccess: (result, { item }) => {
      if (!("moved" in result)) return;
      const inItem = (k: unknown) => typeof k === "string" && (k === item.path || k.startsWith(`${item.path}/`));
      for (const key of ["note", "version", "history"]) queryClient.removeQueries({ queryKey: [key], predicate: (q) => inItem(q.queryKey[1]) });
      archiveChanged(queryClient, vault);
    },
  });
}

/** Moves an archived item to the vault's trash: a note or attachment as anywhere, a folder with everything in it. */
export function useDeleteArchived(vault: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (item: ArchiveItem) => {
      const params = { path: { vault: vaultOf(item.path), path: inVault(item.path) } };
      if (item.folder) return call(api.POST("/api/folders/trash", { body: { path: `${item.path}/` } }));
      if (isAttachment(item.path)) return call(api.DELETE("/api/files/{vault}/{path}", { params }));
      return call(api.DELETE("/api/notes/{vault}/{path}", { params }));
    },
    onSuccess: (_, item) => {
      queryClient.removeQueries({ queryKey: ["note", item.path] });
      queryClient.removeQueries({ queryKey: ["version", item.path] });
      archiveChanged(queryClient, vault);
    },
  });
}
