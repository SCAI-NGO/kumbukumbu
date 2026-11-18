// SPDX-License-Identifier: AGPL-3.0-or-later
import { lazy, Suspense, useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useLocation, useMatch, useNavigate } from "react-router-dom";
import { useShallow } from "zustand/react/shallow";
import { errorText } from "../api/error-text";
import { onRefusal } from "../api/client";
import { atLeast, groupVaults, levelOf, mayChange, refusalOf, refusalSentence, useCreateUntitled, useLevel, useNote, useNoteLoaded, useNoteMissing, useTree, useVaults, type VaultView } from "../api/notes";
import { folderOf, noteUrl, pathFromRoute, vaultOf } from "../api/paths";
import { LoadBoundary, LoadFailedSheet } from "../components/AppError";
import { primaryButtonClass, secondaryButtonClass } from "../components/form";
import { PointMenu } from "../components/PointMenu";
import { topDialog } from "../hooks/useDialog";
import { useDesktop } from "../hooks/useDesktop";
import { useEdgeSwipe } from "../hooks/useEdgeSwipe";
import { useSidePanelCollapsed } from "../hooks/useSidePanel";
import { useSidebarCollapsed } from "../hooks/useSidebarCollapsed";
import { useWide } from "../hooks/useWide";
import { useSyncSocket } from "../hooks/useSyncSocket";
import DiscreetPill, { DiscreetFrame, useRevealed } from "../discreet/DiscreetPill";
import { useTellOpenNote } from "../discreet/openNote";
import { NO_SUCH_NOTE, useUnnamedVault } from "../discreet/errorText";
import { goneVault, useDiscreet, useGoneWithVaults } from "../discreet/state";
import { FindBar } from "../note/NoteTools";
import NoteView, { DeleteNoteDialog } from "../note/NoteView";
import SidePanel, { sidePanelId } from "../note/SidePanel";
import NoteBar from "./NoteBar";
import InstallHint from "../update/InstallHint";
import UpdateBar from "../update/UpdateBar";
import { useEditor } from "../state/editor";
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
import TopBar, { sidePanelToggleId, sidebarToggleId } from "./TopBar";
import TreeDrawer, { sidebarId, type DeleteSubject, type VaultSheet } from "./TreeDrawer";
import { useNoteActions } from "./useNoteActions";
import { useNoteMenuKey } from "./useNoteMenuKey";
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
  // The discreet note on its way out: the route still names it until the navigation is in, and nothing shows it.
  const gone = useGoneWithVaults(path);
  // A missing note whose vault the list doesn't name (a hidden one's bookmark after a reload): its path isn't shown either.
  const unnamed = useUnnamedVault(path);
  const missing = useNoteMissing(path);
  const hiding = useDiscreet((s) => s.hiding);
  // Picked, so that the editor's text published as it is typed (`live`) doesn't render the shell again on every key.
  const { recentPaths, overlay, mode, activeVault, upload, noteTool, openNote, openUpload, setOverlay, setActiveVault } = useEditor(
    useShallow(({ recentPaths, overlay, mode, activeVault, upload, noteTool, openNote, openUpload, setOverlay, setActiveVault }) => ({
      recentPaths,
      overlay,
      mode,
      activeVault,
      upload,
      noteTool,
      openNote,
      openUpload,
      setOverlay,
      setActiveVault,
    })),
  );
  const vaults = useVaults();
  const dockLevel = useLevel(activeVault);
  const noteLevel = useLevel(path && vaultOf(path));
  const createUntitled = useCreateUntitled();
  const navigate = useNavigate();
  const overview = (useLocation().state as { overview?: boolean } | null)?.overview === true;
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

  // From 1280 px on, the side panel sits beside the open note, collapsed or expanded: its links and its outline are there
  // and nowhere else. Not for a note on its way out, nor one whose path isn't shown (as for the top bar).
  const wide = useWide();
  const [panelCollapsed, setPanelCollapsed] = useSidePanelCollapsed();
  const shownNote = gone || (unnamed && missing) ? null : path;
  const sidePanel = wide && !!shownNote;
  const toggleSidePanel = useCallback(() => {
    if (!panelCollapsed && document.getElementById(sidePanelId)?.contains(document.activeElement)) document.getElementById(sidePanelToggleId)?.focus();
    setPanelCollapsed(!panelCollapsed);
  }, [panelCollapsed, setPanelCollapsed]);

  usePaletteShortcut();

  // The URL says which note is open; the store follows it when it changes (a link, back and forward, a reload).
  // Only then: after signing out, the store is empty while this URL is still shown for a moment.
  // Nor while the discreet vaults are being hidden: the note's URL is on its way out.
  useEffect(() => {
    if (path && path !== useEditor.getState().currentPath && !useDiscreet.getState().hiding) useEditor.getState().openNote(path);
  }, [path]);

  // A note named from its heading: the URL goes along, unless the user has gone on to another note meanwhile.
  const shownPath = useRef(path);
  shownPath.current = path;
  useEffect(() => {
    const follow = (from: string, to: string) => {
      if (shownPath.current === from) navigate(noteUrl(to), { replace: true });
    };
    useEditor.getState().setFollow(follow);
    return () => {
      if (useEditor.getState().follow === follow) useEditor.getState().setFollow(null);
    };
  }, [navigate]);
  const renaming = useEditor((s) => !!path && s.renaming.includes(path));
  const renameProblem = useEditor((s) => (s.renameProblem && s.renameProblem.path === s.currentPath ? s.renameProblem.text : null));

  // Fetched ahead, so the first tap on Search finds the sheet loaded, or nearly (see the dock's stand-in field), and
  // Rename or move finds its dialog loaded.
  useEffect(() => {
    void loadSearch().catch(() => {});
    void loadMoveDialog().catch(() => {});
  }, []);

  // Skills sorts before Welcome, where a new user should start: the Skills vault only when there is no other.
  useEffect(() => {
    if (!activeVault && vaults.data?.length) setActiveVault(keptVault ?? (vaults.data.find((v) => v.kind !== "skills") ?? vaults.data[0]).name);
  }, [activeVault, vaults.data, setActiveVault, keptVault]);

  // A note the caller can't change (any more: her role was lowered) is only read. Not before the vaults are in, whose
  // levels say so: Write would be off for a moment on every note, and a note opened in Write mode would drop out of it.
  // A save of the open note the server refused counts at once, by its code, until the vault list read after it is in:
  // that list then says (collab design §9.3). Nor for a vault the list doesn't name yet: renamed in another tab, the
  // note moves to the new name before the list read again is in.
  const refused = useEditor((s) => s.refused);
  const held = refused && refused.path === path && vaults.dataUpdatedAt <= refused.at ? refusalSentence(refused.code, vaultOf(path)) : null;
  const listed = !!path && !!vaults.data?.some((v) => v.name === vaultOf(path));
  const readOnlyNote = !!held || (listed && !mayChange(noteLevel, path!));
  useEffect(() => {
    if (readOnlyNote && useEditor.getState().mode === "write") useEditor.getState().setMode("read");
  }, [readOnlyNote, mode]);
  // Nothing of a note on its way out with its vault hidden again, a refusal's sentence naming it neither.
  const refusal = gone ? null : held ?? (path && vaults.data ? refusalOf(vaults.data.find((v) => v.name === vaultOf(path)), path) : null);

  // A write the server refused for the level (a role lowered, the owner deactivated, the vault closing): the list is
  // read again, and the write actions and Write mode follow it.
  const queryClient = useQueryClient();
  useEffect(() => onRefusal(() => void queryClient.invalidateQueries({ queryKey: ["vaults"] })), [queryClient]);

  // The note's ⋯ and the tree's share one flag menu, and its message outlives both.
  const flagging = useFlagging();
  const noteActions = useNoteActions(path, { onMove: setMoving, onDelete: setDeleting, onDate: setDating, onFlag: flagging.open });
  const noteMenu = useNoteMenuKey(path);
  if (!path && recentPaths.length && !overview) return <ShellRedirect to={noteUrl(recentPaths[0])} />;
  if (keptThere) return <ShellRedirect to={noteUrl(kept!)} />;

  /** A new note in `folder` (`/Work/projects`), opened in Write mode. */
  const newNote = (folder: string) =>
    createUntitled.mutate(folder, {
      onSuccess: (created) => {
        openNote(created, "write");
        navigate(noteUrl(created));
      },
    });
  // From the dock: the open note's folder, or the root of the vault the drawer shows.
  const dockFolder = activeVault && (path && vaultOf(path) === activeVault ? folderOf(path) : `/${activeVault}`);
  // Below `write` the dock makes nothing, and the note can't be changed; the board's definition wants `maintain`.
  const dockWrites = !!dockFolder && atLeast(dockLevel, "write");
  const noteChanges = !!path && mayChange(noteLevel, path);
  // On a phone the tree is a drawer: the frame and the pill go around and under the top bar instead (spec §3.7).
  const framed = discreetShown && !docked;
  const treeProps = { onNewNote: newNote, onNewFolder: setFolderDialog, onNewVault: () => setVaultDialog(true), onMove: setMoving, onDelete: setDeleting, onVaultSheet: setVaultSheet, onDate: setDating, onFlag: flagging.open };

  return (
    <div data-testid="shell" {...swipe} className="relative flex h-dvh flex-col overflow-hidden bg-bg">
      {framed && <DiscreetFrame />}
      {/* No Write mode while the note is being named: a save would make it again at its old path. */}
      <TopBar
        path={shownNote}
        writable={!readOnlyNote && !renaming}
        refusal={refusal}
        sidebar={docked ? { expanded: !collapsed, toggle: toggleSidebar } : null}
        sidePanel={sidePanel ? { expanded: !panelCollapsed, toggle: toggleSidePanel } : null}
      />
      {framed && (
        <div className="flex-none px-3 pt-2">
          <DiscreetPill />
        </div>
      )}
      <UpdateBar />
      <MemberNotices />
      <StorageNotice />
      <SkillsNotice />
      <InstallHint />
      {/* In the tab order the docked tree comes before the note, as it is shown: top bar, tree, note, dock. */}
      <div className="flex min-h-0 flex-1">
        {docked && <TreeDrawer docked hidden={collapsed} {...treeProps} />}
        <div className="flex min-w-0 flex-1 flex-col">
          <div className="pt-2 empty:hidden">
            <SharedBanner vault={path && !gone ? vaultOf(path) : activeVault} refusal={refusal} />
          </div>
          <main className="flex min-h-0 flex-1 flex-col">
            {gone ? (
              // Empty while the hide goes on; reached again later (a bookmark, say), said as a discreet note's 404 is.
              <div className="flex-1 px-[22px] pt-[22px]">{!hiding && <p className="text-text-body">{NO_SUCH_NOTE}</p>}</div>
            ) : path ? (
              <>
                {noteChanges && (
                  <NoteBar onRename={renaming ? null : () => setMoving({ kind: "note", path })} onDelete={() => setDeleting({ kind: "note", path })} />
                )}
                <NoteView key={path} path={path} panel={sidePanel} />
              </>
            ) : keptPending ? (
              <div className="flex-1" />
            ) : (
              <VaultOverview onNewVault={() => setVaultDialog(true)} />
            )}
          </main>
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
        </div>
        {sidePanel && <NoteSidePanel path={shownNote!} hidden={panelCollapsed} />}
      </div>

      {!docked && overlay === "tree" && <TreeDrawer {...treeProps} />}
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
            <Palette note={path && !gone ? { path, items: noteActions.items } : null} />
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
      {deleting?.kind === "note" && <DeleteNoteDialog path={deleting.path} open={deleting.path === path} onClose={() => setDeleting(null)} />}
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
