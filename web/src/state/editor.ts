// SPDX-License-Identifier: AGPL-3.0-or-later
import { create, type StateCreator } from "zustand";
import { folderOf, vaultOf } from "../api/paths";
import type { RefusalCode } from "../api/client";
import type { Picked } from "../upload/collectFiles";

export type Mode = "read" | "write";
export type Overlay = "none" | "tree" | "search" | "upload" | "palette";
export type SearchMode = "hybrid" | "meaning" | "exact";
/** The open note's tools in Read mode: its outline (a sheet), find in the note (a bar in place of the dock), or the Add property dialog. */
export type NoteTool = "outline" | "find" | "addProperty" | null;
export type Find = { query: string; index: number; count: number };
export type BeforeRead = (options?: { reread?: boolean }) => Promise<boolean>;
/** The open editor's text and the version it goes on, while any of it isn't saved. */
export type Unsaved = () => { text: string; version: string } | null;
/** The caret's place on screen: its left, and the top and bottom of its line. */
export type CaretAt = () => { left: number; top: number; bottom: number };
/** The open editor's text as it stands (unsaved included) and the line its caret is on, 1-based. */
export type Live = { path: string; text: string; caretLine: number };
/** Text a session end left unsaved, to be saved on `version` once `username` is signed in again. */
export type Draft = { username: string; path: string; text: string; version: string };
export type Side = "left" | "right";
/** A pane: its note (none on the left before one is opened) and its mode. */
export type Pane = { path: string | null; mode: Mode };
/** The left pane, always there, and the right one of the split view, or null. */
export type Panes = { left: Pane; right: (Pane & { path: string }) | null };

const RECENT = 5;

/**
 * The editor's view state (MOBILE_EDITOR §3). In memory only, never persisted: note paths are content too, and
 * nothing of a vault's content stays on the device. Note content itself comes from the API layer (TanStack Query).
 */
interface EditorState {
  /** The focused pane's note, e.g. `/Work/projects/plan.md`; the URL is the source, the shell keeps this in step. */
  currentPath: string | null;
  /** The focused pane's mode. */
  mode: Mode;
  /**
   * The split view's panes (workspace spec §3). The focused one follows `currentPath` and `mode`, however these are set;
   * at most one is in Write mode, and the editor's state (`beforeRead`, `unsaved`, `draft`, `live`, `goToLine`) is its.
   */
  panes: Panes;
  focused: Side;
  overlay: Overlay;
  /** Folder paths (`/Work/projects`) that are open in the drawer. */
  expandedFolders: Record<string, boolean>;
  /** The folder the drawer marks (`/Work/projects`): one just made, or found by its name; until a note is opened. */
  selectedFolder: string | null;
  /** Set by `revealFolder`: the marked folder's entry takes the focus once, when it is next shown (`folderRevealed`). */
  revealing: boolean;
  activeVault: string | null;
  searchQuery: string;
  searchMode: SearchMode;
  /** The search sheet's tag filter, without `#` (`research`), or null. */
  searchTag: string | null;
  showAgentHighlights: boolean;
  /** The last notes opened, newest first. */
  recentPaths: string[];
  /** A search result's section, to scroll to once its note is shown (`line` is the section's first source line). */
  scrollTarget: { path: string; line: number } | null;
  /**
   * The upload sheet's folder (`/Work/projects`), and the files dropped there if it opened with a drop; `start`: they go
   * without the review (a photo just taken), unless something of them is skipped.
   */
  upload: { folder: string; dropped: Picked[] | null; start?: boolean } | null;
  /**
   * Set by the open editor: saves what is pending and answers whether Write mode may end (no conflict, no failure).
   * Afterwards the note is asked for again, unless `reread` is false: signing out, where that read would race the
   * session's end and answer 401.
   */
  beforeRead: BeforeRead | null;
  /** Set by the open editor with `beforeRead`: what a save that failed left, for hiding discreet vaults again. */
  unsaved: Unsaved | null;
  /** Set by the open editor: where its caret stands on screen, for the note's menu from the keyboard. */
  caretAt: CaretAt | null;
  /**
   * Set by the open editor as the text or the caret moves, null without one: what follows the text being written (the
   * side panel's outline, the live preview).
   */
  live: Live | null;
  /** Set by the open editor: puts the caret at the start of a line (1-based), the focus in the editor, the line in view. */
  goToLine: ((line: number) => void) | null;
  /**
   * Where Write mode, started by a click into a pane's text, puts the caret in the note at `path` (workspace spec §3):
   * at `offset` in its text, the caret's line at `y` on screen, where the click was. Taken by the editor as it mounts.
   */
  writeAt: { path: string; offset: number; y: number } | null;
  /** Where the reader of the note at `path` is in Read mode: the line of the last heading above the top of the visible text, 0 above the first. */
  reading: { path: string; line: number } | null;
  /** Kept in memory only, like everything here; the note opens with it in Write mode. */
  draft: Draft | null;
  noteTool: NoteTool;
  /** Untitled notes being named from their headings (`/Home/Garden/Untitled.md`), or queued to be: no Write mode for them until then. */
  renaming: string[];
  /** Why naming the note at `path` from its heading failed; shown with that note until Write mode starts on it again. */
  renameProblem: { path: string; text: string } | null;
  /** Set by the shell: takes the URL along after a note was named from its heading, if it still shows `from`. */
  follow: ((from: string, to: string) => void) | null;
  /** Find in the note: what is looked for, which match is the current one (0-based), and how many the note shows. */
  find: Find;

