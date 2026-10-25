// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, call } from "./client";
import { untitledName } from "./paths";
import type { components } from "./schema";

export type TreeFolder = components["schemas"]["TreeFolder"];
export type TreeView = components["schemas"]["TreeView"];

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
