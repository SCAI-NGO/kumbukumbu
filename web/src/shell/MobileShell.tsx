// SPDX-License-Identifier: AGPL-3.0-or-later
import { lazy, Suspense, useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useLocation, useMatch, useNavigate } from "react-router-dom";
import { useShallow } from "zustand/react/shallow";
import { errorText } from "../api/error-text";
import { onRefusal } from "../api/client";
import { atLeast, groupVaults, levelOf, refusalOf, useCreateUntitled, useLevel, useNote, useNoteLoaded, useNoteMissing, useTree, useVaults, type VaultView } from "../api/notes";
import { besideOf, folderOf, noteUrl, paneUrl, pathFromRoute, vaultOf } from "../api/paths";
import { LoadBoundary, LoadFailedSheet } from "../components/AppError";
import { primaryButtonClass, secondaryButtonClass } from "../components/form";
import { PointMenu } from "../components/PointMenu";
import { topDialog } from "../hooks/useDialog";
import { useDesktop } from "../hooks/useDesktop";
import { useEdgeSwipe } from "../hooks/useEdgeSwipe";
import { useGraphColumnCollapsed } from "../hooks/useGraphColumn";
import { useGraphWide, useGraphWideSplit } from "../hooks/useGraphWide";
import { useSidePanelCollapsed } from "../hooks/useSidePanel";
import { useSidebarCollapsed } from "../hooks/useSidebarCollapsed";
import { useWide } from "../hooks/useWide";
import { useSyncSocket } from "../hooks/useSyncSocket";
import DiscreetPill, { DiscreetFrame, useRevealed } from "../discreet/DiscreetPill";
import { useTellOpenNote } from "../discreet/openNote";
import { useUnnamedVault } from "../discreet/errorText";
import { goneVault, useDiscreet, useGoneWithVaults } from "../discreet/state";
import GraphColumn, { graphColumnId } from "../graph/GraphColumn";
import { FindBar } from "../note/NoteTools";
import { DeleteNoteDialog } from "../note/NoteView";
import SidePanel, { sidePanelId } from "../note/SidePanel";
import InstallHint from "../update/InstallHint";
import OfflineInvite from "../offline/OfflineInvite";
import PasswordChangedCopies from "../offline/PasswordChangedCopies";
import OfflineBar from "../offline/OfflineBar";
import { NEEDS_CONNECTION } from "../offline/mode";
import UpdateBar from "../update/UpdateBar";
import { movedPath, useEditor, type Side } from "../state/editor";
import { useSession } from "../state/session";
import { usePaletteShortcut } from "../palette/usePaletteShortcut";
import DeleteFileDialog from "../attachments/DeleteFileDialog";
import { forgetLastNote, keepLastNote, lastNote, ordinary, settleStart, startSettled, treeHas } from "./lastNote";
import Dock from "./Dock";
import type { MoveSubject } from "./MoveDialog";
import NewFolderDialog from "./NewFolderDialog";
import NewVaultDialog from "./NewVaultDialog";
import SkillsNotice from "./SkillsNotice";
import SharedBanner, { MemberNotices } from "./SharedBanner";
import StorageNotice from "./StorageNotice";
import TopBar, { graphToggleId, sidePanelToggleId, sidebarToggleId } from "./TopBar";
import TreeDrawer, { sidebarId, type DeleteSubject, type VaultSheet } from "./TreeDrawer";
import { useNoteActions } from "./useNoteActions";
import { useNoteMenuKey } from "./useNoteMenuKey";
import { startWrite } from "./ModeToggle";
import { useOpenNote } from "./openInPane";
import Pane, { useNoteAccess } from "./Pane";
import { usePaneUrl } from "./usePaneUrl";
import DateDialog from "../calendar/DateDialog";
import { useFlagging } from "../flags/FlagMenu";
import { ShellRedirect } from "../windows/context";

// Loaded on first use, which keeps the main bundle small: upload, rename and move are seldom used, and search has
// its own sheet over the note.
const UploadSheet = lazy(() => import("../upload/UploadSheet"));
// Its name field is focused on open, and iOS raises the keyboard only for a focus inside the tap: fetched ahead, and
// once loaded rendered directly (React.lazy would suspend on first render even then, mounting it in a later task).
let LoadedMoveDialog: (typeof import("./MoveDialog"))["default"] | null = null;
const loadMoveDialog = () =>
  import("./MoveDialog").then((m) => {
    LoadedMoveDialog = m.default;
    return m;
  });