  /**
   * Opens a note in the focused pane: overlays close, its vault becomes active and its folder chain opens in the drawer.
   * In the other pane (`side`) it only shows there, in Read mode, once a writing one there has saved as Done does (not
   * if that fails). Write mode only while the other pane doesn't write. Either way it joins the recent notes.
   */
  openNote(path: string, mode?: Mode, side?: Side): void | Promise<void>;
  /** Opens a note in pane `side` and focuses it, the right one starting the split view if there is none. */
  openIn(side: Side, path: string, mode?: Mode): void;
  /** Sets a pane as it is (the editor's guard holding its note while a navigation waits): the focused one by `currentPath` and `mode`. */
  setPane(side: Side, pane: Pane & { path: string }): void;
  /** The focused pane's mode. Write mode in it first ends Write mode in the other pane (`endWrite`), and not if that fails. */
  setMode(mode: Mode): void | Promise<void>;
  /** Ends Write mode (Done, the Read toggle) in the pane that writes: only once the editor has saved everything. */
  endWrite(): Promise<void>;
  /** Focuses a pane; Write mode stays where it is. */
  focus(side: Side): void;
  /** Closes a pane, once a writing one has saved as Done does; the right one left alone becomes the only pane. */
  closePane(side: Side): Promise<void>;
  setScrollTarget(target: { path: string; line: number } | null): void;
  setBeforeRead(guard: BeforeRead | null, unsaved?: Unsaved | null): void;
  setCaretAt(caretAt: CaretAt | null): void;
  setLive(live: Live | null): void;
  setGoToLine(goToLine: ((line: number) => void) | null): void;
  /** Write mode in the focused pane, its caret at `at` (a click into its text); not if the other pane's save fails. */
  writeHere(at: { path: string; offset: number; y: number }): Promise<void>;
  setWriteAt(writeAt: { path: string; offset: number; y: number } | null): void;
  setReading(reading: { path: string; line: number } | null): void;
  setDraft(draft: Draft | null): void;
  /** A save of `path` the server refused for the level, by its `code`, at `at` (ms): the note is read only until the vault list read after it says. */
  refused: { path: string; code: RefusalCode; at: number } | null;
  setRefused(refused: { path: string; code: RefusalCode; at: number } | null): void;
  setOverlay(overlay: Overlay): void;
  /** Opens the upload sheet for `folder` (`/Work/projects`), with the files of a drop if there was one; `start`: sent at once. */
  openUpload(folder: string, dropped?: Picked[], start?: boolean): void;
  toggleFolder(path: string): void;
  /** Shows a folder (`/Work/projects`) in the drawer: it and every folder above it open, and it is the one marked. */
  revealFolder(path: string): void;
  folderRevealed(): void;
  setActiveVault(vault: string): void;
  setSearchQuery(query: string): void;
  setSearchMode(mode: SearchMode): void;
  setSearchTag(tag: string | null): void;
  /** Opens the search sheet on the notes with a tag (a tapped chip): the filter set, the query empty. */
  searchByTag(tag: string): void;
  setShowAgentHighlights(show: boolean): void;
  setNoteTool(tool: NoteTool): void;
  setRenaming(path: string, on: boolean): void;
  setRenameProblem(problem: { path: string; text: string } | null): void;
  setFollow(follow: ((from: string, to: string) => void) | null): void;
  setFind(find: Partial<Find>): void;
  /** A deleted note: it leaves the recent notes, and is no longer the open one. */
  forgetNote(path: string): void;
  /** A moved note (or folder, `from` ending in `/`): the open note, the recent notes and the open folders follow it. */
  moved(from: string, to: string): void;
  /**
   * A deleted vault: its notes leave the recent notes, the open note and the draft, a folder marked in it, a refusal and
   * a scroll target in it go, an upload sheet for it closes, and it is no longer active.
   */
  forgetVault(vault: string): void;
  /**
   * A session that ended with `vaults` shown (the next one starts with them hidden): what `forgetVault` forgets of them
   * except the draft, which opens again with its vault; and what was looked for (query, tag, find in the note) and the
   * note's tools, as these may name what was in them.
   */
  forgetShown(vaults: Iterable<string>): void;
  reset(): void;
}

