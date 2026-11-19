// SPDX-License-Identifier: AGPL-3.0-or-later
import { createContext, useCallback, useContext } from "react";
import { useNavigate } from "react-router-dom";
import { useShallow } from "zustand/react/shallow";
import { besideOf, noteUrl, paneUrl } from "../api/paths";
import { movedPath, useEditor, type Mode, type Side } from "../state/editor";

/** The pane a part of the page is in, for the links in it; null outside the panes (Recent changes, What's on, a window). */
export const PaneSide = createContext<Side | null>(null);

/** The notes of both panes and which is focused, as the URL is made of them. */
export type PanesNow = { left: string | null; right: string | null; focused: Side };

export const panesNow = (s: ReturnType<typeof useEditor.getState>): PanesNow => ({ left: s.panes.left.path, right: s.panes.right?.path ?? null, focused: s.focused });

/**
 * The URL that opens `path` in pane `side`, the focused one unless given, the other pane kept: the right one starts the
 * split view where there is none. With no note on the left there is nothing to split, and it opens there.
 */
export function paneHref({ left, right, focused }: PanesNow, path: string, side: Side = focused): string {
  return side === "right" && left ? paneUrl(left, path) : paneUrl(path, side === "left" ? right : null);
}

/**
 * The URL of both panes once `from` (a note, or a folder or vault ending in `/`) is at `to`, as `movedPath` maps them;
 * null when neither pane's note moved. Taken before the store follows the move.
 */
export function movedPanesUrl({ left, right }: PanesNow, from: string, to: string): string | null {
  if (!left) return null;
  const [l, r] = [movedPath(left, from, to), right && movedPath(right, from, to)];
  return l === left && r === right ? null : paneUrl(l, r);
}

/**
 * Where the shell goes once `vault` is forgotten (deleted, left; `forgetVault` closed its panes, the other one left as
 * the only pane): null while neither pane of `location` is in it, else the panes the store kept, or the vaults.
 */
export function afterVaultGone(location: { pathname: string; search: string }, vault: string): string | null {
  const left = location.pathname.startsWith(noteUrl(`/${vault}/`));
  const right = location.pathname.startsWith("/notes/") && !!besideOf(location.search)?.startsWith(`/${vault}/`);
  if (!left && !right) return null;
  const { panes } = useEditor.getState();
  return panes.left.path ? paneUrl(panes.left.path, panes.right?.path ?? null) : "/";
}

/** Where a link to a note leads from here: into the pane it is shown in, else the focused one (workspace spec §3). */
export function useNoteHref(): (path: string) => string {
  const side = useContext(PaneSide);
  const panes = useEditor(useShallow(panesNow));
  return useCallback((path: string) => paneHref(panes, path, side ?? panes.focused), [panes, side]);
}

export type OpenOptions = { side?: Side; mode?: Mode; state?: unknown; replace?: boolean };

/**
 * Opens a note, what every opener calls (the tree, the palette, search, Recent, What's on): in the pane this is in
 * (`PaneSide`, as a link opens in its pane), else the focused one, or in `side` (the right one for Open beside); that
 * pane takes the focus. The store first, as the editor's guard expects, then the URL, a new history entry unless `replace`.
 */
export function useOpenNote() {
  const navigate = useNavigate();
  const here = useContext(PaneSide);
  return useCallback(
    (path: string, { side, mode = "read", state, replace }: OpenOptions = {}) => {
      const s = useEditor.getState();
      const wanted = side ?? here ?? s.focused;
      const target = wanted === "right" && !s.panes.left.path ? "left" : wanted;
      s.openIn(target, path, mode);
      navigate(paneHref(panesNow(useEditor.getState()), path, target), { state, replace });
    },
    [navigate, here],
  );
}