const MoveDialog = lazy(loadMoveDialog);
const TrashSheet = lazy(() => import("./TrashSheet"));
const CheckpointsSheet = lazy(() => import("./CheckpointsSheet"));
const ActivitySheet = lazy(() => import("./ActivitySheet"));
const ArchiveSheet = lazy(() => import("./ArchiveSheet"));
const loadSearch = () => import("../search/SearchSheet");
const SearchSheet = lazy(loadSearch);
const Palette = lazy(() => import("../palette/Palette"));
const GraphDialog = lazy(() => import("../graph/GraphDialog"));

/** The search sheet didn't load: the dock's stand-in field lets go of the focus, so the keyboard goes down. */
function SearchLoadFailed({ onClose }: { onClose(): void }) {
  useLayoutEffect(() => {
    const active = document.activeElement;
    if (active instanceof HTMLElement && active.dataset.focusReturn) active.blur();
  }, []);
  return <LoadFailedSheet onClose={onClose} />;
}

/** `/` without a last note, or asked for ("All vaults" in the switcher): the vaults, or the way to the first one. */
function VaultOverview({ onNewVault }: { onNewVault(): void }) {
  const vaults = useVaults();
  // Why the shell came here, when a vault went from under a window (`useLeaveGoneVault`).
  const notice = (useLocation().state as { notice?: string } | null)?.notice;
  const activeVault = useEditor((s) => s.activeVault);
  const open = (vault: string) => {
    useEditor.getState().setActiveVault(vault);
    useEditor.getState().setOverlay("tree");
  };

  if (vaults.isPending) return <div className="flex-1" />;
  if (vaults.isError) return <p className="flex-1 p-[22px] text-text-body">{errorText(vaults.error)}</p>;
  if (vaults.data.length === 0) {
    return (
      <div className="flex flex-1 flex-col justify-center gap-4 px-[22px]">
        <h1 className="text-[28px] leading-tight font-bold text-text">No vault yet</h1>
        <p className="text-text-body">A vault holds your notes, and agents get their keys to it. Start with one.</p>
        <button type="button" onClick={onNewVault} className={primaryButtonClass}>
          Create your first vault
        </button>
      </div>
    );
  }
  const { own, shared } = groupVaults(vaults.data);
  const item = (v: VaultView) => (
    <li key={v.name}>
      <button
        type="button"
        aria-current={v.name === activeVault ? "true" : undefined}
        onClick={() => open(v.name)}
        className={`flex min-h-12 w-full items-center justify-between gap-3 rounded-[12px] border px-4 text-left focus-visible:outline-2 focus-visible:outline-accent ${
          v.name === activeVault ? "border-accent bg-accent/15 font-medium text-accent-text" : "border-line bg-surface text-text"
        }`}
      >
        <span className="truncate font-medium">{v.name}</span>
        <span className="flex-none font-mono text-[12px] text-dim">
          {v.pending ? "Being re-encrypted" : levelOf(v) === "read" ? "Read only" : v.notes === 1 ? "1 note" : `${v.notes} notes`}
        </span>
      </button>
    </li>
  );
  return (
    <div className="min-h-0 flex-1 overflow-y-auto px-[22px] pt-[22px] pb-7">
      <h1 className="text-[28px] leading-tight font-bold text-text">Your vaults</h1>
      {notice && (
        <p role="status" className="mt-2 text-text-body">
          {notice}
        </p>
      )}
      <ul aria-label="Your vaults" className="mt-4 flex flex-col gap-2">
        {own.map(item)}
      </ul>
      <button type="button" onClick={onNewVault} className={`${secondaryButtonClass} mt-4`}>
        New vault
      </button>
      {shared.length > 0 && (
        <>
          <h2 className="mt-8 text-[22px] leading-tight font-bold text-text">Shared with me</h2>
          {shared.map(({ key, owner, vaults: list }) => (
            <section key={key} className="mt-4">
              {/* The owner's display name, as she typed it: quoted by the server, so it reads as a name and nothing more. */}
              <h3 className="truncate font-mono text-[12px] tracking-[0.08em] text-muted uppercase">{owner}</h3>
              <ul aria-label={`Shared by ${owner}`} className="mt-2 flex flex-col gap-2">
                {list.map(item)}
              </ul>
            </section>
          ))}
        </>
      )}
    </div>
  );
}

/**
 * The side panel beside the open note. While it is written: the editor's text and caret, and a heading clicked takes
 * the caret there. Else the note as saved and where its reader is, and a heading clicked comes into view as from the
 * outline sheet.
 */