const initial = {
  currentPath: null,
  mode: "read" as Mode,
  panes: { left: { path: null, mode: "read" }, right: null } as Panes,
  focused: "left" as Side,
  overlay: "none" as Overlay,
  expandedFolders: {},
  selectedFolder: null as string | null,
  revealing: false,
  activeVault: null,
  searchQuery: "",
  searchMode: "hybrid" as SearchMode,
  searchTag: null as string | null,
  showAgentHighlights: false,
  recentPaths: [],
  beforeRead: null,
  unsaved: null as Unsaved | null,
  caretAt: null as CaretAt | null,
  live: null as Live | null,
  goToLine: null as ((line: number) => void) | null,
  writeAt: null as { path: string; offset: number; y: number } | null,
  reading: null as { path: string; line: number } | null,
  draft: null,
  refused: null as { path: string; code: RefusalCode; at: number } | null,
  scrollTarget: null,
  upload: null,
  noteTool: null as NoteTool,
  renaming: [] as string[],
  renameProblem: null as { path: string; text: string } | null,
  follow: null as ((from: string, to: string) => void) | null,
  find: { query: "", index: 0, count: 0 } as Find,
};

/** `/Work/projects/deep` → `/Work/projects`, `/Work/projects/deep` (the vault's root is always open). */
function chain(folder: string) {
  const segments = folder.split("/");
  return segments.slice(2).map((_, i) => segments.slice(0, i + 3).join("/"));
}

const other = (side: Side): Side => (side === "left" ? "right" : "left");

/** The pane in Write mode, if one is. */
export const writingSide = (s: Pick<EditorState, "panes">): Side | null =>
  s.panes.left.mode === "write" ? "left" : s.panes.right?.mode === "write" ? "right" : null;

/**
 * The split view's pane that is the live preview (workspace spec §3): the one that isn't writing, on the note the other
 * writes; null without one.
 */
export const previewSide = (s: Pick<EditorState, "panes">): Side | null => {
  const writing = writingSide(s);
  if (!writing || !s.panes.right) return null;
  const side = other(writing);
  return s.panes[side]!.path === s.panes[writing]!.path ? side : null;
};

/** The note the one editor has open: the writing pane's, or else the focused one's. */
export const editedPath = (s: Pick<EditorState, "panes" | "currentPath">): string | null => {
  const side = writingSide(s);
  return side ? s.panes[side]!.path : s.currentPath;
};

/** The state with `pane` as the only one, focused, or none with null. */
function onlyPane(pane: Pane | null, mode: Mode): Partial<EditorState> {
  const left = pane ?? { path: null, mode };
  return { panes: { left, right: null }, focused: "left", currentPath: left.path, mode: left.mode };
}

