// SPDX-License-Identifier: AGPL-3.0-or-later
import { create } from "zustand";
import { folderOf, vaultOf } from "../api/paths";
import type { Picked } from "../upload/collectFiles";

export type Mode = "read" | "write";
export type Overlay = "none" | "tree" | "search" | "upload";
export type SearchMode = "hybrid" | "meaning" | "exact";
/** Text a session end left unsaved, to be saved on `version` once `username` is signed in again. */
export type Draft = { username: string; path: string; text: string; version: string };

const RECENT = 5;

/**
 * The editor's view state (MOBILE_EDITOR §3). In memory only, never persisted: note paths are content too, and
 * nothing of a vault's content stays on the device. Note content itself comes from the API layer (TanStack Query).
 */
interface EditorState {
  /** The open note, e.g. `/Work/projects/plan.md`; the URL is the source, the shell keeps this in step. */
  currentPath: string | null;
  mode: Mode;
  overlay: Overlay;
  /** Folder paths (`/Work/projects`) that are open in the drawer. */
  expandedFolders: Record<string, boolean>;
  activeVault: string | null;
  searchQuery: string;
  searchMode: SearchMode;
  showAgentHighlights: boolean;
  /** The last notes opened, newest first. */
  recentPaths: string[];
  /** A search result's section, to scroll to once its note is shown (`line` is the section's first source line). */
  scrollTarget: { path: string; line: number } | null;
  /** The upload sheet's folder (`/Work/projects`), and the files dropped there if it opened with a drop. */
  upload: { folder: string; dropped: Picked[] | null } | null;
  /** Set by the open editor: saves what is pending and answers whether Write mode may end (no conflict, no failure). */
  beforeRead: (() => Promise<boolean>) | null;
  /** Kept in memory only, like everything here; the note opens with it in Write mode. */
  draft: Draft | null;

  /** Opens a note: overlays close, its vault becomes active and its folder chain opens in the drawer. */
  openNote(path: string, mode?: Mode): void;
  setMode(mode: Mode): void;
  /** Ends Write mode (Done, the Read toggle): only once the editor has saved everything. */
  endWrite(): Promise<void>;
  setScrollTarget(target: { path: string; line: number } | null): void;
  setBeforeRead(guard: (() => Promise<boolean>) | null): void;
  setDraft(draft: Draft | null): void;
  setOverlay(overlay: Overlay): void;
  /** Opens the upload sheet for `folder` (`/Work/projects`), with the files of a drop if there was one. */
  openUpload(folder: string, dropped?: Picked[]): void;
  toggleFolder(path: string): void;
  setActiveVault(vault: string): void;
  setSearchQuery(query: string): void;
  setSearchMode(mode: SearchMode): void;
  setShowAgentHighlights(show: boolean): void;
  /** A deleted note: it leaves the recent notes, and is no longer the open one. */
  forgetNote(path: string): void;
  reset(): void;
}

const initial = {
  currentPath: null,
  mode: "read" as Mode,
  overlay: "none" as Overlay,
  expandedFolders: {},
  activeVault: null,
  searchQuery: "",
  searchMode: "hybrid" as SearchMode,
  showAgentHighlights: false,
  recentPaths: [],
  beforeRead: null,
  draft: null,
  scrollTarget: null,
  upload: null,
};

/** `/Work/projects/deep` → `/Work/projects`, `/Work/projects/deep` (the vault's root is always open). */
function chain(folder: string) {
  const segments = folder.split("/");
  return segments.slice(2).map((_, i) => segments.slice(0, i + 3).join("/"));
}

export const useEditor = create<EditorState>()((set, get) => ({
  ...initial,
  openNote: (path, mode = "read") =>
    set((s) => ({
      currentPath: path,
      mode: s.draft?.path === path ? "write" : mode,
      overlay: "none",
      activeVault: vaultOf(path),
      expandedFolders: { ...s.expandedFolders, ...Object.fromEntries(chain(folderOf(path)).map((f) => [f, true])) },
      recentPaths: [path, ...s.recentPaths.filter((p) => p !== path)].slice(0, RECENT),
    })),
  setMode: (mode) => set({ mode }),
  endWrite: async () => {
    const guard = get().beforeRead;
    if (!guard || (await guard())) set({ mode: "read" });
  },
  setBeforeRead: (beforeRead) => set({ beforeRead }),
  setDraft: (draft) => set({ draft }),
  setScrollTarget: (scrollTarget) => set({ scrollTarget }),
  setOverlay: (overlay) => set({ overlay }),
  openUpload: (folder, dropped) => set({ overlay: "upload", upload: { folder, dropped: dropped ?? null } }),
  toggleFolder: (path) => set((s) => ({ expandedFolders: { ...s.expandedFolders, [path]: !s.expandedFolders[path] } })),
  setActiveVault: (activeVault) => set({ activeVault }),
  setSearchQuery: (searchQuery) => set({ searchQuery }),
  setSearchMode: (searchMode) => set({ searchMode }),
  setShowAgentHighlights: (showAgentHighlights) => set({ showAgentHighlights }),
  forgetNote: (path) =>
    set((s) => ({ recentPaths: s.recentPaths.filter((p) => p !== path), currentPath: s.currentPath === path ? null : s.currentPath })),
  reset: () => set(initial),
}));
