// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { api, call, HttpError } from "./client";
import { inVault, vaultOf } from "./paths";
import type { components } from "./schema";

export type PublicationView = components["schemas"]["PublicationView"];
export type VaultPublicationsView = components["schemas"]["VaultPublicationsView"];
export type PublicationOverviewView = components["schemas"]["PublicationOverviewView"];
export type PublishedView = components["schemas"]["PublishedView"];

/** What `plain` holds besides the embeds' names when one of them was a PDF (publish plan, decision 1). */
export const pdfsNotYet = "PDFs are not published yet";

/** `{ vault, path }` of the `/api/…/{vault}/{path}` endpoints for a note path (`/Work/plan.md`). */
const params = (path: string) => ({ path: { vault: vaultOf(path), path: inVault(path) } });

/** The session view, shared by the questions asked of it. */
export const sessionQuery = { queryKey: ["session"], queryFn: () => call(api.GET("/api/session")) };

/**
 * Whether the server publishes at all (`Kumbu:Publish:Enabled`): off, the web app offers no Publish and no Update.
 * Undefined until the session is in.
 */
export const usePublishEnabled = (): boolean | undefined => useQuery({ ...sessionQuery, select: (s) => s.publishEnabled === true }).data;

/**
 * Whether the password window is open, asked afresh: a cached answer could be one that closed meanwhile. Unknown
 * (still asking, or the question failed) it reads as closed, so the password is asked.
 */
export function usePasswordWindowOpen() {
  const session = useQuery({ ...sessionQuery, refetchOnMount: "always" });
  return { known: session.isFetchedAfterMount || session.isError, open: session.isFetchedAfterMount && session.data?.passwordWindowOpen === true };
}

/** A vault's publications; for its owner also its unpublishable notes and folders. Asked only while `enabled`. */
export const usePublications = (vault: string, enabled = true) =>
  useQuery({
    queryKey: ["publications", vault],
    enabled,
    queryFn: () => call(api.GET("/api/vaults/{vault}/publications", { params: { path: { vault } } })),
  });

/**
 * The note's publication, with the link for the owner; null when it isn't published. Asked only while `enabled`. The
 * link holds the code: once nothing shows it, it is gone from the cache (as the share dialog's links are).
 */
export const usePublication = (path: string, enabled = true) =>
  useQuery({
    queryKey: ["publication", path],
    enabled,
    gcTime: 0,
    queryFn: async () => {
      try {
        return await call(api.GET("/api/publications/{vault}/{path}", { params: params(path) }));
      } catch (e) {
        if (e instanceof HttpError && e.status === 404) return null;
        throw e;
      }
    },
  });

/** The publications of all the owner's own vaults, for the overview; never with a link. */
export const useAllPublications = () => useQuery({ queryKey: ["publications"], queryFn: () => call(api.GET("/api/publications")) });

/** After any change: the note's state, the lists, and the tree, whose marks follow. */
function invalidate(queryClient: QueryClient, path: string) {
  const vault = vaultOf(path);
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ["publication", path] }),
    // A prefix: the overview and every vault's list.
    queryClient.invalidateQueries({ queryKey: ["publications"] }),
    queryClient.invalidateQueries({ queryKey: ["tree", vault] }),
  ]);
}

/**
 * Publishes the note. A password given is checked as an invitation's and opens the window; without one the window
 * must be open. The answer holds the link: not kept in the mutation cache, and neither is the password.
 */
export function usePublish(path: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ password }: { password?: string }) =>
      call(api.POST("/api/publications/{vault}/{path}", { params: params(path), body: password ? { password } : {} })),
    gcTime: 0,
    onSettled: () => Promise.all([invalidate(queryClient, path), queryClient.invalidateQueries({ queryKey: ["session"] })]),
  });
}

/** Makes a new copy under the same link; a closed password window asks for the password first. */
export function useUpdatePublication(path: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => call(api.PUT("/api/publications/{vault}/{path}", { params: params(path) })),
    gcTime: 0,
    onSettled: () => invalidate(queryClient, path),
  });
}

/** Withdraws the link: its copy is deleted. Works while publishing is switched off too. */
export function useWithdraw(path: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => void (await call(api.DELETE("/api/publications/{vault}/{path}", { params: params(path) }))),
    onSettled: () => invalidate(queryClient, path),
  });
}

/** Whether a mark covers `path`: its own, or a folder's above it (publish spec §3.6). */
export const covered = (marks: readonly string[], path: string) => marks.some((m) => path === m || path.startsWith(`${m}/`));

const setMark = async (path: string, marked: boolean) =>
  void (await call(marked ? api.PUT("/api/unpublishable/{vault}/{path}", { params: params(path) }) : api.DELETE("/api/unpublishable/{vault}/{path}", { params: params(path) })));

/** A folder's mark withdraws whatever it covers: every note's state is asked again. */
const markSettled = (queryClient: QueryClient, path: string, marked: boolean) =>
  Promise.all([invalidate(queryClient, path), ...(marked ? [queryClient.invalidateQueries({ queryKey: ["publication"] })] : [])]);

/** Marks a note or folder unpublishable; whatever it covers that is published is withdrawn. */
export function useMarkUnpublishable(path: string) {
  const queryClient = useQueryClient();
  return useMutation({ mutationFn: () => setMark(path, true), onSettled: () => markSettled(queryClient, path, true) });
}

/** Lifts a note's or folder's mark. */
export function useAllowPublishing(path: string) {
  const queryClient = useQueryClient();
  return useMutation({ mutationFn: () => setMark(path, false), onSettled: () => markSettled(queryClient, path, false) });
}

/** Sets (`marked`) or lifts the mark of whichever note, file or folder is named when it runs: the menus' entries. */
export function useSetUnpublishable() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ path, marked }: { path: string; marked: boolean }) => setMark(path, marked),
    onSettled: (_, __, { path, marked }) => markSettled(queryClient, path, marked),
  });
}