/**
 * What keeps the focused pane in step with `currentPath` and `mode` after `s`: these are set in many places (the
 * editor's guard, a session's end, tests), and stay the one source of the focused pane. No note open leaves no pane.
 */
function inStep(s: EditorState): Partial<EditorState> {
  if (s.currentPath === null) return s.panes.left.path === null && s.panes.left.mode === s.mode && s.panes.right === null ? {} : onlyPane(null, s.mode);
  const pane = s.panes[s.focused];
  // A focus on a right pane there isn't goes to the only one, rather than opening one there.
  if (!pane) return onlyPane({ path: s.currentPath, mode: s.mode }, s.mode);
  if (pane.path === s.currentPath && pane.mode === s.mode) return {};
  return { panes: { ...s.panes, [s.focused]: { path: s.currentPath, mode: s.mode } } };
}

/** Every write to the store, its own actions' and `setState` from outside, keeps the focused pane in step (`inStep`). */
const focusedInStep =
  (config: StateCreator<EditorState>): StateCreator<EditorState> =>
  (set, get, api) => {
    const stepped = ((partial: EditorState | Partial<EditorState> | ((s: EditorState) => EditorState | Partial<EditorState>), replace?: boolean) => {
      const next = typeof partial === "function" ? partial(get()) : partial;
      const merged = (replace ? next : { ...get(), ...next }) as EditorState;
      set({ ...next, ...inStep(merged) } as EditorState, replace as false);
    }) as typeof set;
    api.setState = stepped;
    return config(stepped, get, api);
  };

