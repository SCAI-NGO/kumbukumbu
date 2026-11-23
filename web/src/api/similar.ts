// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { afterEntryWrite, editorSettled, UnsavedError } from "../calendar/api";
import { api, call } from "./client";
import { inVault, vaultOf } from "./paths";

// Link prediction's JSON, as `SimilarApi` answers it, by hand until the generated schema has it (as the calendar's).

/** A suggested link: the other note, the 1-based line its closest section starts on, that section's first line, its cosine. */
export type SimilarNote = { path: string; line: number; text: string; score: number };
/** `unindexed`: the note has no embeddings yet; `preparing`: the model is loading; `off`: no search by meaning for this vault. */
export type SimilarState = "ready" | "unindexed" | "preparing" | "off";
export type SimilarNotesView = { state: SimilarState; notes: SimilarNote[]; pending: number };
type Written = { path: string; version: string };

type Answer<T> = Promise<{ data?: T; error?: unknown; response: Response }>;
type Init = { params: { path: Record<string, string> }; body?: unknown };
const untyped = api as unknown as { GET<T>(url: string, init: Init): Answer<T>; POST<T>(url: string, init: Init): Answer<T> };

/** The notes of `path`'s vault close in meaning that it neither links nor is linked from; asked only while `enabled`. */
export function useSimilarNotes(path: string, enabled: boolean) {
  return useQuery({
    queryKey: ["similar", path],
    queryFn: () =>
      call(untyped.GET<SimilarNotesView>("/api/similar/{vault}/{path}", { params: { path: { vault: vaultOf(path), path: inVault(path) } } })),
    enabled,
  });
}

/**
 * One answer to a suggestion, once the editor's text is saved (as a flag's): `link` writes `- [[other]]` in the note's
 * Related section, `not-related` keeps the pair in its properties (`undo` takes it back). Every view of the note is asked
 * again, and the suggestions of both notes.
 */
function useAnswer(kind: "link" | "not-related") {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ path, other, undo = false }: { path: string; other: string; undo?: boolean }) => {
      if (!(await editorSettled(path))) throw new UnsavedError();
      const body = kind === "link" ? { path, target: other } : { path, other, undo };
      return call(untyped.POST<Written>(`/api/vaults/{vault}/similar/${kind}`, { params: { path: { vault: vaultOf(path) } }, body }));
    },
    onSuccess: (written, { other }) => {
      for (const queryKey of [["links"], ["backlinks"], ["mentions"], ["graph"], ["search"], ["tags"], ["history", written.path], ["changes"], ["recent"], ["similar", other]])
        void queryClient.invalidateQueries({ queryKey });
      void queryClient.invalidateQueries({ queryKey: ["similar", written.path] });
      return afterEntryWrite(queryClient, vaultOf(written.path), written.path);
    },
  });
}

export const useAddLink = () => useAnswer("link");
export const useNotRelated = () => useAnswer("not-related");
