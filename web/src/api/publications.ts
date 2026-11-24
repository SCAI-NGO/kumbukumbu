// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueries, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { api, call, HttpError } from "./client";
import { inVault, vaultOf } from "./paths";
import type { components } from "./schema";

export type PublicationView = components["schemas"]["PublicationView"];
export type VaultPublicationsView = components["schemas"]["VaultPublicationsView"];
export type PublicationOverviewView = components["schemas"]["PublicationOverviewView"];
export type PublishedView = components["schemas"]["PublishedView"];
export type PublicationPreviewView = components["schemas"]["PublicationPreviewView"];

/** The deepest a publication goes (Publish 2.0); from this many notes on the dialog warns. */
export const maxDepth = 5;
export const manyNotes = 50;

/** The paths of a preview's notes, sorted: what a publication sends as `expected`. */
export const previewSet = (preview: PublicationPreviewView) => preview.notes.map((n) => n.path).sort();

/** A path in the vault (`Plan.md`), as the publication endpoints take the notes they name. */
const inItsVault = (path: string) => inVault(path);

/** The notes a publication would take changed since the list was shown (409): nothing went out; the list as it is now. */
export class BundleChangedError extends Error {
  constructor(readonly preview: PublicationPreviewView) {
    super("Something changed in the vault: check the list again.");
  }
}

type Answer<T> = { data?: T; error?: unknown; response: Response };

/** A 409 with the walk as it is now is a {@link BundleChangedError}; everything else as `call` reads it. */
async function bundled<T>(request: Promise<Answer<T>>): Promise<T> {
  const answer = await request;
  const error = answer.error;
  if (answer.response.status === 409 && error && typeof error === "object" && "preview" in error) {
    throw new BundleChangedError((error as { preview: PublicationPreviewView }).preview);
  }
  return call(Promise.resolve(answer));
}

/**
 * What publishing the note `depth` links deep, without the notes `without`, would take; at depth 0 the note alone.
 */
export const usePublicationPreview = (path: string, depth: number, without: readonly string[]) =>
  useQuery({
    queryKey: ["publication-preview", path, depth, [...without].sort()],
    queryFn: () =>
      call(
        api.GET("/api/publication-previews/{vault}/{path}", {
          params: { ...params(path), query: { depth, ...(without.length > 0 ? { without: without.map(inItsVault) } : {}) } },
        }),
      ),
    staleTime: 0,
    gcTime: 0,
  });

/** The start notes of the bundles `path` is published in, not as their start (Publish 2.0 decision 7). A member is
 * told no start: for her, one empty name says that it is published. */
export function useBundledIn(path: string): string[] {
  const list = usePublications(vaultOf(path)).data;
  return (list?.bundled ?? []).filter((b) => b.path === path).map((b) => b.start ?? "");
}

/** The notes of `start`'s bundle as the vault's list names them, the start first. */
export function useBundleOf(start: string): string[] {
  const list = usePublications(vaultOf(start)).data;
  return [start, ...(list?.bundled ?? []).filter((b) => b.start === start).map((b) => b.path)];
}

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

/** The note's publication, shared by its own bar and the bars of the notes in its bundle. */
const publicationQuery = (path: string) => ({
  queryKey: ["publication", path],
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

/**
 * The note's publication, with the link for the owner; null when it isn't published. Asked only while `enabled`. The
 * link holds the code: once nothing shows it, it is gone from the cache (as the share dialog's links are).
 */
export const usePublication = (path: string, enabled = true) => useQuery({ ...publicationQuery(path), enabled });

/** When the copy of each of `starts`' bundles was made (Publish 2.0 decision 7), from the start's own publication;
 * undefined until it answers, null when it holds no time. Asked only while `enabled`. */
export const useBundleCopies = (starts: readonly string[], enabled: boolean): (string | null | undefined)[] =>
  useQueries({ queries: starts.map((start) => ({ ...publicationQuery(start), enabled })) }).map((q) => q.data && (q.data.snapshotAt ?? null));

/**
 * The note's publication as its bar wants it: asked of its own only once the vault's list names it, so an unpublished
 * note makes no request that answers 404. Undefined until the list is in (and while it fails: no bar then, not even
 * Unpublish, the price of asking nothing that answers 404), null when it doesn't name the note.
 */
export function useNotePublication(path: string): { data: PublicationView | null | undefined } {
  const list = usePublications(vaultOf(path)).data;
  const listed = !!list?.publications.some((p) => p.path === path);
  const publication = usePublication(path, listed);
  return { data: !list ? undefined : listed ? publication.data : null };
}

/** The publications of all the owner's own vaults, for the overview; never with a link. */
export const useAllPublications = () => useQuery({ queryKey: ["publications"], queryFn: () => call(api.GET("/api/publications")) });

/** After any change: the note's state (unless it is known), the lists, and the tree, whose marks follow. */
function invalidate(queryClient: QueryClient, path: string, { note = true } = {}) {
  const vault = vaultOf(path);
  return Promise.all([
    ...(note ? [queryClient.invalidateQueries({ queryKey: ["publication", path] })] : []),
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
    mutationFn: ({ password, depth = 0, without = [], expected }: { password?: string; depth?: number; without?: string[]; expected?: string[] }) =>
      bundled(
        api.POST("/api/publications/{vault}/{path}", {
          params: params(path),
          body: {
            ...(password ? { password } : {}),
            ...(depth > 0 ? { depth, without: without.map(inItsVault), ...(expected ? { expected: expected.map(inItsVault) } : {}) } : {}),
          },
        }),
      ),
    gcTime: 0,
    onSettled: () => Promise.all([invalidate(queryClient, path), queryClient.invalidateQueries({ queryKey: ["session"] })]),
  });
}

/** Makes a new copy under the same link; a closed password window asks for the password first. */
export function useUpdatePublication(path: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (walk?: { depth: number; without: string[]; expected: string[] }) =>
      bundled(
        api.PUT("/api/publications/{vault}/{path}", {
          params: params(path),
          ...(walk ? { body: { depth: walk.depth, without: walk.without.map(inItsVault), expected: walk.expected.map(inItsVault) } } : {}),
        }),
      ),
    gcTime: 0,
    onSettled: () => invalidate(queryClient, path),
  });
}

/** Withdraws the link: its copy is deleted. Works while publishing is switched off too. */
export function useWithdraw(path: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => void (await call(api.DELETE("/api/publications/{vault}/{path}", { params: params(path) }))),
    // Withdrawn, it is known not to be published: asked again, it would answer 404 until the list says so.
    onSuccess: () => queryClient.setQueryData(["publication", path], null),
    onSettled: (_, error) => invalidate(queryClient, path, { note: !!error }),
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