export const useEditor = create<EditorState>()(focusedInStep((set, get) => ({
  ...initial,
  openNote: (path, mode = "read", side) => {
    const s = get();
    if (side && side !== s.focused) {
      const show = () =>
        set((s) => ({ panes: { ...s.panes, [side]: { path, mode: "read" } }, recentPaths: [path, ...s.recentPaths.filter((p) => p !== path)].slice(0, RECENT) }));
      const pane = s.panes[side];
      // A writing pane's text is saved first, as Done saves it; its own note again leaves it as it is.
      if (pane?.mode !== "write") return show();
      if (pane.path === path) return;
      return get()
        .endWrite()
        .then(() => {
          if (writingSide(get()) !== side) show();
        });
    }
    set((s) => {
      // One pane in Write mode at most: Write mode moves only with `setMode`, which saves first.
      const writes = (m: Mode): Mode => (writingSide(s) === other(s.focused) ? "read" : m);
      return {
        currentPath: path,
        selectedFolder: null,
        revealing: false,
        // While the note is being named, a save would make it again at its old path.
        mode: s.renaming.includes(path) ? "read" : writes(s.draft?.path === path ? "write" : mode),
        overlay: "none",
        noteTool: s.currentPath === path ? s.noteTool : null,
        find: s.currentPath === path ? s.find : initial.find,
        activeVault: vaultOf(path),
        expandedFolders: { ...s.expandedFolders, ...Object.fromEntries(chain(folderOf(path)).map((f) => [f, true])) },
        recentPaths: [path, ...s.recentPaths.filter((p) => p !== path)].slice(0, RECENT),
      };
    });
  },
  openIn: (side, path, mode = "read") => {
    if (side === "right" && !get().panes.right) set((s) => ({ panes: { ...s.panes, right: { path, mode: "read" } } }));
    get().focus(side);
    void get().openNote(path, mode);
  },
  setPane: (side, pane) =>
    set((s) => (side === s.focused ? { currentPath: pane.path, mode: pane.mode } : { panes: { ...s.panes, [side]: pane } })),
  setMode: (mode) => {
    const apply = (mode: Mode) =>
      set((s) =>
        mode !== "write"
          ? { mode }
          : s.currentPath !== null && s.renaming.includes(s.currentPath)
            ? {}
            : { mode, noteTool: null, renameProblem: s.renameProblem?.path === s.currentPath ? null : s.renameProblem },
      );
    // Answered at once unless it waits for the other pane's save: `act()` in a test takes a promise for async work.
    if (mode !== "write" || writingSide(get()) !== other(get().focused)) return apply(mode);
    // Meant for the note focused now: once the save is in, the focus or its note may have moved on.
    const { focused, currentPath } = get();
    return get()
      .endWrite()
      .then(() => {
        if (writingSide(get()) === null && get().focused === focused && get().currentPath === currentPath) apply(mode);
      });
  },
  setRefused: (refused) => set({ refused }),
  endWrite: async () => {
    const side = writingSide(get()) ?? get().focused;
    const guard = get().beforeRead;
    if (guard && !(await guard())) return;
    const s = get();
    if (side === s.focused) set({ mode: "read" });
    else if (s.panes[side]) set({ panes: { ...s.panes, [side]: { ...s.panes[side], mode: "read" } } });
  },
  focus: (side) =>
    set((s) => {
      const pane = s.panes[side];
      if (!pane || side === s.focused) return {};
      const same = pane.path === s.currentPath;
      return {
        focused: side,
        currentPath: pane.path,
        mode: pane.mode,
        noteTool: same ? s.noteTool : null,
        find: same ? s.find : initial.find,
        ...(pane.path ? { activeVault: vaultOf(pane.path) } : {}),
      };
    }),
  closePane: async (side) => {
    if (!get().panes.right) return;
    // The right pane, writing, takes the left's place as the left closes: the editor goes with its place, so it saves first too.
    const writing = writingSide(get());
    if (writing === side || (side === "left" && writing === "right")) {
      await get().endWrite();
      if (writingSide(get()) !== null) return;
    }
    set((s) => (s.panes.right ? onlyPane(side === "left" ? s.panes.right : s.panes.left, s.mode) : {}));
  },
  setBeforeRead: (beforeRead, unsaved = null) => set({ beforeRead, unsaved }),
  setCaretAt: (caretAt) => set({ caretAt }),
  setLive: (live) => set({ live }),
  setGoToLine: (goToLine) => set({ goToLine }),
  setWriteAt: (writeAt) => set({ writeAt }),
  writeHere: async (at) => {
    set({ writeAt: at });
    await get().setMode("write");
    // Not started (the other pane's save failed, the note being named): nothing is left for a later editor to take.
    if (get().writeAt === at && get().mode !== "write") set({ writeAt: null });
  },
  setReading: (reading) => set({ reading }),
  setDraft: (draft) => set({ draft }),
  setScrollTarget: (scrollTarget) => set({ scrollTarget }),
  setOverlay: (overlay) => set({ overlay }),
  openUpload: (folder, dropped, start = false) => set({ overlay: "upload", upload: { folder, dropped: dropped ?? null, start } }),
  toggleFolder: (path) => set((s) => ({ expandedFolders: { ...s.expandedFolders, [path]: !s.expandedFolders[path] } })),
  revealFolder: (path) =>
    set((s) => ({ selectedFolder: path, revealing: true, expandedFolders: { ...s.expandedFolders, ...Object.fromEntries(chain(path).map((f) => [f, true])) } })),
  folderRevealed: () => set({ revealing: false }),
  setActiveVault: (activeVault) => set({ activeVault }),
  setSearchQuery: (searchQuery) => set({ searchQuery }),
  setSearchMode: (searchMode) => set({ searchMode }),
  setSearchTag: (searchTag) => set({ searchTag }),
  searchByTag: (searchTag) => set({ searchTag, searchQuery: "", overlay: "search" }),
  setShowAgentHighlights: (showAgentHighlights) => set({ showAgentHighlights }),
  setNoteTool: (noteTool) => set({ noteTool }),
  setRenaming: (path, on) => set((s) => ({ renaming: on ? [...s.renaming.filter((p) => p !== path), path] : s.renaming.filter((p) => p !== path) })),
  setRenameProblem: (renameProblem) => set({ renameProblem }),
  setFollow: (follow) => set({ follow }),
  setFind: (find) => set((s) => ({ find: { ...s.find, ...find } })),
  forgetNote: (path) => set((s) => ({ recentPaths: s.recentPaths.filter((p) => p !== path), ...closing(s, (p) => p === path) })),
  moved: (from, to) =>
    set((s) => {
      const map = (p: string) => movedPath(p, from, to);
      // A folder (`/Work/projects`) moves with a folder move only.
      const mapFolder = (f: string) => (from.endsWith("/") ? movedPath(`${f}/`, from, to).replace(/\/$/, "") : f);
      const folders = Object.entries(s.expandedFolders).map(([f, open]) => [mapFolder(f), open]);
      // Where it went is shown open in the drawer, as opening a note there would.
      const target = chain(folderOf(to.replace(/\/$/, ""))).map((f) => [f, true]);
      return {
        currentPath: s.currentPath && map(s.currentPath),
        panes: { left: { ...s.panes.left, path: s.panes.left.path && map(s.panes.left.path) }, right: s.panes.right && { ...s.panes.right, path: map(s.panes.right.path) } },
        recentPaths: s.recentPaths.map(map),
        expandedFolders: Object.fromEntries([...folders, ...target]),
        draft: s.draft && { ...s.draft, path: map(s.draft.path) },
        upload: s.upload && { ...s.upload, folder: mapFolder(s.upload.folder) },
      };
    }),
  forgetVault: (vault) => set((s) => forgotten(s, vault, false)),
  forgetShown: (vaults) =>
    set((s) => {
      let left: EditorState | null = null;
      for (const vault of vaults) left = { ...(left ?? s), ...forgotten(left ?? s, vault, true) };
      return left ? { ...left, searchQuery: "", searchTag: null, find: initial.find, noteTool: null } : {};
    }),
  reset: () => set(initial),
})));

