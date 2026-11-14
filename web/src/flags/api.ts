// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, call } from "../api/client";
import { vaultOf } from "../api/paths";
import { afterEntryWrite, editorSettled, UnsavedError } from "../calendar/api";

// The flag's JSON, as `FlagApi` answers it, by hand until the generated schema has it (as the calendar's).

/** The eight flags (spec §2.2), in the order the menu offers them: each is the tag `flag/<name>`. */
export const FLAGS = ["red", "green", "blue", "purple", "yellow", "white", "done", "important"] as const;
export type FlagName = (typeof FLAGS)[number];

export const isFlag = (name: string | null | undefined): name is FlagName => FLAGS.includes(name as FlagName);

/**
 * A note's flag: `inText` is the tag as the text writes it (`#flag/red`) when the flag is the text's, which Kumbu²
 * never changes; `openTask` whether the note is an open to-do of the calendar (no board card), which ✓ asks about.
 */
export type FlagView = { flag: string | null; inText: string | null; openTask: boolean; canWrite: boolean };
export type FlagWrittenView = { path: string; version: string; note: FlagView };
/** `calendar`: with the flag, close the to-do (Mark done) or open it again (its Undo), in the same write. */
export type SetFlagRequest = { path: string; flag: FlagName | null; calendar?: "close" | "reopen" };

type Answer<T> = Promise<{ data?: T; error?: unknown; response: Response }>;
type Init = { params: { path: { vault: string }; query?: Record<string, string> }; body?: unknown };
const untyped = api as unknown as { GET<T>(url: string, init: Init): Answer<T>; POST<T>(url: string, init: Init): Answer<T> };

/** The note's flag, for its header and the flag menu: asked only for a note shown, or whose menu is open. */
export function useFlag(path: string | null, enabled = true) {
  return useQuery({
    queryKey: ["flag", path],
    queryFn: () => call(untyped.GET<FlagView>("/api/vaults/{vault}/flag", { params: { path: { vault: vaultOf(path!) }, query: { path: path! } } })),
    enabled: enabled && path !== null,
    retry: false,
  });
}

/**
 * Sets, changes or removes a note's flag, once the editor's text is saved (as the calendar's writes). The flag's view is
 * the write's own answer; every other view of the note is asked again: the note itself (awaited, so Write mode after it
 * starts on the new version), the tree, the search, the tags, its history, the changes and Recent changes, and the
 * calendar and What's on even without `calendar`, as a tag filter may name a flag; with `calendar`, the rest of the
 * calendar's views too.
 */
export function useSetFlag() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ path, flag, calendar }: SetFlagRequest) => {
      if (!(await editorSettled(path))) throw new UnsavedError();
      const body = calendar ? { path, flag, calendar } : { path, flag };
      return call(untyped.POST<FlagWrittenView>("/api/vaults/{vault}/flag", { params: { path: { vault: vaultOf(path) } }, body }));
    },
    onSuccess: (written, { calendar }) => {
      const vault = vaultOf(written.path);
      queryClient.setQueryData(["flag", written.path], written.note);
      const touched = [["tree", vault], ["search"], ["tags"], ["history", written.path], ["changes"], ["recent"], ["calendar"], ["whats-on"]];
      for (const queryKey of touched) void queryClient.invalidateQueries({ queryKey });
      const note = queryClient.invalidateQueries({ queryKey: ["note", written.path] });
      return calendar ? Promise.all([note, afterEntryWrite(queryClient, vault, written.path)]) : note;
    },
  });
}