function NoteSidePanel({ path, hidden }: { path: string; hidden: boolean }) {
  const note = useNote(path);
  const live = useEditor((s) => (s.live?.path === path ? s.live : null));
  const reading = useEditor((s) => (s.reading?.path === path ? s.reading.line : 0));
  const jump = (line: number) => {
    const { mode, goToLine, setScrollTarget } = useEditor.getState();
    if (mode === "write") goToLine?.(line);
    else setScrollTarget({ path, line });
  };
  return <SidePanel path={path} hidden={hidden} text={live?.text ?? note.data?.content ?? ""} currentLine={live?.caretLine ?? reading} onJump={jump} />;
}

/** The signed-in app: top bar, the note (or the vaults), the dock, and the drawer and sheets over them. */
export default function MobileShell() {
  const match = useMatch("/notes/:vault/*");
  const path = match ? pathFromRoute(match.params.vault!, match.params["*"]!) : null;
  const location = useLocation();
  // The right pane's note in the split view (`beside`): beside the left one from 1280 px on, else shown alone while it
  // has the focus (workspace spec, Widths). The URL says which notes are open, the store which pane is focused.
  const beside = path ? besideOf(location.search) : null;
  const wide = useWide();
  const focusedSide = useEditor((s) => s.focused);
  const split = wide && !!beside;
  // The focused pane's note: what the top bar, the dock, the palette's Note group and the side panel are about.
  const focusedPath = beside && focusedSide === "right" ? beside : path;
  // The discreet note on its way out: the route still names it until the navigation is in, and nothing shows it.
  const gone = useGoneWithVaults(focusedPath);
  // A missing note whose vault the list doesn't name (a hidden one's bookmark after a reload): its path isn't shown either.
  const unnamed = useUnnamedVault(focusedPath);
  const missing = useNoteMissing(focusedPath);
  // Picked, so that the editor's text published as it is typed (`live`) doesn't render the shell again on every key.
  const { recentPaths, overlay, mode, activeVault, upload, noteTool, openUpload, setOverlay, setActiveVault } = useEditor(
    useShallow(({ recentPaths, overlay, mode, activeVault, upload, noteTool, openUpload, setOverlay, setActiveVault }) => ({
      recentPaths,
      overlay,
      mode,
      activeVault,
      upload,
      noteTool,
      openUpload,
      setOverlay,
      setActiveVault,
    })),
  );
  const vaults = useVaults();
  const dockLevel = useLevel(activeVault);
  const createUntitled = useCreateUntitled();
  const navigate = useNavigate();
  const overview = (location.state as { overview?: boolean } | null)?.overview === true;
  const userId = useSession((s) => s.user?.id ?? null);
  const discreetNames = useDiscreet((s) => s.names);
  // The last note of an ordinary vault is kept for the next start, once it has loaded; a discreet one never is, and
  // while one is open the kept path stays the last ordinary note.
  const loaded = useNoteLoaded(path);
  useEffect(() => {
    if (userId && path && loaded && ordinary(vaults.data, discreetNames, vaultOf(path))) keepLastNote(userId, path);
  }, [userId, path, loaded, vaults.data, discreetNames]);
  // It goes once its vault is no longer an ordinary one: marked discreet (here, or in another session: the list then
  // marks it or, hidden, leaves it out), removed, or no longer shared.
  useEffect(() => {
    if (!userId || !vaults.data) return;
    const kept = lastNote(userId);
    if (kept && !ordinary(vaults.data, discreetNames, vaultOf(kept))) forgetLastNote(userId);
  }, [userId, vaults.data, discreetNames]);
  // On start only, with no note in the address, and not when a vault was chosen before the shell was shown (Share's
  // "Open"). Read once per page load: once a note was shown or the start was decided, `/` is the vaults.
  const [chosen] = useState(() => useEditor.getState().activeVault !== null);
  const kept = userId && !startSettled(userId) && !chosen && !path && !overview && !recentPaths.length && vaults.data ? lastNote(userId) : null;
  const keptVault = kept !== null && ordinary(vaults.data, discreetNames, vaultOf(kept)) ? vaultOf(kept) : null;
  const keptTree = useTree(keptVault);
  const keptThere = !!keptVault && !!keptTree.data && treeHas(keptTree.data.root, kept!);
  // Settled with a note shown, or with the vault list in and nothing to open.
  useEffect(() => {
    if (path || (vaults.data && !keptVault)) settleStart(userId);
  }, [path, vaults.data, keptVault, userId]);
  // A note that is gone opens its vault's root and is forgotten; with its tree unreadable for now, that vault's root
  // opens too, and the note stays kept for the next start.
  useEffect(() => {
    if (!keptVault || !(keptTree.data || keptTree.isError) || keptThere) return;
    if (keptTree.data) forgetLastNote(userId);
    settleStart(userId);
    setActiveVault(keptVault);
  }, [keptVault, keptTree.data, keptTree.isError, keptThere, userId, setActiveVault]);
  // Until the tree says, the vaults aren't shown: they would flash before the note opens.
  const keptPending = !!keptVault && !keptThere && keptTree.isPending;
  useSyncSocket();
  useTellOpenNote();
  const [vaultDialog, setVaultDialog] = useState(false);
  /** The folder a new folder goes into, while its dialog is open. */
  const [folderDialog, setFolderDialog] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<DeleteSubject | null>(null);
  const [moving, setMoving] = useState<MoveSubject | null>(null);
  /** The note whose date the date dialog gives or changes. */
  const [dating, setDating] = useState<string | null>(null);
  const [vaultSheet, setVaultSheet] = useState<VaultSheet | null>(null);
  // A sheet or dialog about a vault hidden again goes with it: its title and its answers name the note or the vault.
  useEffect(
    () =>
      useDiscreet.subscribe((s, previous) => {
        if (s.gone === previous.gone && s.names === previous.names) return;
        const gonePath = (path: string | null | undefined) => !!path && goneVault(s, vaultOf(path));
        setFolderDialog((folder) => (gonePath(folder) ? null : folder));
        setDeleting((subject) => (gonePath(subject?.path) ? null : subject));
        setMoving((subject) => (gonePath(subject?.path) ? null : subject));
        setDating((path) => (gonePath(path) ? null : path));
        setVaultSheet((sheet) => (sheet && goneVault(s, sheet.vault) ? null : sheet));
      }),
    [],
  );
  // The drawer opens over the note only, never over a sheet or dialog; a swipe closes it only while it is on top.
  const swipe = useEdgeSwipe(
    overlay === "tree",
    () => useEditor.getState().overlay === "none" && !topDialog() && setOverlay("tree"),
    () => topDialog()?.dataset.drawer !== undefined && setOverlay("none"),
  );

  // From the desktop breakpoint on, the tree is docked beside the note: collapsed or expanded, never an overlay.
  const docked = useDesktop();
  const discreetShown = useRevealed();
  const [collapsed, setCollapsed] = useSidebarCollapsed();
  const [revealed, setRevealed] = useState(0);
  const toggleSidebar = useCallback(() => {
    // Hiding the tree that holds the focus would drop it to the page: it goes to the button that brings the tree back.
    if (!collapsed && document.getElementById(sidebarId)?.contains(document.activeElement)) document.getElementById(sidebarToggleId)?.focus();
    setCollapsed(!collapsed);
  }, [collapsed, setCollapsed]);

  // Docked, whatever opens the drawer (the title, the dock's Files, a vault picked or made) shows the sidebar
  // instead and takes the focus there, as the drawer would.
  useEffect(() => {
    if (!docked || overlay !== "tree") return;
    setOverlay("none");
    setCollapsed(false);
    setRevealed((n) => n + 1);
  }, [docked, overlay, setOverlay, setCollapsed]);
  useEffect(() => {
    if (!revealed) return;
    const sidebar = document.getElementById(sidebarId);
    (sidebar?.querySelector<HTMLElement>('[aria-current="page"]') ?? sidebar?.querySelector<HTMLElement>("[data-tree-row]"))?.focus();
  }, [revealed]);

  // Ctrl+\ (Cmd+\ on a Mac), as in other editors with a sidebar; the note's editor has no shortcut of its own for it.
  // `code` too, for layouts where `\` needs AltGr. Not while a dialog is open: the sidebar is behind it.
  useEffect(() => {
    if (!docked) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey || e.defaultPrevented) return;
      if (e.key !== "\\" && e.code !== "Backslash") return;
      if (topDialog()) return;
      e.preventDefault();
      toggleSidebar();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [docked, toggleSidebar]);

  // From 1280 px on, the side panel sits beside the open note (the focused pane's), collapsed or expanded: its links and
  // its outline are there and nowhere else. Not for a note on its way out, nor one whose path isn't shown (as for the top bar).
  const [panelCollapsed, setPanelCollapsed] = useSidePanelCollapsed();
  const shownNote = gone || (unnamed && missing) ? null : focusedPath;
  const sidePanel = wide && !!shownNote;
  const toggleSidePanel = useCallback(() => {
    if (!panelCollapsed && document.getElementById(sidePanelId)?.contains(document.activeElement)) document.getElementById(sidePanelToggleId)?.focus();
    setPanelCollapsed(!panelCollapsed);
  }, [panelCollapsed, setPanelCollapsed]);

  // Ctrl+Shift+\ (Cmd+Shift+\ on a Mac), the tree's shortcut mirrored. Shift+\ types `|` on many layouts, and `code`
  // goes for layouts where `\` needs AltGr. Not while a dialog is open: the panel is behind it.
  useEffect(() => {
    if (!sidePanel) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || e.altKey || !e.shiftKey || e.defaultPrevented) return;
      if (e.key !== "\\" && e.key !== "|" && e.code !== "Backslash") return;
      if (topDialog()) return;
      e.preventDefault();
      toggleSidePanel();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [sidePanel, toggleSidePanel]);

  // From 1920 px on (2880 px in the split view, two panes beside each other), the graph column sits after the side
  // panel: the focused pane's note in its graph, the other pane's named in its foot. Where no column fits, the graph is
  // a dialog; on a phone in read mode only, since in Write mode it would fight the keyboard and the caret.
  const graphWide = useGraphWide();
  const graphWideSplit = useGraphWideSplit();
  const [graphCollapsed, setGraphCollapsed] = useGraphColumnCollapsed();
  const graphColumn = !!shownNote && (split ? graphWideSplit : graphWide);
  // The other pane's note, for its ring and its distance line.
  const graphOther = split ? (focusedSide === "right" ? path : beside) : null;
  // The button and the shortcut: where no column fits, and on a phone in read mode only.
  const graphDialogAllowed = !!shownNote && !graphColumn && (docked || mode === "read");
  // The note whose graph the dialog shows: the one shown, or another one from its menu in the tree.
  const [graphDialog, setGraphDialog] = useState<string | null>(null);
  // Gone once the column fits for its note, not back when the window narrows again.
  useEffect(() => {
    if (graphDialog !== null && graphColumn && graphDialog === shownNote) setGraphDialog(null);
  }, [graphDialog, graphColumn, shownNote]);
  const toggleGraph = useCallback(() => {
    if (!graphCollapsed && document.getElementById(graphColumnId)?.contains(document.activeElement)) document.getElementById(graphToggleId)?.focus();
    setGraphCollapsed(!graphCollapsed);
  }, [graphCollapsed, setGraphCollapsed]);

  // Open graph… in the note's menus: the column where it shows that note, else the dialog; the tree's drawer makes way.
  const openGraph = useCallback(
    (note: string) => {
      if (graphColumn && note === shownNote) setGraphCollapsed(false);
      else {
        setGraphDialog(note);
        if (!docked) useEditor.getState().setOverlay("none");
      }
    },
    [graphColumn, shownNote, setGraphCollapsed, docked],
  );

  // Ctrl+Shift+G (Cmd+Shift+G on a Mac); `code` too, for layouts where G is elsewhere. Not while a dialog is open, the
  // graph's own included: Escape closes that.
  useEffect(() => {
    if (!graphColumn && !graphDialogAllowed) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || e.altKey || !e.shiftKey || e.defaultPrevented) return;
      if (e.key !== "G" && e.key !== "g" && e.code !== "KeyG") return;
      if (topDialog()) return;
      e.preventDefault();
      if (graphColumn) toggleGraph();
      else setGraphDialog(shownNote);
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [graphColumn, graphDialogAllowed, toggleGraph, shownNote]);

  usePaletteShortcut();

  // The URL says which notes are open, the left pane's and the right one's (`beside`); the store follows it when it
  // changes (a link, back and forward, a reload). Only then: after signing out, the store is empty while this URL is
  // still shown for a moment. Nor while the discreet vaults are being hidden: the note's URL is on its way out.
  useEffect(() => {
    if (!path || useDiscreet.getState().hiding) return;
    const { panes, openNote, closePane } = useEditor.getState();
    if (path !== panes.left.path) openNote(path, "read", "left");
    if (beside === (panes.right?.path ?? null)) return;
    if (beside) openNote(beside, "read", "right");
    else void closePane("right");
  }, [path, beside]);

  // A note named from its heading: the URL goes along, in the pane that shows it, unless the user has gone on to another
  // note meanwhile; the other pane stays.
  const shownPath = useRef({ path, beside });
  shownPath.current = { path, beside };
  useEffect(() => {
    const follow = (from: string, to: string) => {
      const { path, beside } = shownPath.current;
      if (path !== from && beside !== from) return;
      navigate(paneUrl(movedPath(path!, from, to), beside && movedPath(beside, from, to)), { replace: true });
    };
    useEditor.getState().setFollow(follow);
    return () => {
      if (useEditor.getState().follow === follow) useEditor.getState().setFollow(null);
    };
  }, [navigate]);
  const renaming = useEditor((s) => !!focusedPath && s.renaming.includes(focusedPath));
  const renameProblem = useEditor((s) => (s.renameProblem && s.renameProblem.path === s.currentPath ? s.renameProblem.text : null));

  // Fetched ahead, so the first tap on Search finds the sheet loaded, or nearly (see the dock's stand-in field), and
  // Rename or move finds its dialog loaded.
  useEffect(() => {
    void loadSearch().catch(() => {});
    void loadMoveDialog().catch(() => {});
  }, []);

  // Skills sorts before Welcome, where a new user should start: the Skills vault only when there is no other.
  // The store's own value, not the render's: the effect above may have just opened a note in another vault (a deep link).
  useEffect(() => {
    if (!useEditor.getState().activeVault && vaults.data?.length) setActiveVault(keptVault ?? (vaults.data.find((v) => v.kind !== "skills") ?? vaults.data[0]).name);
  }, [activeVault, vaults.data, setActiveVault, keptVault]);

  // A note the caller can't change (any more: her role was lowered) is only read (`useNoteAccess`; each pane ends Write
  // mode in it). The top bar's Write is the focused pane's, the split view's start the left one's.
  const { held, readOnly: readOnlyNote, needsConnection } = useNoteAccess(focusedPath);
  const leftAccess = useNoteAccess(path);
  const leftRenaming = useEditor((s) => !!path && s.renaming.includes(path));
  // Nothing of a note on its way out with its vault hidden again, a refusal's sentence naming it neither.
  const refusal = gone ? null : held ?? (focusedPath && vaults.data ? refusalOf(vaults.data.find((v) => v.name === vaultOf(focusedPath)), focusedPath) : null);

  // A write the server refused for the level (a role lowered, the owner deactivated, the vault closing): the list is
  // read again, and the write actions and Write mode follow it.
  const queryClient = useQueryClient();
  useEffect(() => onRefusal(() => void queryClient.invalidateQueries({ queryKey: ["vaults"] })), [queryClient]);

  // The note's ⋯ and the tree's share one flag menu, and its message outlives both.
  const flagging = useFlagging();
  const noteActions = useNoteActions(focusedPath, { onMove: setMoving, onDelete: setDeleting, onDate: setDating, onFlag: flagging.open, onGraph: shownNote && (docked || mode === "read") ? openGraph : undefined });
  const noteMenu = useNoteMenuKey(focusedPath);
  const openInPane = useOpenNote();
  const paneUrlOf = usePaneUrl();

  // Split view: the left pane in Write mode where its note may be written, the right one the same note (its live
  // preview); the focus stays in the left pane's editor. Again, the right pane closes, saving first if it writes.
  const toggleSplit = () => {
    if (!path) return;
    if (beside) return void paneUrlOf.close("right");
    const { focus, openNote, mode } = useEditor.getState();
    focus("left");
    void openNote(path, "read", "right");
    navigate(paneUrl(path, path));
    if (!leftAccess.readOnly && !leftAccess.needsConnection && !leftRenaming && mode !== "write") startWrite();
  };

  // Ctrl+Alt+← / → focus the left or the right pane, and the focus goes into it: its editor where it writes. Ctrl on a
  // Mac too, never Cmd: Cmd+Option+← / → switch the browser's tabs there. Not over a dialog (the palette's too).
  useEffect(() => {
    if (!split) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (!e.ctrlKey || e.metaKey || !e.altKey || e.shiftKey || e.defaultPrevented) return;
      const side: Side | null = e.key === "ArrowLeft" ? "left" : e.key === "ArrowRight" ? "right" : null;
      if (!side || topDialog()) return;
      e.preventDefault();
      useEditor.getState().focus(side);
      const writes = useEditor.getState().panes[side]?.mode === "write";
      (writes ? document.getElementById("markdown-source") : document.querySelector<HTMLElement>(`[data-pane="${side}"]`))?.focus({ preventScroll: true });
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [split]);
  if (!path && recentPaths.length && !overview) return <ShellRedirect to={noteUrl(recentPaths[0])} />;
  if (keptThere) return <ShellRedirect to={noteUrl(kept!)} />;

  /** A new note in `folder` (`/Work/projects`), opened in Write mode. */
  const newNote = (folder: string) =>
    createUntitled.mutate(folder, {
      onSuccess: (created) => openInPane(created, { mode: "write" }),
    });
  // From the dock: the open note's folder, or the root of the vault the drawer shows.
  const dockFolder = activeVault && (focusedPath && vaultOf(focusedPath) === activeVault ? folderOf(focusedPath) : `/${activeVault}`);
  // Below `write` the dock makes nothing, and the note can't be changed; the board's definition wants `maintain`.
  const dockWrites = !!dockFolder && atLeast(dockLevel, "write");
  // On a phone the tree is a drawer: the frame and the pill go around and under the top bar instead (spec §3.7).
  const framed = discreetShown && !docked;
  /** What a pane needs of the shell: its dialogs, Split view in the left one's bar from 1280 px on, and its ✕. */
  const paneProps = (side: Side) => ({
    onMove: (path: string) => setMoving({ kind: "note", path }),
    onDelete: (path: string) => setDeleting({ kind: "note", path }),
    onSplit: wide && side === "left" ? { on: split, toggle: toggleSplit } : null,
    onClose: () => void paneUrlOf.close(side),
  });
  // Under the note, or under the focused pane in the split view.
  const footer = (
    <>
      {createUntitled.isError && (
        <p role="alert" className="mx-3 mb-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
          {errorText(createUntitled.error)}
        </p>
      )}
      {renameProblem && (
        <p role="alert" className="mx-3 mb-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
          {renameProblem}
        </p>
      )}
      {noteActions.notices}
      {path && mode === "read" && noteTool === "find" && <FindBar />}
      {(!path || mode === "read") && !(path && noteTool === "find") && (
        <Dock
          phone={!docked}
          newNote={dockFolder && dockWrites ? () => newNote(dockFolder) : null}
          newFolder={dockFolder && dockWrites ? () => setFolderDialog(dockFolder) : null}
          upload={dockFolder && dockWrites ? () => openUpload(dockFolder) : null}
          noteActions={path ? noteActions.items : null}
        />
      )}
    </>
  );
  const treeProps = { onNewNote: newNote, onNewFolder: setFolderDialog, onNewVault: () => setVaultDialog(true), onMove: setMoving, onDelete: setDeleting, onVaultSheet: setVaultSheet, onDate: setDating, onFlag: flagging.open, onGraph: openGraph };

  return (
    <div data-testid="shell" {...swipe} className="relative flex h-dvh flex-col overflow-hidden bg-bg">
      {framed && <DiscreetFrame />}
      {/* No Write mode while the note is being named: a save would make it again at its old path. */}
      <TopBar
        path={shownNote}
        writable={!readOnlyNote && !renaming && !needsConnection}
        refusal={needsConnection ? NEEDS_CONNECTION : refusal}
        sidebar={docked ? { expanded: !collapsed, toggle: toggleSidebar } : null}
        sidePanel={sidePanel ? { expanded: !panelCollapsed, toggle: toggleSidePanel } : null}
        graph={
          graphColumn
            ? { expanded: !graphCollapsed, toggle: toggleGraph, dialog: false }
            : graphDialogAllowed
              ? { expanded: false, toggle: () => setGraphDialog(shownNote), dialog: true }
              : null
        }
      />
      {framed && (
        <div className="flex-none px-3 pt-2">
          <DiscreetPill />
        </div>
      )}
      <OfflineBar inShell />
      <UpdateBar />
      <MemberNotices />
      <StorageNotice />
      <SkillsNotice />
      <InstallHint />
      <OfflineInvite />
      <PasswordChangedCopies />
      {/* In the tab order the docked tree comes before the note, as it is shown: top bar, tree, note, dock. */}
      <div className="flex min-h-0 flex-1">
        {docked && <TreeDrawer docked hidden={collapsed} {...treeProps} />}
        <div className="flex min-w-0 flex-1 flex-col">
          <div className="pt-2 empty:hidden">
            <SharedBanner vault={focusedPath && !gone ? vaultOf(focusedPath) : activeVault} refusal={refusal} />
          </div>
          {/* One element and a pane by its key, whether split or not: the editor in a pane stays mounted as the split
              starts or ends and as the window crosses 1280 px, with its caret and what it holds unsaved. Below 1280 px
              the other pane is hidden, not gone: where it writes, its text stays with it. */}
          <main className={`flex min-h-0 flex-1 ${split ? "" : "flex-col"}`}>
            {path ? (
              (beside ? (["left", "right"] as const) : (["left"] as const)).map((side) => (
                <Pane
                  key={side}
                  side={side}
                  path={side === "left" ? path : beside!}
                  split={split}
                  hidden={!split && !!beside && side !== focusedSide}
                  wide={wide}
                  panel={sidePanel}
                  {...paneProps(side)}
                >
                  {split && footer}
                </Pane>
              ))
            ) : keptPending ? (
              <div className="flex-1" />
            ) : (
              <VaultOverview onNewVault={() => setVaultDialog(true)} />
            )}
          </main>
          {!split && footer}
        </div>
        {sidePanel && <NoteSidePanel path={shownNote!} hidden={panelCollapsed} />}
        {graphColumn && <GraphColumn path={shownNote!} otherPath={graphOther} hidden={graphCollapsed} />}
      </div>

      {!docked && overlay === "tree" && <TreeDrawer {...treeProps} />}
      {graphDialog !== null && (
        <LoadBoundary fallback={<LoadFailedSheet onClose={() => setGraphDialog(null)} />}>
          <Suspense>
            <GraphDialog path={graphDialog} otherPath={graphDialog === shownNote ? graphOther : null} onClose={() => setGraphDialog(null)} />
          </Suspense>
        </LoadBoundary>
      )}
      {overlay === "search" && (
        <LoadBoundary fallback={<SearchLoadFailed onClose={() => setOverlay("none")} />}>
          <Suspense>
            <SearchSheet />
          </Suspense>
        </LoadBoundary>
      )}
      {overlay === "palette" && (
        <LoadBoundary fallback={<LoadFailedSheet onClose={() => setOverlay("none")} />}>
          <Suspense>
            <Palette note={focusedPath && !gone ? { path: focusedPath, items: noteActions.items } : null} onSplit={wide && path ? toggleSplit : null} />
          </Suspense>
        </LoadBoundary>
      )}
      {overlay === "upload" && upload && (
        <LoadBoundary fallback={<LoadFailedSheet onClose={() => setOverlay("none")} />}>
          <Suspense>
            <UploadSheet folder={upload.folder} dropped={upload.dropped} start={upload.start} />
          </Suspense>
        </LoadBoundary>
      )}
      {moving && (
        <LoadBoundary fallback={<LoadFailedSheet onClose={() => setMoving(null)} />}>
          {LoadedMoveDialog ? (
            <LoadedMoveDialog subject={moving} onClose={() => setMoving(null)} />
          ) : (
            <Suspense>
              <MoveDialog subject={moving} onClose={() => setMoving(null)} />
            </Suspense>
          )}
        </LoadBoundary>
      )}
      {/* Outside the dock, which Write mode hides. */}
      {noteMenu && <PointMenu label="Note actions" items={noteActions.items} at={noteMenu.at} onClose={noteMenu.close} />}
      {noteActions.dialogs}
      {vaultSheet && (
        <LoadBoundary fallback={<LoadFailedSheet onClose={() => setVaultSheet(null)} />}>
          <Suspense>
            {vaultSheet.kind === "trash" ? (
              <TrashSheet vault={vaultSheet.vault} onClose={() => setVaultSheet(null)} />
            ) : vaultSheet.kind === "activity" ? (
              <ActivitySheet vault={vaultSheet.vault} onClose={() => setVaultSheet(null)} onOpenTrash={() => setVaultSheet({ kind: "trash", vault: vaultSheet.vault })} />
            ) : vaultSheet.kind === "archive" ? (
              <ArchiveSheet vault={vaultSheet.vault} onClose={() => setVaultSheet(null)} />
            ) : (
              <CheckpointsSheet vault={vaultSheet.vault} onClose={() => setVaultSheet(null)} />
            )}
          </Suspense>
        </LoadBoundary>
      )}
      {deleting?.kind === "note" && <DeleteNoteDialog path={deleting.path} open={deleting.path === path || deleting.path === beside} onClose={() => setDeleting(null)} />}
      {deleting?.kind === "attachment" && (
        <DeleteFileDialog path={deleting.path} version={deleting.version} onDeleted={() => setDeleting(null)} onClose={() => setDeleting(null)} />
      )}
      {dating && <DateDialog path={dating} onClose={() => setDating(null)} />}
      {flagging.element}
      {folderDialog && <NewFolderDialog parent={folderDialog} onClose={() => setFolderDialog(null)} />}
      {vaultDialog && (
        <NewVaultDialog
          onClose={() => setVaultDialog(false)}
          onCreated={(name) => {
            setVaultDialog(false);
            setActiveVault(name);
            setOverlay("tree");
          }}
        />
      )}
    </div>
  );
}