/**
 * The panes that stay once the notes `gone` names go: the one left alone becomes the only pane (focused, on the left);
 * with none, no note is open, the mode as it was.
 */
function closing(s: EditorState, gone: (path: string) => boolean): Partial<EditorState> {
  const left = s.panes.left.path !== null && gone(s.panes.left.path);
  const right = !!s.panes.right && gone(s.panes.right.path);
  if (!left && !right) return {};
  if (!s.panes.right) return { currentPath: null };
  if (left && right) return onlyPane(null, s.mode);
  return onlyPane(left ? s.panes.right : s.panes.left, s.mode);
}

/** What a vault's notes and folders leave in the store once it is forgotten; its draft too, unless `keepDraft`. */
function forgotten(s: EditorState, vault: string, keepDraft: boolean): Partial<EditorState> {
  const outside = (path: string | null | undefined) => path != null && vaultOf(path) !== vault;
  // An upload sheet for a folder of the vault would send its files to a vault that is gone.
  const upload = s.upload && !outside(s.upload.folder) ? { upload: null, overlay: s.overlay === "upload" ? ("none" as Overlay) : s.overlay } : {};
  return {
    ...upload,
    recentPaths: s.recentPaths.filter(outside),
    ...closing(s, (path) => !outside(path)),
    draft: keepDraft || outside(s.draft?.path) ? s.draft : null,
    selectedFolder: outside(s.selectedFolder) ? s.selectedFolder : null,
    refused: outside(s.refused?.path) ? s.refused : null,
    scrollTarget: outside(s.scrollTarget?.path) ? s.scrollTarget : null,
    live: outside(s.live?.path) ? s.live : null,
    writeAt: outside(s.writeAt?.path) ? s.writeAt : null,
    reading: outside(s.reading?.path) ? s.reading : null,
    renaming: s.renaming.filter(outside),
    renameProblem: outside(s.renameProblem?.path) ? s.renameProblem : null,
    activeVault: s.activeVault === vault ? null : s.activeVault,
    expandedFolders: Object.fromEntries(Object.entries(s.expandedFolders).filter(([folder]) => outside(folder))),
  };
}

/** Where `path` is after moving `from` to `to`: a note (`/W/a.md`) or a folder (`/W/a/`, with everything below it). */
export function movedPath(path: string, from: string, to: string) {
  if (!from.endsWith("/")) return path === from ? to : path;
  return path.startsWith(from) ? `${to.replace(/\/$/, "")}/${path.slice(from.length)}` : path;
}
